using LinqToDB;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace WikiTasks
{
    public enum ProcessStatus
    {
        NotProcessed = 0,
        Success = 1,
        Failure = 2,
        Skipped = 3
    }

    [Table(Name = "Pages")]
    class Page
    {
        [PrimaryKey]
        public int PageId;
        [Column()]
        public int RevisionId;
        [Column()]
        public string Timestamp;
        [Column()]
        public string Title;
        [Column()]
        public string SrcWikiText;
        [Column()]
        public string SrcRepl;
        [Column()]
        public string DstRepl;
        [Column()]
        public ProcessStatus Status;
    };

    class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<Page> Pages { get { return GetTable<Page>(); } }
    }

    class Program
    {
        MwApi wpApi;
        static Db db;

        string csrfToken;

        List<List<T>> SplitToChunks<T>(T[] elements, int chunkSize)
        {
            int chunkCount = (elements.Length + chunkSize - 1) / chunkSize;

            var chunks = new List<List<T>>();
            for (int i = 0; i < chunkCount; i++)
            {
                var chunk = new List<T>();
                for (int j = 0; j < chunkSize; j++)
                {
                    int k = i * chunkSize + j;
                    if (k >= elements.Length)
                        break;
                    chunk.Add(elements[k]);
                }
                chunks.Add(chunk);
            }

            return chunks;
        }

        static bool HaveTable(string name)
        {
            return db.DataProvider.GetSchemaProvider().
                GetSchema(db).Tables.Any(t => t.TableName == name);
        }

        List<Page> DeserializePages(string xml)
        {
            List<Page> pages = new List<Page>();

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            foreach (XmlNode pageNode in doc.SelectNodes("/api/query/pages/page"))
            {
                if (pageNode.Attributes["missing"] != null)
                    continue;

                Page page = new Page();
                page.Title = pageNode.Attributes["title"].Value;
                page.PageId = int.Parse(pageNode.Attributes["pageid"].Value);

                XmlNode revNode = pageNode.SelectSingleNode("revisions/rev");
                page.SrcWikiText = revNode.InnerText;
                page.RevisionId = int.Parse(revNode.Attributes["revid"].Value);
                if (revNode.Attributes["timestamp"] != null)
                    page.Timestamp = revNode.Attributes["timestamp"].Value;

                pages.Add(page);
            }

            return pages;
        }

        void DownloadPages(Dictionary<int, int> ids)
        {
            if (!HaveTable("Pages"))
                db.CreateTable<Page>();

            var dbids = db.Pages.ToDictionary(a => a.PageId, a => a.RevisionId);


            var idsset = new HashSet<int>(ids.Keys);
            var dbidsset = new HashSet<int>(dbids.Keys);

            var deleted = dbidsset.Except(idsset).ToArray();
            var added = idsset.Except(dbidsset).ToArray();
            var existing = idsset.Intersect(dbidsset).ToArray();
            var changed = existing.Where(id => ids[id] != dbids[id]).ToArray();

            var todl = added.ToDictionary(id => id, id => true).Union(
                changed.ToDictionary(id => id, id => false)).ToDictionary(
                kv => kv.Key, kv => kv.Value);

            db.Pages.Delete(a => deleted.Contains(a.PageId));

            Console.Write("Downloading pages");
            var chunks = SplitToChunks(todl.Keys.OrderBy(x => x).ToArray(), 100);
            foreach (var chunk in chunks)
            {
                string idsChunk = string.Join("|", chunk);
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "prop", "revisions",
                    "rvprop", "ids|timestamp|content",
                    "rvslots", "main",
                    "format", "xml",
                    "pageids", idsChunk);
                Console.Write('.');

                List<Page> pages = DeserializePages(xml);
                db.BeginTransaction();
                foreach (Page page in pages)
                    if (todl[page.PageId])
                        db.Insert(page);
                    else
                        db.Update(page);
                db.CommitTransaction();
            }
            Console.WriteLine(" Done");
        }

        Dictionary<int, int> SearchPages(string query, string ns = "0")
        {
            Console.Write("Searching pages");
            var ids = new Dictionary<int, int>();

            string continueQuery = null;
            string continueGsr = null;
            string continueRv = null;
            for (;;)
            {
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "generator", "search",
                    "prop", "revisions",
                    "rvprop", "ids",
                    "gsrwhat", "text",
                    "gsrsearch", query,
                    "gsrnamespace", ns,
                    "gsrprop", "",
                    "gsrinfo", "",
                    "gsrlimit", "4999", // hack for T213745
                    "gsroffset", continueGsr,
                    "rvcontinue", continueRv,
                    "continue", continueQuery,
                    "format", "xml");
                Console.Write('.');

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                XmlNode errNode = doc.SelectSingleNode("/api/error");
                if (errNode != null)
                {
                    throw new Exception(
                        $"{errNode.Attributes["code"].Value}: {errNode.Attributes["info"].Value}");
                }

                foreach (XmlNode node in doc.SelectNodes("/api/query/pages/page"))
                {
                    int pageId = int.Parse(node.Attributes["pageid"].Value);
                    int revisionId = int.Parse(node.
                        SelectSingleNode("revisions/rev").Attributes["revid"].Value);
                    ids.Add(pageId, revisionId);
                }

                XmlNode contNode = doc.SelectSingleNode("/api/continue");
                if (contNode == null)
                    break;
                var continueQueryAttr = contNode.Attributes["continue"];
                var continueGsrAttr = contNode.Attributes["gsroffset"];
                var continueRvAttr = contNode.Attributes["rvcontinue"];
                continueQuery = continueQueryAttr == null ? null : continueQueryAttr.Value;
                continueGsr = continueGsrAttr == null ? null : continueGsrAttr.Value;
                continueRv = continueRvAttr == null ? null : continueRvAttr.Value;
            }
            Console.WriteLine(" Done");
            return ids;
        }

        void ObtainEditToken()
        {
            Console.Write("Authenticating...");
            csrfToken = wpApi.GetToken("csrf");
            if (csrfToken == null)
            {
                Console.WriteLine(" Failed");
                throw new Exception();
            }
            Console.WriteLine(" Done");
        }

        bool EditPage(string csrfToken, string timestamp, string title, string summary, string text)
        {
            string xml = wpApi.PostRequest(
                "action", "edit",
                "format", "xml",
                "bot", "true",
                "title", title,
                "summary", summary,
                "text", text,
                "basetimestamp", timestamp,
                "token", csrfToken);

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            if (doc.SelectNodes("/api/error").Count == 1)
                return false;
            return doc.SelectSingleNode("/api/edit").Attributes["result"].InnerText == "Success";
        }

        void ProcessPages()
        {
            Console.Write("Processing pages...");
            var pages = db.Pages.Where(p => p.Status == ProcessStatus.NotProcessed).ToArray();

            foreach (var p in pages)
            {
                p.SrcRepl = null;
                p.DstRepl = null;

                var nameMatch = Regex.Match(p.Title, "^Шаблон:(.+)/doc$");
                if (!nameMatch.Success)
                    continue;
                var templateName = nameMatch.Groups[1].Value;
                var templateNameLC = char.ToLowerInvariant(templateName[0]) + templateName.Substring(1);

                var wikiMatches = Regex.Matches(p.SrcWikiText,
                    "(\\{\\{" + $"({Regex.Escape(templateName)}|{Regex.Escape(templateNameLC)})" +
                    ".+?)[ \t\n]*<pre( +style=\"overflow *: *auto\")?>[ \t\n]*\\1[ \t\n]*</pre>",
                    RegexOptions.Singleline);
                if (wikiMatches.Count != 1)
                    continue;

                string templateInvocation = wikiMatches[0].Groups[1].Value;
                if (templateInvocation.Length < 40)
                    continue;

                bool pre = !templateInvocation.Contains('\n');

                p.SrcRepl = wikiMatches[0].Value;
                p.DstRepl = "{{demo|reverse=1" + (pre ? "|tag=pre" : "") +
                    "|br=|<nowiki>\n" + templateInvocation + "\n</nowiki>}}";
            }
            db.BeginTransaction();
            foreach (var p in pages)
                db.Update(p);
            db.CommitTransaction();
            Console.WriteLine(" Done");
        }

        void MakeReplacements()
        {
            Console.Write("Making replacements");
            var pages = db.Pages.
                Where(a => a.Status == ProcessStatus.NotProcessed && a.DstRepl != null).
                OrderBy(a => a.Title).ToArray();

            foreach (var page in pages)
            {
                string ReplWikiText =
                    page.SrcWikiText.Replace(page.SrcRepl, page.DstRepl);
                bool isEditSuccessful = EditPage(csrfToken, page.Timestamp,
                    page.Title, "устранение дублирования", ReplWikiText);
                page.Status = isEditSuccessful ? ProcessStatus.Success : ProcessStatus.Failure;
                db.Update(page);
                Console.Write(isEditSuccessful ? '.' : 'x');
            }

            Console.WriteLine(" Done");
        }


        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");
            var ids = SearchPages(
                "t: hastemplate:docpage insource:/\\{\\{docpage/ insource:/\\<pre/");
            DownloadPages(ids);
            ProcessPages();
            ObtainEditToken();
            MakeReplacements();
        }

        static void Main(string[] args)
        {
            db = new Db();
            new Program();
            db.Dispose();
        }
    }
}
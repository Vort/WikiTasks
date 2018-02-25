using LinqToDB;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace WikiTasks
{
    public enum ProcessStatus
    {
        NotProcessed = 0,
        Success = 1,
        Failure = 2
    }

    [Table(Name = "Articles")]
    public class Article
    {
        [PrimaryKey]
        public int PageId;
        [Column()]
        public string Timestamp;
        [Column()]
        public string Title;
        [Column()]
        public string SrcWikiText;
        [Column()]
        public string ReplWikiText;
        [Column()]
        public ProcessStatus Status;
    };

    public class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }


        public ITable<Article> Articles { get { return GetTable<Article>(); } }
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

        static bool HaveTable(string name)
        {
            return db.DataProvider.GetSchemaProvider().
                GetSchema(db).Tables.Any(t => t.TableName == name);
        }

        List<Article> DeserializeArticles(string xml)
        {
            List<Article> articles = new List<Article>();

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            foreach (XmlNode pageNode in doc.SelectNodes("/api/query/pages/page"))
            {
                if (pageNode.Attributes["missing"] != null)
                    continue;

                Article article = new Article();
                article.Title = pageNode.Attributes["title"].InnerText;
                article.PageId = int.Parse(pageNode.Attributes["pageid"].InnerText);

                XmlNode revNode = pageNode.SelectSingleNode("revisions/rev");
                article.SrcWikiText = revNode.InnerText;
                if (revNode.Attributes["timestamp"] != null)
                    article.Timestamp = revNode.Attributes["timestamp"].InnerText;

                articles.Add(article);
            }

            return articles;
        }

        void DownloadArticles(int[] ids)
        {
            if (HaveTable("Articles"))
                db.DropTable<Article>();
            db.CreateTable<Article>();

            Console.Write("Downloading articles");
            var chunks = SplitToChunks(ids, 50);
            foreach (var chunk in chunks)
            {
                string idsChunk = string.Join("|", chunk);
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "prop", "revisions",
                    "rvprop", "timestamp|content",
                    "format", "xml",
                    "pageids", idsChunk);
                Console.Write('.');

                List<Article> articles = DeserializeArticles(xml);
                db.BeginTransaction();
                foreach (Article a in articles)
                    db.Insert(a);
                db.CommitTransaction();
            }
            Console.WriteLine(" Done");
        }

        List<int> SearchArticles(string query, string ns = "0")
        {
            var idList = new List<int>();

            Console.Write("Searching articles");
            string sroffset = "";
            for (;;)
            {
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "list", "search",
                    "srwhat", "text",
                    "srsearch", query,
                    "srprop", "",
                    "srinfo", "",
                    "srlimit", "100",
                    "sroffset", sroffset,
                    "srnamespace", ns,
                    "format", "xml");
                Console.Write('.');

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                foreach (XmlNode pNode in doc.SelectNodes("/api/query/search/p"))
                {
                    int id = int.Parse(pNode.Attributes["pageid"].InnerText);
                    idList.Add(id);
                }

                XmlNode contNode = doc.SelectSingleNode("/api/continue");
                if (contNode == null)
                    break;
                sroffset = contNode.Attributes["sroffset"].InnerText;
            }
            Console.WriteLine(" Done");

            return idList;
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

        void ProcessArticles()
        {
            var repls = new Dictionary<string, string>();
            var articles = db.Articles.ToArray();

            foreach (var article in articles)
            {
                var matches = Regex.Matches(
                    article.SrcWikiText, "gramota\\.ru/slovari/dic[/]?\\?([^ |\\]}\\n'<]+)");
                if (matches.Count == 0)
                    throw new Exception();
                for (int i = 0; i < matches.Count; i++)
                {
                    string part = matches[i].Groups[1].Value;
                    if (!Regex.IsMatch(part, "%[0-9a-fA-F]{2,4}"))
                        continue;
                    repls[part] = part;
                }
            }

            foreach (var repl in repls.Keys.ToArray())
            {
                string result = repl;
                for (int i = 0; i < 2; i++)
                {
                    if (Regex.IsMatch(result, "%25[0-9a-fA-F]{2}"))
                        result = Regex.Replace(result, "%25([0-9a-fA-F]{2})", "%$1");
                }
                bool utf8 = Regex.Matches(result, "%D[01]").Count > 1;
                if (!utf8)
                {
                    for (int i = 0xC0; i <= 0xFF; i++)
                    {
                        string decChar = Encoding.GetEncoding(1251).
                            GetChars(new byte[] { (byte)i })[0].ToString();
                        result = result.Replace($"%{i:X2}", decChar);
                    }
                    result = result.Replace("%A8", "Ё");
                    result = result.Replace("%B8", "ё");
                    result = result.Replace("%3F", "?");
                    result = result.Replace("%2A", "*");
                }
                else
                {
                    for (int i = 0x400; i <= 0x460; i++)
                    {
                        byte b1 = (byte)((i >> 6) & 0x1F | 0xD0);
                        byte b2 = (byte)(i & 0x3F | 0x80);
                        result = result.Replace($"%{b1:X2}%{b2:X2}",
                            Encoding.UTF8.GetChars(new byte[] { b1, b2 })[0].ToString());
                    }
                }
                repls[repl] = result;
            }

            File.WriteAllLines("fragments.txt", repls.Values);

            db.BeginTransaction();
            foreach (var article in articles)
            {
                string result = article.SrcWikiText;
                foreach (var replkv in repls)
                    result = result.Replace(replkv.Key, replkv.Value);
                if (result != article.SrcWikiText)
                {
                    article.ReplWikiText = result;
                    db.Update(article);
                }
            }
            db.CommitTransaction();
        }

        void MakeReplacements()
        {
            Console.Write("Making replacements");
            var articles = db.Articles.
                Where(a => a.Status == ProcessStatus.NotProcessed && a.ReplWikiText != null).
                OrderBy(a => a.Title).ToArray();

            foreach (var article in articles)
            {
                bool isEditSuccessful = EditPage(csrfToken, article.Timestamp,
                    article.Title, "исправление ссылок gramota.ru", article.ReplWikiText);
                article.Status = isEditSuccessful ? ProcessStatus.Success : ProcessStatus.Failure;
                db.Update(article);
                Console.Write(isEditSuccessful ? '.' : 'x');
            }

            Console.WriteLine(" Done");
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");

            var ids = SearchArticles("insource:\"gramota.ru/slovari/dic\"");
            DownloadArticles(ids.ToArray());

            ProcessArticles();
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
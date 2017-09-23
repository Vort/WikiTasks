using LinqToDB;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace WikiTasks
{
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
        public string WikiText;
    };

    [Table(Name = "Replacements")]
    public class Replacement
    {
        [PrimaryKey]
        public int Id;
        [Column()]
        public int PageId;
        [Column()]
        public string SrcString;
        [Column()]
        public string DstString;
        [Column()]
        public byte Status;
    }

    public class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<Article> Articles { get { return GetTable<Article>(); } }
        public ITable<Replacement> Replacements { get { return GetTable<Replacement>(); } }
    }

    class Program
    {
        WpApi api;
        static Db db;

        const int editsPerMinute = 180;
        const int connectionsLimit = 4;

        string csrfToken;

        List<List<int>> SplitToChunks(List<int> ids, int chunkSize)
        {
            int chunkCount = (ids.Count + chunkSize - 1) / chunkSize;

            List<List<int>> chunks = new List<List<int>>();
            for (int i = 0; i < chunkCount; i++)
            {
                List<int> chunk = new List<int>();
                for (int j = 0; j < chunkSize; j++)
                {
                    int k = i * chunkSize + j;
                    if (k >= ids.Count)
                        break;
                    chunk.Add(ids[k]);
                }
                chunks.Add(chunk);
            }

            return chunks;
        }

        List<Article> GetArticles(string xml)
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
                article.WikiText = revNode.InnerText;
                if (revNode.Attributes["timestamp"] != null)
                    article.Timestamp = revNode.Attributes["timestamp"].InnerText;

                articles.Add(article);
            }

            return articles;
        }

        void SearchArticles(string query, string ns = "0")
        {
            if (HaveTable("Articles"))
                db.DropTable<Article>();
            db.CreateTable<Article>();

            Console.Write("Searching articles");
            string sroffset = "";
            for (;;)
            {
                string xml = api.PostRequest(
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

                db.BeginTransaction();
                foreach (XmlNode pNode in doc.SelectNodes("/api/query/search/p"))
                {
                    int id = int.Parse(pNode.Attributes["pageid"].InnerText);
                    db.Insert(new Article() { PageId = id });
                }
                db.CommitTransaction();

                XmlNode contNode = doc.SelectSingleNode("/api/continue");
                if (contNode == null)
                    break;
                sroffset = contNode.Attributes["sroffset"].InnerText;
            }
            Console.WriteLine(" Done");
        }


        void DownloadArticles()
        {
            Console.Write("Downloading articles");
            var updChunks = SplitToChunks(
                db.Articles.Select(a => a.PageId).ToList(), 100);
            foreach (List<int> chunk in updChunks)
            {
                string idss = string.Join("|", chunk);
                string xml = api.PostRequest(
                    "action", "query",
                    "prop", "revisions",
                    "rvprop", "timestamp|content",
                    "format", "xml",
                    "pageids", idss);
                Console.Write('.');

                List<Article> articles = GetArticles(xml);
                db.BeginTransaction();
                foreach (Article a in articles)
                    db.Update(a);
                db.CommitTransaction();
            }
            Console.WriteLine(" Done");
        }

        string GetToken(string type)
        {
            string xml = api.PostRequest(
                "action", "query",
                "format", "xml",
                "meta", "tokens",
                "type", type);

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            if (doc.SelectNodes("/api/error").Count == 1)
                return null;

            return doc.SelectNodes("/api/query/tokens")[0].Attributes[type + "token"].InnerText;
        }

        async Task<bool> EditPage(string csrfToken, string timestamp, int pageId, string summary, string text)
        {
            string xml = await api.PostRequestAsync(
                "action", "edit",
                "format", "xml",
                "bot", "true",
                "pageid", pageId.ToString(),
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

        static bool HaveTable(string name)
        {
            return db.DataProvider.GetSchemaProvider().
                GetSchema(db).Tables.Any(t => t.TableName == name);
        }

        void ParseArticles()
        {
            Console.Write("Parsing articles...");

            if (!HaveTable("Articles"))
                throw new Exception();
            if (HaveTable("Replacements"))
                db.DropTable<Replacement>();
            db.CreateTable<Replacement>();

            var regex = new Regex("\\<font color *= *\"?(\\#[a-zA-Z0-9]{6})\"?\\>\\[\\[([^\\]]+)\\]\\] *\\<\\/font\\>");
            var articles = db.Articles.ToList();

            int replId = 0;
            db.BeginTransaction();
            foreach (var article in articles)
            {
                foreach (Match m in regex.Matches(article.WikiText))
                {
                    string color = m.Groups[1].Value;
                    string link = m.Groups[2].Value;

                    if (link.StartsWith("File:") || link.StartsWith("Файл:"))
                        continue;

                    if (color.ToLower() == "#000000")
                        color = "black";
                    else if (color.ToLower() == "#ffffff")
                        color = "white";

                    Replacement r = new Replacement();
                    r.PageId = article.PageId;
                    r.SrcString = m.Value;
                    r.DstString = $"{{{{цветная ссылка|{color}|{link}}}}}";
                    r.Id = replId;
                    replId++;

                    db.Insert(r);
                }
            }

            db.CommitTransaction();

            Console.WriteLine(" Done");
            Console.WriteLine(" Articles: " + articles.Count);
            Console.WriteLine(" Replacements: " + db.Replacements.Count());
        }

        void MakeReplacements()
        {
            Console.Write("Making replacements");

            var exitEvent = new ManualResetEvent(false);
            var articles = db.Articles.ToDictionary(a => a.PageId);
            var replGroups = db.Replacements.Where(r => r.Status == 0).
                OrderByDescending(r => r.Id).GroupBy(r => r.PageId).Select(g => g.ToArray()).ToArray();
            int editsLeft = replGroups.Length;

            foreach (var replGroup in replGroups)
            {
                var article = articles[replGroup.First().PageId];

                string newText = article.WikiText;
                foreach (var replacement in replGroup)
                    newText = newText.Replace(replacement.SrcString, replacement.DstString);

                Task<bool> editTask = EditPage(csrfToken, article.Timestamp, article.PageId,
                    "замена font color на шаблон [[Template:цветная ссылка|цветная ссылка]]", newText);
                editTask.ContinueWith(cont =>
                {
                    bool isEditSuccessful = cont.Result;
                    lock (db)
                    {
                        Console.Write(isEditSuccessful ? '.' : 'x');

                        db.BeginTransaction();
                        foreach (var replacement in replGroup)
                        {
                            replacement.Status = isEditSuccessful ? (byte)1 : (byte)2;
                            db.Update(replacement);
                        }
                        db.CommitTransaction();

                        editsLeft--;
                        if (editsLeft == 0)
                            exitEvent.Set();
                    }
                });
                Thread.Sleep(60 * 1000 / editsPerMinute);
            }
            exitEvent.WaitOne();
            Console.WriteLine(" Done");
        }

        void GetArticles()
        {
            SearchArticles("insource:/\\<font color *= *\\\"?(\\#[a-zA-Z0-9]{6})\\\"?\\>\\[\\[[^\\]]+\\]\\] *\\<\\/font\\>/", "10");
            DownloadArticles();
        }

        void ObtainEditToken()
        {
            Console.Write("Authenticating...");
            csrfToken = GetToken("csrf");
            if (csrfToken == null)
            {
                Console.WriteLine(" Failed");
                throw new Exception();
            }
            Console.WriteLine(" Done");
        }

        Program()
        {
            api = new WpApi();
            ObtainEditToken();
            GetArticles();
            ParseArticles();
            MakeReplacements();
        }

        static void Main(string[] args)
        {
            ServicePointManager.DefaultConnectionLimit = connectionsLimit;
            db = new Db();
            new Program();
            db.Dispose();
        }
    }
}
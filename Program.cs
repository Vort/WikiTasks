using Antlr4.Runtime;
using LinqToDB;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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

    class ELinkInvoke
    {
        public int StartPosition;
        public int EndPosition;
        public string Text;
        public string Url;
        public string Title;
    }

    [Table(Name = "Articles")]
    class Article
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
        public string DstWikiText;
        [Column()]
        public ProcessStatus Status;


        public List<string> Errors;
        public List<ELinkInvoke> ElinkInvokes;
    };

    [Table(Name = "GNISEntries")]
    class GNISEntry
    {
        [PrimaryKey]
        public int Id;
        [Column()]
        public string Name;
    }


    class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<GNISEntry> GNISEntries { get { return GetTable<GNISEntry>(); } }

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
                article.Title = pageNode.Attributes["title"].Value;
                article.PageId = int.Parse(pageNode.Attributes["pageid"].Value);

                XmlNode revNode = pageNode.SelectSingleNode("revisions/rev");
                article.SrcWikiText = revNode.InnerText;
                if (revNode.Attributes["timestamp"] != null)
                    article.Timestamp = revNode.Attributes["timestamp"].Value;

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
            var chunks = SplitToChunks(ids, 100);
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

        void ProcessArticles()
        {
            int processed = 0;
            int lexerErrors = 0;
            int parserErrors = 0;

            var articles = db.Articles.ToArray();
            //var articles = db.Articles.Take(500).ToArray();
            //var articles = db.Articles.Where(a => a.Title == "Мюра (водопады)").ToArray();

            Console.Write("Parsing articles");
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            Parallel.ForEach(articles, article =>
            {
                AntlrErrorListener ael = new AntlrErrorListener();
                AntlrInputStream inputStream = new AntlrInputStream(article.SrcWikiText);
                WikiLexer lexer = new WikiLexer(inputStream);
                lexer.RemoveErrorListeners();
                lexer.AddErrorListener(ael);
                CommonTokenStream commonTokenStream = new CommonTokenStream(lexer);
                WikiParser parser = new WikiParser(commonTokenStream);
                parser.RemoveErrorListeners();
                parser.AddErrorListener(ael);
                WikiParser.InitContext initContext = parser.init();
                WikiVisitor visitor = new WikiVisitor(article);
                visitor.VisitInit(initContext);
                article.Errors = ael.ErrorList;

                Interlocked.Add(ref lexerErrors, ael.LexerErrors);
                Interlocked.Add(ref parserErrors, ael.ParserErrors);

                if (Interlocked.Increment(ref processed) % 100 == 0)
                    Console.Write('.');
            });
            stopwatch.Stop();

            Console.WriteLine(" Done");
            Console.WriteLine(" Articles: " + articles.Count());
            Console.WriteLine(" Parser errors: " + parserErrors);
            Console.WriteLine(" Lexer errors: " + lexerErrors);
            Console.WriteLine(" Parsing time: " + stopwatch.Elapsed.TotalSeconds + " sec");

            List<string> errorLog = new List<string>();
            foreach (Article article in articles)
            {
                if (article.Errors == null)
                    continue;
                if (article.Errors.Count == 0)
                    continue;
                errorLog.Add("Статья: " + article.Title);
                errorLog.AddRange(article.Errors);
            }
            File.WriteAllLines("error_log.txt", errorLog.ToArray(), Encoding.UTF8);

            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

            Console.Write("Processing...");

            var sb = new StringBuilder();

            int i = 1;
            sb.AppendLine("{|class=\"wide\" style=\"table-layout: fixed;word-wrap:break-word\"");
            sb.AppendLine("!width=\"28px\"|№");
            sb.AppendLine("!width=\"20%\"|Статья");
            sb.AppendLine("!width=\"50%\"|Ссылка");
            sb.AppendLine("!width=\"30%\"|Шаблон");

            db.BeginTransaction();
            foreach (var article in articles.OrderBy(a => a.Title))
            {
                foreach (var eli in article.ElinkInvokes)
                {
                    int gnisId = int.Parse(Regex.Match(eli.Url, "P3_FID:([0-9]+)").Groups[1].Value);

                    GNISEntry gnisEntry = db.GNISEntries.FirstOrDefault(e => e.Id == gnisId);
                    var templText = $"{{{{GNIS|{gnisId}|{gnisEntry.Name}}}}}";

                    var prev = Regex.Match(
                        article.SrcWikiText.Substring(0, eli.StartPosition), "[>*\n] *([^>*\n]+)$", RegexOptions.Singleline).Groups[1].Value.Trim();

                    var rest = Regex.Match(
                        article.SrcWikiText.Substring(eli.EndPosition), "([^\n<]+)?").Groups[1].Value;

                    bool replace = false;
                    if (gnisEntry != null)
                    {
                        if (prev == "" && rest == "")
                        {
                            string[] replTitles =
                            {
                                "U.S. Geological Survey Geographic Names Information System: " + gnisEntry.Name,
                                "USGS GNIS Feature Detail Report: " + gnisEntry.Name,
                                "GNIS. Feature Detail Report for: " + gnisEntry.Name,
                                "Feature Detail Report for: " + gnisEntry.Name,
                                "GNIS Detail — " + gnisEntry.Name,
                                "Geographic Names Information System, U.S. Geological Survey",
                                "Geographic Names Information System, U.S. Geological Survey.",
                                "Geographic Names Information System. United States Geological Survey",
                                "Geographic Names Information System. United States Geological Survey.",
                                "Geographic Names Information System (GNIS). United States Geological Survey (USGS)",
                                "U.S. Geological Survey Geographic Names Information System",
                                "geonames.usgs.gov",
                                ""
                            };

                            string title = eli.Title.Trim();
                            replace = replTitles.Any(t => title == t);
                        }
                    }

                    if (replace)
                    {
                        if (article.DstWikiText == null)
                            article.DstWikiText = article.SrcWikiText;
                        article.DstWikiText = article.DstWikiText.Replace(eli.Text, templText);
                    }

                    sb.AppendLine($"|-");
                    sb.AppendLine($"| {i}");
                    sb.AppendLine($"| [[{article.Title}]]");
                    sb.AppendLine($"| {(prev.Length != 0 ? "<small><code>{{color|gray|<nowiki>" + prev + "</nowiki>}}</code></small>" : "")}<small><code><nowiki>{eli.Text}</nowiki></code></small>{(rest.Length != 0 ? "<small><code>{{color|gray|<nowiki>" + rest + "</nowiki>}}</code></small>" : "")}");
                    sb.AppendLine($"| <small><code><nowiki>{templText}</nowiki></code></small>");
                    i++;
                }
                if (article.DstWikiText != null)
                    db.Update(article);
            }
            db.CommitTransaction();
            sb.AppendLine("|}");

            File.WriteAllText("result.txt", sb.ToString());

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

        void MakeReplacements()
        {
            Console.Write("Making replacements");
            var articles = db.Articles.
                Where(a => a.Status == ProcessStatus.NotProcessed && a.DstWikiText != null).
                OrderBy(a => a.Title).ToArray();

            foreach (var article in articles)
            {
                bool isEditSuccessful = EditPage(csrfToken, article.Timestamp,
                    article.Title, "оформление", article.DstWikiText);
                article.Status = isEditSuccessful ? ProcessStatus.Success : ProcessStatus.Failure;
                db.Update(article);
                Console.Write(isEditSuccessful ? '.' : 'x');
            }

            Console.WriteLine(" Done");
        }

        void LoadGNIS()
        {
            if (HaveTable("GNISEntries"))
                return;
            Console.Write("Loading GNIS...");
            db.CreateTable<GNISEntry>();
            string[] entries = File.ReadAllLines("AllNames_20181201.txt");
            int lastId = -1;
            db.BeginTransaction();
            for (int i = 1; i < entries.Length; i++)
            {
                var se = entries[i].Split('|');
                int id = int.Parse(se[0]);
                if (id != lastId)
                {
                    lastId = id;
                    db.Insert(new GNISEntry { Id = id, Name = se[1] });
                }
            }
            db.CommitTransaction();

            Console.WriteLine(" Done");
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");
            LoadGNIS();
            var ids = SearchArticles(
                "insource:/\\[https?:\\/\\/geonames\\.usgs\\.gov\\/(pls\\/gnispublic|apex)\\/f\\?p=gnispq:3:[0-9]*::NO::P3_FID:([0-9]+)/");
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
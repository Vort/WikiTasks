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
        public string SrcRepl;
        [Column()]
        public string DstRepl;
        [Column()]
        public ProcessStatus Status;


        public List<string> Errors;
        public List<Template> Templates;
    };

    class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<Article> Articles { get { return GetTable<Article>(); } }
    }

    class TemplateParam
    {
        public bool Newline;
        public int Sp1;
        public int Sp2;
        public string Name;
        public int Sp3;
        public int Sp4;
        public string Value;
        // Отключено для экономии памяти
        // public IParseTree[] ValueTrees;
    }

    class Template
    {
        public Template()
        {
            Params = new List<TemplateParam>();
        }

        public TemplateParam this[string name]
        {
            get
            {
                var pl = Params.Where(p => p.Name == name).ToArray();
                if (pl.Length == 0)
                    return null;
                else if (pl.Length == 1)
                    return pl[0];
                else
                    throw new Exception();
            }
        }

        public TemplateParam this[int index]
        {
            get
            {
                return Params[index];
            }
        }

        public bool HaveZeroNewlines()
        {
            if (Params.Count == 0)
                return true;
            return Params.All(p => !p.Newline);
        }

        public void Reformat()
        {
            var v = Params.Where(p => p.Value != "").Select(p => p.Sp4).Distinct().ToArray();
            bool std = v.Length == 1 && v[0] == 1;

            for (int i = 1; i < Params.Count; i++)
            {
                if (Params[i].Newline &&
                    Params[i - 1].Value == "")
                {
                    if (std)
                        Params[i - 1].Sp4 = 1;
                    else
                        Params[i - 1].Sp4 = Params[i - 1].Sp4 >= 1 ? 1 : 0;
                }
            }
        }

        public int GetIndex(TemplateParam param)
        {
            return Params.FindIndex(p => p == param);
        }

        public void InsertAfter(TemplateParam param, TemplateParam newParam)
        {
            if (Params.Where(p => p.Name == newParam.Name).Count() != 0)
                throw new Exception();
            Params.Insert(Params.FindIndex(p => p == param) + 1, newParam);
        }

        public void Remove(string paramName)
        {
            Params.RemoveAll(p => p.Name == paramName);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("{{");
            sb.Append(Name);
            foreach (var param in Params)
            {
                if (param.Newline)
                    sb.Append('\n');
                sb.Append(new string(' ', param.Sp1));
                sb.Append('|');
                sb.Append(new string(' ', param.Sp2));
                sb.Append(param.Name);
                sb.Append(new string(' ', param.Sp3));
                sb.Append('=');
                sb.Append(new string(' ', param.Sp4));
                sb.Append(param.Value);
            }
            if (!HaveZeroNewlines())
                sb.Append("\n");
            sb.Append("}}");
            return sb.ToString();
        }

        public string Name;
        public List<TemplateParam> Params;
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
                WikiVisitor visitor = new WikiVisitor(article,
                    new string[] { "Cite web" }, null);
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

            db.BeginTransaction();
            foreach (Article article in articles)
            {
                var tl = article.Templates.Where(t => t["url"] != null &&
                    t["url"].Value.Contains("://space.kursknet.ru/")).ToArray();
                if (tl.Length != 1)
                    continue;

                var template = tl[0];
                var deadlink = template["deadlink"];
                var accessdate = template["accessdate"];

                if (deadlink == null)
                    continue;
                if (deadlink.Value != "no")
                    continue;

                article.SrcRepl = template.ToString();
                deadlink.Value = "yes";
                if (accessdate != null)
                    accessdate.Value = "2019-02-04";
                article.DstRepl = template.ToString();
                db.Update(article);
            }
            db.CommitTransaction();

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
                Where(a => a.Status == ProcessStatus.NotProcessed && a.DstRepl != null).
                OrderBy(a => a.Title).ToArray();

            foreach (var article in articles)
            {
                string ReplWikiText =
                    article.SrcWikiText.Replace(article.SrcRepl, article.DstRepl);
                bool isEditSuccessful = EditPage(csrfToken, article.Timestamp,
                    article.Title, "сайт закрыт", ReplWikiText);
                article.Status = isEditSuccessful ? ProcessStatus.Success : ProcessStatus.Failure;
                db.Update(article);
                Console.Write(isEditSuccessful ? '.' : 'x');
            }

            Console.WriteLine(" Done");
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");
            var ids = SearchArticles("insource:/space\\.kursknet\\.ru/ hastemplate:\"cite web\"");
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
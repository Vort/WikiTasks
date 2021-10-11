using Antlr4.Runtime;
using LinqToDB;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace WikiTasks
{
    class ResultRecord
    {
        public string Title;
        public string WdId;
        public string Region;
        public string EGROKN;
        public string WV;
    }

    class WdIdsRecord
    {
        public string WdId;
        public string WdEGROKN;
        public string WdWV;
    }


    class Program
    {
        MwApi wpApi;

        static Db db;

        List<WdIdsRecord> wdIds;

        public class Db : LinqToDB.Data.DataConnection
        {
            public Db() : base("Db") { }

            public ITable<Article> Articles { get { return GetTable<Article>(); } }
        }

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
                article.WikiText = revNode.InnerText;
                if (revNode.Attributes["timestamp"] != null)
                    article.Timestamp = revNode.Attributes["timestamp"].Value;

                articles.Add(article);
            }

            return articles;
        }

        static bool HaveTable(string name)
        {
            return db.DataProvider.GetSchemaProvider().
                GetSchema(db).Tables.Any(t => t.TableName == name);
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

        void FillWikidataIds()
        {
            Console.Write("Filling wikidata ids");
            var articles = db.Articles.ToArray();

            var chunks = SplitToChunks(articles.Select(a => a.PageId).ToArray(), 500);
            foreach (var chunk in chunks)
            {
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "prop", "pageprops",
                    "ppprop", "wikibase_item",
                    "format", "xml",
                    "pageids", string.Join("|", chunk));
                Console.Write('.');

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                foreach (XmlNode pageNode in doc.SelectNodes("/api/query/pages/page/pageprops"))
                {
                    int articleId = int.Parse(pageNode.ParentNode.Attributes["pageid"].Value);
                    string wdId = pageNode.Attributes["wikibase_item"].Value;
                    articles.First(a => a.PageId == articleId).WdId = wdId;
                }
            }

            db.BeginTransaction();
            foreach (Article a in articles)
                db.Update(a);
            db.CommitTransaction();
            Console.WriteLine(" Done");
        }

        string CombineTwoParams(TemplateParam p1, TemplateParam p2)
        {
            string s1 = null;
            string s2 = null;
            if (p1 != null && p1.Value != "")
                s1 = p1.Value;
            if (p2 != null && p2.Value != "")
                s2 = p2.Value;
            if (s1 != null || s2 != null)
                return $"{s1} / {s2}";
            return "";
        }

        void ProcessArticles()
        {
            int processed = 0;
            int lexerErrors = 0;
            int parserErrors = 0;

            var articles = db.Articles.ToArray();
            //var articles = db.Articles.Take(100).ToArray();
            //var articles = db.Articles.Where(a => a.Title == "Храм Жён-Мироносиц (Великий Новгород)").ToArray();

            Console.Write("Parsing articles");
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            Parallel.ForEach(articles, article =>
            {
                AntlrErrorListener ael = new AntlrErrorListener();
                AntlrInputStream inputStream = new AntlrInputStream(article.WikiText);
                WikiLexer lexer = new WikiLexer(inputStream);
                lexer.RemoveErrorListeners();
                lexer.AddErrorListener(ael);
                CommonTokenStream commonTokenStream = new CommonTokenStream(lexer);
                WikiParser parser = new WikiParser(commonTokenStream);
                parser.RemoveErrorListeners();
                parser.AddErrorListener(ael);
                WikiParser.InitContext initContext = parser.init();
                WikiVisitor visitor = new WikiVisitor(article,
                    new string[] { "Культурное наследие народов РФ",
                        "Культурное наследие народов РФ 4" }, null);
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


            Console.Write("Processing...");
            var results = new List<ResultRecord>();
            var sbExcl = new StringBuilder();
            foreach (Article article in articles)
            {
                if (article.Template == null)
                {
                    sbExcl.AppendLine($"* [[{article.Title}]] | templ_fail");
                    continue;
                }

                var cat1p = article.Template["Статус"];
                var cat2p = article.Template[4, true];

                if (cat1p != null && cat1p.Value == "В" ||
                    cat2p != null && cat2p.Value == "В")
                {
                    continue;
                }

                var lostp = article.Template["утрачен"];
                if (lostp != null)
                    continue;

                var reg = CombineTwoParams(
                    article.Template["Регион"],
                    article.Template[3, true]);

                if (reg != "")
                {
                    var rr = new ResultRecord();
                    rr.Title = article.Title;
                    rr.WdId = article.WdId;
                    rr.Region = reg;
                    rr.EGROKN = CombineTwoParams(
                        article.Template["рег_N"],
                        article.Template[1, true]);
                    var wdrecs = wdIds.Where(ids => ids.WdId == article.WdId);
                    rr.EGROKN += "<br>" + string.Join(" / ", wdrecs.Select(ids => ids.WdEGROKN));
                    var wvp = article.Template["Код-памятника"];
                    rr.WV = wvp != null ? wvp.Value : "";
                    rr.WV += "<br>" + string.Join(" / ", wdrecs.Select(ids => ids.WdWV));
                    results.Add(rr);
                }
            }


            var sb = new StringBuilder();

            sb.AppendLine("{|class=\"wide sortable\" style=\"table-layout: fixed;word-wrap:break-word\"");
            sb.AppendLine("!width=\"16em\"|№");
            sb.AppendLine("!Заголовок");
            sb.AppendLine("!width=\"90em\"|Элемент");
            sb.AppendLine("!Регион");
            sb.AppendLine("!ЕГР ОКН");
            sb.AppendLine("!Викигид");
            results = results.OrderBy(mm => mm.Title).ToList();
            for (int i = 0; i < results.Count; i++)
            {
                var rr = results[i];
                sb.AppendLine("|-");
                sb.AppendLine($"| {i + 1}");
                sb.AppendLine($"| [[{rr.Title}]]");
                sb.AppendLine($"| [[:d:{rr.WdId}|{rr.WdId}]]");
                sb.AppendLine($"| {rr.Region}");
                sb.AppendLine($"| {rr.EGROKN}");
                sb.AppendLine($"| {rr.WV}");
            }
            sb.AppendLine("|}");

            File.WriteAllText("result_excl.txt", sbExcl.ToString());
            File.WriteAllText("result.txt", sb.ToString());
            Console.WriteLine(" Done");
        }

        List<int> SearchArticles(string query, string ns = "0")
        {
            var idList = new List<int>();

            Console.Write("Searching articles");
            string sroffset = null;
            for (;;)
            {
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "list", "search",
                    "srwhat", "text",
                    "srsearch", query,
                    "srprop", "",
                    "srinfo", "",
                    "srlimit", "500",
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

        void RequestWdCodes()
        {
            Console.Write("Requesting wikidata codes...");
            wdIds = new List<WdIdsRecord>();

            string[] aids = db.Articles.Where(
                a => a.WdId != null).Select(a => a.WdId).Distinct().ToArray();
            string sparqlTemplate =
                "SELECT ?item ?egrokn ?wv WHERE" +
                "{" +
                "  VALUES ?item { __ids__ } ." +
                "  OPTIONAL { ?item wdt:P5381 ?egrokn . }" +
                "  OPTIONAL { ?item wdt:P1483 ?wv . }" +
                "}";
            var spResult = SparqlApi.Query(sparqlTemplate.Replace(
                "__ids__", string.Join(" ", aids.Select(x => $"wd:{x}"))));
            foreach (var r1 in spResult)
            {
                var r2 = new WdIdsRecord();
                r2.WdId = r1["item"];
                if (r1.ContainsKey("egrokn"))
                    r2.WdEGROKN = r1["egrokn"];
                if (r1.ContainsKey("wv"))
                    r2.WdWV = r1["wv"];
                wdIds.Add(r2);
            }
            Console.WriteLine(" Done");
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");

            /*
            var ids = SearchArticles("hastemplate:\"Культурное наследие народов РФ\"");
            DownloadArticles(ids.Distinct().ToArray());
            FillWikidataIds();
            */

            RequestWdCodes();
            ProcessArticles();
        }

        static void Main(string[] args)
        {
            db = new Db();
            new Program();
            db.Dispose();
        }
    }
}

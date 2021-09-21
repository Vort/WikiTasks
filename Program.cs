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
    class GKGNRecord
    {
        public int Id;
        public string Name;
        public string Type;
        public string ATE1;
        public string ATE2;
        public double Lat;
        public double Lon;
    }

    class ResultRecord
    {
        public string WdId;
        public string GkgnId;
    }

    class Program
    {
        MwApi wpApi;

        static Db db;

        List<GKGNRecord> gkgnRecords;

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
            var chunks = SplitToChunks(ids, 500);
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

        string Normalize(string name)
        {
            return Regex.Replace(Regex.Replace(name, " \\([^(]+\\)", "").Replace("ё", "е"),
                " (водохранилище|губа|пруд|ильмень|залив|канал|ледник|озеро)$", "");
        }

        void ProcessArticles()
        {
            int processed = 0;
            int lexerErrors = 0;
            int parserErrors = 0;

            var articles = db.Articles.ToArray();
            //var articles = db.Articles.Take(1000).ToArray();
            //var articles = db.Articles.Where(a => a.Title == "Лабынкыр").ToArray();

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
                    new string[] { "ГКГН", "РЗНГО",
                        "Реестры зарегистрированных наименований географических объектов" }, null);
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
                if (article.Template.Params.Count > 2)
                {
                    var gkgnVal = article.Template.Params[2].Value;
                    if (!Regex.IsMatch(gkgnVal, "[0-9]{7}"))
                        sbExcl.AppendLine($"* [[{article.Title}]] | val_fail | {gkgnVal}");
                    else
                        results.Add(new ResultRecord { WdId = article.WdId, GkgnId = gkgnVal });
                }
            }

            var verifiedResults = new List<ResultRecord>();
            foreach (ResultRecord result in results)
            {
                var articleTitle = articles.First(a => a.WdId == result.WdId).Title;
                var objName = Normalize(articleTitle);
                var gkgnId = int.Parse(result.GkgnId);
                GKGNRecord gkgnRec = gkgnRecords.FirstOrDefault(r => r.Id == gkgnId);
                if (gkgnRec == null)
                    sbExcl.AppendLine($"* [[{articleTitle}]] | no_gkgn_id_in_db | {result.GkgnId}");
                else
                {
                    var gkgnName = Normalize(gkgnRec.Name);
                    if (objName == gkgnName)
                        verifiedResults.Add(result);
                    else
                        sbExcl.AppendLine($"* [[{articleTitle}]] | name_mismatch | {result.GkgnId} | {gkgnRec.Name}");
                }
            }

            verifiedResults = verifiedResults.OrderByDescending(
                r => int.Parse(r.WdId.Substring(1))).ToList();
            var resString = string.Join(Environment.NewLine,
                verifiedResults.Select(r => $"{r.WdId} | {r.GkgnId}"));

            File.WriteAllText("result_excl.txt", sbExcl.ToString());
            File.WriteAllText("result.txt", resString);
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

        private void LoadGkgn()
        {
            Console.Write("Loading gkgn...");
            gkgnRecords = new List<GKGNRecord>();
            var lines = File.ReadAllLines(
                "goskatalog_Spisok_NP_i_ATE_na_vsu_RF_1_1.csv");
            foreach (var line in lines)
            {
                var ls = line.Split('|');
                var rec = new GKGNRecord();
                rec.Id = int.Parse(ls[0]);
                rec.Name = ls[1];
                rec.Type = ls[2];
                rec.ATE1 = ls[3];
                rec.ATE2 = ls[4];
                rec.Lat = double.Parse(ls[5]); // rus locale required
                rec.Lon = double.Parse(ls[6]);
                gkgnRecords.Add(rec);
            }
            Console.WriteLine(" Done");
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");

            var ids = SearchArticles("incategory:\"Водные объекты по алфавиту\""+
                " hastemplate:\"Реестры зарегистрированных наименований географических объектов\"" +
                " -incategory:\"Карточка водного объекта: Викиданные: указано свойство: код ГКГН\"");
            DownloadArticles(ids.Distinct().ToArray());
            FillWikidataIds();

            LoadGkgn();
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

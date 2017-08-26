using Antlr4.Runtime;
using LinqToDB;
using LinqToDB.Mapping;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using VBot;

namespace WikiTasks
{
    [Table(Name = "Articles")]
    public class Article
    {
        [PrimaryKey]
        public string WikidataItem;
        [Column()]
        public string Title;
        [Column()]
        public string WikiText;

        public string MouthParam;
        public List<string> Errors;
        public string P403Item;
    };

    public class ImportEntry
    {
        public string RootItem;
        public string MouthItem;
    }

    public class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<Article> Articles { get { return GetTable<Article>(); } }
    }

    class Program
    {
        static Db db;
        WebClient wc;

        string ApiRequest(string parameters)
        {
            byte[] gzb = null;
            for (;;)
            {
                gzb = wc.DownloadData("https://ru.wikipedia.org/w/api.php" + parameters + "&maxlag=1");
                if (wc.ResponseHeaders["Retry-After"] != null)
                {
                    int retrySec = int.Parse(wc.ResponseHeaders["Retry-After"]);
                    Thread.Sleep(retrySec * 1000);
                }
                else
                    break;
            }
            GZipStream gzs = new GZipStream(
                new MemoryStream(gzb), CompressionMode.Decompress);
            MemoryStream xmls = new MemoryStream();
            gzs.CopyTo(xmls);
            byte[] xmlb = xmls.ToArray();
            string xml = Encoding.UTF8.GetString(xmlb);
            return xml;
        }

        List<List<string>> SplitToChunks(string[] titles, int chunkSize)
        {
            int chunkCount = (titles.Length + chunkSize - 1) / chunkSize;

            List<List<string>> chunks = new List<List<string>>();
            for (int i = 0; i < chunkCount; i++)
            {
                List<string> chunk = new List<string>();
                for (int j = 0; j < chunkSize; j++)
                {
                    int k = i * chunkSize + j;
                    if (k >= titles.Length)
                        break;
                    chunk.Add(titles[k]);
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

                XmlNode revNode = pageNode.SelectSingleNode("revisions/rev");
                article.WikiText = revNode.InnerText;

                articles.Add(article);
            }

            return articles;
        }

        void DownloadArticles()
        {
            Console.Write("Downloading articles");
            var updChunks = SplitToChunks(
                db.Articles.Select(a => a.Title).ToArray(), 50);
            foreach (var chunk in updChunks)
            {
                string titlesString = string.Join("|", chunk);
                string xml = ApiRequest(
                    "?action=query" +
                    "&prop=revisions" +
                    "&rvprop=content" +
                    "&format=xml" +
                    "&titles=" + titlesString);
                Console.Write('.');

                List<Article> articles = GetArticles(xml);
                db.BeginTransaction();
                foreach (Article article in articles)
                {
                    article.WikidataItem = db.Articles.First(
                        a => a.Title == article.Title).WikidataItem;
                    db.Update(article);
                }
                db.CommitTransaction();
            }
            Console.WriteLine(" Done");
        }

        void RequestArticlesList()
        {
            db.CreateTable<Article>();

            Console.Write("Requesting articles list...");

            db.BeginTransaction();
            foreach (var petScanEntry in PetScan.Query(File.ReadAllText("petscan_query1.txt")))
            {
                db.Insert(new Article() {
                    Title = petScanEntry.ArticleTitle.Replace('_', ' '),
                    WikidataItem = petScanEntry.WikidataItem
                });
            }
            db.CommitTransaction();
            Console.WriteLine(" Done");
        }

        void FillArticlesDb()
        {
            wc = new WebClient();
            wc.Headers.Add("Accept-Encoding", "gzip");
            wc.Headers.Add("User-Agent", "WikiTasks");

            if (!db.DataProvider.GetSchemaProvider().GetSchema(db).
                Tables.Any(t => t.TableName == "Articles"))
            {
                RequestArticlesList();
            }
            if (db.Articles.All(a => a.WikiText == null))
                DownloadArticles();
        }

        void ProcessArticles(out List<Article> articles)
        {
            if (File.Exists("import_entries.json"))
            {
                articles = null;
                return;
            }

            int processed = 0;
            int lexerErrors = 0;
            int parserErrors = 0;

            articles = db.Articles/*.Take(50)*/.ToList();

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
                WikiVisitor visitor = new WikiVisitor();
                visitor.VisitInit(initContext);
                article.Errors = ael.ErrorList;
                article.MouthParam = visitor.MouthParam;

                Interlocked.Add(ref lexerErrors, ael.LexerErrors);
                Interlocked.Add(ref parserErrors, ael.ParserErrors);

                if (Interlocked.Increment(ref processed) % 50 == 0)
                    Console.Write('.');
            });
            stopwatch.Stop();

            Console.WriteLine(" Done");
            Console.WriteLine(" Articles: " + articles.Count());
            Console.WriteLine(" Mouth count: " + articles.Count(a => a.MouthParam != null));
            Console.WriteLine(" Parser errors: " + parserErrors);
            Console.WriteLine(" Lexer errors: " + lexerErrors);
            Console.WriteLine(" Parsing time: " + stopwatch.Elapsed.TotalSeconds + " sec");
            Console.WriteLine();

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
        }

        void ConvertNamesToItems(List<Article> articles)
        {
            if (File.Exists("import_entries.json"))
                return;

            Console.Write("Converting names to items...");

            var srcNames = articles.Where(a => a.MouthParam != null && !a.MouthParam.Contains('[')).
                Select(a => a.MouthParam).OrderBy(m => m).Distinct().ToArray();
            var qNames = PetScan.Query(File.ReadAllText("petscan_query2.txt").Replace("{{list}}",
                HttpUtility.UrlEncode(string.Join("\r\n", srcNames))));

            var importEntries = new List<ImportEntry>();
            foreach (var qName in qNames)
            {
                var foundArticles = articles.Where(
                    a => a.MouthParam == qName.ArticleTitle.Replace('_', ' ')).ToArray();
                foreach (var article in foundArticles)
                    importEntries.Add(new ImportEntry { RootItem = article.WikidataItem, MouthItem = qName.WikidataItem });
            }
            importEntries = importEntries.OrderByDescending(
                ie => int.Parse(ie.RootItem.Substring(1))).ToList();
            File.WriteAllText("import_entries.json", JsonConvert.SerializeObject(importEntries));
            Console.WriteLine(" Done");
        }

        void ApplyChanges()
        {
            string[] settings = File.ReadAllLines("auth.txt");
            string botName = settings[0];
            string botPassword = settings[1];

            Console.Write("Applying changes");

            WikimediaAPI wd = new WikimediaAPI("https://www.wikidata.org", botName, botPassword);

            var importEntries = JsonConvert.DeserializeObject<ImportEntry[]>(
                File.ReadAllText("import_entries.json"));

            var chunks = SplitToChunks(importEntries.Select(ie => ie.RootItem).ToArray(), 20);
            foreach (var chunk in chunks)
            {
                string qlist = string.Join("|", chunk);
                string entitiesJson = wd.LoadWD(qlist);

                var entities = JsonConvert.DeserializeObject<Entities>(
                    entitiesJson, new DatavalueConverter());
                foreach (var entity in entities.entities.Values)
                {
                    if (entity.claims.ContainsKey("P403"))
                        continue;

                    var importEntry = importEntries.First(ie => ie.RootItem == entity.id);

                    Claim claim = new Claim();
                    claim.mainsnak.property = "P403";
                    claim.mainsnak.datavalue = Utility.CreateDataValue(
                        importEntry.MouthItem, Utility.typeData.Item);

                    var snak = new Snak();
                    var reference = new Reference();
                    snak.datavalue = Utility.CreateDataValue(
                        "Q206855", Utility.typeData.Item);
                    reference.snaks.Add("P143", new List<Snak> { snak });

                    claim.references = new List<Reference> { reference };

                    wd.EditEntity(importEntry.RootItem, null, null, null, null,
                        new List<Claim> { claim }, "Import from ruwiki: [[Property:P403]]: [[" +
                        importEntry.MouthItem + "]]");
                    Console.Write(".");
                }
            }
            Console.WriteLine(" Done");
        }

        void DumpBadParameters()
        {
            var importEntries = JsonConvert.DeserializeObject<ImportEntry[]>(
                File.ReadAllText("import_entries.json"));

            var errArts = new List<string>();
            foreach (var article in db.Articles.ToArray())
                if (!importEntries.Any(ie => ie.RootItem == article.WikidataItem))
                    errArts.Add(article.Title);

            File.WriteAllLines("bad_parameters.txt", errArts.OrderBy(t => t));
        }

        Program()
        {
            FillArticlesDb();

            List<Article> articles;
            ProcessArticles(out articles);
            ConvertNamesToItems(articles);
            DumpBadParameters();
            ApplyChanges();
        }

        static void Main(string[] args)
        {
            db = new Db();
            new Program();
            db.Dispose();
        }
    }
}

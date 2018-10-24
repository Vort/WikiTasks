using Antlr4.Runtime;
using LinqToDB;
using LinqToDB.Mapping;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        public List<string> Errors;

        public string MouthTitle;
        public string MouthItem;
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
        MwApi wpApi;

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
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "prop", "revisions",
                    "rvprop", "content",
                    "format", "xml",
                    "titles", titlesString);
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
            foreach (var petScanEntry in PetScan.Query(
                "language", "ru",
                "categories", "Реки по алфавиту",
                "negcats", "Википедия:Статьи о реках, требующие проверки",
                "sparql", Properties.Resources.GetNoMouthRivers,
                "common_wiki", "cats",
                "wikidata_item", "with"))
            {
                db.Insert(new Article() {
                    Title = petScanEntry.Title.Replace('_', ' '),
                    WikidataItem = petScanEntry.WikidataItem
                });
            }
            db.CommitTransaction();
            Console.WriteLine(" Done");
        }

        void FillArticlesDb()
        {
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

            articles = db.Articles.ToList();

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
                WikiVisitor visitor = new WikiVisitor(article);
                visitor.VisitInit(initContext);
                article.Errors = ael.ErrorList;

                Interlocked.Add(ref lexerErrors, ael.LexerErrors);
                Interlocked.Add(ref parserErrors, ael.ParserErrors);

                if (Interlocked.Increment(ref processed) % 50 == 0)
                    Console.Write('.');
            });
            stopwatch.Stop();

            Console.WriteLine(" Done");
            Console.WriteLine(" Articles: " + articles.Count());
            Console.WriteLine(" Mouth count: " + articles.Count(a => a.MouthTitle != null));
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

        void FillMouthIds(List<Article> articles)
        {
            var srcNames = articles.Where(a => a.MouthTitle != null &&
                !a.MouthTitle.Contains('[') && !a.MouthTitle.Contains('{')).
                Select(a => a.MouthTitle).OrderBy(m => m).Distinct().ToArray();

            var mouthNames = new Dictionary<string, HashSet<string>>();

            var chunks = SplitToChunks(srcNames, 50);
            foreach (var chunk in chunks)
            {
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "prop", "pageprops",
                    "ppprop", "wikibase_item",
                    "redirects", "1",
                    "format", "xml",
                    "titles", string.Join("|", chunk));
                Console.Write('.');

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                foreach (XmlNode pageNode in doc.SelectNodes("/api/query/pages/page/pageprops"))
                {
                    string title = pageNode.ParentNode.Attributes["title"].Value;
                    string id = pageNode.Attributes["wikibase_item"].Value;
                    if (!mouthNames.ContainsKey(id))
                        mouthNames[id] = new HashSet<string> { title };
                }

                foreach (XmlNode pageNode in doc.SelectNodes("/api/query/redirects/r"))
                {
                    string from = pageNode.Attributes["from"].Value;
                    string to = pageNode.Attributes["to"].Value;
                    mouthNames.First(kv => kv.Value.Contains(to)).Value.Add(from);
                }
            }

            foreach (var kv in mouthNames)
                foreach (var article in articles.Where(a => kv.Value.Contains(a.MouthTitle)))
                    article.MouthItem = kv.Key;
        }

        void ConvertNamesToItems(List<Article> articles)
        {
            if (File.Exists("import_entries.json"))
                return;

            Console.Write("Converting names to items");

            var subclasses = SparqlApi.Query(
                Properties.Resources.GetWoSubclasses).Select(e => e["type"]).ToArray();

            FillMouthIds(articles);

            string[] ids = articles.Where(
                a => a.MouthItem != null).Select(a => a.MouthItem).Distinct().ToArray();
            var objTypes = SparqlApi.Query(Properties.Resources.GetTypes.Replace(
                "__ids__", string.Join(" ", ids.Select(x => $"wd:{x}"))));

            var goodObjects = objTypes.Where(
                e => subclasses.Contains(e["type"])).Select(e => e["item"]).Distinct().ToArray();

            var importEntries = new List<ImportEntry>();
            foreach (var goodObjId in goodObjects)
            {
                var foundArticles = articles.Where(
                    a => a.MouthItem == goodObjId).ToArray();
                foreach (var article in foundArticles)
                {
                    importEntries.Add(new ImportEntry
                    {
                        RootItem = article.WikidataItem,
                        MouthItem = article.MouthItem
                    });
                }
            }
            importEntries = importEntries.OrderByDescending(
                ie => int.Parse(ie.RootItem.Substring(1))).ToList();
            File.WriteAllText("import_entries.json", JsonConvert.SerializeObject(importEntries));
            Console.WriteLine(" Done");
        }

        void ApplyChanges()
        {
            string[] settings = File.ReadAllLines("auth2.txt");
            if (settings.Length != 2)
                throw new Exception();
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

            File.WriteAllLines("bad_parameters_all.txt", errArts.OrderBy(t => t));

            errArts = PetScan.Query(
                "language", "ru",
                "manual_list", string.Join("\r\n", errArts),
                "manual_list_wiki", "ruwiki",
                "categories", "Карточка реки: исправить: Устье\r\nКарточка реки: заполнить: Устье\r\nКарточка реки: нет статьи об устье",
                "combination", "union",
                "source_combination", "manual not categories").Select(pe => pe.Title.Replace("_", " ")).ToList();
            File.WriteAllLines("bad_parameters.txt", errArts.OrderBy(t => t).Select(n => $"# [[{n}]]"));
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");
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

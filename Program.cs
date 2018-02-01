using LinqToDB;
using LinqToDB.Mapping;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
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
        Failure = 2
    }

    [Table(Name = "Items")]
    public class DbItem
    {
        [PrimaryKey]
        public int Id;
        [Column()]
        public string SrcData;
        [Column()]
        public string ReplData;
        [Column()]
        public ProcessStatus Status;
    };

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
        [Column()]
        public int ItemId;
    };

    public class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<DbItem> Items { get { return GetTable<DbItem>(); } }

        public ITable<Article> Articles { get { return GetTable<Article>(); } }
    }


    class Program
    {
        MwApi wpApi;
        MwApi wdApi;
        static Db db;

        const int editsPerMinute = 120;
        const int connectionsLimit = 4;

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

        string GetToken(string type)
        {
            string xml = wdApi.PostRequest(
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
                article.WikiText = revNode.InnerText;
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

        void DownloadItems(QId[] ids)
        {
            if (HaveTable("Items"))
                db.DropTable<DbItem>();
            db.CreateTable<DbItem>();

            Console.Write("Downloading items");
            var chunks = SplitToChunks(ids, 50);
            foreach (var chunk in chunks)
            {
                string json = wdApi.PostRequest(
                    "action", "wbgetentities",
                    "format", "json",
                    "ids", string.Join("|", chunk),
                    "redirects", "no");
                Console.Write('.');

                var obj = JObject.Parse(json);

                if (obj.Count != 2)
                    throw new Exception();
                if ((int)obj["success"] != 1)
                    throw new Exception();
                if (!obj["entities"].All(t => t is JProperty))
                    throw new Exception();
                db.BeginTransaction();
                foreach (JToken t in obj["entities"])
                {
                    var kv = t as JProperty;
                    db.Insert(new DbItem
                    {
                        Id = new QId(kv.Name).Id,
                        SrcData = kv.Value.ToString()
                    });
                }
                db.CommitTransaction();
            }
            Console.WriteLine(" Done");
        }

        bool FixDate(Dictionary<PId, List<Claim>> claims,
            string wikiText, int propertyId, string paramName)
        {
            var p = new PId(propertyId);

            if (!claims.ContainsKey(p))
                return false;

            var pcs = claims[p];
            if (pcs.Count != 1)
                return false;

            var c = pcs[0];

            if (c.Qualifiers == null)
                return false;

            if (c.Qualifiers.Count != 1)
                return false;

            var q = c.Qualifiers[0];

            if (q.Count != 1)
                return false;

            var s = q[0];

            if (s.Property.Id != 31)
                return false;

            if ((s.DataValue.Value as QId).Id != 26932615)
                return false;

            var wdt = c.MainSnak.DataValue.Value as WdTime;
            if (wdt.Precision != 11)
                return false;
            if (wdt.Time[0] != '+')
                return false;

            if (wdt.CalendarModel != "http://www.wikidata.org/entity/Q1985786")
                return false;

            var matches = Regex.Matches(wikiText,
                $"\\| *{paramName} *= *([0-9]+)\\.([0-9]+)\\.([0-9]+) ?" +
                "\\(([0-9]{1,2})(\\.([0-9]{1,2}))?(\\.([0-9]{4}))?\\)",
                RegexOptions.IgnoreCase);
            if (matches.Count != 1)
                return false;

            var match = matches[0];
            int gDay = int.Parse(match.Groups[1].Value);
            int gMonth = int.Parse(match.Groups[2].Value);
            int gYear = int.Parse(match.Groups[3].Value);
            int jDay = int.Parse(match.Groups[4].Value);
            int? jMonth = null;
            int? jYear = null;
            if (match.Groups[6].Value != "")
                jMonth = int.Parse(match.Groups[6].Value);
            if (match.Groups[8].Value != "")
                jYear = int.Parse(match.Groups[8].Value);

            DateTime t;
            if (!DateTime.TryParse(wdt.Time.Substring(1),
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t))
            {
                return false;
            }

            if (t.Day != gDay)
                return false;
            if (t.Month != gMonth)
                return false;
            if (t.Year != gYear)
                return false;

            var jc = new JulianCalendar();
            DateTime jt = new DateTime(jc.GetYear(t), jc.GetMonth(t), jc.GetDayOfMonth(t));

            if (jt.Day != jDay)
                return false;
            if (jMonth != null && jMonth != jt.Month)
                return false;
            if (jYear != null && jYear != jt.Year)
                return false;

            wdt.Time = "+" + jt.ToString("s") + "Z";
            c.Qualifiers = null;

            if (c.References == null)
                c.References = new List<Reference> { new Reference(new PId(143), new QId(206855)) };

            return true;
        }

        void FillReplData()
        {
            int i = 0;
            int replCnt = 0;

            Console.Write("Processing articles");
            var dba = db.Items.Where(dbi => dbi.Status == ProcessStatus.NotProcessed).ToArray();
            db.BeginTransaction();
            foreach (var dbi in dba)
            {
                var item = new Item(dbi.SrcData);
                var json2 = item.ToString();
                if (!JToken.DeepEquals(JObject.Parse(json2), JObject.Parse(dbi.SrcData)))
                    throw new Exception();

                var article = db.Articles.First(a => a.ItemId == item.Id.Id);

                bool bf = FixDate(item.Claims, article.WikiText, 569, "Дата рождения");
                bool df = FixDate(item.Claims, article.WikiText, 570, "Дата смерти");

                if (bf || df)
                {
                    dbi.ReplData = item.ToString();
                    db.Update(dbi);
                    replCnt++;
                }
                i++;
                if (i % 50 == 0)
                    Console.Write('.');
            }
            db.CommitTransaction();
            Console.WriteLine(" Done");
            Console.WriteLine($"Replacements: {replCnt}");
        }

        async Task<bool> UpdateEntity(Item item, string summary)
        {
            string json = await wdApi.PostRequestAsync(
                "action", "wbeditentity",
                "format", "json",
                "id", item.Id.ToString(),
                "baserevid", item.LastRevId.ToString(),
                "summary", summary,
                "token", csrfToken,
                "bot", "1",
                "data", item.ToString(),
                "clear", "1");

            var obj = JObject.Parse(json);
            return obj["success"] != null;
        }

        void MakeReplacements()
        {
            var tasks = new List<Task>();
            Console.Write("Making replacements");
            foreach (var dbi in db.Items.Where(i => i.Status == 0 && i.ReplData != null).Take(5).ToArray())
            {
                if (tasks.Any(t => t.IsFaulted))
                    Task.WaitAll(tasks.ToArray());
                tasks.RemoveAll(t => t.IsCompleted);

                Task<bool> updateTask = UpdateEntity(new Item(dbi.ReplData),
                    "Reimport julian dates from ruwiki");
                tasks.Add(updateTask);
                tasks.Add(updateTask.ContinueWith(cont =>
                {
                    bool isUpdateSuccessful = cont.Result;
                    lock (db)
                    {
                        Console.Write(isUpdateSuccessful ? '.' : 'x');
                        dbi.Status = isUpdateSuccessful ? ProcessStatus.Success : ProcessStatus.Failure;
                        db.Update(dbi);
                    }
                }));
                Thread.Sleep(60 * 1000 / editsPerMinute);
            }
            Task.WaitAll(tasks.ToArray());
            Console.WriteLine(" Done");
        }

        Program()
        {
            Console.Write("Obtaining article list...");
            var petRes = PetScan.Query(
                "language", "ru",
                "sparql", "SELECT ?item WHERE { { ?item p:P569 ?s1 . ?s1 pq:P31 wd:Q26932615 } UNION { ?item p:P570 ?s2 . ?s2 pq:P31 wd:Q26932615 } } GROUP BY ?item ORDER BY ?item",
                "common_wiki", "cats",
                "wikidata_item", "with");
            Console.WriteLine(" Done");

            wpApi = new MwApi("ru.wikipedia.org");


            DownloadArticles(petRes.Select(r => r.Id).ToArray());

            var articles = db.Articles.ToArray();
            db.BeginTransaction();
            foreach (Article a in articles)
            {
                a.ItemId = new QId(petRes.First(r => r.Id == a.PageId).WikidataItem).Id;
                db.Update(a);
            }
            db.CommitTransaction();

            wdApi = new MwApi("www.wikidata.org");
            ObtainEditToken();

            DownloadItems(db.Articles.ToArray().Select(a => new QId(a.ItemId)).ToArray());

            FillReplData();

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
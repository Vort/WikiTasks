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

    class MonolingualText
    {
        public MonolingualText(JObject obj)
        {
            Obj = obj;
        }

        public JObject ToJObject()
        {
            return Obj;
        }

        public JObject Obj;
    }

    class Quantity
    {
        public Quantity(JObject obj)
        {
            Obj = obj;
        }

        public JObject ToJObject()
        {
            return Obj;
        }

        public JObject Obj;
    }

    class WdTime
    {
        public WdTime(JObject obj)
        {
            if (obj.Count != 6)
                throw new Exception();
            if ((int)obj["before"] != 0)
                throw new Exception();
            if ((int)obj["after"] != 0)
                throw new Exception();
            Time = (string)obj["time"];
            TimeZone = (int)obj["timezone"];
            Precision = (int)obj["precision"];
            CalendarModel = (string)obj["calendarmodel"];
        }

        public JObject ToJObject()
        {
            var o = new JObject();
            o["before"] = 0;
            o["after"] = 0;
            o["time"] = Time;
            o["timezone"] = TimeZone;
            o["precision"] = Precision;
            o["calendarmodel"] = CalendarModel;
            return o;
        }

        public string Time;
        public int TimeZone;
        public int Precision;
        public string CalendarModel;
    }

    class Coordinate
    {
        public Coordinate(JObject obj)
        {
            if (obj.Count != 5)
                throw new Exception();
            if (obj["altitude"].Type != JTokenType.Null)
                throw new Exception();
            Globe = (string)obj["globe"];
            Latitude = (double)obj["latitude"];
            Longitude = (double)obj["longitude"];
            Precision = (double?)obj["precision"];
        }

        public JObject ToJObject()
        {
            var o = new JObject();
            o["altitude"] = JValue.CreateNull();
            o["globe"] = Globe;
            o["latitude"] = NumberToJValue(Latitude);
            o["longitude"] = NumberToJValue(Longitude);
            o["precision"] = NumberToJValue(Precision);
            return o;
        }

        private static JValue NumberToJValue(double? num)
        {
            if (num == null)
                return JValue.CreateNull();
            if (Math.Truncate((double)num) == num)
            {
                try
                {
                    return new JValue(Convert.ToInt32(num));
                }
                catch { }
            }
            return new JValue(num);
        }

        public double Latitude;
        public double Longitude;
        public double? Precision;
        public string Globe;
    }

    class DataValue
    {
        public DataValue(JObject obj)
        {
            if (obj.Count != 2)
                throw new Exception();
            string type = (string)obj["type"];
            JToken valueTok = obj["value"];
            JObject valueObj = valueTok as JObject;
            if (type == "string")
                Value = (string)valueTok;
            else if (type == "globecoordinate")
                Value = new Coordinate(valueObj);
            else if (type == "wikibase-entityid")
            {
                int id = (int)valueObj["numeric-id"];
                string entityType = (string)valueObj["entity-type"];
                if (entityType == "item")
                    Value = new QId(id);
                else if (entityType == "property")
                    Value = new PId(id);
                else
                    throw new Exception();
            }
            else if (type == "quantity")
                Value = new Quantity(valueObj);
            else if (type == "time")
                Value = new WdTime(valueObj);
            else if (type == "monolingualtext")
                Value = new MonolingualText(valueObj);
            else
                throw new Exception();
        }

        public JObject ToJObject()
        {
            JToken value = null;
            string type = null;
            if (Value is string)
            {
                type = "string";
                value = (string)Value;
            }
            else if (Value is Coordinate)
            {
                type = "globecoordinate";
                value = (Value as Coordinate).ToJObject();
            }
            else if (Value is WdId)
            {
                type = "wikibase-entityid";
                value = (Value as WdId).ToJObject();
            }
            else if (Value is Quantity)
            {
                type = "quantity";
                value = (Value as Quantity).ToJObject();
            }
            else if (Value is WdTime)
            {
                type = "time";
                value = (Value as WdTime).ToJObject();
            }
            else if (Value is MonolingualText)
            {
                type = "monolingualtext";
                value = (Value as MonolingualText).ToJObject();
            }
            else
                throw new Exception();

            var o = new JObject();
            o["type"] = type;
            o["value"] = value;
            return o;
        }

        public object Value;
    }

    class Snak
    {
        public Snak(JObject obj)
        {
            SnakType = (string)obj["snaktype"];
            Property = new PId((string)obj["property"]);
            Hash = (string)obj["hash"];
            DataType = (string)obj["datatype"];
            if (SnakType == "value")
            {
                DataValue = new DataValue((JObject)obj["datavalue"]);
                if (obj.Count != 5)
                    throw new Exception();
            }
            else
            {
                if (obj.Count != 4)
                    throw new Exception();
            }
        }

        public JObject ToJObject()
        {
            var o = new JObject();
            o["snaktype"] = SnakType;
            o["property"] = Property.ToString();
            o["hash"] = Hash;
            if (SnakType == "value")
                o["datavalue"] = DataValue.ToJObject();
            o["datatype"] = DataType;
            return o;
        }

        public string SnakType;
        public PId Property;
        public string Hash;
        public DataValue DataValue;
        public string DataType;
    }

    class Reference
    {
        public Reference(JObject obj)
        {
            if (obj.Count != 3)
                throw new Exception();

            Hash = (string)obj["hash"];
            var snaks = (JObject)obj["snaks"];
            var snaksOrder = ((JArray)obj["snaks-order"]).Select(x => (string)x).ToArray();

            Snaks = new OrderedDictionary<PId, List<Snak>>();
            foreach (var s in snaksOrder)
            {
                Snaks.Add(new PId(s),
                    ((JArray)snaks[s]).Select(x => new Snak((JObject)x)).ToList());
            }
        }

        public JObject ToJObject()
        {
            var o = new JObject();
            o["hash"] = Hash;
            o["snaks"] = JObject.FromObject(Snaks.ToDictionary(
                kv => kv.Key, kv => kv.Value.Select(s => s.ToJObject())));
            o["snaks-order"] = JArray.FromObject(Snaks.Keys.Select(id => id.ToString()));
            return o;
        }

        public string Hash;
        public OrderedDictionary<PId, List<Snak>> Snaks;
    }

    class Claim
    {
        public Claim(JObject obj)
        {
            JObject qualifiers = null;
            string[] qualifiersOrder = null;

            foreach (var t in obj)
            {
                if (t.Key == "id")
                    Id = (string)t.Value;
                else if (t.Key == "rank")
                    Rank = (string)t.Value;
                else if (t.Key == "mainsnak")
                    MainSnak = new Snak((JObject)t.Value);
                else if (t.Key == "references")
                    References = ((JArray)t.Value).Select(x => new Reference((JObject)x)).ToList();
                else if (t.Key == "qualifiers")
                    qualifiers = (JObject)t.Value;
                else if (t.Key == "qualifiers-order")
                    qualifiersOrder = ((JArray)t.Value).Select(x => (string)x).ToArray();
                else if (t.Key == "type")
                {
                    if ((string)t.Value != "statement")
                        throw new Exception();
                }
                else
                    throw new Exception();
            }

            if (qualifiers != null)
            {
                Qualifiers = new OrderedDictionary<PId, List<Snak>>();
                foreach (var q in qualifiersOrder)
                {
                    Qualifiers.Add(new PId(q),
                        ((JArray)qualifiers[q]).Select(x => new Snak((JObject)x)).ToList());
                }
            }
        }

        public JObject ToJObject()
        {
            var o = new JObject();
            o["id"] = Id;
            o["rank"] = Rank;
            o["type"] = "statement";
            o["mainsnak"] = MainSnak.ToJObject();
            if (Qualifiers != null)
            {
                o["qualifiers"] = JObject.FromObject(Qualifiers.ToDictionary(
                    kv => kv.Key, kv => kv.Value.Select(s => s.ToJObject())));
                o["qualifiers-order"] = JArray.FromObject(Qualifiers.Keys.Select(id => id.ToString()));
            }
            if (References != null)
                o["references"] = JArray.FromObject(References.Select(r => r.ToJObject()));
            return o;
        }


        public Snak MainSnak;
        public string Id;
        public string Rank;
        public OrderedDictionary<PId, List<Snak>> Qualifiers;
        public List<Reference> References;
    }

    class Item
    {
        public Item(string json)
        {
            var o = JObject.Parse(json);
            if (o.Count != 12)
                throw new Exception();
            if ((int)o["ns"] != 0)
                throw new Exception();
            if ((string)o["type"] != "item")
                throw new Exception();
            Id = new QId((string)o["id"]);
            if ((string)o["title"] != Id.ToString())
                throw new Exception();
            PageId = (int)o["pageid"];
            LastRevId = (long)o["lastrevid"];
            Modified = (DateTime)o["modified"];

            Labels = o["labels"].ToDictionary(
                t => ((JProperty)t).Name,
                t => (string)((JProperty)t).Value["value"]);
            Descriptions = o["descriptions"].ToDictionary(
                t => ((JProperty)t).Name,
                t => (string)((JProperty)t).Value["value"]);
            Aliases = (JObject)o["aliases"];
            Sitelinks = (JObject)o["sitelinks"];

            Claims = o["claims"].ToDictionary(
                t => new PId(((JProperty)t).Name),
                t => ((JArray)((JProperty)t).Value).
                    Select(c => new Claim((JObject)c)).ToList());
        }

        public override string ToString()
        {
            var o = new JObject();
            o["ns"] = 0;
            o["type"] = "item";
            o["pageid"] = PageId;
            o["title"] = Id.ToString();
            o["lastrevid"] = LastRevId;
            o["modified"] = Modified;
            o["id"] = Id.ToString();
            o["labels"] = JObject.FromObject(Labels.ToDictionary(kv => kv.Key,
                kv => new JObject { { "language", kv.Key }, { "value", kv.Value } }));
            o["descriptions"] = JObject.FromObject(Descriptions.ToDictionary(kv => kv.Key,
                kv => new JObject { { "language", kv.Key }, { "value", kv.Value } }));
            o["aliases"] = Aliases;
            o["sitelinks"] = Sitelinks;

            o["claims"] = JObject.FromObject(Claims.ToDictionary(kv => kv.Key,
                kv => kv.Value.Select(c => c.ToJObject())));

            return o.ToString();
        }

        public int PageId;
        public long LastRevId;
        public DateTime Modified;
        public QId Id;

        public Dictionary<string, string> Labels;
        public Dictionary<string, string> Descriptions;
        public JObject Aliases;
        public Dictionary<PId, List<Claim>> Claims;
        public JObject Sitelinks;
    }


    abstract class WdId
    {
        public WdId(char c, int id)
        {
            if (id < 1)
                throw new Exception();

            Id = id;
            this.c = c;
        }

        public WdId(char c, string id)
        {
            if (id[0] != c)
                throw new Exception();

            int idi = int.Parse(id.Substring(1));
            if (idi < 1)
                throw new Exception();

            Id = idi;
            this.c = c;
        }

        public override string ToString()
        {
            return c + Id.ToString();
        }

        public abstract JObject ToJObject();

        private readonly char c;
        public readonly int Id;
    }

    class QId : WdId
    {
        public QId(int id) : base('Q', id)
        {
        }

        public QId(string id) : base('Q', id)
        {
        }

        public override JObject ToJObject()
        {
            var o = new JObject();
            o["id"] = ToString();
            o["numeric-id"] = Id;
            o["entity-type"] = "item";
            return o;
        }
    }

    class PId : WdId, IEquatable<PId>
    {
        public PId(int id) : base('P', id)
        {
        }

        public PId(string id) : base('P', id)
        {
        }

        public override int GetHashCode()
        {
            return Id;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as PId);
        }

        public bool Equals(PId obj)
        {
            return obj != null && obj.Id == Id;
        }

        public override JObject ToJObject()
        {
            var o = new JObject();
            o["id"] = ToString();
            o["numeric-id"] = Id;
            o["entity-type"] = "property";
            return o;
        }
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

            var matches = Regex.Matches(wikiText, $"\\| *{paramName} *= *([0-9]+).([0-9]+).([0-9]+) ?\\(([0-9]+)\\)");
            if (matches.Count != 1)
                return false;

            var match = matches[0];

            int gDay = int.Parse(match.Groups[1].Value);
            int gMonth = int.Parse(match.Groups[2].Value);
            int gYear = int.Parse(match.Groups[3].Value);
            int jDay = int.Parse(match.Groups[4].Value);

            var wdt = c.MainSnak.DataValue.Value as WdTime;
            if (wdt.Precision != 11)
                return false;
            if (wdt.Time[0] != '+')
                return false;
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

            wdt.Time = "+" + jt.ToString("s") + "Z";
            c.Qualifiers = null;

            return true;
        }

        void FillReplData()
        {
            int replCnt = 0;

            Console.Write("Processing articles...");
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
                    if (replCnt % 50 == 0)
                        Console.Write('.');
                }
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
            foreach (var dbi in db.Items.Where(i => i.Status == 0 && i.ReplData != null).Take(2).ToArray())
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
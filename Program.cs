using LinqToDB;
using LinqToDB.Mapping;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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

    public class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<DbItem> Items { get { return GetTable<DbItem>(); } }
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

    class Time
    {
        public Time(JObject obj)
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
                Value = new Time(valueObj);
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
            else if (Value is Time)
            {
                type = "time";
                value = (Value as Time).ToJObject();
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
        MwApi api;
        static Db db;

        const int editsPerMinute = 120;
        const int connectionsLimit = 4;

        string csrfToken;

        List<List<QId>> SplitToChunks(QId[] ids, int chunkSize)
        {
            int chunkCount = (ids.Length + chunkSize - 1) / chunkSize;

            var chunks = new List<List<QId>>();
            for (int i = 0; i < chunkCount; i++)
            {
                var chunk = new List<QId>();
                for (int j = 0; j < chunkSize; j++)
                {
                    int k = i * chunkSize + j;
                    if (k >= ids.Length)
                        break;
                    chunk.Add(ids[k]);
                }
                chunks.Add(chunk);
            }

            return chunks;
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

        QId[] GetIds()
        {
            string[] ids = SparqlApi.GetItemIds(
                SparqlApi.RunWikidataQuery("dup_sparql.txt"), "river");
            return ids.Select(i => new QId(i)).ToArray();
        }

        void GetItems(QId[] ids)
        {
            if (HaveTable("Items"))
                db.DropTable<DbItem>();
            db.CreateTable<DbItem>();

            Console.Write("Downloading items");
            var chunks = SplitToChunks(ids, 50);
            foreach (var chunk in chunks)
            {
                string json = api.PostRequest(
                    "action", "wbgetentities",
                    "format", "json",
                    "ids", string.Join<QId>("|", chunk),
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

        public static double DegToRad(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        public static double Distance(double lat1, double lon1, double lat2, double lon2)
        {
            double r = 6371000.0;
            double lat1rad = DegToRad(lat1);
            double lat2rad = DegToRad(lat2);
            double deltaLatRad = DegToRad(lat2 - lat1);
            double deltaLonRad = DegToRad(lon2 - lon1);

            double a = Math.Sin(deltaLatRad / 2) * Math.Sin(deltaLatRad / 2) +
                Math.Cos(lat1rad) * Math.Cos(lat2rad) *
                Math.Sin(deltaLonRad / 2) * Math.Sin(deltaLonRad / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            return r * c;
        }

        void GetCoord(Claim claim, out double lat, out double lon)
        {
            var coord = claim.MainSnak.DataValue.Value as Coordinate;
            lat = coord.Latitude;
            lon = coord.Longitude;
        }

        void FillReplData()
        {
            int replCnt = 0;
            int nullPrec = 0;

            var dba = db.Items.Where(dbi => dbi.Status == ProcessStatus.NotProcessed).ToArray();
            db.BeginTransaction();
            foreach (var dbi in dba)
            {
                var item = new Item(dbi.SrcData);
                var json2 = item.ToString();
                if (!JToken.DeepEquals(JObject.Parse(json2), JObject.Parse(dbi.SrcData)))
                    throw new Exception();

                var p625 = item.Claims[new PId(625)];

                bool modified;
                bool modifiedOnce = false;
                for (;;)
                {
                    modified = false;

                    var fltp625 = p625.Where(c =>
                        c.References != null &&
                        c.References.Count == 1 &&
                        c.References[0].Snaks.Count == 1 &&
                        c.References[0].Snaks.ContainsKey(new PId(143)) &&
                        c.References[0].Snaks[new PId(143)].Count == 1 &&
                        (c.References[0].Snaks[new PId(143)][0].DataValue.Value as QId).Id == 206855);

                    foreach (var c1 in fltp625.Where(c => c.Qualifiers == null))
                    {
                        double c1lat;
                        double c1lon;

                        GetCoord(c1, out c1lat, out c1lon);

                        foreach (var c2 in fltp625.Where(c => c.Id != c1.Id))
                        {
                            double c2lat;
                            double c2lon;

                            GetCoord(c2, out c2lat, out c2lon);

                            double d = Distance(c1lat, c1lon, c2lat, c2lon);
                            if (d < 0.1)
                            {
                                p625.RemoveAll(c => c.Id == c1.Id);
                                modifiedOnce = true;
                                modified = true;
                                break;
                            }
                        }
                        if (modified)
                            break;
                    }

                    if (!modified)
                        break;
                }

                if (modifiedOnce)
                {
                    if (!p625.Any(c => (c.MainSnak.DataValue.Value as Coordinate).Precision == null))
                    {
                        dbi.ReplData = item.ToString();
                        db.Update(dbi);
                        replCnt++;
                    }
                    else
                        nullPrec++;
                }
            }
            db.CommitTransaction();
            Console.WriteLine($"Replacements: {replCnt}");
            Console.WriteLine($"Null precision: {nullPrec}");
        }

        async Task<bool> UpdateEntity(Item item, string summary)
        {
            string json = await api.PostRequestAsync(
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
            foreach (var dbi in db.Items.Where(i => i.Status == 0 && i.ReplData != null).Take(120).ToArray())
            {
                if (tasks.Any(t => t.IsFaulted))
                    Task.WaitAll(tasks.ToArray());
                tasks.RemoveAll(t => t.IsCompleted);

                Task<bool> updateTask = UpdateEntity(new Item(dbi.ReplData),
                    "Remove duplicate coordinates (distance < 0.1m)");
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
            api = new MwApi("www.wikidata.org");
            ObtainEditToken();

            //GetItems(GetIds());

            //FillReplData();

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
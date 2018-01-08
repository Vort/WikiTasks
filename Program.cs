using LinqToDB;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Newtonsoft.Json.Linq;
using System.Globalization;

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
                DataValue = (JObject)obj["datavalue"];
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
                o["datavalue"] = DataValue;
            o["datatype"] = DataType;
            return o;
        }

        public string SnakType;
        public PId Property;
        public string Hash;
        public JObject DataValue;
        public string DataType;
    }

    class Claim
    {
        public Claim(JObject obj)
        {
            foreach (var t in obj)
            {
                if (t.Key == "id")
                    Id = (string)t.Value;
                else if (t.Key == "rank")
                    Rank = (string)t.Value;
                else if (t.Key == "mainsnak")
                    MainSnak = new Snak((JObject)t.Value);
                else if (t.Key == "references")
                    References = ((JArray)t.Value).Select(x => (JObject)x).ToArray();
                else if (t.Key == "qualifiers")
                    Qualifiers = (JObject)t.Value;
                else if (t.Key == "qualifiers-order")
                    QualifiersOrder = ((JArray)t.Value).Select(x => (string)x).ToArray();
                else if (t.Key == "type")
                {
                    if ((string)t.Value != "statement")
                        throw new Exception();
                }
                else
                    throw new Exception();
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
                o["qualifiers"] = Qualifiers;
                o["qualifiers-order"] = JArray.FromObject(QualifiersOrder);
            }
            if (References != null)
                o["references"] = JArray.FromObject(References);
            return o;
        }


        public Snak MainSnak;
        public string Id;
        public string Rank;
        public JObject Qualifiers;
        public string[] QualifiersOrder;
        public JObject[] References;
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
    }


    class Program
    {
        MwApi api;
        static Db db;

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

        void GetCoord(Claim c, out double lat, out double lon)
        {
            var o = c.MainSnak.DataValue["value"];
            lat = double.Parse((string)o["latitude"], CultureInfo.InvariantCulture);
            lon = double.Parse((string)o["longitude"], CultureInfo.InvariantCulture);
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

                    foreach (var c1 in p625.Where(c => 
                        c.Qualifiers == null && c.References == null))
                    {
                        double c1lat;
                        double c1lon;

                        GetCoord(c1, out c1lat, out c1lon);

                        foreach (var c2 in p625.Where(c => c.Id != c1.Id))
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
                    if (!p625.Any(c => c.MainSnak.DataValue["value"]["precision"].Type == JTokenType.Null))
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

        bool UpdateEntity(Item item, string summary)
        {
            string json = api.PostRequest(
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
            Console.Write("Making replacements");
            foreach (var dbi in db.Items.Where(i => i.Status == 0 && i.ReplData != null).Take(10).ToArray())
            {
                bool success = UpdateEntity(new Item(dbi.ReplData),
                    "Remove duplicate coordinates (distance < 0.1m)");
                dbi.Status = success ? ProcessStatus.Success : ProcessStatus.Failure;
                Console.Write(success ? '.' : 'x');
                db.Update(dbi);
            }
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
            db = new Db();
            new Program();
            db.Dispose();
        }
    }
}
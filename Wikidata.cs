using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WikiTasks
{
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

        public DataValue(QId qId)
        {
            Value = qId;
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

        public Snak(PId pId, QId qId)
        {
            SnakType = "value";
            Property = pId;
            Hash = "";
            DataType = "wikibase-item";
            DataValue = new DataValue(qId);
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

        public Reference(PId pId, QId qId)
        {
            Hash = "";
            Snaks = new OrderedDictionary<PId, List<Snak>>();
            Snaks.Add(pId, new List<Snak> { new Snak(pId, qId) });
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
            if (o.Count != 8)
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

            /*
            Labels = o["labels"].ToDictionary(
                t => ((JProperty)t).Name,
                t => (string)((JProperty)t).Value["value"]);
            Descriptions = o["descriptions"].ToDictionary(
                t => ((JProperty)t).Name,
                t => (string)((JProperty)t).Value["value"]);
            Aliases = (JObject)o["aliases"];
            Sitelinks = (JObject)o["sitelinks"];
            */

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
            if (Labels != null)
            {
                o["labels"] = JObject.FromObject(Labels.ToDictionary(kv => kv.Key,
                    kv => new JObject { { "language", kv.Key }, { "value", kv.Value } }));
            }
            if (Descriptions != null)
            {
                o["descriptions"] = JObject.FromObject(Descriptions.ToDictionary(kv => kv.Key,
                    kv => new JObject { { "language", kv.Key }, { "value", kv.Value } }));
            }
            if (Aliases != null)
                o["aliases"] = Aliases;
            if (Sitelinks != null)
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
}

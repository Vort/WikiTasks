using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using ProtoBuf;

namespace WikiTasks
{
    class OsmRelationMemberJson
    {
        public string type;
        [JsonProperty("ref")]
        public long id;
        public string role;
    }

    class OsmObjectJson
    {
        public long id;
        public string type;
        public Dictionary<string, string> tags;
        public OsmRelationMemberJson[] members;
        public long[] nodes;
        public double lat;
        public double lon;
    }

    class OsmDataJson
    {
        public double version;
        public OsmObjectJson[] elements;
    }


    [ProtoContract]
    enum OsmRelationMemberType
    {
        Node,
        Way,
        Relation
    }

    [ProtoContract]
    class OsmRelationMember
    {
        [ProtoMember(1)]
        public OsmRelationMemberType Type;
        [JsonProperty("ref")]
        [ProtoMember(2)]
        public long Id;
        [ProtoMember(3)]
        public string Role;
    }

    [ProtoContract]
    class OsmRelation
    {
        [ProtoMember(1)]
        public long Id;
        [ProtoMember(2)]
        public Dictionary<string, string> Tags;
        [ProtoMember(3)]
        public OsmRelationMember[] Members;
    }

    [ProtoContract]
    class OsmWay
    {
        [ProtoMember(1)]
        public long Id;
        [ProtoMember(2)]
        public Dictionary<string, string> Tags;
        [ProtoMember(3)]
        public long[] NodeIds;
    }

    [ProtoContract]
    class OsmNode
    {
        [ProtoMember(1)]
        public long Id;
        [ProtoMember(2)]
        public double lat;
        [ProtoMember(3)]
        public double lon;
    }

    [ProtoContract]
    class OsmData
    {
        [ProtoMember(1)]
        public List<OsmRelation> Relations;
        [ProtoMember(2)]
        public List<OsmWay> Ways;
        [ProtoMember(3)]
        public List<OsmNode> Nodes;
    }

    class ResultEntry
    {
        public long RelationId;
        public string WikidataId;
        public string RuArticleName;
        public string Name;
        public double OsmLength;
        public double WdLength;
        public double WdLat;
        public double WdLon;
        public double Error;
    }

    class WdVariable
    {
        public string value;
    }

    class WdItem
    {
        public WdVariable Item;
        public WdVariable ItemLabel;
        public WdVariable Type;
        public WdVariable TypeLabel;
        public WdVariable Mouth;
        public WdVariable MouthLabel;
        public WdVariable Country;
        public WdVariable CountryLabel;
        public WdVariable Lat;
        public WdVariable Lon;
        public WdVariable Length;
        public WdVariable RuWpLink;
    }

    class WdResult
    {
        public WdItem[] bindings;
    }

    class WdData
    {
        public WdResult results;
    }

    class Program
    {
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

        OsmData osmData;


        List<List<long>> SplitToChunks(List<long> ids, int chunkSize)
        {
            int chunkCount = (ids.Count + chunkSize - 1) / chunkSize;

            var chunks = new List<List<long>>();
            for (int i = 0; i < chunkCount; i++)
            {
                var chunk = new List<long>();
                for (int j = 0; j < chunkSize; j++)
                {
                    int k = i * chunkSize + j;
                    if (k >= ids.Count)
                        break;
                    chunk.Add(ids[k]);
                }
                chunks.Add(chunk);
            }

            return chunks;
        }
        void DownloadOsmData()
        {
            osmData = new OsmData();
            osmData.Ways = new List<OsmWay>();
            osmData.Nodes = new List<OsmNode>();

            Console.Write("Downloading relations...");
            string relJson = OverpassApi.Request(
                "[out:json][timeout:300];" +
                "relation[\"type\"=\"waterway\"][\"wikidata\"" + "~\"^Q111\"" + "];" +
                "out body;");
            var relData = JsonConvert.DeserializeObject<OsmDataJson>(relJson);
            osmData.Relations = relData.elements.Select(r => new OsmRelation
            {
                Id = r.id,
                Tags = r.tags,
                Members = r.members == null ? new OsmRelationMember[0] :
                    r.members.Select(m => new OsmRelationMember
                    {
                        Id = m.id,
                        Role = m.role,
                        Type = m.type == "relation" ? OsmRelationMemberType.Relation :
                        (m.type == "way" ? OsmRelationMemberType.Way : OsmRelationMemberType.Node)
                    }).ToArray()
            }).ToList();
            Console.WriteLine(" Done");
            relData = null;

            var reqWayIds = new HashSet<long>();
            foreach (var relation in osmData.Relations)
            {
                reqWayIds.UnionWith(
                    relation.Members.
                    Where(m => m.Type == OsmRelationMemberType.Way &&
                        (m.Role == "main_stream" || m.Role == "")).
                    Select(m => m.Id));
            }

            Console.Write("Downloading ways");
            OverpassApi.Requests(
                SplitToChunks(reqWayIds.ToList(), 2000).Select(chunk =>
                "[out:json][timeout:300];" +
                $"way(id:{string.Join(",", chunk)});" +
                "out body;").ToArray(), wayJson =>
                {
                    var wayData = JsonConvert.DeserializeObject<OsmDataJson>(wayJson);
                    lock (osmData)
                    {
                        osmData.Ways.AddRange(wayData.elements.Select(w => new OsmWay
                        {
                            Id = w.id,
                            Tags = w.tags,
                            NodeIds = w.nodes
                        }).ToArray());
                        Console.Write('.');
                    }
                });
            Console.WriteLine(" Done");
            reqWayIds = null;

            var reqNodeIds = new HashSet<long>();
            foreach (var way in osmData.Ways)
                reqNodeIds.UnionWith(way.NodeIds);

            Console.Write("Downloading nodes");
            OverpassApi.Requests(
                SplitToChunks(reqNodeIds.ToList(), 20000).Select(chunk =>
                "[out:json][timeout:300];" +
                $"node(id:{string.Join(",", chunk)});" +
                "out skel;").ToArray(), nodeJson =>
                {
                    var nodeData = JsonConvert.DeserializeObject<OsmDataJson>(nodeJson);
                    lock (osmData)
                    {
                        osmData.Nodes.AddRange(nodeData.elements.Select(n => new OsmNode
                        {
                            Id = n.id,
                            lat = n.lat,
                            lon = n.lon
                        }).ToArray());
                        Console.Write('.');
                    }
                });
            Console.WriteLine(" Done");
            reqNodeIds = null;

            Stream stream = new FileStream("rivers.bin", FileMode.Create);
            Serializer.Serialize(stream, osmData);
            stream.Close();
        }

        void DownloadWikidata(List<OsmRelation> relations)
        {
            string ids = string.Join(" ", relations.Select(r => "wd:" + r.Tags["wikidata"]));

            string sparql = "query=" + File.ReadAllText("query.txt").
                Replace("__Ids__", ids).Replace("\r\n", " ");

            WebClient wc = new WebClient();
            wc.Headers["Content-Type"] = "application/x-www-form-urlencoded";
            wc.Headers["Accept"] = "application/sparql-results+json";
            string data = Encoding.UTF8.GetString(wc.UploadData(
                "https://query.wikidata.org/sparql", Encoding.UTF8.GetBytes(sparql)));
            File.WriteAllText("wdq.json", data);
        }

        Program()
        {
            DownloadOsmData();

            Stream stream = new FileStream("rivers.bin", FileMode.Open);
            osmData = Serializer.Deserialize<OsmData>(stream);
            stream.Close();

            var relations = osmData.Relations.ToDictionary(r => r.Id);
            var ways = osmData.Ways.ToDictionary(w => w.Id);
            var nodes = osmData.Nodes.ToDictionary(n => n.Id);
            osmData = null;

            DownloadWikidata(relations.Values.ToList());

            string wdJson = File.ReadAllText("wdq.json");
            var wdItems = JsonConvert.DeserializeObject<WdData>(wdJson).results.bindings;
            foreach (var item in wdItems)
                item.Item.value = item.Item.value.Replace("http://www.wikidata.org/entity/", "");

            var results = new List<ResultEntry>();
            foreach (var relation in relations.Values)
            {
                var result = new ResultEntry();
                result.WdLength = -1.0;
                result.Error = -1.0;
                var r = wdItems.Where(i => i.Item.value == relation.Tags["wikidata"]).ToArray();
                if (r.Length != 0)
                {
                    if (r[0].Length != null)
                    {
                        result.WdLength = double.Parse(r[0].Length.value);
                        if (r[0].Lat != null)
                        {
                            result.WdLat = double.Parse(r[0].Lat.value);
                            result.WdLon = double.Parse(r[0].Lon.value);
                        }
                    }
                    if (r[0].RuWpLink != null)
                    {
                        string articleName = r[0].RuWpLink.value;
                        result.RuArticleName = Uri.UnescapeDataString(
                            articleName.Replace("https://ru.wikipedia.org/wiki/", ""));
                    }
                }
                try
                {
                    var riverWays = relation.Members.
                        Where(m => m.Type == OsmRelationMemberType.Way &&
                            (m.Role == "main_stream" || m.Role == "")).
                        Select(m => ways[m.Id]);
                    foreach (var way in riverWays)
                    {
                        var wayNodes = way.NodeIds.Select(n => nodes[n]).ToList();
                        for (int i = 1; i < wayNodes.Count; i++)
                        {
                            result.OsmLength += Distance(
                                wayNodes[i - 1].lat,
                                wayNodes[i - 1].lon,
                                wayNodes[i].lat,
                                wayNodes[i].lon);
                        }
                    }
                    result.Error = result.OsmLength / result.WdLength / 1000.0;
                }
                catch
                {
                    result.OsmLength = -1000.0;
                }

                result.Name = "";
                if (relation.Tags.ContainsKey("name"))
                    result.Name = relation.Tags["name"];
                if (relation.Tags.ContainsKey("name:en"))
                    result.Name = relation.Tags["name:en"];
                if (relation.Tags.ContainsKey("name:uk"))
                    result.Name = relation.Tags["name:uk"];
                if (relation.Tags.ContainsKey("name:ru"))
                    result.Name = relation.Tags["name:ru"];

                result.RelationId = relation.Id;
                result.WikidataId = relation.Tags["wikidata"];
                result.OsmLength /= 1000.0;
                results.Add(result);
            }

            var sb = new StringBuilder();
            var q = results.Where(
                r => r.WdLength != -1.0 &&
                r.OsmLength != -1.0 &&
                r.Error != -1.0
                ).
                OrderByDescending(r => r.Error);
            foreach (var result in q)
            {
                sb.Append($"<a href=\"http://www.openstreetmap.org/relation/{result.RelationId}?mlat={result.WdLat}&mlon={result.WdLon}\">{result.RelationId}</a> ");
                if (result.RuArticleName != null)
                    sb.Append($"<a href=\"https://ru.wikipedia.org/wiki/{result.RuArticleName.Replace(' ', '_')}\">{result.RuArticleName}</a> ");
                sb.Append($"<a href=\"https://www.wikidata.org/wiki/{result.WikidataId}\">{result.WikidataId}</a> ");
                sb.AppendLine($"{result.OsmLength:0.000} {result.WdLength:0.000} {result.Error:0.000} {result.Name}<br/>");
            }
            File.WriteAllText("river_coord_errors.html", sb.ToString());
        }

        long GetMouthNodeId(List<OsmWay> ways)
        {
            if (ways.Count == 0)
                return -1;

            long forwardNodeId = ways.First().NodeIds.Last();
            long backwardNodeId = ways.First().NodeIds.First();
            ways.RemoveAt(0);

            for (;;)
            {
                var way = ways.FirstOrDefault(w => w.NodeIds.First() == forwardNodeId);
                if (way == null)
                    break;
                forwardNodeId = way.NodeIds.Last();
                ways.Remove(way);
            }

            for (;;)
            {
                var way = ways.FirstOrDefault(w => w.NodeIds.Last() == backwardNodeId);
                if (way == null)
                    break;
                backwardNodeId = way.NodeIds.First();
                ways.Remove(way);
            }

            if (ways.Count != 0)
                return -1;

            return forwardNodeId;
        }

        static void Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            new Program();
        }
    }
}

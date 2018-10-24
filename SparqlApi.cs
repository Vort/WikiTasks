using Newtonsoft.Json.Linq;
using System.Net;
using System.Text;
using System.Collections.Generic;

namespace WikiTasks
{
    class SparqlApi : Api
    {
        public static Dictionary<string, string>[] Query(string sparql)
        {
            WebClient wc = new WebClient();
            wc.Headers["Accept-Encoding"] = "gzip";
            wc.Headers["Content-Type"] = "application/x-www-form-urlencoded";
            wc.Headers["Accept"] = "application/sparql-results+json";
            byte[] gzb = wc.UploadData(
                "https://query.wikidata.org/sparql",
                Encoding.UTF8.GetBytes("query=" + sparql.Replace("\r\n", " ")));
            var json = GZipUnpack(gzb);

            var rows = new List<Dictionary<string, string>>();
            var jsonObj = JObject.Parse(json);

            foreach (var jsonRow in jsonObj["results"]["bindings"])
            {
                var row = new Dictionary<string, string>();
                foreach (JProperty jsonColumn in jsonRow)
                {
                    string value = (string)jsonColumn.Value["value"];
                    if ((string)jsonColumn.Value["type"] == "uri")
                        value = value.Replace("http://www.wikidata.org/entity/", "");
                    row[jsonColumn.Name] = value;
                }
                rows.Add(row);
            }
            return rows.ToArray();
        }
    }
}

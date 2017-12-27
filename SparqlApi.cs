using Newtonsoft.Json.Linq;
using System.IO;
using System.Net;
using System.Text;
using System.Linq;
using System;

namespace WikiTasks
{
    class SparqlApi : Api
    {
        public static string RunWikidataQuery(string fileName)
        {
            string sparql = "query=" +
                File.ReadAllText(fileName).Replace("\r\n", " ");

            WebClient wc = new WebClient();
            wc.Headers["Accept-Encoding"] = "gzip";
            wc.Headers["Content-Type"] = "application/x-www-form-urlencoded";
            wc.Headers["Accept"] = "application/sparql-results+json";
            byte[] gzb = wc.UploadData(
                "https://query.wikidata.org/sparql",
                Encoding.UTF8.GetBytes(sparql));
            return GZipUnpack(gzb);
        }

        public static string[] GetItemIds(string json, string column)
        {
            var o = JObject.Parse(json);
            var rl = o["results"]["bindings"].Select(t => t[column]).ToArray();
            if (rl.Any(t => (string)t["type"] != "uri"))
                throw new Exception();
            return rl.Select(t => ((string)t["value"]).
                Replace("http://www.wikidata.org/entity/", "")).ToArray();
        }
    }
}

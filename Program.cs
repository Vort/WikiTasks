using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json.Linq;
using System.IO.Compression;
using System.Text;

namespace WikiTasks
{
    class Program
    {
        MwApi wpApi;

        Regex cyrLatRegex;

        string DownloadPage(string title)
        {
            string xml = wpApi.PostRequest(
                "action", "query",
                "prop", "revisions",
                "rvprop", "content",
                "format", "xml",
                "titles", title);

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            foreach (XmlNode pageNode in doc.SelectNodes("/api/query/pages/page"))
            {
                if (pageNode.Attributes["missing"] != null)
                    continue;
                return pageNode.SelectSingleNode("revisions/rev").InnerText;
            }

            throw new Exception();
        }

        List<string> GetExceptions(string wikitext)
        {
            var exceptions = new List<string>();
            var matches = Regex.Matches(wikitext, " *\\|\\| *\\[\\[([^\\]]+)]] *\\n");
            foreach (Match match in matches)
                exceptions.Add(match.Groups[1].Value.Replace('_', ' '));
            return exceptions;
        }

        Dictionary<int, string> LoadNamespaces()
        {
            var o = JObject.Parse(File.ReadAllText("ruwiki-siteinfo-namespaces.json"));
            return o["query"]["namespaces"].ToDictionary(
                n => (int)((JProperty)n).Value["id"],
                n => (string)((JProperty)n).Value["*"]);
        }

        List<string> GetCyrLat(Dictionary<int, string> namespaces, string filename)
        {
            var cyrLat = new List<string>();

            GZipStream gzs = new GZipStream(
                File.OpenRead(filename), CompressionMode.Decompress);

            var sr = new StreamReader(gzs);
            sr.ReadLine(); // header

            for (;;)
            {
                var line = sr.ReadLine();
                if (line == null)
                    break;
                var spl = line.Split('\t');

                int ns = int.Parse(spl[0]);

                switch (ns)
                {
                    case 1:
                    case 2:
                    case 3:
                    case 4:
                    case 5:
                    case 7:
                    case 11:
                    case 106:
                    case 107:
                        continue;
                }

                string name = spl[1].Replace('_', ' ');
                if (cyrLatRegex.IsMatch(name))
                {
                    if (ns == 0)
                        cyrLat.Add(name);
                    else
                        cyrLat.Add($":{namespaces[ns]}:{name}");
                }
            }
            return cyrLat;
        }

        void ProduceResultFile(List<string> cyrlat, string fileDate)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Данные на {fileDate}.");
            sb.AppendLine();
            sb.AppendLine("{| class=\"sortable\"");
            sb.AppendLine("! Название || Ссылка");
            foreach (var cl in cyrlat)
            {
                sb.AppendLine("|-");
                sb.AppendLine($"| [[{cl}|{{{{кирлат|{cl}}}}}]] || [[{cl}]]");
            }
            sb.AppendLine("|}");
            File.WriteAllText("result.txt", sb.ToString());
        }


        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");

            string cyr = "[а-яА-ЯёЁіІїЇ]";
            string lat = "[a-zA-Z]";
            cyrLatRegex = new Regex($"{cyr}+{lat}+|{lat}+{cyr}+");

            var namespaces = LoadNamespaces();

            var filename = Directory.EnumerateFiles(".", "*all-titles.gz").OrderBy(n => n).Last();
            string fileDate = Regex.Replace(filename,
                ".*(\\d{4})(\\d{2})(\\d{2}).*",
                "{{date|$3|$2|$1|4}}");
            var cyrlat = GetCyrLat(namespaces, filename);
            var exceptions = GetExceptions(
                DownloadPage("Википедия:Кирлат/Проверенные"));
            cyrlat = cyrlat.Except(exceptions).OrderBy(x => x).ToList();
            ProduceResultFile(cyrlat, fileDate);
        }

        static void Main(string[] args)
        {
            new Program();
        }
    }
}
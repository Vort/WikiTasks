using Newtonsoft.Json;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ConsoleApplication62
{
    class CatToRegion
    {
        public string CatLabel;
        public string RegionItem;
        public string RegionLabel;
    }

    class WdVariable
    {
        public string value;
    }

    class WdItem
    {
        public WdVariable Item;
        public WdVariable CatLabel;
        public WdVariable Region;
        public WdVariable RegionLabel;

        public WdVariable river;
        public WdVariable nameRu;
        public WdVariable nameCeb;
        public WdVariable slbCeb;
        public WdVariable slRu;
        public WdVariable ate;
        public WdVariable coord;
    }

    class WdResult
    {
        public WdItem[] bindings;
    }

    class WdData
    {
        public WdResult results;
    }

    class Coord
    {
        public Coord(string coord)
        {
            var match = Regex.Match(coord, "^Point\\(([^ ]+) ([^ ]+)\\)$");
            Lat = double.Parse(match.Groups[2].Value);
            Lon = double.Parse(match.Groups[1].Value);
        }

        public double Lat;
        public double Lon;
    }

    class River
    {
        public string Item;
        public string SitelinkRu;
        public bool SitelinkCeb;
        public string NameRu;
        public string NameRuNorm;
        public string NameRuMod;
        public string NameCeb;
        public string NameCebMod;
        public string AteItem;
        public Coord Coord;
    }


    class Program
    {
        public static string Translit(string str)
        {
            string[] lat_up = { "A", "B", "V", "G", "D", "E", "Ë", "Zh", "Z", "I", "Y", "K", "L", "M", "N", "O", "P", "R", "S", "T", "U", "F", "Kh", "Ts", "Ch", "Sh", "Shch", "”", "Y", "'", "E", "Yu", "Ya" };
            string[] lat_low = { "a", "b", "v", "g", "d", "e", "ë", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "kh", "ts", "ch", "sh", "shch", "”", "y", "'", "e", "yu", "ya" };
            string[] rus_up = { "А", "Б", "В", "Г", "Д", "Е", "Ё", "Ж", "З", "И", "Й", "К", "Л", "М", "Н", "О", "П", "Р", "С", "Т", "У", "Ф", "Х", "Ц", "Ч", "Ш", "Щ", "Ъ", "Ы", "Ь", "Э", "Ю", "Я" };
            string[] rus_low = { "а", "б", "в", "г", "д", "е", "ё", "ж", "з", "и", "й", "к", "л", "м", "н", "о", "п", "р", "с", "т", "у", "ф", "х", "ц", "ч", "ш", "щ", "ъ", "ы", "ь", "э", "ю", "я" };
            for (int i = 0; i <= 32; i++)
            {
                str = str.Replace(rus_up[i], lat_up[i]);
                str = str.Replace(rus_low[i], lat_low[i]);
            }
            return str;
        }

        string UnpackGZip(byte[] data)
        {
            GZipStream gzs = new GZipStream(
                new MemoryStream(data), CompressionMode.Decompress);
            MemoryStream ms = new MemoryStream();
            gzs.CopyTo(ms);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        string RunWikidataQuery(string fileName)
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
            return UnpackGZip(gzb);
        }

        string[] GetPetScanList(string category, int depth)
        {
            string template = File.ReadAllText("petscan_request.txt");
            string postRequest = template.
                Replace("{{cat}}", Uri.EscapeDataString(category)).
                Replace("{{depth}}", depth.ToString());

            WebClient wc = new WebClient();
            wc.Headers["Content-Type"] = "application/x-www-form-urlencoded";
            string json = Encoding.UTF8.GetString(wc.UploadData(
                "https://petscan.wmflabs.org/", Encoding.UTF8.GetBytes(postRequest)));
            var matches = Regex.Matches(json, "\"title\":\"(Q[0-9]+)\"");
            return matches.Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
        }

        Program()
        {
            if (!File.Exists("cats_wd.json"))
                File.WriteAllText("cats_wd.json", RunWikidataQuery("cats_query.txt"));
            if (!File.Exists("rivers_wd.json"))
                File.WriteAllText("rivers_wd.json", RunWikidataQuery("rivers_query.txt"));

            var wdJson = File.ReadAllText("cats_wd.json");
            var rawItems = JsonConvert.DeserializeObject<WdData>(wdJson).results.bindings;
            CatToRegion[] catToRegionItems = rawItems.Select(
                ri => new CatToRegion
                {
                    CatLabel = ri.CatLabel.value.Replace("Категория:", ""),
                    RegionItem = ri.Region.value.Replace("http://www.wikidata.org/entity/", ""),
                    RegionLabel = ri.RegionLabel.value
                }).ToArray();

            foreach (var catToReg in catToRegionItems)
            {
                string fileName = "rivers_wp_" + catToReg.RegionItem + ".txt";
                if (!File.Exists(fileName))
                    File.WriteAllLines(fileName, GetPetScanList(catToReg.CatLabel, 1));
            }

            wdJson = File.ReadAllText("rivers_wd.json");
            rawItems = JsonConvert.DeserializeObject<WdData>(wdJson).results.bindings;

            River[] rivers = rawItems.Select(
                ri => new River
                {
                    Item = ri.river.value.Replace("http://www.wikidata.org/entity/", ""),
                    AteItem = ri.ate == null ? null : ri.ate.value.Replace("http://www.wikidata.org/entity/", ""),
                    Coord = ri.coord == null ? null : new Coord(ri.coord.value),
                    NameRu = ri.nameRu == null ? null : ri.nameRu.value,
                    NameCeb = ri.nameCeb == null ? null : ri.nameCeb.value,
                    SitelinkRu = ri.slRu == null ? null : ri.slRu.value,
                    SitelinkCeb = ri.slbCeb != null
                }).ToArray();

            foreach (var river in rivers)
            {
                if (river.NameRu != null)
                {
                    if (river.NameRu.Contains("("))
                        river.NameRuNorm = Regex.Replace(river.NameRu, " \\([^)]+\\)$", "");
                    else
                        river.NameRuNorm = river.NameRu;
                    river.NameRuMod = Translit(river.NameRuNorm.ToLower().Replace("-", ""));
                }
                if (river.NameCeb != null)
                {
                    river.NameCebMod = river.NameCeb.ToLower().Replace("-", "");
                }
            }

            int i = 0;
            var sb = new StringBuilder();
            foreach (var catToReg in catToRegionItems.OrderBy(ctr => ctr.CatLabel))
            {
                Console.Write('.');
                string fileName = "rivers_wp_" + catToReg.RegionItem + ".txt";
                string[] riversWpCat = File.ReadAllLines(fileName);

                foreach (var wpWdRiver in riversWpCat)
                    foreach (var river in rivers.Where(r => r.Item == wpWdRiver && r.AteItem == null))
                        river.AteItem = catToReg.RegionItem;

                var regionRivers = rivers.Where(r => r.AteItem == catToReg.RegionItem).ToArray();

                var regionRiversRu = regionRivers.Where(
                    r => r.SitelinkRu != null && !r.SitelinkCeb && r.NameRu != null).OrderBy(r => r.NameRu).ToArray();
                var regionRiversCeb = regionRivers.Where(
                    r => r.SitelinkRu == null && r.SitelinkCeb && r.NameCeb != null).ToArray();

                sb.AppendLine("''" + catToReg.RegionLabel + ":''");

                sb.AppendLine("{|class=\"standard\"");
                sb.AppendLine("!" + "ruwd");
                sb.AppendLine("!" + "cebwd");
                sb.AppendLine("!" + "ruarticle");
                sb.AppendLine("!" + "cebcoord");
                sb.AppendLine("|-");
                foreach (var ruRiver in regionRiversRu.Where(r => r.Coord == null))
                {
                    if (regionRiversRu.Count(r => r.NameRuMod == ruRiver.NameRuMod) != 1)
                        continue;
                    if (regionRiversCeb.Count(r => r.NameCebMod == ruRiver.NameRuMod) != 1)
                        continue;
                    var cebRiver = regionRiversCeb.First(r => r.NameCebMod == ruRiver.NameRuMod);
                    if (cebRiver.Coord == null)
                        continue;
                    sb.AppendLine("|[[:d:" + ruRiver.Item + "|" + ruRiver.NameRu + "]]");
                    sb.AppendLine("|[[:d:" + cebRiver.Item + "|" + cebRiver.NameCeb + "]]");
                    sb.AppendLine("|[[" + ruRiver.SitelinkRu + "]]");
                    sb.AppendLine("|{{coord|" + cebRiver.Coord.Lat + "|" + cebRiver.Coord.Lon + "}}");
                    sb.AppendLine("|-");
                    i++;
                }
                sb.AppendLine("|}");
                sb.AppendLine("<br/>");
            }
            File.WriteAllText("result.txt", sb.ToString());
            Console.WriteLine();
            Console.WriteLine("Count: " + i);
        }

        static void Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            new Program();
        }
    }
}

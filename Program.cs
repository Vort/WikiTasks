using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WikiTasks
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
        public WdVariable slCeb;
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
        public string SitelinkCeb;
        public string NameRu;
        public string NameRuMod;
        public string NameCeb;
        public string NameCebMod;
        public HashSet<string> AteItems;
        public Coord Coord;
    }

    class DupEntry
    {
        public string SortName;
        public string RuWd;
        public string CebWd;
        public string RuArticle;
        public string CebCoord;
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
            Console.Write("Retrieving data...");
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
                {
                    Console.Write('.');
                    File.WriteAllLines(fileName, GetPetScanList(catToReg.CatLabel, 1));
                }
            }

            wdJson = File.ReadAllText("rivers_wd.json");
            rawItems = JsonConvert.DeserializeObject<WdData>(wdJson).results.bindings;
            Console.WriteLine(" Done");

            Console.Write("Processing");
            River[] rivers = rawItems.GroupBy(ri => ri.river.value).Select(
                g => new River
                {
                    Item = g.First().river.value.Replace("http://www.wikidata.org/entity/", ""),
                    AteItems = new HashSet<string>(g.Where(ri => ri.ate != null).
                        Select(ri => ri.ate.value.Replace("http://www.wikidata.org/entity/", ""))),
                    Coord = g.First().coord == null ? null : new Coord(g.First().coord.value),
                    NameRu = g.First().nameRu == null ? null : g.First().nameRu.value,
                    NameCeb = g.First().nameCeb == null ? null : g.First().nameCeb.value,
                    SitelinkRu = g.First().slRu == null ? null : g.First().slRu.value,
                    SitelinkCeb = g.First().slCeb == null ? null : g.First().slCeb.value,
                }).ToArray();

            foreach (var river in rivers)
            {
                river.NameRuMod = river.NameRu;
                river.NameCebMod = river.NameCeb;
                if (river.NameRuMod == null)
                    river.NameRuMod = river.SitelinkRu;
                if (river.NameCebMod == null)
                    river.NameCebMod = river.SitelinkCeb;
                if (river.NameRuMod != null)
                {
                    if (river.NameRuMod.Contains("("))
                        river.NameRuMod = Regex.Replace(river.NameRuMod, " \\([^)]+\\)$", "");
                    river.NameRuMod = Translit(river.NameRuMod.ToLower().Replace("-", ""));
                }
                if (river.NameCebMod != null)
                {
                    if (river.NameCebMod.Contains("("))
                        river.NameCebMod = Regex.Replace(river.NameCebMod, " \\([^)]+\\)$", "");
                    river.NameCebMod = river.NameCebMod.ToLower().Replace("-", "");
                }
            }

            var riversDic = rivers.ToDictionary(r => r.Item, r => r);
            foreach (var catToReg in catToRegionItems)
            {
                string fileName = "rivers_wp_" + catToReg.RegionItem + ".txt";
                string[] riversWpCat = File.ReadAllLines(fileName);
                foreach (var wpWdRiver in riversWpCat)
                    if (riversDic.ContainsKey(wpWdRiver))
                        riversDic[wpWdRiver].AteItems.Add(catToReg.RegionItem);
            }

            var result = catToRegionItems.ToDictionary(
                c => c.RegionItem, c => new List<DupEntry>());
            var onlyRuRivers = rivers.Where(
                r => r.SitelinkRu != null && r.SitelinkCeb == null && r.NameRuMod != null);
            var onlyCebRivers = rivers.Where(
                r => r.SitelinkRu == null && r.SitelinkCeb != null && r.NameCebMod != null);
            var onlyRuNoCoordRivers = onlyRuRivers.Where(r => r.Coord == null).ToArray();

            int i = 0;
            Parallel.ForEach(onlyRuNoCoordRivers, ruRiver =>
            {
                if (Interlocked.Increment(ref i) % 100 == 0)
                    Console.Write('.');

                if (onlyRuRivers.Count(r =>
                    r.AteItems.Intersect(ruRiver.AteItems).Count() != 0 &&
                    r.NameRuMod == ruRiver.NameRuMod) != 1)
                {
                    return;
                }
                if (onlyCebRivers.Count(r =>
                    r.AteItems.Intersect(ruRiver.AteItems).Count() != 0 &&
                    r.NameCebMod == ruRiver.NameRuMod) != 1)
                {
                    return;
                }
                var cebRiver = onlyCebRivers.First(r =>
                    r.AteItems.Intersect(ruRiver.AteItems).Count() != 0 &&
                    r.NameCebMod == ruRiver.NameRuMod);
                if (cebRiver.Coord == null)
                    return;

                string regionItem = cebRiver.AteItems.Intersect(ruRiver.AteItems).First();

                lock (result)
                {
                    result[regionItem].Add(new DupEntry()
                    {
                        SortName = ruRiver.NameRu,
                        RuWd = "[[:d:" + ruRiver.Item + "|" + ruRiver.NameRu + "]]",
                        CebWd = "[[:d:" + cebRiver.Item + "|" + cebRiver.NameCeb + "]]",
                        RuArticle = "[[" + ruRiver.SitelinkRu + "]]",
                        CebCoord = "{{coord|" + cebRiver.Coord.Lat + "|" + cebRiver.Coord.Lon + "}}"
                    });
                }
            });
            Console.WriteLine(" Done");

            var sb = new StringBuilder();
            foreach (var catToReg in catToRegionItems.OrderBy(ctr => ctr.CatLabel))
            {
                var dupEntries = result[catToReg.RegionItem].OrderBy(de => de.SortName).ToArray();
                sb.AppendLine("''" + catToReg.RegionLabel + ":''");

                sb.AppendLine("{|class=\"standard\"");
                sb.AppendLine("!" + "№");
                sb.AppendLine("!" + "ruwd");
                sb.AppendLine("!" + "cebwd");
                sb.AppendLine("!" + "ruarticle");
                sb.AppendLine("!" + "cebcoord");
                sb.AppendLine("|-");

                for (i = 0; i < dupEntries.Length; i++)
                {
                    sb.AppendLine("|" + (i + 1));
                    sb.AppendLine("|" + dupEntries[i].RuWd);
                    sb.AppendLine("|" + dupEntries[i].CebWd);
                    sb.AppendLine("|" + dupEntries[i].RuArticle);
                    sb.AppendLine("|" + dupEntries[i].CebCoord);
                    sb.AppendLine("|-");
                }

                sb.AppendLine("|}");
                sb.AppendLine("<br/>");
            }

            sb.AppendLine("'''Всего:''' " + result.Sum(r => r.Value.Count));
            File.WriteAllText("result.txt", sb.ToString());
        }

        static void Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            new Program();
        }
    }
}

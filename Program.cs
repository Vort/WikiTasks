using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
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

            throw new Exception($"Page '{title}' not found.");
        }

        string ObtainEditToken()
        {
            Console.Write("Authenticating...");
            string csrfToken = wpApi.GetToken("csrf");
            if (csrfToken == null)
            {
                Console.WriteLine(" Failed");
                throw new Exception();
            }
            Console.WriteLine(" Done");
            return csrfToken;
        }

        bool EditPage(string csrfToken, string title, string summary, string text)
        {
            string xml = wpApi.PostRequest(
                "format", "xml",
                "action", "edit",
                "bot", "true",
                "title", title,
                "summary", summary,
                "text", text,
                "token", csrfToken);

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            if (doc.SelectNodes("/api/error").Count == 1)
                return false;
            return doc.SelectSingleNode("/api/edit").Attributes["result"].InnerText == "Success";
        }

        List<string> ParseTable(string wikitext)
        {
            var exceptions = new List<string>();
            var matches = Regex.Matches(wikitext, " *\\|\\| *\\[\\[([^\\]]+)]] *\\n");
            foreach (Match match in matches)
                exceptions.Add(match.Groups[1].Value.Replace('_', ' '));
            return exceptions;
        }

        string ProduceResultTable(List<string> cyrlat)
        {
            var date = DateTime.UtcNow.ToString("{{\\da\\te|dd|MM|yyyy|4}}");

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"Данные на {date}.");
            sb.AppendLine();
            sb.AppendLine("{| class=\"sortable\"");
            sb.AppendLine("! Название || Ссылка");
            foreach (var cl in cyrlat)
            {
                sb.AppendLine("|-");
                sb.AppendLine($"| [[{cl}|{{{{кирлат|{cl}}}}}]] || [[{cl}]]");
            }
            sb.AppendLine("|}");
            return sb.ToString();
        }

        void GetCyrLat(int ns, List<string> cyrlat)
        {
            string apcontinue = null;
            string continueParam = null;
            for (;;)
            {
                string xml = wpApi.PostRequest(
                    "format", "xml",
                    "action", "query",
                    "list", "allpages",
                    "apnamespace", ns,
                    "aplimit", 5000,
                    "apcontinue", apcontinue,
                    "continue", continueParam);
                Console.Write('.');

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                var errorNode = doc.SelectSingleNode("/api/error");
                if (errorNode != null)
                    throw new Exception(errorNode.OuterXml);

                foreach (XmlNode pNode in doc.SelectNodes("/api/query/allpages/p"))
                {
                    string title = pNode.Attributes["title"].Value;
                    if (cyrLatRegex.IsMatch(title))
                    {
                        if (ns == 0)
                            cyrlat.Add(title);
                        else
                            cyrlat.Add($":{title}");
                    }
                }

                XmlNode contNode = doc.SelectSingleNode("/api/continue");
                if (contNode == null)
                    break;
                apcontinue = contNode.Attributes["apcontinue"].Value;
                continueParam = contNode.Attributes["continue"].Value;
            }
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");

            string csrfToken = ObtainEditToken();

            string cyr = "[Ѐ-Яа-џ]";
            string lat = "[A-Za-zÀ-ÖØ-Þß-öù-ÿ]";
            cyrLatRegex = new Regex($"{cyr}{lat}|{lat}{cyr}");

            var cyrlat = new List<string>();

            Console.Write("Scanning page titles");
            foreach (int ns in new int[] {
                0,   // (Основное)
                6,   // Файл
                10,  // Шаблон
                14,  // Категория
                100, // Портал
                102, // Инкубатор
                104, // Проект
                828, // Модуль
                2300 // Гаджет
            })
            {
                GetCyrLat(ns, cyrlat);
            }
            Console.WriteLine(" Done");

            string exceptionsTableName = "Википедия:Кирлат/Проверенные";
            string resultTableName = "Википедия:Кирлат/Подозрительные";

            Console.Write("Downloading exceptions...");
            var exceptions = ParseTable(DownloadPage(exceptionsTableName));
            Console.WriteLine(" Done");
            cyrlat = cyrlat.Except(exceptions).OrderBy(x => x).ToList();

            Console.Write("Downloading old table...");
            var wikitextOld = DownloadPage(resultTableName);
            var cyrlatOld = ParseTable(wikitextOld);
            Console.WriteLine(" Done");

            if (cyrlatOld.Except(cyrlat).Any())
            {
                string marker = "<!-- Маркер вставки. Не трогайте эту строку. -->";
                int markerIndex = wikitextOld.IndexOf(marker);
                if (markerIndex == -1)
                {
                    Console.WriteLine("Insertion marker not found!");
                    return;
                }
                int insertIndex = markerIndex + marker.Length;
                string newWikitext = 
                    wikitextOld.Substring(0, insertIndex) +
                    ProduceResultTable(cyrlat);
                Console.Write("Updating table...");
                var r = EditPage(csrfToken, resultTableName, "обновление данных", newWikitext);
                Console.WriteLine(r ? " Done" : " Failed");
            }
            else
                Console.WriteLine("No changes are needed.");
        }

        static void Main(string[] args)
        {
            new Program();
        }
    }
}
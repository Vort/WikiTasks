using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Xml;

namespace WikiTasks
{
    class CategoryScanResult
    {
        public int PageId;
        public bool Flagged;
    }

    class Program
    {
        MwApi wpApi;
        string csrfToken;

        List<List<T>> SplitToChunks<T>(T[] elements, int chunkSize)
        {
            int chunkCount = (elements.Length + chunkSize - 1) / chunkSize;

            var chunks = new List<List<T>>();
            for (int i = 0; i < chunkCount; i++)
            {
                var chunk = new List<T>();
                for (int j = 0; j < chunkSize; j++)
                {
                    int k = i * chunkSize + j;
                    if (k >= elements.Length)
                        break;
                    chunk.Add(elements[k]);
                }
                chunks.Add(chunk);
            }

            return chunks;
        }

        void ObtainEditToken()
        {
            Console.Write("Authenticating...");
            csrfToken = wpApi.GetToken("csrf");
            if (csrfToken == null)
            {
                Console.WriteLine(" Failed");
                throw new Exception();
            }
            Console.WriteLine(" Done");
        }

        string DownloadArticle(string title)
        {
            string xml = wpApi.PostRequest(
                "action", "query",
                "prop", "revisions",
                "rvprop", "content",
                "format", "xml",
                "titles", title);

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            return doc.SelectSingleNode("/api/query/pages/page/revisions/rev").InnerText;
        }

        List<int> SearchArticles(string query, string ns = "0")
        {
            var idList = new List<int>();

            string sroffset = "";
            for (;;)
            {
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "list", "search",
                    "srwhat", "text",
                    "srsearch", query,
                    "srprop", "",
                    "srinfo", "",
                    "srlimit", "100",
                    "sroffset", sroffset,
                    "srnamespace", ns,
                    "format", "xml");
                Console.Write('.');

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                foreach (XmlNode pNode in doc.SelectNodes("/api/query/search/p"))
                {
                    int id = int.Parse(pNode.Attributes["pageid"].InnerText);
                    idList.Add(id);
                }

                XmlNode contNode = doc.SelectSingleNode("/api/continue");
                if (contNode == null)
                    break;
                sroffset = contNode.Attributes["sroffset"].InnerText;
            }

            return idList;
        }

        int[] ScanCategory(string category)
        {
            return ScanCategoryG(category).Select(c => c.PageId).ToArray();
        }

        List<CategoryScanResult> ScanCategoryG(string category)
        {
            var idList = new List<CategoryScanResult>();

            string continueParam = "";
            for (;;)
            {
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "generator", "categorymembers",
                    "prop", "flagged",
                    "gcmprop", "ids",
                    "gcmtype", "page",
                    "gcmtitle", category,
                    "gcmlimit", "500",
                    "gcmcontinue", continueParam,
                    "format", "xml");
                Console.Write('.');

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                foreach (XmlNode node in doc.SelectNodes("/api/query/pages/page"))
                {
                    int id = int.Parse(node.Attributes["pageid"].InnerText);
                    bool flagged = node.SelectNodes("flagged").Count != 0;
                    idList.Add(new CategoryScanResult { PageId = id, Flagged = flagged });
                }

                XmlNode contNode = doc.SelectSingleNode("/api/continue");
                if (contNode == null)
                    break;
                continueParam = contNode.Attributes["gcmcontinue"].InnerText;
            }

            return idList;
        }

        List<string> GetSubCategories(string category)
        {
            // Функция работает без цикла (делает только один запрос)

            var result = new List<string>();

            string xml = wpApi.PostRequest(
                "action", "query",
                "list", "categorymembers",
                "cmtitle", category,
                "cmtype", "subcat",
                "cmlimit", "5000",
                "format", "xml");

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            foreach (XmlNode node in doc.SelectNodes("/api/query/categorymembers/cm"))
                result.Add(node.Attributes["title"].InnerText);
            if (result.Count == 0)
                throw new Exception();
            return result;
        }

        int GetCategoryPageCount(string category)
        {
            string xml = wpApi.PostRequest(
                "action", "query",
                "prop", "categoryinfo",
                "titles", category,
                "format", "xml");

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            XmlNode ciNode = doc.SelectSingleNode("/api/query/pages/page/categoryinfo");
            if (ciNode == null)
                throw new Exception();
            return int.Parse(ciNode.Attributes["pages"].InnerText);
        }


        bool EditPage(string csrfToken, string title, string summary, string text)
        {
            string xml = wpApi.PostRequest(
                "action", "edit",
                "format", "xml",
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

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");

            string mainCategoryName = "Водные объекты по алфавиту";

            Console.Write("Obtaining category list...");
            int totalCount = GetCategoryPageCount($"Категория:{mainCategoryName}");
            var noRsTypeCats = GetSubCategories("Категория:Википедия:Статьи без источников по типам").
                Select(s => s.Replace("Категория:", "")).ToArray();
            Console.WriteLine(" Done");

            var wocat = $"incategory:\"{mainCategoryName}\"";
            Console.Write("Scanning categories");
            var idsNoPat = ScanCategoryG($"Категория:{mainCategoryName}").
                Where(c => !c.Flagged).Select(c => c.PageId).ToArray();
            Console.WriteLine();
            var idsNoRs = new HashSet<int>();
            foreach (var cat in noRsTypeCats)
                idsNoRs.UnionWith(SearchArticles($"{wocat} incategory:\"{cat}\""));
            Console.WriteLine();
            var idsSmallSize = ScanCategory("Категория:ПРО:ВО:Размер статьи: менее 600 символов").ToList();
            idsSmallSize.AddRange(ScanCategory("Категория:ПРО:ВО:Размер статьи: менее 400 символов"));
            Console.WriteLine();
            var idsNotChecked = SearchArticles($"{wocat} hastemplate:\"Непроверенная река\"");
            Console.WriteLine();
            var idsToImprove = SearchArticles($"{wocat} incategory:\"Википедия:Статьи для срочного улучшения\"");
            Console.WriteLine();
            var idsNoRefs = SearchArticles($"{wocat} incategory:\"Википедия:Статьи без сносок\"");
            Console.WriteLine();
            var idsNoCoords = SearchArticles($"{wocat} deepcat:\"Карточка реки: заполнить: Координаты устья\"");
            Console.WriteLine(" Done");

            var problemIds = new HashSet<int>(idsNoRs);
            problemIds.UnionWith(idsNoPat);
            problemIds.UnionWith(idsSmallSize);
            problemIds.UnionWith(idsNotChecked);
            problemIds.UnionWith(idsToImprove);
            problemIds.UnionWith(idsNoRefs);
            problemIds.UnionWith(idsNoCoords);

            double problemIdsPerc = problemIds.Count * 100.0 / totalCount;
            double idsNoPatPerc = idsNoPat.Length * 100.0 / totalCount;
            double idsNoRsPerc = idsNoRs.Count * 100.0 / totalCount;
            double idsSmallSizePerc = idsSmallSize.Count * 100.0 / totalCount;
            double idsNotCheckedPerc = idsNotChecked.Count * 100.0 / totalCount;
            double idsToImprovePerc = idsToImprove.Count * 100.0 / totalCount;
            double idsNoRefsPerc = idsNoRefs.Count * 100.0 / totalCount;
            double idsNoCoordsPerc = idsNoCoords.Count * 100.0 / totalCount;

            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var date = DateTime.Now.ToString("d MMMM yyyy");

            var tableLine = $"|-\n| {date} || " +
                $"{totalCount} || " +
                $"{idsNotChecked.Count} || {idsNotCheckedPerc:0.0} || " +
                $"{idsToImprove.Count} || {idsToImprovePerc:0.0} || " +
                $"{idsNoRs.Count} || {idsNoRsPerc:0.0} || " +
                $"{idsNoRefs.Count} || {idsNoRefsPerc:0.0} || " +
                $"{idsSmallSize.Count} || {idsSmallSizePerc:0.0} || " +
                $"{idsNoCoords.Count} || {idsNoCoordsPerc:0.0} || " +
                $"{idsNoPat.Length} || {idsNoPatPerc:0.0} || " +
                $"{problemIds.Count} || {problemIdsPerc:0.0}\n";

            ObtainEditToken();

            Console.Write("Updating table...");
            string pageName = "Проект:Водные объекты/Статистика/Проблемные статьи";
            string wikiText = DownloadArticle(pageName);

            string replWikiText =
                wikiText.Substring(0, wikiText.LastIndexOf("|}")) + tableLine +
                wikiText.Substring(wikiText.LastIndexOf("|}"));
            bool isEditSuccessful = EditPage(csrfToken,
                pageName, "обновление данных", replWikiText);

            if (isEditSuccessful)
                Console.WriteLine(" Done");
            else
                Console.WriteLine(" Failed");
        }

        static void Main(string[] args)
        {
            new Program();
        }
    }
}
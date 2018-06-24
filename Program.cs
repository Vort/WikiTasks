using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Xml;

namespace WikiTasks
{
    class Article
    {
        public int PageId;
        public bool Flagged;
        public List<string> Categories;
        public List<string> Templates;
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

        Article[] ScanCategoryA(string category,
            string[] includeCategories,
            string[] includeTemplates)
        {
            var articles = new Dictionary<int, Article>();

            bool firstCatChunk = true;
            foreach (var catChunk in SplitToChunks(includeCategories, 500))
            {
                string continueQuery = null;
                string continueGcm = null;
                string continueCl = null;
                string continueTl = null;
                for (;;)
                {
                    string xml = wpApi.PostRequest(
                        "action", "query",
                        "generator", "categorymembers",
                        "prop", firstCatChunk ? "flagged|categories|templates" : "categories",
                        "clcategories", string.Join("|", catChunk),
                        "cllimit", "5000",
                        "clcontinue", continueCl,
                        "tltemplates", firstCatChunk ? string.Join("|", includeTemplates) : null,
                        "tlcontinue", continueTl,
                        "tllimit", "5000",
                        "gcmlimit", "5000",
                        "gcmprop", "ids",
                        "gcmtype", "page",
                        "gcmtitle", category,
                        "gcmcontinue", continueGcm,
                        "continue", continueQuery,
                        "format", "xml");
                    Console.Write('.');

                    XmlDocument doc = new XmlDocument();
                    doc.LoadXml(xml);

                    foreach (XmlNode node in doc.SelectNodes("/api/query/pages/page"))
                    {
                        Article article = null;
                        int pageId = int.Parse(node.Attributes["pageid"].Value);
                        if (!articles.ContainsKey(pageId))
                        {
                            article = new Article
                            {
                                PageId = pageId,
                                Categories = new List<string>(),
                                Templates = new List<string>()
                            };
                            articles.Add(pageId, article);
                        }
                        else
                            article = articles[pageId];
                        if (firstCatChunk)
                            article.Flagged = node.SelectNodes("flagged").Count != 0;
                        foreach (XmlNode catNode in node.SelectNodes("categories/cl"))
                            article.Categories.Add(catNode.Attributes["title"].Value);
                        foreach (XmlNode tmpNode in node.SelectNodes("templates/tl"))
                            article.Templates.Add(tmpNode.Attributes["title"].Value);
                    }

                    XmlNode contNode = doc.SelectSingleNode("/api/continue");
                    if (contNode == null)
                        break;
                    var continueQueryAttr = contNode.Attributes["continue"];
                    var continueGcmAttr = contNode.Attributes["gcmcontinue"];
                    var continueClAttr = contNode.Attributes["clcontinue"];
                    var continueTlAttr = contNode.Attributes["tlcontinue"];
                    continueQuery = continueQueryAttr == null ? null : continueQueryAttr.Value;
                    continueGcm = continueGcmAttr == null ? null : continueGcmAttr.Value;
                    continueCl = continueClAttr == null ? null : continueClAttr.Value;
                    continueTl = continueTlAttr == null ? null : continueTlAttr.Value;
                }
                firstCatChunk = false;
            }

            return articles.Values.ToArray();
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");

            Console.Write("Obtaining category list...");
            var noRsTypeCats = GetSubCategories(
                "Категория:Википедия:Статьи без источников по типам");
            Console.WriteLine(" Done");

            string catSmall400 = "Категория:ПРО:ВО:Размер статьи: менее 400 символов";
            string catSmall600 = "Категория:ПРО:ВО:Размер статьи: менее 600 символов";
            string catNoCoords = "Категория:Карточка реки: заполнить: Координаты устья";
            string catNoCoords10 = "Категория:Карточка реки: заполнить: Координаты устья реки свыше десяти км";
            string catNoCoords50 = "Категория:Карточка реки: заполнить: Координаты устья реки свыше пятидесяти км";
            string catNoCoords100 = "Категория:Карточка реки: заполнить: Координаты устья реки свыше ста км";
            string catToImprove = "Категория:Википедия:Статьи для срочного улучшения";
            string catNoRefs = "Категория:Википедия:Статьи без сносок";
            string tmplNotChecked = "Шаблон:Непроверенная река";
            var catSmallList = new string[] { catSmall400, catSmall600 };
            var catNoCoordsList = new string[] { catNoCoords, catNoCoords10, catNoCoords50, catNoCoords100 };

            Console.Write("Scanning categories");
            var articles = ScanCategoryA(
                "Категория:Водные объекты по алфавиту",
                noRsTypeCats.Concat(catSmallList).Concat(catNoCoordsList).Concat(
                new string[] { catToImprove, catNoRefs }).ToArray(),
                new string[] { tmplNotChecked });
            Console.WriteLine(" Done");

            int totalCount = articles.Length;
            var artsNoPat = articles.Where(a => !a.Flagged).ToArray();
            var artsNoRs = articles.Where(a => a.Categories.Intersect(noRsTypeCats).Any()).ToArray();
            var artsSmallSize = articles.Where(a => a.Categories.Intersect(catSmallList).Any()).ToArray();
            var artsNotChecked = articles.Where(a => a.Templates.Contains(tmplNotChecked)).ToArray();
            var artsToImprove = articles.Where(a => a.Categories.Contains(catToImprove)).ToArray();
            var artsNoRefs = articles.Where(a => a.Categories.Contains(catNoRefs)).ToArray();
            var artsNoCoords = articles.Where(a => a.Categories.Intersect(catNoCoordsList).Any()).ToArray();

            var problemArts = new HashSet<Article>();
            problemArts.UnionWith(artsNoPat);
            problemArts.UnionWith(artsNoRs);
            problemArts.UnionWith(artsSmallSize);
            problemArts.UnionWith(artsNotChecked);
            problemArts.UnionWith(artsToImprove);
            problemArts.UnionWith(artsNoRefs);
            problemArts.UnionWith(artsNoCoords);

            double problemIdsPerc = problemArts.Count * 100.0 / totalCount;
            double artsNoPatPerc = artsNoPat.Length * 100.0 / totalCount;
            double artsNoRsPerc = artsNoRs.Length * 100.0 / totalCount;
            double artsSmallSizePerc = artsSmallSize.Length * 100.0 / totalCount;
            double artsNotCheckedPerc = artsNotChecked.Length * 100.0 / totalCount;
            double artsToImprovePerc = artsToImprove.Length * 100.0 / totalCount;
            double artsNoRefsPerc = artsNoRefs.Length * 100.0 / totalCount;
            double artsNoCoordsPerc = artsNoCoords.Length * 100.0 / totalCount;

            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var date = DateTime.Now.ToString("d MMMM yyyy");

            var tableLine = $"|-\n| {date} || " +
                $"{totalCount} || " +
                $"{artsNotChecked.Length} || {artsNotCheckedPerc:0.0} || " +
                $"{artsToImprove.Length} || {artsToImprovePerc:0.0} || " +
                $"{artsNoRs.Length} || {artsNoRsPerc:0.0} || " +
                $"{artsNoRefs.Length} || {artsNoRefsPerc:0.0} || " +
                $"{artsSmallSize.Length} || {artsSmallSizePerc:0.0} || " +
                $"{artsNoCoords.Length} || {artsNoCoordsPerc:0.0} || " +
                $"{artsNoPat.Length} || {artsNoPatPerc:0.0} || " +
                $"{problemArts.Count} || {problemIdsPerc:0.0}\n";

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
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
        public bool Unreviewed;
        public bool OldReviewed;
        public List<string> Categories;
        public List<string> Templates;
    }

    class Program
    {
        MwApi wpApi;
        string csrfToken;

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

            string continueQuery = null;
            string continueGcm = null;
            string continueCl = null;
            string continueTl = null;
            for (;;)
            {
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "generator", "categorymembers",
                    "prop", "flagged|categories|templates",
                    "clcategories", string.Join("|", includeCategories),
                    "cllimit", "5000",
                    "clcontinue", continueCl,
                    "tltemplates", string.Join("|", includeTemplates),
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
                        var flNode = node.SelectSingleNode("flagged");
                        if (flNode == null)
                            article.Unreviewed = true;
                        else if (flNode.Attributes["pending_since"] != null)
                            article.OldReviewed = true;
                        articles.Add(pageId, article);
                    }
                    else
                        article = articles[pageId];
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

            return articles.Values.ToArray();
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");

            string catToImprove = "Категория:Википедия:Статьи для срочного улучшения";
            string catToDel = "Категория:Википедия:Кандидаты на удаление";
            string catToSpeedyDel = "Категория:Википедия:К быстрому удалению";
            string catToRename = "Категория:Википедия:Статьи для переименования";
            string catToMerge = "Категория:Википедия:Кандидаты на объединение";
            string catToMove = "Категория:Википедия:Статьи к перемещению";
            string catToSplit = "Категория:Википедия:Статьи для разделения";
            string catNoArchives = "Категория:Википедия:Cite web (недоступные ссылки без архивной копии)";
            string catWebcitation = "Категория:Википедия:Cite web (заменить webcitation-архив: deadlink yes)";
            string catNoRefs = "Категория:Википедия:Статьи без сносок";
            string catSmall700 = "Категория:ПРО:ВО:Размер статьи: менее 700 символов";
            string catSmall800 = "Категория:ПРО:ВО:Размер статьи: менее 800 символов";
            string catNoGeoCoords = "Категория:Википедия:Водные объекты без указанных географических координат";
            string catNoSourceCoords = "Категория:Карточка реки: заполнить: Координаты истока";
            string catNoMouthCoords = "Категория:Карточка реки: заполнить: Координаты устья";
            string catProblems = "Категория:Википедия:Статьи с шаблонами недостатков по алфавиту";
            string tmplNoRs = "Шаблон:Сортировка: статьи без источников";
            string tmplDeadLink = "Шаблон:Недоступная ссылка";

            var catProceduresList = new string[] { catToImprove,
                catToDel, catToSpeedyDel, catToRename, catToMerge, catToMove, catToSplit };
            var catSmallList = new string[] { catSmall700, catSmall800 };
            var catCoordsList = new string[] { catNoGeoCoords, catNoSourceCoords, catNoMouthCoords };

            Console.Write("Scanning category");
            var articles = ScanCategoryA(
                "Категория:Водные объекты по алфавиту",
                catProceduresList.Concat(catSmallList).Concat(catCoordsList).
                    Concat(new string[] { catNoArchives, catWebcitation, catNoRefs, catProblems }).ToArray(),
                new string[] { tmplNoRs, tmplDeadLink });
            Console.WriteLine(" Done");

            Console.Write("Processing...");
            int totalCount = articles.Length;
            var artsProcedures = articles.Where(
                a => a.Categories.Intersect(catProceduresList).Any()).ToArray();
            var artsNoRs = articles.Where(
                a => a.Templates.Contains(tmplNoRs)).ToArray();
            var artsNoArchives = articles.Where(
                a => a.Categories.Contains(catNoArchives) ||
                a.Categories.Contains(catWebcitation) ||
                a.Templates.Contains(tmplDeadLink)).ToArray();
            var artsNoRefs = articles.Where(
                a => a.Categories.Contains(catNoRefs)).ToArray();
            var artsSmallSize = articles.Where(
                a => a.Categories.Intersect(catSmallList).Any()).ToArray();
            var artsNoCoords = articles.Where(
                a => a.Categories.Intersect(catCoordsList).Any()).ToArray();
            var artsProblems = articles.Where(
                a => a.Categories.Contains(catProblems)).ToArray();
            var artsOldPat = articles.Where(
                a => a.OldReviewed).ToArray();
            var artsNoPat = articles.Where(
                a => a.Unreviewed).ToArray();

            var problemGroups = new List<Article[][]> {
                new[] { artsProcedures },
                new[] { artsNoRs, artsNoArchives },
                new[] { artsNoRefs },
                new[] { artsSmallSize },
                new[] { artsNoCoords },
                new[] { artsProblems },
                new[] { artsNoPat, artsOldPat } };
            problemGroups.Add(new[] { problemGroups.
                SelectMany(a => a).SelectMany(a => a).Distinct().ToArray() });

            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var date = DateTime.UtcNow.ToString("yyyy.MM.dd HH:mm");

            var tableLine = $"\n|-\n| {date} || {totalCount} || " + string.Join(" || ",
                problemGroups.Select(g => string.Join(" || ", g.Select(l => l.Length)) +
                $" || {g.SelectMany(a => a).Distinct().Count() * 100.0 / totalCount:0.00}"));

            Console.WriteLine(" Done");

            ObtainEditToken();

            Console.Write("Updating table...");
            string pageName = "Проект:Водные объекты/Статистика/2022";
            string wikiText = DownloadArticle(pageName);

            string marker = "<!-- Маркер вставки. Не трогать. -->";
            int insPoint = wikiText.IndexOf(marker) + marker.Length;

            string replWikiText = wikiText.Substring(0, insPoint) +
                tableLine + wikiText.Substring(insPoint);
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
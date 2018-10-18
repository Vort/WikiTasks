using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;

namespace WikiTasks
{
    class Article
    {
        public int PageId;
        public string Title;
        public List<string> Categories;
    }

    static class ShuffleClass
    {
        private static Random rng = new Random();

        public static void Shuffle<T>(this IList<T> list)
        {
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }
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

        Article[] ScanCategoryA(string category, string[] includeCategories)
        {
            var articles = new Dictionary<int, Article>();

            string continueQuery = null;
            string continueGcm = null;
            string continueCl = null;
            for (;;)
            {
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "generator", "categorymembers",
                    "prop", "categories",
                    "clcategories", string.Join("|", includeCategories),
                    "cllimit", "5000",
                    "clcontinue", continueCl,
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
                    string title = node.Attributes["title"].Value;
                    if (!articles.ContainsKey(pageId))
                    {
                        article = new Article
                        {
                            PageId = pageId,
                            Title = title,
                            Categories = new List<string>(),
                        };
                        articles.Add(pageId, article);
                    }
                    else
                        article = articles[pageId];
                    foreach (XmlNode catNode in node.SelectNodes("categories/cl"))
                        article.Categories.Add(catNode.Attributes["title"].Value);
                }

                XmlNode contNode = doc.SelectSingleNode("/api/continue");
                if (contNode == null)
                    break;
                var continueQueryAttr = contNode.Attributes["continue"];
                var continueGcmAttr = contNode.Attributes["gcmcontinue"];
                var continueClAttr = contNode.Attributes["clcontinue"];
                continueQuery = continueQueryAttr == null ? null : continueQueryAttr.Value;
                continueGcm = continueGcmAttr == null ? null : continueGcmAttr.Value;
                continueCl = continueClAttr == null ? null : continueClAttr.Value;
            }

            return articles.Values.ToArray();
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");

            string[] catLenList = new string[]
            {
                "Категория:Реки до 1000 км в длину",
                "Категория:Реки до 500 км в длину",
                "Категория:Реки до 100 км в длину",
                "Категория:Реки до 50 км в длину",
                "Категория:Реки до 10 км в длину",
                "Категория:Реки до 5 км в длину"
            };

            Console.Write("Scanning category");
            var articles = ScanCategoryA(
                "Категория:Википедия:Статьи о реках, требующие проверки", catLenList);
            Console.WriteLine(" Done");

            Console.Write("Processing...");
            var reorderedArticles = new List<Article>();
            for (int i = 0; i < catLenList.Length; i++)
            {
                var sameLenCatArticles = new List<Article>();
                foreach (Article article in articles)
                    if (article.Categories.Contains(catLenList[i]))
                        sameLenCatArticles.Add(article);
                sameLenCatArticles.Shuffle();
                reorderedArticles.AddRange(sameLenCatArticles);
            }
            var noLenInfoArticles = articles.Except(reorderedArticles).ToArray();
            noLenInfoArticles.Shuffle();
            reorderedArticles.AddRange(noLenInfoArticles);

            int selectedCount = 5;
            var selectedArticles = reorderedArticles;
            if (selectedArticles.Count > selectedCount)
                selectedArticles = selectedArticles.Take(selectedCount).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("{{колонки|3}}");
            sb.AppendLine("* '''Реки для " +
                "[[Шаблон:Непроверенная река/Краткая инструкция|выверки]]''':");
            if (selectedArticles.Count != 0)
            {
                foreach (var article in selectedArticles.Take(selectedArticles.Count - 1))
                    sb.AppendLine($"# [[{article.Title}]];");
                sb.AppendLine($"# [[{selectedArticles.Last().Title}]].");
            }
            sb.AppendLine("{{колонки/конец}}<noinclude>" +
                "[[Категория:Проект:Водные объекты/Текущие события]]</noinclude>");

            Console.WriteLine(" Done");

            ObtainEditToken();

            Console.Write("Updating table...");
            string pageName = "Проект:Водные объекты/Выверка/Случайные статьи";
            bool isEditSuccessful = EditPage(csrfToken,
                pageName, "обновление данных", sb.ToString());

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
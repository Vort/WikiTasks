using LinqToDB;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace WikiTasks
{
    [Table(Name = "Articles")]
    class Article
    {
        [PrimaryKey]
        public int PageId;
        [Column()]
        public string Timestamp;
        [Column()]
        public string Title;
        [Column()]
        public string SrcWikiText;

        public string Template;
    };

    class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<Article> Articles { get { return GetTable<Article>(); } }
    }

    class Program
    {
        MwApi wpApi;
        static Db db;

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


        static bool HaveTable(string name)
        {
            return db.DataProvider.GetSchemaProvider().
                GetSchema(db).Tables.Any(t => t.TableName == name);
        }

        List<Article> DeserializeArticles(string xml)
        {
            List<Article> articles = new List<Article>();

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            foreach (XmlNode pageNode in doc.SelectNodes("/api/query/pages/page"))
            {
                if (pageNode.Attributes["missing"] != null)
                    continue;

                Article article = new Article();
                article.Title = pageNode.Attributes["title"].InnerText;
                article.PageId = int.Parse(pageNode.Attributes["pageid"].InnerText);

                XmlNode revNode = pageNode.SelectSingleNode("revisions/rev");
                article.SrcWikiText = revNode.InnerText;
                if (revNode.Attributes["timestamp"] != null)
                    article.Timestamp = revNode.Attributes["timestamp"].InnerText;

                articles.Add(article);
            }

            return articles;
        }

        void DownloadArticles(int[] ids)
        {
            if (HaveTable("Articles"))
                db.DropTable<Article>();
            db.CreateTable<Article>();

            Console.Write("Downloading articles");
            var chunks = SplitToChunks(ids, 50);
            foreach (var chunk in chunks)
            {
                string idsChunk = string.Join("|", chunk);
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "prop", "revisions",
                    "rvprop", "timestamp|content",
                    "format", "xml",
                    "pageids", idsChunk);
                Console.Write('.');

                List<Article> articles = DeserializeArticles(xml);
                db.BeginTransaction();
                foreach (Article a in articles)
                    db.Insert(a);
                db.CommitTransaction();
            }
            Console.WriteLine(" Done");
        }

        List<int> SearchArticles(string query, string ns = "0")
        {
            var idList = new List<int>();

            Console.Write("Searching articles");
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
            Console.WriteLine(" Done");

            return idList;
        }

        void ProcessArticles()
        {
            Console.Write("Processing articles...");
            var articles = db.Articles.OrderBy(a => a.Title).ToArray();
            foreach (Article article in articles)
            {
                var match = Regex.Match(article.SrcWikiText,
                    "((с|за) +([0-9]{4})( по | *— *)([0-9]{4}) г.*)?" +
                    "<timeline>\nColors=\n.*Period *= *from:0 till:([0-9]+).*" +
                    "ScaleMajor *= *gridcolor:[a-z]+ increment:([0-9]+) *start:0 *\n" +
                    "(ScaleMinor *= *gridcolor:[a-z]+ increment:([0-9]+) *start:0 *)?.*" +
                    "( *bar:[^ ]+ *from:0 *till: *([0-9.]+) *(text: *[0-9.]+ *)?\n){12}"
                    ,
                    RegexOptions.Singleline);
                if (!match.Success)
                    continue;

                article.Template = "{{Расход воды";
                var match2 = Regex.Match(match.Groups[1].Value, "(<ref[^>]*>[^<]+<\\/ref>)");
                if (match2.Success)
                    article.Template += "|" + match2.Groups[1].Value;
                else
                    article.Template += "|&amp;nbsp;";
                article.Template += "|" + match.Groups[6].Value;
                article.Template += "|" + match.Groups[7].Value;
                article.Template += "|" + match.Groups[9].Value;
                foreach (var month in match.Groups[11].Captures)
                    article.Template += "|" + month.ToString().Replace('.', ',');
                article.Template += "|годовой=нет";
                if (match.Groups[2].Value != "")
                    article.Template += "|с=" + match.Groups[3].Value;
                if (match.Groups[3].Value != "")
                    article.Template += "|по=" + match.Groups[5].Value;
                article.Template += "|пост=}}";
            }

            var fltArticles = articles.Where(a => a.Template != null).ToArray();

            var sb = new StringBuilder();
            sb.AppendLine("{|class=\"wide\" style=\"table-layout: fixed;word-wrap:break-word\"");
            sb.AppendLine("!width=\"28px\"|№");
            sb.AppendLine("!width=\"20%\"|Статья");
            sb.AppendLine("!width=\"80%\"|Шаблон");
            for (int i = 0; i < fltArticles.Length; i++)
            {
                sb.AppendLine("|-");
                sb.AppendLine("|" + (i + 1));
                sb.AppendLine("|[[" + fltArticles[i].Title + "]]");
                sb.AppendLine("|<small><code><nowiki>" + fltArticles[i].Template + "</nowiki></code></small>");
            }
            sb.AppendLine("|}");
            sb.AppendLine();
            sb.AppendLine("[[Категория:Проект:Водные объекты/Текущие события]]");

            File.WriteAllText("result.txt", sb.ToString());
            Console.WriteLine(" Done");
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");
            var ids = SearchArticles("hastemplate:Река insource:/\\<timeline\\>/");
            DownloadArticles(ids.ToArray());
            ProcessArticles();
        }

        static void Main(string[] args)
        {
            db = new Db();
            new Program();
            db.Dispose();
        }
    }
}
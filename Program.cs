using LinqToDB;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace WikiTasks
{
    [Table(Name = "Articles")]
    public class Article
    {
        [PrimaryKey]
        public string Title;
        [Column()]
        public string Timestamp;
        [Column()]
        public string WikiText;
        [Column()]
        public string NewWikiText;
        [Column()]
        public int Status;
    };


    public class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<Article> Articles { get { return GetTable<Article>(); } }
    }

    class Program
    {
        static Db db;
        MwApi wpApi;

        string csrfToken;

        Dictionary<string, string[]> templDic;

        List<List<string>> SplitToChunks(string[] titles, int chunkSize)
        {
            int chunkCount = (titles.Length + chunkSize - 1) / chunkSize;

            List<List<string>> chunks = new List<List<string>>();
            for (int i = 0; i < chunkCount; i++)
            {
                List<string> chunk = new List<string>();
                for (int j = 0; j < chunkSize; j++)
                {
                    int k = i * chunkSize + j;
                    if (k >= titles.Length)
                        break;
                    chunk.Add(titles[k]);
                }
                chunks.Add(chunk);
            }

            return chunks;
        }

        List<Article> GetArticles(string xml)
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

                XmlNode revNode = pageNode.SelectSingleNode("revisions/rev");
                article.WikiText = revNode.InnerText;
                if (revNode.Attributes["timestamp"] != null)
                    article.Timestamp = revNode.Attributes["timestamp"].InnerText;

                articles.Add(article);
            }

            return articles;
        }

        void DownloadArticles()
        {
            Console.Write("Downloading articles");
            var updChunks = SplitToChunks(
                db.Articles.Select(a => a.Title).ToArray(), 50);
            foreach (var chunk in updChunks)
            {
                string titlesString = string.Join("|", chunk);
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "prop", "revisions",
                    "rvprop", "timestamp|content",
                    "format", "xml",
                    "titles", titlesString);
                Console.Write('.');

                List<Article> articles = GetArticles(xml);
                db.BeginTransaction();
                foreach (Article a in articles)
                    db.Update(a);
                db.CommitTransaction();
            }
            Console.WriteLine(" Done");
        }

        void FillArticlesDb()
        {
            if (!db.DataProvider.GetSchemaProvider().GetSchema(db).
                Tables.Any(t => t.TableName == "Articles"))
            {
                db.CreateTable<Article>();

                db.BeginTransaction();
                foreach (var kv in templDic)
                {
                    foreach (var name in kv.Value)
                    {
                        db.Insert(new Article()
                        {
                            Title = name
                        });
                    }
                }
                db.CommitTransaction();
            }
            if (db.Articles.All(a => a.WikiText == null))
                DownloadArticles();
        }

        void ScanCats()
        {
            Directory.CreateDirectory("data");
            var cats = PetScan.Query(
                "language", "ru",
                "categories", "Бассейн Таза",
                "ns[0]", null,
                "ns[14]", "1").Select(pe => pe.Title.Replace("_", " ")).ToList();
            File.WriteAllLines("data\\cats.txt", cats);

            foreach (string cat in cats)
            {
                var arts = PetScan.Query(
                    "language", "ru",
                    "depth", "10",
                    "categories", cat).Select(pe => pe.Title.Replace("_", " ")).ToList();
                File.WriteAllLines("data\\" + cat + ".txt", arts);
            }
        }

        string RemoveParentheses(string s)
        {
            return Regex.Replace(s, " \\([^)]+\\)", "");
        }

        void ProcessCats()
        {
            var cats = File.ReadAllLines("data\\cats.txt");
            var dic = new Dictionary<string, string[]>();
            foreach (string cat in cats)
                dic[cat] = File.ReadAllLines("data\\" + cat + ".txt");
            templDic = dic.Where(kv => kv.Value.Length > 4).ToDictionary(kv => kv.Key, kv => kv.Value);

            var genTempl = File.ReadAllText("templ.txt");
            foreach (var kv in templDic)
            {
                string[] sl1 = kv.Value.OrderBy(s => s).ToArray();
                string[] sl2 = sl1.Select(s => RemoveParentheses(s)).ToArray();
                var sl3 = new List<string>();
                for (int i = 0; i < sl1.Length; i++)
                {
                    if (sl1[i] == sl2[i])
                        sl3.Add($"\r\n* [[{sl1[i]}]]");
                    else
                        sl3.Add($"\r\n* [[{sl1[i]}|{sl2[i]}]]");
                }
                var templ = genTempl.Replace("{{{список_рек}}}", string.Join("", sl3));
                templ = templ.Replace("{{{заглавие}}}", RemoveParentheses(kv.Key));
                File.WriteAllText("templ\\" + kv.Key + ".txt", templ);
            }
        }

        void MakeReplacements()
        {
            var articles = db.Articles.ToArray();

            if (articles.Any(a => a.NewWikiText != null))
                return;

            db.BeginTransaction();
            foreach (var kv in templDic)
            {
                foreach (var name in kv.Value)
                {
                    var article = articles.First(a => a.Title == name);
                    var newWikiText = article.WikiText.Replace(
                        "\n{{Таз}}", "\n{{" + kv.Key + "}}");
                    if (newWikiText != article.WikiText)
                        article.NewWikiText = newWikiText;
                    db.Update(article);
                }
            }
            db.CommitTransaction();
        }

        bool EditPage(string csrfToken, string timestamp, string title, string summary, string text)
        {
            string xml = wpApi.PostRequest(
                "action", "edit",
                "format", "xml",
                "bot", "true",
                "title", title,
                "summary", summary,
                "text", text,
                "basetimestamp", timestamp,
                "token", csrfToken);

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            if (doc.SelectNodes("/api/error").Count == 1)
                return false;
            return doc.SelectSingleNode("/api/edit").Attributes["result"].InnerText == "Success";
        }


        void ApplyReplacements()
        {
            Console.Write("Making replacements");

            var articles = db.Articles.
                Where(a => a.Status == 0 && a.NewWikiText != null).
                OrderByDescending(a => a.Title).ToArray();
            foreach (var article in articles)
            {
                bool isEditSuccessful = EditPage(csrfToken, article.Timestamp,
                    article.Title, "замена навигационного шаблона", article.NewWikiText);

                article.Status = isEditSuccessful ? (byte)1 : (byte)2;
                db.Update(article);
                Console.Write(isEditSuccessful ? '.' : 'x');
            }
            Console.WriteLine(" Done");
        }

        string GetToken(string type)
        {
            string xml = wpApi.PostRequest(
                "action", "query",
                "format", "xml",
                "meta", "tokens",
                "type", type);

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            if (doc.SelectNodes("/api/error").Count == 1)
                return null;

            return doc.SelectNodes("/api/query/tokens")[0].Attributes[type + "token"].InnerText;
        }

        void ObtainEditToken()
        {
            Console.Write("Authenticating...");
            csrfToken = GetToken("csrf");
            if (csrfToken == null)
            {
                Console.WriteLine(" Failed");
                throw new Exception();
            }
            Console.WriteLine(" Done");
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");
            ObtainEditToken();

            //ScanCats();
            ProcessCats();
            FillArticlesDb();
            MakeReplacements();

            ApplyReplacements();
        }

        static void Main(string[] args)
        {
            db = new Db();
            new Program();
            db.Dispose();
        }
    }
}

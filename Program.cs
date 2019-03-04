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
    public enum ProcessStatus
    {
        NotProcessed = 0,
        Success = 1,
        Failure = 2,
        Skipped = 3
    }

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
        [Column()]
        public string DstWikiText;
        [Column()]
        public ProcessStatus Status;
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
                article.Title = pageNode.Attributes["title"].Value;
                article.PageId = int.Parse(pageNode.Attributes["pageid"].Value);

                XmlNode revNode = pageNode.SelectSingleNode("revisions/rev");
                article.SrcWikiText = revNode.InnerText;
                if (revNode.Attributes["timestamp"] != null)
                    article.Timestamp = revNode.Attributes["timestamp"].Value;

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
            var chunks = SplitToChunks(ids, 100);
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

        void ProcessArticles()
        {
            Console.Write("Processing...");

            var articles = db.Articles.ToArray();
            var dct = new Dictionary<string, int>();

            string[] replTitles =
            {
                "{{книга |автор= |часть= |ссылка часть= |заглавие=Блакітная кніга Беларусі. |ответственный=Рэдкал.: Н. А. Дзiсько i iнш |место=Мн. |издательство=[[Белорусская энциклопедия имени Петруся Бровки|БелЭн]] |год=1994 |том= |страницы= |столбцы= |страниц=415}}{{ref-be}}",
                "{{книга |автор= |часть= |ссылка часть= |заглавие=Блакітная кніга Беларусі. |ответственный=Рэдкал.: Н. А. Дзiсько i iнш |место=Мн. |издательство=[[Белорусская энциклопедия имени Петруся Бровки|БелЭн]] |год=1994 |том= |страницы= |столбцы= |страниц=415}} {{ref-be}}",
                "{{книга |автор= |заглавие=Блакітная кніга Беларусі. |ответственный=Рэдкал.: Н. А. Дзiсько i iнш |место=Мн. |издательство=[[Белорусская энциклопедия имени Петруся Бровки|БелЭн]] |год=1994 |том= |страницы= |столбцы= |страниц=415}}{{ref-be}}",
                "{{книга |автор= |заглавие=Блакітная кніга Беларусі. |ответственный=Рэдкал.: Н. А. Дзiсько i iнш |место=Мн. |издательство=[[Белорусская энциклопедия имени Петруся Бровки|БелЭн]] |год=1994 |страницы= |страниц=415}}{{ref-be}}",
                "{{книга|заглавие=Блакiтная кнiга Беларусi: энцыкл. |ответственный=Рэдкал.: Н. А. Дзiсько i iнш. |место=Мн. |издательство= БелЭн| год=1994 |страниц=415}}{{ref-be}}",
                "Блакiтная кнiга Беларусi: энцыкл. / Рэдкал.: Н. А. Дзiсько i iнш. — Мн.: БелЭн, 1994. — 415 с.",
                "Блакітная кніга Беларусі: Энцыкл. / БелЭн; Рэдкал.: Н. А. Дзісько і інш. — Мн.: БелЭн, 1994.",
                "Блакiтная кнiга Беларусi: Энцыкл. / БелЭн; Рэдкал.: Н. А. Дзiсько i iнш. — Мн.: БелЭн, 1994.",
                "Блакітная кніга Беларусі: Энцыкл. / БелЭн; Рэдкал.: Н. А. Дзісько і інш. — Мн.: БелЭн, 1994",
                "«Блакітная кніга Беларусі». — Мн.:БелЭн, 1994.",
                "«Блакітная кніга Беларусі». — Мн.:БелЭн, 1994",
                "Блакітная кніга Беларусі. — Мн.: БелЭн, 1994.",
                "Блакiтная кнiга Беларусi. — Мн.: БелЭн, 1994.",
                "Блакiтная кнiга Беларусi. — Мн.: БелЭн, 1994",
                "Блакiтная кнiга Беларусi. — Мн.: БелЭн, 1994",
                "Блакітная кніга Беларусі. — Мн.:БелЭн, 1994.",
                "Блакiтная кнiга Беларусi. — Мн.:БелЭн, 1994.",
                "Блакiтная кнiга Беларусi. — Мн.:БелЭн, 1994",
            };

            db.BeginTransaction();
            foreach (var article in articles.OrderBy(a => a.Title))
            {
                var matches = Regex.Matches(article.SrcWikiText,
                    "[>*\\n] *([^>*\\n]*Блак[іi]тная кн[iі]га Беларус[iі][^<*\\n]*)[\\n<]");
                if (matches.Count != 1)
                    continue;
                var m = matches[0];
                var citation = m.Groups[1].Value;

                if (replTitles.Any(t => citation == t))
                {
                    article.DstWikiText = article.SrcWikiText.Replace(
                        citation, "{{Книга:БКБ}}");
                    if (article.SrcWikiText == article.DstWikiText)
                        throw new Exception();
                    db.Update(article);
                }
                else
                {
                    if (!dct.ContainsKey(citation))
                        dct[citation] = 1;
                    else
                        dct[citation]++;
                }
            }
            db.CommitTransaction();

            File.WriteAllLines("result.txt",
                dct.OrderByDescending(kv => kv.Value).Select(kv => kv.Value + " : " + kv.Key).ToArray());

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

        void MakeReplacements()
        {
            Console.Write("Making replacements");
            var articles = db.Articles.
                Where(a => a.Status == ProcessStatus.NotProcessed && a.DstWikiText != null).
                OrderBy(a => a.Title).ToArray();

            foreach (var article in articles)
            {
                bool isEditSuccessful = EditPage(csrfToken, article.Timestamp,
                    article.Title, "оформление", article.DstWikiText);
                article.Status = isEditSuccessful ? ProcessStatus.Success : ProcessStatus.Failure;
                db.Update(article);
                Console.Write(isEditSuccessful ? '.' : 'x');
            }

            Console.WriteLine(" Done");
        }


        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");
            var ids = SearchArticles("insource:/Блак[іi]тная кн[iі]га Беларус[iі]/");
            DownloadArticles(ids.ToArray());
            ProcessArticles();
            ObtainEditToken();
            MakeReplacements();
        }

        static void Main(string[] args)
        {
            db = new Db();
            new Program();
            db.Dispose();
        }
    }
}
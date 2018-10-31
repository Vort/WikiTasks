using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using LinqToDB;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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

        public List<string> Errors;
        public Template Template;

        public double? k;
    };

    class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<Article> Articles { get { return GetTable<Article>(); } }
    }

    class TemplateParam
    {
        public bool Newline;
        public int Sp1;
        public int Sp2;
        public string Name;
        public int Sp3;
        public int Sp4;
        public string Value;
        // Отключено для экономии памяти
        // public IParseTree[] ValueTrees;
    }

    class Template
    {
        public Template()
        {
            Params = new List<TemplateParam>();
        }

        public TemplateParam this[string name]
        {
            get
            {
                var pl = Params.Where(p => p.Name == name).ToArray();
                if (pl.Length == 0)
                    return null;
                else if (pl.Length == 1)
                    return pl[0];
                else
                    throw new Exception();
            }
        }

        public TemplateParam this[int index]
        {
            get
            {
                return Params[index];
            }
        }

        public bool HaveZeroNewlines()
        {
            if (Params.Count == 0)
                return true;
            return Params.All(p => !p.Newline);
        }

        public void Reformat()
        {
            var v = Params.Where(p => p.Value != "").Select(p => p.Sp4).Distinct().ToArray();
            bool std = v.Length == 1 && v[0] == 1;

            for (int i = 1; i < Params.Count; i++)
            {
                if (Params[i].Newline &&
                    Params[i - 1].Value == "")
                {
                    if (std)
                        Params[i - 1].Sp4 = 1;
                    else
                        Params[i - 1].Sp4 = Params[i - 1].Sp4 >= 1 ? 1 : 0;
                }
            }
        }

        public int GetIndex(TemplateParam param)
        {
            return Params.FindIndex(p => p == param);
        }

        public void InsertAfter(TemplateParam param, TemplateParam newParam)
        {
            if (Params.Where(p => p.Name == newParam.Name).Count() != 0)
                throw new Exception();
            Params.Insert(Params.FindIndex(p => p == param) + 1, newParam);
        }

        public void Remove(string paramName)
        {
            Params.RemoveAll(p => p.Name == paramName);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("{{");
            sb.Append(Name);
            foreach (var param in Params)
            {
                if (param.Newline)
                    sb.Append('\n');
                sb.Append(new string(' ', param.Sp1));
                sb.Append('|');
                sb.Append(new string(' ', param.Sp2));
                sb.Append(param.Name);
                sb.Append(new string(' ', param.Sp3));
                sb.Append('=');
                sb.Append(new string(' ', param.Sp4));
                sb.Append(param.Value);
            }
            if (!HaveZeroNewlines())
                sb.Append("\n");
            sb.Append("}}");
            return sb.ToString();
        }

        public string Name;
        public List<TemplateParam> Params;
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

        List<int> SearchTransclusions(string pageName)
        {
            var idList = new List<int>();

            Console.Write("Searching articles");
            string continueQuery = null;
            string continueTi = null;
            for (;;)
            {
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "prop", "transcludedin",
                    "titles", pageName,
                    "tinamespace", "0",
                    "tiprop", "pageid",
                    "tilimit", "500",
                    "ticontinue", continueTi,
                    "continue", continueQuery,
                    "format", "xml");
                Console.Write('.');

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                foreach (XmlNode node in doc.SelectNodes("/api/query/pages/page/transcludedin/ti"))
                {
                    int id = int.Parse(node.Attributes["pageid"].InnerText);
                    idList.Add(id);
                }

                XmlNode contNode = doc.SelectSingleNode("/api/continue");
                if (contNode == null)
                    break;
                var continueQueryAttr = contNode.Attributes["continue"];
                var continueTiAttr = contNode.Attributes["ticontinue"];
                continueQuery = continueQueryAttr == null ? null : continueQueryAttr.Value;
                continueTi = continueTiAttr == null ? null : continueTiAttr.Value;
            }
            Console.WriteLine(" Done");

            return idList;
        }

        double? GetDouble(TemplateParam param)
        {
            if (param == null)
                return null;

            string tv = param.Value.Trim().Replace(',', '.').Replace(" ", "");
            if (tv.Contains("<r"))
                tv = tv.Substring(0, tv.IndexOf("<r"));
            if (tv.Length == 0)
                return null;

            double d;
            if (!double.TryParse(tv, out d))
                return null;

            return d;
        }

        void ProcessArticles()
        {
            int processed = 0;
            int lexerErrors = 0;
            int parserErrors = 0;

            var articles = db.Articles.ToArray();
            //var articles = db.Articles.Take(100).ToArray();

            Console.Write("Parsing articles");
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            Parallel.ForEach(articles, article =>
            {
                AntlrErrorListener ael = new AntlrErrorListener();
                AntlrInputStream inputStream = new AntlrInputStream(article.SrcWikiText);
                WikiLexer lexer = new WikiLexer(inputStream);
                lexer.RemoveErrorListeners();
                lexer.AddErrorListener(ael);
                CommonTokenStream commonTokenStream = new CommonTokenStream(lexer);
                WikiParser parser = new WikiParser(commonTokenStream);
                parser.RemoveErrorListeners();
                parser.AddErrorListener(ael);
                WikiParser.InitContext initContext = parser.init();
                WikiVisitor visitor = new WikiVisitor(article,
                    new string[] { "Водохранилище", "Озеро", "Пруд" }, null);
                visitor.VisitInit(initContext);
                article.Errors = ael.ErrorList;

                Interlocked.Add(ref lexerErrors, ael.LexerErrors);
                Interlocked.Add(ref parserErrors, ael.ParserErrors);

                if (Interlocked.Increment(ref processed) % 50 == 0)
                    Console.Write('.');
            });
            stopwatch.Stop();

            Console.WriteLine(" Done");
            Console.WriteLine(" Articles: " + articles.Count());
            Console.WriteLine(" Parser errors: " + parserErrors);
            Console.WriteLine(" Lexer errors: " + lexerErrors);
            Console.WriteLine(" Parsing time: " + stopwatch.Elapsed.TotalSeconds + " sec");

            List<string> errorLog = new List<string>();
            foreach (Article article in articles)
            {
                if (article.Errors == null)
                    continue;
                if (article.Errors.Count == 0)
                    continue;
                errorLog.Add("Статья: " + article.Title);
                errorLog.AddRange(article.Errors);
            }
            File.WriteAllLines("error_log.txt", errorLog.ToArray(), Encoding.UTF8);

            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

            var l1 = new List<string>();
            var l2 = new List<string>();

            Console.Write("Processing...");


            foreach (Article article in articles)
            {
                double? volume = GetDouble(article.Template["Объём"]);
                double? area = GetDouble(article.Template["Площадь"]);
                if (area == null)
                {
                    double? length = GetDouble(article.Template["Длина"]);
                    double? width = GetDouble(article.Template["Ширина"]);
                    area = length * width;
                }
                double? depth = GetDouble(article.Template["Средняя глубина"]);
                if (depth == null)
                    depth = GetDouble(article.Template["Наибольшая глубина"]);
                double? volumeCalc = area * depth / 1000.0;
                article.k = volume > volumeCalc ? 
                    volume / volumeCalc : volumeCalc / volume;
            }

            var sb = new StringBuilder();

            sb.AppendLine("{|class=\"wikitable\"");
            sb.AppendLine("!№");
            sb.AppendLine("!Статья");
            sb.AppendLine("!k");
            int i = 1;
            foreach (Article article in articles.
                Where(a => a.k != null && a.k >= 5.0).OrderByDescending(a => a.k))
            {
                sb.AppendLine("|-");
                sb.AppendLine($"| {i}");
                sb.AppendLine($"| [[{article.Title}]]");
                sb.AppendLine($"| {article.k:0.000}");
                i++;
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
            var idsR = SearchTransclusions("Шаблон:Водохранилище");
            var idsL = SearchTransclusions("Шаблон:Озеро");
            DownloadArticles(idsR.Concat(idsL).ToArray());
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
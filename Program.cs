using Antlr4.Runtime;
using LinqToDB;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
        public int RevisionId;
        [Column()]
        public string Title;
        [Column()]
        public string SrcWikiText;

        public List<string> Errors;
        public Template Template;

        public string PreambleName;
        public string PreambleNameNorm;
        public string TemplateName;
        public string TemplateNameNorm;
        public string TitleName;
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
        public int StartPosition;
        public int StopPosition;
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
                article.Title = pageNode.Attributes["title"].Value;
                article.PageId = int.Parse(pageNode.Attributes["pageid"].Value);

                XmlNode revNode = pageNode.SelectSingleNode("revisions/rev");
                article.SrcWikiText = revNode.InnerText;
                article.RevisionId = int.Parse(revNode.Attributes["revid"].Value);

                articles.Add(article);
            }

            return articles;
        }

        void DownloadArticles(Dictionary<int, int> ids)
        {
            if (!HaveTable("Articles"))
                db.CreateTable<Article>();

            var dbids = db.Articles.ToDictionary(a => a.PageId, a => a.RevisionId);


            var idsset = new HashSet<int>(ids.Keys);
            var dbidsset = new HashSet<int>(dbids.Keys);

            var deleted = dbidsset.Except(idsset).ToArray();
            var added = idsset.Except(dbidsset).ToArray();
            var existing = idsset.Intersect(dbidsset).ToArray();
            var changed = existing.Where(id => ids[id] != dbids[id]).ToArray();

            var todl = added.ToDictionary(id => id, id => true).Union(
                changed.ToDictionary(id => id, id => false)).ToDictionary(
                kv => kv.Key, kv => kv.Value);

            db.Articles.Delete(a => deleted.Contains(a.PageId));

            Console.Write("Downloading articles");
            var chunks = SplitToChunks(todl.Keys.OrderBy(x => x).ToArray(), 100);
            foreach (var chunk in chunks)
            {
                string idsChunk = string.Join("|", chunk);
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "prop", "revisions",
                    "rvprop", "ids|content",
                    "rvslots", "main",
                    "format", "xml",
                    "pageids", idsChunk);
                Console.Write('.');

                List<Article> articles = DeserializeArticles(xml);
                db.BeginTransaction();
                foreach (Article a in articles)
                    if (todl[a.PageId])
                        db.Insert(a);
                    else
                        db.Update(a);
                db.CommitTransaction();
            }
            Console.WriteLine(" Done");
        }

        string Normalize1(string name)
        {
            string normalized = name.Replace("\u0301", "");
            normalized = normalized.Replace("&nbsp;", " ");
            normalized = normalized.Replace("\u00A0", " ");
            return normalized;
        }

        string Normalize2(string name)
        {
            string[] toReplace = { "водохранилище", "озеро", "пруд-накопитель", "пруд", "сардоба",
                "горный парк", "река", "канал", "болото", "пролив", "водопады", "водопад", "ручей",
                "ледник", "море", "минеральная вода", "залив", "бухта", "губа", "лагуна", "овраг" };

            string normalized = Regex.Replace(name, "\\([^)]+\\)", "").Trim();
            foreach (var replName in toReplace)
            {
                normalized = normalized.Replace(replName, "");
                normalized = normalized.Replace(
                    replName.First().ToString().ToUpper() + replName.Substring(1), "");
            }
            normalized = normalized.Trim();
            return normalized;
        }


        string Normalize(string name)
        {
            return Normalize2(Normalize1(name));
        }

        string Colorize(string name)
        {
            return Regex.Replace(
                name, "([^0-9а-яА-ЯёЁ —\\(\\)«»\\.\\-]+)", "{{color|crimson|$1}}");
        }

        void ProcessArticles()
        {
            int processed = 0;
            int lexerErrors = 0;
            int parserErrors = 0;

            var articles = db.Articles.ToArray();
            //var articles = db.Articles.Take(1000).ToArray();
            //var articles = db.Articles.Where(a => a.Title == "Большой Сарышыганак").ToArray();

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
                    new string[] { "Водохранилище", "Озеро", "Пруд", "Река", "Канал",
                    "Море", "Залив", "Пролив", "Группа озёр", "Водопад", "Морское течение",
                    "Ледник", "Болото", "Речной порог", "Водный источник", "Солончак",
                    "Родник", "Заповедная зона" }, null);
                visitor.VisitInit(initContext);
                article.Errors = ael.ErrorList;

                Interlocked.Add(ref lexerErrors, ael.LexerErrors);
                Interlocked.Add(ref parserErrors, ael.ParserErrors);

                if (Interlocked.Increment(ref processed) % 100 == 0)
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

            string[] blacklist = { "/", "{", ",", "(", "<" };

            foreach (Article article in articles)
            {
                article.TitleName = Normalize(article.Title);

                if (article.Template == null)
                    continue;

                if (article.Template["Название"] != null)
                {
                    article.TemplateName = article.Template["Название"].Value;
                    if (blacklist.Any(ble => article.TemplateName.Contains(ble)))
                        article.TemplateName = null;
                }

                if (article.TemplateName != null)
                    article.TemplateNameNorm = Normalize(article.TemplateName);

                var match = Regex.Match(article.SrcWikiText.Substring(
                    article.Template.StopPosition), "'''([^']+)'''");
                if (match.Success)
                {
                    article.PreambleName = match.Groups[1].Value;
                    article.PreambleName = Regex.Replace(article.PreambleName, "^([^<]+).*", "$1");
                    if (!article.PreambleName.Contains('[') && !(article.PreambleName.Length == 1))
                    {
                        article.PreambleName = Normalize1(article.PreambleName);
                        article.PreambleNameNorm = Normalize2(article.PreambleName);
                    }
                    else
                        article.PreambleName = null;
                }
            }

            var sb = new StringBuilder();

            sb.AppendLine("{|class=\"wikitable sortable\"");
            sb.AppendLine("!№");
            sb.AppendLine("!Статья");
            sb.AppendLine("!Название в карточке");
            sb.AppendLine("!Название в преамбуле");
            int i = 1;
            foreach (Article article in articles.
                Where(a => a.TemplateNameNorm != null && a.PreambleName != null &&
                (a.TitleName != a.TemplateNameNorm || a.TitleName != a.PreambleNameNorm)).OrderBy(a => a.Title))
            {
                string templateOut = "{{color|gray|— // —}}";
                string preambleOut = "{{color|gray|— // —}}";
                if (article.TitleName != article.TemplateNameNorm)
                    templateOut = Colorize(article.TemplateName);
                if (article.TitleName != article.PreambleNameNorm)
                    preambleOut = Colorize(article.PreambleName);

                sb.AppendLine("|-");
                sb.AppendLine($"| {i}");
                sb.AppendLine($"| [[{article.Title}]]");
                sb.AppendLine($"| {templateOut}");
                sb.AppendLine($"| {preambleOut}");
                i++;
            }
            sb.AppendLine("|}");
            sb.AppendLine();
            sb.AppendLine("[[Категория:Проект:Водные объекты/Текущие события]]");

            File.WriteAllText("result.txt", sb.ToString());
            Console.WriteLine(" Done");
        }

        Dictionary<int, int> ScanCategory(string category)
        {
            Console.Write("Scanning category");
            var ids = new Dictionary<int, int>();

            string continueQuery = null;
            string continueGcm = null;
            string continueRv = null;
            for (;;)
            {
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "generator", "categorymembers",
                    "prop", "revisions",
                    "rvprop", "ids",
                    "gcmprop", "ids",
                    "gcmtype", "page",
                    "gcmtitle", category,
                    "gcmlimit", "max",
                    "gcmcontinue", continueGcm,
                    "rvcontinue", continueRv,
                    "continue", continueQuery,
                    "format", "xml");
                Console.Write('.');

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                foreach (XmlNode node in doc.SelectNodes("/api/query/pages/page"))
                {
                    int pageId = int.Parse(node.Attributes["pageid"].Value);
                    int revisionId = int.Parse(node.
                        SelectSingleNode("revisions/rev").Attributes["revid"].Value);
                    ids.Add(pageId, revisionId);
                }

                XmlNode contNode = doc.SelectSingleNode("/api/continue");
                if (contNode == null)
                    break;
                var continueQueryAttr = contNode.Attributes["continue"];
                var continueGcmAttr = contNode.Attributes["gcmcontinue"];
                var continueRvAttr = contNode.Attributes["rvcontinue"];
                continueQuery = continueQueryAttr == null ? null : continueQueryAttr.Value;
                continueGcm = continueGcmAttr == null ? null : continueGcmAttr.Value;
                continueRv = continueRvAttr == null ? null : continueRvAttr.Value;
            }
            Console.WriteLine(" Done");
            return ids;
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");
            var ids = ScanCategory("Категория:Водные объекты по алфавиту");
            DownloadArticles(ids);
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
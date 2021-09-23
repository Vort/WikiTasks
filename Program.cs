using Antlr4.Runtime;
using LinqToDB;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    class Mismatch
    {
        public string Title;
        public string TemplateOut;
        public string PreambleOut;
    }

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

            if (deleted.Length > 100)
                throw new Exception("Too many articles deleted. Looks like a bug");
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
            string[] toReplace = { "аул", "деревня", "кордон",
                "рабочий посёлок", "посёлок", "разъезд", "село", "хутор" };
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
                name, "([^0-9а-яА-ЯёЁ ’—\\(\\)«»№\\.\\-]+)", "{{color|crimson|<nowiki>$1</nowiki>}}");
        }

        void ProcessArticles()
        {
            int processed = 0;
            int lexerErrors = 0;
            int parserErrors = 0;

            var articles = db.Articles;
            //var articles = db.Articles.Take(50000);
            //var articles = db.Articles.Take(1000).Where(a => a.Title == "Мюнхен");

            string[] blacklist = { "/", "{", ",", "(", "<" };
            var templParams = new Dictionary<string, string>
            {
                { "НП", "русское название" },
                { "НП-Абхазия", "русское название" },
                { "НП-Австралия", "русское название" },
                { "НП-Армения", "русское название" },
                { "НП-Белоруссия", "русское название" },
                { "НП-Великобритания", "русское название" },
                { "НП-Грузия", "русское название" },
                { "НП/Грузия", "русское название" },
                { "НП-Донбасс", "русское название" },
                { "НП-Израиль", "русское название" },
                { "НП-Ирландия", "русское название" },
                { "НП-Казахстан", "русское название" },
                { "НП-Канада", "русское название" },
                { "НП-Киргизия", "русское название" },
                { "НП-Крым", "русское название" },
                { "НП-Майотта", "русское название" },
                { "НП-Мексика", "русское название" },
                { "НП-Молдавия", "русское название" },
                { "НП-Нидерланды", "русское название" },
                { "НП-НКР", "русское название" },
                { "НП-ОАЭ", "русское название" },
                { "НП-ПМР", "русское название" },
                { "НП-ПНА", "русское название" },
                { "НП-Польша", "русское название" },
                { "НП-Россия", "русское название" },
                { "НП-США", "русское название" },
                { "НП-Северная Ирландия", "русское название" },
                { "НП-Таиланд", "русское название" },
                { "НП-Тайвань", "русское название" },
                { "НП-Турция", "русское название" },
                { "НП-Украина", "русское название" },
                { "НП-Украина2", "русское название" },
                { "НП-Франция", "русское название" },
                { "НП-Южная Корея", "русское название" },
                { "НП-Южная Осетия", "русское название" },
                { "НП-Япония", "русское название" },
                { "НП+", "русское название" },
                { "НП+Россия", "русское название" },
                { "Бывший НП", "русское название" },
                { "Бывший населённый пункт", "русское название" },
                { "Достопримечательность", "Русское название"},
                { "Древний город", "русское название" },
                { "Историческая местность в Москве", "Название" },
                { "Исторический район", "Название" },
                { "Канадский муниципалитет", "название" },
                { "Крепость", "Русское название" },
                { "Микрорайон", "Название" },
                { "Муниципальный район Германии", "русское название" },
                { "Община Германии", "русское название" },
                { "Объект Всемирного наследия", "RusName" },
                { "Район", "название" },
                { "Поселение Москвы", "Название поселения" },
                { "Префектура Японии", "Название" }
            };

            List<string> errorLog = new List<string>();
            List<Mismatch> mismatches = new List<Mismatch>();

            Console.Write("Processing articles");
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
                WikiVisitor visitor = new WikiVisitor(article, templParams.Keys.ToArray(), null);
                visitor.VisitInit(initContext);
                article.Errors = ael.ErrorList;

                if (article.Errors != null && article.Errors.Count != 0)
                {
                    lock (errorLog)
                    {
                        errorLog.Add("Статья: " + article.Title);
                        errorLog.AddRange(article.Errors);
                    }
                }

                Interlocked.Add(ref lexerErrors, ael.LexerErrors);
                Interlocked.Add(ref parserErrors, ael.ParserErrors);

                if (Interlocked.Increment(ref processed) % 100 == 0)
                    Console.Write('.');

                article.TitleName = Normalize(article.Title);

                if (article.Template == null)
                    return;

                var templateNameNorm = 
                    article.Template.Name.First().ToString().ToUpper() +
                    article.Template.Name.Substring(1);

                string paramName = templParams[templateNameNorm];
                if (article.Template[paramName] != null && article.Template[paramName].Value != "")
                {
                    article.TemplateName = article.Template[paramName].Value;
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

                if (article.TemplateNameNorm != null && article.PreambleName != null &&
                    (article.TitleName != article.TemplateNameNorm ||
                    article.TitleName != article.PreambleNameNorm))
                {
                    var mm = new Mismatch();
                    mm.Title = article.Title;
                    mm.TemplateOut = "{{color|gray|— // —}}";
                    mm.PreambleOut = "{{color|gray|— // —}}";
                    if (article.TitleName != article.TemplateNameNorm)
                        mm.TemplateOut = Colorize(article.TemplateName);
                    if (article.TitleName != article.PreambleNameNorm)
                        mm.PreambleOut = Colorize(article.PreambleName);
                    lock (mismatches)
                        mismatches.Add(mm);
                }
            });
            stopwatch.Stop();

            Console.WriteLine(" Done");
            Console.WriteLine(" Parser errors: " + parserErrors);
            Console.WriteLine(" Lexer errors: " + lexerErrors);
            Console.WriteLine(" Processing time: " + stopwatch.Elapsed.TotalSeconds + " sec");

            File.WriteAllLines("error_log.txt", errorLog.ToArray(), Encoding.UTF8);

            var sb = new StringBuilder();

            sb.AppendLine("{|class=\"wide sortable\" style=\"table-layout: fixed;word-wrap:break-word\"");
            sb.AppendLine("!width=\"16em\"|№");
            sb.AppendLine("!Статья");
            sb.AppendLine("!width=\"30%\"|Название в карточке");
            sb.AppendLine("!width=\"30%\"|Название в преамбуле");
            mismatches = mismatches.OrderBy(mm => mm.Title).ToList();
            for (int i = 0; i < mismatches.Count; i++)
            {
                var mm = mismatches[i];
                sb.AppendLine("|-");
                sb.AppendLine($"| {i + 1}");
                sb.AppendLine($"| [[{mm.Title}]]");
                sb.AppendLine($"| {mm.TemplateOut}");
                sb.AppendLine($"| {mm.PreambleOut}");
            }
            sb.AppendLine("|}");

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

                if (ids.Count == 0)
                    throw new Exception("Category is empty. Looks like a bug");
            }
            Console.WriteLine(" Done");
            return ids;
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");

            var ids = ScanCategory("Категория:Населённые пункты по алфавиту");
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
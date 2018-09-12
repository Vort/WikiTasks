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
        public int ReplIndex1;
        [Column()]
        public int ReplIndex2;
        [Column()]
        public string NewTemplateText;
        [Column()]
        public int Priority;
        [Column()]
        public ProcessStatus Status;


        public List<string> Errors;
        public Template Template;
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

        const int editsPerMinute = 120;
        const int connectionsLimit = 4;

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

        async Task<bool> EditPage(string csrfToken, string timestamp, int pageId, string summary, string text)
        {
            string xml = await wpApi.PostRequestAsync(
                "action", "edit",
                "format", "xml",
                "bot", "true",
                "pageid", pageId.ToString(),
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

        double? ProcessCoordD(TemplateParam param)
        {
            if (param == null)
                return null;

            string tv = param.Value.Trim();
            if (tv.Length == 0)
                return null;

            double d;
            if (double.TryParse(tv, out d))
                return d;
            else
                throw new Exception();
        }

        char? ProcessCoordC(TemplateParam param)
        {
            if (param == null)
                return null;

            string tv = param.Value.Trim().ToUpper();
            if (tv.Length == 0)
                return null;

            if (tv == "N")
                return 'N';
            else if (tv == "S")
                return 'S';
            else if (tv == "W")
                return 'W';
            else if (tv == "E")
                return 'E';
            else
                throw new Exception();
        }

        bool ProcessCoordinates(Article article, List<string> l1)
        {
            TemplateParam lat_dir_p = article.Template["lat_dir"];
            TemplateParam lat_deg_p = article.Template["lat_deg"];
            TemplateParam lat_min_p = article.Template["lat_min"];
            TemplateParam lat_sec_p = article.Template["lat_sec"];
            TemplateParam lon_dir_p = article.Template["lon_dir"];
            TemplateParam lon_deg_p = article.Template["lon_deg"];
            TemplateParam lon_min_p = article.Template["lon_min"];
            TemplateParam lon_sec_p = article.Template["lon_sec"];

            TemplateParam[] paramCoords = {
                lat_dir_p, lat_deg_p, lat_min_p, lat_sec_p,
                lon_dir_p, lon_deg_p, lon_min_p, lon_sec_p };

            string part1Coord = null;
            string part2Coord = null;
            int? scale = null;

            if (paramCoords.Any(pc => pc != null))
            {
                char? lat_dir = ProcessCoordC(lat_dir_p);
                double? lat_deg = ProcessCoordD(lat_deg_p);
                double? lat_min = ProcessCoordD(lat_min_p);
                double? lat_sec = ProcessCoordD(lat_sec_p);
                char? lon_dir = ProcessCoordC(lon_dir_p);
                double? lon_deg = ProcessCoordD(lon_deg_p);
                double? lon_min = ProcessCoordD(lon_min_p);
                double? lon_sec = ProcessCoordD(lon_sec_p);

                if (lat_deg != null && lat_min != null && lat_sec != null && lat_dir != null && lon_deg != null && lon_min != null && lon_sec != null && lon_dir != null)
                    part1Coord = $"{lat_deg}/{lat_min}/{lat_sec}/{lat_dir}/{lon_deg}/{lon_min}/{lon_sec}/{lon_dir}";
                else if (lat_deg != null && lat_min != null && lat_sec != null && lat_dir == null && lon_deg != null && lon_min != null && lon_sec != null && lon_dir == null)
                    part1Coord = $"{lat_deg}/{lat_min}/{lat_sec}/N/{lon_deg}/{lon_min}/{lon_sec}/E";
                else if (lat_deg != null && lat_min != null && lat_sec == null && lat_dir != null && lon_deg != null && lon_min != null && lon_sec == null && lon_dir != null)
                    part1Coord = $"{lat_deg}/{lat_min}/{lat_dir}/{lon_deg}/{lon_min}/{lon_dir}";
                else if (lat_deg != null && lat_min != null && lat_sec == null && lat_dir == null && lon_deg != null && lon_min != null && lon_sec == null && lon_dir == null)
                    part1Coord = $"{lat_deg}/{lat_min}/N/{lon_deg}/{lon_min}/E";
                else if (lat_deg != null && lat_min == null && lat_sec == null && lat_dir != null && lon_deg != null && lon_min == null && lon_sec == null && lon_dir != null)
                    part1Coord = $"{lat_deg}/{lat_dir}/{lon_deg}/{lon_dir}";
                else if (lat_deg != null && lat_min == null && lat_sec == null && lat_dir == null && lon_deg != null && lon_min == null && lon_sec == null && lon_dir == null)
                    part1Coord = $"{lat_deg}/{lon_deg}";
                else if (lat_deg == null && lat_min == null && lat_sec == null && lat_dir == null && lon_deg == null && lon_min == null && lon_sec == null && lon_dir == null)
                    part1Coord = "";
                else if (lat_deg == null && lat_min == null && lat_sec == null && lat_dir != null && lat_dir == 'N' && lon_deg == null && lon_min == null && lon_sec == null && lon_dir != null && lon_dir == 'E')
                    part1Coord = "";
                else
                {
                    l1.Add($"# [[{article.Title}]], 1");
                    return false;
                }
            }

            var coordP = article.Template["Координаты"];
            if (coordP != null)
            {
                var cvt = coordP.Value.Trim();
                if (cvt.Length != 0)
                {
                    Match pm = Regex.Match(cvt, "^\\{\\{[Cc]oord(\\|[^|}]+){2,}\\}\\}$");
                    if (!pm.Success)
                    {
                        if (!Regex.IsMatch(cvt, "[0-9.\\-]+/"))
                            l1.Add($"# [[{article.Title}]], 2.1");
                        return false;
                    }
                    var coordParams = pm.Groups[1].Captures.Cast<Capture>().
                        Select(c => c.Value.Substring(1)).ToArray();
                    var coordParamsClass = coordParams.Select(p => {
                        double t;
                        if (double.TryParse(p, out t))
                            return 0;
                        string pn = p.ToUpper();
                        if (pn == "N" || pn == "S")
                            return 1;
                        if (pn == "W" || pn == "E")
                            return 2;
                        return 3;
                    }).ToArray();
                    var cm = Regex.Match(string.Concat(coordParamsClass), "([3]*)([^3].*[^3])([3]*)");
                    if (!new string[] { "00010002", "001002", "0102", "00" }.Contains(cm.Groups[2].Value))
                    {
                        l1.Add($"# [[{article.Title}]], 2.2");
                        return false;
                    }
                    var coordParamsMain = coordParams.
                        Skip(cm.Groups[1].Length).Take(cm.Groups[2].Length).ToArray();
                    var coordParamsEtc = coordParams.Take(cm.Groups[1].Length).Concat(
                        coordParams.Skip(cm.Groups[1].Length + cm.Groups[2].Length)).ToArray();

                    for (int i = 0; i < coordParamsMain.Length; i++)
                        if (cm.Groups[2].Value[i] == '0')
                            coordParamsMain[i] = double.Parse(coordParamsMain[i]).ToString();

                    part2Coord = string.Join("/", coordParamsMain);

                    foreach (var p in coordParamsEtc)
                    {
                        var sm = Regex.Match(p, "scale[:=]([0-9]+)");
                        if (sm.Success)
                        {
                            if (scale != null)
                            {
                                l1.Add($"# [[{article.Title}]], 2.3");
                                return false;
                            }
                            int ts;
                            if (!int.TryParse(sm.Groups[1].Value, out ts))
                            {
                                l1.Add($"# [[{article.Title}]], 2.4");
                                return false;
                            }
                            scale = ts;
                        }
                    }
                }
            }

            if (part1Coord != null && part1Coord != "" && part2Coord != null && part1Coord != part2Coord)
            {
                l1.Add($"# [[{article.Title}]], 3.1");
                return false;
            }

            var coord = part1Coord;
            if (part2Coord != null)
                coord = part2Coord;

            if (coord == null)
                return false;

            var paramCoordsNN = paramCoords.Where(p => p != null).ToArray();

            if (coordP == null)
            {
                var cloneParam = article.Template["Метро"];
                if (cloneParam == null)
                    cloneParam = article.Template["Местоположение"];
                if (cloneParam == null && paramCoordsNN.Length != 0)
                {
                    var insertIndex = paramCoordsNN.Select(p =>
                        article.Template.GetIndex(p)).Min() - 1;
                    if (insertIndex < 0)
                        return false;
                    cloneParam = article.Template[insertIndex];
                }
                if (cloneParam == null)
                    return false;
                coordP = new TemplateParam
                {
                    Name = "Координаты",
                    Newline = !article.Template.HaveZeroNewlines(),
                    Sp1 = cloneParam.Sp1,
                    Sp2 = cloneParam.Sp2,
                    Sp3 = Math.Max(cloneParam.Sp3 + cloneParam.Name.Length - 10, 1),
                    Sp4 = cloneParam.Sp4,
                };
                article.Template.InsertAfter(cloneParam, coordP);
            }
            coordP.Value = coord;

            if (scale != null)
            {
                var scaleP = article.Template["CoordScale"];
                if (scaleP == null)
                {
                    scaleP = new TemplateParam
                    {
                        Name = "CoordScale",
                        Newline = !article.Template.HaveZeroNewlines(),
                        Sp1 = coordP.Sp1,
                        Sp2 = coordP.Sp2,
                        Sp3 = coordP.Sp3,
                        Sp4 = coordP.Sp4,
                        Value = scale.ToString()
                    };
                    article.Template.InsertAfter(coordP, scaleP);
                }
                else
                {
                    var svt = scaleP.Value.Trim();
                    if (svt.Length == 0)
                        scaleP.Value = scale.ToString();
                    else if (svt != scale.ToString())
                    {
                        l1.Add($"# [[{article.Title}]], 3.2");
                        return false;
                    }
                }
            }

            article.Template.Remove("region");
            article.Template.Remove("CoordAddon");
            foreach (var p in paramCoordsNN)
                article.Template.Remove(p.Name);

            if (coord == part2Coord)
                article.Priority = 2;
            else
                article.Priority = 3;

            return true;
        }

        bool ProcessCountry(Article article, List<string> l2)
        {
            bool countryChanged = false;
            var c1 = article.Template["Страна"];
            var c2 = article.Template["Страна2"];
            if (c2 != null)
            {
                var c2t = c2.Value.Trim();
                if (c2t.Length == 0)
                {
                    article.Template.Remove("Страна2");
                    countryChanged = true;
                }
                else if (c1 != null && c2t.ToLower() == "{{в крыму}}")
                {
                    c1.Value = "Россия-Украина";
                    article.Template.Remove("Страна2");
                    countryChanged = true;
                }
                else if (c1 != null && Regex.IsMatch(c2t,
                    "\\{\\{ *[Фф]лагификация *\\| *" +
                    Regex.Escape(c1.Value.Trim()) + " *\\}\\}"))
                {
                    article.Template.Remove("Страна2");
                    countryChanged = true;
                }
                else
                {

                }
            }

            var countryList = new string[] {
                    "Австрия", "Азербайджан", "Албания", "Алжир", "Англия", "Белоруссия", "Болгария",
                    "Бразилия", "Великобритания", "Германия", "Греция", "Грузия", "Египет", "Индия",
                    "Иран","Испания", "Италия", "Латвия", "Ливан", "Мексика", "Мьянма", "Намибия",
                    "Нидерланды", "Новая Зеландия", "Папская область", "Польша", "Португалия",
                    "Российская империя", "Россия", "Румыния", "Сербия", "Словакия", "Словения",
                    "Соединённые Штаты Америки", "США", "Тайвань", "Таиланд", "Туркмения",
                    "Узбекистан", "Украина", "Филиппины", "Финляндия", "Франция", "Чехия",
                    "Шотландия", "Эстония"
                };

            if (c1 != null)
            {
                var c1t = c1.Value.Trim();
                if (c1t.Length != 0)
                {
                    var m = Regex.Match(c1t, "^\\[\\[([а-яА-ЯёЁ ]+)\\]\\][,]?$");
                    if (m.Success && countryList.Contains(m.Groups[1].Value))
                    {
                        c1.Value = m.Groups[1].Value;
                        countryChanged = true;
                    }
                    else if (c1t[0] == '[')
                    {
                        l2.Add(c1t);
                    }
                }
            }
            if (countryChanged)
                article.Priority = 1;
            return countryChanged;
        }


        void ProcessArticles()
        {
            int processed = 0;
            int lexerErrors = 0;
            int parserErrors = 0;

            var articlesFlt = db.Articles.Where(a =>
                a.Status == ProcessStatus.NotProcessed ||
                a.Status == ProcessStatus.Skipped);

            var articles = articlesFlt.ToArray();
            //var articles = articlesFlt.Take(2000).ToArray();
            //var articles = articlesFlt.Where(a => a.Title == "Цистерцианский монастырь (Бохум)").ToArray();

            foreach (Article article in articles)
            {
                article.ReplIndex1 = 0;
                article.ReplIndex2 = 0;
                article.NewTemplateText = null;
                if (article.Status == ProcessStatus.Skipped)
                    article.Status = ProcessStatus.NotProcessed;
            }

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
                    new string[] { "Достопримечательность", "Монастырь", "Замок",
                    "Культовое сооружение", "Памятник", "Крепость", "Храм"}, null);
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
                if (article.Template == null)
                    article.Status = ProcessStatus.Skipped;

            foreach (Article article in articles.Where(a => a.Status != ProcessStatus.Skipped))
            {
                article.Template.Reformat();

                bool countryChanged = ProcessCountry(article, l2);
                bool coordsChanged = ProcessCoordinates(article, l1);

                if (coordsChanged || countryChanged)
                    article.NewTemplateText = article.Template.ToString();
                else
                    article.Status = ProcessStatus.Skipped;
            }
            Console.WriteLine(" Done");
            Console.WriteLine(" Replacements count: " + articles.Count(a => a.NewTemplateText != null));

            File.WriteAllLines("bad_coords.txt", l1);
            File.WriteAllLines("bad_country.txt", l2.OrderBy(e => e).ToArray());


            var sb1 = new StringBuilder();
            var sb2 = new StringBuilder();
            foreach (Article article in articles.Where(a => a.Status != ProcessStatus.Skipped))
            {
                sb1.Append($"Статья '{article.Title}', приоритет {article.Priority}:\n\n");
                sb1.Append(article.SrcWikiText.Substring(
                    article.ReplIndex1, article.ReplIndex2 - article.ReplIndex1));
                sb1.Append("\n\n\n");
                sb2.Append($"Статья '{article.Title}', приоритет {article.Priority}:\n\n");
                sb2.Append(article.NewTemplateText);
                sb2.Append("\n\n\n");
            }
            var sb3 = new StringBuilder();
            var sb4 = new StringBuilder();
            foreach (Article article in articles.Where(a => a.Status == ProcessStatus.Skipped))
            {
                sb3.Append($"Статья '{article.Title}':\n\n");
                if (article.Template != null)
                {
                    sb3.Append(article.SrcWikiText.Substring(
                        article.ReplIndex1, article.ReplIndex2 - article.ReplIndex1));
                }
                else
                {
                    sb3.Append("!!! Ошибка разбора !!!\n");
                    sb4.Append($"# [[{article.Title}]]\n");
                }
                sb3.Append("\n\n\n");
            }

            File.WriteAllText("src_templates.txt", sb1.ToString(), Encoding.UTF8);
            File.WriteAllText("new_templates.txt", sb2.ToString(), Encoding.UTF8);
            File.WriteAllText("skipped_templates.txt", sb3.ToString(), Encoding.UTF8);
            File.WriteAllText("bad_infoboxes.txt", sb4.ToString(), Encoding.UTF8);

            db.BeginTransaction();
            foreach (var article in articles)
                db.Update(article);
            db.CommitTransaction();
        }

        void MakeReplacements()
        {
            var tasks = new List<Task>();
            Console.Write("Making replacements");
            var articles = db.Articles.
                Where(a => a.Status == ProcessStatus.NotProcessed && a.NewTemplateText != null).
                OrderBy(a => a.Title).ToArray();

            foreach (var article in articles)
            {
                if (tasks.Any(t => t.IsFaulted))
                    Task.WaitAll(tasks.ToArray());
                tasks.RemoveAll(t => t.IsCompleted);

                string ReplWikiText =
                    article.SrcWikiText.Substring(0, article.ReplIndex1) +
                    article.NewTemplateText +
                    article.SrcWikiText.Substring(article.ReplIndex2);
                Task<bool> editTask = EditPage(csrfToken, article.Timestamp,
                    article.PageId, "унификация оформления", ReplWikiText);
                tasks.Add(editTask);
                tasks.Add(editTask.ContinueWith(cont =>
                {
                    bool isEditSuccessful = cont.Result;
                    lock (db)
                    {
                        Console.Write(isEditSuccessful ? '.' : 'x');
                        article.Status = isEditSuccessful ? ProcessStatus.Success : ProcessStatus.Failure;
                        db.Update(article);
                    }
                }));
                Thread.Sleep(60 * 1000 / editsPerMinute);
            }

            Console.WriteLine(" Done");
        }

        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");
            var ids = SearchTransclusions("Шаблон:Достопримечательность");
            DownloadArticles(ids.ToArray());
            ProcessArticles();
            ObtainEditToken();
            MakeReplacements();
        }

        static void Main(string[] args)
        {
            ServicePointManager.DefaultConnectionLimit = connectionsLimit;
            db = new Db();
            new Program();
            db.Dispose();
        }
    }
}
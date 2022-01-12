using Antlr4.Runtime;
using LinqToDB;
using LinqToDB.Mapping;
using Newtonsoft.Json.Linq;
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
        public string WdId;
        [Column()]
        public string Title;
        [Column()]
        public string SrcWikiText;

        public List<string> Errors;
        public Template Template;
    };

    [Table(Name = "Items")]
    class DbItem
    {
        [PrimaryKey]
        public string ItemId;
        [Column()]
        public int RevisionId;
        [Column()]
        public string Data;
    }

    class Mismatch
    {
        public string Title;
        public string WdId;
        public string TypeOut;
        public string Code1Out;
        public string Code2Out;
    }

    class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<Article> Articles { get { return GetTable<Article>(); } }
        public ITable<DbItem> Items { get { return GetTable<DbItem>(); } }
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
                    return null; // throw new Exception();
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

    static class ChunkExtension
    {
        public static IEnumerable<IEnumerable<T>> Chunks<T>(
            this IEnumerable<T> source, int chunkSize)
        {
            T[] items = new T[chunkSize];
            int count = 0;
            foreach (var item in source)
            {
                items[count] = item;
                count++;

                if (count == chunkSize)
                {
                    yield return items;
                    items = new T[chunkSize];
                    count = 0;
                }
            }
            if (count > 0)
            {
                if (count == chunkSize)
                    yield return items;
                else
                {
                    T[] tempItems = new T[count];
                    Array.Copy(items, tempItems, count);
                    yield return tempItems;
                }
            }
        }
    }

    class Program
    {
        MwApi wpApi;
        MwApi wdApi;
        static Db db;
        static Db db2;

        Dictionary<int, string> qToStr;
        char[] nowikifyChars;

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

        void FillWikidataIds()
        {
            Console.Write("Filling wikidata ids");

            foreach (var chunk in db.Articles.Select(a => a.PageId).ToArray().Chunks(500))
            {
                var wdIds = new Dictionary<int, string>();
                foreach (int pid in chunk)
                    wdIds.Add(pid, null);

                string xml = wpApi.PostRequest(
                    "action", "query",
                    "prop", "pageprops",
                    "ppprop", "wikibase_item",
                    "format", "xml",
                    "pageids", string.Join("|", chunk));

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                foreach (XmlNode pageNode in doc.SelectNodes("/api/query/pages/page/pageprops"))
                {
                    int articleId = int.Parse(pageNode.ParentNode.Attributes["pageid"].Value);
                    string wdId = pageNode.Attributes["wikibase_item"].Value;
                    wdIds[articleId] = wdId;
                }

                db.BeginTransaction();
                foreach (var kv in wdIds)
                {
                    // For some reason it is faster than .Set() method
                    db.Update(
                        new Article { PageId = kv.Key, WdId = kv.Value },
                        (a, b) => b.ColumnName == nameof(Article.WdId));
                }
                db.CommitTransaction();

                Console.Write('.');
            }
            Console.WriteLine(" Done");
        }

        void DownloadArticles(Dictionary<int, int> ids)
        {
            Console.Write("Preparing articles download...");
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
            Console.WriteLine(" Done");

            Console.Write("Downloading articles");
            foreach (var chunk in todl.Keys.OrderBy(x => x).Chunks(50))
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

        void DownloadItems(Dictionary<string, int> ids)
        {
            Console.Write("Preparing items download...");
            if (!HaveTable("Items"))
                db.CreateTable<DbItem>();

            var dbids = db.Items.ToDictionary(a => a.ItemId, a => a.RevisionId);


            var idsset = new HashSet<string>(ids.Keys);
            var dbidsset = new HashSet<string>(dbids.Keys);

            var deleted = dbidsset.Except(idsset).ToArray();
            var added = idsset.Except(dbidsset).ToArray();
            var existing = idsset.Intersect(dbidsset).ToArray();
            var changed = existing.Where(id => ids[id] != dbids[id]).ToArray();

            var todl = added.ToDictionary(id => id, id => true).Union(
                changed.ToDictionary(id => id, id => false)).ToDictionary(
                kv => kv.Key, kv => kv.Value);

            db.Items.Delete(i => deleted.Contains(i.ItemId));
            Console.WriteLine(" Done");

            Console.Write("Downloading items");
            foreach (var chunk in todl.Keys.OrderBy(x => x).Chunks(100))
            {
                for (;;)
                {
                    string json = wdApi.PostRequest(
                        "action", "wbgetentities",
                        "format", "json",
                        "props", "info|claims",
                        "ids", string.Join("|", chunk),
                        "redirects", "no");
                    Console.Write('.');

                    var obj = JObject.Parse(json);

                    if (obj["success"] == null)
                        continue; // T272319
                    if (obj.Count != 2)
                        throw new Exception();
                    if (!obj["entities"].All(t => t is JProperty))
                        throw new Exception();

                    db.BeginTransaction();
                    foreach (JToken t in obj["entities"])
                    {
                        var kv = t as JProperty;
                        var item = new DbItem
                        {
                            ItemId = kv.Name,
                            RevisionId = (int)kv.Value["lastrevid"],
                            Data = kv.Value.ToString()
                        };

                        if (todl[item.ItemId])
                            db.Insert(item);
                        else
                            db.Update(item);
                    }
                    db.CommitTransaction();
                    break;
                }
            }
            Console.WriteLine(" Done");
        }

        Dictionary<string, int> GetItemsRevisions()
        {
            Console.Write("Getting items revisions");

            var ids = new Dictionary<string, int>();
            string[] qids = db.Articles.
                Where(a => a.WdId != null).Select(a => a.WdId).ToArray();
            foreach (var chunk in qids.Chunks(500))
            {
                string xml = wdApi.PostRequest(
                    "action", "query",
                    "format", "xml",
                    "prop", "revisions",
                    "titles", string.Join("|", chunk));
                Console.Write('.');

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                foreach (XmlNode node in doc.SelectNodes("/api/query/pages/page"))
                {
                    string title = node.Attributes["title"].Value;
                    int revisionId = int.Parse(node.
                        SelectSingleNode("revisions/rev").Attributes["revid"].Value);
                    ids.Add(title, revisionId);
                }
            }
            Console.WriteLine(" Done");
            return ids;
        }

        string[] GetWdTypes(Item item)
        {
            var p31 = new PId(31);

            var claims = item.Claims;
            if (!claims.ContainsKey(p31))
                return null;

            var result = new List<string>();

            foreach (var c in claims[p31])
            {
                var typeId = c.MainSnak.DataValue.Value as QId;

                if (qToStr.ContainsKey(typeId.Id))
                    result.Add(qToStr[typeId.Id]);
                else
                    result.Add(typeId.ToString());
            }

            return result.ToArray();
        }

        string[] GetWdIds(Item item, PId p)
        {
            var claims = item.Claims;
            if (!claims.ContainsKey(p))
                return null;

            return claims[p].Select(c =>
                c.MainSnak.DataValue == null ?
                "—" : (string)c.MainSnak.DataValue.Value).ToArray();
        }

        string Nowikify(string s)
        {
            if (s.IndexOfAny(nowikifyChars) == -1)
                return s;
            else
                return $"<nowiki>{s}</nowiki>";
        }

        string NormalizeStatus(string status)
        {
            if (status == null)
                return null;

            string result = status.ToLower();

            result = result.Replace("поселок", "посёлок");
            result = Regex.Replace(result, "\\A(\\[\\[)([^\\]]+)(\\]\\])\\z", "$2");

            return result;
        }

        string NormalizeOKATO(string okato)
        {
            if (okato == null)
                return null;

            if (!Regex.IsMatch(okato, "\\A[0-9]+\\z"))
                return okato;

            if (okato.Length == 8)
                return $"{okato}000";
            else if (okato.Length == 5)
                return $"{okato}000000";
            else if (okato.Length == 2)
                return $"{okato}000000000";

            return okato;
        }


        void ProcessArticles()
        {
            int processed = 0;
            int lexerErrors = 0;
            int parserErrors = 0;

            var articles = db.Articles;
            //var articles = db.Articles.Take(1000);
            //var articles = db.Articles.Take(1000).Where(a => a.Title == "Мюнхен");

            nowikifyChars = new char[] { '\'', '<', '>', '[', ']', '{', '}' };

            //string[] blacklist = { "/", "{", ",", "(", "<" };

            var templNames = new string[]
            {
                "НП-Россия",
                "НП+Россия"
            };

            /*var templParams = new Dictionary<string, string>
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
            };*/

            qToStr = new Dictionary<int, string>
            {
                { 515, "город" },
                { 532, "село" },
                { 5084, "деревня" },
                { 39715, "маяк" },
                { 55488, "железнодорожная станция" },
                { 74047, "город-призрак" },
                { 123705, "нейборхуд" },
                { 183366, "территория" },
                { 188509, "пригород" },
                { 183342, "город федерального значения" /* в России */ },
                { 190107, "метеостанция" },
                { 192287, "административно-территориальная единица России" },
                { 211450, "закрытое административно-территориальное образование" },
                { 350895, "пустошь" },
                { 365627, "железнодорожная будка" },
                { 486972, "населённый пункт" },
                { 505774, "гидрологический пост" },
                { 604435, "микрорайон" },
                { 620509, "военный городок" },
                { 634099, "сельское поселение в России" },
                { 691960, "город-спутник" },
                { 748331, "станица" },
                { 771444, "аул" },
                { 902814, "граничный город" },
                { 1130491, "слобода" },
                { 1350536, "наукоград" },
                { 1434274, "урочище" },
                { 1549591, "город с населением более 100 000 человек" },
                { 1637706, "город-миллионер" },
                { 1782540, "посёлок станции" },
                { 2023000, "хутор" },
                { 2191999, "погост" },
                { 2514025, "посёлок" },
                { 2974842, "покинутый город" },
                { 2989457, "посёлок городского типа" },
                { 3257686, "обжитая местность" },
                { 3374262, "местечко" },
                { 4129217, "выселок" },
                { 4155473, "дачный посёлок" },
                { 4286337, "район города" },
                { 4375340, "починок" },
                { 4946461, "курорт" },
                { 5175541, "коттеджный посёлок" },
                { 7930989, "город" },
                { 10354598, "сельский/деревенский населённый пункт" },
                { 12116124, "кутан" },
                { 14616455, "разрушенный город" },
                { 15078955, "посёлок городского типа" /* России */ },
                { 15195406, "район города в России" },
                { 15243209, "исторический район" },
                { 16638537, "городок" },
                { 16652878, "заимка" },
                { 18632459, "улус" },
                { 18729324, "разъезд" /* (нас. пункт) */ },
                { 19953632, "бывшая административно-территориальная единица" },
                { 20019082, "рабочий посёлок" },
                { 21130185, "деревня в бывшем муниципалитете Финляндии" },
                { 21507948, "бывшая деревня" },
                { 22674925, "бывший населённый пункт" },
                { 24026248, "участок" },
                { 24258416, "железнодорожная станция" /* (нас. пункт) */ },
                { 27062006, "станция" },
                { 27254663, "посёлок железнодорожного разъезда" },
                { 27254666, "посёлок железнодорожной станции" },
                { 27295972, "посёлок железнодорожной платформы" },
                { 27517161, "курортный посёлок" /* в России */ },
                { 27517483, "железнодорожный разъезд" /* (нас. пункт) */ },
                { 27518087, "казарма" },
                { 27518287, "контрольный пункт связи" },
                { 27518260, "монтёрский пункт" },
                { 27518266, "метеостанция" },
                { 27518282, "гидрологический пост" },
                { 27518793, "железнодорожный блокпост" },
                { 27526294, "железнодорожный пост" },
                { 27531730, "кордон" },
                { 27532791, "железнодорожная будка" },
                { 27532818, "железнодорожная казарма" },
                { 27537502, "посёлок лесоучастка" },
                { 27566198, "остановочная платформа" },
                { 27577201, "железнодорожная платформа" },
                { 27587207, "муниципальный округ" },
                { 27587491, "городской посёлок" },
                { 27588300, "железнодорожная площадка" },
                { 27588331, "посёлок совхоза" },
                { 30892074, "вахтовый посёлок" },
                { 50330360, "второй по величине город" },
                { 51929311, "первый по величине город" },
                { 56580425, "город областного значения" }
            };
            var okatoP = new PId(721);
            var oktmoP = new PId(764);

            List<string> errorLog = new List<string>();
            List<Mismatch> mismatches = new List<Mismatch>();

            Console.Write("Processing articles");
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            Parallel.ForEach(articles, article =>
            {
                if (!Regex.IsMatch(article.SrcWikiText.ToLower(), "нп[+\\-]россия"))
                    return;

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
                WikiVisitor visitor = new WikiVisitor(article, templNames, null);
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

                if (article.Template == null)
                    return;

                if (article.WdId == null)
                    return;

                string itemData = null;
                lock (db2)
                    itemData = db2.Items.First(i => i.ItemId == article.WdId).Data;

                var item = new Item(itemData);
                var json2 = item.ToString();
                if (!JToken.DeepEquals(JObject.Parse(json2), JObject.Parse(itemData)))
                    throw new Exception();

                var mm = new Mismatch();
                mm.Title = article.Title;
                mm.WdId = article.WdId;

                var statusp = article.Template["статус"];
                string wpStatus = statusp == null ? "" : statusp.Value;

                string[] wdStatuses = GetWdTypes(item);

                if (wdStatuses == null)
                    mm.TypeOut = "{{color|red|Нет}}";
                else if (!wdStatuses.Contains(NormalizeStatus(wpStatus)))
                    mm.TypeOut = $"{Nowikify(wpStatus)}<br>{string.Join(", ", wdStatuses)}";

                var id1p = article.Template["цифровой идентификатор"];
                string id1 = id1p == null ? "" : id1p.Value;
                if (id1 != "")
                {
                    var wdOkatoL = GetWdIds(item, okatoP);
                    if (wdOkatoL != null && !wdOkatoL.Contains(id1) && !wdOkatoL.Contains(NormalizeOKATO(id1)))
                        mm.Code1Out = $"{Nowikify(id1)}<br>{string.Join(", ", wdOkatoL)}";
                }

                var id2p = article.Template["цифровой идентификатор 2"];
                string id2 = id2p == null ? "" : id2p.Value;
                if (id2 != "")
                {
                    var wdOktmoL = GetWdIds(item, oktmoP);
                    if (wdOktmoL != null && !wdOktmoL.Contains(id2))
                        mm.Code2Out = $"{Nowikify(id2)}<br>{string.Join(", ", wdOktmoL)}";
                }

                if (mm.TypeOut != null || mm.Code1Out != null || mm.Code2Out != null)
                    lock (mismatches)
                        mismatches.Add(mm);
            });
            stopwatch.Stop();

            Console.WriteLine(" Done");
            Console.WriteLine(" Parser errors: " + parserErrors);
            Console.WriteLine(" Lexer errors: " + lexerErrors);
            Console.WriteLine(" Processing time: " + stopwatch.Elapsed.TotalSeconds + " sec");

            File.WriteAllLines("error_log.txt", errorLog.ToArray(), Encoding.UTF8);

            var sb = new StringBuilder();

            sb.AppendLine("{|class=\"wide sortable\" style=\"table-layout: fixed;word-wrap:break-word\"");
            sb.AppendLine("!width=\"25em\"|№");
            sb.AppendLine("!Статья");
            sb.AppendLine("!width=\"90em\"|Элемент");
            sb.AppendLine("!Тип");
            sb.AppendLine("!width=\"100em\"|Код 1");
            sb.AppendLine("!width=\"100em\"|Код 2");
            mismatches = mismatches.OrderBy(mm => mm.Title).ToList();
            for (int i = 0; i < mismatches.Count; i++)
            {
                var mm = mismatches[i];
                sb.AppendLine("|-");
                sb.AppendLine($"|{i + 1}");
                sb.AppendLine($"|[[{mm.Title}]]");
                sb.AppendLine($"|[[:d:{mm.WdId}|{mm.WdId}]]");
                sb.AppendLine($"|{mm.TypeOut}");
                sb.AppendLine($"|{mm.Code1Out}");
                sb.AppendLine($"|{mm.Code2Out}");
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
            wdApi = new MwApi("www.wikidata.org");

            var ids = ScanCategory("Категория:Населённые пункты по алфавиту");
            DownloadArticles(ids);
            FillWikidataIds();
            DownloadItems(GetItemsRevisions());

            ProcessArticles();
        }

        static void Main(string[] args)
        {
            db = new Db();
            db2 = new Db();
            new Program();
            db2.Dispose();
            db.Dispose();
        }
    }
}
using Antlr4.Runtime;
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
    class Program
    {
        MwApi wpApi;
        string csrfToken;

        bool remoteMode = true;

        List<Article> articles;
        List<Replacement> replacements;


        int[] GetLintErrors(string errorCategory)
        {
            var idList = new List<int>();

            string lntfrom = "";
            Console.Write("Searching for lint errors...");
            for (;;)
            {
                string continueParam = "";
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "list", "linterrors",
                    "format", "xml",
                    "lntcategories", errorCategory,
                    "lntlimit", "500",
                    "lntnamespace", "0|14|100|104",
                    "lntfrom", lntfrom,
                    "continue", continueParam);
                Console.Write('.');

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                foreach (XmlNode pNode in doc.SelectNodes("/api/query/linterrors/_v"))
                {
                    int id = int.Parse(pNode.Attributes["pageid"].InnerText);
                    idList.Add(id);
                }

                XmlNode contNode = doc.SelectSingleNode("/api/continue");
                if (contNode == null)
                    break;
                lntfrom = contNode.Attributes["lntfrom"].InnerText;
                continueParam = contNode.Attributes["continue"].InnerText;
            }
            Console.WriteLine(" Done");
            return idList.ToArray();
        }

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
                article.WikiText = revNode.InnerText;
                if (revNode.Attributes["timestamp"] != null)
                    article.Timestamp = revNode.Attributes["timestamp"].Value;

                articles.Add(article);
            }

            return articles;
        }

        void DownloadArticles(int[] ids)
        {
            Console.Write("Downloading articles");
            articles = new List<Article>();
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
                articles.AddRange(DeserializeArticles(xml));
            }
            Console.WriteLine(" Done");
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

        bool EditPage(string csrfToken, string timestamp, int pageId, string summary, string text)
        {
            string xml = wpApi.PostRequest(
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

        class ParamsEqualityComparer : IEqualityComparer<string>
        {
            HashSet<string>[] dups;

            public ParamsEqualityComparer()
            {
                dups = new HashSet<string>[]
                {
                    new HashSet<string>() { "thumb", "thumbnail", "мини", "миниатюра" },
                    new HashSet<string>() { "frame", "framed", "обрамить" },
                    new HashSet<string>() { "frameless", "безрамки" },
                    new HashSet<string>() { "border", "граница" },
                    new HashSet<string>() { "left", "слева" },
                    new HashSet<string>() { "center", "центр" },
                    new HashSet<string>() { "right", "справа" }
                };
            }

            public bool Equals(string x, string y)
            {
                string trimX = x.Trim();
                string trimY = y.Trim();
                foreach (var dup in dups)
                    if (dup.Contains(trimX) && dup.Contains(trimY))
                        return true;
                return x == y;
            }

            public int GetHashCode(string obj)
            {
                string trimObj = obj.Trim();
                for (int i = 0; i < dups.Length; i++)
                    if (dups[i].Contains(trimObj))
                        return i;
                return obj.GetHashCode();
            }
        }

        string ParamReplace(string param)
        {
            string trimParam = param.Trim();
            string[] rightRepl = {"rıght", "ight", "rait", "righ", "riht", "rigt", "righy",
                "richt", "rihgt", "rihts", "rught", "wright", "write", "rihgt", "ritht",
                "rifgt", "rigrt", "rifht", "rightt", "tight", "rigjht", "rjght", "rigjt",
                "rught", "whrite", "raight", "rucht", "rght", "rigth", "rightt", "roght",
                "rignt", "righn", "rihht", "righr", "reght", "rechts", "lright", "righft", "righjt",
                "вправо", "право", "правее", "права", "праворуч", "кшпре", "срава", "яправа" };
            string[] thumbRepl = { "miniatur", "miniatyr", "mini", "міні", "thum", "thunb",
                "thoumb", "thumbs", "humb", "trumb", "tumb", "tumbs", "rhumb" };
            string[] leftRepl = { "leftt", "lrft", "ltft", "ліворуч", "лево", "зьлева", "левее" };
            if (rightRepl.Contains(trimParam))
                return "right";
            if (thumbRepl.Contains(trimParam))
                return "thumb";
            if (leftRepl.Contains(trimParam))
                return "left";

            string pxPattern1 = "^([0-9]{2,3}) *(пск|пикс|пркс|пк|пс|пх|п|зч|рх|pх|рx|PX|Px|xp|x|p|pix|pxl|pxL|pcx|pxt|pz|pt|ps|dpi)?$";
            string pxPattern2 = "^(пкс|пк|px|x) *([0-9]{2,3})$";
            string pxPattern3 = "^([0-9]{2,3}) +(px|пкс)$";
            if (Regex.IsMatch(trimParam, pxPattern1))
                return Regex.Replace(trimParam, pxPattern1, "$1px");
            else if (Regex.IsMatch(trimParam, pxPattern2))
                return Regex.Replace(trimParam, pxPattern2, "$2px");
            else if (Regex.IsMatch(trimParam, pxPattern3))
                return trimParam.Replace(" ", "");
            else
                return param;
        }

        void ParseArticles()
        {
            int processed = 0;
            int lexerErrors = 0;
            int parserErrors = 0;

            Console.Write("Parsing articles");
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            Parallel.ForEach(articles,
                new ParallelOptions { MaxDegreeOfParallelism = remoteMode ? 1 : -1 },
                article =>
            {
                AntlrErrorListener ael = new AntlrErrorListener();
                AntlrInputStream inputStream = new AntlrInputStream(article.WikiText);
                WikiLexer lexer = new WikiLexer(inputStream);
                lexer.RemoveErrorListeners();
                lexer.AddErrorListener(ael);
                CommonTokenStream commonTokenStream = new CommonTokenStream(lexer);
                WikiParser parser = new WikiParser(commonTokenStream);
                parser.RemoveErrorListeners();
                parser.AddErrorListener(ael);
                WikiParser.InitContext initContext = parser.init();
                WikiVisitor visitor = new WikiVisitor(article.PageId);
                visitor.VisitInit(initContext);
                article.Errors = ael.ErrorList;
                article.FileInvokes = visitor.FileInvokes;

                Interlocked.Add(ref lexerErrors, ael.LexerErrors);
                Interlocked.Add(ref parserErrors, ael.ParserErrors);

                if (Interlocked.Increment(ref processed) % 100 == 0)
                    Console.Write('.');
            });
            stopwatch.Stop();


            replacements = new List<Replacement>();

            var pec = new ParamsEqualityComparer();
            foreach (var article in articles)
            {
                if (article.FileInvokes.Count == 0)
                    continue;

                foreach (FileInvoke inv in article.FileInvokes)
                {
                    inv.Params.RemoveAll(p => string.IsNullOrWhiteSpace(p) || p.Trim() == "default");
                    inv.Params = inv.Params.Select(p => ParamReplace(p)).Reverse().Distinct(pec).Reverse().ToList();

                    Replacement r = new Replacement();
                    r.PageId = article.PageId;
                    r.SrcString = inv.Raw;
                    r.DstString = inv.ToString();
                    if (r.SrcString != r.DstString)
                        replacements.Add(r);
                }
            }

            Console.WriteLine(" Done");
            Console.WriteLine(" Articles: " + articles.Count);
            Console.WriteLine(" Invokes: " + articles.Sum(a => a.FileInvokes.Count()));
            Console.WriteLine(" Replacements: " + replacements.Count());
            Console.WriteLine(" Parser errors: " + parserErrors);
            Console.WriteLine(" Lexer errors: " + lexerErrors);
            Console.WriteLine(" Parsing time: " + stopwatch.Elapsed.TotalSeconds + " sec");

            if (remoteMode)
                return;


            var sb = new StringBuilder();

            sb.AppendLine("<small>");
            foreach (var r in replacements)
            {
                sb.AppendLine("[[" + articles.First(a => a.PageId == r.PageId).Title + "]]:");
                sb.AppendLine();
                sb.AppendLine("<code><nowiki>" + r.SrcString + "</nowiki></code>");
                sb.AppendLine();
                sb.AppendLine("<code><nowiki>" + r.DstString + "</nowiki></code>");
                sb.AppendLine();
                sb.AppendLine();
            }
            sb.AppendLine("</small>");

            File.WriteAllText("result.txt", sb.ToString());


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
        }

        void MakeReplacements(string csrfToken)
        {
            Console.Write("Making replacements");

            foreach (var article in articles.OrderBy(a => a.Title).ToArray())
            {
                var artReplacements = replacements.Where(
                    r => r.PageId == article.PageId).ToArray();
                if (artReplacements.Length == 0)
                    continue;

                string newText = article.WikiText;
                foreach (var artReplacement in artReplacements)
                    newText = newText.Replace(artReplacement.SrcString, artReplacement.DstString);

                bool isEditSuccessful = EditPage(csrfToken, article.Timestamp,
                    article.PageId, "исправление разметки", newText);
                Console.Write(isEditSuccessful ? '.' : 'x');
            }
            Console.WriteLine(" Done");
        }

        Program()
        {
            if (remoteMode)
                Console.WriteLine("Remote mode activated");

            wpApi = new MwApi("ru.wikipedia.org");
            var ids = GetLintErrors("bogus-image-options");
            DownloadArticles(ids);
            ParseArticles();
            ObtainEditToken();
            MakeReplacements(csrfToken);
        }

        static void Main(string[] args)
        {
            new Program();
        }
    }
}

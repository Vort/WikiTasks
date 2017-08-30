using Antlr4.Runtime;
using LinqToDB;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace WikiTasks
{
    static class OAuth
    {
        static OAuth()
        {
            var lines = File.ReadAllLines("auth.txt");
            if (lines.Length != 4)
                throw new Exception();
            ConsumerToken = lines[0];
            ConsumerSecret = lines[1];
            AccessToken = lines[2];
            AccessSecret = lines[3];
        }

        public static readonly string ConsumerToken;
        public static readonly string ConsumerSecret;
        public static readonly string AccessToken;
        public static readonly string AccessSecret;
    }

    public class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<Article> Articles { get { return GetTable<Article>(); } }
        public ITable<Replacement> Replacements { get { return GetTable<Replacement>(); } }
    }

    class Program
    {
        bool remoteMode = true;

        static Db db;

        WebClient wc;
        Random random;
        CookieContainer cookies;
        const string appName = "WikiTasks";
        const string wikiUrl = "https://ru.wikipedia.org";

        public static string UrlEncode(string s)
        {
            const string unreserved = "abcdefghijklmnopqrstuvwxyz" +
                "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.~";
            var sb = new StringBuilder();
            var bytes = Encoding.UTF8.GetBytes(s);
            foreach (byte b in bytes)
            {
                if (unreserved.Contains((char)b))
                    sb.Append((char)b);
                else
                    sb.Append($"%{b:X2}");
            }
            return sb.ToString();
        }

        string ApiPostRequest(params string[] postParameters)
        {
            if (postParameters.Length % 2 != 0)
                throw new Exception();

            var postParametersList = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < postParameters.Length / 2; i++)
            {
                postParametersList.Add(new KeyValuePair<string, string>(
                    postParameters[i * 2], postParameters[i * 2 + 1]));
            }
            postParametersList.Add(new KeyValuePair<string, string>("maxlag", "1"));

            var postBody = string.Join("&", postParametersList.Select(
                p => UrlEncode(p.Key) + "=" + UrlEncode(p.Value)));

            byte[] gzb = null;
            for (;;)
            {
                var headerParams = new Dictionary<string, string>();
                headerParams["oauth_consumer_key"] = OAuth.ConsumerToken;
                headerParams["oauth_token"] = OAuth.AccessToken;
                headerParams["oauth_signature_method"] = "HMAC-SHA1";
                headerParams["oauth_timestamp"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
                headerParams["oauth_version"] = "1.0";

                var nonce = new StringBuilder();
                for (int i = 0; i < 32; i++)
                    nonce.Append((char)(random.Next(26) + 'a'));
                headerParams["oauth_nonce"] = nonce.ToString();

                var allParams = headerParams.Union(postParametersList).OrderBy(p => p.Key).ToArray();
                var allParamsJoined = string.Join("&", allParams.Select(
                    p => UrlEncode(p.Key) + "=" + UrlEncode(p.Value)));

                string url = wikiUrl + "/w/api.php";
                string signatureBase = string.Join("&", new string[] { "POST", url, allParamsJoined }.
                    Select(s => UrlEncode(s)));

                string signature = Convert.ToBase64String(new HMACSHA1(
                    Encoding.ASCII.GetBytes(OAuth.ConsumerSecret + "&" + OAuth.AccessSecret)).
                    ComputeHash(Encoding.ASCII.GetBytes(signatureBase)));
                headerParams["oauth_signature"] = signature;

                string oauthHeader = "OAuth " + string.Join(",",
                    headerParams.Select(p => UrlEncode(p.Key) + "=" + UrlEncode(p.Value)));
                wc.Headers["Authorization"] = oauthHeader;

                wc.Headers["Accept-Encoding"] = "gzip";
                wc.Headers["Content-Type"] = "application/x-www-form-urlencoded";
                wc.Headers["User-Agent"] = appName;

                gzb = wc.UploadData(url, Encoding.ASCII.GetBytes(postBody.ToString()));
                if (wc.ResponseHeaders["Retry-After"] != null)
                {
                    int retrySec = int.Parse(wc.ResponseHeaders["Retry-After"]);
                    Thread.Sleep(retrySec * 1000);
                }
                else
                    break;
            }
            if (wc.ResponseHeaders["Set-Cookie"] != null)
            {
                var apiUri = new Uri(wikiUrl);
                cookies.SetCookies(apiUri, wc.ResponseHeaders["Set-Cookie"]);
                wc.Headers["Cookie"] = cookies.GetCookieHeader(apiUri);
            }
            GZipStream gzs = new GZipStream(
                new MemoryStream(gzb), CompressionMode.Decompress);
            MemoryStream xmls = new MemoryStream();
            gzs.CopyTo(xmls);
            byte[] xmlb = xmls.ToArray();
            string xml = Encoding.UTF8.GetString(xmlb);
            return xml;
        }

        void GetLintErrors(string errorCategory)
        {
            db.CreateTable<Article>();

            string lntfrom = "";
            Console.Write("Searching for lint errors...");
            for (;;)
            {
                string continueParam = "";
                string xml = ApiPostRequest(
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

                db.BeginTransaction();
                foreach (XmlNode pNode in doc.SelectNodes("/api/query/linterrors/_v"))
                {
                    int id = int.Parse(pNode.Attributes["pageid"].InnerText);
                    db.InsertOrReplace(new Article() { PageId = id });
                }
                db.CommitTransaction();

                XmlNode contNode = doc.SelectSingleNode("/api/continue");
                if (contNode == null)
                    break;
                lntfrom = contNode.Attributes["lntfrom"].InnerText;
                continueParam = contNode.Attributes["continue"].InnerText;
            }
            Console.WriteLine(" Done");
        }

        List<List<int>> SplitToChunks(List<int> ids, int chunkSize)
        {
            int chunkCount = (ids.Count + chunkSize - 1) / chunkSize;

            List<List<int>> chunks = new List<List<int>>();
            for (int i = 0; i < chunkCount; i++)
            {
                List<int> chunk = new List<int>();
                for (int j = 0; j < chunkSize; j++)
                {
                    int k = i * chunkSize + j;
                    if (k >= ids.Count)
                        break;
                    chunk.Add(ids[k]);
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
                article.PageId = int.Parse(pageNode.Attributes["pageid"].InnerText);

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
                db.Articles.Select(a => a.PageId).ToList(), 50);
            foreach (List<int> chunk in updChunks)
            {
                string idss = string.Join("|", chunk);
                string xml = ApiPostRequest(
                    "action", "query",
                    "prop", "revisions",
                    "rvprop", "timestamp|content",
                    "format", "xml",
                    "pageids", idss);
                Console.Write('.');

                List<Article> articles = GetArticles(xml);
                db.BeginTransaction();
                foreach (Article a in articles)
                    db.Update(a);
                db.CommitTransaction();
            }
            Console.WriteLine(" Done");
        }

        string GetToken(string type)
        {
            string xml = ApiPostRequest(
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

        bool EditPage(string csrfToken, string timestamp, int pageId, string summary, string text)
        {
            string xml = ApiPostRequest(
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

        static bool HaveTable(string name)
        {
            return db.DataProvider.GetSchemaProvider().
                GetSchema(db).Tables.Any(t => t.TableName == name);
        }

        void ParseArticles()
        {
            int processed = 0;
            int lexerErrors = 0;
            int parserErrors = 0;

            var articles = db.Articles.ToList();

            Console.Write("Parsing articles");
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            Parallel.ForEach(articles, article =>
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

                if (Interlocked.Increment(ref processed) % 50 == 0)
                    Console.Write('.');
            });
            stopwatch.Stop();


            if (HaveTable("Replacements"))
                db.DropTable<Replacement>();
            db.CreateTable<Replacement>();
            int id = 0;
            db.BeginTransaction();
            foreach (var article in articles)
            {
                if (article.FileInvokes.Count == 0)
                    continue;

                foreach (FileInvoke inv in article.FileInvokes)
                {
                    inv.Params.RemoveAll(p => string.IsNullOrWhiteSpace(p) || p.Trim() == "default");
                    inv.Params = inv.Params.Select(p => ParamReplace(p)).Reverse().Distinct().Reverse().ToList();

                    Replacement r = new Replacement();
                    r.PageId = article.PageId;
                    r.SrcString = inv.Raw;
                    r.DstString = inv.ToString();
                    r.Id = id;
                    id++;
                    if (r.SrcString != r.DstString)
                        db.Insert(r);
                }
            }
            db.CommitTransaction();

            Console.WriteLine(" Done");
            Console.WriteLine(" Articles: " + articles.Count);
            Console.WriteLine(" Invokes: " + articles.Sum(a => a.FileInvokes.Count()));
            Console.WriteLine(" Replacements: " + db.Replacements.Count());
            Console.WriteLine(" Parser errors: " + parserErrors);
            Console.WriteLine(" Lexer errors: " + lexerErrors);
            Console.WriteLine(" Parsing time: " + stopwatch.Elapsed.TotalSeconds + " sec");

            if (remoteMode)
                return;


            var sb = new StringBuilder();

            sb.AppendLine("<small>");
            foreach (var r in db.Replacements)
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

        string ParamReplace(string param)
        {
            string trimParam = param.Trim();
            string[] rightRepl = {"rıght", "ight", "rait", "righ", "riht", "rigt",
                "richt", "rihgt", "rihts", "rught", "wright", "write", "rihgt", "ritht",
                "rifgt", "rigrt", "rifht", "rightt", "tight", "rigjht", "rjght", "rigjt",
                "rught", "whrite", "raight", "rucht", "rght", "rigth", "rightt", "roght",
                "rignt", "righn", "rihht", "righr", "lright", "righft", "righjt",
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

            string pxPattern1 = "^([0-9]{2,3}) *(пск|пркс|пс|пх|п|зч|рх|PX|Px|xp|x|p|pix|pxl|pxL|pcx|pxt|pz|pt|ps|dpi)?$";
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

        void MakeReplacements(string csrfToken)
        {
            Console.Write("Making replacements");

            var articles = db.Articles.OrderByDescending(a => a.PageId).ToArray();
            foreach (var article in articles)
            {
                var replacements = db.Replacements.Where(
                    r => r.PageId == article.PageId && r.Status == 0).ToArray();
                if (replacements.Length == 0)
                    continue;

                string newText = article.WikiText;
                foreach (var replacement in replacements)
                    newText = newText.Replace(replacement.SrcString, replacement.DstString);

                bool isEditSuccessful = EditPage(csrfToken, article.Timestamp,
                    article.PageId, "исправление разметки ([[ВП:РДБ#Bogus file options]])", newText);

                db.BeginTransaction();
                foreach (var replacement in replacements)
                {
                    replacement.Status = isEditSuccessful ? (byte)1 : (byte)2;
                    db.Update(replacement);
                }
                db.CommitTransaction();
                Console.Write(isEditSuccessful ? '.' : 'x');
            }
            Console.WriteLine(" Done");
        }

        Program()
        {
            if (remoteMode)
                Console.WriteLine("Remote mode enabled");

            wc = new WebClient();
            cookies = new CookieContainer();
            random = new Random();

            Console.Write("Authenticating...");
            string csrfToken = GetToken("csrf");
            if (csrfToken == null)
            {
                Console.WriteLine(" Failed");
                return;
            }
            Console.WriteLine(" Done");

            Console.WriteLine($"[{DateTime.Now}] Task is starting");
            if (remoteMode)
            {
                if (HaveTable("Articles"))
                    db.DropTable<Article>();
                if (HaveTable("Replacements"))
                    db.DropTable<Replacement>();
            }

            if (!HaveTable("Articles"))
            {
                GetLintErrors("bogus-image-options");
                DownloadArticles();
            }

            ParseArticles();
            MakeReplacements(csrfToken);

            Console.WriteLine($"[{DateTime.Now}] Task is finished");
        }

        static void Main(string[] args)
        {
            db = new Db();
            new Program();
            db.Dispose();
        }
    }
}

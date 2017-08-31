using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

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

    class WpApi
    {
        WebClient wc;
        Random random;
        CookieContainer cookies;
        const string appName = "WikiTasks";
        const string wikiUrl = "https://ru.wikipedia.org";

        public WpApi()
        {
            wc = new WebClient();
            cookies = new CookieContainer();
            random = new Random();
        }

        static string UrlEncode(string s)
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

        public string PostRequest(params string[] postParameters)
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
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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


    class MwApi : Api
    {
        public class FileInfo
        {
            public byte[] Data;
            public string Name;
        }

        Random random;
        HttpClient hc;
        readonly string apiUrl;
        const string appName = "WikiTasks";

        public MwApi(string site)
        {
            random = new Random();
            hc = new HttpClient();
            hc.DefaultRequestHeaders.Add("User-Agent", appName);
            hc.DefaultRequestHeaders.Add("Accept-Encoding", "gzip");
            apiUrl = $"https://{site}/w/api.php";
        }

        public string PostRequest(params object[] postParameters)
        {
            return PostRequestAsync(postParameters).Result;
        }

        public async Task<string> PostRequestAsync(params object[] postParameters)
        {
            if (postParameters.Length % 2 != 0)
                throw new Exception();

            for (;;)
            {
                var postData = new MultipartFormDataContent();
                for (int i = 0; i < postParameters.Length / 2; i++)
                {
                    string key = postParameters[i * 2] as string;
                    object value = postParameters[i * 2 + 1];
                    string valueString = value as string;
                    FileInfo valueFileInfo = value as FileInfo;
                    if (key == null)
                        throw new Exception();
                    if (valueString == null && valueFileInfo == null)
                        throw new Exception();
                    if (valueString != null)
                        postData.Add(new StringContent(value as string), key);
                    if (valueFileInfo != null)
                        postData.Add(new ByteArrayContent(valueFileInfo.Data), key, valueFileInfo.Name);
                }
                postData.Add(new StringContent("1"), "maxlag");

                var headerParams = new Dictionary<string, string>();
                headerParams["oauth_consumer_key"] = OAuth.ConsumerToken;
                headerParams["oauth_token"] = OAuth.AccessToken;
                headerParams["oauth_signature_method"] = "HMAC-SHA1";
                headerParams["oauth_timestamp"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
                headerParams["oauth_version"] = "1.0";

                var nonce = new StringBuilder();
                lock (random)
                {
                    for (int i = 0; i < 32; i++)
                        nonce.Append((char)(random.Next(26) + 'a'));
                }
                headerParams["oauth_nonce"] = nonce.ToString();

                var headerParamsJoined = string.Join("&", headerParams.OrderBy(p => p.Key).Select(
                    p => UrlEncode(p.Key) + "=" + UrlEncode(p.Value)));

                string signatureBase = string.Join("&",
                    new string[] { "POST", apiUrl, headerParamsJoined }.
                    Select(s => UrlEncode(s)));

                string signature = Convert.ToBase64String(new HMACSHA1(
                    Encoding.ASCII.GetBytes(OAuth.ConsumerSecret + "&" + OAuth.AccessSecret)).
                    ComputeHash(Encoding.ASCII.GetBytes(signatureBase)));
                headerParams["oauth_signature"] = signature;

                string oauthHeader = "OAuth " + string.Join(",",
                    headerParams.Select(p => UrlEncode(p.Key) + "=" + UrlEncode(p.Value)));

                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
                request.Headers.Add("Authorization", oauthHeader);
                request.Content = postData;

                var response = await hc.SendAsync(request);
                if (response.Headers.RetryAfter != null)
                    Thread.Sleep((int)response.Headers.RetryAfter.Delta.Value.TotalMilliseconds);
                else
                    return GZipUnpack(await response.Content.ReadAsByteArrayAsync());
            }
        }
    }
}

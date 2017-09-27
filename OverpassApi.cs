using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace WikiTasks
{
    [System.ComponentModel.DesignerCategory("Code")]
    class LowTimeoutWebClient : WebClient
    {
        protected override WebRequest GetWebRequest(Uri uri)
        {
            WebRequest w = base.GetWebRequest(uri);
            w.Timeout = 1000;
            return w;
        }
    }

    class OverpassApi : Api
    {
        const string overpassAddress = "http://overpass-api.de/api";

        public static void Requests(string[] requests, Action<string> action)
        {
            var ltwc = new LowTimeoutWebClient();
            var reqStack = new Stack<byte[]>(
                requests.Select(r => Encoding.UTF8.GetBytes(r)));

            int rateLimit = 1;
            int requestsLeft = requests.Length;
            Exception taskException = null;

            for (;;)
            {
                if (requestsLeft == 0)
                    break;
                if (taskException != null)
                    ExceptionDispatchInfo.Capture(taskException).Throw();

                string status = null;
                while (status == null)
                {
                    try
                    {
                        status = ltwc.DownloadString(overpassAddress + "/status");
                    }
                    catch (WebException)
                    {
                        Thread.Sleep(500);
                    }
                }

                rateLimit = int.Parse(Regex.Match(status, "Rate limit: ([0-9]+)").Groups[1].Value);

                var match = Regex.Match(status, "([0-9]+) slots available now");
                if (match.Success)
                {
                    int freeSlotCount = int.Parse(match.Groups[1].Value);
                    for (int i = 0; i < freeSlotCount; i++)
                    {
                        byte[] request;
                        lock (reqStack)
                        {
                            if (reqStack.Count == 0)
                                break;
                            request = reqStack.Pop();
                        }

                        WebClient wc = new WebClient();
                        wc.Headers["Accept-Encoding"] = "gzip";
                        var task = wc.UploadDataTaskAsync(
                            overpassAddress + "/interpreter", request);
                        task.ContinueWith(cont =>
                        {
                            try
                            {
                                if (cont.IsFaulted)
                                {
                                    if (cont.Exception.InnerException is WebException)
                                    {
                                        lock (reqStack)
                                            reqStack.Push(request);
                                    }
                                    else
                                        taskException = cont.Exception.InnerException;
                                }
                                else
                                {
                                    action(GZipUnpack(cont.Result));
                                    Interlocked.Decrement(ref requestsLeft);
                                }
                            }
                            catch (Exception e)
                            {
                                taskException = e;
                            }
                        });
                    }
                    Thread.Sleep(1000);
                }
                else
                {
                    var matches = Regex.Matches(status, ", in (-?[0-9]+) seconds.");
                    int waitSec = 1;
                    if (matches.Count == rateLimit)
                        waitSec = matches.Cast<Match>().Min(m => int.Parse(m.Groups[1].Value));
                    if (waitSec <= 0)
                        Thread.Sleep(500);
                    else
                        Thread.Sleep(waitSec * 1000);
                }
            }
        }

        public static string Request(string request)
        {
            string result = null;
            var returnEvent = new ManualResetEvent(false);
            Requests(new string[] { request }, r =>
            {
                result = r;
                returnEvent.Set();
            });
            returnEvent.WaitOne();
            return result;
        }
    }
}

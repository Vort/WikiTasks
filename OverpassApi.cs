using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace WikiTasks
{
    class OverpassApi : Api
    {
        public static string Request(string request)
        {
            var ltwc = new LowTimeoutWebClient();
            string overpassAddress = "http://overpass-api.de/api";
            byte[] gzb = null;
            while (gzb == null)
            {
                for (;;)
                {
                    string status = null;
                    while (status == null)
                    {
                        try
                        {
                            status = ltwc.DownloadString(
                                overpassAddress + "/status");
                        }
                        catch (WebException)
                        {
                            Thread.Sleep(500);
                        }
                    }
                    if (status.Contains("available now"))
                        break;
                    var matches = Regex.Matches(status, ", in (-?[0-9])+ seconds.");
                    int waitSec = 1;
                    if (matches.Count != 0)
                        waitSec = matches.Cast<Match>().Min(m => int.Parse(m.Groups[1].Value));
                    if (waitSec <= 0)
                        Thread.Sleep(500);
                    else
                        Thread.Sleep(waitSec * 1000);
                }
                try
                {
                    WebClient wc = new WebClient();
                    wc.Headers["Accept-Encoding"] = "gzip";
                    gzb = wc.UploadData(
                        overpassAddress + "/interpreter",
                        Encoding.UTF8.GetBytes(request));
                }
                catch (WebException)
                {
                    Thread.Sleep(1000);
                }
            }
            return GZipUnpack(gzb);
        }
    }
}

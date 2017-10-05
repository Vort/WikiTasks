using LinqToDB;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace WikiTasks
{
    public enum ProcessStatus
    {
        NotProcessed = 0,
        Success = 1,
        NoHigherResolution = 2,
        NoFlickrLink = 3,
        OriginalNotFound = 4,
        ImagesNotEqual = 5,
        CommonsSizeMismatch = 6,
        FlickrErrorUnknown = 7,
        FlickrErrorNotFound = 8
    }

    [Table(Name = "Images")]
    public class Image
    {
        [PrimaryKey]
        public int PageId;
        [Column()]
        public string Title;
        [Column()]
        public string CommonsUrl;
        [Column()]
        public bool SingleRev;
        [Column()]
        public int Size;
        [Column()]
        public int Width;
        [Column()]
        public int Height;
        [Column()]
        public string Comment;
        [Column()]
        public ProcessStatus Status;
        [Column()]
        public double MaxDeltaE;
        [Column()]
        public int OriginalWidth;
        [Column()]
        public int OriginalHeight;
    };
    
    public class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<Image> Images { get { return GetTable<Image>(); } }
    }

    class Delay
    {
        int msecDuration;
        DateTime waitTill;

        public Delay(int msecDuration)
        {
            this.msecDuration = msecDuration;
        }

        public void Wait()
        {
            int delay = (int)(waitTill - DateTime.Now).TotalMilliseconds;
            if (delay > 0)
                Thread.Sleep(delay);
            waitTill = DateTime.Now.AddMilliseconds(msecDuration);
        }
    }

    class Program
    {
        MwApi api;
        static Db db;
        string flickrKey;

        Delay commonsDelay;
        Delay flickrDelay;

        string csrfToken;

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

        string GetToken(string type)
        {
            string xml = api.PostRequest(
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

        void ObtainEditToken()
        {
            Console.Write("Authenticating...");
            csrfToken = GetToken("csrf");
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

        void GetImageList()
        {
            if (HaveTable("Images"))
                db.DropTable<Image>();
            db.CreateTable<Image>();
            
            string category = "Category:Flickr images uploaded by Flickr upload bot";

            string continueParam = "";
            Console.Write("Scanning category...");
            for (;;)
            {
                string xml = api.PostRequest(
                    "action", "query",
                    "list", "categorymembers",
                    "format", "xml",
                    "cmprop", "ids",
                    "cmtitle", category,
                    "cmlimit", "500",
                    "cmcontinue", continueParam);
                Console.Write('.');

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                db.BeginTransaction();
                foreach (XmlNode pNode in doc.SelectNodes("/api/query/categorymembers/cm"))
                {
                    int id = int.Parse(pNode.Attributes["pageid"].InnerText);
                    db.Insert(new Image() { PageId = id });
                }
                db.CommitTransaction();

                XmlNode contNode = doc.SelectSingleNode("/api/continue");
                if (contNode == null)
                    break;
                continueParam = contNode.Attributes["cmcontinue"].InnerText;
            }
            Console.WriteLine(" Done");
        }

        List<Image> ParseImageInfos(string xml)
        {
            List<Image> images = new List<Image>();

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);

            foreach (XmlNode pageNode in doc.SelectNodes("/api/query/pages/page"))
            {
                if (pageNode.Attributes["missing"] != null)
                    continue;

                Image image = new Image();
                image.Title = pageNode.Attributes["title"].InnerText;
                image.PageId = int.Parse(pageNode.Attributes["pageid"].InnerText);

                XmlNodeList iiNodes = pageNode.SelectNodes("imageinfo/ii");
                image.SingleRev = iiNodes.Count == 1;
                if (image.SingleRev)
                {
                    var iiNode = iiNodes.Item(0);
                    image.Size = int.Parse(iiNode.Attributes["size"].InnerText);
                    image.Width = int.Parse(iiNode.Attributes["width"].InnerText);
                    image.Height = int.Parse(iiNode.Attributes["height"].InnerText);
                    image.Comment = iiNode.Attributes["comment"].InnerText;
                    image.CommonsUrl = iiNode.Attributes["url"].InnerText;
                }

                images.Add(image);
            }

            return images;
        }

        void GetImageInfo()
        {
            if (!HaveTable("Images"))
                throw new Exception();

            Console.Write("Retrieving image info");
            var updChunks = SplitToChunks(
                db.Images.Where(i => i.Title == null).Select(i => i.PageId).ToList(), 50);
            foreach (List<int> chunk in updChunks)
            {
                string idss = string.Join("|", chunk);
                string xml = api.PostRequest(
                    "action", "query",
                    "prop", "imageinfo",
                    "iiprop", "size|comment|url",
                    "iilimit", "2",
                    "format", "xml",
                    "pageids", idss);
                Console.Write('.');

                List<Image> images = ParseImageInfos(xml);
                db.BeginTransaction();
                foreach (Image i in images)
                    db.Update(i);
                db.CommitTransaction();
            }
            Console.WriteLine(" Done");
        }

        ProcessStatus ProcessImageEntry(Image image)
        {
            var m = Regex.Match(image.Comment, "/([0-9]+) using ");
            if (!m.Success)
                return ProcessStatus.NoFlickrLink;

            long id = long.Parse(m.Groups[1].Value);
            WebClient wc = new WebClient();
            flickrDelay.Wait();
            string xml = wc.DownloadString(
                "https://api.flickr.com/services/rest/" +
                "?method=flickr.photos.getSizes" +
                "&api_key=" + flickrKey +
                "&photo_id=" + id);
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xml);
            var rspNode = doc.SelectSingleNode("/rsp");
            if (rspNode.Attributes["stat"].InnerText == "fail")
            {
                var errNode = rspNode.SelectSingleNode("err");
                int errCode = int.Parse(errNode.Attributes["code"].InnerText);
                if (errCode == 1)
                    return ProcessStatus.FlickrErrorNotFound;
                else
                    return ProcessStatus.FlickrErrorUnknown;
            }

            foreach (XmlNode sizeNode in doc.SelectNodes("/rsp/sizes/size"))
            {
                if (sizeNode.Attributes["label"].InnerText != "Original")
                    continue;

                int width = int.Parse(sizeNode.Attributes["width"].InnerText);
                int height = int.Parse(sizeNode.Attributes["height"].InnerText);
                string source = sizeNode.Attributes["source"].InnerText;
                image.OriginalWidth = width;
                image.OriginalHeight = height;
                if (width <= image.Width || height <= image.Height)
                    return ProcessStatus.NoHigherResolution;

                byte[] commonsFileData = wc.DownloadData(image.CommonsUrl);
                flickrDelay.Wait();
                byte[] flickrFileData = wc.DownloadData(source);

                var cms = new MemoryStream(commonsFileData);
                var ci = System.Drawing.Image.FromStream(cms);
                ExifRotate.RotateImageByExifOrientationData(ci);
                if (commonsFileData.Length != image.Size ||
                    ci.Width != image.Width || ci.Height != image.Height)
                {
                    return ProcessStatus.CommonsSizeMismatch;
                }

                var fms = new MemoryStream(flickrFileData);
                var fi = System.Drawing.Image.FromStream(fms);
                ExifRotate.RotateImageByExifOrientationData(fi);

                var rfi = ImageDiff.ResizeImage(fi, ci.Width, ci.Height);
                double maxDeltaE = ImageDiff.GetMaxDeltaE((Bitmap)ci, rfi);

                image.MaxDeltaE = Math.Round(maxDeltaE, 2);
                if (maxDeltaE > 45.0)
                {
                    File.WriteAllBytes($"images\\{image.PageId}_c.jpeg", commonsFileData);
                    File.WriteAllBytes($"images\\{image.PageId}_f.jpeg", flickrFileData);
                    return ProcessStatus.ImagesNotEqual;
                }

                string fileName = image.Title;
                if (!fileName.StartsWith("File:"))
                    throw new Exception();
                fileName = fileName.Substring(5);

                commonsDelay.Wait();
                string result = api.PostRequest(
                    "action", "upload",
                    "ignorewarnings", "true",
                    "filename", fileName,
                    "filesize", flickrFileData.Length.ToString(),
                    "file", new MwApi.FileInfo { Data = flickrFileData, Name = fileName },
                    "comment", "better quality",
                    "token", csrfToken,
                    "format", "xml");
                return ProcessStatus.Success;
            }

            return ProcessStatus.OriginalNotFound;
        }

        void CheckAndReplace()
        {
            if (!Directory.Exists("images"))
                Directory.CreateDirectory("images");

            if (!HaveTable("Images"))
                throw new Exception();

            Console.Write("Checking and replacing...");

            flickrKey = File.ReadAllText("flickr_key.txt");

            commonsDelay = new Delay(5000);
            flickrDelay = new Delay(1000);

            var images = db.Images.Where(i => i.SingleRev &&
                i.Status == ProcessStatus.NotProcessed).ToArray();
            foreach (var image in images)
            {
                char statusChar = 'x';
                var statusCode = ProcessImageEntry(image);
                switch (statusCode)
                {
                    case ProcessStatus.Success:
                        statusChar = '+';
                        break;
                    case ProcessStatus.NoHigherResolution:
                        statusChar = '-';
                        break;
                    case ProcessStatus.ImagesNotEqual:
                        statusChar = '!';
                        break;
                }
                image.Status = statusCode;
                db.Update(image);
                Console.Write(statusChar);
            }

            Console.WriteLine(" Done");
        }

        Program()
        {
            api = new MwApi("commons.wikimedia.org");
            ObtainEditToken();
            GetImageList();
            GetImageInfo();
            CheckAndReplace();
        }

        static void Main(string[] args)
        {
            db = new Db();
            new Program();
            db.Dispose();
        }
    }
}
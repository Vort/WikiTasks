using ImageDiff;
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
        NoHighResolution = 2,
        NoFlickrLink = 3,
        OriginalNotFound = 4,
        ImagesNotEqual = 5,
        CommonsSizeMismatch = 6
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
        public int OriginalWidth;
        [Column()]
        public int OriginalHeight;
    };
    
    public class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<Image> Images { get { return GetTable<Image>(); } }
    }

    class Program
    {
        WpApi api;
        static Db db;

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
                    "cmlimit", "100",
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
                db.Images.Select(a => a.PageId).ToList(), 50);
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

        void CheckAndReplace()
        {
            if (!Directory.Exists("images"))
                Directory.CreateDirectory("images");

            if (!HaveTable("Images"))
                throw new Exception();

            Console.Write("Checking and replacing");
            var images = db.Images.Where(i => i.SingleRev &&
                i.Status == ProcessStatus.NotProcessed).ToArray();

            WebClient wc = new WebClient();

            foreach (var i in images)
            {
                var m = Regex.Match(i.Comment, "/([0-9]+) using ");
                if (m.Success)
                {
                    long id = long.Parse(m.Groups[1].Value);
                    string xml = wc.DownloadString(
                        "https://api.flickr.com/services/rest/" +
                        "?method=flickr.photos.getSizes" +
                        "&api_key=12d6c742e9da7839c6bc0571bb614071" +
                        "&photo_id=" + id);
                    XmlDocument doc = new XmlDocument();
                    doc.LoadXml(xml);
                    foreach (XmlNode sizeNode in doc.SelectNodes("/rsp/sizes/size"))
                    {
                        if (sizeNode.Attributes["label"].InnerText == "Original")
                        {
                            int width = int.Parse(sizeNode.Attributes["width"].InnerText);
                            int height = int.Parse(sizeNode.Attributes["height"].InnerText);
                            string source = sizeNode.Attributes["source"].InnerText;
                            i.OriginalWidth = width;
                            i.OriginalHeight = height;
                            if (width > i.Width && height > i.Height)
                            {
                                byte[] commonsFileData = wc.DownloadData(i.CommonsUrl);
                                Thread.Sleep(1000);
                                byte[] flickrFileData = wc.DownloadData(source);

                                var bc = new BitmapComparer(new CompareOptions
                                {
                                    AnalyzerType = AnalyzerTypes.CIE76,
                                    JustNoticeableDifference = 6
                                });

                                var cms = new MemoryStream(commonsFileData);
                                var fms = new MemoryStream(flickrFileData);
                                var ci = System.Drawing.Image.FromStream(cms);
                                var fi = System.Drawing.Image.FromStream(fms);
                                ExifRotate.RotateImageByExifOrientationData(ci);
                                if (ci.Width == i.Width && ci.Height == i.Height)
                                {
                                    ExifRotate.RotateImageByExifOrientationData(fi);
                                    var rfi = new Bitmap(fi, new Size(ci.Width, ci.Height));
                                    bool equal = bc.Equals((Bitmap)ci, rfi);

                                    if (equal)
                                    {
                                        string result = api.PostRequest(
                                            "action", "upload",
                                            "filename", i.Title,
                                            "url", source,
                                            "comment", "better quality",
                                            "token", csrfToken,
                                            "format", "xml");
                                        i.Status = ProcessStatus.Success;
                                        Console.Write("+");
                                        Thread.Sleep(5000);
                                    }
                                    else
                                    {
                                        i.Status = ProcessStatus.ImagesNotEqual;
                                        File.WriteAllBytes($"images\\{i.PageId}_c.jpeg", commonsFileData);
                                        File.WriteAllBytes($"images\\{i.PageId}_f.jpeg", flickrFileData);
                                        Console.Write("!");
                                    }
                                }
                                else
                                {
                                    i.Status = ProcessStatus.CommonsSizeMismatch;
                                    Console.Write("!");
                                }
                            }
                            else
                            {
                                i.Status = ProcessStatus.NoHighResolution;
                                Console.Write("-");
                                break;
                            }
                        }
                    }
                    if (i.Status == ProcessStatus.NotProcessed)
                    {
                        i.Status = ProcessStatus.OriginalNotFound;
                        Console.Write("x");
                    }
                }
                else
                {
                    i.Status = ProcessStatus.NoFlickrLink;
                    Console.Write("*");
                }
                db.Update(i);
                Thread.Sleep(1000);
            }

            Console.WriteLine(" Done");
        }

        Program()
        {
            api = new WpApi();
            ObtainEditToken();
            //GetImageList();
            //GetImageInfo();
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
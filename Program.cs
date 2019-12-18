using LinqToDB;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace WikiTasks
{
    [Table(Name = "Errors")]
    class Error
    {
        [PrimaryKey]
        public int LintId;
        [Column()]
        public int PageId;
        [Column()]
        public string PageTitle;
        [Column()]
        public string TemplateName;
        [Column()]
        public string ParamName;
    };

    class Db : LinqToDB.Data.DataConnection
    {
        public Db() : base("Db") { }

        public ITable<Error> Errors { get { return GetTable<Error>(); } }
    }
 
    class Program
    {
        MwApi wpApi;
        static Db db;

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

        static bool HaveTable(string name)
        {
            return db.DataProvider.GetSchemaProvider().
                GetSchema(db).Tables.Any(t => t.TableName == name);
        }

        void GetLintErrors(string errorCategory)
        {
            string lntfrom = "";

            if (!HaveTable("Errors"))
                db.CreateTable<Error>();
            else
                lntfrom = (db.Errors.OrderByDescending(e => e.LintId).First().LintId + 1).ToString();

            Console.Write("Searching for lint errors...");
            for (;;)
            {
                string continueParam = "";
                string xml = wpApi.PostRequest(
                    "action", "query",
                    "list", "linterrors",
                    "format", "xml",
                    "lntcategories", errorCategory,
                    "lntlimit", "5000",
                    "lntnamespace", "0",
                    "lntfrom", lntfrom,
                    "continue", continueParam);
                Console.Write('.');

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                db.BeginTransaction();
                foreach (XmlNode vNode in doc.SelectNodes("/api/query/linterrors/_v"))
                {
                    var err = new Error();
                    err.LintId = int.Parse(vNode.Attributes["lintId"].Value);
                    err.PageId = int.Parse(vNode.Attributes["pageid"].Value);
                    err.PageTitle = vNode.Attributes["title"].Value;
                    var tNode = vNode.SelectSingleNode("templateInfo");
                    var pNode = vNode.SelectSingleNode("params");
                    var templateName = tNode.Attributes["name"];
                    var paramName = pNode.Attributes["name"];
                    err.TemplateName = templateName != null ? templateName.Value : "";
                    err.ParamName = paramName != null ? paramName.Value : "";
                    db.Insert(err);
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


        Program()
        {
            wpApi = new MwApi("ru.wikipedia.org");

            GetLintErrors("missing-end-tag");
        }

        static void Main(string[] args)
        {
            db = new Db();
            new Program();
            db.Dispose();
        }
    }
}
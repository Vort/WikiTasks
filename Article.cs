using LinqToDB.Mapping;
using System.Collections.Generic;

namespace WikiTasks
{
    public class FileInvoke
    {
        public FileInvoke()
        {
            Start = "";
            Params = new List<string>();
            End = "";
        }

        public override string ToString()
        {
            if (Params.Count != 0)
                return Start + "|" + string.Join("|", Params) + End;
            else
                return Start + End;
        }

        public string Start;
        public List<string> Params;
        public string End;
        public string Raw;
    }

    [Table(Name = "Replacements")]
    public class Replacement
    {
        [PrimaryKey]
        public int Id;
        [Column()]
        public int PageId;
        [Column()]
        public string SrcString;
        [Column()]
        public string DstString;
        [Column()]
        public byte Status;
    }

    [Table(Name = "Articles")]
    public class Article
    {
        [PrimaryKey]
        public int PageId;
        [Column()]
        public string Timestamp;
        [Column()]
        public string Title;
        [Column()]
        public string WikiText;
        public List<FileInvoke> FileInvokes;
        public List<string> Errors;
    };
}

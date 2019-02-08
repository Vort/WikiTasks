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

    public class Replacement
    {
        public int PageId;
        public string SrcString;
        public string DstString;
    }

    public class Article
    {
        public int PageId;
        public string Timestamp;
        public string Title;
        public string WikiText;
        public List<FileInvoke> FileInvokes;
        public List<string> Errors;
    };
}

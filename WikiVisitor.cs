using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using System.Collections.Generic;

namespace WikiTasks
{
    public class WikiVisitor : WikiParserBaseVisitor<object>
    {
        int articleId;
        public List<FileInvoke> FileInvokes;

        public WikiVisitor(int articleId)
        {
            this.articleId = articleId;
            FileInvokes = new List<FileInvoke>();
        }

        public override object VisitFlink(WikiParser.FlinkContext context)
        {
            var fileInvoke = new FileInvoke();
            fileInvoke.Raw = context.GetText();
            foreach (var child in context.children)
            {
                var param = child as WikiParser.ParamContext;
                if (param != null)
                    fileInvoke.Params.Add(param.GetText().TrimStart().Substring(1));
                else if (fileInvoke.Params.Count == 0)
                    fileInvoke.Start += child.GetText();
                else
                    fileInvoke.End += child.GetText();
            }
            FileInvokes.Add(fileInvoke);
            return null;
        }
    }
}

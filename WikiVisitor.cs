using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WikiTasks
{
    class WikiVisitor : WikiParserBaseVisitor<object>
    {
        Article article;

        string paramName;
        bool templateFound;

        public WikiVisitor(Article article, string paramName)
        {
            templateFound = false;
            this.article = article;
            this.paramName = paramName;
        }

        void Trim(string source, out int spaces1, out string trimmed, out bool newline, out int spaces2)
        {
            var match = Regex.Match(source,
                "\\A([ \t]*)([^ \t]|[^ \t].*[^ \t])?([ \t]*)\\z",
                RegexOptions.Singleline);
            spaces1 = match.Groups[1].Length;
            spaces2 = match.Groups[3].Length;
            trimmed = match.Groups[2].Value;
            newline = match.Groups[2].Value.EndsWith("\n");
        }

        public override object VisitTempl(WikiParser.TemplContext templContext)
        {
            var template = new Template();
            template.Name = templContext.children[1].GetText().Trim();
            var prevParam = new TemplateParam();
            bool v1;
            string v2;
            int v3 = 0;
            int v4 = 0;
            foreach (var child in templContext.children)
            {
                var paramContext = child as WikiParser.ParamContext;
                if (paramContext == null)
                    continue;

                var param = new TemplateParam();
                if (v3 != 0)
                    param.Sp1 = v3;
                string pipe = paramContext.children[0].GetText();
                Trim(pipe.Remove(pipe.Length - 1), out v3, out v2, out param.Newline, out v4);
                if (param.Sp1 == 0)
                    param.Sp1 = v4;
                if (prevParam.Value == "" && prevParam.Sp4 == 0)
                    prevParam.Sp4 = v3;
                if (paramContext.ChildCount < 3)
                    return null; // Unnamed parameters are not supported
                string name = paramContext.children[1].GetText();
                Trim(name, out param.Sp2, out param.Name, out v1, out param.Sp3);
                var eqContext = paramContext.children[2] as ITerminalNode;
                if (eqContext == null || eqContext.Symbol.Type != WikiLexer.EQ)
                    return null; // Unnamed parameters are not supported
                param.ValueTrees = paramContext.children.Skip(3).ToArray();
                string value = string.Concat(param.ValueTrees.Select(t => t.GetText()));
                Trim(value, out param.Sp4, out param.Value, out v1, out v3);
                template.Params.Add(param);
                prevParam = param;
            }
            if (template.Params.Count != 0)
            {
                template.Params[template.Params.Count - 1].Value =
                    template.Params[template.Params.Count - 1].Value.TrimEnd();
            }

            if (template[paramName] != null)
            {
                if (templateFound)
                    throw new Exception();
                templateFound = true;

                article.ReplIndex1 = templContext.Start.StartIndex;
                article.ReplIndex2 = templContext.Stop.StopIndex + 1;
                article.Template = template;
            }

            return null;
        }
    }
}

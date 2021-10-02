using Antlr4.Runtime.Tree;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WikiTasks
{
    class WikiVisitor : WikiParserBaseVisitor<object>
    {
        Article article;

        string[] templNames;
        string[] paramNames;
        bool templateFound;

        public WikiVisitor(Article article, string[] templNames, string[] paramNames)
        {
            templateFound = false;
            this.article = article;
            this.templNames = templNames != null ? templNames : new string[] { };
            this.paramNames = paramNames != null ? paramNames : new string[] { };
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

        string CapitalizeFirstLetter(string s)
        {
            if (s.Length == 0)
                return s;
            else if (s.Length == 1)
                return char.ToUpper(s[0]).ToString();
            else
                return char.ToUpper(s[0]) + s.Substring(1);
        }

        public override object VisitTempl(WikiParser.TemplContext templContext)
        {
            var template = new Template();
            var nameParts = new List<string>();
            for (int i = 1; i < templContext.ChildCount - 1; i++)
            {
                if (templContext.children[i] is WikiParser.ParamContext)
                    break;
                nameParts.Add(templContext.children[i].GetText());
            }
            template.Name = string.Concat(nameParts).Trim();
            template.StartPosition = templContext.start.StartIndex;
            template.StopPosition = templContext.stop.StopIndex + 1;
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
                if (paramContext.ChildCount == 1 ||
                    paramContext.ChildCount == 2 && paramContext.children[1].GetText().Trim().Length == 0)
                    continue; // Ignore empty parameters
                if (paramContext.ChildCount < 3)
                    return null; // Unnamed parameters are not supported
                string name = paramContext.children[1].GetText();
                Trim(name, out param.Sp2, out param.Name, out v1, out param.Sp3);
                var eqContext = paramContext.children[2] as ITerminalNode;
                if (eqContext == null || eqContext.Symbol.Type != WikiLexer.EQ)
                    return null; // Unnamed parameters are not supported
                IParseTree[] valueTrees = paramContext.children.Skip(3).ToArray();
                string value = string.Concat(valueTrees.Select(t => t.GetText()));
                // Отключено для экономии памяти
                // param.ValueTrees = valueTrees;
                Trim(value, out param.Sp4, out param.Value, out v1, out v3);
                template.Params.Add(param);
                prevParam = param;
            }
            if (template.Params.Count != 0)
            {
                template.Params[template.Params.Count - 1].Value =
                    template.Params[template.Params.Count - 1].Value.TrimEnd();
            }


            bool templMatch = false;
            bool paramMatch = false;

            var normName1 = CapitalizeFirstLetter(template.Name);
            foreach (var templName in templNames)
            {
                var normName2 = CapitalizeFirstLetter(templName);
                if (normName1 == normName2)
                {
                    templMatch = true;
                    break;
                }
            }

            foreach (var paramName in paramNames)
            {
                if (template[paramName] != null)
                {
                    paramMatch = true;
                    break;
                }
            }


            if (templMatch || paramMatch)
            {
                if (templateFound)
                    return null;
                templateFound = true;
                article.Template = template;
            }

            return null;
        }
    }
}

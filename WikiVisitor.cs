using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using System;
using System.Text;
using System.Text.RegularExpressions;

namespace WikiTasks
{
    public class WikiVisitor : WikiParserBaseVisitor<object>
    {
        Article article;

        bool templateFound;

        public WikiVisitor(Article article)
        {
            templateFound = false;
            this.article = article;
        }

        string NodesToText(ParserRuleContext context, int startIndex)
        {
            var sb = new StringBuilder();
            for (int i = startIndex; i < context.ChildCount; i++)
                sb.Append(context.children[i].GetText());
            return sb.ToString();
        }

        string ScanWords(ParserRuleContext context, int startIndex, out int endIndex)
        {
            endIndex = startIndex;
            var sb = new StringBuilder();
            for (int i = startIndex; i < context.ChildCount; i++)
            {
                var child = context.children[i] as ITerminalNode;
                if (child == null)
                    break;
                if (child.Symbol.Type != WikiLexer.WORD)
                    break;
                sb.Append(context.children[i].GetText());
                endIndex++;
            }
            return sb.ToString();
        }

        void Trim(string source, out int spaces1, out string trimmed, out bool newline, out int spaces2)
        {
            spaces1 = 0;
            spaces2 = 0;
            trimmed = "";
            newline = false;

            for (; spaces1 < source.Length; spaces1++)
                if (source[spaces1] != ' ')
                    break;
            if (spaces1 == source.Length)
                return;

            for (int i = source.Length - 1; i >= 0; i--)
            {
                if (source[i] == '\n')
                {
                    newline = true;
                    break;
                }
                if (source[i] != ' ')
                    break;
                spaces2++;
            }

            trimmed = source.Substring(spaces1, source.Length - spaces2 - spaces1);
        }

        public override object VisitParam(WikiParser.ParamContext context)
        {
            if (context.Parent.RuleIndex == WikiParser.RULE_templ)
            {
                int mainParamEqIndex;
                string paramName = ScanWords(context, 1, out mainParamEqIndex);

                if (paramName.Trim().ToLower() == "оригинальное название")
                {
                    if (templateFound)
                        throw new Exception();
                    templateFound = true;

                    var templateContext = context.Parent as ParserRuleContext;
                    article.ReplIndex1 = templateContext.Start.StartIndex;
                    article.ReplIndex2 = templateContext.Stop.StopIndex + 1;

                    int endIndex;
                    var template = new Template();
                    template.Name = ScanWords(templateContext, 1, out endIndex).Trim();
                    var prevParam = new TemplateParam();
                    bool v1;
                    string v2;
                    int v3 = 0;
                    int v4 = 0;
                    foreach (var child in templateContext.children)
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
                        if (prevParam.Value == "")
                        {
                            if (param.Newline)
                                prevParam.Sp4 = 1;
                            else if (prevParam.Sp4 == 0)
                                prevParam.Sp4 = v3;
                        }
                        string name = ScanWords(paramContext, 1, out endIndex);
                        Trim(name, out param.Sp2, out param.Name, out v1, out param.Sp3);
                        var eqContext = paramContext.children[endIndex] as ITerminalNode;
                        if (eqContext == null || eqContext.Symbol.Type != WikiLexer.EQ)
                            throw new Exception();
                        string value = NodesToText(paramContext, endIndex + 1);
                        Trim(value, out param.Sp4, out param.Value, out v1, out v3);
                        template.Params.Add(param);
                        prevParam = param;
                    }
                    template.Params[template.Params.Count - 1].Value =
                        template.Params[template.Params.Count - 1].Value.TrimEnd();

                    bool flinkFound = false;
                    for (int i = mainParamEqIndex + 1; i < context.ChildCount; i++)
                    {
                        var ww = context.children[i] as WikiParser.WikiwordContext;
                        if (ww == null)
                            continue;
                        var flink = ww.children[0] as WikiParser.FlinkContext;
                        if (flink == null)
                            continue;
                        if (flinkFound)
                        {
                            article.Status = ProcessStatus.Skipped;
                            return null;
                        }
                        flinkFound = true;
                        int ni1 = (context.children[mainParamEqIndex] as ITerminalNode).Symbol.StopIndex + 1;
                        int ni2 = (flink as ParserRuleContext).Start.StartIndex;
                        string origName = article.SrcWikiText.Substring(ni1, ni2 - ni1).Trim();
                        origName = Regex.Replace(origName, "[ \n]*<br */?>$", "");

                        var orNameParam = template["оригинальное название"];
                        orNameParam.Value = origName;

                        var imageDescParam = template["описание изображения"];
                        if (imageDescParam == null)
                        {
                            imageDescParam = new TemplateParam();
                            imageDescParam.Name = "описание изображения";
                            imageDescParam.Newline = true;
                            imageDescParam.Sp1 = orNameParam.Sp1;
                            imageDescParam.Sp2 = orNameParam.Sp2;
                            imageDescParam.Sp3 = orNameParam.Sp3 + 1;
                            imageDescParam.Sp4 = orNameParam.Sp4;
                            template.InsertAfter(orNameParam, imageDescParam);
                        }
                        var imageParam = template["изображение"];
                        if (imageParam == null)
                        {
                            imageParam = new TemplateParam();
                            imageParam.Name = "изображение";
                            imageParam.Newline = true;
                            imageParam.Sp1 = orNameParam.Sp1;
                            imageParam.Sp2 = orNameParam.Sp2;
                            imageParam.Sp3 = orNameParam.Sp3 + 10;
                            imageParam.Sp4 = orNameParam.Sp4;
                            template.InsertAfter(orNameParam, imageParam);
                        }
                        imageParam.Value = ScanWords(flink, 1, out endIndex);

                        string desc = "";
                        for (int j = endIndex; j < flink.ChildCount - 1; j++)
                        {
                            string newDesc = NodesToText(flink.children[j] as ParserRuleContext, 1);
                            if (newDesc != "" &&
                                !newDesc.EndsWith("px") &&
                                !newDesc.Contains("center") &&
                                !newDesc.Contains("thumb"))
                            {
                                desc = newDesc;
                            }
                        }
                        imageDescParam.Value = desc;
                    }

                    if (!flinkFound)
                    {
                        article.Status = ProcessStatus.Skipped;
                        return null;
                    }

                    article.NewTemplateText = template.ToString();
                }
            }
            return null;
        }
    }
}

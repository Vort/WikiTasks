using Antlr4.Runtime;
using Antlr4.Runtime.Tree;

namespace WikiTasks
{
    public class WikiVisitor : WikiParserBaseVisitor<object>
    {
        public string MouthParam;

        public override object VisitParam(WikiParser.ParamContext context)
        {
            RuleContext paramContext = context;
            if (paramContext.Parent.RuleIndex == WikiParser.RULE_templ)
            {
                string paramName = "";
                foreach (var child in context.children)
                {
                    var terminalChild = child as ITerminalNode;
                    if (terminalChild != null)
                    {
                        if (terminalChild.Symbol.Type == WikiLexer.EQ)
                            break;
                        paramName += terminalChild.GetText();
                    }
                }
                if (paramName.Trim().ToLower() == "устье")
                {
                    string paramValue = "";
                    foreach (var child in context.children)
                    {
                        var wikiwordChild = child as WikiParser.WikiwordContext;
                        if (wikiwordChild != null)
                        {
                            string wwText = wikiwordChild.GetText();
                            if (wwText.StartsWith("\n") || wwText.StartsWith("<"))
                                break;
                            paramValue += wwText;
                        }
                    }
                    paramValue = paramValue.Trim(' ', '/');
                    if (paramValue != "")
                        MouthParam = paramValue;
                }
            }
            return null;
        }
    }
}

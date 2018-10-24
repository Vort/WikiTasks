using Antlr4.Runtime;

namespace WikiTasks
{
    public class WikiVisitor : WikiParserBaseVisitor<object>
    {
        Article article;

        public WikiVisitor(Article article)
        {
            this.article = article;
        }

        public override object VisitParam(WikiParser.ParamContext context)
        {
            RuleContext paramContext = context;
            if (paramContext.Parent.RuleIndex != WikiParser.RULE_templ)
                return null;
            if (context.ChildCount < 2)
                return null;
            string paramName = context.children[1].GetText();
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
                paramValue = paramValue.Trim('/').Trim();
                if (paramValue != "")
                    article.MouthTitle = char.ToUpper(paramValue[0]) + paramValue.Substring(1);
            }
            return null;
        }
    }
}

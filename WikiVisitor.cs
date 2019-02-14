using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WikiTasks
{
    class WikiVisitor : WikiParserBaseVisitor<object>
    {
        Article article;

        public WikiVisitor(Article article)
        {
            this.article = article;
            article.ElinkInvokes = new List<ELinkInvoke>();
        }

        public override object VisitElink(WikiParser.ElinkContext elinkContext)
        {
            string url = elinkContext.children[1].GetText();
            var m = Regex.Match(url,
                "https?:\\/\\/geonames\\.usgs\\.gov\\/(pls\\/gnispublic|apex)\\/f\\?p=gnispq:3:[0-9]*::NO::P3_FID:([0-9]+)");
            if (m.Success)
            {
                var eli = new ELinkInvoke();
                eli.Text = elinkContext.GetText();
                eli.Url = url;
                eli.Title = string.Concat(elinkContext.children.Skip(2).Take(
                    elinkContext.ChildCount - 3).Select(c => c.GetText()));
                eli.StartPosition = elinkContext.Start.StartIndex;
                eli.EndPosition = elinkContext.Stop.StopIndex + 1;
                article.ElinkInvokes.Add(eli);
            }
            return null;
        }
    }
}
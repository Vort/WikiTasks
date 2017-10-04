using System.Drawing;

namespace ImageDiff
{
    public class CompareOptions
    {
        public AnalyzerTypes AnalyzerType { get; set; }
        public double JustNoticeableDifference { get; set; }

        public CompareOptions()
        {
            JustNoticeableDifference = 2.3;
            AnalyzerType = AnalyzerTypes.ExactMatch;
        }
    }
}
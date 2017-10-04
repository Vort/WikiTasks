using System;
using System.Drawing;
using ImageDiff.Analyzers;


namespace ImageDiff
{
    public class BitmapComparer
    {
        private double JustNoticeableDifference { get; set; }
        private AnalyzerTypes AnalyzerType { get; set; }

        private IBitmapAnalyzer BitmapAnalyzer { get; set; }

        public BitmapComparer(CompareOptions options = null)
        {
            if (options == null)
            {
                options = new CompareOptions();
            }
            Initialize(options);

            BitmapAnalyzer = BitmapAnalyzerFactory.Create(AnalyzerType, JustNoticeableDifference);
        }

        private void Initialize(CompareOptions options)
        {
            JustNoticeableDifference = options.JustNoticeableDifference;
            AnalyzerType = options.AnalyzerType;
        }

        public bool Equals(Bitmap firstImage, Bitmap secondImage)
        {
            if (firstImage == null && secondImage == null) return true;
            if (firstImage == null) return false;
            if (secondImage == null) return false;
            if (firstImage.Width != secondImage.Width || firstImage.Height != secondImage.Height) return false;

            var differenceMap = BitmapAnalyzer.Analyze(firstImage, secondImage);

            // differenceMap is a 2d array of boolean values, true represents a difference between the images
            // iterate over the dimensions of the array and look for a true value (difference) and return false
            for (var i = 0; i < differenceMap.GetLength(0); i++)
            {
                for (var j = 0; j < differenceMap.GetLength(1); j++)
                {
                    if (differenceMap[i, j])
                        return false;
                }
            }
            return true;
        }
    }
}

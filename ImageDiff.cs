using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WikiTasks
{
    struct Lab
    {
        public double L;
        public double A;
        public double B;
    }

    struct Color
    {
        public Color(System.Drawing.Color color)
        {
            R = color.R;
            G = color.G;
            B = color.B;
        }

        public Lab ToLab()
        {
            double r = R * (1.0 / 255.0);
            double g = G * (1.0 / 255.0);
            double b = B * (1.0 / 255.0);

            r = (r > 0.04045) ? Math.Exp(Math.Log((r + 0.055) * (1.0 / 1.055)) * 2.4) : r * (1.0 / 12.92);
            g = (g > 0.04045) ? Math.Exp(Math.Log((g + 0.055) * (1.0 / 1.055)) * 2.4) : g * (1.0 / 12.92);
            b = (b > 0.04045) ? Math.Exp(Math.Log((b + 0.055) * (1.0 / 1.055)) * 2.4) : b * (1.0 / 12.92);

            double x = (r * 0.4124 + g * 0.3576 + b * 0.1805) * (1.0 / 0.95047);
            double y = r * 0.2126 + g * 0.7152 + b * 0.0722;
            double z = (r * 0.0193 + g * 0.1192 + b * 0.9505) * (1.0 / 1.08883);

            x = (x > 0.008856) ? Math.Exp(Math.Log(x) * (1.0 / 3)) : (x * 7.787) + 16.0 / 116;
            y = (y > 0.008856) ? Math.Exp(Math.Log(y) * (1.0 / 3)) : (y * 7.787) + 16.0 / 116;
            z = (z > 0.008856) ? Math.Exp(Math.Log(z) * (1.0 / 3)) : (z * 7.787) + 16.0 / 116;

            return new Lab
            {
                L = (116.0 * y) - 16.0,
                A = 500.0 * (x - y),
                B = 200.0 * (y - z)
            };
        }

        public static double Difference(Lab c1, Lab c2)
        {
            double dl = c1.L - c2.L;
            double da = c1.A - c2.A;
            double db = c1.B - c2.B;
            return Math.Sqrt(dl * dl + da * da + db * db);
        }

        public byte R;
        public byte G;
        public byte B;
    }

    class ImageDiff
    {
        public static double GetMaxDeltaE(Bitmap b1, Bitmap b2)
        {
            double maxDeltaE = double.MinValue;
            for (var x = 0; x < b1.Width; x++)
            {
                for (var y = 0; y < b1.Height; y++)
                {
                    var firstLab = new Color(b1.GetPixel(x, y)).ToLab();
                    var secondLab = new Color(b2.GetPixel(x, y)).ToLab();

                    var deltaE = Color.Difference(firstLab, secondLab);
                    maxDeltaE = Math.Max(maxDeltaE, deltaE);
                }
            }
            return maxDeltaE;
        }

        public static Bitmap ResizeImage(System.Drawing.Image image, int width, int height)
        {
            var destRect = new Rectangle(0, 0, width, height);
            var destImage = new Bitmap(width, height);

            destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);

            using (var graphics = Graphics.FromImage(destImage))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (var wrapMode = new ImageAttributes())
                {
                    wrapMode.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(image, destRect, 0, 0,
                        image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
                }
            }

            return destImage;
        }
    }
}

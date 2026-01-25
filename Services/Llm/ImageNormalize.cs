using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Kinectv1.Llm
{
    internal static class ImageNormalize
    {
        public static byte[] ToJpegBytes(Stream input, int maxLongSide = 1536, long quality = 85)
        {
            using var img = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: false);

            int width = img.Width;
            int height = img.Height;
            int longSide = Math.Max(width, height);
            if (longSide > maxLongSide)
            {
                double scale = (double)maxLongSide / longSide;
                width = (int)Math.Round(width * scale);
                height = (int)Math.Round(height * scale);
            }

            using var bmp = new Bitmap(width, height);
            bmp.SetResolution(img.HorizontalResolution, img.VerticalResolution);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(img, new Rectangle(0, 0, width, height));
            }

            using var ms = new MemoryStream();
            var encoder = GetJpegEncoder();
            if (encoder != null)
            {
                using var encParams = new EncoderParameters(1);
                encParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                bmp.Save(ms, encoder, encParams);
            }
            else
            {
                bmp.Save(ms, ImageFormat.Jpeg);
            }
            return ms.ToArray();
        }

        private static ImageCodecInfo GetJpegEncoder()
        {
            try
            {
                foreach (var c in ImageCodecInfo.GetImageEncoders())
                {
                    if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
                }
            }
            catch { }
            return null;
        }
    }
}

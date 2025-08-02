// ImagePreprocess.cs
using System;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows;

namespace Kinectv1   // <-- change this to your project's namespace if needed
{
    public static class ImagePreprocess
    {
        
        /// Convert a BGRA32 BitmapSource (cropped face) to ArcFace input:
        /// float[1,3,112,112], RGB, CHW layout, normalized to [-1,1].
       
        public static float[] ToArcFaceCHW(BitmapSource src)
        {
            try
            {
                // Resize to 112x112
                var resized = new TransformedBitmap(src, new ScaleTransform(112.0 / src.PixelWidth, 112.0 / src.PixelHeight));
                
                // Convert to BGR24 format
                var converted = new FormatConvertedBitmap(resized, PixelFormats.Bgr24, null, 0);
                
                // Extract pixel data
                int stride = (112 * 24 + 7) / 8; // 3 bytes per pixel
                byte[] pixels = new byte[stride * 112];
                converted.CopyPixels(pixels, stride, 0);
                
                // Convert BGR to RGB and normalize to [-1, 1]
                float[] chw = new float[3 * 112 * 112];
                
                for (int y = 0; y < 112; y++)
                {
                    for (int x = 0; x < 112; x++)
                    {
                        int pixelIndex = y * stride + x * 3;
                        
                        // BGR to RGB conversion and normalization
                        byte b = pixels[pixelIndex];
                        byte g = pixels[pixelIndex + 1]; 
                        byte r = pixels[pixelIndex + 2];
                        
                        // Convert to [-1, 1] range (CHW format)
                        chw[0 * 112 * 112 + y * 112 + x] = (r / 255.0f - 0.5f) * 2.0f; // R channel
                        chw[1 * 112 * 112 + y * 112 + x] = (g / 255.0f - 0.5f) * 2.0f; // G channel  
                        chw[2 * 112 * 112 + y * 112 + x] = (b / 255.0f - 0.5f) * 2.0f; // B channel
                    }
                }
                
                return chw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ImagePreprocess.ToArcFaceCHW failed: {ex.Message}");
                return new float[3 * 112 * 112]; // Return empty array as fallback
            }
        }
    }
}

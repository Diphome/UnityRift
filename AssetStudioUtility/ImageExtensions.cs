using System.Drawing;
using System.IO;
using TGASharpLib;

namespace AssetStudio
{
    public static class ImageExtensions
    {
        public static MemoryStream ConvertToStream(this Bitmap image, ImageFormat imageFormat)
        {
            var outputStream = new MemoryStream();
            switch (imageFormat)
            {
                case ImageFormat.Jpeg:
                    image.Save(outputStream, System.Drawing.Imaging.ImageFormat.Jpeg);
                    break;
                case ImageFormat.Png:
                    image.Save(outputStream, System.Drawing.Imaging.ImageFormat.Png);
                    break;
                case ImageFormat.Bmp:
                    image.Save(outputStream, System.Drawing.Imaging.ImageFormat.Bmp);
                    break;
                case ImageFormat.Tga:
                    var tga = new TGA(image);
                    tga.Save(outputStream);
                    break;
            }
            image.Dispose();
            return outputStream;
        }
    }
}

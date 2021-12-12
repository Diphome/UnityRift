using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.IO;

namespace AssetStudio
{
    public static class Texture2DExtensions
    {
        public static Bitmap ConvertToBitmap(this Texture2D m_Texture2D, bool flip)
        {
            var converter = new Texture2DConverter(m_Texture2D);
            var bytes = BigArrayPool<byte>.Shared.Rent(m_Texture2D.m_Width * m_Texture2D.m_Height * 4);
            try
            {
                if (converter.DecodeTexture2D(bytes))
                {
                    var bitmap = new Bitmap(m_Texture2D.m_Width, m_Texture2D.m_Height, PixelFormat.Format32bppArgb);
                    var bmpData = bitmap.LockBits(new Rectangle(0, 0, m_Texture2D.m_Width, m_Texture2D.m_Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                    Marshal.Copy(bytes, 0, bmpData.Scan0, bitmap.Width * bitmap.Height * 4);
                    bitmap.UnlockBits(bmpData);
                    if (flip)
                    {
                        bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
                    }
                    return bitmap;
                }
                return null;
            }
            finally
            {
                BigArrayPool<byte>.Shared.Return(bytes);
            }
        }

        public static MemoryStream ConvertToStream(this Texture2D m_Texture2D, ImageFormat imageFormat, bool flip)
        {
            var image = ConvertToBitmap(m_Texture2D, flip);
            if (image != null)
            {
                using (image)
                {
                    return image.ConvertToStream(imageFormat);
                }
            }
            return null;
        }
    }
}

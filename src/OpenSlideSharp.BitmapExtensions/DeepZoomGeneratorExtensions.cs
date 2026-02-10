using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using static OpenSlideSharp.DeepZoomGenerator;

namespace OpenSlideSharp.BitmapExtensions
{
    /// <summary>
    /// 
    /// </summary>
    public static class DeepZoomGeneratorExtensions
    {
        /// <summary>
        /// Get tile as jpeg.
        /// </summary>
        /// <param name="generator"></param>
        /// <param name="level"></param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <param name="tileInfo"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public static byte[] GetTileAsJpeg(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo, int? quality = null)
        {
            using (var ms = GetTileAsJpegStream(generator, level, col, row, out tileInfo, quality))
            {
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Get tile as jpeg stream.
        /// </summary>
        /// <param name="generator"></param>
        /// <param name="level"></param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <param name="tileInfo"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public static MemoryStream GetTileAsJpegStream(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo, int? quality = null)
        {
            using (var bitmap = GetTileImage(generator, level, col, row, out tileInfo))
            {
                return bitmap.ToStream(ImageFormat.Jpeg, quality);
            }
        }

        /// <summary>
        /// Get tile as png.
        /// </summary>
        /// <param name="generator"></param>
        /// <param name="level"></param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <param name="tileInfo"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public static byte[] GetTileAsPng(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo, int? quality = null)
        {
            using (var ms = GetTileAsPngStream(generator, level, col, row, out tileInfo, quality))
            {
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Get tile as png stream.
        /// </summary>
        /// <param name="generator"></param>
        /// <param name="level"></param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <param name="tileInfo"></param>
        /// <param name="quality"></param>
        /// <returns></returns>
        public static MemoryStream GetTileAsPngStream(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo, int? quality = null)
        {
            using (var bitmap = GetTileImage(generator, level, col, row, out tileInfo))
            {
                return bitmap.ToStream(ImageFormat.Png, quality);
            }
        }

        /// <summary>
        /// Get tile image stream.
        /// </summary>
        /// <param name="generator"></param>
        /// <param name="level"></param>
        /// <param name="col"></param>
        /// <param name="row"></param>
        /// <param name="tileInfo"></param>
        /// <returns></returns>
        public static Bitmap GetTileImage(this DeepZoomGenerator generator, int level, int col, int row, out TileInfo tileInfo)
        {
            if (generator == null)
                throw new NullReferenceException();
            var raw = generator.GetTile(level, col, row, out tileInfo);
            
            // Create a bitmap with its own memory and copy the pixel data into it.
            // This is necessary because the raw byte array must remain valid for the
            // lifetime of the bitmap, and using 'fixed' only pins memory temporarily.
            var bitmap = new Bitmap((int)tileInfo.Width, (int)tileInfo.Height, PixelFormat.Format32bppArgb);
            var bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(raw, 0, bitmapData.Scan0, raw.Length);
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
            return bitmap;
        }
    }
}

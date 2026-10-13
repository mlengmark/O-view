// Pixel comparison for tools/check.ps1, compiled at run time with Add-Type.
//
// Written in C# 5 on purpose: Windows PowerShell 5.1 compiles Add-Type sources with the old
// CodeDom compiler, so no string interpolation, expression-bodied members or tuples here.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OViewTools
{
    public sealed class ImageDiff
    {
        public bool SizeMatches;
        public int Width;
        public int Height;
        public long DifferentPixels;
        public int MaxChannelDelta;
    }

    public static class ImageCompare
    {
        /// <summary>
        /// Compares two PNGs. A pixel counts as different when any channel differs by more
        /// than <paramref name="channelThreshold"/>, which absorbs anti-aliasing noise without
        /// hiding a moved line or changed text. When <paramref name="diffPath"/> is given and
        /// anything differs, writes the new image faded with every differing pixel in magenta.
        /// </summary>
        public static ImageDiff Compare(string expectedPath, string actualPath, int channelThreshold, string diffPath)
        {
            using (var expected = new Bitmap(expectedPath))
            using (var actual = new Bitmap(actualPath))
            {
                var result = new ImageDiff();
                result.Width = actual.Width;
                result.Height = actual.Height;
                result.SizeMatches = expected.Width == actual.Width && expected.Height == actual.Height;
                if (!result.SizeMatches)
                {
                    result.DifferentPixels = (long)actual.Width * actual.Height;
                    result.MaxChannelDelta = 255;
                    return result;
                }

                var a = Pixels(expected);
                var b = Pixels(actual);
                var diff = diffPath == null ? null : new int[b.Length];

                for (var i = 0; i < a.Length; i++)
                {
                    var delta = MaxDelta(a[i], b[i]);
                    if (delta > result.MaxChannelDelta)
                    {
                        result.MaxChannelDelta = delta;
                    }
                    var differs = delta > channelThreshold;
                    if (differs)
                    {
                        result.DifferentPixels++;
                    }
                    if (diff != null)
                    {
                        diff[i] = differs ? unchecked((int)0xFFFF00FF) : Faded(b[i]);
                    }
                }

                if (diff != null && result.DifferentPixels > 0)
                {
                    Save(diff, actual.Width, actual.Height, diffPath);
                }
                return result;
            }
        }

        private static int[] Pixels(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new int[bitmap.Width * bitmap.Height];
                for (var y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * bitmap.Width, bitmap.Width);
                }
                return pixels;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private static int MaxDelta(int x, int y)
        {
            var max = 0;
            for (var shift = 0; shift < 32; shift += 8)
            {
                var d = Math.Abs(((x >> shift) & 0xFF) - ((y >> shift) & 0xFF));
                if (d > max)
                {
                    max = d;
                }
            }
            return max;
        }

        private static int Faded(int argb)
        {
            // Blend toward white so the magenta differences stand out.
            var r = 255 - (255 - ((argb >> 16) & 0xFF)) / 4;
            var g = 255 - (255 - ((argb >> 8) & 0xFF)) / 4;
            var b = 255 - (255 - (argb & 0xFF)) / 4;
            return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }

        private static void Save(int[] pixels, int width, int height, string path)
        {
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                var rect = new Rectangle(0, 0, width, height);
                var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (var y = 0; y < height; y++)
                    {
                        Marshal.Copy(pixels, y * width, IntPtr.Add(data.Scan0, y * data.Stride), width);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
                bitmap.Save(path, ImageFormat.Png);
            }
        }
    }
}

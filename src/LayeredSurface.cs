using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PhoneRemote
{
    internal sealed class LayeredSurface : IDisposable
    {
        private IntPtr dc;
        private IntPtr dib;
        private IntPtr previous;
        private Bitmap bitmap;

        internal Bitmap GetBitmap(Size size, float dpi)
        {
            if (bitmap != null && bitmap.Size == size && bitmap.HorizontalResolution == dpi) return bitmap;
            Dispose();
            dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            try
            {
                var info = new BitmapInfo
                {
                    HeaderSize = 40, Width = size.Width, Height = -size.Height, Planes = 1, BitsPerPixel = 32
                };
                IntPtr pixels;
                dib = CreateDIBSection(dc, ref info, 0, out pixels, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || pixels == IntPtr.Zero) return null;
                previous = SelectObject(dc, dib);
                if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return null;
                var buffer = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, pixels);
                try { buffer.SetResolution(dpi, dpi); }
                catch { buffer.Dispose(); throw; }
                bitmap = buffer;
                return bitmap;
            }
            finally
            {
                if (bitmap == null) Dispose();
            }
        }

        internal bool Present(IntPtr window)
        {
            if (bitmap == null) return false;
            var size = bitmap.Size;
            var origin = Point.Empty;
            var blend = new Blend { Opacity = 255, Format = 1 };
            return UpdateLayeredWindow(window, IntPtr.Zero, IntPtr.Zero, ref size, dc, ref origin, 0, ref blend, 2);
        }

        public void Dispose()
        {
            if (bitmap != null) { bitmap.Dispose(); bitmap = null; }
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            dc = dib = previous = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public uint HeaderSize;
            public int Width, Height;
            public ushort Planes, BitsPerPixel;
            public uint Compression, ImageSize;
            public int XPixelsPerMetre, YPixelsPerMetre;
            public uint ColorsUsed, ImportantColors, FirstColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blend { internal byte Operation, Flags, Opacity, Format; }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr output,
            IntPtr position, ref Size size, IntPtr source, ref Point origin, uint key, ref Blend blend, uint flags);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint colors,
            out IntPtr pixels, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    }
}

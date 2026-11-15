using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickInk;

// 画面の写し（この窓の後ろに見えているもの）。
// 白紙の窓は書き心地を落とさないよう透明にしていない。そのため送るときの動きでは、窓の中に「窓の後ろの画面」の写しを敷き、
// その上で紙だけを動かす。写しを撮るときに白紙の窓自身が写らないよう、窓を画面の写しから外しておく（Windows 10 2004 以降）。

public static class ScreenBackdrop
{
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    const uint SRCCOPY = 0x00CC0020;
    const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

    // 白紙の窓を写しから外せたか。外せなかったときは写しを撮らず、紙の後ろは灰色にする
    public static bool Available { get; private set; }
    static volatile bool selfCaptured;  // 一度でも白紙が写り込んだら、以後は写しを使わない

    public static bool Exclude(IntPtr hwnd)
    {
        try
        {
            return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)
                && GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity == WDA_EXCLUDEFROMCAPTURE;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void EnableFor(IntPtr inkWindow) => Available = !selfCaptured && Exclude(inkWindow);

    // 裏のスレッドで撮り、窓いっぱいに敷く背景（凍結済みなので画面のスレッドでそのまま使える）にして返す。
    // 全画面で数十ミリ秒かかるため、画面の動きを止めない。barHeightPx は白紙の下の帯の高さ（画面の点の数）
    public static Task<Brush?> CaptureBehindAsync(IntPtr hwnd, double barHeightPx) =>
        Available ? Task.Run(() => Capture(hwnd, barHeightPx)) : Task.FromResult<Brush?>(null);

    static Brush? Capture(IntPtr hwnd, double barHeightPx)
    {
        try
        {
            if (!GetWindowRect(hwnd, out RECT win)) return null;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return null;
            // 窓のうち、画面に映っている部分だけを撮る（画面の外は黒くなるため）
            RECT r = new()
            {
                Left = Math.Max(win.Left, info.rcMonitor.Left),
                Top = Math.Max(win.Top, info.rcMonitor.Top),
                Right = Math.Min(win.Right, info.rcMonitor.Right),
                Bottom = Math.Min(win.Bottom, info.rcMonitor.Bottom),
            };
            int width = r.Right - r.Left, height = r.Bottom - r.Top;
            double winWidth = win.Right - win.Left, winHeight = win.Bottom - win.Top;
            if (width <= 0 || height <= 0 || winWidth <= 0 || winHeight <= 0) return null;

            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,  // 上から下の順に並べる
                biPlanes = 1,
                biBitCount = 32,
            };
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr bitmap = CreateDIBSection(screen, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr previous = bitmap == IntPtr.Zero ? IntPtr.Zero : SelectObject(memory, bitmap);
            try
            {
                if (bitmap == IntPtr.Zero || !BitBlt(memory, 0, 0, width, height, screen, r.Left, r.Top, SRCCOPY)) return null;
                GdiFlush();
                int stride = width * 4;
                if (LooksLikeOwnWindow(bits, width, height, stride, barHeightPx))
                {
                    // 写しから外せていない（白紙そのものが写った）。紙が二重に見えるので、以後は灰色にする
                    selfCaptured = true;
                    Available = false;
                    Log.Diag("画面の写しに白紙が写り込むため、送るときの紙の後ろは灰色にします");
                    return null;
                }
                BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bits, stride * height, stride);
                image.Freeze();
                var brush = new ImageBrush(image)
                {
                    Stretch = Stretch.Fill,
                    ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                    Viewport = new Rect(
                        (r.Left - win.Left) / winWidth, (r.Top - win.Top) / winHeight,
                        width / winWidth, height / winHeight),
                };
                brush.Freeze();
                return brush;
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memory, previous);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
        catch (Exception ex)
        {
            Log.Diag("画面の写しを撮れません: " + ex.Message);
            return null;
        }
    }

    // 白紙の窓（上が真っ白、下の帯が薄い灰色 235）がそのまま写っているか。帯の文字を避けて、左右の端の方の16か所ずつを見る
    static bool LooksLikeOwnWindow(IntPtr bits, int width, int height, int stride, double barHeightPx)
    {
        int barY = (int)(height - barHeightPx / 2);
        int paperY = height * 3 / 10;
        if (barY <= paperY || barY >= height) return false;
        int barMatches = 0, paperMatches = 0;
        for (int i = 0; i < 16; i++)
        {
            double f = i < 8 ? 0.02 + i * 0.022 : 0.82 + (i - 8) * 0.022;
            int x = Math.Clamp((int)(width * f), 0, width - 1);
            if (Near(bits, stride, x, barY, 235)) barMatches++;
            if (Near(bits, stride, x, paperY, 255)) paperMatches++;
        }
        return barMatches >= 12 && paperMatches >= 12;  // 書いた線がいくつか重なっていても見分けられるように
    }

    static bool Near(IntPtr bits, int stride, int x, int y, int gray)
    {
        int offset = y * stride + x * 4;
        for (int c = 0; c < 3; c++)
        {
            if (Math.Abs(Marshal.ReadByte(bits, offset + c) - gray) > 3) return false;
        }
        return true;
    }
}

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace InputAutomationTool.Core;

/// <summary>
/// Captures a window's pixels via PrintWindow(PW_RENDERFULLCONTENT). This works
/// for many custom-rendered apps (WPF, Chromium, DirectComposition) that expose
/// no UIA tree. Hardware-accelerated / protected apps may return a blank image —
/// that tells us OCR is not viable for the target.
/// </summary>
public static class ScreenCapture
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000; // include layered/composited windows

    /// <summary>How the window's pixels are obtained.</summary>
    public enum Mode
    {
        /// <summary>Screen copy first, falling back to PrintWindow if it looks blank.</summary>
        Auto,
        /// <summary>Copy what is literally displayed. Needs the window unobscured.</summary>
        Screen,
        /// <summary>Ask the window to re-render itself. Works when covered, but misses live child/GPU text.</summary>
        PrintWindow,
    }

    /// <summary>
    /// Default strategy. Screen-first matters because PrintWindow makes the app
    /// repaint into an off-screen DC: static labels come through, but text in a
    /// child EDIT control or a composited layer is often absent — so a pasted
    /// value can be plainly visible on screen yet missing from the bitmap.
    /// </summary>
    public static Mode CaptureMode { get; set; } = Mode.Auto;

    /// <summary>Which path produced the last capture, for logging.</summary>
    public static string LastMethod { get; private set; } = "";

    /// <summary>
    /// Returns a bitmap of the window's full extent, plus the screen-space origin
    /// (top-left) so caller can convert image coords to screen coords. Null on failure.
    /// </summary>
    public static Bitmap? CaptureWindow(nint hwnd, out int originX, out int originY)
    {
        originX = originY = 0;
        if (!GetWindowRect(hwnd, out var r))
            return null;

        int width = r.Right - r.Left;
        int height = r.Bottom - r.Top;
        if (width <= 0 || height <= 0)
            return null;

        originX = r.Left;
        originY = r.Top;

        if (CaptureMode == Mode.PrintWindow)
        {
            LastMethod = "PrintWindow";
            return ViaPrintWindow(hwnd, width, height);
        }

        var shot = ViaScreen(r.Left, r.Top, width, height);
        if (CaptureMode == Mode.Screen)
        {
            LastMethod = "screen";
            return shot;
        }

        // Auto: a blank screen copy means the window is minimised or fully
        // covered, where PrintWindow can still succeed.
        if (shot is not null && !LooksBlank(shot))
        {
            LastMethod = "screen";
            return shot;
        }

        shot?.Dispose();
        LastMethod = "PrintWindow (screen copy was blank)";
        return ViaPrintWindow(hwnd, width, height);
    }

    private static Bitmap? ViaScreen(int screenX, int screenY, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            var hdc = g.GetHdc();
            var screenDc = GetDC(nint.Zero);
            try
            {
                if (screenDc == nint.Zero)
                    return null;
                BitBlt(hdc, 0, 0, width, height, screenDc, screenX, screenY, SRCCOPY | CAPTUREBLT);
            }
            finally
            {
                if (screenDc != nint.Zero) ReleaseDC(nint.Zero, screenDc);
                g.ReleaseHdc(hdc);
            }
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            return null;
        }
    }

    private static Bitmap ViaPrintWindow(nint hwnd, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        try
        {
            if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT))
            {
                // Fall back to a plain capture (no full-content flag).
                PrintWindow(hwnd, hdc, 0);
            }
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
        return bmp;
    }

    /// <summary>Writes the last capture to disk so a miss can be inspected directly.</summary>
    public static string? SaveDebugCapture(nint hwnd, string path)
    {
        using var bmp = CaptureWindow(hwnd, out _, out _);
        if (bmp is null)
            return null;
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>True if the bitmap is entirely (or almost entirely) one color — a sign capture was blocked.</summary>
    public static bool LooksBlank(Bitmap bmp)
    {
        // Sample a grid of pixels; if they're all identical, treat as blank.
        var first = bmp.GetPixel(0, 0);
        int stepX = Math.Max(1, bmp.Width / 20);
        int stepY = Math.Max(1, bmp.Height / 20);
        for (int y = 0; y < bmp.Height; y += stepY)
            for (int x = 0; x < bmp.Width; x += stepX)
                if (bmp.GetPixel(x, y) != first)
                    return false;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        nint hdcDest, int xDest, int yDest, int w, int h,
        nint hdcSrc, int xSrc, int ySrc, int rop);
}

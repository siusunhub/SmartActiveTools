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
}

using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace InputAutomationTool.Core;

/// <summary>One recognized line of text and its bounding box in SCREEN coordinates.</summary>
public readonly record struct OcrLine(string Text, double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <summary>
/// Reads on-screen text from a window using Windows' built-in OCR engine
/// (Windows.Media.Ocr) — free, offline, no external dependencies. Used as a
/// fallback "screen driver" for apps that expose no UIA tree.
/// </summary>
public static class OcrTextReader
{
    public static bool IsAvailable => OcrEngine.TryCreateFromUserProfileLanguages() != null;

    /// <summary>
    /// Captures <paramref name="hwnd"/> and returns recognized lines with screen
    /// rectangles. <paramref name="diagnostics"/> receives human-readable status.
    /// </summary>
    public static async Task<IReadOnlyList<OcrLine>> ReadAsync(nint hwnd, IList<string>? diagnostics = null)
    {
        using var bmp = ScreenCapture.CaptureWindow(hwnd, out int ox, out int oy);
        if (bmp is null)
        {
            diagnostics?.Add("[ocr] Capture failed (could not get window bitmap).");
            return [];
        }

        diagnostics?.Add($"[ocr] Captured {bmp.Width}x{bmp.Height} px at screen ({ox},{oy}).");
        if (ScreenCapture.LooksBlank(bmp))
            diagnostics?.Add("[ocr] !! Captured image looks blank — app likely blocks screen capture (DirectX/protected).");

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            diagnostics?.Add("[ocr] No OCR language pack installed for your user profile.");
            return [];
        }

        SoftwareBitmap sw;
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Bmp);
            ms.Position = 0;
            var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream()).AsTask().ConfigureAwait(false);
            sw = await decoder.GetSoftwareBitmapAsync().AsTask().ConfigureAwait(false);
        }

        OcrResult result;
        using (sw)
            result = await engine.RecognizeAsync(sw).AsTask().ConfigureAwait(false);

        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            // Union the word boxes into a single line rectangle.
            double minX = double.MaxValue, minY = double.MaxValue, maxX = 0, maxY = 0;
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                minX = Math.Min(minX, r.X);
                minY = Math.Min(minY, r.Y);
                maxX = Math.Max(maxX, r.X + r.Width);
                maxY = Math.Max(maxY, r.Y + r.Height);
            }
            if (minX == double.MaxValue) continue;
            lines.Add(new OcrLine(line.Text, ox + minX, oy + minY, maxX - minX, maxY - minY));
        }

        diagnostics?.Add($"[ocr] Recognized {lines.Count} line(s).");
        return lines;
    }
}

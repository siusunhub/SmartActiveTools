namespace InputAutomationTool.Core;

/// <summary>
/// Shared geometry for locating the target app's on-screen Paste button relative
/// to the Win2 label. The driver clicks in this coordinate frame and the UI
/// position-picker reports offsets in it, so both must derive from the same
/// constants — which is why they live here rather than inside the driver.
/// </summary>
public static class PasteGeometry
{
    /// <summary>The input field starts this far right of the label centre.</summary>
    public const int ProbeRightPx = 50;

    /// <summary>Vertical step used when probing downward for the input field.</summary>
    public const int ProbeStepPx = 10;

    /// <summary>Extra drop from the input-field row down to the Paste button row.</summary>
    public const int PasteDownPx = 10;

    /// <summary>
    /// The origin that every stored paste offset is measured from: right of and
    /// below the Win2 label centre. A saved <c>(dx, dy)</c> means "click this
    /// point plus (dx, dy)" — being relative to OCR-found text is what lets one
    /// saved pair survive a different resolution or DPI scale.
    /// </summary>
    public static (int X, int Y) BasePoint(OcrLine label, int inputOffsetX = 0, int inputOffsetY = 0) =>
        ((int)Math.Round(label.CenterX + ProbeRightPx + inputOffsetX),
         (int)Math.Round(label.CenterY + inputOffsetY + ProbeStepPx + PasteDownPx));

    /// <summary>Converts an absolute screen point into the offset the driver stores.</summary>
    public static (int Dx, int Dy) OffsetFor(
        OcrLine label, int screenX, int screenY, int inputOffsetX = 0, int inputOffsetY = 0)
    {
        var (bx, by) = BasePoint(label, inputOffsetX, inputOffsetY);
        return (screenX - bx, screenY - by);
    }

    /// <summary>Formats an offset for the settings field, e.g. <c>"290,5"</c>.</summary>
    public static string Format(int dx, int dy) => $"{dx},{dy}";

    /// <summary>
    /// Parses <c>"290,5"</c> (also tolerating spaces, <c>x</c>/<c>;</c> separators
    /// and leading <c>+</c>) back into an offset. Returns false on anything else.
    /// </summary>
    public static bool TryParse(string? text, out int dx, out int dy)
    {
        dx = dy = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split([',', ';', 'x', 'X', ' '], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && int.TryParse(parts[0].TrimStart('+').Trim(), out dx)
            && int.TryParse(parts[1].TrimStart('+').Trim(), out dy);
    }
}

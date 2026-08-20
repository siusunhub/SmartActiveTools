using System.Runtime.InteropServices;

namespace SmartActiveTools.Core;

/// <summary>How OCR-mode enters text into the (invisible-to-OCR) input field.</summary>
public enum InputMethod
{
    Paste,        // clipboard + Ctrl+V
    Type,         // SendInput Unicode, char by char
    ScanCode,     // SendInput hardware scan codes (works on some custom/game UIs)
    PasteButton,  // clipboard, then click the on-screen Paste button (mouse only)
}

/// <summary>A screen point to click, in screen pixels (used for inferred inputs).</summary>
internal sealed record OcrClickPoint(double X, double Y);

/// <summary>
/// <see cref="IScreenDriver"/> for apps that expose no UIA tree. Detection is done
/// by OCR-ing the window (<see cref="OcrTextReader"/>) with fuzzy matching; actions
/// are performed by synthesising mouse/keyboard input at screen coordinates.
///
/// Window enumeration is delegated to <see cref="UiaScreenDriver"/> (pure Win32).
/// OCR results are cached briefly so a single poll cycle does not re-capture.
/// </summary>
public sealed class OcrScreenDriver : IScreenDriver
{
    private readonly UiaScreenDriver _win32 = new();

    /// <summary>Optional sink for detailed action logs (click/type coordinates).</summary>
    public Action<string>? Logger
    {
        get => _logger;
        set { _logger = value; _debugSink = value; }
    }
    private Action<string>? _logger;
    private static Action<string>? _debugSink; // lets static input helpers log too

    /// <summary>Extra pixel offset added to the probe base (fine-tuning).</summary>
    public int InputOffsetX { get; set; }
    public int InputOffsetY { get; set; }

    /// <summary>
    /// A hand-picked Paste button offset to try before any automatic strategy.
    /// Null restores the original behaviour (remembered position, then 2-D scan).
    /// </summary>
    public (int Dx, int Dy)? CustomPasteOffset { get; set; }

    /// <summary>
    /// Click the known Paste position and move straight on, without OCR-confirming
    /// that the value appeared. Faster and immune to OCR misreads, but it cannot
    /// detect a missed click — so it only applies when a position is already known
    /// (custom or remembered); a blind scan would "succeed" on its first probe.
    /// </summary>
    public bool SkipPasteVerify { get; set; }

    /// <summary>How text is entered into the focused field.</summary>
    public InputMethod Method { get; set; } = InputMethod.Paste;

    /// <summary>
    /// When true, probe downward (up to <see cref="ProbeMaxTries"/>) verifying via
    /// OCR that the text landed. When false, just enter text at the first position.
    /// </summary>
    public bool ShiftProbe { get; set; }

    private nint _cacheHandle;
    private DateTime _cacheTime;
    private IReadOnlyList<OcrLine> _cache = [];
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMilliseconds(600);

    public IReadOnlyList<TargetWindow> EnumerateWindows() => _win32.EnumerateWindows();
    public bool IsWindowAlive(TargetWindow window) => _win32.IsWindowAlive(window);

    private IReadOnlyList<OcrLine> GetLines(TargetWindow window, bool forceRefresh = false)
    {
        if (!forceRefresh &&
            _cacheHandle == window.Handle &&
            DateTime.UtcNow - _cacheTime < CacheTtl)
        {
            return _cache;
        }

        _cache = OcrTextReader.ReadAsync(window.Handle).GetAwaiter().GetResult();
        _cacheHandle = window.Handle;
        _cacheTime = DateTime.UtcNow;
        return _cache;
    }

    public void InvalidateCache() => _cacheTime = DateTime.MinValue;

    public UiElement? TryFindText(TargetWindow window, string textContains) =>
        FindTextLine(window, textContains) is { } l
            ? new UiElement { Name = l.Text, ControlType = "ocr.text", Native = l }
            : null;

    /// <summary>
    /// Same match as <see cref="TryFindText"/> but returns the OCR line itself,
    /// including its screen rectangle. Public so the UI's paste-position picker
    /// can draw over exactly the line the engine would have matched.
    /// </summary>
    public OcrLine? FindTextLine(TargetWindow window, string textContains)
    {
        if (string.IsNullOrWhiteSpace(textContains))
            return null;

        OcrLine? best = null;
        var bestRank = int.MaxValue;

        // Among all lines that fuzzy-contain the text, prefer the one whose full
        // string is closest to the needle ("Activation key" beats "Enter an activation key").
        foreach (var line in GetLines(window))
        {
            if (!FuzzyMatch.Contains(line.Text, textContains))
                continue;
            var rank = FuzzyMatch.FullDistance(line.Text, textContains);
            if (rank < bestRank)
            {
                bestRank = rank;
                best = line;
            }
        }

        // Reject if the best match is much longer than the needle — it merely contains
        // the needle as a substring (e.g. "use a purchased activation key" should NOT
        // match a search for "Activation key" because FullDistance 16 > needle length 14).
        return best is { } l && bestRank <= textContains.Length ? l : null;
    }

    // OCR cannot tell a button from a label, so a "button" is just matching text.
    public UiElement? FindButton(TargetWindow window, string buttonText) =>
        TryFindText(window, buttonText) is { } el
            ? new UiElement { Name = el.Name, ControlType = "ocr.button", Native = el.Native }
            : null;

    public UiElement? FindInputNear(TargetWindow window, UiElement label)
    {
        if (label.Native is not OcrLine l)
            return null;

        // We cannot see an empty textbox via OCR. SetTextAsync will probe downward
        // from this label, typing and re-OCR-ing until the text actually appears.
        Logger?.Invoke($"Input anchor \"{l.Text}\" @({l.CenterX:0},{l.CenterY:0}) — will probe downward to find the field.");
        return new UiElement { Name = "(input probe)", ControlType = "ocr.input", Native = l };
    }

    public Task<bool> InvokeAsync(UiElement element, CancellationToken ct)
    {
        if (!TryGetPoint(element, out var x, out var y))
        {
            Logger?.Invoke($"CLICK skipped: '{element.Name}' has no screen coordinates.");
            return Task.FromResult(false);
        }

        Logger?.Invoke($"CLICK at screen ({x},{y}) — target: \"{element.Name}\"");
        ClickAt(_cacheHandle, x, y);
        InvalidateCache(); // the screen probably changed
        return Task.FromResult(true);
    }

    // Vertical probe (find the input field by typing). The offsets that define the
    // paste coordinate frame live in PasteGeometry, shared with the UI picker.
    private const int ProbeRightPx = PasteGeometry.ProbeRightPx;
    private const int ProbeStepPx = PasteGeometry.ProbeStepPx;
    private const int ProbeMaxTries = 5;

    // Horizontal + vertical probe (find the on-screen Paste button).
    private const int PasteStepPx = 15;       // step right this much each inner step
    private const int PasteDownPx = PasteGeometry.PasteDownPx;
    private const int PasteStartStep = 18;    // inner loop starts ~270px right (skip the empty input bar)
    private const int PasteStartOffsetPx = 20;    // extra rightward shift at scan origin
    private const int PasteStartOffsetDownPx = 5; // extra downward shift at scan origin
    private const int PasteScanStepsX = 15;   // inner loop: scan this many steps rightward
    private const int PasteScanStepsY = 5;    // outer loop: try this many rows
    private const int PasteRowShiftPx = 10;   // outer loop: shift down this many px per row
    private const int PasteVerifyRetryMs = 200; // delay before re-reading the screen

    /// <summary>
    /// The position found by the scan, kept only in memory. The driver does no file
    /// I/O of its own: the host reads this after a run and persists it in
    /// settings.json, so there is exactly one config file on disk.
    /// </summary>
    public (int Dx, int Dy)? LearnedPasteOffset { get; private set; }

    /// <summary>Working copy for this session — seeded from settings, updated by the scan.</summary>
    private (int dx, int dy)? _pasteButtonOffset;

    /// <summary>
    /// Seeds the remembered position from persisted settings. Passing null (or a
    /// position that later fails OCR verification) leaves the driver to scan.
    /// </summary>
    public void SeedRememberedPasteOffset((int Dx, int Dy)? offset)
    {
        _pasteButtonOffset = offset is (int dx, int dy) ? (dx, dy) : null;
        LearnedPasteOffset = null;
    }

    public Task<bool> SetTextAsync(UiElement element, string value, CancellationToken ct)
    {
        // Anchored input → probe downward, verifying via OCR that the text landed.
        if (element.Native is OcrLine anchor)
            return Task.FromResult(ProbeAndType(anchor, value, ct));

        // Fallback: a fixed click point.
        if (!TryGetPoint(element, out var x, out var y))
        {
            Logger?.Invoke($"TYPE skipped: '{element.Name}' has no screen coordinates.");
            return Task.FromResult(false);
        }

        Logger?.Invoke($"{Method} \"{value}\" — click field at screen ({x},{y})");
        ClickAt(_cacheHandle, x, y);
        Thread.Sleep(80);
        SelectAllAndDelete();
        Thread.Sleep(40);
        EnterText(value);
        InvalidateCache();
        return Task.FromResult(true);
    }

    /// <summary>
    /// Finds the input field by trial: starting just right of and below the label,
    /// click → clear → type → re-OCR; if the typed text now appears on screen the
    /// field was hit. Otherwise step down <see cref="ProbeStepPx"/> and retry.
    /// </summary>
    private bool ProbeAndType(OcrLine label, string value, CancellationToken ct)
    {
        var hwnd = _cacheHandle;
        var baseX = label.CenterX + ProbeRightPx + InputOffsetX;
        var baseY = label.CenterY + InputOffsetY;

        // Paste-button method: focus the field, then find the on-screen Paste button
        // by clicking rightward (mouse only — no keyboard needed).
        if (Method == InputMethod.PasteButton)
        {
            var (fieldX, fieldY) = PasteGeometry.BasePoint(label, InputOffsetX, InputOffsetY);
            return ClickPasteButton(hwnd, fieldX, fieldY, label, value, ct);
        }

        var tries = ShiftProbe ? ProbeMaxTries : 1;

        for (int i = 1; i <= tries; i++)
        {
            ct.ThrowIfCancellationRequested();

            int x = (int)Math.Round(baseX);
            int y = (int)Math.Round(baseY + ProbeStepPx * i);

            ClickAt(hwnd, x, y);
            Thread.Sleep(80);
            SelectAllAndDelete();
            Thread.Sleep(40);
            EnterText(value);

            // Shift-probe off: trust the first position, don't verify.
            if (!ShiftProbe)
            {
                Logger?.Invoke($"Entered text at ({x},{y}) (shift-probe off, not verifying).");
                InvalidateCache();
                return true;
            }

            Thread.Sleep(150);

            // Re-OCR (fresh capture, no cache) and check the text is now on screen.
            var lines = OcrTextReader.ReadAsync(hwnd).GetAwaiter().GetResult();
            var hit = lines.Any(ln => FuzzyMatch.Contains(ln.Text, value));

            Logger?.Invoke($"Probe {i}/{ProbeMaxTries} at ({x},{y}): {(hit ? "TEXT FOUND ✓" : "not visible, going down 10px")}");

            if (!hit)
                Logger?.Invoke("   screen now: " + string.Join(" | ", lines.Take(8).Select(ln => ln.Text)));

            if (hit)
            {
                InvalidateCache();
                return true;
            }

            // Clear what we just pasted (if it went somewhere) before the next probe.
            SelectAllAndDelete();
        }

        Logger?.Invoke($"Input field not found after {ProbeMaxTries} probes.");
        InvalidateCache();
        return false;
    }

    /// <summary>
    /// Enters text via the app's on-screen Paste button (mouse only, for apps that
    /// block synthetic keyboard input). Sets the clipboard, focuses the field, then
    /// clicks rightward in <see cref="PasteStepPx"/> steps until OCR confirms the
    /// text was pasted. The winning offset is cached so later cases skip the scan.
    /// </summary>
    private bool ClickPasteButton(nint hwnd, int baseX, int baseY, OcrLine label, string value, CancellationToken ct)
    {
        // 1. Copy the text to the clipboard once.
        SetClipboardText(value);
        var readback = GetClipboardText();
        Logger?.Invoke(readback == value
            ? $"Clipboard set OK (\"{readback}\")."
            : $"!! Clipboard readback mismatch: \"{readback}\".");

        var start = DateTime.UtcNow;

        // 1a. Verification disabled: click the known position and trust it. Only
        // valid with a known offset — without one there is nothing to click but a
        // guess, and an unverified guess would report success from the first probe.
        if (SkipPasteVerify)
        {
            var known = CustomPasteOffset ?? _pasteButtonOffset;
            if (known is (int sx, int sy))
            {
                Logger?.Invoke($"Clicking paste at +{sx}px right, +{sy}px down (success check skipped).");
                ClickAt(hwnd, baseX + sx, baseY + sy);
                Thread.Sleep(PasteVerifyRetryMs);
                InvalidateCache();
                return true;
            }

            Logger?.Invoke("!! 'Skip Paste Success check' needs a known paste position — "
                         + "set a Custom Paste Icon Position first. Verifying this run instead.");
        }

        // 1b. A position the user picked by hand wins over anything learned
        // automatically. It is still OCR-verified, so a stale pick (window moved
        // to a different scale) falls through to the strategies below rather than
        // silently pasting nowhere.
        if (CustomPasteOffset is (int ux, int uy))
        {
            Logger?.Invoke($"Trying custom paste position +{ux}px right, +{uy}px down…");
            if (TryPasteAt(hwnd, baseX + ux, baseY + uy, label, value, ux, uy, start, verbose: true))
                return true;

            Logger?.Invoke("Custom position failed — falling back to the remembered position / scan.");
        }

        // Try the remembered position (from a previous run's saved config or
        // from earlier in this session). Verify with OCR — if the text doesn't
        // appear the position is stale (e.g. resolution changed) so clear and scan.
        if (_pasteButtonOffset is (int cdx, int cdy))
        {
            Logger?.Invoke($"Trying remembered paste position +{cdx}px right, +{cdy}px down…");
            if (TryPasteAt(hwnd, baseX + cdx, baseY + cdy, label, value, cdx, cdy, start, verbose: true))
                return true;

            Logger?.Invoke("Remembered position failed — discarding it and re-scanning…");
            _pasteButtonOffset = null;
        }

        // 2-D scan: outer loop shifts down (handles different screen scales /
        // resolutions), inner loop shifts right to locate the Paste button.
        Logger?.Invoke($"Scanning for Paste button ({PasteScanStepsY} rows × {PasteScanStepsX} cols)…");
        for (int row = 0; row < PasteScanStepsY; row++)
        {
            int dy = PasteRowShiftPx * row;
            for (int col = 0; col < PasteScanStepsX; col++)
            {
                ct.ThrowIfCancellationRequested();
                int dx = PasteStepPx * (PasteStartStep + col) + PasteStartOffsetPx;
                int ady = dy + PasteStartOffsetDownPx;
                if (TryPasteAt(hwnd, baseX + dx, baseY + ady, label, value, dx, ady, start))
                {
                    _pasteButtonOffset = (dx, ady);
                    LearnedPasteOffset = (dx, ady);
                    Logger?.Invoke($"Paste position learned: {PasteGeometry.Format(dx, ady)} (saved with settings).");
                    return true;
                }
            }
        }

        Logger?.Invoke("Paste button not found in scan range.");
        InvalidateCache();
        return false;
    }

    /// <summary>
    /// Clicks a candidate point and confirms via OCR that the value appeared.
    /// <paramref name="verbose"/> additionally logs the text OCR actually read —
    /// only worth it for the handful of known-position attempts, not for all 75
    /// scan probes.
    /// </summary>
    private bool TryPasteAt(
        nint hwnd, int x, int y, OcrLine label, string value, int dx, int dy, DateTime start, bool verbose = false)
    {
        ClickAt(hwnd, x, y);
        Thread.Sleep(120);
        var activationKeyX = (int)Math.Round(label.CenterX);
        var checks = ReadPasteChecks(hwnd, label, activationKeyX, y, value);
        var successful = checks.FirstOrDefault(check => check.Match.IsMatch);
        var best = checks.OrderByDescending(check => check.Match.Matched).First();
        var selected = successful ?? best;
        var match = selected.Match;

        var ms = (int)(DateTime.UtcNow - start).TotalMilliseconds;
        var verdict = match.IsMatch ? $"PASTED ✓ ({match})"
                    : match.Matched > 0 ? $"partial ({match})"
                    : "nothing";

        // Per-chunk verdicts make a miss self-explaining: which groups of the key
        // OCR could and could not read back off the screen.
        var detail = match.Detail is { Length: > 0 } d ? $"  [{d}]" : "";
        Logger?.Invoke($"Paste probe: +{dx}px right +{dy}px down, {ms}ms → {verdict}{detail}");

        foreach (var check in checks)
            Logger?.Invoke($"   {check.Name}: {check.Match}");

        if (!match.IsMatch && verbose)
            foreach (var check in checks)
                Logger?.Invoke($"   {check.Name} OCR: {Trim(check.Seen)}");

        if (match.IsMatch)
            InvalidateCache();
        return match.IsMatch;

        static string Trim(string s) =>
            string.IsNullOrWhiteSpace(s) ? "(nothing at all)"
            : s.Length <= 220 ? s
            : s[..220] + "…";
    }

    /// <summary>
    /// Runs the four tested verification variants: normal and enhanced crop
    /// immediately after Paste, then the same two after focusing the input field.
    /// One exact four-character key group is enough for <see cref="FuzzyMatch.MatchChunks"/>
    /// to confirm the paste.
    /// </summary>
    private IReadOnlyList<PasteOcrCheck> ReadPasteChecks(
        nint hwnd, OcrLine label, int activationKeyX, int activationKeyY, string value)
    {
        var checks = new List<PasteOcrCheck>();
        AddChecks("after Paste");

        ClickAt(hwnd, activationKeyX, activationKeyY);
        Thread.Sleep(120);
        AddChecks("after input focus");
        return checks;

        void AddChecks(string phase)
        {
            using var capture = ScreenCapture.CaptureWindow(hwnd, out var originX, out var originY);
            if (capture is null)
            {
                checks.Add(new PasteOcrCheck($"{phase} normal", default, "(capture failed)"));
                checks.Add(new PasteOcrCheck($"{phase} enhanced", default, "(capture failed)"));
                return;
            }

            using var crop = OcrImageProcessing.CropInputText(capture, label, originX, originY, out _);
            using var enhanced = OcrImageProcessing.EnhanceForOcr(crop);
            Add("normal", OcrTextReader.ReadBitmapAsync(crop, originX, originY).GetAwaiter().GetResult());
            Add("enhanced", OcrTextReader.ReadBitmapAsync(enhanced, originX, originY).GetAwaiter().GetResult());

            void Add(string kind, IReadOnlyList<OcrLine> lines)
            {
                var seen = string.Join(" | ", lines.Select(line => line.Text));
                checks.Add(new PasteOcrCheck($"{phase} {kind}", FuzzyMatch.MatchChunks(seen, value), seen));
            }
        }
    }

    private sealed record PasteOcrCheck(string Name, FuzzyMatch.ChunkMatch Match, string Seen);

    public IReadOnlyList<string> DumpVisibleElements(TargetWindow window)
    {
        var diag = new List<string>();
        var lines = OcrTextReader.ReadAsync(window.Handle, diag).GetAwaiter().GetResult();
        foreach (var line in lines)
            diag.Add($"[ocr] \"{line.Text}\"  @({line.CenterX:0},{line.CenterY:0})");
        return diag;
    }

    private static bool TryGetPoint(UiElement element, out int x, out int y)
    {
        switch (element.Native)
        {
            case OcrLine l:
                x = (int)Math.Round(l.CenterX);
                y = (int)Math.Round(l.CenterY);
                return true;
            case OcrClickPoint p:
                x = (int)Math.Round(p.X);
                y = (int)Math.Round(p.Y);
                return true;
            default:
                x = y = 0;
                return false;
        }
    }

    // --- input synthesis -------------------------------------------------

    private void ClickAt(nint hwnd, int screenX, int screenY)
    {
        if (hwnd != 0)
        {
            ForceForeground(hwnd);
            Thread.Sleep(60);
        }
        SetCursorPos(screenX, screenY);
        Thread.Sleep(20);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
    }

    /// <summary>
    /// Reliably brings <paramref name="hwnd"/> to the foreground. A plain
    /// SetForegroundWindow is blocked by Windows' foreground lock when another
    /// process is active, so keystrokes would never reach the target. Attaching
    /// to the target's input thread lifts that restriction.
    /// </summary>
    private void ForceForeground(nint hwnd)
    {
        var ok = WindowHelper.EnsureWindowVisibleAndForeground(hwnd);
        if (ok)
            Logger?.Invoke($"Foreground OK (target hwnd {hwnd}).");
        else
            Logger?.Invoke($"!! Foreground target hwnd {hwnd} may not be primary foreground window.");
    }

    /// <summary>Enters text into the focused field via the selected <see cref="Method"/>.</summary>
    private void EnterText(string text)
    {
        switch (Method)
        {
            case InputMethod.Type: TypeUnicode(text); break;
            case InputMethod.ScanCode: TypeScanCodes(text); break;
            default: PasteText(text); break;
        }
    }

    /// <summary>Puts <paramref name="text"/> on the clipboard and sends Ctrl+V.</summary>
    private static void PasteText(string text)
    {
        SetClipboardText(text);
        Thread.Sleep(20);
        var readback = GetClipboardText();
        _debugSink?.Invoke(readback == text
            ? $"Clipboard set OK (\"{readback}\"). Sending Ctrl+V…"
            : $"!! Clipboard readback mismatch: got \"{readback}\", expected \"{text}\".");

        SendVk(VK_CONTROL, false);
        SendVk(VK_V, false);
        SendVk(VK_V, true);
        SendVk(VK_CONTROL, true);
    }

    private static string GetClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero))
            return "<could not open clipboard>";
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero)
                return "<empty>";
            var ptr = GlobalLock(h);
            if (ptr == IntPtr.Zero)
                return "<lock failed>";
            try { return Marshal.PtrToStringUni(ptr) ?? ""; }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Types each character as a hardware scan code (most physical-like input).</summary>
    private static void TypeScanCodes(string text)
    {
        foreach (var ch in text)
        {
            var vks = VkKeyScan(ch);
            if (vks == -1) { SendChar(ch); Thread.Sleep(10); continue; } // no key for char → unicode

            var vk = (byte)(vks & 0xFF);
            var shift = (vks & 0x100) != 0;
            var scan = MapVirtualKey(vk, 0 /*MAPVK_VK_TO_VSC*/);
            if (scan == 0) { SendChar(ch); Thread.Sleep(10); continue; }

            if (shift) SendVk(VK_SHIFT, false);
            SendScan((ushort)scan, false);
            SendScan((ushort)scan, true);
            if (shift) SendVk(VK_SHIFT, true);
            Thread.Sleep(10);
        }
    }

    private static void SendScan(ushort scan, bool keyUp)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wScan = scan, dwFlags = KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0) } }
        };
        SendInputChecked([input], "scan");
    }

    /// <summary>SendInput wrapper that surfaces injection failures (0 events = blocked).</summary>
    private static uint SendInputChecked(INPUT[] inputs, string what)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            _debugSink?.Invoke($"!! SendInput({what}) injected {sent}/{inputs.Length} events — err {Marshal.GetLastWin32Error()}. Input is being BLOCKED (try Run as administrator).");
        return sent;
    }

    private static void SetClipboardText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
            return;
        try
        {
            EmptyClipboard();
            var bytes = (UIntPtr)((text.Length + 1) * 2);
            var hGlobal = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (hGlobal == IntPtr.Zero)
                return;
            var ptr = GlobalLock(hGlobal);
            if (ptr != IntPtr.Zero)
            {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * 2, 0); // null terminator
                GlobalUnlock(hGlobal);
            }
            SetClipboardData(CF_UNICODETEXT, hGlobal); // clipboard now owns hGlobal
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void SelectAllAndDelete()
    {
        // Ctrl+A then Delete.
        SendVk(VK_CONTROL, false);
        SendVk(VK_A, false);
        SendVk(VK_A, true);
        SendVk(VK_CONTROL, true);
        SendVk(VK_DELETE, false);
        SendVk(VK_DELETE, true);
    }

    private static void TypeUnicode(string text)
    {
        foreach (var ch in text)
        {
            SendChar(ch);
            Thread.Sleep(5);
        }
    }

    private static void SendChar(char ch)
    {
        var down = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } }
        };
        var up = down;
        up.U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        SendInputChecked([down, up], "unicode");
    }

    private static void SendVk(ushort vk, bool keyUp)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = keyUp ? KEYEVENTF_KEYUP : 0 } }
        };
        SendInputChecked([input], $"vk 0x{vk:X2}");
    }

    // --- P/Invoke --------------------------------------------------------

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_A = 0x41;
    private const ushort VK_V = 0x56;
    private const ushort VK_DELETE = 0x2E;
    private const int SW_SHOW = 5;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint CF_UNICODETEXT = 13;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // --- clipboard -------------------------------------------------------

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll")]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint hMem);
}

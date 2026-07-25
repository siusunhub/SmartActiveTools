using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace InputAutomationTool.Core;

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

    public UiElement? TryFindText(TargetWindow window, string textContains)
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
        // Reject if the best candidate is much longer than the needle.
        return best is { } l && bestRank <= textContains.Length
            ? new UiElement { Name = l.Text, ControlType = "ocr.text", Native = l }
            : null;
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

    // Vertical probe (find the input field by typing).
    private const int ProbeRightPx = 50;   // start this far right of the label
    private const int ProbeStepPx = 10;    // step down this much each attempt
    private const int ProbeMaxTries = 5;

    // Horizontal + vertical probe (find the on-screen Paste button).
    private const int PasteStepPx = 15;       // step right this much each inner step
    private const int PasteDownPx = 10;       // initial downward offset from the input field label
    private const int PasteStartStep = 18;    // inner loop starts ~270px right (skip the empty input bar)
    private const int PasteStartOffsetPx = 20;    // extra rightward shift at scan origin
    private const int PasteStartOffsetDownPx = 5; // extra downward shift at scan origin
    private const int PasteScanStepsX = 15;   // inner loop: scan this many steps rightward
    private const int PasteScanStepsY = 5;    // outer loop: try this many rows
    private const int PasteRowShiftPx = 10;   // outer loop: shift down this many px per row

    // Persistent position — loaded from / saved to JSON next to the executable.
    private static readonly string _configPath =
        Path.ChangeExtension(
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "config"),
            ".json");
    private (int dx, int dy)? _pasteButtonOffset;
    private bool _offsetLoaded;

    private sealed class PastePositionConfig
    {
        public string MachineName { get; set; } = "";
        public int Dx { get; set; }
        public int Dy { get; set; }
    }

    private void EnsureOffsetLoaded()
    {
        if (_offsetLoaded) return;
        _offsetLoaded = true;
        try
        {
            if (!File.Exists(_configPath)) return;
            var cfg = JsonSerializer.Deserialize<PastePositionConfig>(File.ReadAllText(_configPath));
            if (cfg == null) return;

            if (!string.Equals(cfg.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                Logger?.Invoke($"Config machine '{cfg.MachineName}' ≠ this machine '{Environment.MachineName}' — ignoring saved position.");
                return;
            }

            _pasteButtonOffset = (cfg.Dx, cfg.Dy);
            Logger?.Invoke($"Loaded paste position from config: +{cfg.Dx}px right, +{cfg.Dy}px down.");
        }
        catch { /* corrupt file — ignore, will re-scan */ }
    }

    private void SaveOffsetToFile(int dx, int dy)
    {
        try
        {
            File.WriteAllText(_configPath,
                JsonSerializer.Serialize(
                    new PastePositionConfig { MachineName = Environment.MachineName, Dx = dx, Dy = dy },
                    new JsonSerializerOptions { WriteIndented = true }));
            Logger?.Invoke($"Paste position saved to config (machine: {Environment.MachineName}).");
        }
        catch { /* non-critical */ }
    }

    private void ClearOffsetFile()
    {
        _pasteButtonOffset = null;
        _offsetLoaded = true; // don't try to re-load a file we just invalidated
        try { if (File.Exists(_configPath)) File.Delete(_configPath); } catch { }
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
            var fieldX = (int)Math.Round(baseX);
            var fieldY = (int)Math.Round(baseY + ProbeStepPx + PasteDownPx);
            return ClickPasteButton(hwnd, fieldX, fieldY, value, ct);
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
    private bool ClickPasteButton(nint hwnd, int baseX, int baseY, string value, CancellationToken ct)
    {
        // 1. Copy the text to the clipboard once.
        SetClipboardText(value);
        var readback = GetClipboardText();
        Logger?.Invoke(readback == value
            ? $"Clipboard set OK (\"{readback}\")."
            : $"!! Clipboard readback mismatch: \"{readback}\".");

        // Load the remembered position from file the first time this session.
        EnsureOffsetLoaded();
        var start = DateTime.UtcNow;

        // Try the remembered position (from a previous run's saved config or
        // from earlier in this session). Verify with OCR — if the text doesn't
        // appear the position is stale (e.g. resolution changed) so clear and scan.
        if (_pasteButtonOffset is (int cdx, int cdy))
        {
            Logger?.Invoke($"Trying remembered paste position +{cdx}px right, +{cdy}px down…");
            if (TryPasteAt(hwnd, baseX + cdx, baseY + cdy, value, cdx, cdy, start))
                return true;

            Logger?.Invoke("Remembered position failed — clearing config and re-scanning…");
            ClearOffsetFile();
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
                if (TryPasteAt(hwnd, baseX + dx, baseY + ady, value, dx, ady, start))
                {
                    _pasteButtonOffset = (dx, ady);
                    SaveOffsetToFile(dx, ady);
                    return true;
                }
            }
        }

        Logger?.Invoke("Paste button not found in scan range.");
        InvalidateCache();
        return false;
    }

    private bool TryPasteAt(nint hwnd, int x, int y, string value, int dx, int dy, DateTime start)
    {
        ClickAt(hwnd, x, y);
        Thread.Sleep(120);
        var lines = OcrTextReader.ReadAsync(hwnd).GetAwaiter().GetResult();
        var hit = lines.Any(ln => FuzzyMatch.Contains(ln.Text, value));
        var ms = (int)(DateTime.UtcNow - start).TotalMilliseconds;
        Logger?.Invoke($"Paste probe: +{dx}px right +{dy}px down, {ms}ms → {(hit ? "PASTED ✓" : "nothing")}");
        if (hit)
            InvalidateCache();
        return hit;
    }

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
        if (GetForegroundWindow() == hwnd)
            return;

        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var thisThread = GetCurrentThreadId();

        var attached = targetThread != thisThread &&
                       AttachThreadInput(thisThread, targetThread, true);
        try
        {
            ShowWindow(hwnd, SW_SHOW);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(thisThread, targetThread, false);
        }

        var fg = GetForegroundWindow();
        if (fg == hwnd)
            Logger?.Invoke($"Foreground OK (target hwnd {hwnd}).");
        else
            Logger?.Invoke($"!! Foreground is {fg}, not target {hwnd} — keystrokes may not reach it.");
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

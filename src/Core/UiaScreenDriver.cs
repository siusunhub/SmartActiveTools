using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace InputAutomationTool.Core;

/// <summary>
/// UI Automation implementation of <see cref="IScreenDriver"/>. Reads the target
/// app's real control tree, so detection is resolution/theme independent and we can
/// invoke buttons / set values directly rather than simulating pixel clicks.
/// </summary>
public sealed class UiaScreenDriver : IScreenDriver
{
    // Cap tree walks so a huge window cannot hang the UI thread of the target.
    private const int MaxNodes = 4000;


    public IReadOnlyList<TargetWindow> EnumerateWindows()
    {
        var result = new List<TargetWindow>();
        var self = Process.GetCurrentProcess().Id;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            var len = GetWindowTextLength(hWnd);
            if (len == 0)
                return true;

            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title))
                return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == self)
                return true;

            var procName = "";
            try { procName = Process.GetProcessById((int)pid).ProcessName; }
            catch { /* process may have exited */ }

            result.Add(new TargetWindow
            {
                Handle = hWnd,
                Title = title,
                ProcessId = (int)pid,
                ProcessName = procName,
            });
            return true;
        }, IntPtr.Zero);

        return result
            .GroupBy(w => w.Handle)
            .Select(g => g.First())
            .OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool IsWindowAlive(TargetWindow window) =>
        window.Handle != IntPtr.Zero && IsWindow(window.Handle);

    public UiElement? TryFindText(TargetWindow window, string textContains)
    {
        if (string.IsNullOrWhiteSpace(textContains))
            return null;

        foreach (var el in EnumerateVisible(window))
        {
            string name;
            try { name = el.Current.Name; }
            catch { continue; }

            if (!string.IsNullOrEmpty(name) &&
                name.Contains(textContains, StringComparison.OrdinalIgnoreCase))
            {
                return Wrap(el);
            }
        }
        return null;
    }

    public UiElement? FindButton(TargetWindow window, string buttonText)
    {
        if (string.IsNullOrWhiteSpace(buttonText))
            return null;

        foreach (var el in EnumerateVisible(window))
        {
            try
            {
                if (el.Current.ControlType != ControlType.Button)
                    continue;
                var name = el.Current.Name;
                if (!string.IsNullOrEmpty(name) &&
                    name.Contains(buttonText, StringComparison.OrdinalIgnoreCase))
                {
                    return Wrap(el);
                }
            }
            catch { /* element gone */ }
        }
        return null;
    }

    public UiElement? FindInputNear(TargetWindow window, UiElement label)
    {
        if (label.Native is not AutomationElement labelEl)
            return null;

        Rect labelRect;
        try { labelRect = labelEl.Current.BoundingRectangle; }
        catch { return null; }

        AutomationElement? best = null;
        var bestScore = double.MaxValue;

        foreach (var el in EnumerateVisible(window))
        {
            try
            {
                var ct = el.Current.ControlType;
                if (ct != ControlType.Edit && ct != ControlType.Document)
                    continue;
                if (!el.Current.IsEnabled)
                    continue;

                var r = el.Current.BoundingRectangle;
                if (r.IsEmpty)
                    continue;

                // Prefer controls below or to the right of the label; penalise those above/left.
                var dx = (r.Left + r.Width / 2) - (labelRect.Left + labelRect.Width / 2);
                var dy = (r.Top + r.Height / 2) - (labelRect.Top + labelRect.Height / 2);
                var distance = Math.Sqrt(dx * dx + dy * dy);
                var directionPenalty = (dy < -2 ? 400 : 0) + (dx < -labelRect.Width ? 200 : 0);
                var score = distance + directionPenalty;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = el;
                }
            }
            catch { /* element gone */ }
        }

        return best is null ? null : Wrap(best);
    }

    public Task<bool> InvokeAsync(UiElement element, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            if (element.Native is not AutomationElement el)
                return false;
            try
            {
                if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var p) && p is InvokePattern inv)
                {
                    inv.Invoke();
                    return true;
                }
                if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tp) && tp is TogglePattern toggle)
                {
                    toggle.Toggle();
                    return true;
                }
                if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp) && sp is SelectionItemPattern sel)
                {
                    sel.Select();
                    return true;
                }
            }
            catch { /* fall through */ }
            return false;
        }, ct);
    }

    public Task<bool> SetTextAsync(UiElement element, string value, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            if (element.Native is not AutomationElement el)
                return false;
            try
            {
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var p) &&
                    p is ValuePattern vp && !vp.Current.IsReadOnly)
                {
                    try { el.SetFocus(); } catch { /* not focusable; SetValue may still work */ }
                    vp.SetValue(value);
                    return true;
                }
            }
            catch { /* fall through */ }
            return false;
        }, ct);
    }

    /// <summary>
    /// Diagnostic dump: walks the UIA tree (same mechanism the detection methods
    /// use, so this shows exactly what TryFindText can and cannot see). Falls
    /// back to listing Win32 child windows if the UIA walk yields nothing.
    /// </summary>
    public IReadOnlyList<string> DumpVisibleElements(TargetWindow window)
    {
        var results = new List<string>();
        try
        {
            if (!IsWindow(window.Handle))
            {
                results.Add("ERROR: window handle is no longer valid.");
                return results;
            }

            // --- Diagnostic header: elevation mismatch is the #1 cause of an empty tree.
            var selfElevated = IsProcessElevated(Process.GetCurrentProcess().Id);
            var targetElevated = IsProcessElevated(window.ProcessId);
            results.Add($"[diag] tool elevated={selfElevated}, target elevated={targetElevated}");
            if (targetElevated && !selfElevated)
                results.Add("[diag] !! Target runs as admin but tool does NOT. UIPI blocks reading AND input. Run the tool as administrator.");

            // --- ControlView walk (what detection uses).
            var controlCount = 0;
            foreach (var el in EnumerateVisible(window, TreeWalker.ControlViewWalker))
            {
                string name = "", type = "";
                try { name = el.Current.Name; } catch { }
                try { type = el.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); } catch { }
                if (!string.IsNullOrEmpty(name))
                {
                    results.Add($"[{type}] {name}");
                    controlCount++;
                }
            }
            results.Add($"[diag] ControlView named elements: {controlCount}");

            // --- RawView walk: shows elements ControlView hides (custom frameworks).
            // The root window always contributes 1 to controlCount, so <= 1 means
            // "nothing but the window itself was found".
            if (controlCount <= 1)
            {
                results.Add("-- RawView walk: --");
                var rawNamed = 0;
                var rawTotal = 0;
                foreach (var el in EnumerateVisible(window, TreeWalker.RawViewWalker))
                {
                    rawTotal++;
                    string name = "", type = "", value = "";
                    try { name = el.Current.Name; } catch { }
                    try { type = el.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); } catch { }
                    // Some frameworks leave Name empty but expose text via ValuePattern.
                    try
                    {
                        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && p is ValuePattern vp)
                            value = vp.Current.Value;
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(value))
                    {
                        results.Add($"[raw:{type}] name='{name}' value='{value}'");
                        rawNamed++;
                    }
                }
                results.Add($"[diag] RawView: {rawTotal} total node(s), {rawNamed} with text.");
            }

            // --- Win32 children as a last resort.
            if (controlCount <= 1)
            {
                results.Add("-- Win32 child windows: --");
                EnumChildWindows(window.Handle, (hWnd, _) =>
                {
                    try
                    {
                        if (IsWindowVisible(hWnd))
                            AppendWindowText(hWnd, results);
                    }
                    catch { /* skip this child */ }
                    return true;
                }, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            results.Add($"ERROR during dump: {ex.Message}");
        }
        return results;
    }

    private static bool IsProcessElevated(int pid)
    {
        var hProcess = IntPtr.Zero;
        var hToken = IntPtr.Zero;
        try
        {
            hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero)
                return false;
            if (!OpenProcessToken(hProcess, TOKEN_QUERY, out hToken))
                return false;

            var size = Marshal.SizeOf<int>();
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetTokenInformation(hToken, TOKEN_ELEVATION, buf, size, out _))
                    return Marshal.ReadInt32(buf) != 0;
            }
            finally { Marshal.FreeHGlobal(buf); }
            return false;
        }
        catch { return false; }
        finally
        {
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
        }
    }

    private static void AppendWindowText(nint hWnd, List<string> results)
    {
        var len = GetWindowTextLength(hWnd);
        if (len <= 0) return;
        var sb = new System.Text.StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        var text = sb.ToString().Trim();
        if (string.IsNullOrEmpty(text)) return;

        var cls = new System.Text.StringBuilder(256);
        GetClassName(hWnd, cls, cls.Capacity);
        results.Add($"[{cls}] {text}");
    }

    // --- helpers ---------------------------------------------------------

    private static UiElement Wrap(AutomationElement el)
    {
        string name = "", type = "";
        try { name = el.Current.Name; } catch { }
        try { type = el.Current.ControlType.ProgrammaticName; } catch { }
        return new UiElement { Name = name, ControlType = type, Native = el };
    }

    /// <summary>Breadth-first walk of visible descendants, capped at <see cref="MaxNodes"/>.</summary>
    private static IEnumerable<AutomationElement> EnumerateVisible(TargetWindow window) =>
        EnumerateVisible(window, TreeWalker.ControlViewWalker);

    private static IEnumerable<AutomationElement> EnumerateVisible(TargetWindow window, TreeWalker walker)
    {
        AutomationElement? root;
        try { root = AutomationElement.FromHandle(window.Handle); }
        catch { yield break; }
        if (root is null)
            yield break;

        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        var count = 0;

        while (queue.Count > 0 && count < MaxNodes)
        {
            var current = queue.Dequeue();
            count++;

            var offscreen = false;
            try { offscreen = current.Current.IsOffscreen; } catch { }
            if (!offscreen)
                yield return current;

            AutomationElement? child = null;
            try { child = walker.GetFirstChild(current); } catch { }
            while (child != null && count < MaxNodes)
            {
                queue.Enqueue(child);
                try { child = walker.GetNextSibling(child); } catch { break; }
            }
        }
    }

    // --- P/Invoke --------------------------------------------------------

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    // --- elevation check -------------------------------------------------

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int TOKEN_QUERY = 0x0008;
    private const int TOKEN_ELEVATION = 20; // TOKEN_INFORMATION_CLASS.TokenElevation

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr hProcess, int access, out IntPtr hToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr hToken, int tokenInfoClass, IntPtr info, int size, out int retSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

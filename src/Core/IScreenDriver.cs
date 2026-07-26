namespace SmartActiveTools.Core;

/// <summary>
/// Abstraction over "how we see and drive the target app". The primary
/// implementation is <see cref="UiaScreenDriver"/> (UI Automation); an OCR-based
/// driver can be swapped in for apps UIA cannot inspect.
/// </summary>
public interface IScreenDriver
{
    /// <summary>Top-level windows with a visible title.</summary>
    IReadOnlyList<TargetWindow> EnumerateWindows();

    /// <summary>True if the window handle still refers to a live window.</summary>
    bool IsWindowAlive(TargetWindow window);

    /// <summary>
    /// Returns the first visible element whose name contains <paramref name="textContains"/>
    /// (case-insensitive), or null. Non-blocking — the engine handles polling/timeouts.
    /// </summary>
    UiElement? TryFindText(TargetWindow window, string textContains);

    /// <summary>Finds a Button control whose name contains <paramref name="buttonText"/>.</summary>
    UiElement? FindButton(TargetWindow window, string buttonText);

    /// <summary>Finds the editable control geometrically nearest to <paramref name="label"/>.</summary>
    UiElement? FindInputNear(TargetWindow window, UiElement label);

    /// <summary>Clicks / invokes an element. Returns true on success.</summary>
    Task<bool> InvokeAsync(UiElement element, CancellationToken ct);

    /// <summary>Sets the text value of an editable control. Returns true on success.</summary>
    Task<bool> SetTextAsync(UiElement element, string value, CancellationToken ct);

    /// <summary>
    /// Returns a diagnostic snapshot of every UIA element visible in the window:
    /// "ControlType | Name". Capped at <see cref="MaxDumpNodes"/> entries.
    /// </summary>
    IReadOnlyList<string> DumpVisibleElements(TargetWindow window);

    /// <summary>
    /// Drops any cached view of the screen so the next query re-reads it. No-op for
    /// drivers that don't cache (UIA); the OCR driver re-captures on the next call.
    /// </summary>
    void InvalidateCache() { }
}

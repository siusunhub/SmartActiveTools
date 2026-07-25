namespace InputAutomationTool.Core;

/// <summary>
/// Hardcoded fallback values. The UI may override these; an empty UI field
/// falls back to the matching constant here (see <see cref="AutomationConfig"/>).
/// </summary>
public static class DetectionDefaults
{
    /// <summary>Auto-select a target window whose title starts with this prefix (case-insensitive).</summary>
    public const string DetectWindowName = "Product Activation";

    public const string DefaultWin1DetectText = "Use a purchased activation key";
    public const string DefaultWin2DetectText = "Activation key";
    public const string DefaultWin3DetectText = "Activation failed";

    /// <summary>Win3 review screen — shown before final activation; has an Activate button.</summary>
    public const string DefaultWin3ReviewText = "Review your activation details";
    public const string DefaultActivateButtonText = "Activate";

    /// <summary>Text that marks the result screen as a success.</summary>
    public const string DefaultSuccessText = "Success";

    public const string ContinueButtonText = "Continue";
    public const string BackButtonText = "Back";
}

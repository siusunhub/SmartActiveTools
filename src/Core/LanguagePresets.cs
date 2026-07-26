namespace SmartActiveTools.Core;

/// <summary>
/// Every language-dependent string for one UI language of the target app.
/// Applying a preset fills the detect-text fields at once, so a non-English user
/// does not have to type them all by hand. <see cref="LanguagePresets.English"/>
/// doubles as the hardcoded fallback set for blank fields.
/// </summary>
public sealed record LanguagePreset(
    string Code,
    string DisplayName,
    string WindowName,
    string Win1DetectText,
    string Win2DetectText,
    string Win3FailText,
    string Win3SuccessText,
    string SuccessText,
    string ContinueButtonText,
    string ActivateButtonText,
    string BackButtonText)
{
    /// <summary>What the language dropdown shows for this preset.</summary>
    public override string ToString() => DisplayName;
}

/// <summary>
/// Built-in string sets, one per target-app language. To add a language, declare
/// a preset here and list it in <see cref="All"/>.
/// </summary>
public static class LanguagePresets
{
    /// <summary>
    /// English — also the fallback used whenever a field is left blank, so these
    /// literals are the single source of truth for "the default text".
    /// </summary>
    public static readonly LanguagePreset English = new(
        Code: "EN",
        DisplayName: "EN",
        WindowName: "Product Activation",
        Win1DetectText: "Use a purchased activation key",
        Win2DetectText: "Activation key",
        Win3FailText: "Activation failed",
        Win3SuccessText: "Review your activation details",
        SuccessText: "Success",
        ContinueButtonText: "Continue",
        ActivateButtonText: "Activate",
        BackButtonText: "Back");

    /// <summary>The set used when a field is blank.</summary>
    public static LanguagePreset Fallback => English;

    /// <summary>Every built-in preset, in dropdown order (English first).</summary>
    public static IReadOnlyList<LanguagePreset> All { get; } = [English];

    /// <summary>Looks up a preset by its short code, or null if there is no such language.</summary>
    public static LanguagePreset? Find(string? code) =>
        All.FirstOrDefault(p => string.Equals(p.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));
}

using System.Text.Json.Serialization;

namespace SmartActiveTools.Core;

/// <summary>
/// All run settings. Detect-text properties hold the raw UI value; the
/// <c>Effective*</c> properties apply the "blank → fallback language" rule,
/// where the fallback set is <see cref="LanguagePresets.Fallback"/>.
/// Every derived property is <see cref="JsonIgnoreAttribute"/>d — persisting them
/// would write the resolved defaults straight back into the file that
/// <see cref="StripDefaults"/> just cleaned.
/// </summary>
public sealed class AutomationConfig
{
    private static LanguagePreset Def => LanguagePresets.Fallback;

    public string WindowDetectName { get; set; } = Def.WindowName;

    public string Win1DetectText { get; set; } = Def.Win1DetectText;
    public string Win2DetectText { get; set; } = Def.Win2DetectText;
    public string Win3FailText { get; set; } = Def.Win3FailText;

    /// <summary>Text on the Win3 review screen (requires clicking Activate to proceed).</summary>
    public string Win3SuccText { get; set; } = Def.Win3SuccessText;
    public string ActivateButtonText { get; set; } = Def.ActivateButtonText;

    public string SuccessText { get; set; } = Def.SuccessText;

    /// <summary>Use OCR-based detection instead of UIA (for custom-rendered apps).</summary>
    public bool UseOcr { get; set; }

    /// <summary>OCR-mode: extra pixel offset added to the input-probe base (fine-tuning).</summary>
    public int InputOffsetX { get; set; }
    public int InputOffsetY { get; set; }

    /// <summary>OCR-mode: how text is entered into the field.</summary>
    public InputMethod InputMethod { get; set; } = InputMethod.Paste;

    /// <summary>OCR-mode: probe downward (5×) verifying the field, vs. trust the first position.</summary>
    public bool InputProbeShift { get; set; }

    /// <summary>
    /// OCR-mode: try a hand-picked Paste button offset before anything else.
    /// The scan and the remembered-position methods stay in place as fallbacks.
    /// </summary>
    public bool UseCustomPastePosition { get; set; }

    /// <summary>Hand-picked offset from <see cref="PasteGeometry.BasePoint"/>.</summary>
    public int CustomPasteDx { get; set; }
    public int CustomPasteDy { get; set; }

    /// <summary>
    /// Click the known paste position and go straight to Continue, without
    /// OCR-confirming the value landed. Requires a known position to be useful.
    /// </summary>
    public bool SkipPasteVerify { get; set; }

    /// <summary>
    /// The offset the 2-D scan last found, remembered so later runs skip the scan.
    /// Guarded by <see cref="RememberedPasteMachine"/> because %AppData% roams:
    /// a position learned on one screen is meaningless on another machine.
    /// </summary>
    public int? RememberedPasteDx { get; set; }
    public int? RememberedPasteDy { get; set; }
    public string RememberedPasteMachine { get; set; } = "";

    /// <summary>The remembered position, or null if unset or learned on another machine.</summary>
    [JsonIgnore]
    public (int Dx, int Dy)? RememberedPasteOffset =>
        RememberedPasteDx is { } dx && RememberedPasteDy is { } dy
        && string.Equals(RememberedPasteMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            ? (dx, dy)
            : null;

    public bool StopOnFirstSuccess { get; set; } = true;
    public bool ContinueTestingAll { get; set; }
    public int StepTimeoutSeconds { get; set; } = 1;
    public int PollIntervalMs { get; set; } = 250;

    /// <summary>How many times to look for each screen/button before giving up.</summary>
    public int DetectRetries { get; set; } = 3;

    /// <summary>Delay between detection attempts, to let the window refresh.</summary>
    public int DetectRetryDelayMs { get; set; } = 1000;

    /// <summary>Minimum seconds to keep checking for the result/verification screen (it can be slow).</summary>
    public int VerifySeconds { get; set; } = 10;

    [JsonIgnore] public TimeSpan VerifyTimeout => TimeSpan.FromSeconds(Math.Max(1, VerifySeconds));

    /// <summary>Delay between submissions; guards against target-app throttling.</summary>
    public int BetweenCasesDelayMs { get; set; }

    public string ContinueButtonText { get; set; } = Def.ContinueButtonText;
    public string BackButtonText { get; set; } = Def.BackButtonText;

    [JsonIgnore] public string EffectiveWindowName => Resolve(WindowDetectName, Def.WindowName);

    [JsonIgnore] public string EffectiveWin1 => Resolve(Win1DetectText, Def.Win1DetectText);
    [JsonIgnore] public string EffectiveWin2 => Resolve(Win2DetectText, Def.Win2DetectText);
    [JsonIgnore] public string EffectiveWin3 => Resolve(Win3FailText, Def.Win3FailText);
    [JsonIgnore] public string EffectiveWin3Review => Resolve(Win3SuccText, Def.Win3SuccessText);
    [JsonIgnore] public string EffectiveActivateButton => Resolve(ActivateButtonText, Def.ActivateButtonText);
    [JsonIgnore] public string EffectiveSuccess => Resolve(SuccessText, Def.SuccessText);
    [JsonIgnore] public string EffectiveContinueButton => Resolve(ContinueButtonText, Def.ContinueButtonText);
    [JsonIgnore] public string EffectiveBackButton => Resolve(BackButtonText, Def.BackButtonText);

    [JsonIgnore] public TimeSpan StepTimeout => TimeSpan.FromSeconds(Math.Max(1, StepTimeoutSeconds));

    /// <summary>
    /// Ensures all detect-text properties are populated.
    /// If a property is empty or whitespace, it falls back to LanguagePresets.Fallback defaults.
    /// </summary>
    public AutomationConfig EnsureDefaults()
    {
        WindowDetectName   = Resolve(WindowDetectName, Def.WindowName);
        Win1DetectText     = Resolve(Win1DetectText, Def.Win1DetectText);
        Win2DetectText     = Resolve(Win2DetectText, Def.Win2DetectText);
        Win3FailText       = Resolve(Win3FailText, Def.Win3FailText);
        Win3SuccText       = Resolve(Win3SuccText, Def.Win3SuccessText);
        ActivateButtonText = Resolve(ActivateButtonText, Def.ActivateButtonText);
        BackButtonText     = Resolve(BackButtonText, Def.BackButtonText);
        ContinueButtonText = Resolve(ContinueButtonText, Def.ContinueButtonText);
        SuccessText        = Resolve(SuccessText, Def.SuccessText);
        return this;
    }

    /// <summary>
    /// Ensures defaults are applied before saving or stripping.
    /// </summary>
    public AutomationConfig StripDefaults()
    {
        return EnsureDefaults();
    }

    private static string Resolve(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

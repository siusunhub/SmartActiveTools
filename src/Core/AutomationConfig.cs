namespace InputAutomationTool.Core;

/// <summary>
/// All run settings. Detect-text properties hold the raw UI value; the
/// <c>Effective*</c> properties apply the "blank → hardcoded default" rule.
/// </summary>
public sealed class AutomationConfig
{
    public string WindowDetectName { get; set; } = DetectionDefaults.DetectWindowName;

    public string Win1DetectText { get; set; } = DetectionDefaults.DefaultWin1DetectText;
    public string Win2DetectText { get; set; } = DetectionDefaults.DefaultWin2DetectText;
    public string Win3DetectText { get; set; } = DetectionDefaults.DefaultWin3DetectText;

    /// <summary>Text on the Win3 review screen (requires clicking Activate to proceed).</summary>
    public string Win3ReviewText { get; set; } = DetectionDefaults.DefaultWin3ReviewText;
    public string ActivateButtonText { get; set; } = DetectionDefaults.DefaultActivateButtonText;

    public string SuccessText { get; set; } = DetectionDefaults.DefaultSuccessText;

    /// <summary>Use OCR-based detection instead of UIA (for custom-rendered apps).</summary>
    public bool UseOcr { get; set; }

    /// <summary>OCR-mode: extra pixel offset added to the input-probe base (fine-tuning).</summary>
    public int InputOffsetX { get; set; }
    public int InputOffsetY { get; set; }

    /// <summary>OCR-mode: how text is entered into the field.</summary>
    public InputMethod InputMethod { get; set; } = InputMethod.Paste;

    /// <summary>OCR-mode: probe downward (5×) verifying the field, vs. trust the first position.</summary>
    public bool InputProbeShift { get; set; }

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

    public TimeSpan VerifyTimeout => TimeSpan.FromSeconds(Math.Max(1, VerifySeconds));

    /// <summary>Delay between submissions; guards against target-app throttling.</summary>
    public int BetweenCasesDelayMs { get; set; }

    public string ContinueButtonText { get; set; } = DetectionDefaults.ContinueButtonText;
    public string BackButtonText { get; set; } = DetectionDefaults.BackButtonText;

    public string EffectiveWindowName => Resolve(WindowDetectName, DetectionDefaults.DetectWindowName);

    public string EffectiveWin1 => Resolve(Win1DetectText, DetectionDefaults.DefaultWin1DetectText);
    public string EffectiveWin2 => Resolve(Win2DetectText, DetectionDefaults.DefaultWin2DetectText);
    public string EffectiveWin3 => Resolve(Win3DetectText, DetectionDefaults.DefaultWin3DetectText);
    public string EffectiveWin3Review => Resolve(Win3ReviewText, DetectionDefaults.DefaultWin3ReviewText);
    public string EffectiveActivateButton => Resolve(ActivateButtonText, DetectionDefaults.DefaultActivateButtonText);
    public string EffectiveSuccess => Resolve(SuccessText, DetectionDefaults.DefaultSuccessText);

    public TimeSpan StepTimeout => TimeSpan.FromSeconds(Math.Max(1, StepTimeoutSeconds));

    /// <summary>
    /// Returns a shallow copy with any text field that equals its default replaced by "".
    /// Keeps the JSON clean for non-English users — only customised values are stored.
    /// </summary>
    public AutomationConfig StripDefaults()
    {
        static string Keep(string value, string def) =>
            string.Equals(value?.Trim(), def, StringComparison.OrdinalIgnoreCase) ? "" : value;

        return new AutomationConfig
        {
            WindowDetectName    = Keep(WindowDetectName,    DetectionDefaults.DetectWindowName),
            Win1DetectText      = Keep(Win1DetectText,      DetectionDefaults.DefaultWin1DetectText),
            Win2DetectText      = Keep(Win2DetectText,      DetectionDefaults.DefaultWin2DetectText),
            Win3DetectText      = Keep(Win3DetectText,      DetectionDefaults.DefaultWin3DetectText),
            Win3ReviewText      = Keep(Win3ReviewText,      DetectionDefaults.DefaultWin3ReviewText),
            ActivateButtonText  = Keep(ActivateButtonText,  DetectionDefaults.DefaultActivateButtonText),
            BackButtonText      = Keep(BackButtonText,      DetectionDefaults.BackButtonText),
            ContinueButtonText  = Keep(ContinueButtonText,  DetectionDefaults.ContinueButtonText),
            SuccessText         = Keep(SuccessText,         DetectionDefaults.DefaultSuccessText),

            // non-text fields copied as-is
            UseOcr              = UseOcr,
            InputOffsetX        = InputOffsetX,
            InputOffsetY        = InputOffsetY,
            InputMethod         = InputMethod,
            InputProbeShift     = InputProbeShift,
            StopOnFirstSuccess  = StopOnFirstSuccess,
            ContinueTestingAll  = ContinueTestingAll,
            StepTimeoutSeconds  = StepTimeoutSeconds,
            PollIntervalMs      = PollIntervalMs,
            DetectRetries       = DetectRetries,
            DetectRetryDelayMs  = DetectRetryDelayMs,
            VerifySeconds       = VerifySeconds,
            BetweenCasesDelayMs = BetweenCasesDelayMs,
        };
    }

    private static string Resolve(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

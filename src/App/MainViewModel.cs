using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using InputAutomationTool.Core;
using Microsoft.Win32;
using InputMethod = InputAutomationTool.Core.InputMethod;

namespace InputAutomationTool.App;

public sealed class MainViewModel : ObservableObject
{
    // This tool targets custom-rendered apps with no UIA tree, so detection is
    // always OCR-based and text is entered via the on-screen Paste button.
    private readonly OcrScreenDriver _ocr = new();
    private IScreenDriver _driver => _ocr;

    private CancellationTokenSource? _cts;
    private PauseTokenSource? _pause;

    public MainViewModel()
    {
        var cfg = SettingsStore.Load(out var settingsLogs);
        foreach (var entry in settingsLogs)
        {
            Log.Add(entry);
        }

        _windowDetectName = cfg.WindowDetectName;
        _win1 = cfg.Win1DetectText;
        _win2 = cfg.Win2DetectText;
        _win3 = cfg.Win3FailText;
        _win3Review = cfg.Win3SuccText;
        _activateButtonText = cfg.ActivateButtonText;
        _backButtonText = cfg.BackButtonText;
        _continueButtonText = cfg.ContinueButtonText;
        _successText = cfg.SuccessText;
        _stopOnFirstSuccess = cfg.StopOnFirstSuccess;
        _continueTestingAll = cfg.ContinueTestingAll;
        _stepTimeoutSeconds = cfg.StepTimeoutSeconds;
        _betweenCasesDelayMs = cfg.BetweenCasesDelayMs;
        _detectRetries = cfg.DetectRetries;
        _detectRetryDelayMs = cfg.DetectRetryDelayMs;
        _verifySeconds = cfg.VerifySeconds;
        _inputProbeShift = cfg.InputProbeShift;
        _useCustomPastePosition = cfg.UseCustomPastePosition;
        _skipPasteVerify = cfg.SkipPasteVerify;
        _customPastePositionText = PasteGeometry.Format(cfg.CustomPasteDx, cfg.CustomPasteDy);
        _rememberedPasteDx = cfg.RememberedPasteDx;
        _rememberedPasteDy = cfg.RememberedPasteDy;
        _rememberedPasteMachine = cfg.RememberedPasteMachine;

        StartCommand = new RelayCommand(OnStart, () => !IsRunning && SelectedWindow != null);
        PauseCommand = new RelayCommand(OnPauseToggle, () => IsRunning);
        StopCommand = new RelayCommand(OnStop, () => IsRunning);
        RefreshCommand = new RelayCommand(RefreshWindows, () => !IsRunning);
        ExportCommand = new RelayCommand(OnExport, () => Results.Count > 0 && !IsRunning);
        DumpUiaCommand = new RelayCommand(OnDumpUia, () => SelectedWindow != null);
        OcrTextCommand = new RelayCommand(OnOcrText, () => SelectedWindow != null);
        ClearInputCommand = new RelayCommand(() => InputText = "", () => !IsRunning);
        ApplyLanguageCommand = new RelayCommand(OnApplyLanguage, () => !IsRunning && SelectedLanguage != null);
        SetCustomPasteCommand = new RelayCommand(OnSetCustomPaste, () => !IsRunning && SelectedWindow != null);
        RetryStepCommand = new RelayCommand(() => Decide(StepDecision.Retry));
        ContinueStepCommand = new RelayCommand(() => Decide(StepDecision.Continue));
        AbortStepCommand = new RelayCommand(() => Decide(StepDecision.Abort));

        Log.CollectionChanged += (_, _) => Raise(nameof(LogText));

        RefreshWindows();
    }

    // --- bound collections -------------------------------------------------

    public ObservableCollection<TargetWindow> Windows { get; } = [];
    public ObservableCollection<LogEntry> Log { get; } = [];
    public ObservableCollection<TestResult> Results { get; } = [];

    /// <summary>Whole log as plain text, for a copy/paste-friendly TextBox.</summary>
    public string LogText => string.Join(Environment.NewLine, Log.Select(e => e.ToString()));

    private TargetWindow? _selectedWindow;
    public TargetWindow? SelectedWindow
    {
        get => _selectedWindow;
        set => Set(ref _selectedWindow, value);
    }

    // --- window auto-select ------------------------------------------------

    private string _windowDetectName;
    public string WindowDetectName { get => _windowDetectName; set => Set(ref _windowDetectName, value); }

    public string WindowDetectDefault => LanguagePresets.Fallback.WindowName;

    // --- language presets --------------------------------------------------

    /// <summary>Built-in detect-text sets offered in the language dropdown.</summary>
    public IReadOnlyList<LanguagePreset> Languages { get; } = LanguagePresets.All;

    /// <summary>Null until the user picks one, so the dropdown starts empty.</summary>
    private LanguagePreset? _selectedLanguage;
    public LanguagePreset? SelectedLanguage { get => _selectedLanguage; set => Set(ref _selectedLanguage, value); }

    // --- detect-text fields ------------------------------------------------

    private string _win1;
    public string Win1Text { get => _win1; set => Set(ref _win1, value); }

    private string _win2;
    public string Win2Text { get => _win2; set => Set(ref _win2, value); }

    private string _win3;
    public string Win3Text { get => _win3; set => Set(ref _win3, value); }

    private string _win3Review;
    public string Win3ReviewText { get => _win3Review; set => Set(ref _win3Review, value); }

    private string _activateButtonText;
    public string ActivateButtonText { get => _activateButtonText; set => Set(ref _activateButtonText, value); }

    private string _backButtonText;
    public string BackButtonText { get => _backButtonText; set => Set(ref _backButtonText, value); }

    private string _continueButtonText;
    public string ContinueButtonText { get => _continueButtonText; set => Set(ref _continueButtonText, value); }

    private string _successText;
    public string SuccessText { get => _successText; set => Set(ref _successText, value); }

    // placeholder hints showing the hardcoded defaults
    public string Win1Default => LanguagePresets.Fallback.Win1DetectText;
    public string Win2Default => LanguagePresets.Fallback.Win2DetectText;
    public string Win3Default => LanguagePresets.Fallback.Win3FailText;

    // --- options -----------------------------------------------------------

    private bool _stopOnFirstSuccess;
    public bool StopOnFirstSuccess
    {
        get => _stopOnFirstSuccess;
        set
        {
            if (Set(ref _stopOnFirstSuccess, value))
            {
                if (value)
                    ContinueTestingAll = false;
                else if (!ContinueTestingAll)
                    ContinueTestingAll = true;
            }
        }
    }

    private bool _continueTestingAll;
    public bool ContinueTestingAll
    {
        get => _continueTestingAll;
        set
        {
            if (Set(ref _continueTestingAll, value))
            {
                if (value)
                    StopOnFirstSuccess = false;
                else if (!StopOnFirstSuccess)
                    StopOnFirstSuccess = true;
            }
        }
    }

    private int _stepTimeoutSeconds;
    public int StepTimeoutSeconds { get => _stepTimeoutSeconds; set => Set(ref _stepTimeoutSeconds, value); }

    private int _betweenCasesDelayMs;
    public int BetweenCasesDelayMs { get => _betweenCasesDelayMs; set => Set(ref _betweenCasesDelayMs, value); }

    private int _detectRetries;
    public int DetectRetries { get => _detectRetries; set => Set(ref _detectRetries, value); }

    private int _detectRetryDelayMs;
    public int DetectRetryDelayMs { get => _detectRetryDelayMs; set => Set(ref _detectRetryDelayMs, value); }

    private int _verifySeconds;
    public int VerifySeconds { get => _verifySeconds; set => Set(ref _verifySeconds, value); }

    private bool _inputProbeShift;
    public bool InputProbeShift { get => _inputProbeShift; set => Set(ref _inputProbeShift, value); }

    // --- custom paste position ---------------------------------------------

    private bool _useCustomPastePosition;
    public bool UseCustomPastePosition { get => _useCustomPastePosition; set => Set(ref _useCustomPastePosition, value); }

    /// <summary>Click the saved position and continue without OCR-verifying the paste.</summary>
    private bool _skipPasteVerify;
    public bool SkipPasteVerify { get => _skipPasteVerify; set => Set(ref _skipPasteVerify, value); }

    /// <summary>The offset as editable "dx,dy" text; parsed in <see cref="BuildConfig"/>.</summary>
    private string _customPastePositionText;
    public string CustomPastePositionText { get => _customPastePositionText; set => Set(ref _customPastePositionText, value); }

    // The scan-learned position. Not user-editable — round-tripped through
    // settings.json so the driver itself never touches the disk.
    private int? _rememberedPasteDx;
    private int? _rememberedPasteDy;
    private string _rememberedPasteMachine = "";

    private string _inputText = "";
    public string InputText { get => _inputText; set => Set(ref _inputText, value); }

    // --- manual step decision (paste verification override) ----------------

    private TaskCompletionSource<StepDecision>? _decisionTcs;

    private bool _isAwaitingDecision;
    public bool IsAwaitingDecision { get => _isAwaitingDecision; private set => Set(ref _isAwaitingDecision, value); }

    private string _decisionPrompt = "";
    public string DecisionPrompt { get => _decisionPrompt; private set => Set(ref _decisionPrompt, value); }

    /// <summary>Called by the engine (on a background thread) to ask the user what to do.</summary>
    private Task<StepDecision> RequestDecisionAsync(string prompt)
    {
        var tcs = new TaskCompletionSource<StepDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.Current.Dispatcher.Invoke(() =>
        {
            _decisionTcs = tcs;
            DecisionPrompt = prompt;
            IsAwaitingDecision = true;
        });
        return tcs.Task;
    }

    private void Decide(StepDecision decision)
    {
        IsAwaitingDecision = false;
        DecisionPrompt = "";
        _decisionTcs?.TrySetResult(decision);
        _decisionTcs = null;
    }

    // --- run state ---------------------------------------------------------

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) Raise(nameof(IsIdle)); }
    }

    public bool IsIdle => !_isRunning;

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        private set { if (Set(ref _isPaused, value)) Raise(nameof(PauseButtonText)); }
    }

    public string PauseButtonText => _isPaused ? "Resume" : "Pause";

    private int _progressCurrent;
    public int ProgressCurrent { get => _progressCurrent; private set { if (Set(ref _progressCurrent, value)) Raise(nameof(ProgressText)); } }

    private int _progressTotal;
    public int ProgressTotal { get => _progressTotal; private set { if (Set(ref _progressTotal, value)) Raise(nameof(ProgressText)); } }

    public string ProgressText => $"Progress: {ProgressCurrent} / {ProgressTotal}";

    private string _statusText = "Ready.";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    // --- commands ----------------------------------------------------------

    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand DumpUiaCommand { get; }
    public ICommand OcrTextCommand { get; }
    public ICommand ClearInputCommand { get; }
    public ICommand ApplyLanguageCommand { get; }
    public ICommand SetCustomPasteCommand { get; }
    public ICommand RetryStepCommand { get; }
    public ICommand ContinueStepCommand { get; }
    public ICommand AbortStepCommand { get; }

    private void RefreshWindows()
    {
        var previous = SelectedWindow?.Handle;
        Windows.Clear();
        foreach (var w in _driver.EnumerateWindows())
            Windows.Add(w);

        // Always prefer the configured window name prefix (so Refresh snaps back
        // to the default window even if something else was previously selected).
        TargetWindow? reselect = null;
        var prefix = WindowDetectName?.Trim() ?? "";
        if (prefix.Length > 0)
            reselect = Windows.FirstOrDefault(w =>
                w.Title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        // Fall back to the previously selected window handle if still open.
        if (reselect is null && previous != default)
            reselect = Windows.FirstOrDefault(w => w.Handle == previous);

        SelectedWindow = reselect ?? Windows.FirstOrDefault();
    }

    /// <summary>
    /// Overwrites every detect-text field from the selected language preset.
    /// The window name is deliberately left alone: it is matched against the live
    /// window list, not the target app's own UI text.
    /// </summary>
    private void OnApplyLanguage()
    {
        if (SelectedLanguage is not { } preset)
            return;

        Win1Text = preset.Win1DetectText;
        Win2Text = preset.Win2DetectText;
        Win3Text = preset.Win3FailText;
        Win3ReviewText = preset.Win3SuccessText;
        ContinueButtonText = preset.ContinueButtonText;
        ActivateButtonText = preset.ActivateButtonText;
        BackButtonText = preset.BackButtonText;

        StatusText = $"Detect texts filled from the {preset.Code} preset.";
    }

    /// <summary>
    /// Lets the user point at the Paste button. Locates the Win2 label first —
    /// offsets are stored relative to it, so without that anchor there is nothing
    /// to measure from and the target app must be on its Paste Key screen.
    /// </summary>
    private async void OnSetCustomPaste()
    {
        var target = SelectedWindow;
        if (target is null)
        {
            StatusText = "Select a target window first.";
            return;
        }

        var win2 = (Win2Text?.Trim() is { Length: > 0 } t) ? t : Win2Default;

        StatusText = $"Looking for '{win2}' on screen…";
        var label = await RunOnStaThreadAsync(() =>
        {
            _ocr.InvalidateCache();
            return _ocr.FindTextLine(target, win2);
        });

        if (label is not { } anchor)
        {
            StatusText = "Could not find the input screen.";
            MessageBox.Show(
                $"Could not find \"{win2}\" on the target window.\n\n" +
                "Open the Paste Key screen in the target app first, then press Set again.",
                "Input Screen Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var picker = new PastePickerWindow(anchor, _ocr.InputOffsetX, _ocr.InputOffsetY)
        {
            Owner = Application.Current?.MainWindow,
        };

        if (picker.ShowDialog() == true && picker.Picked is (int dx, int dy))
        {
            CustomPastePositionText = PasteGeometry.Format(dx, dy);
            UseCustomPastePosition = true;
            var (bx, by) = PasteGeometry.BasePoint(anchor, _ocr.InputOffsetX, _ocr.InputOffsetY);
            Log.Add(LogEntry.Info(
                $"Custom paste position set to +{dx}px right, +{dy}px down " +
                $"(anchor \"{anchor.Text}\", base point {bx},{by})."));
            StatusText = $"Custom paste position saved: {dx},{dy}";
            SaveConfig();
        }
        else
        {
            StatusText = "Paste position pick cancelled.";
        }
    }

    public void SaveConfig()
    {
        try
        {
            var cfg = BuildConfig();
            SettingsStore.Save(cfg);
        }
        catch { /* non-fatal */ }
    }

    private AutomationConfig BuildConfig()
    {
        // An unparseable offset is treated as "not set" rather than silently
        // becoming (0,0), which would click the input field instead of Paste.
        var hasOffset = PasteGeometry.TryParse(CustomPastePositionText, out var dx, out var dy);

        return new AutomationConfig
        {
            WindowDetectName = WindowDetectName,
            Win1DetectText = Win1Text,
            Win2DetectText = Win2Text,
            Win3FailText = Win3Text,
            Win3SuccText = Win3ReviewText,
            ActivateButtonText = ActivateButtonText,
            BackButtonText = BackButtonText,
            ContinueButtonText = ContinueButtonText,
            SuccessText = SuccessText,
            StopOnFirstSuccess = StopOnFirstSuccess,
            ContinueTestingAll = ContinueTestingAll,
            StepTimeoutSeconds = StepTimeoutSeconds,
            BetweenCasesDelayMs = BetweenCasesDelayMs,
            UseOcr = true,
            DetectRetries = DetectRetries,
            DetectRetryDelayMs = DetectRetryDelayMs,
            VerifySeconds = VerifySeconds,
            InputMethod = InputMethod.PasteButton,
            InputProbeShift = InputProbeShift,
            UseCustomPastePosition = UseCustomPastePosition && hasOffset,
            SkipPasteVerify = SkipPasteVerify,
            CustomPasteDx = dx,
            CustomPasteDy = dy,
            RememberedPasteDx = _rememberedPasteDx,
            RememberedPasteDy = _rememberedPasteDy,
            RememberedPasteMachine = _rememberedPasteMachine,
        };
    }

    private static List<string> ParseInputs(string text) =>
        text.Replace("\r\n", "\n").Split('\n')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

    private async void OnStart()
    {
        var target = SelectedWindow;
        if (target is null)
        {
            StatusText = "Select a target window first.";
            return;
        }

        var inputs = ParseInputs(InputText);
        if (inputs.Count == 0)
        {
            StatusText = "Enter at least one input string.";
            return;
        }

        // Warn if the selected window title doesn't match the configured name prefix.
        var prefix = WindowDetectName?.Trim() ?? "";
        if (prefix.Length > 0 &&
            !target.Title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var answer = MessageBox.Show(
                $"The selected window:\n  \"{target.Title}\"\n\ndoes not match the expected window name:\n  \"{prefix}\"\n\nContinue anyway?",
                "Window Mismatch",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        var cfg = BuildConfig();
        SettingsStore.Save(cfg);

        Log.Clear();
        Results.Clear();
        ProgressCurrent = 0;
        ProgressTotal = inputs.Count;

        _cts = new CancellationTokenSource();
        _pause = new PauseTokenSource();
        IsRunning = true;
        IsPaused = false;
        StatusText = "Running…";

        var logProgress = new Progress<LogEntry>(e => Log.Add(e));
        var runProgress = new Progress<ProgressInfo>(p =>
        {
            ProgressCurrent = p.Current;
            ProgressTotal = p.Total;
        });

        // Route the OCR driver's detailed click/type logs into the status log.
        _ocr.Logger = msg => ((IProgress<LogEntry>)logProgress).Report(LogEntry.Info(msg));
        _ocr.Method = InputMethod.PasteButton;
        _ocr.ShiftProbe = InputProbeShift;
        _ocr.CustomPasteOffset = cfg.UseCustomPastePosition
            ? (cfg.CustomPasteDx, cfg.CustomPasteDy)
            : null;
        _ocr.SkipPasteVerify = cfg.SkipPasteVerify;
        // The driver does no file I/O: hand it the persisted position, take back
        // whatever it learns, and save that through settings.json below.
        _ocr.SeedRememberedPasteOffset(cfg.RememberedPasteOffset);

        // The engine reports results via the shared list; mirror into the bound collection.
        var resultSink = new List<TestResult>();
        var engine = new AutomationEngine(_driver) { OnStepNeedsDecision = RequestDecisionAsync };
        var token = _cts.Token;
        var pause = _pause;

        try
        {
            await RunOnStaThreadAsync(() => engine.RunAsync(
                inputs, cfg, target, logProgress, runProgress, resultSink, token, pause).GetAwaiter().GetResult());
            StatusText = "Completed.";
        }
        catch (OperationCanceledException)
        {
            Log.Add(LogEntry.Info("Stopped by user."));
            StatusText = "Stopped.";
        }
        catch (StepException ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            Log.Add(LogEntry.Error($"Unexpected error: {ex.Message}"));
            StatusText = "Error.";
        }
        finally
        {
            foreach (var r in resultSink)
                if (!Results.Contains(r))
                    Results.Add(r);

            // Persist a newly-scanned paste position so the next run can skip the
            // scan. Stamped with the machine name because %AppData% roams.
            if (_ocr.LearnedPasteOffset is (int ldx, int ldy)
                && (_rememberedPasteDx != ldx || _rememberedPasteDy != ldy))
            {
                _rememberedPasteDx = ldx;
                _rememberedPasteDy = ldy;
                _rememberedPasteMachine = Environment.MachineName;
                SettingsStore.Save(BuildConfig());
            }

            IsRunning = false;
            IsPaused = false;
            IsAwaitingDecision = false;
            _decisionTcs = null;
            _cts?.Dispose();
            _cts = null;
            _pause = null;
        }
    }

    private void OnPauseToggle()
    {
        if (_pause is null)
            return;
        if (IsPaused)
        {
            _pause.Resume();
            IsPaused = false;
            StatusText = "Running…";
        }
        else
        {
            _pause.Pause();
            IsPaused = true;
            StatusText = "Paused (will halt before the next step).";
        }
    }

    private void OnStop()
    {
        _pause?.Resume();   // release any pause so cancellation can propagate
        _decisionTcs?.TrySetResult(StepDecision.Abort); // unblock a pending decision
        _cts?.Cancel();
        StatusText = "Stopping…";
    }

    private async void OnDumpUia()
    {
        var target = SelectedWindow;
        if (target is null) return;

        try
        {
            Log.Add(LogEntry.Info($"--- UIA dump: {target.Title} ---"));
            StatusText = "Dumping UIA tree…";

            // Save the exact bitmap OCR works from. If text is visible on screen but
            // absent from this PNG, the capture is at fault, not the recognition.
            var shot = await RunOnStaThreadAsync(() =>
                ScreenCapture.SaveDebugCapture(target.Handle, SettingsStore.CapturePath));
            Log.Add(shot is null
                ? LogEntry.Error("Could not save the OCR capture.")
                : LogEntry.Info($"OCR capture ({ScreenCapture.LastMethod}) saved: {shot}"));

            var elements = await RunOnStaThreadAsync(() => _driver.DumpVisibleElements(target));

            if (elements.Count == 0)
                Log.Add(LogEntry.Error("No elements found at all."));
            else
            {
                foreach (var e in elements)
                    Log.Add(LogEntry.Info(e));
            }

            Log.Add(LogEntry.Info($"--- {elements.Count} element(s) total ---"));

            // If UIA saw nothing but the window itself, try OCR.
            var uiaEmpty = elements.All(e =>
                e.StartsWith("[diag]") || e.StartsWith("[Window]") || e.StartsWith("--") || e.StartsWith("[raw:Window]"));
            if (uiaEmpty)
            {
                Log.Add(LogEntry.Info("--- UIA empty; running OCR ---"));
                StatusText = "Running OCR…";
                var diag = new List<string>();
                var lines = await OcrTextReader.ReadAsync(target.Handle, diag);
                foreach (var d in diag)
                    Log.Add(LogEntry.Info(d));
                foreach (var line in lines)
                    Log.Add(LogEntry.Info($"[ocr] \"{line.Text}\"  @({line.CenterX:0},{line.CenterY:0})"));
                Log.Add(LogEntry.Info($"--- OCR: {lines.Count} line(s) ---"));
            }

            StatusText = "Dump complete.";
        }
        catch (Exception ex)
        {
            Log.Add(LogEntry.Error($"Dump failed: {ex}"));
            StatusText = "Dump failed.";
        }
    }

    private void OnOcrText()
    {
        if (SelectedWindow is not { } target)
            return;

        new OcrDebugWindow(target)
        {
            Owner = Application.Current.MainWindow
        }.Show();
    }

    /// <summary>
    /// Runs <paramref name="func"/> on a background STA thread.
    /// UIA COM servers are apartment-threaded; calling them from an MTA thread-pool
    /// thread (Task.Run) causes COM to marshal back to the UI STA, which can deadlock
    /// or hard-crash with no managed exception.
    /// </summary>
    private static Task RunOnStaThreadAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static Task<T> RunOnStaThreadAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private void OnExport()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"results_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("Index,Input,Outcome,Reason,Timestamp");
        foreach (var r in Results)
            sb.AppendLine($"{r.Index},{Csv(r.Input)},{r.Outcome},{Csv(r.Reason ?? "")},{r.Timestamp:yyyy-MM-dd HH:mm:ss}");

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            StatusText = $"Exported {Results.Count} results.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save file: {ex.Message}", "Export failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}

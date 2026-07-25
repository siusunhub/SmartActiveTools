namespace InputAutomationTool.Core;

/// <summary>Thrown when a workflow step cannot be completed within its limits.</summary>
public sealed class StepException(string message) : Exception(message);

/// <summary>Which screen of the workflow the target window is currently showing.</summary>
public enum Screen { Unknown, Win1, Win2, Result }

/// <summary>User's choice when a step can't be auto-verified.</summary>
public enum StepDecision { Retry, Continue, Abort }

/// <summary>
/// Drives the per-input-string workflow against the target window. UI-agnostic:
/// progress and log flow out via <see cref="IProgress{T}"/>; control flows in via
/// <see cref="CancellationToken"/> (Stop) and <see cref="PauseTokenSource"/> (Pause).
/// </summary>
public sealed class AutomationEngine(IScreenDriver driver)
{
    private readonly IScreenDriver _driver = driver;

    /// <summary>
    /// Invoked when a step cannot be auto-verified (e.g. OCR couldn't confirm the
    /// pasted text). Returns the user's choice. If null, the engine aborts the case.
    /// </summary>
    public Func<string, Task<StepDecision>>? OnStepNeedsDecision { get; set; }

    public async Task RunAsync(
        IReadOnlyList<string> inputs,
        AutomationConfig cfg,
        TargetWindow target,
        IProgress<LogEntry> log,
        IProgress<ProgressInfo> progress,
        IList<TestResult> results,
        CancellationToken ct,
        PauseTokenSource pause)
    {
        log.Report(LogEntry.Info($"Loaded {inputs.Count} test strings"));
        log.Report(LogEntry.Info($"Detection mode: {(cfg.UseOcr ? "OCR (screen text)" : "UI Automation")}"));
        progress.Report(new ProgressInfo(0, inputs.Count));

        for (var i = 0; i < inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await pause.WaitWhilePausedAsync(ct).ConfigureAwait(false);

            var input = inputs[i];
            var number = i + 1;
            log.Report(LogEntry.Info($"Trying {number}/{inputs.Count}: {input}"));

            if (!_driver.IsWindowAlive(target))
                throw new StepException("Target window is no longer available");

            TestResult result;
            try
            {
                var (outcome, reason) = await ProcessOneAsync(input, cfg, target, log, ct, pause)
                    .ConfigureAwait(false);
                result = new TestResult(number, input, outcome, reason, DateTime.Now);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (StepException ex)
            {
                log.Report(LogEntry.Error($"Error on '{input}': {ex.Message}. Stopped at case {number}/{inputs.Count}."));
                results.Add(new TestResult(number, input, Outcome.Error, ex.Message, DateTime.Now));
                progress.Report(new ProgressInfo(number, inputs.Count));
                throw; // surface to caller; state is saved in `results`
            }

            results.Add(result);
            progress.Report(new ProgressInfo(number, inputs.Count));

            if (result.Outcome == Outcome.Pass)
            {
                log.Report(LogEntry.Success($"'{input}' SUCCEEDED"));
                if (cfg.StopOnFirstSuccess && !cfg.ContinueTestingAll)
                {
                    log.Report(LogEntry.Success("Stopping: success found (stop-on-first-success enabled)."));
                    return;
                }
            }
            else
            {
                log.Report(LogEntry.Fail($"'{input}' FAILED"));
            }

            if (cfg.BetweenCasesDelayMs > 0)
                await Task.Delay(cfg.BetweenCasesDelayMs, ct).ConfigureAwait(false);
        }

        log.Report(LogEntry.Info("All test strings processed."));
    }

    private async Task<(Outcome, string?)> ProcessOneAsync(
        string input, AutomationConfig cfg, TargetWindow target,
        IProgress<LogEntry> log, CancellationToken ct, PauseTokenSource pause)
    {
        // 1. Detect the current screen (so we can start from the Win1 menu OR an
        //    already-open input screen). A leftover result screen → click Back first.
        var screen = Screen.Unknown;
        await StepAsync("Detect current screen", async () =>
        {
            screen = await DetectScreenAsync(target, cfg, log, ct).ConfigureAwait(false);
            if (screen == Screen.Result)
            {
                var back = _driver.FindButton(target, cfg.BackButtonText);
                if (back != null)
                {
                    await _driver.InvokeAsync(back, ct).ConfigureAwait(false);
                    await WaitForTextGoneAsync(target, cfg.EffectiveSuccess, cfg, log, ct).ConfigureAwait(false);
                    await WaitForTextGoneAsync(target, cfg.EffectiveWin3, cfg, log, ct).ConfigureAwait(false);
                    screen = await DetectScreenAsync(target, cfg, log, ct).ConfigureAwait(false);
                }
            }
            if (screen is Screen.Win1 or Screen.Win2)
                return true;
            log.Report(LogEntry.Error("Could not determine the starting screen. The driver saw:"));
            foreach (var seen in _driver.DumpVisibleElements(target).Take(50))
                log.Report(LogEntry.Info("   " + seen));
            return false;
        }, log, ct).ConfigureAwait(false);
        log.Report(LogEntry.Info($"Starting screen: {screen}"));

        await pause.WaitWhilePausedAsync(ct).ConfigureAwait(false);

        // 2. If on the Win1 menu, open the input screen.
        if (screen == Screen.Win1)
        {
            UiElement? win1 = null;
            if (await StepAsync($"Detect Win1 ('{cfg.EffectiveWin1}')",
                    async () => (win1 = await WaitForTextAsync(target, cfg.EffectiveWin1, cfg, log, ct)) != null,
                    log, ct).ConfigureAwait(false) && win1 != null)
            {
                await StepAsync($"Click '{cfg.EffectiveWin1}'",
                    () => _driver.InvokeAsync(win1, ct), log, ct).ConfigureAwait(false);
                // Win1 text can persist on Win2 as a label, so waiting for it to vanish
                // blocks forever. Just pause briefly and let Win2 detection handle timing.
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
        }
        else
        {
            log.Report(LogEntry.Info("Already on the input screen — skipping the Win1 step."));
        }

        // 3. Confirm the input screen (label + Continue + Back + textbox).
        UiElement? win2 = null;
        await StepAsync($"Detect input screen ('{cfg.EffectiveWin2}')",
            async () => (win2 = await WaitForTextAsync(target, cfg.EffectiveWin2, cfg, log, ct)) != null,
            log, ct).ConfigureAwait(false);

        UiElement? continueBtn = null;
        await StepAsync($"Find '{cfg.ContinueButtonText}' button",
            async () => (continueBtn = await WaitForButtonAsync(target, cfg.ContinueButtonText, cfg, log, ct)) != null,
            log, ct).ConfigureAwait(false);

        await StepAsync($"Find '{cfg.BackButtonText}' button",
            async () => await WaitForButtonAsync(target, cfg.BackButtonText, cfg, log, ct) != null,
            log, ct).ConfigureAwait(false);

        var textbox = win2 is null ? null : _driver.FindInputNear(target, win2);
        log.Report(LogEntry.Info($"Input screen ready ({cfg.EffectiveWin2})"));

        await pause.WaitWhilePausedAsync(ct).ConfigureAwait(false);

        // 4. Enter the key (paste). If OCR can't confirm it, the prompt offers
        //    Retry / Continue (proceed anyway) / Stop.
        if (textbox != null)
            await StepAsync($"Enter key '{input}'",
                () => _driver.SetTextAsync(textbox, input, ct), log, ct).ConfigureAwait(false);

        log.Report(LogEntry.Info("Submitting test string"));

        // 5. Submit.
        if (continueBtn != null)
            await StepAsync($"Click '{cfg.ContinueButtonText}'",
                () => _driver.InvokeAsync(continueBtn, ct), log, ct).ConfigureAwait(false);

        // 6. Wait for verification and read the result screen.
        log.Report(LogEntry.Info("Waiting for verification"));
        (Outcome Outcome, string? Reason) result = (Outcome.Error, "not verified");
        await StepAsync("Read verification result", async () =>
        {
            try { result = await ReadResultAsync(target, cfg, log, ct).ConfigureAwait(false); return true; }
            catch (StepException) { return false; }
        }, log, ct).ConfigureAwait(false);
        var (outcome, reason) = result;

        if (outcome == Outcome.Fail)
        {
            log.Report(LogEntry.Fail($"Detected result: {cfg.EffectiveWin3}"));
            var back = _driver.FindButton(target, cfg.BackButtonText);
            if (back != null)
                await _driver.InvokeAsync(back, ct).ConfigureAwait(false);
            // Return to the Win1 screen so the next case can start cleanly.
            await WaitForTextAsync(target, cfg.EffectiveWin1, cfg, log, ct);
        }
        else if (outcome == Outcome.Pass)
        {
            log.Report(LogEntry.Success($"Detected result: {cfg.EffectiveSuccess}"));
        }

        return (outcome, reason);
    }

    /// <summary>
    /// Runs a step; on failure asks the user (Retry / Continue / Stop). Retry re-runs
    /// the step, Continue proceeds (returns false = "skipped"), Stop aborts the case.
    /// If no decision handler is wired, failure aborts. Returns true if it succeeded.
    /// </summary>
    private async Task<bool> StepAsync(
        string what, Func<Task<bool>> attempt, IProgress<LogEntry> log, CancellationToken ct)
    {
        if (await attempt().ConfigureAwait(false))
            return true;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var decision = OnStepNeedsDecision is null
                ? StepDecision.Abort
                : await OnStepNeedsDecision($"{what} failed. Retry, Continue, or Stop?").ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            switch (decision)
            {
                case StepDecision.Retry:
                    log.Report(LogEntry.Info($"Retrying: {what}"));
                    _driver.InvalidateCache();
                    if (await attempt().ConfigureAwait(false))
                        return true;
                    break;
                case StepDecision.Continue:
                    log.Report(LogEntry.Info($"Skipping (user override): {what}"));
                    return false;
                default:
                    throw new StepException($"{what} failed");
            }
        }
    }

    /// <summary>
    /// Determines the current screen by checking the markers in priority order.
    /// Win1 is checked first because its marker ("Use a purchased activation key")
    /// is unique, whereas the Win2 marker ("Activation key") can also appear on Win1.
    /// </summary>
    private async Task<Screen> DetectScreenAsync(
        TargetWindow target, AutomationConfig cfg, IProgress<LogEntry> log, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= cfg.DetectRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _driver.InvalidateCache(); // fresh read each attempt

            if (_driver.TryFindText(target, cfg.EffectiveWin1) != null)
                return Screen.Win1;
            if (_driver.TryFindText(target, cfg.EffectiveWin3) != null)
                return Screen.Result;
            if (_driver.TryFindText(target, cfg.EffectiveWin3Review) != null)
                return Screen.Result;
            if (_driver.TryFindText(target, cfg.EffectiveWin2) != null)
                return Screen.Win2;

            await DelayBetweenAttemptsAsync("a known screen", attempt, cfg, log, ct).ConfigureAwait(false);
        }

        // Couldn't identify the screen — dump OCR for debugging.
        log.Report(LogEntry.Info($"[debug] No known screen detected after {cfg.DetectRetries} attempt(s). OCR text on screen:"));
        foreach (var line in _driver.DumpVisibleElements(target).Take(25))
            log.Report(LogEntry.Info("  " + line));
        return Screen.Unknown;
    }

    /// <summary>
    /// Races the success marker against the Win3 (failure) marker until the step
    /// timeout. Neither appearing is an error (unexpected screen / timeout).
    /// </summary>
    private async Task<(Outcome, string?)> ReadResultAsync(
        TargetWindow target, AutomationConfig cfg, IProgress<LogEntry> log, CancellationToken ct)
    {
        // Verification can be slow, so keep checking for at least VerifyTimeout
        // rather than a fixed retry count.
        var deadline = DateTime.UtcNow + cfg.VerifyTimeout;
        var attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            _driver.InvalidateCache(); // re-read the screen fresh each attempt

            if (_driver.TryFindText(target, cfg.EffectiveWin3) != null)
            {
                var hasBack = _driver.FindButton(target, cfg.BackButtonText) != null;
                return (Outcome.Fail, hasBack ? $"matched '{cfg.EffectiveWin3}'" : $"matched '{cfg.EffectiveWin3}' (no Back button)");
            }

            // Win3-Success screen: find and click Activate — that IS the success.
            if (_driver.TryFindText(target, cfg.EffectiveWin3Review) != null)
            {
                var activateBtn = _driver.FindButton(target, cfg.EffectiveActivateButton);
                if (activateBtn != null)
                {
                    log.Report(LogEntry.Info($"Success screen ('{cfg.EffectiveWin3Review}') — clicking '{cfg.EffectiveActivateButton}'…"));
                    await _driver.InvokeAsync(activateBtn, ct).ConfigureAwait(false);
                    return (Outcome.Pass, $"activated via '{cfg.EffectiveActivateButton}'");
                }
                log.Report(LogEntry.Info($"Success screen found but '{cfg.EffectiveActivateButton}' button not visible yet — waiting…"));
            }

            var remaining = (deadline - DateTime.UtcNow).TotalSeconds;
            if (remaining <= 0)
                break;
            log.Report(LogEntry.Info($"Verifying result… attempt {attempt}, {remaining:0}s left"));
            await Task.Delay(cfg.DetectRetryDelayMs, ct).ConfigureAwait(false);
        }

        throw new StepException($"Result screen not detected within {cfg.VerifySeconds}s (unexpected screen)");
    }

    private Task<UiElement?> WaitForTextAsync(
        TargetWindow target, string text, AutomationConfig cfg, IProgress<LogEntry> log, CancellationToken ct) =>
        RetryFindAsync($"screen '{text}'", () => _driver.TryFindText(target, text), target, cfg, log, ct);

    private Task<UiElement?> WaitForButtonAsync(
        TargetWindow target, string text, AutomationConfig cfg, IProgress<LogEntry> log, CancellationToken ct) =>
        RetryFindAsync($"button '{text}'", () => _driver.FindButton(target, text), target, cfg, log, ct);

    /// <summary>
    /// Tries <paramref name="find"/> up to <see cref="AutomationConfig.DetectRetries"/>
    /// times, waiting <see cref="AutomationConfig.DetectRetryDelayMs"/> between attempts
    /// to let the window refresh. On each attempt the cache is invalidated so a fresh
    /// OCR scan runs. On final failure the raw OCR text is logged for debugging.
    /// </summary>
    private async Task<UiElement?> RetryFindAsync(
        string what, Func<UiElement?> find, TargetWindow target, AutomationConfig cfg, IProgress<LogEntry> log, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= cfg.DetectRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _driver.InvalidateCache(); // re-read the screen fresh each attempt
            var found = find();
            if (found != null)
            {
                if (attempt > 1)
                    log.Report(LogEntry.Info($"Found {what} on attempt {attempt}/{cfg.DetectRetries}."));
                return found;
            }
            if (attempt == cfg.DetectRetries)
            {
                // Final miss — dump what OCR actually sees so the user can compare.
                log.Report(LogEntry.Info($"[debug] {what} not found after {cfg.DetectRetries} attempt(s). OCR text on screen:"));
                foreach (var line in _driver.DumpVisibleElements(target).Take(25))
                    log.Report(LogEntry.Info("  " + line));
            }
            await DelayBetweenAttemptsAsync(what, attempt, cfg, log, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Waits until <paramref name="text"/> is no longer on screen — i.e. a navigation
    /// click actually changed the screen. Best-effort: returns when gone or after retries.
    /// </summary>
    private async Task WaitForTextGoneAsync(
        TargetWindow target, string text, AutomationConfig cfg, IProgress<LogEntry> log, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= cfg.DetectRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _driver.InvalidateCache(); // re-read the screen fresh each attempt
            if (_driver.TryFindText(target, text) is null)
                return;
            await DelayBetweenAttemptsAsync($"old screen '{text}' to clear", attempt, cfg, log, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Logs the retry and waits the refresh delay, unless this was the last attempt.</summary>
    private static async Task DelayBetweenAttemptsAsync(
        string what, int attempt, AutomationConfig cfg, IProgress<LogEntry> log, CancellationToken ct)
    {
        if (attempt >= cfg.DetectRetries)
            return;
        log.Report(LogEntry.Info($"Waiting for {what} — attempt {attempt}/{cfg.DetectRetries}, refresh {cfg.DetectRetryDelayMs}ms…"));
        await Task.Delay(cfg.DetectRetryDelayMs, ct).ConfigureAwait(false);
    }
}

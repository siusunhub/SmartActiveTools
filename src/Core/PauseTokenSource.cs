namespace InputAutomationTool.Core;

/// <summary>
/// Cooperative pause gate. The engine awaits <see cref="WaitWhilePausedAsync"/>
/// between atomic steps, so a pause never interrupts a click or set-text mid-action.
/// </summary>
public sealed class PauseTokenSource
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _tcs;

    public bool IsPaused
    {
        get { lock (_gate) { return _tcs != null; } }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _tcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? t;
        lock (_gate)
        {
            t = _tcs;
            _tcs = null;
        }
        t?.TrySetResult(true);
    }

    public async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        Task? wait;
        lock (_gate) { wait = _tcs?.Task; }
        if (wait != null)
            await wait.WaitAsync(ct).ConfigureAwait(false);
    }
}

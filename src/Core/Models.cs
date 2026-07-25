namespace InputAutomationTool.Core;

public enum LogLevel { Info, Success, Fail, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public static LogEntry Info(string m) => new(DateTime.Now, LogLevel.Info, m);
    public static LogEntry Success(string m) => new(DateTime.Now, LogLevel.Success, m);
    public static LogEntry Fail(string m) => new(DateTime.Now, LogLevel.Fail, m);
    public static LogEntry Error(string m) => new(DateTime.Now, LogLevel.Error, m);

    public override string ToString() => $"{Time:HH:mm:ss}  {Message}";
}

public sealed record ProgressInfo(int Current, int Total);

public enum Outcome { Pass, Fail, Error }

public sealed record TestResult(int Index, string Input, Outcome Outcome, string? Reason, DateTime Timestamp);

/// <summary>A top-level window the user can target.</summary>
public sealed class TargetWindow
{
    public required nint Handle { get; init; }
    public required string Title { get; init; }
    public required int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";

    public string Display => string.IsNullOrEmpty(ProcessName)
        ? Title
        : $"{Title}  —  {ProcessName} (pid {ProcessId})";

    public override string ToString() => Display;
}

/// <summary>
/// UI-framework-agnostic wrapper around a detected element. The engine only reads
/// <see cref="Name"/> / <see cref="ControlType"/>; <see cref="Native"/> is the
/// driver-specific handle (e.g. a UIA AutomationElement).
/// </summary>
public sealed class UiElement
{
    public string Name { get; init; } = "";
    public string ControlType { get; init; } = "";
    internal object? Native { get; init; }
}

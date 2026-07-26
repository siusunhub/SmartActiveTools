using System.IO;
using System.Text.Json;
using InputAutomationTool.Core;

namespace InputAutomationTool.App;

/// <summary>Persists the last-used <see cref="AutomationConfig"/> to app directory or %AppData%.</summary>
public static class SettingsStore
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "InputAutomationTool");

    private static readonly string AppDataFilePath = Path.Combine(AppDataDir, "settings.json");

    /// <summary>Where the OCR debug capture is written by the dump button.</summary>
    public static string CapturePath
    {
        get
        {
            Directory.CreateDirectory(AppDataDir);
            return Path.Combine(AppDataDir, "ocr-capture.png");
        }
    }
    private static readonly string LocalFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static AutomationConfig Load() => Load(out _);

    public static AutomationConfig Load(out List<LogEntry> logs)
    {
        logs = new List<LogEntry>();

        string[] candidatePaths = [
            LocalFilePath,
            Path.Combine(Directory.GetCurrentDirectory(), "settings.json"),
            AppDataFilePath
        ];

        foreach (var path in candidatePaths.Distinct())
        {
            logs.Add(LogEntry.Info($"Checking settings file: {path}"));
            try
            {
                if (File.Exists(path))
                {
                    logs.Add(LogEntry.Info($"Settings file exists at: {path}"));
                    var json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        logs.Add(LogEntry.Error($"Settings file at {path} is empty."));
                        continue;
                    }

                    var cfg = JsonSerializer.Deserialize<AutomationConfig>(json, Options);
                    if (cfg != null)
                    {
                        cfg.EnsureDefaults();
                        logs.Add(LogEntry.Success($"Successfully loaded settings from: {path}"));
                        return cfg;
                    }
                    else
                    {
                        logs.Add(LogEntry.Error($"Failed to deserialize settings at: {path}"));
                    }
                }
                else
                {
                    logs.Add(LogEntry.Info($"Settings file does not exist at: {path}"));
                }
            }
            catch (Exception ex)
            {
                logs.Add(LogEntry.Error($"Error reading settings file at {path}: {ex.Message}"));
            }
        }

        logs.Add(LogEntry.Info("No settings file loaded. Using default settings."));
        return new AutomationConfig();
    }

    public static void Save(AutomationConfig config)
    {
        try
        {
            // If settings.json exists in the app directory, save back to it. Otherwise save to AppData.
            string targetPath = File.Exists(LocalFilePath) ? LocalFilePath : AppDataFilePath;
            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(targetPath, JsonSerializer.Serialize(config.StripDefaults(), Options));
        }
        catch
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(AppDataFilePath, JsonSerializer.Serialize(config.StripDefaults(), Options));
            }
            catch { /* non-fatal */ }
        }
    }
}

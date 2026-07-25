using System.IO;
using System.Text.Json;
using InputAutomationTool.Core;

namespace InputAutomationTool.App;

/// <summary>Persists the last-used <see cref="AutomationConfig"/> to %AppData%.</summary>
public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "InputAutomationTool");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AutomationConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<AutomationConfig>(json);
                if (cfg != null)
                    return cfg;
            }
        }
        catch { /* ignore corrupt settings; fall back to defaults */ }
        return new AutomationConfig();
    }

    public static void Save(AutomationConfig config)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(config.StripDefaults(), Options));
        }
        catch { /* non-fatal */ }
    }
}

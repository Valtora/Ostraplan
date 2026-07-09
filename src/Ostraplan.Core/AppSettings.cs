using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ostraplan.Core;

/// <summary>Ostraplan's own settings (%APPDATA%\Ostraplan\settings.json) - never the game's.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("gameRootOverride")] public string? GameRootOverride { get; set; }
    [JsonPropertyName("theme")] public string Theme { get; set; } = "system";   // "system" | "light" | "dark"
    [JsonPropertyName("recentFiles")] public List<string> RecentFiles { get; set; } = [];
    [JsonPropertyName("exportAuthor")] public string? ExportAuthor { get; set; }
    [JsonPropertyName("lastExportDir")] public string? LastExportDir { get; set; }
    [JsonPropertyName("installPromptDismissed")] public bool InstallPromptDismissed { get; set; }
    [JsonPropertyName("ostrasortPath")] public string? OstrasortPath { get; set; }
    /// <summary>Let modded parts be placed where Ostraplan's core-game placement law says they don't fit (they are
    /// still flagged as warnings). Core parts stay hard-blocked. Off by default — the Law is authoritative for core.</summary>
    [JsonPropertyName("allowModdedOverrides")] public bool AllowModdedOverrides { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostraplan");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt settings are replaced on next save */ }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Touch(string file)
    {
        RecentFiles.Remove(file);
        RecentFiles.Insert(0, file);
        if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
    }
}

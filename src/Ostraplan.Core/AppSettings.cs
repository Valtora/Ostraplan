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
    /// <summary>Light Viz "unlit dimming" level (0..1): how far unlit areas darken while the lighting overlay is on.
    /// 0 = additive glow over the full-bright ship (default), 1 = unlit areas to black (the in-game look). The
    /// overlay on/off state is session-only like the other viz toggles; only this preference persists.</summary>
    [JsonPropertyName("lightDarkness")] public double LightDarkness { get; set; }
    /// <summary>Light Viz light brightness ("reveal gain"): how strongly a light lifts its area toward fully lit
    /// (scales the light's own intensity). Higher = brighter, harder pools; lower = softer. Default 1.5.</summary>
    [JsonPropertyName("lightReveal")] public double LightReveal { get; set; } = 1.5;
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

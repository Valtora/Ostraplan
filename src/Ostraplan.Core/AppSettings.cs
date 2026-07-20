using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ostraplan.Core;

/// <summary>A palette part singled out for Favorites / Recent: its def name plus whether it is a loose ITEMS-tab
/// entry. The same def name can exist as both a buildable part and a loose item, so the <see cref="Loose"/> flag
/// is what disambiguates the two universes when the reference is resolved back to a palette row.</summary>
public sealed class PartRef
{
    [JsonPropertyName("def")] public string Def { get; set; } = "";
    [JsonPropertyName("loose")] public bool Loose { get; set; }

    public PartRef() { }
    public PartRef(string def, bool loose) { Def = def; Loose = loose; }

    public bool Same(string def, bool loose) => Loose == loose && string.Equals(Def, def, StringComparison.Ordinal);
}

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
    /// <summary>Light Viz exterior daylight: the parallax location whose sun lights shine on the design (a name
    /// from <c>data/parallax</c>), or empty/null for no sun. The overlay renders game-exact, so there are no
    /// brightness/dimming tuners any more — only the sun location + angle persist.</summary>
    [JsonPropertyName("lightSunParallax")] public string? LightSunParallax { get; set; }
    /// <summary>Light Viz sun-constellation rotation in degrees (the game's world rotation of its far sun
    /// transform). Meaningful only when <see cref="LightSunParallax"/> is set.</summary>
    [JsonPropertyName("lightSunAngle")] public double LightSunAngle { get; set; }
    /// <summary>Parts the user pinned for quick access (the palette's ★ tab's Favorites group), in pin order.</summary>
    [JsonPropertyName("favorites")] public List<PartRef> Favorites { get; set; } = [];
    /// <summary>The most-recently-placed parts, newest first, capped at <see cref="RecentCap"/> (the ★ tab's Recent group).</summary>
    [JsonPropertyName("recentParts")] public List<PartRef> RecentParts { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>How many parts the Recent list keeps (the issue asked for "the last 5 or so").</summary>
    public const int RecentCap = 8;

    public bool IsFavorite(string def, bool loose) => Favorites.Any(f => f.Same(def, loose));

    /// <summary>Toggle a part's favorite state. Returns the new state (true = now a favorite). Caller persists.</summary>
    public bool ToggleFavorite(string def, bool loose)
    {
        var existing = Favorites.FirstOrDefault(f => f.Same(def, loose));
        if (existing is not null) { Favorites.Remove(existing); return false; }
        Favorites.Add(new PartRef(def, loose));
        return true;
    }

    /// <summary>Record a part as just-used: move (or insert) it at the front of Recent, drop any duplicate, and cap
    /// the length. Returns true when the list actually changed — a repeat of the current front is a no-op, so a
    /// multi-tile paint stroke of the same part doesn't churn the list or the settings file. Caller persists.</summary>
    public bool PushRecent(string def, bool loose)
    {
        if (RecentParts.Count > 0 && RecentParts[0].Same(def, loose)) return false;
        RecentParts.RemoveAll(r => r.Same(def, loose));
        RecentParts.Insert(0, new PartRef(def, loose));
        if (RecentParts.Count > RecentCap) RecentParts.RemoveRange(RecentCap, RecentParts.Count - RecentCap);
        return true;
    }

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

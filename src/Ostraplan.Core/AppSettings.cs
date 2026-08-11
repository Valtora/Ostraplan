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

/// <summary>
/// The export wizard's last-used settings, so a repeat export is one click.
///
/// <para>These live here rather than in the <c>.oplan</c> on purpose: a design shared with someone else must not
/// carry local folder paths, save-game names or credit amounts. What belongs to the <i>design</i> — its name and
/// in-game identity — stays in <see cref="OplanMeta"/> and travels with it.</para>
/// </summary>
public sealed class LastExport
{
    /// <summary>"mod", "newShip" or "update". A name rather than an enum so an unknown value from a newer build
    /// degrades to the default instead of throwing.</summary>
    [JsonPropertyName("destination")] public string? Destination { get; set; }

    [JsonPropertyName("wearOn")] public bool WearOn { get; set; } = true;
    [JsonPropertyName("wearTarget")] public double WearTarget { get; set; } = 0.87625;

    [JsonPropertyName("modVersion")] public string ModVersion { get; set; } = "1.0.0";
    [JsonPropertyName("brokerPools")] public List<string> BrokerPools { get; set; } = [];
    [JsonPropertyName("brokerWeight")] public double BrokerWeight { get; set; }
    [JsonPropertyName("specialOfferPools")] public List<string> SpecialOfferPools { get; set; } = [];
    [JsonPropertyName("derelictPools")] public List<string> DerelictPools { get; set; } = [];
    [JsonPropertyName("derelictWeight")] public double DerelictWeight { get; set; }
    [JsonPropertyName("noDeliveryRoute")] public bool NoDeliveryRoute { get; set; }
    [JsonPropertyName("startingShip")] public bool StartingShip { get; set; }
    [JsonPropertyName("startingShipExclusive")] public bool StartingShipExclusive { get; set; }
    [JsonPropertyName("startStation")] public string StartStation { get; set; } = "OKLG";
    [JsonPropertyName("stagedIntoMods")] public bool StagedIntoMods { get; set; } = true;
    [JsonPropertyName("registerWithOstrasort")] public bool RegisterWithOstrasort { get; set; }

    [JsonPropertyName("saveName")] public string? SaveName { get; set; }
    [JsonPropertyName("charge")] public bool Charge { get; set; }
    [JsonPropertyName("price")] public double Price { get; set; }

    [JsonPropertyName("inPlace")] public bool InPlace { get; set; }
    [JsonPropertyName("backup")] public bool Backup { get; set; } = true;
    [JsonPropertyName("deduct")] public bool Deduct { get; set; }
    /// <summary>The edit-cost multipliers, one per side of the edit (see <see cref="EditCost"/>). New keys as of
    /// the split in 0.62: a settings file written before it has neither, so both sliders start at their defaults
    /// rather than inheriting the single <c>costMultiplier</c> that used to cover both.</summary>
    [JsonPropertyName("newCostMultiplier")]
    public double NewCostMultiplier { get; set; } = EditCost.DefaultNewMultiplier;

    [JsonPropertyName("movedCostMultiplier")]
    public double MovedCostMultiplier { get; set; } = EditCost.DefaultMovedMultiplier;

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Ostraplan's own settings (%APPDATA%\Ostraplan\settings.json) - never the game's.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("gameRootOverride")] public string? GameRootOverride { get; set; }
    /// <summary>An explicit Ostranauts Saves folder, for an install that keeps its saves somewhere other than
    /// LocalLow (the game's own <c>strSaveLocation</c> setting). Null = resolve it automatically, which is what
    /// nearly every install wants — see <see cref="GameEnv.SavesDir"/> for the order.</summary>
    [JsonPropertyName("savesDirOverride")] public string? SavesDirOverride { get; set; }
    [JsonPropertyName("theme")] public string Theme { get; set; } = "system";   // "system" | "light" | "dark"
    /// <summary>Ostraplan's own UI scale, 1.0 = 100%. Everything the app draws scales by this: chrome, dialogs,
    /// reports and the canvas. For a high-DPI monitor run at a low Windows scaling factor, where the app's text
    /// would otherwise be tiny. Clamped to <see cref="UiScaling.Min"/>..<see cref="UiScaling.Max"/> at use, so a
    /// hand-edited settings file can't produce an unusable window.</summary>
    [JsonPropertyName("uiScale")] public double UiScale { get; set; } = UiScaling.Default;
    [JsonPropertyName("recentFiles")] public List<string> RecentFiles { get; set; } = [];
    [JsonPropertyName("exportAuthor")] public string? ExportAuthor { get; set; }
    [JsonPropertyName("lastExportDir")] public string? LastExportDir { get; set; }
    /// <summary>The export wizard's remembered settings. Null until the first export; an older build ignores it.
    /// <see cref="ExportAuthor"/> and <see cref="LastExportDir"/> keep their own keys, so no settings file written
    /// before the wizard needs migrating.</summary>
    [JsonPropertyName("lastExport")] public LastExport? LastExport { get; set; }
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
    /// <summary>WalkViz: count tiles that are not part of the ship as walkable, joining zones that only connect by
    /// an EVA route over the hull. The game does count them (<c>Tile.IsWalkable</c> needs no floor), but left on,
    /// almost every design reads as one zone — so this is off by default and the overlay shows interior routes.</summary>
    [JsonPropertyName("walkIncludeExterior")] public bool WalkIncludeExterior { get; set; }
    /// <summary>WalkViz: treat painted Forbid zones as impassable. The game's test is per crew member (a zone
    /// matches a PersonSpec), so this is the "for a crew member the zone binds" reading. On by default.</summary>
    [JsonPropertyName("walkRespectForbidZones")] public bool WalkRespectForbidZones { get; set; } = true;
    /// <summary>Take a rotating snapshot of the open design every <see cref="AutoSaveMinutes"/> minutes (see
    /// <see cref="AutoSaveStore"/>). Opt-in: off until the user turns it on, since it writes to disk on a timer.
    /// A snapshot never touches the user's own .oplan — Ctrl+S stays the only thing that writes it.</summary>
    [JsonPropertyName("autoSave")] public bool AutoSave { get; set; }
    /// <summary>Minutes between auto-save snapshots. Clamped to
    /// <see cref="AutoSaveStore.MinIntervalMinutes"/>..<see cref="AutoSaveStore.MaxIntervalMinutes"/> at use, so a
    /// hand-edited settings file can't produce a zero-interval timer.</summary>
    [JsonPropertyName("autoSaveMinutes")] public int AutoSaveMinutes { get; set; } = AutoSaveStore.DefaultIntervalMinutes;
    /// <summary>How many snapshots each design keeps; older ones rotate out. Clamped like
    /// <see cref="AutoSaveMinutes"/>.</summary>
    [JsonPropertyName("autoSaveKeep")] public int AutoSaveKeep { get; set; } = AutoSaveStore.DefaultKeep;
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

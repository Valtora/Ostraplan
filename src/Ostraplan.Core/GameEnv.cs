using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Ostraplan.Core;

/// <summary>
/// Locates the game install and the folders Ostraplan reads (adapted from
/// Ostrasort's GameEnv). Everything here is read-only toward the install.
/// </summary>
public sealed class GameEnv
{
    public const string DefaultGameRoot = @"C:\Program Files (x86)\Steam\steamapps\common\Ostranauts";

    /// <summary>The game version the ported constants/tables were last verified against. Moved to 1.0.0.7
    /// (Steam build 24535205) by a full re-verification pass over every ported system, the compiled render
    /// shaders included; see docs/GAME-INTERNALS.md.</summary>
    public const string VerifiedGameVersion = "1.0.0.7";

    public required string GameRoot { get; init; }
    public required string DiscoveredVia { get; init; }
    public required string StreamingAssetsDir { get; init; }   // holds data\ and images\
    public required string ModsDir { get; init; }              // holds loading_order.json + local mods
    public string? WorkshopContentDir { get; init; }           // steamapps\workshop\content\1022980
    public string? InstalledVersion { get; init; }             // e.g. "1.0.0.7"

    public string CoreDataDir => Path.Combine(StreamingAssetsDir, "data");
    public string CoreImagesDir => Path.Combine(StreamingAssetsDir, "images");
    public string LoadingOrderPath => Path.Combine(ModsDir, "loading_order.json");

    /// <summary>An explicit Saves folder from Ostraplan's own settings, or null to resolve one. Wins over
    /// everything else, because it is the only source the user set deliberately.</summary>
    public string? SavesDirOverride { get; init; }

    /// <summary>The Saves folder the game's own <c>settings.json</c> points at through <c>strSaveLocation</c>,
    /// already resolved to a real directory, or null when the key is absent or names somewhere that isn't there.
    /// This is what an install with saves outside LocalLow is actually using.</summary>
    public string? GameSavesSetting { get; init; }

    /// <summary>Where Ostranauts keeps saves out of the box:
    /// <c>%USERPROFILE%\AppData\LocalLow\Blue Bottle Games\Ostranauts\Saves</c>. Shown in Settings as the
    /// fallback, so a user can see what "automatic" resolved to.</summary>
    public static string DefaultSavesDir => Path.Combine(
        Environment.GetEnvironmentVariable("USERPROFILE") ?? "",
        @"AppData\LocalLow\Blue Bottle Games\Ostranauts\Saves");

    /// <summary>
    /// The Saves folder to read, or null when none of the candidates exist. Read-only, always.
    ///
    /// <para>In order: the user's own override, then the game's <c>strSaveLocation</c>, then the LocalLow default.
    /// The middle one matters because the game lets a player relocate its save folder, and until 0.69 Ostraplan
    /// hard-coded LocalLow and simply reported "no save games found" for anyone who had.</para>
    /// </summary>
    public string? SavesDir => ResolveSaves(SavesDirOverride) ?? GameSavesSetting ?? ResolveSaves(DefaultSavesDir);

    /// <summary>
    /// Turn a candidate path into the real Saves folder, or null if it isn't one. Accepts <b>either</b> the Saves
    /// folder itself or the folder holding it, since <c>strSaveLocation</c> names the parent
    /// (<c>…\Blue Bottle Games\Ostranauts</c>) while a user picking a folder by hand will usually pick the
    /// <c>Saves</c> folder they can see.
    /// </summary>
    public static string? ResolveSaves(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Replace('/', '\\'));
            var nested = Path.Combine(full, "Saves");
            if (Directory.Exists(nested)) return nested;
            return Directory.Exists(full) ? full : null;
        }
        catch { return null; }   // a malformed path is simply not a saves folder
    }

    public static GameEnv Locate(string? gameRootOverride, string? savesDirOverride = null)
    {
        string root, via;
        if (gameRootOverride is not null)
        {
            root = Path.GetFullPath(gameRootOverride);
            via = "user setting";
            if (!Directory.Exists(Path.Combine(root, "Ostranauts_Data")))
                throw new DirectoryNotFoundException(
                    $"'{root}' does not look like an Ostranauts install (no Ostranauts_Data folder inside it).");
        }
        else if (LocateViaSteam() is { } steamHit)
        {
            (root, via) = steamHit;
        }
        else if (Directory.Exists(Path.Combine(DefaultGameRoot, "Ostranauts_Data")))
        {
            root = DefaultGameRoot;
            via = "default install path";
        }
        else
        {
            throw new DirectoryNotFoundException(
                "Could not find the Ostranauts install (checked the Steam registry, every Steam " +
                "library, and the default path). Pick the game folder manually in Settings.");
        }

        var dataDir = Path.Combine(root, "Ostranauts_Data");
        var modsDir = Path.Combine(dataDir, "Mods");

        // The game's own settings.json can relocate both folders Ostraplan reads: the Mods folder via
        // strPathMods, and the save folder via strSaveLocation (which names the parent of Saves).
        string? gameSaves = null;
        var settings = Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE") ?? "",
            @"AppData\LocalLow\Blue Bottle Games\Ostranauts\settings.json");
        if (File.Exists(settings))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(settings));
                // The file is an array of one settings object in every build seen so far, but read a bare object
                // too rather than depending on the shape.
                var user = node as JsonObject ?? (node as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
                var custom = user?["strPathMods"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom))
                    modsDir = custom;
                gameSaves = ResolveSaves(user?["strSaveLocation"]?.GetValue<string>());
            }
            catch { /* unreadable settings.json is not Ostraplan's problem */ }
        }

        string? workshop = null;
        var steamapps = Path.GetDirectoryName(Path.GetDirectoryName(root));
        if (steamapps is not null)
        {
            var candidate = Path.Combine(steamapps, "workshop", "content", "1022980");
            if (Directory.Exists(candidate)) workshop = candidate;
        }

        return new GameEnv
        {
            GameRoot = root,
            DiscoveredVia = via,
            StreamingAssetsDir = Path.Combine(dataDir, "StreamingAssets"),
            ModsDir = modsDir,
            WorkshopContentDir = workshop,
            InstalledVersion = ReadInstalledVersion(dataDir),
            SavesDirOverride = savesDirOverride,
            GameSavesSetting = gameSaves,
        };
    }

    private static (string Root, string Via)? LocateViaSteam()
    {
        string? steam = null;
        try
        {
            steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                 ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        }
        catch { /* no registry access -> fall through to the default path */ }
        if (string.IsNullOrWhiteSpace(steam)) return null;
        steam = Path.GetFullPath(steam.Replace('/', '\\'));

        var libraries = new List<string> { steam };
        foreach (var vdf in new[]
                 {
                     Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steam, "config", "libraryfolders.vdf"),
                 })
        {
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                try { libraries.Add(Regex.Unescape(m.Groups[1].Value)); }
                catch (ArgumentException) { /* malformed escape in vdf - skip that entry */ }
            }
            break;
        }

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(lib, "steamapps", "common", "Ostranauts");
            if (Directory.Exists(Path.Combine(candidate, "Ostranauts_Data")))
                return (candidate, $"Steam library at {lib}");
        }
        return null;
    }

    /// <summary>
    /// Application.version sits as a plain ASCII string inside globalgamemanagers
    /// (the same string the main menu shows). It tracks the install, not the last run.
    /// </summary>
    private static string? ReadInstalledVersion(string dataDir)
    {
        var ggm = Path.Combine(dataDir, "globalgamemanagers");
        if (!File.Exists(ggm)) return null;
        var text = Encoding.ASCII.GetString(File.ReadAllBytes(ggm));
        var m = Regex.Match(text, @"\d+\.\d+\.\d+\.\d+");
        return m.Success ? m.Value : null;
    }
}

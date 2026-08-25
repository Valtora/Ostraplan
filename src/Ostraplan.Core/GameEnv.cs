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

    /// <summary>The game's executable, which is what tells a real install apart from a folder shaped like one.</summary>
    public const string GameExeName = "Ostranauts.exe";

    /// <summary>The game version the ported constants/tables were last verified against. Moved to 1.0.0.11
    /// (Steam build 24744728) by the sweep in docs/GAME-INTERNALS.md §1: the named methods re-read against a
    /// fresh decompile with no logic drift, the lighting shaders re-extracted and disassembled, and the parity
    /// corpus green against the live install's data.</summary>
    public const string VerifiedGameVersion = "1.0.0.11";

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

    /// <summary>
    /// Why <paramref name="root"/> is not an Ostranauts install Ostraplan can run on, or null when it is one.
    /// Phrased as a complete sentence naming the folder, because every caller puts it straight in front of the user.
    ///
    /// <para>Deliberately stricter than "there is an Ostranauts_Data folder inside it", which is all this used to
    /// ask. A <b>mod deploy target</b> is exactly that folder and nothing else, and it passed: the catalogue then
    /// built from an empty core, the planner opened with no parts in the palette and said nothing, and the
    /// game-gated tests reported as failures rather than the honest skip they promise. The exe is what separates
    /// a game from a folder shaped like one; the two StreamingAssets folders are the data and sprites Ostraplan
    /// actually reads, so a half-downloaded install is caught as well as an absent one.</para>
    /// </summary>
    public static string? InstallProblem(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return "No game folder has been chosen.";

        string full;
        try { full = Path.GetFullPath(root.Replace('/', '\\')); }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return $"'{root}' is not a usable folder path.";
        }

        if (!Directory.Exists(full))
            return $"'{full}' is not a folder that exists.";

        if (!File.Exists(Path.Combine(full, GameExeName)))
            return $"'{full}' holds no {GameExeName}, so it is not an Ostranauts install.";

        var streaming = Path.Combine(full, "Ostranauts_Data", "StreamingAssets");
        foreach (var need in new[] { "data", "images" })
            if (!Directory.Exists(Path.Combine(streaming, need)))
                return $"'{full}' has no Ostranauts_Data\\StreamingAssets\\{need} folder, " +
                        "so the game's own data is not there to read.";

        return null;
    }

    public static GameEnv Locate(string? gameRootOverride, string? savesDirOverride = null)
    {
        string root, via;
        if (gameRootOverride is not null)
        {
            // Validate before GetFullPath: an override of "" reaches here from a settings file written by hand,
            // and GetFullPath throws ArgumentException for it, which no caller catches.
            if (InstallProblem(gameRootOverride) is { } why) throw new DirectoryNotFoundException(why);
            root = Path.GetFullPath(gameRootOverride.Replace('/', '\\'));
            via = "user setting";
        }
        else if (LocateViaSteam() is { } steamHit)
        {
            (root, via) = steamHit;
        }
        else if (InstallProblem(DefaultGameRoot) is null)
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
            if (InstallProblem(candidate) is null)
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

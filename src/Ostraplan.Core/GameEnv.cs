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

    /// <summary>The game version the ported constants/tables were last verified against.</summary>
    public const string VerifiedGameVersion = "0.15.1.6";

    public required string GameRoot { get; init; }
    public required string DiscoveredVia { get; init; }
    public required string StreamingAssetsDir { get; init; }   // holds data\ and images\
    public required string ModsDir { get; init; }              // holds loading_order.json + local mods
    public string? WorkshopContentDir { get; init; }           // steamapps\workshop\content\1022980
    public string? InstalledVersion { get; init; }             // e.g. "0.15.1.6"

    public string CoreDataDir => Path.Combine(StreamingAssetsDir, "data");
    public string CoreImagesDir => Path.Combine(StreamingAssetsDir, "images");
    public string LoadingOrderPath => Path.Combine(ModsDir, "loading_order.json");

    /// <summary>The persistent Saves folder (LocalLow), or null if it doesn't exist. Read-only.</summary>
    public string? SavesDir
    {
        get
        {
            var p = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "",
                @"AppData\LocalLow\Blue Bottle Games\Ostranauts\Saves");
            return Directory.Exists(p) ? p : null;
        }
    }

    public bool VersionMatchesVerified =>
        InstalledVersion is null || InstalledVersion == VerifiedGameVersion;

    public static GameEnv Locate(string? gameRootOverride)
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

        // settings.json can relocate the Mods folder via strPathMods
        var settings = Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE") ?? "",
            @"AppData\LocalLow\Blue Bottle Games\Ostranauts\settings.json");
        if (File.Exists(settings))
        {
            try
            {
                var custom = JsonNode.Parse(File.ReadAllText(settings))?["strPathMods"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom))
                    modsDir = custom;
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

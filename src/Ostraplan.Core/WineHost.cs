using System.IO;

namespace Ostraplan.Core;

/// <summary>
/// What locating the game needs to know when Ostraplan runs under Wine on Linux. Two things differ there: the
/// Windows registry has no Steam in it, because Steam is a Linux program, and the game runs in Proton's prefix
/// rather than Ostraplan's own, so its saves and <c>settings.json</c> are in a different <c>C:</c> drive.
///
/// <para>Inert on Windows. Everything keys off <c>WINEHOMEDIR</c>, which Wine sets for every process it starts
/// and Windows never does. Wine strips <c>HOME</c> from the Windows environment, so it is the only route to the
/// Linux home folder that does not guess the account name.</para>
/// </summary>
public static class WineHost
{
    /// <summary>Ostranauts' Steam app id, which names its Proton prefix under <c>steamapps\compatdata</c>.</summary>
    public const string AppId = "1022980";

    /// <summary>Where Steam lives relative to the Linux home folder, most likely first. The symlink is the one
    /// Steam maintains to point at whichever install is active, so it wins when there are several.</summary>
    public static readonly string[] SteamRoots =
    [
        @".steam\steam",
        @".local\share\Steam",
        @".var\app\com.valvesoftware.Steam\.local\share\Steam",   // Flatpak
        @"snap\steam\common\.local\share\Steam",                  // Snap
    ];

    /// <summary>The Linux home folder as a Windows path (<c>Z:\home\name</c>), or null when not under Wine.</summary>
    public static string? Home => HomeFrom(Environment.GetEnvironmentVariable("WINEHOMEDIR"));

    /// <summary>
    /// Turn <c>WINEHOMEDIR</c> into a path .NET can open. Wine sets it as an NT path, <c>\??\Z:\home\name</c>,
    /// or <c>\??\unix\home\name</c> in a prefix where no drive letter maps the root.
    /// </summary>
    public static string? HomeFrom(string? wineHomeDir)
    {
        if (string.IsNullOrWhiteSpace(wineHomeDir)) return null;
        var path = wineHomeDir.Trim();
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        if (path.StartsWith(@"unix\", StringComparison.OrdinalIgnoreCase)) return @"\\?\" + path.TrimEnd('\\');
        return path.Length >= 3 && path[1] == ':' && path[2] == '\\' ? path.TrimEnd('\\') : null;
    }

    /// <summary>
    /// A Linux path (as <c>libraryfolders.vdf</c> writes one) as Wine shows it to a Windows program, or null when
    /// it is not an absolute Linux path. Uses the drive <paramref name="home"/> is on, on the reading that the
    /// drive holding the home folder is the one mapping <c>/</c>. That is Wine's default (<c>Z:</c>) and
    /// Proton's, and a prefix without it reaches here as the <c>\\?\unix</c> form, which maps <c>/</c> by definition.
    /// </summary>
    public static string? ToWindows(string linuxPath, string home)
    {
        if (!linuxPath.StartsWith('/')) return null;
        var root = home.StartsWith(@"\\?\unix", StringComparison.OrdinalIgnoreCase) ? @"\\?\unix"
                 : home.Length >= 2 && home[1] == ':' ? home[..2]
                 : null;
        return root is null ? null : root + linuxPath.TrimEnd('/').Replace('/', '\\');
    }

    /// <summary>
    /// The <c>C:</c> drive of the Proton prefix the game at <paramref name="gameRoot"/> runs in, or null when it
    /// has none. Steam keeps a game's prefix in the same library as the game, so it sits beside <c>common</c>.
    /// </summary>
    public static string? ProtonDriveC(string gameRoot)
    {
        var steamapps = Path.GetDirectoryName(Path.GetDirectoryName(gameRoot.TrimEnd('\\', '/')));
        if (steamapps is null) return null;
        var driveC = Path.Combine(steamapps, "compatdata", AppId, "pfx", "drive_c");
        return Directory.Exists(Path.Combine(driveC, "users", "steamuser")) ? driveC : null;
    }

    /// <summary>The profile the game sees inside its Proton prefix. Proton runs every game as <c>steamuser</c>.</summary>
    public static string ProtonProfile(string driveC) => Path.Combine(driveC, "users", "steamuser");

    /// <summary>
    /// A path the game wrote into its own <c>settings.json</c>, as Ostraplan has to open it. The game's <c>C:</c>
    /// is the Proton prefix's, not Ostraplan's, so it is re-rooted at <paramref name="protonDriveC"/>. Any other
    /// drive (<c>Z:</c> above all) maps the same in both prefixes and is left alone.
    /// </summary>
    public static string MapProtonPath(string path, string protonDriveC)
    {
        if (path.Length >= 3 && (path[0] is 'C' or 'c') && path[1] == ':' && (path[2] is '/' or '\\'))
            return Path.Combine(protonDriveC, path[3..].Replace('/', '\\'));
        return path;
    }
}

using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Locating the game when Ostraplan runs under Wine on Linux. There the registry has no Steam in it, so
/// auto-detection found nothing and every Linux user had to pick the folder by hand, and the game runs in Proton's
/// prefix rather than Ostraplan's, so the saves were looked for in a LocalLow that the game never writes to.
///
/// <para>Runs on Windows against temp directories: the Linux home folder is passed in rather than read from
/// Wine, and a Linux path in <c>libraryfolders.vdf</c> is written as the temp folder's path on its own drive,
/// which is what <see cref="WineHost.ToWindows"/> maps it back to.</para>
/// </summary>
public class WineHostTests
{
    [Theory]
    [InlineData(@"\??\Z:\home\valtora", @"Z:\home\valtora")]
    [InlineData(@"\??\Z:\home\valtora\", @"Z:\home\valtora")]
    [InlineData(@"\??\unix\home\valtora", @"\\?\unix\home\valtora")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("not a path", null)]
    public void WINEHOMEDIR_becomes_a_path_dotnet_can_open(string? wineHomeDir, string? expected) =>
        Assert.Equal(expected, WineHost.HomeFrom(wineHomeDir));

    [Theory]
    [InlineData("/mnt/games/SteamLibrary", @"Z:\home\v", @"Z:\mnt\games\SteamLibrary")]
    [InlineData("/mnt/games/SteamLibrary/", @"Z:\home\v", @"Z:\mnt\games\SteamLibrary")]
    [InlineData("/mnt/games/SteamLibrary", @"\\?\unix\home\v", @"\\?\unix\mnt\games\SteamLibrary")]
    [InlineData(@"D:\SteamLibrary", @"Z:\home\v", null)]
    public void A_linux_library_path_maps_onto_the_drive_holding_home(string linux, string home, string? expected) =>
        Assert.Equal(expected, WineHost.ToWindows(linux, home));

    [Theory]
    [InlineData("C:/users/steamuser/AppData/LocalLow/Blue Bottle Games/Ostranauts",
                @"P:\pfx\drive_c\users\steamuser\AppData\LocalLow\Blue Bottle Games\Ostranauts")]
    [InlineData(@"c:\users\steamuser\Saves", @"P:\pfx\drive_c\users\steamuser\Saves")]
    [InlineData("Z:/home/v/.local/share/Steam/steamapps/common/Ostranauts/Ostranauts_Data/Mods",
                "Z:/home/v/.local/share/Steam/steamapps/common/Ostranauts/Ostranauts_Data/Mods")]
    public void Only_the_games_own_C_drive_is_rerooted_into_the_proton_prefix(string path, string expected) =>
        Assert.Equal(expected, WineHost.MapProtonPath(path, @"P:\pfx\drive_c"));

    [Fact]
    public void The_game_is_found_in_a_secondary_linux_steam_library()
    {
        using var tmp = new TempDir();
        var home = Directory.CreateDirectory(Path.Combine(tmp.Path, "home")).FullName;
        var steam = Directory.CreateDirectory(Path.Combine(home, ".local", "share", "Steam", "steamapps")).Parent!.FullName;
        var library = Path.Combine(tmp.Path, "games", "SteamLibrary");
        var root = Install(Path.Combine(library, "steamapps", "common"));

        // Steam on Linux writes its libraries as Linux paths, the main one included.
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
            	"0"	{ "path"	"{{Linux(steam)}}" }
            	"1"	{ "path"	"{{Linux(library)}}" }
            }
            """);

        var env = GameEnv.Locate(null, null, home);
        Assert.Equal(root, env.GameRoot);
        Assert.Contains("Linux Steam library", env.DiscoveredVia);
    }

    [Fact]
    public void Saves_are_read_from_the_proton_prefix_the_game_writes_them_to()
    {
        using var tmp = new TempDir();
        var steamapps = Path.Combine(tmp.Path, "Steam", "steamapps");
        var root = Install(Path.Combine(steamapps, "common"));
        var steamuser = Path.Combine(steamapps, "compatdata", WineHost.AppId, "pfx", "drive_c", "users", "steamuser");
        var gameData = Path.Combine(steamuser, "AppData", "LocalLow", "Blue Bottle Games", "Ostranauts");
        var saves = Directory.CreateDirectory(Path.Combine(gameData, "Saves")).FullName;
        // What the game writes under Proton: a C: path, meaning Proton's C: rather than Ostraplan's.
        File.WriteAllText(Path.Combine(gameData, "settings.json"), """
            [ { "strName": "UserSettings",
                "strSaveLocation": "C:/users/steamuser/AppData/LocalLow/Blue Bottle Games/Ostranauts" } ]
            """);

        var env = GameEnv.Locate(root, null, wineHome: Path.Combine(tmp.Path, "home"));
        Assert.Equal(steamuser, env.GameProfile);
        Assert.Equal(saves, env.GameSavesSetting);
        Assert.Equal(saves, env.SavesDir);
    }

    [Fact]
    public void Without_a_settings_file_the_default_saves_folder_is_proton_ones()
    {
        using var tmp = new TempDir();
        var steamapps = Path.Combine(tmp.Path, "Steam", "steamapps");
        var root = Install(Path.Combine(steamapps, "common"));
        var saves = Directory.CreateDirectory(Path.Combine(steamapps, "compatdata", WineHost.AppId, "pfx", "drive_c",
            "users", "steamuser", "AppData", "LocalLow", "Blue Bottle Games", "Ostranauts", "Saves")).FullName;

        var env = GameEnv.Locate(root, null, wineHome: Path.Combine(tmp.Path, "home"));
        Assert.Null(env.GameSavesSetting);
        Assert.Equal(saves, env.SavesDir);
    }

    [Fact]
    public void On_windows_a_compatdata_folder_beside_the_game_changes_nothing()
    {
        using var tmp = new TempDir();
        var steamapps = Path.Combine(tmp.Path, "Steam", "steamapps");
        var root = Install(Path.Combine(steamapps, "common"));
        Directory.CreateDirectory(Path.Combine(steamapps, "compatdata", WineHost.AppId, "pfx", "drive_c", "users", "steamuser"));

        var env = GameEnv.Locate(root, null, wineHome: null);
        Assert.Equal(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", env.GameProfile);
    }

    /// <summary>A Windows temp path written the way Linux Steam would write it, on the reading that the drive
    /// holding the home folder maps <c>/</c>.</summary>
    private static string Linux(string windowsPath) => windowsPath[2..].Replace('\\', '/');

    private static string Install(string under)
    {
        var root = Directory.CreateDirectory(Path.Combine(under, "Ostranauts")).FullName;
        File.WriteAllText(Path.Combine(root, GameEnv.GameExeName), "");
        var streaming = Path.Combine(root, "Ostranauts_Data", "StreamingAssets");
        Directory.CreateDirectory(Path.Combine(streaming, "data"));
        Directory.CreateDirectory(Path.Combine(streaming, "images"));
        Directory.CreateDirectory(Path.Combine(root, "Ostranauts_Data", "Mods"));
        return root;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ostraplan-tests-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* a locked temp dir is not a test failure */ }
        }
    }
}

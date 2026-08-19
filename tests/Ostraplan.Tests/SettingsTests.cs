using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The two app-settings rules that a hand-edited file or an unusual install can break: the UI scale clamp, and
/// how a Saves folder is resolved when the game keeps its saves somewhere other than LocalLow. Both are pure
/// (temp directories only), so they run without the game.
/// </summary>
public class SettingsTests
{
    // ---- UI scale ----

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(2.0, 2.0)]
    [InlineData(0.8, 0.8)]     // the floor itself
    [InlineData(0.4, 0.8)]     // below the floor
    [InlineData(6.0, 2.0)]     // above the ceiling
    [InlineData(0.0, 1.0)]     // a settings file written before the key existed deserializes to 0 — unset, NOT the floor
    [InlineData(-1.0, 1.0)]    // nor is a negative one, which is equally not a scale anybody chose
    [InlineData(double.NaN, 1.0)]                // not a number: treat as unset rather than as a huge scale
    [InlineData(double.PositiveInfinity, 1.0)]
    public void Clamp_brings_any_stored_scale_into_range(double stored, double expected) =>
        Assert.Equal(expected, UiScaling.Clamp(stored), 3);

    [Theory]
    [InlineData(1.23, 1.25)]
    [InlineData(1.21, 1.20)]
    [InlineData(1.08, 1.10)]
    [InlineData(0.87, 0.85)]   // the step is the same one below 100% as above it
    [InlineData(0.82, 0.80)]
    public void Clamp_snaps_to_the_slider_step(double typed, double expected) =>
        Assert.Equal(expected, UiScaling.Clamp(typed), 3);

    [Fact]
    public void A_fresh_settings_file_starts_at_100_percent()
    {
        Assert.Equal(UiScaling.Default, new AppSettings().UiScale);
        Assert.Equal("150%", UiScaling.Percent(1.5));
    }

    // ---- saves folder resolution ----

    [Fact]
    public void ResolveSaves_accepts_the_folder_that_holds_Saves()
    {
        using var tmp = new TempDir();
        var saves = Directory.CreateDirectory(Path.Combine(tmp.Path, "Saves")).FullName;

        // what the game's own strSaveLocation names: the parent
        Assert.Equal(saves, GameEnv.ResolveSaves(tmp.Path));
        // and what a user picking a folder by hand will pick: the Saves folder itself
        Assert.Equal(saves, GameEnv.ResolveSaves(saves));
    }

    [Fact]
    public void ResolveSaves_reads_a_forward_slash_path_the_game_wrote()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Saves"));

        Assert.NotNull(GameEnv.ResolveSaves(tmp.Path.Replace('\\', '/')));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Q:\\nowhere\\at\\all")]
    [InlineData("<not a path>")]
    public void ResolveSaves_returns_null_for_anything_that_isnt_a_folder(string? path) =>
        Assert.Null(GameEnv.ResolveSaves(path));

    [Fact]
    public void The_user_override_wins_over_the_games_setting_and_the_default()
    {
        using var tmp = new TempDir();
        var mine = Directory.CreateDirectory(Path.Combine(tmp.Path, "mine", "Saves")).FullName;
        var game = Directory.CreateDirectory(Path.Combine(tmp.Path, "game", "Saves")).FullName;

        Assert.Equal(mine, Env(over: mine, fromGame: game).SavesDir);
        Assert.Equal(game, Env(over: null, fromGame: game).SavesDir);   // no override -> the game's own setting
        Assert.Equal(game, Env(over: "Q:\\gone", fromGame: game).SavesDir);   // an override that no longer exists falls through
    }

    private static GameEnv Env(string? over, string? fromGame) => new()
    {
        GameRoot = "C:\\Ostranauts",
        DiscoveredVia = "test",
        StreamingAssetsDir = "C:\\Ostranauts\\Ostranauts_Data\\StreamingAssets",
        ModsDir = "C:\\Ostranauts\\Ostranauts_Data\\Mods",
        SavesDirOverride = over,
        GameSavesSetting = fromGame,
    };

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

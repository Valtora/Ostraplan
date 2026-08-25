using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// What counts as an Ostranauts install. The check used to be "there is an Ostranauts_Data folder inside it",
/// which a <b>mod deploy target</b> satisfies: that is the same folder with a Mods subfolder under it and no game
/// around it. One resolved as the install, the catalogue then built from an empty core, and the planner opened
/// with an empty palette and said nothing at all. The suite had the same hole, reporting a wall of failures on a
/// machine with no game rather than the honest skip <c>TestData.RequireGame</c> promises.
///
/// <para>All of it runs on temp directories, so it holds on a machine with the game and on one without.</para>
/// </summary>
public class InstallDetectionTests
{
    [Fact]
    public void A_complete_install_is_accepted()
    {
        using var tmp = new TempDir();
        var root = Install(tmp.Path);

        Assert.Null(GameEnv.InstallProblem(root));
        Assert.Equal(root, GameEnv.Locate(root).GameRoot);
    }

    [Fact]
    public void A_mod_deploy_target_is_not_an_install()
    {
        using var tmp = new TempDir();
        // Exactly what ModTools deploys into: Ostranauts_Data\Mods, with no game around it.
        var root = Directory.CreateDirectory(Path.Combine(tmp.Path, "Ostranauts")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "Ostranauts_Data", "Mods", "SomeMod"));

        var why = GameEnv.InstallProblem(root);
        Assert.NotNull(why);
        Assert.Contains(GameEnv.GameExeName, why);
        Assert.Throws<DirectoryNotFoundException>(() => GameEnv.Locate(root));
    }

    [Theory]
    [InlineData("data")]
    [InlineData("images")]
    public void A_half_downloaded_install_is_refused_by_the_folder_it_is_missing(string missing)
    {
        using var tmp = new TempDir();
        var root = Install(tmp.Path);
        Directory.Delete(Path.Combine(root, "Ostranauts_Data", "StreamingAssets", missing), recursive: true);

        var why = GameEnv.InstallProblem(root);
        Assert.NotNull(why);
        Assert.Contains(missing, why);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Q:\\nowhere\\at\\all")]
    [InlineData("<not a path>")]
    public void Anything_that_isnt_a_folder_is_reported_rather_than_thrown(string? path) =>
        Assert.NotNull(GameEnv.InstallProblem(path));

    /// <summary>
    /// A settings file written by hand can carry <c>"gameRootOverride": ""</c>. That reached
    /// <c>Path.GetFullPath</c>, which throws <see cref="System.ArgumentException"/> for it, and the startup gate
    /// catches only <see cref="DirectoryNotFoundException"/>: the app came up on the crash handler rather than on
    /// the gate that exists to explain this.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<not a path>")]
    public void A_malformed_override_reaches_the_install_gate_not_the_crash_handler(string path) =>
        Assert.Throws<DirectoryNotFoundException>(() => GameEnv.Locate(path));

    [Fact]
    public void Every_reason_names_the_folder_it_judged()
    {
        using var tmp = new TempDir();
        var root = Directory.CreateDirectory(Path.Combine(tmp.Path, "Ostranauts")).FullName;

        // The message is put in front of the user verbatim, so it has to say which folder was looked at.
        Assert.Contains(root, GameEnv.InstallProblem(root));
        Assert.Contains(root, GameEnv.InstallProblem(root.Replace('\\', '/')));   // and normalize what it echoes
    }

    /// <summary>Build the shape <see cref="GameEnv.InstallProblem"/> asks for: the exe, and the two
    /// StreamingAssets folders the data and the sprites are read from.</summary>
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

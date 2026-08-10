using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// How a data complaint is attributed, and what that decides. A defect in the game's own data is permanent and no
/// user can fix it, so it is logged and reported but kept off the toolbar badge; a defect a mod brought in is
/// theirs to act on and surfaces. Game-free: builds a synthetic install in a temp folder rather than reading the
/// real one, so it asserts the same way on a machine with no Ostranauts at all.
/// </summary>
public class DataWarningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ostraplan-data-" + Guid.NewGuid().ToString("N"));

    private string CoreData => Path.Combine(_root, "Ostranauts_Data", "StreamingAssets");
    private string ModsDir => Path.Combine(_root, "mods");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Write one items file into a source ("core", or a local mod folder name).</summary>
    private void WriteItems(string source, string fileName, string json)
    {
        var dir = Path.Combine(source == "core" ? CoreData : Path.Combine(ModsDir, source), "data", "items");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), json);
    }

    private void WriteLoadOrder(params string[] entries)
    {
        Directory.CreateDirectory(ModsDir);
        var list = string.Join(", ", entries.Select(e => $"\"{e}\""));
        File.WriteAllText(Path.Combine(ModsDir, "loading_order.json"), $"[ {{ \"aLoadOrder\" : [ {list} ] }} ]");
    }

    private DataIndex Load()
    {
        Directory.CreateDirectory(CoreData);
        return DataIndex.Load(new GameEnv
        {
            GameRoot = _root,
            DiscoveredVia = "test",
            StreamingAssetsDir = CoreData,
            ModsDir = ModsDir,
        });
    }

    private const string Unreadable = "{ \"strName\" : \"Broken\" ";   // a missing brace, which no mend can rescue

    [Fact]
    public void A_defect_in_the_games_own_data_is_marked_core()
    {
        WriteLoadOrder("core");
        WriteItems("core", "broken.json", Unreadable);

        var warning = Assert.Single(Load().Warnings);
        Assert.True(warning.Core);
        Assert.Equal("core", warning.Source);
        Assert.Contains("invalid JSON", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_defect_a_mod_brought_in_is_marked_as_that_mods()
    {
        WriteLoadOrder("core", "BadMod");
        WriteItems("BadMod", "broken.json", Unreadable);

        var warning = Assert.Single(Load().Warnings);
        Assert.False(warning.Core);          // the user can disable or update this one
        Assert.Equal("BadMod", warning.Source);
    }

    [Fact]
    public void A_mod_that_calls_itself_core_cannot_pass_its_defects_off_as_unfixable()
    {
        // attribution is by source identity, not by label text, so the name is not a way to hide behind the game
        WriteLoadOrder("core", "core|edit");
        Directory.CreateDirectory(Path.Combine(ModsDir, "core"));
        WriteItems("core", "fine.json", """[ { "strName" : "Ok" } ]""");

        var index = Load();
        Assert.True(index.IsCoreSource("core"));
        Assert.False(index.IsCoreSource("BadMod"));
    }

    [Fact]
    public void A_missing_mod_folder_is_the_users_setup_and_always_surfaces()
    {
        WriteLoadOrder("core", "NotInstalled");

        var warning = Assert.Single(Load().Warnings);
        Assert.False(warning.Core);
        Assert.Contains("NotInstalled", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_line_breaks_in_core_prose_are_mended_and_never_warned_about()
    {
        // the stock 1.0.0.7 case, end to end: the file loads, its def is usable, and nothing is reported as wrong
        WriteLoadOrder("core");
        WriteItems("core", "prose.json", "[ { \"strName\" : \"Plot_79au\", \"strDesc\" : \"At long last.\r\n\r\nA golden sphere.\" } ]");

        var index = Load();

        Assert.Empty(index.Warnings);
        Assert.Single(index.Repaired);
        Assert.True(index.Type("items").ContainsKey("Plot_79au"));
        Assert.Equal("At long last.\r\n\r\nA golden sphere.",
            index.Type("items")["Plot_79au"].El.GetProperty("strDesc").GetString());
    }

    [Fact]
    public void A_warning_reads_as_its_source_then_the_problem()
    {
        Assert.Equal("core: something is off.", new DataWarning("core", "something is off.", Core: true).ToString());
    }
}

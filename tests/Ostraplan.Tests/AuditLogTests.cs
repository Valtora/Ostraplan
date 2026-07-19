using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The activity log's two pure pieces: <see cref="PathScrub"/> (strips the account name and
/// user-profile paths so the on-disk trail can be shared for support) and <see cref="AuditLog.Label"/>
/// (the terse command line). Both run without the game install. File I/O in AuditLog itself is not
/// exercised here — these tests never call anything that writes to disk.
/// </summary>
public class AuditLogTests
{
    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');

    [Fact]
    public void Scrub_replaces_the_user_profile_prefix_and_hides_the_account_name()
    {
        var path = Path.Combine(Profile, "OneDrive", "Projects", "Vagabond.oplan");
        var scrubbed = PathScrub.Clean($"Opened {path}.");

        Assert.StartsWith("Opened %USERPROFILE%", scrubbed);
        Assert.DoesNotContain(Profile, scrubbed);                       // no C:\Users\<name>
        Assert.DoesNotContain(Environment.UserName, scrubbed);         // and the bare name is gone too
        Assert.EndsWith("Vagabond.oplan.", scrubbed);                  // the non-identifying tail survives
    }

    [Fact]
    public void Scrub_collapses_localLow_without_mangling_it_into_localAppData()
    {
        // The game's saves live under <profile>\AppData\LocalLow — whose prefix "…\AppData\Local" is
        // itself the LocalAppData root. Longest-first ordering must catch LocalLow whole.
        var save = Path.Combine(Profile, "AppData", "LocalLow", "Blue Bottle Games", "Ostranauts", "Saves", "Trip");
        var scrubbed = PathScrub.Clean($"Wrote {save}");

        Assert.Contains("%LOCALLOW%", scrubbed);
        Assert.DoesNotContain("%LOCALAPPDATA%", scrubbed);   // not "%LOCALAPPDATA%Low"
        Assert.DoesNotContain(Profile, scrubbed);
        Assert.EndsWith("Ostranauts\\Saves\\Trip", scrubbed);
    }

    [Fact]
    public void Scrub_tokenises_localAppData_and_roaming()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\', '/');
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).TrimEnd('\\', '/');

        Assert.Equal("%LOCALAPPDATA%\\Ostraplan", PathScrub.Clean(Path.Combine(local, "Ostraplan")));
        Assert.Equal("%APPDATA%\\Ostraplan\\audit.log", PathScrub.Clean(Path.Combine(roaming, "Ostraplan", "audit.log")));
    }

    [Fact]
    public void Scrub_leaves_a_line_with_no_user_info_untouched()
    {
        Assert.Equal("Edit: Place", PathScrub.Clean("Edit: Place"));
        Assert.Equal("Tool: Airlock", PathScrub.Clean("Tool: Airlock"));
    }

    [Fact]
    public void Scrub_only_hits_the_account_name_on_a_word_boundary()
    {
        var user = Environment.UserName;
        if (string.IsNullOrEmpty(user)) return;   // headless account — nothing to assert

        Assert.Equal("player <user> here", PathScrub.Clean($"player {user} here"));   // standalone → scrubbed
        Assert.Equal($"{user}ington", PathScrub.Clean($"{user}ington"));              // substring of a word → kept
    }

    [Fact]
    public void Scrub_is_idempotent()
    {
        var line = $"Opened {Path.Combine(Profile, "ships", "x.oplan")}";
        var once = PathScrub.Clean(line);
        Assert.Equal(once, PathScrub.Clean(once));
    }

    [Fact]
    public void Command_labels_are_terse_and_carry_the_action()
    {
        Placement P(string def) => new() { DefName = def };

        Assert.Equal("Edit: Place", AuditLog.Label(CommandAction.Do, new PlaceCommand(P("ItmWall1x1"))));
        Assert.Equal("Undo: Place", AuditLog.Label(CommandAction.Undo, new PlaceCommand(P("ItmWall1x1"))));
        Assert.Equal("Redo: Move", AuditLog.Label(CommandAction.Redo, new MoveCommand([P("A")], 1, 0)));
        Assert.Equal("Edit: Remove", AuditLog.Label(CommandAction.Do, new RemoveCommand([P("A"), P("B")])));
        Assert.Equal("Edit: Composite", AuditLog.Label(CommandAction.Do, new CompositeCommand([new PlaceCommand(P("A"))])));
        Assert.Equal("Edit: SetPoses", AuditLog.Label(CommandAction.Do, new SetPosesCommand([(P("A"), 0, 0, 0)])));
    }

    // ---- detailed command descriptions (IAuditDescribable), the "what @where" a bug report needs ----

    // A stand-in catalog resolver: known defs get a friendly name, unknown defs fall back to the raw def.
    private static readonly Func<string, string?> Friendly = def => def switch
    {
        "ItmStationNav" => "Nav Station",
        "ItmWall" => "Wall",
        _ => null,
    };

    private static Placement Part(string def, int x, int y, int rot = 0) => new() { DefName = def, X = x, Y = y, Rot = rot };

    [Fact]
    public void Describe_place_records_friendly_name_tile_and_rotation()
    {
        Assert.Equal("Edit: Place Nav Station @(12,7) r90",
            AuditLog.Label(CommandAction.Do, new PlaceCommand(Part("ItmStationNav", 12, 7, 90)), Friendly));
    }

    [Fact]
    public void Describe_omits_rot_zero_and_shows_the_raw_name_for_an_unknown_def()
    {
        Assert.Equal("Edit: Place ItmMystery @(0,0)",
            AuditLog.Label(CommandAction.Do, new PlaceCommand(Part("ItmMystery", 0, 0)), Friendly));
    }

    [Fact]
    public void Describe_remove_handles_a_single_part_and_summarises_a_batch()
    {
        Assert.Equal("Edit: Remove Wall @(1,2)",
            AuditLog.Label(CommandAction.Do, new RemoveCommand([Part("ItmWall", 1, 2)]), Friendly));

        Assert.Equal("Edit: Remove ×3 (Wall, Nav Station)",
            AuditLog.Label(CommandAction.Do,
                new RemoveCommand([Part("ItmWall", 0, 0), Part("ItmWall", 1, 0), Part("ItmStationNav", 2, 0)]), Friendly));
    }

    [Fact]
    public void Describe_move_reports_a_signed_delta()
    {
        Assert.Equal("Edit: Move Wall by (+3,-2)",
            AuditLog.Label(CommandAction.Do, new MoveCommand([Part("ItmWall", 0, 0)], 3, -2), Friendly));
    }

    [Fact]
    public void Describe_composite_spells_out_a_form_swap()
    {
        var swap = new CompositeCommand([
            new RemoveCommand([Part("ItmStationNav", 4, 5)]),
            new PlaceCommand(Part("ItmStationNavLoose", 4, 5)),
        ]);
        Assert.Equal("Edit: Remove Nav Station @(4,5) + Place ItmStationNavLoose @(4,5)",
            AuditLog.Label(CommandAction.Do, swap, Friendly));
    }

    [Fact]
    public void Describe_undo_and_redo_keep_their_prefixes()
    {
        Assert.StartsWith("Undo: ", AuditLog.Label(CommandAction.Undo, new PlaceCommand(Part("ItmWall", 0, 0)), Friendly));
        Assert.StartsWith("Redo: ", AuditLog.Label(CommandAction.Redo, new PlaceCommand(Part("ItmWall", 0, 0)), Friendly));
    }

    // ---- LogTail: reading + scrubbing the tail of a log file for the diagnostics attachment ----

    [Fact]
    public void LogTail_returns_the_last_lines_scrubbed_of_the_account_name()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-logtail-{Guid.NewGuid():N}.log");
        try
        {
            var user = Environment.UserName;
            File.WriteAllLines(tmp, ["line1", "line2", $@"at Foo() in {Profile}\proj\Bar.cs:line 9", "line4"]);

            var tail = LogTail.LastLines(tmp, 2);
            Assert.Equal(2, tail.Count);                          // only the last two lines
            Assert.Equal("line4", tail[1]);
            if (!string.IsNullOrEmpty(user))                      // the profile path in the trace is scrubbed away
                Assert.DoesNotContain(user, string.Join("\n", tail), StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void LogTail_on_a_missing_file_is_empty()
    {
        Assert.Empty(LogTail.LastLines(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.log"), 10));
    }
}

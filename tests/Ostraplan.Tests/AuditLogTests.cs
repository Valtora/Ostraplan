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
}

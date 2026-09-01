using System.IO;
using Ostraplan.App.Bundle;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// What the bundle editor will and will not take into a pack, decided when a design is added rather than at the
/// write. A refusal at the write is a refusal after the user thought the work was done, and in one case it would
/// not be a refusal at all: a design with unloaded parts exports quite happily, minus those parts.
/// </summary>
public class BundleMemberTests
{
    private static Catalog Cat() => new Fixtures().Floor("Floor").Build();

    private static void InTempDir(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "OstraplanMember_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string WriteDesign(string dir, string name, string def = "Floor", string? kind = null, int parts = 1)
    {
        var file = new OplanFile
        {
            Meta = new OplanMeta { Name = name },
            Kind = kind,
            Parts = [.. Enumerable.Range(0, parts).Select(i => new OplanPart { Def = def, X = i, Y = 0 })],
        };
        var path = Path.Combine(dir, name + ".oplan");
        file.Save(path);
        return path;
    }

    private static BundleMember Read(string path, Catalog cat) =>
        BundleMember.Read(new BundleEntry { Path = path }, path, cat);

    [Fact]
    public void A_design_that_can_be_a_ship_mod_comes_in_with_its_name_and_part_count()
    {
        InTempDir(dir =>
        {
            var member = Read(WriteDesign(dir, "Kestrel", parts: 3), Cat());

            Assert.Null(member.Problem);
            Assert.NotNull(member.Doc);
            Assert.Equal("Kestrel", member.Name);
            Assert.Equal("Kestrel", member.StrName);
            Assert.Equal(3, member.PartCount);
        });
    }

    /// <summary>
    /// The important one. A part whose def is not loaded is dropped by <c>ToDocument</c>, so a mod export would
    /// write a ship with holes in it and say nothing. Docking only warns about this, because a hole still answers
    /// a docking question; a mod cannot afford to.
    /// </summary>
    [Fact]
    public void A_design_whose_parts_are_not_loaded_is_refused_rather_than_exported_with_holes()
    {
        InTempDir(dir =>
        {
            var member = Read(WriteDesign(dir, "Modded", def: "ItmFromAModYouRemoved"), Cat());

            Assert.Null(member.Doc);
            Assert.Contains("not in your current game and mods data", member.Problem);
        });
    }

    [Fact]
    public void An_apartment_is_refused_because_a_mod_sells_ships()
    {
        InTempDir(dir =>
        {
            var member = Read(WriteDesign(dir, "Home", kind: "Residence"), Cat());

            Assert.Null(member.Doc);
            Assert.Contains("apartment", member.Problem);
        });
    }

    [Fact]
    public void An_empty_design_is_refused()
    {
        InTempDir(dir =>
        {
            var member = Read(WriteDesign(dir, "Nothing", parts: 0), Cat());

            Assert.Null(member.Doc);
            Assert.Contains("no parts", member.Problem);
        });
    }

    [Fact]
    public void A_design_that_has_moved_or_will_not_parse_says_so_instead_of_throwing()
    {
        InTempDir(dir =>
        {
            var gone = Read(Path.Combine(dir, "Gone.oplan"), Cat());
            Assert.Contains("not where the pack says it is", gone.Problem);

            var broken = Path.Combine(dir, "Broken.oplan");
            File.WriteAllText(broken, "not an oplan at all");
            Assert.NotNull(Read(broken, Cat()).Problem);
        });
    }

    /// <summary>A pack can rename a ship without touching the design, which is how two designs that share a name
    /// stop colliding over the one thing the game keys everything on.</summary>
    [Fact]
    public void A_name_override_wins_over_the_designs_own_name()
    {
        InTempDir(dir =>
        {
            var path = WriteDesign(dir, "Kestrel");
            var member = BundleMember.Read(new BundleEntry { Path = path, NameOverride = "Kestrel Mk2" }, path, Cat());

            Assert.Equal("Kestrel Mk2", member.Name);
            Assert.Equal("Kestrel Mk2", member.StrName);
        });
    }

    /// <summary>A replacement's <c>strName</c> is the ship it replaces: that is the override key the game keys the
    /// swap on, so it wins over whatever the design is called.</summary>
    [Fact]
    public void A_replacement_takes_the_name_of_the_ship_it_replaces()
    {
        InTempDir(dir =>
        {
            var path = WriteDesign(dir, "My Refit");
            var member = BundleMember.Read(new BundleEntry { Path = path, Replaces = "Vagabond+" }, path, Cat());

            Assert.Equal("My Refit", member.Name);
            Assert.Equal("Vagabond+", member.StrName);
        });
    }
}

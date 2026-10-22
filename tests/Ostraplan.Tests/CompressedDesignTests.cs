using System.IO;
using System.IO.Compression;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The <c>.oplanz</c>: the same design document, zipped for sharing. What it writes, what it reads back, and the
/// zips somebody else made that it has to read too.
/// </summary>
public class CompressedDesignTests
{
    private static void InTempDir(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "OstraplanCompressed_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A design with enough in it to be worth compressing: a hold's worth of cargo, much as an imported
    /// ship carries.</summary>
    private static OplanFile Sample() => new()
    {
        Meta = new OplanMeta { Name = "Kestrel", Author = "Valtora", PublicName = "Wayfarer" },
        Parts = Enumerable.Range(0, 200).Select(i => new OplanPart
        {
            Def = "ItmLocker01", X = i % 20, Y = i / 20,
            Cargo = [new OplanCargo { Def = "ItmFoodRation", StrID = $"ration-{i}", Authored = true, Stack = 4, IsStack = true }],
            CargoOwn = true,
        }).ToList(),
        Zones = [new OplanZone { Name = "Hold", Color = [0.2, 0.6, 0.9, 0.4], Tiles = [[3, 2], [4, 2]] }],
    };

    /// <summary>Write a zip by hand, the way a player's archiver would, holding the named entries.</summary>
    private static void WriteZip(string path, params (string Name, string Text)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
    }

    [Fact]
    public void A_design_round_trips_through_an_oplanz_unchanged()
    {
        InTempDir(dir =>
        {
            var path = Path.Combine(dir, "Kestrel.oplanz");
            var design = Sample();
            design.Save(path);

            Assert.Equal(design.ToJson(), OplanFile.Load(path).ToJson());
        });
    }

    /// <summary>An <c>.oplanz</c> is a plain zip, so it can be opened with anything that opens one, and unzipping
    /// it gives back the <c>.oplan</c> that would have been saved uncompressed.</summary>
    [Fact]
    public void An_oplanz_is_a_zip_holding_the_oplan_named_after_it()
    {
        InTempDir(dir =>
        {
            var path = Path.Combine(dir, "Kestrel.oplanz");
            var design = Sample();
            design.Save(path);

            using var zip = ZipFile.OpenRead(path);
            var entry = Assert.Single(zip.Entries);
            Assert.Equal("Kestrel.oplan", entry.FullName);
            using var reader = new StreamReader(entry.Open());
            Assert.Equal(design.ToJson(), reader.ReadToEnd());
        });
    }

    [Fact]
    public void An_oplanz_is_much_smaller_than_the_oplan()
    {
        InTempDir(dir =>
        {
            var plain = Path.Combine(dir, "Kestrel.oplan");
            var zipped = Path.Combine(dir, "Kestrel.oplanz");
            Sample().Save(plain);
            Sample().Save(zipped);

            Assert.True(new FileInfo(zipped).Length * 5 < new FileInfo(plain).Length,
                $"{new FileInfo(zipped).Length} bytes zipped against {new FileInfo(plain).Length} plain");
        });
    }

    /// <summary>An <c>.oplan</c> stays exactly what it was: the extension decides the form, and plain is the
    /// default for everything but an <c>.oplanz</c>.</summary>
    [Fact]
    public void An_oplan_is_still_written_as_plain_json()
    {
        InTempDir(dir =>
        {
            var path = Path.Combine(dir, "Kestrel.oplan");
            var design = Sample();
            design.Save(path);

            Assert.Equal(design.ToJson(), File.ReadAllText(path));
        });
    }

    /// <summary>A file renamed on its way through a chat still opens: what is in it decides how it is read, not
    /// what it is called.</summary>
    [Fact]
    public void A_file_is_read_by_what_is_in_it_not_by_its_name()
    {
        InTempDir(dir =>
        {
            var zippedButNamedPlain = Path.Combine(dir, "a.oplan");
            var plainButNamedZipped = Path.Combine(dir, "b.oplanz");
            Sample().Save(Path.Combine(dir, "a.oplanz"));
            File.Move(Path.Combine(dir, "a.oplanz"), zippedButNamedPlain);
            File.WriteAllText(plainButNamedZipped, Sample().ToJson());

            Assert.Equal("Kestrel", OplanFile.Load(zippedButNamedPlain).Meta.Name);
            Assert.Equal("Kestrel", OplanFile.Load(plainButNamedZipped).Meta.Name);
        });
    }

    /// <summary>The zips already being passed around: one design in a folder, with a readme beside it and the
    /// shadow files a Mac adds. The design is found and the rest is left alone.</summary>
    [Fact]
    public void A_zip_somebody_made_opens_on_the_one_design_inside()
    {
        InTempDir(dir =>
        {
            var path = Path.Combine(dir, "Kestrel by someone.zip");
            WriteZip(path,
                ("Kestrel/readme.txt", "Fly it gently."),
                ("Kestrel/Kestrel.oplan", Sample().ToJson()),
                ("__MACOSX/Kestrel/._Kestrel.oplan", "not a design"));

            Assert.Equal("Kestrel", OplanFile.Load(path).Meta.Name);
        });
    }

    [Fact]
    public void A_zip_with_no_design_in_it_is_refused()
    {
        InTempDir(dir =>
        {
            var path = Path.Combine(dir, "My Mod.zip");
            WriteZip(path, ("data/ships/Kestrel.json", "{}"), ("Kestrel.oplanmod", "{}"));

            var ex = Assert.Throws<InvalidDataException>(() => OplanFile.Load(path));
            Assert.Contains("no Ostraplan design", ex.Message);
        });
    }

    /// <summary>Which of several designs was meant is not a guess to make, so a zip holding more than one says so
    /// rather than opening whichever came first.</summary>
    [Fact]
    public void A_zip_holding_several_designs_is_refused_rather_than_one_picked()
    {
        InTempDir(dir =>
        {
            var path = Path.Combine(dir, "Fleet.zip");
            WriteZip(path, ("Kestrel.oplan", Sample().ToJson()), ("Heron.oplan", Sample().ToJson()));

            var ex = Assert.Throws<InvalidDataException>(() => OplanFile.Load(path));
            Assert.Contains("2 designs", ex.Message);
        });
    }

    [Fact]
    public void A_later_format_inside_a_zip_is_refused_the_same_as_outside_one()
    {
        InTempDir(dir =>
        {
            var path = Path.Combine(dir, "Future.oplanz");
            WriteZip(path, ("Future.oplan", """{ "formatVersion": 99, "parts": [] }"""));

            Assert.Throws<InvalidDataException>(() => OplanFile.Load(path));
        });
    }

    [Fact]
    public void Saving_over_an_oplanz_replaces_it_whole()
    {
        InTempDir(dir =>
        {
            var path = Path.Combine(dir, "Kestrel.oplanz");
            Sample().Save(path);
            var smaller = new OplanFile { Meta = new OplanMeta { Name = "Kestrel" } };
            smaller.Save(path);

            Assert.Equal(smaller.ToJson(), OplanFile.Load(path).ToJson());
            using var zip = ZipFile.OpenRead(path);
            Assert.Single(zip.Entries);
        });
    }
}

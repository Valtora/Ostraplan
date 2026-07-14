using System.IO;
using System.Linq;
using System.Text.Json;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Device signal connections (the <c>Electrical</c> GPM wiring): validity rules, undoable add/remove, the
/// <c>.oplan</c> index-pair round-trip (incl. remapping when a part is dropped), and — against the live install —
/// that an export bakes each wired part's <c>inputConnections</c>/<c>outputConnections</c>.
/// </summary>
public class DeviceLinkTests
{
    private static Catalog Fake(params PartDef[] parts) => new()
    {
        Parts = parts,
        ByDefName = parts.ToDictionary(p => p.DefName),
        Loots = new Dictionary<string, LootDef>(),
        Triggers = new Dictionary<string, CondTriggerDef>(),
        Warnings = [],
    };

    /// <summary>A 1×1 part with the given starting conds — signalable/installed as needed.</summary>
    private static PartDef MakePart(string name, params string[] conds) => new(
        name, name, "SENS", "core",
        new ItemDef(name, "", false, null, 0, 1, ["L"], [], []),
        null, [], [], conds, new Dictionary<string, double>(), new Dictionary<string, (double, double)>());

    private static PartDef Device(string name) => MakePart(name, "IsSignalable", "IsInstalled");

    private static Placement Place(ShipDocument doc, string def, int x, int y)
    {
        var p = new Placement { DefName = def, X = x, Y = y };
        new PlaceCommand(p).Do(doc);
        return p;
    }

    [Fact]
    public void CanConnect_requires_two_distinct_installed_signalable_parts()
    {
        var cat = Fake(Device("Alarm"), Device("Pump"), MakePart("Wall", "IsWall"),
            MakePart("LooseAlarm", "IsSignalable"));   // signalable but NOT installed
        var doc = new ShipDocument(cat);
        var a = Place(doc, "Alarm", 0, 0);
        var b = Place(doc, "Pump", 2, 0);
        var wall = Place(doc, "Wall", 4, 0);
        var loose = Place(doc, "LooseAlarm", 6, 0);

        Assert.True(DeviceLinks.CanConnect(doc, a, b));
        Assert.False(DeviceLinks.CanConnect(doc, a, a));       // self
        Assert.False(DeviceLinks.CanConnect(doc, a, wall));    // target not signalable
        Assert.False(DeviceLinks.CanConnect(doc, a, loose));   // signalable but not installed
        Assert.True(DeviceLinks.IsConnectable(doc, a));
        Assert.False(DeviceLinks.IsConnectable(doc, loose));
    }

    [Fact]
    public void AddLink_and_RemoveLink_are_undoable_and_dedup()
    {
        var cat = Fake(Device("A"), Device("B"));
        var doc = new ShipDocument(cat);
        var stack = new CommandStack();
        var a = Place(doc, "A", 0, 0);
        var b = Place(doc, "B", 2, 0);
        var link = new DeviceLink(a.Id, b.Id);

        stack.Push(doc, new AddLinkCommand(link));
        Assert.Single(doc.Links);
        Assert.False(DeviceLinks.CanConnect(doc, a, b));   // already linked → not addable again

        new AddLinkCommand(link).Do(doc);              // exact duplicate is a no-op
        Assert.Single(doc.Links);

        stack.Undo(doc);
        Assert.Empty(doc.Links);
        stack.Redo(doc);
        Assert.Single(doc.Links);

        stack.Push(doc, new RemoveLinkCommand(link));
        Assert.Empty(doc.Links);
    }

    [SkippableFact]
    public void Link_survives_oplan_round_trip_by_part_index()
    {
        var g = TestData.RequireGame();   // FromDocument needs a real DataIndex (versions + mods manifest)
        var cat = Fake(Device("A"), Device("B"), Device("C"));
        var doc = new ShipDocument(cat);
        var a = Place(doc, "A", 0, 0);
        var b = Place(doc, "B", 2, 0);
        var c = Place(doc, "C", 4, 0);
        new AddLinkCommand(new DeviceLink(a.Id, c.Id)).Do(doc);   // A (index 0) → C (index 2)

        var tmp = Path.Combine(Path.GetTempPath(), $"ostraplan-link-{System.Guid.NewGuid():N}.oplan");
        try
        {
            OplanFile.FromDocument(doc, g.Index, new OplanMeta()).Save(tmp);
            var (doc2, missing) = OplanFile.Load(tmp).ToDocument(cat);

            Assert.Empty(missing);
            var link = Assert.Single(doc2.Links);
            // the link reconnects the SAME two defs (A → C), by index
            Assert.Equal("A", doc2.ById(link.Source)?.DefName);
            Assert.Equal("C", doc2.ById(link.Target)?.DefName);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Link_to_a_dropped_missing_part_is_skipped_without_corrupting_others()
    {
        // Author a file whose part index 1 is a missing def; a link 0→2 must still resolve to the right parts
        // after the drop shifts nothing (indices are original), and a link touching the dropped part vanishes.
        var file = new OplanFile
        {
            Parts =
            [
                new OplanPart { Def = "A", X = 0, Y = 0 },
                new OplanPart { Def = "GONE", X = 2, Y = 0 },   // def not in the catalog → dropped
                new OplanPart { Def = "C", X = 4, Y = 0 },
            ],
            Links =
            [
                new OplanLink { Src = 0, Tgt = 2 },   // A → C: both survive
                new OplanLink { Src = 0, Tgt = 1 },   // A → GONE: dropped
            ],
        };
        var cat = Fake(Device("A"), Device("C"));
        var (doc, missing) = file.ToDocument(cat);

        Assert.Single(missing);
        var link = Assert.Single(doc.Links);
        Assert.Equal("A", doc.ById(link.Source)?.DefName);
        Assert.Equal("C", doc.ById(link.Target)?.DefName);
    }

    [SkippableFact]
    public void Export_bakes_input_and_output_connections_on_wired_devices()
    {
        var g = TestData.RequireGame();
        // two installed, signalable buildable parts from the real catalog
        var devices = g.Catalog.Parts
            .Where(p => p.IsSignalable && p.StartingConds.Contains("IsInstalled"))
            .DistinctBy(p => p.DefName).Take(2).ToList();
        Skip.If(devices.Count < 2, "no two signalable installed parts in this install");

        var doc = new ShipDocument(g.Catalog);
        var src = new Placement { DefName = devices[0].DefName, X = 0, Y = 0 };
        var tgt = new Placement { DefName = devices[1].DefName, X = 3, Y = 0 };
        new PlaceCommand(src).Do(doc);
        new PlaceCommand(tgt).Do(doc);
        new AddLinkCommand(new DeviceLink(src.Id, tgt.Id)).Do(doc);

        var specs = RoomCertifier.LoadSpecs(g.Index);
        var dest = Path.Combine(Path.GetTempPath(), "OstraplanLinkExport_" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        try
        {
            var opts = new ExportOptions("LinkTest", "Tester", "", "1.0.0",
                g.Env.InstalledVersion ?? GameEnv.VerifiedGameVersion, dest, "LinkTest");
            var result = ShipExport.Write(doc, g.Catalog, specs, opts, g.Index);

            using var jd = JsonDocument.Parse(File.ReadAllText(result.ShipJsonPath));
            var ship = jd.RootElement[0];
            var items = ship.GetProperty("aItems");

            string ElectricalConn(JsonElement item, string key)
            {
                if (!item.TryGetProperty("aGPMSettings", out var gpms)) return "";
                foreach (var gpm in gpms.EnumerateArray())
                    if (gpm.GetProperty("strName").GetString() == "Electrical")
                    {
                        var map = gpm.GetProperty("dictGUIPropMap").EnumerateArray().Select(e => e.GetString()).ToList();
                        var i = map.IndexOf(key);
                        return i >= 0 && i + 1 < map.Count ? map[i + 1] ?? "" : "";
                    }
                return "";
            }

            // find the two device items by def name; the source lists the target in outputConnections, the target
            // lists the source in inputConnections (both by the fresh export strID).
            var srcItem = items.EnumerateArray().Single(it => it.GetProperty("strName").GetString() == devices[0].DefName);
            var tgtItem = items.EnumerateArray().Single(it => it.GetProperty("strName").GetString() == devices[1].DefName);
            var tgtId = tgtItem.GetProperty("strID").GetString()!;
            var srcId = srcItem.GetProperty("strID").GetString()!;

            Assert.Contains(tgtId, ElectricalConn(srcItem, "outputConnections"));
            Assert.Contains(srcId, ElectricalConn(tgtItem, "inputConnections"));
            Assert.Equal("", ElectricalConn(srcItem, "inputConnections"));   // source has no incoming
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }
}

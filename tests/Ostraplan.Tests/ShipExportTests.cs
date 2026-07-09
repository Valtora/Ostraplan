using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The P3 export Law gate: a design exported to the game's JsonShip shape must, when the game
/// re-parses it and recomputes rooms/rating on full load, reproduce exactly what Ostraplan
/// baked — otherwise the broker rating would visibly change on load. Proven by round-tripping
/// through the real loader path: <c>doc → ShipExport → ShipTemplate.Parse → ShipGrid.FromTemplate</c>
/// must rebuild the same tiles, the same room partition, and the same rating. No-ops without the install.
/// </summary>
public class ShipExportTests
{
    private static bool Ready((GameEnv, DataIndex, Catalog)? g) =>
        g is { } gg && gg.Item3.ByDefName.ContainsKey("ItmWall1x1") && gg.Item3.ByDefName.ContainsKey("ItmFloorGrate01");

    private static void Place(ShipDocument doc, string def, int x, int y, int rot = 0) =>
        new PlaceCommand(new Placement { DefName = def, X = x, Y = y, Rot = rot }).Do(doc);

    /// <summary>A 5×7 hull split by a full-width door into two floored compartments — walls, floor and a door.</summary>
    private static ShipDocument BuildDooredHull(Catalog catalog)
    {
        var doc = new ShipDocument(catalog);
        for (var x = 0; x < 5; x++) { Place(doc, "ItmWall1x1", x, 0); Place(doc, "ItmWall1x1", x, 6); }
        for (var y = 1; y <= 5; y++)
        {
            Place(doc, "ItmWall1x1", 0, y); Place(doc, "ItmWall1x1", 4, y);
            for (var x = 1; x < 4; x++) Place(doc, "ItmFloorGrate01", x, y);
        }
        if (catalog.ByDefName.ContainsKey("ItmDoor01Open")) Place(doc, "ItmDoor01Open", 0, 3);
        return doc;
    }

    /// <summary>Re-parse an exported ship string and rebuild the analysis grid + room partition through the loader.</summary>
    private static (ShipGrid Grid, RoomPartition Rooms, ShipTemplate Tmpl) Reload(
        string json, (GameEnv Env, DataIndex Index, Catalog Catalog) g, IReadOnlyList<RoomSpecDef> specs)
    {
        var tmpl = Assert.Single(ShipTemplate.ParseFile(json).ToList());
        var grid = ShipGrid.FromTemplate(tmpl, new PartResolver(g.Index), g.Catalog);
        var rooms = RoomBuilder.Build(grid);
        RoomCertifier.CertifyAll(rooms, specs, g.Catalog);
        return (grid, rooms, tmpl);
    }

    /// <summary>Every tile carries the same set of conditions in both grids (same dims, same parts, same coords).</summary>
    private static void AssertSameTiles(ShipGrid a, ShipGrid b)
    {
        Assert.Equal(a.NCols, b.NCols);
        Assert.Equal(a.NRows, b.NRows);
        for (var i = 0; i < a.TileCount; i++)
        {
            var ca = a.CondsAt(i)?.Keys.OrderBy(k => k, System.StringComparer.Ordinal) ?? Enumerable.Empty<string>();
            var cb = b.CondsAt(i)?.Keys.OrderBy(k => k, System.StringComparer.Ordinal) ?? Enumerable.Empty<string>();
            Assert.True(ca.SequenceEqual(cb),
                $"tile #{i} [{a.Col(i)},{a.Row(i)}] conds differ: [{string.Join("+", a.CondsAt(i)?.Keys ?? [])}] vs [{string.Join("+", b.CondsAt(i)?.Keys ?? [])}]");
        }
    }

    [SkippableFact]
    public void Export_roundtrips_tiles_rooms_and_rating()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildDooredHull(g.Catalog);

        // the design as Ostraplan sees it
        var grid0 = ShipGrid.FromDocument(doc, g.Catalog);
        var rooms0 = RoomBuilder.Build(grid0);
        RoomCertifier.CertifyAll(rooms0, specs, g.Catalog);

        var (ship, rating, _) = ShipExport.Build(doc, g.Catalog, specs, "Roundtrip Test");
        var (grid1, rooms1, tmpl) = Reload(ShipExport.Serialize(ship), g, specs);

        // 1. parts + coordinates survive the reverse-mapping exactly
        AssertSameTiles(grid0, grid1);
        Assert.Equal(doc.Placements.Count, ship.AItems.Length);
        Assert.All(ship.AItems, it => Assert.False(string.IsNullOrEmpty(it.StrID)));
        Assert.Equal(ship.AItems.Length, ship.AItems.Select(i => i.StrID).Distinct().Count());   // fresh, unique

        // 2. the game's recompute of our baked rooms matches the bake (no visible rating change on load)
        Assert.Null(RoomParity.Compare(grid1, rooms1, tmpl, out _));
        Assert.Equal(rooms0.Rooms.Count(r => !r.Void), rooms1.Rooms.Count(r => !r.Void));

        // 3. the baked rating equals a fresh recompute from the reloaded grid
        var reRating = Rating.Calculate(grid1, rooms1, g.Catalog);
        Assert.Equal(rating.Display, reRating.Display);
        Assert.Equal(rating.Condition, ship.ARating[1]);
        Assert.Equal(rating.RoomCount, ship.ARating[2]);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Export_roundtrips_a_rotated_multitile_fixture(int rot)
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        if (!g.Catalog.ByDefName.ContainsKey("ItmBed01Off")) return;   // a 3×5 non-sheet fixture
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var doc = new ShipDocument(g.Catalog);
        Place(doc, "ItmBed01Off", 4, 4, rot);   // one rotated multi-tile part is enough to exercise centre+rotation inversion

        var grid0 = ShipGrid.FromDocument(doc, g.Catalog);
        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, specs, $"Bed {rot}");
        var (grid1, _, _) = Reload(ShipExport.Serialize(ship), g, specs);

        AssertSameTiles(grid0, grid1);   // wrong centre/rotation inversion would land the footprint on different tiles
        var item = Assert.Single(ship.AItems);
        Assert.Equal(GridMath.Norm(-rot), (int)item.FRotation);   // fRotation is CCW
    }

    [SkippableFact]
    public void Write_produces_a_mod_folder_and_never_touches_loading_order()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildDooredHull(g.Catalog);

        var dest = Path.Combine(Path.GetTempPath(), "OstraplanExportTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        try
        {
            var opts = new ExportOptions("My Test Ship", "Tester", "", "1.0.0",
                g.Env.InstalledVersion ?? GameEnv.VerifiedGameVersion, dest, "My Test Ship");
            var result = ShipExport.Write(doc, g.Catalog, specs, opts);

            Assert.True(File.Exists(result.ModInfoPath));
            Assert.True(File.Exists(result.ShipJsonPath));
            Assert.EndsWith(Path.Combine("data", "ships", "My Test Ship.json"), result.ShipJsonPath);

            // the ship file parses back as one valid ship with the right grid
            var tmpl = Assert.Single(ShipTemplate.ParseFile(File.ReadAllText(result.ShipJsonPath)).ToList());
            Assert.Equal(ShipGrid.FromDocument(doc, g.Catalog).NCols, tmpl.NCols);

            // mod_info is the game's loader shape — a one-element array (NOT a bare object) carrying the name
            using (var modInfo = JsonDocument.Parse(File.ReadAllText(result.ModInfoPath)))
            {
                Assert.Equal(JsonValueKind.Array, modInfo.RootElement.ValueKind);
                var only = Assert.Single(modInfo.RootElement.EnumerateArray().ToList());
                Assert.Equal("My Test Ship", only.GetProperty("strName").GetString());
            }

            // the Law: registration is single-owner, so NO loading_order.json is written
            Assert.Empty(Directory.EnumerateFiles(dest, "loading_order.json", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [SkippableFact]
    public void Write_with_delivery_emits_loot_lifeevents_and_interactions_preserving_core_pools()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildDooredHull(g.Catalog);

        var dest = Path.Combine(Path.GetTempPath(), "OstraplanDeliveryTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        try
        {
            var delivery = new ShipDelivery(
                ["RandomShipBrokerOKLG"], 0.05, ["RandomShipBrokerSpecialOffer"],
                true, 0.16, "OKLG", 500000, "My Test Ship.", "A listing you found.");
            // a DISTINCT in-game name from the ship name: the loot pools must reference the strName ("My Test
            // Ship"), never the display publicName ("The Wanderer") — else the broker can't find the template.
            var opts = new ExportOptions("My Test Ship", "Tester", "", "1.0.0",
                g.Env.InstalledVersion ?? GameEnv.VerifiedGameVersion, dest, "The Wanderer",
                Delivery: delivery);
            var result = ShipExport.Write(doc, g.Catalog, specs, opts, g.Index);

            Assert.True(result.TouchedLootPools);

            // the ship file keeps its strName (the override/reference key) and the custom display name
            var shipTmpl = Assert.Single(ShipTemplate.ParseFile(File.ReadAllText(result.ShipJsonPath)).ToList());
            Assert.Equal("My Test Ship", shipTmpl.Name);
            Assert.Equal("The Wanderer", shipTmpl.PublicName);

            var lootPath = Path.Combine(result.ModDir, "data", "loot", "loot.json");
            var lifePath = Path.Combine(result.ModDir, "data", "lifeevents", "lifeevents.json");
            var interPath = Path.Combine(result.ModDir, "data", "interactions", "interactions.json");
            Assert.True(File.Exists(lootPath));
            Assert.True(File.Exists(lifePath));
            Assert.True(File.Exists(interPath));

            using var loot = JsonDocument.Parse(File.ReadAllText(lootPath));
            var pools = loot.RootElement.EnumerateArray().ToList();

            // the OKLG broker override keeps every ship already in the EFFECTIVE (mod-resolved) pool AND adds ours,
            // still a single-element aCOs. Comparing against the effective pool is environment-independent — the
            // user may have other ship mods loaded (e.g. Ithalan's), which is exactly what this must preserve.
            var effective = LootList.Parse(g.Index.Type("loot")["RandomShipBrokerOKLG"].El.GetProperty("aCOs")[0].GetString()!)
                .Select(e => e.Name).ToList();
            var oklg = pools.Single(p => p.GetProperty("strName").GetString() == "RandomShipBrokerOKLG");
            Assert.Equal(1, oklg.GetProperty("aCOs").GetArrayLength());
            var oklgNames = LootList.Parse(oklg.GetProperty("aCOs")[0].GetString()!).Select(e => e.Name).ToList();
            foreach (var name in effective) Assert.Contains(name, oklgNames);   // every existing ship preserved
            Assert.Contains("My Test Ship", oklgNames);                          // ours added

            // the Special Offer is a straight overwrite to our ship
            var special = pools.Single(p => p.GetProperty("strName").GetString() == "RandomShipBrokerSpecialOffer");
            Assert.Equal("My Test Ship=1x1", special.GetProperty("aCOs")[0].GetString());

            // the starting-ship reward loot names our ship template
            var reward = pools.Single(p => p.GetProperty("strName").GetString() == "CGEncMyTestShipReward");
            Assert.Equal("My Test Ship=1x1", reward.GetProperty("aCOs")[0].GetString());

            // lifeevents + interactions each carry the Intro/Take pair
            using var life = JsonDocument.Parse(File.ReadAllText(lifePath));
            var lifeNames = life.RootElement.EnumerateArray().Select(o => o.GetProperty("strName").GetString()).ToList();
            Assert.Contains("CGEncMyTestShipIntro", lifeNames);
            Assert.Contains("CGEncMyTestShipTake", lifeNames);

            using var inter = JsonDocument.Parse(File.ReadAllText(interPath));
            var interNames = inter.RootElement.EnumerateArray().Select(o => o.GetProperty("strName").GetString()).ToList();
            Assert.Contains("CGEncMyTestShipIntro", interNames);
            Assert.Contains("CGEncMyTestShipTake", interNames);

            // still no loading_order.json — registration stays single-owner
            Assert.Empty(Directory.EnumerateFiles(dest, "loading_order.json", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [SkippableFact]
    public void Write_replacing_a_ship_keys_the_export_to_the_target_and_keeps_vanilla_naming()
    {
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildDooredHull(g.Catalog);

        var dest = Path.Combine(Path.GetTempPath(), "OstraplanReplaceTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        try
        {
            // ship named "Sundancer", replacing the vanilla "Sundancer", no custom mod name or in-game name
            var opts = new ExportOptions("Sundancer", "Tester", "", "1.0.0",
                g.Env.InstalledVersion ?? GameEnv.VerifiedGameVersion, dest, "",
                ReplaceTarget: "Sundancer");
            var result = ShipExport.Write(doc, g.Catalog, specs, opts, g.Index);

            // the mod folder/file default to a distinct "{target} - Replaced via Ostraplan" name, not the ship's…
            Assert.EndsWith(Path.Combine("data", "ships", "Sundancer - Replaced via Ostraplan.json"), result.ShipJsonPath);
            Assert.EndsWith("Sundancer - Replaced via Ostraplan", result.ModDir);
            using (var modInfo = JsonDocument.Parse(File.ReadAllText(result.ModInfoPath)))
                Assert.Equal("Sundancer - Replaced via Ostraplan", modInfo.RootElement[0].GetProperty("strName").GetString());
            // …but the ship's strName is the REPLACE TARGET (so the game overrides the vanilla Sundancer), and with
            // no custom name it keeps the vanilla varied-naming sentinel rather than a fixed publicName
            var tmpl = Assert.Single(ShipTemplate.ParseFile(File.ReadAllText(result.ShipJsonPath)).ToList());
            Assert.Equal("Sundancer", tmpl.Name);
            Assert.Equal("$TEMPLATE", tmpl.PublicName);

            // a custom mod name is honoured over the default
            var custom = ShipExport.Write(doc, g.Catalog, specs,
                opts with { ModName = "Sundancer MkII" }, g.Index);
            Assert.EndsWith("Sundancer MkII", custom.ModDir);
            Assert.Equal("Sundancer", Assert.Single(ShipTemplate.ParseFile(File.ReadAllText(custom.ShipJsonPath)).ToList()).Name);
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [SkippableFact]
    public void SerializeModInfo_is_a_one_element_array_carrying_the_name()
    {
        // Regression: the game's mod loader (DataHandler.JsonToData) reads mod_info.json as an ARRAY
        // of objects, like every core data file. Ostraplan used to write a bare object, which parses
        // to an empty collection — the game then fell back to a default name and logged a spurious
        // "Missing mod_info.json" warning + "Error loading file". No game install needed for this one.
        var json = ShipExport.SerializeModInfo(new ModInfo { StrName = "Classic Parasite" });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var only = Assert.Single(doc.RootElement.EnumerateArray().ToList());
        Assert.Equal("Classic Parasite", only.GetProperty("strName").GetString());
    }

    [SkippableFact]
    public void Export_installs_the_standard_nav_module_set_into_a_console()
    {
        // a nav console is only a frame — the interface is built from module items contained inside it, which
        // Ostraplan doesn't model as placed parts. The export must add the standard set or the console spawns
        // empty (the in-game symptom). Modules are parented to the console and sit at its coordinates.
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        if (g.Catalog.Lookup("ItmStationNav") is not { } consoleDef || !NavConsole.IsConsole(consoleDef)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        var doc = new ShipDocument(g.Catalog);
        Place(doc, "ItmStationNav", 4, 4);

        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, specs, "Nav Test");

        var console = Assert.Single(ship.AItems, i => i.StrName == "ItmStationNav");
        var modules = ship.AItems.Where(i => i.StrParentID == console.StrID).ToList();
        Assert.Equal(NavConsole.StandardModules.OrderBy(x => x, System.StringComparer.Ordinal),
                     modules.Select(m => m.StrName).OrderBy(x => x, System.StringComparer.Ordinal));   // the full set, parented
        Assert.All(modules, m => { Assert.Equal(console.FX, m.FX); Assert.Equal(console.FY, m.FY); Assert.Equal(0.0, m.FRotation); });
        Assert.Equal(ship.AItems.Length, ship.AItems.Select(i => i.StrID).Distinct().Count());   // fresh, unique ids throughout
    }

    [SkippableFact]
    public void Export_adds_no_child_items_when_there_is_no_console()
    {
        // guard against over-eager injection: a design with no nav console stays 1:1 with its placements.
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var doc = BuildDooredHull(g.Catalog);   // walls/floor/door — no nav console

        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, specs, "No Nav");

        Assert.Equal(doc.Placements.Count, ship.AItems.Length);
        Assert.All(ship.AItems, i => Assert.Null(i.StrParentID));
    }

    [SkippableFact]
    public void Export_carries_a_containers_cargo_as_parented_items()
    {
        // a container's contents travel into the exported ship as pristine parented items at the container's coords.
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var containerDef = g.Catalog.Parts.FirstOrDefault(p => p.ContainerGrid is not null
            && ContainerFilter.AcceptedBy(g.Catalog, p).Any(i => i.SpriteAbs is not null));
        if (containerDef is null) return;
        var itemDef = ContainerFilter.AcceptedBy(g.Catalog, containerDef).First(i => i.SpriteAbs is not null);

        var doc = new ShipDocument(g.Catalog);
        var p = new Placement { DefName = containerDef.DefName, X = 4, Y = 4 };
        new PlaceCommand(p).Do(doc);
        if (CargoEdit.Add(p.Cargo, null, containerDef.ContainerGrid!.Value, itemDef, 1) is not { } added) return;
        new SetCargoCommand(p, p.Cargo, added).Do(doc);

        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, specs, "Cargo Ship");

        var container = Assert.Single(ship.AItems, i => i.StrName == containerDef.DefName);
        var cargo = Assert.Single(ship.AItems, i => i.StrParentID == container.StrID);
        Assert.Equal(itemDef.DefName, cargo.StrName);
        Assert.Equal(container.FX, cargo.FX);   // contained items sit at the container's coordinates
        Assert.Equal(container.FY, cargo.FY);
        Assert.Equal(ship.AItems.Length, ship.AItems.Select(i => i.StrID).Distinct().Count());   // fresh, unique ids
        Assert.NotNull(cargo.ACondOverrides);           // the marker that makes a template spawn KEEP the cargo
        Assert.NotEmpty(cargo.ACondOverrides!);
        Assert.True(cargo.BForceLoad);                  // keeps its strID so it links to its baked CO
        Assert.Null(container.ACondOverrides);          // a top-level part is kept unconditionally — no marker needed
        Assert.Null(container.BForceLoad);
        // authored cargo carries save-style CO data (a template keeps contained items only if it does)
        Assert.NotNull(ship.ACOs);
        var cargoCo = Assert.Single(ship.ACOs!, c => c.StrID == cargo.StrID);
        Assert.Equal(cargo.StrName, cargoCo.StrCODef);
        Assert.DoesNotContain(ship.ACOs!, c => c.StrID == container.StrID);   // the top-level container needs none
    }

    [SkippableFact]
    public void Export_carries_a_stack_as_a_lead_plus_members()
    {
        // a stack exports the way the game stores it: a lead item parented to the container, its copies parented to the lead.
        var g = TestData.RequireGame();
        if (!Ready(g)) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);
        var containerDef = g.Catalog.Parts.FirstOrDefault(p => p.ContainerGrid is not null
            && ContainerFilter.AcceptedBy(g.Catalog, p).Any(i => i.SpriteAbs is not null && i.StackLimit >= 2));
        if (containerDef is null) return;
        var stackable = ContainerFilter.AcceptedBy(g.Catalog, containerDef).First(i => i.SpriteAbs is not null && i.StackLimit >= 2);

        var doc = new ShipDocument(g.Catalog);
        var p = new Placement { DefName = containerDef.DefName, X = 4, Y = 4 };
        new PlaceCommand(p).Do(doc);
        if (CargoEdit.Add(p.Cargo, null, containerDef.ContainerGrid!.Value, stackable, 2) is not { } added) return;
        new SetCargoCommand(p, p.Cargo, added).Do(doc);

        var (ship, _, _) = ShipExport.Build(doc, g.Catalog, specs, "Stack Ship");

        var container = Assert.Single(ship.AItems, i => i.StrName == containerDef.DefName);
        var stackItems = ship.AItems.Where(i => i.StrName == stackable.DefName).ToList();
        Assert.Equal(2, stackItems.Count);                                     // lead + one member
        var lead = Assert.Single(stackItems, i => i.StrParentID == container.StrID);
        var member = Assert.Single(stackItems, i => i.StrParentID == lead.StrID);   // the member hangs off the lead

        // both keep their strID (bForceLoad) so the head's CO can list the member, and the head's CO carries the
        // aStack the game reads to rebuild the ×2 stack at the right count.
        Assert.True(lead.BForceLoad);
        Assert.True(member.BForceLoad);
        Assert.NotNull(ship.ACOs);
        var leadCo = Assert.Single(ship.ACOs!, c => c.StrID == lead.StrID);
        Assert.NotNull(leadCo.AStack);
        Assert.Equal(new[] { member.StrID }, leadCo.AStack);                   // exactly the member, by id
        var memberCo = Assert.Single(ship.ACOs!, c => c.StrID == member.StrID);
        Assert.Null(memberCo.AStack);                                          // a member is not itself a stack head
    }
}

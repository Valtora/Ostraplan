using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>Assertions against the live install; every test no-ops on a machine without the game.</summary>
public class GameDataTests
{
    [SkippableFact]
    public void Install_located_and_catalog_builds()
    {
        var g = TestData.RequireGame();
        Assert.NotNull(g.Env.InstalledVersion);
        // core alone has 331 unique menu install defs (154 of them HULL); mods only add
        Assert.True(g.Catalog.Parts.Count >= 300, $"only {g.Catalog.Parts.Count} buildable parts found");
        Assert.True(g.Catalog.Parts.Count(p => p.Category == "HULL") >= 140, "HULL tab suspiciously small");
        foreach (var category in Catalog.Categories)
            Assert.Contains(g.Catalog.Parts, p => p.Category == category);

        // new-document seeding and the problems scan depend on the buildable docking port
        Assert.True(g.Catalog.ByDefName.TryGetValue("ItmDockSys03Closed", out var dock), "buildable docksys missing");
        Assert.True(ProblemScan.IsDocksys(dock, g.Catalog), "docksys part not recognized (ALL-reqs trigger match)");
        Assert.True(g.Catalog.ByDefName.TryGetValue("ItmFloorGrate01", out var floorPart)
                    && !ProblemScan.IsDocksys(floorPart, g.Catalog),
            "floor grate wrongly recognized as a docking port (the Any-match bug)");
        Assert.True(dock!.MapPoints.ContainsKey("DockA") && dock.MapPoints.ContainsKey("DockB"),
            "DockA/DockB map points missing from the docksys condowner");

        // seeded pose: rot 0 at origin -> mating face along doc y = 0, outward up
        Assert.True(ProblemScan.TryGetFace(dock, new Placement { DefName = dock.DefName, X = 0, Y = 0 },
            out var axisY, out var dir, out var face));
        Assert.True(axisY);
        Assert.Equal(-1, dir);
        Assert.Equal(0, face, 3);

        // the Primary Airlock: resolvable for documents, absent from the palette, locked in docs
        Assert.True(g.Catalog.ByDefName.TryGetValue(Catalog.PrimaryDocksysDef, out var primary), "primary airlock unresolved");
        Assert.DoesNotContain(g.Catalog.Parts, p => p.DefName == Catalog.PrimaryDocksysDef);
        Assert.Contains("Primary", primary!.Friendly);
        Assert.True(ProblemScan.IsDocksys(primary, g.Catalog));
        Assert.True(primary.MapPoints.ContainsKey("DockA") && primary.MapPoints.ContainsKey("DockB"));
        Assert.Equal(7, primary.Item.Width);
        var doc = new ShipDocument(g.Catalog);
        Assert.True(doc.IsLocked(new Placement { DefName = Catalog.PrimaryDocksysDef }));
        Assert.False(doc.IsLocked(new Placement { DefName = "ItmDockSys03Closed" }));
    }

    /// <summary>
    /// Issue #11. The overhead ceiling lights (<c>ItmLitCeiling1x1*</c>) are the only buildable parts whose
    /// <c>aSocketReqs</c> demand a power conduit (<c>IsPowerConduit</c>) on an adjacent tile. The game's
    /// interactive builder enforces that, but dev-authored / spawned ships hang them freely — so a planner
    /// (which produces spawn-placed ships) treats the missing conduit as a soft advisory, not a hard block:
    /// the light PLACES, flagged, and the advisory clears once a real conduit is put on its anchor tile.
    /// </summary>
    [SkippableFact]
    public void Overhead_light_places_with_a_conduit_advisory_and_clears_when_wired()
    {
        var g = TestData.RequireGame();

        var light = g.Catalog.Parts.FirstOrDefault(p =>
            p.Item.SocketReqs.SelectMany(g.Catalog.LootConds).Contains("IsPowerConduit"));
        Skip.If(light is null, "no conduit-anchored light in this install");
        Assert.Contains("Overhead Light", light!.Friendly);

        var doc = new ShipDocument(g.Catalog);

        // No conduit: the light places (this is the issue-#11 fix — it used to be hard-blocked) but is flagged.
        var bare = CheckFit.Check(doc, light, 0, 0, 0, includeEnvelope: false);
        Assert.True(bare.Ok);
        Assert.Equal("no power conduit adjacent", bare.Advisory);
        Assert.Empty(bare.FailedCells);
        var anchor = Assert.Single(bare.AdvisoryCells!);   // the one ring cell wanting a conduit

        // Put a real power conduit on that anchor tile; the advisory clears — fully-legal, unflagged.
        var conduit = g.Catalog.Parts.First(p =>
            p.Item.SocketAdds.SelectMany(g.Catalog.LootConds).Contains("IsPowerConduit"));
        new PlaceCommand(new Placement { DefName = conduit.DefName, X = anchor.X, Y = anchor.Y }).Do(doc);

        var wired = CheckFit.Check(doc, light, 0, 0, 0, includeEnvelope: false);
        Assert.True(wired.Ok);
        Assert.Null(wired.Advisory);
    }

    [SkippableFact]
    public void Parts_expose_raw_mass_and_health_figures()
    {
        // The inspector's STATS block reads these straight from PartDef.StartingCondValues (the same source as
        // Base Value). Mass is kilograms; Health is the durability pool StatDamageMax the game never shows.
        var g = TestData.RequireGame();
        var withStats = g.Catalog.Parts.Where(p =>
            p.StartingCondValues.ContainsKey("StatMass") && p.StartingCondValues.ContainsKey("StatDamageMax")).ToList();
        Assert.True(withStats.Count >= 200, $"only {withStats.Count} parts carry mass + health");

        foreach (var p in withStats)
        {
            Assert.True(p.StartingCondValues["StatMass"] > 0, $"{p.DefName} has non-positive mass");
            Assert.True(p.StartingCondValues["StatDamageMax"] > 0, $"{p.DefName} has non-positive health");
        }
    }

    [SkippableFact]
    public void Door_open_and_closed_states_both_resolve_and_toggle()
    {
        var g = TestData.RequireGame();
        var cat = g.Catalog;
        if (!cat.ByDefName.ContainsKey("ItmDoor01Open")) return;

        // the buildable door is the OPEN form; the CLOSED peer is registered
        // resolvable-but-not-buildable (like the primary airlock) so a placement can flip to it
        Assert.Contains(cat.Parts, p => p.DefName == "ItmDoor01Open");           // in the palette
        Assert.True(cat.ByDefName.ContainsKey("ItmDoor01Closed"), "closed door not resolvable");
        Assert.DoesNotContain(cat.Parts, p => p.DefName == "ItmDoor01Closed");   // NOT in the palette

        // toggles both ways; skinned palette doors (e.g. the blast door) map to their own closed skin
        Assert.Equal("ItmDoor01Closed", cat.DoorToggle("ItmDoor01Open"));
        Assert.Equal("ItmDoor01Open", cat.DoorToggle("ItmDoor01Closed"));
        if (cat.ByDefName.ContainsKey("ItmDoor05Open"))
        {
            Assert.Equal("ItmDoor05Closed", cat.DoorToggle("ItmDoor05Open"));
            Assert.True(cat.ByDefName.ContainsKey("ItmDoor05Closed"), "closed blast-door skin not resolvable");
        }

        // non-doors never toggle
        Assert.Null(cat.DoorToggle("ItmWall1x1"));
        Assert.Null(cat.DoorToggle("ItmFloorGrate01"));

        // geometry is preserved (both 5×1) so a swapped door covers the same tiles, and the
        // closed form still stamps IsWall+IsPortal → identical room topology (the law is state-blind)
        var open = cat.ByDefName["ItmDoor01Open"];
        var closed = cat.ByDefName["ItmDoor01Closed"];
        Assert.Equal((open.Item.Width, open.Item.Height), (closed.Item.Width, closed.Item.Height));
        var openConds = open.Item.SocketAdds.SelectMany(cat.LootConds).ToHashSet();
        var closedConds = closed.Item.SocketAdds.SelectMany(cat.LootConds).ToHashSet();
        foreach (var c in new[] { "IsWall", "IsPortal" })
        {
            Assert.Contains(c, openConds);
            Assert.Contains(c, closedConds);
        }
    }

    [SkippableFact]
    public void Known_parts_have_game_exact_footprints_and_sprites()
    {
        var g = TestData.RequireGame();

        Assert.True(g.Catalog.ByDefName.TryGetValue("ItmWall1x1", out var wall), "wall not in palette");
        Assert.True(wall!.Item.HasSpriteSheet);
        Assert.Equal("TIsWall", wall.Item.CtSpriteSheet);
        Assert.Equal((1, 1), (wall.Item.Width, wall.Item.Height));
        Assert.True(wall.SpriteAbs is not null && File.Exists(wall.SpriteAbs), "wall sheet png missing");

        Assert.True(g.Catalog.ByDefName.TryGetValue("ItmFloorGrate01", out var floor), "floor grate not in palette");
        Assert.Equal((1, 1), (floor!.Item.Width, floor.Item.Height));
        Assert.Equal("HULL", floor.Category);
        Assert.True(floor.SpriteAbs is not null && File.Exists(floor.SpriteAbs));

        // footprints straight from the items data (independent of palette membership)
        var door = ItemDef.Parse(g.Index.Type("items")["ItmDoor01Closed"].El);
        Assert.Equal((5, 1), (door.Width, door.Height));
        var bed = ItemDef.Parse(g.Index.Type("items")["ItmBed01"].El);
        Assert.Equal(3, bed.Width);
        Assert.Equal(5, bed.Height);
    }

    [SkippableFact]
    public void Wall_socket_loot_expands_to_IsWall_and_trigger_reads_it()
    {
        var g = TestData.RequireGame();
        Assert.True(g.Catalog.Loots.TryGetValue("TILWallAdds", out var loot), "TILWallAdds loot missing");
        Assert.Contains("IsWall", loot!.Conds);
        Assert.True(g.Catalog.Triggers.TryGetValue("TIsWall", out var ct), "TIsWall trigger missing");
        Assert.Contains("IsWall", ct!.Reqs);
    }

    [SkippableFact]
    public void Wall_autotiling_sees_neighbors_including_door_frames()
    {
        var g = TestData.RequireGame();
        var doc = new ShipDocument(g.Catalog);
        void Place(string def, int x, int y) =>
            new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);

        Place("ItmWall1x1", 0, 0);
        Place("ItmWall1x1", 1, 0);
        Place("ItmWall1x1", 0, 1);
        // corner wall at (0,0): east + south neighbors -> mask E|S = 3
        Assert.Equal(3, Autotile.MaskAt(doc.Conds, "TIsWall", 0, 0));

        if (g.Catalog.ByDefName.ContainsKey("ItmDoor01Open"))
        {
            Place("ItmDoor01Open", 2, 5);   // covers (2..6, 5); its end cells add wall conds
            Place("ItmWall1x1", 1, 5);
            Assert.Equal(2, Autotile.MaskAt(doc.Conds, "TIsWall", 1, 5));   // east only
        }
    }

    [SkippableFact]
    public void Real_wall_is_free_standing_but_will_not_stack()
    {
        var g = TestData.RequireGame();
        var wall = g.Catalog.ByDefName["ItmWall1x1"];
        var doc = new ShipDocument(g.Catalog);

        Assert.True(CheckFit.Check(doc, wall, 8, 8, 0, includeEnvelope: false).Ok);   // no reqs -> free-standing
        new PlaceCommand(new Placement { DefName = "ItmWall1x1", X = 8, Y = 8 }).Do(doc);

        var stacked = CheckFit.Check(doc, wall, 8, 8, 0, includeEnvelope: false);     // onto its own obstruction
        Assert.False(stacked.Ok);
        Assert.Equal("tile is already occupied", stacked.Reason);
        Assert.Contains((8, 8), stacked.FailedCells);
    }

    [SkippableFact]
    public void Real_bed_requires_a_sealed_floor_beneath_and_a_wall_at_its_head()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.TryGetValue("ItmBed01Off", out var bed)) return;
        if (!g.Catalog.ByDefName.ContainsKey("ItmFloorGrate01") || !g.Catalog.ByDefName.ContainsKey("ItmWall1x1")) return;

        var doc = new ShipDocument(g.Catalog);
        void Place(string def, int x, int y) => new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);

        // ItmBed01Off is 3x5: reqs are TILFloor across the footprint + TILWall down the right (head) border.
        // bare space -> the floor requirement can't be met
        var bare = CheckFit.Check(doc, bed, 20, 20, 0, includeEnvelope: false);
        Assert.False(bare.Ok);
        Assert.Equal("needs a sealed floor beneath", bare.Reason);

        // sealed floor (grate adds IsFloor+IsFloorSealed) under the whole footprint, but no headboard wall yet
        for (var y = 20; y < 25; y++)
            for (var x = 20; x < 23; x++)
                Place("ItmFloorGrate01", x, y);
        var noWall = CheckFit.Check(doc, bed, 20, 20, 0, includeEnvelope: false);
        Assert.False(noWall.Ok);
        Assert.Equal("needs a wall alongside", noWall.Reason);

        // wall column at the bed's right edge (x = 23) -> the whole ring is now satisfied
        for (var y = 20; y < 25; y++) Place("ItmWall1x1", 23, y);
        Assert.True(CheckFit.Check(doc, bed, 20, 20, 0, includeEnvelope: false).Ok);
    }

    /// <summary>
    /// Issue #6. A cargo pod (ItmCargoPod01, 3×6) requires a wall at its top-middle tile and contributes a
    /// wall at its own bottom-middle tile — so the "cargo train" is pods overlapping by ONE row: the lower
    /// pod's top edge sits on the upper pod's bottom wall. That single-row overlap is the only CheckFit-legal
    /// stack, and the planner's paint-dedup guard used to reject any same-def overlap and so refused it.
    /// </summary>
    [SkippableFact]
    public void Cargo_pods_stack_by_overlapping_one_row()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.TryGetValue("ItmCargoPod01", out var pod)) return;
        Assert.Equal((3, 6), (pod.Item.Width, pod.Item.Height));

        var doc = new ShipDocument(g.Catalog);
        void Place(string def, int x, int y) => new PlaceCommand(new Placement { DefName = def, X = x, Y = y }).Do(doc);

        // a lone pod in open space fails: nothing provides the wall its top edge needs
        var lone = CheckFit.Check(doc, pod, 10, 10, 0, includeEnvelope: false);
        Assert.False(lone.Ok);
        Assert.Equal("needs a wall alongside", lone.Reason);

        // a wall at the pod's top-middle tile (11,10) lets the first pod attach
        Place("ItmWall1x1", 11, 10);
        Assert.True(CheckFit.Check(doc, pod, 10, 10, 0, includeEnvelope: false).Ok);
        Place("ItmCargoPod01", 10, 10);   // now contributes IsWall at its bottom-middle tile (11,15)

        // the second pod stacks by overlapping the first's bottom row (y=15), where that wall now sits -> legal
        Assert.True(CheckFit.Check(doc, pod, 10, 15, 0, includeEnvelope: false).Ok);

        // a flush pod one row lower (y=16) has no wall at its top edge -> refused, exactly as the game refuses it
        var flush = CheckFit.Check(doc, pod, 10, 16, 0, includeEnvelope: false);
        Assert.False(flush.Ok);
        Assert.Equal("needs a wall alongside", flush.Reason);
    }

    [SkippableFact]
    public void Real_envelope_makes_construction_beyond_the_airlock_unplaceable()
    {
        var g = TestData.RequireGame();
        if (!g.Catalog.ByDefName.TryGetValue("ItmDockSys03Closed", out var dock)) return;
        var wall = g.Catalog.ByDefName["ItmWall1x1"];
        var doc = new ShipDocument(g.Catalog);
        new PlaceCommand(new Placement { DefName = "ItmDockSys03Closed", X = 0, Y = 0 }).Do(doc);   // face at doc y = 0, outward up

        var beyond = CheckFit.Check(doc, wall, 2, -2, 0, includeEnvelope: true);
        Assert.False(beyond.Ok);
        Assert.Equal("beyond the airlock's mating face", beyond.Reason);

        Assert.True(CheckFit.Check(doc, wall, 2, 5, 0, includeEnvelope: true).Ok);   // inside the hull
    }

    /// <summary>
    /// A name that is BOTH a condowner and a cooverlay resolves through the condowner: the game reads the
    /// overlay only when no condowner of that name exists (<c>DataHandler.GetCondOwner</c>). Core ships eight
    /// such names — the grey Rakow "Reserve" bins, which carry a full condowner alongside a legacy cooverlay
    /// pointing at the "01" sibling. Reading the overlay first gave the 2x104 the 2x101's item def, whose
    /// socket mask demands <c>TILFloor</c> under the bin; the grey bin hangs on a bulkhead with nothing
    /// beneath it, so every imported one went "needs a sealed floor beneath" the moment it was touched.
    /// </summary>
    [SkippableFact]
    public void A_condowner_wins_over_a_cooverlay_of_the_same_name()
    {
        var g = TestData.RequireGame();
        var resolver = new PartResolver(g.Index);

        var grey = resolver.Resolve("ItmStorageBin2x104");
        var vanilla = resolver.Resolve("ItmStorageBin2x101");
        Skip.If(grey is null || vanilla is null, "this install lacks the bulkhead bins");

        Assert.Equal("ItmStorageBin2x104", grey!.Item.Name);
        Assert.DoesNotContain("TILFloor", grey.Item.SocketReqs);          // hangs on a wall, needs no deck
        Assert.Contains("TILWall", grey.Item.SocketReqs);
        Assert.Contains("TILFloor", vanilla!.Item.SocketReqs);            // the 01 genuinely does

        // …and the same through the catalog, which is what a document actually places against.
        var part = g.Catalog.Lookup("ItmStorageBin2x104");
        Assert.NotNull(part);
        Assert.Equal("ItmStorageBin2x104", part!.Item.Name);
        Assert.Contains("IsGray", part.StartingConds);                    // the "Reserve" bin's own conds…
        Assert.Contains("IsTough", part.StartingConds);
        Assert.Equal(1482.0, part.StartingCondValues["StatBasePrice"]);   // …not the 01's 882
        Assert.Equal(18.0, part.StartingCondValues["StatMass"]);

        // The corner bin is the same story, and the mask difference is a wall vs a floor on one cell.
        var corner = resolver.Resolve("ItmStorageBin2x2C04");
        Assert.Equal("ItmStorageBin2x2C04", corner?.Item.Name);
        Assert.DoesNotContain("TILFloor", corner!.Item.SocketReqs);
    }

    /// <summary>
    /// The guard behind the test above: no name may resolve through a cooverlay while a condowner of that
    /// name exists. Written against the eight "04" bins in core 1.0.0.7; if a game patch (or a mod) adds
    /// more, they are covered automatically, and if core ever drops the duplicate overlays this still holds.
    /// </summary>
    [SkippableFact]
    public void No_cooverlay_shadows_a_real_condowner()
    {
        var g = TestData.RequireGame();
        var owners = g.Index.Type("condowners");
        var resolver = new PartResolver(g.Index);

        var shadowed = new List<string>();
        foreach (var (name, _) in g.Index.Type("cooverlays"))
        {
            if (!owners.TryGetValue(name, out var own)) continue;   // overlay-only: the fallback is correct
            var expected = Json.Str(own.El, "strItemDef") ?? name;
            var actual = resolver.Resolve(name)?.Item.Name;
            if (actual is not null && !string.Equals(actual, expected, StringComparison.Ordinal))
                shadowed.Add($"{name}: resolved to item '{actual}', condowner says '{expected}'");
        }

        Assert.True(shadowed.Count == 0, string.Join("; ", shadowed));
    }

    [SkippableFact]
    public void Large_tank_footprint_is_the_7x7_socket_grid_not_the_3x3_sprite()
    {
        var g = TestData.RequireGame();
        var tank = ItemDef.Parse(g.Index.Type("items")["ItmCanisterLH02"].El);

        // The footprint IS 7x7 (nCols=7, 49 adds): the game requires a 7x7 sealed-floor
        // area to place it, so the placement/CheckFit footprint must stay 7x7 or Ostraplan
        // would allow tanks the game refuses. The visible tank is only the center 3x3
        // (TIL2DeckAdds); the outer ring is TILSubfloorAdds — abstracted storage under the floor.
        Assert.Equal((7, 7), (tank.Width, tank.Height));
        Assert.Equal(49, tank.SocketAdds.Length);
        Assert.Contains("TILSubfloorAdds", tank.SocketAdds);
        Assert.Contains("TIL2DeckAdds", tank.SocketAdds);
        Assert.All(tank.SocketReqs.Where(r => r != "Blank"), r => Assert.Equal("TILFloor", r));

        // the ghost distinguishes the two footprints: the outer TILSubfloorAdds ring is
        // under-floor storage, the center TIL2DeckAdds body is above-floor (an obstruction)
        Assert.True(g.Catalog.IsUnderFloorLoot("TILSubfloorAdds"));
        Assert.False(g.Catalog.IsUnderFloorLoot("TIL2DeckAdds"));
    }

    /// <summary>
    /// Issue #29. <c>ItmTowingBrace01</c>'s <c>aSocketReqs</c> carries exactly one non-Blank cell
    /// (<c>TILDockSys</c>), so the requirement is a single point and <b>every</b> brace rotation satisfies it at
    /// some offset against a port. Three of the four leave the brace across or against the airlock, and on a
    /// Secondary nothing bounds them, so the ghost reads green on a pose that parks the brace ahead of the
    /// mating face. The game accepts those poses too (this is not a placement-offset defect), so the outcome is
    /// the dismissible <see cref="ProblemScan.BlockedPortAlertKey"/> warning rather than a refusal.
    /// </summary>
    [SkippableFact]
    public void Towing_brace_rotated_against_a_secondary_lands_ahead_of_its_face()
    {
        var g = TestData.RequireGame();
        var brace = g.Catalog.ByDefName["ItmTowingBrace01"];

        // one TILDockSys cell in the whole ring: that is what makes all four rotations placeable
        Assert.Single(brace.Item.SocketReqs, r => r != "Blank");
        Assert.Contains("TILDockSys", brace.Item.SocketReqs);

        // the reporter's design: the seeded Primary at the origin, plus a Secondary facing west
        static ShipDocument Design(Catalog cat, params Placement[] extra)
        {
            var d = new ShipDocument(cat);
            new PlaceCommand(new Placement { DefName = Catalog.PrimaryDocksysDef, X = 0, Y = 0 }).Do(d);
            new PlaceCommand(new Placement { DefName = "ItmDockSys03Closed", X = 0, Y = 40, Rot = 270 }).Do(d);
            foreach (var p in extra) new PlaceCommand(p).Do(d);
            return d;
        }

        // matched rotation is the pose the game's own tugs use (Station Tug: port (1,6) rot270, brace (2,6) rot270)
        var right = new Placement { DefName = brace.DefName, X = 1, Y = 40, Rot = 270 };
        Assert.True(CheckFit.Check(Design(g.Catalog), brace, right.X, right.Y, right.Rot).Ok);
        Assert.DoesNotContain(ProblemScan.Scan(Design(g.Catalog, right), g.Catalog),
            p => p.DismissKey == ProblemScan.BlockedPortAlertKey);

        // the reported pose: brace at rot 90 against a rot 270 port, two tiles the wrong side of the face
        var wrong = new Placement { DefName = brace.DefName, X = -2, Y = 40, Rot = 90 };
        Assert.True(CheckFit.Check(Design(g.Catalog), brace, wrong.X, wrong.Y, wrong.Rot).Ok,
            "the game accepts this pose, so Ostraplan must too — the fix is the warning, not a refusal");
        var problem = Assert.Single(ProblemScan.Scan(Design(g.Catalog, wrong), g.Catalog),
            p => p.DismissKey == ProblemScan.BlockedPortAlertKey);
        Assert.Equal(ProblemSeverity.Warning, problem.Severity);
        Assert.Contains("Secondary", problem.Title);
    }
}

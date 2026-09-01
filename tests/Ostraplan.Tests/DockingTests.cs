using System.IO;
using System.Threading;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The docking-legality port (<see cref="DockMating"/>). The synthetic cases pin the rules that are easy to get
/// wrong and impossible to see in a real ship: the Blank halo, the order dependence it causes, deck cargo
/// counting, and <c>IsTypeB</c> taking no part. The game-data cases hold the port to what the shipped ships
/// actually do.
/// </summary>
public class DockingTests
{
    private const string Dock = "Dock";
    private const string DockSecondary = "DockB";
    private const string Wall = "Wall";
    private const string Crate = "Crate";

    // 1x1 throughout. The real airlock is 7x2 with a 7-cell floor plan, which the game-data cases below
    // exercise; here the point is the overlay rule, and a single cell makes the arithmetic readable.
    private static PartDef Part(string name, params string[] conds) => new(
        name, name, "HULL", "core",
        new ItemDef(name, "", false, null, 0, 1, ["L"], [], []),
        null, [], [], conds,
        new Dictionary<string, double>(), new Dictionary<string, (double, double)>());

    private static Catalog Cat() => new()
    {
        Parts = [],
        ByDefName = new[]
        {
            // IsPortal is what makes a port "bulky", so it spreads over its floor plan and carries a mating
            // anchor rather than sitting on one cell with an anchor of (0,0). Every real docksys has it.
            Part(Dock, "IsDockSys", "IsInstalled", "IsPortal"),
            Part(DockSecondary, "IsDockSys", "IsInstalled", "IsPortal", ProblemScan.TypeBCond),
            Part(Wall, "IsInstalled"),
            Part(Crate),   // deck cargo: not installed, and counted all the same
        }.ToDictionary(p => p.DefName),
        Loots = new Dictionary<string, LootDef> { ["L"] = new("L", ["IsX"], []) },
        Triggers = new Dictionary<string, CondTriggerDef>
        {
            [ProblemScan.DocksysTrigger] = new(ProblemScan.DocksysTrigger, ["IsDockSys", "IsInstalled"], [], false),
        },
        Warnings = [],
    };

    /// <summary>
    /// A hull three tiles wide with a port in the middle of its top edge, facing out: document rotation 0 puts
    /// the port's DockB arrow along the game's +y, which is up the canvas. <paramref name="crate"/> drops a
    /// deck item on a document tile.
    /// </summary>
    private static ShipDocument Hull(Catalog cat, string port = Dock, (int X, int Y)? crate = null)
    {
        var doc = Fixtures.Doc(cat,
            Fixtures.P(Wall, 0, 1), Fixtures.P(Wall, 1, 1), Fixtures.P(Wall, 2, 1),
            Fixtures.P(port, 1, 0));
        if (crate is { } c) new PlaceLooseCommand(new LooseObject { DefName = Crate, X = c.X, Y = c.Y }).Do(doc);
        return doc;
    }

    private static DockShip Ship(ShipDocument doc, Catalog cat, string name) =>
        DockShip.FromDocument(doc, cat, DockDefs.For(cat), name);

    private static DockMate Only(DockShip receiver, DockShip incoming) =>
        Assert.Single(DockMating.Cross(receiver, incoming).Pairs);

    [Fact]
    public void Two_clear_hulls_mate_at_their_ports()
    {
        var cat = Cat();
        var mate = Only(Ship(Hull(cat), cat, "receiver"), Ship(Hull(cat), cat, "incoming"));
        Assert.True(mate.Mates);
        Assert.Empty(mate.Blocks);
    }

    [Fact]
    public void An_item_landing_on_a_neighbours_halo_is_dropped_from_the_grid()
    {
        // The single most surprising line in CreateDockingPortGrid: an item whose cell is already occupied is
        // skipped, and a neighbour's Blank halo counts as occupied. So three walls in a row register as two,
        // with a Blank between them, and the whole grid depends on the order aItems is written in. It is not a
        // rounding slip to tidy away — a Blank refuses an incoming hull just as a wall does, so the geometry
        // still holds up, and "correcting" it would change every answer the tool gives.
        var cat = Cat();
        var ship = Ship(Hull(cat), cat, "s");
        var walls = ship.Grid.Cells.Values.Count(c => c.DefName == Wall);
        Assert.Equal(2, walls);
        Assert.Equal(3, Fixtures.Doc(cat,
            Fixtures.P(Wall, 0, 1), Fixtures.P(Wall, 1, 1), Fixtures.P(Wall, 2, 1)).Placements.Count);
    }

    [Fact]
    public void Deck_cargo_in_the_mating_corridor_refuses_a_mate_the_bare_hull_accepts()
    {
        // CreateDockingPortGrid applies no IsInstalled filter, so a crate on the deck occupies a cell and lays
        // its own halo exactly as a wall does. A design that docks fine empty can stop docking once it is
        // loaded, which is the whole reason this check counts deck cargo.
        var cat = Cat();
        var receiver = Ship(Hull(cat), cat, "receiver");
        Assert.True(Only(receiver, Ship(Hull(cat), cat, "incoming")).Mates);
        Assert.False(Only(receiver, Ship(Hull(cat, crate: (1, -2)), cat, "incoming")).Mates);

        // Well clear of the corridor, the same crate costs nothing.
        Assert.True(Only(receiver, Ship(Hull(cat, crate: (4, -2)), cat, "incoming")).Mates);
    }

    [Fact]
    public void A_blocked_cell_names_the_document_tile_it_came_from()
    {
        // The raw collision list is in the RECEIVER's grid coordinates, which are no use for highlighting our
        // own design, so every block carries the incoming cell's document tile as well.
        var cat = Cat();
        var mate = Only(Ship(Hull(cat), cat, "receiver"), Ship(Hull(cat, crate: (1, -2)), cat, "incoming"));
        Assert.False(mate.Mates);
        Assert.All(mate.Blocks, b => Assert.NotNull(b.DocTile));
        Assert.Contains(mate.Blocks, b => b.DocTile == (1, -2));
    }

    [Fact]
    public void IsTypeB_takes_no_part_in_legality()
    {
        // The Primary/Secondary split decides which port bounds construction (ProblemScan.BoundingPort) and
        // nothing else at all. Same geometry, opposite class, same verdict.
        var cat = Cat();
        Assert.True(Only(Ship(Hull(cat), cat, "r"), Ship(Hull(cat), cat, "i")).Mates);
        Assert.True(Only(Ship(Hull(cat, DockSecondary), cat, "r"), Ship(Hull(cat, DockSecondary), cat, "i")).Mates);
        Assert.True(Only(Ship(Hull(cat, DockSecondary), cat, "r"), Ship(Hull(cat), cat, "i")).Mates);

        // And the same holds for a refusal: the crate refuses a Secondary exactly as it refuses a Primary.
        Assert.False(Only(Ship(Hull(cat, DockSecondary), cat, "r"),
            Ship(Hull(cat, DockSecondary, (1, -2)), cat, "i")).Mates);
    }

    [Fact]
    public void A_port_reports_its_class_from_the_condition_not_the_def_name()
    {
        var cat = Cat();
        Assert.Equal("Primary", Assert.Single(Ship(Hull(cat), cat, "s").Ports).Class);
        Assert.Equal("Secondary", Assert.Single(Ship(Hull(cat, DockSecondary), cat, "s").Ports).Class);
    }

    [Fact]
    public void A_design_with_no_port_produces_an_empty_table()
    {
        var cat = Cat();
        var portless = Fixtures.Doc(cat, Fixtures.P(Wall, 0, 0), Fixtures.P(Wall, 2, 0));
        var report = DockMating.Cross(Ship(Hull(cat), cat, "receiver"), Ship(portless, cat, "incoming"));
        Assert.Empty(report.Pairs);
        Assert.False(report.AnyMate);
    }

    [Fact]
    public void The_table_is_the_full_cross_product_of_both_port_lists()
    {
        var cat = Cat();
        var two = Fixtures.Doc(cat,
            Fixtures.P(Wall, 0, 1), Fixtures.P(Wall, 2, 1), Fixtures.P(Wall, 4, 1), Fixtures.P(Wall, 6, 1),
            Fixtures.P(Dock, 1, 0), Fixtures.P(DockSecondary, 5, 0));
        var receiver = Ship(two, cat, "receiver");
        var incoming = Ship(Hull(cat), cat, "incoming");
        Assert.Equal(2, receiver.Ports.Count);
        Assert.Equal(2 * incoming.Ports.Count, DockMating.Cross(receiver, incoming).Pairs.Count);
    }

    [Fact]
    public void The_survey_sweep_captures_nothing_the_ui_thread_owns()
    {
        // Ui.OffThread refuses a lambda that closes over a UI object, and every lambda in one method shares a
        // single closure — so writing the sweep inline beside a progress callback that touched the window
        // captured the window too, and the survey threw before it ran. This is the guard the runtime one could
        // only give us after somebody clicked the button.
        var cat = Cat();
        var design = Ship(Hull(cat), cat, "design");
        var work = DockingWindow.SweepWork(
            design, null!, cat, new Progress<(int Done, int Total)>(_ => { }), CancellationToken.None);

        Ui.VerifyCaptures(work);
    }

    // ---- the drawable pose ----

    private static IReadOnlyList<DockPart> Posed(DockShip receiver, DockShip incoming, DockMate mate) =>
        mate.Pose is { } pose ? DockPose.ReceiverParts(receiver, incoming, pose) : [];

    [Fact]
    public void The_posed_ship_puts_its_airlock_against_ours()
    {
        // The whole point of the pose: the two collars end up adjacent, which is what the one-tile dockOffset
        // is for. If the fitted transform had a sign inverted this is what would catch it, because the other
        // ship would be a tile out or on the wrong side entirely.
        var cat = Cat();
        var receiver = Ship(Hull(cat), cat, "receiver");
        var incoming = Ship(Hull(cat), cat, "incoming");
        var mate = Only(receiver, incoming);
        Assert.True(mate.Mates);

        var posed = Posed(receiver, incoming, mate);
        Assert.NotEmpty(posed);

        var ours = incoming.Ports[0].DocTile;
        var theirs = posed.Single(p => p.DefName == Dock);
        Assert.Equal(1, Math.Max(Math.Abs(theirs.X - ours.X), Math.Abs(theirs.Y - ours.Y)));
    }

    [Fact]
    public void A_posed_ship_that_mates_never_lands_on_our_own_tiles()
    {
        var cat = Cat();
        var receiver = Ship(Hull(cat), cat, "receiver");
        var incoming = Ship(Hull(cat), cat, "incoming");
        var mate = Only(receiver, incoming);
        Assert.True(mate.Mates);

        var ourTiles = incoming.Parts.Select(p => (p.X, p.Y)).ToHashSet();
        Assert.All(Posed(receiver, incoming, mate), p => Assert.DoesNotContain((p.X, p.Y), ourTiles));
    }

    [Fact]
    public void The_pose_keeps_every_part_it_was_given()
    {
        // A part dropped in the transform would leave a hole in the drawn hull with nothing to say so.
        var cat = Cat();
        var receiver = Ship(Hull(cat), cat, "receiver");
        var incoming = Ship(Hull(cat), cat, "incoming");
        Assert.Equal(receiver.Parts.Count, Posed(receiver, incoming, Only(receiver, incoming)).Count);
    }

    // ---- against the shipped ships ----

    private static List<DockShip> StockShips((GameEnv Env, DataIndex Index, Catalog Catalog) g)
    {
        var lookup = DockDefs.For(g.Catalog);
        var ships = new List<DockShip>();
        foreach (var file in TemplateImport.ListShipFiles(g.Index))
            foreach (var tmpl in ShipTemplate.ParseFileChecked(File.ReadAllText(file.Path), out _))
                ships.Add(DockShip.FromTemplate(tmpl, g.Catalog, lookup));
        return ships;
    }

    [SkippableFact]
    public void Stock_primaries_mate_with_very_nearly_every_other_stock_primary()
    {
        // The premise the request rests on: "build limitations essentially guarantee all primary docks can dock
        // to all other primary docks". Measured over the install, 26,064 of 26,082 ordered pairs mate. The 18
        // that do not are one symmetric cluster of six ships (SecurityOutpost and the Vector line), each of
        // which has a wall standing level with its airlock so that two of them cannot close on each other. That
        // is what "essentially" is doing in the reporter's sentence, and it is why the pairwise tool earns its
        // place even for primaries.
        var g = TestData.RequireGame();
        var primaries = StockShips(g).Where(s => s.Ports.Any(p => !p.TypeB)).ToList();
        Skip.If(primaries.Count < 2, "install carries fewer than two ships with a primary port");

        var pairs = 0;
        var mated = 0;
        foreach (var receiver in primaries)
            foreach (var incoming in primaries)
            {
                if (ReferenceEquals(receiver, incoming)) continue;
                pairs++;
                if (DockMating.Mate(receiver, incoming,
                        receiver.Ports.First(p => !p.TypeB), incoming.Ports.First(p => !p.TypeB)).Mates)
                    mated++;
            }

        Assert.True(mated >= pairs * 0.99,
            $"only {mated}/{pairs} stock primary pairs mate; the construction rules should make this near-total");
    }

    [SkippableFact]
    public void The_primary_sweep_comes_out_the_same_in_both_directions()
    {
        // The overlay tests the incoming ship's cells against the receiver's bounds and skips whatever falls
        // outside them, so it is directional in principle (see DockGrid on the left-edge quirk). Over the
        // shipped ships it comes out symmetric, and a change that broke that would be a change in the geometry
        // rather than in the frame.
        var g = TestData.RequireGame();
        var primaries = StockShips(g).Where(s => s.Ports.Any(p => !p.TypeB)).ToList();
        Skip.If(primaries.Count < 2, "install carries fewer than two ships with a primary port");

        var asymmetric = new List<string>();
        for (var i = 0; i < primaries.Count; i++)
            for (var j = i + 1; j < primaries.Count; j++)
            {
                var (a, b) = (primaries[i], primaries[j]);
                var (ap, bp) = (a.Ports.First(p => !p.TypeB), b.Ports.First(p => !p.TypeB));
                if (DockMating.Mate(a, b, ap, bp).Mates != DockMating.Mate(b, a, bp, ap).Mates)
                    asymmetric.Add($"{a.Name} / {b.Name}");
            }

        Assert.True(asymmetric.Count == 0, $"asymmetric pairs: {string.Join(", ", asymmetric.Take(5))}");
    }

    [SkippableFact]
    public void A_secondary_is_not_guaranteed_against_a_primary()
    {
        // The request's actual complaint, confirmed against the shipped ships: unlike a primary, a Secondary
        // airlock mates with some primaries and not others, so the answer cannot be known without checking.
        // Station_EJDR's two secondaries take 135 and 119 of the 162 stock primaries. A check that answered
        // "yes" to everything would pass every other test here and be worthless.
        var g = TestData.RequireGame();
        var ships = StockShips(g);
        var receivers = ships.Where(s => s.Ports.Any(p => !p.TypeB)).ToList();
        var secondaries = ships.SelectMany(s => s.Ports.Where(p => p.TypeB).Select(p => (Ship: s, Port: p))).ToList();
        Skip.If(receivers.Count < 2 || secondaries.Count == 0, "install carries no secondary port to measure");

        var discriminating = secondaries.Any(s =>
        {
            var mates = receivers.Count(r => DockMating.Mate(r, s.Ship, r.Ports.First(p => !p.TypeB), s.Port).Mates);
            return mates > 0 && mates < receivers.Count;
        });

        Assert.True(discriminating,
            "no stock secondary both mates and refuses across the stock primaries, so the check is not discriminating");
    }

    [SkippableFact]
    public void A_posed_stock_ship_lands_against_our_airlock_and_clear_of_our_hull()
    {
        // The synthetic cases use a 1x1 airlock on a three-tile hull. This runs the same two properties over
        // real geometry — a 7x2 airlock with a 7-cell floor plan, at all four rotations, on hulls up to 29,863
        // parts — which is where a transform that happens to work on a symmetric toy falls over.
        var g = TestData.RequireGame();
        var lookup = DockDefs.For(g.Catalog);
        var ships = StockShips(g).Where(s => s.Ports.Any(p => !p.TypeB)).Take(40).ToList();
        Skip.If(ships.Count < 2, "install carries too few ships with a primary airlock");

        var incoming = ships[0];
        var ourTiles = incoming.Parts.Select(p => (p.X, p.Y)).ToHashSet();
        var checkedPairs = 0;

        foreach (var receiver in ships.Skip(1))
        {
            var mate = DockMating.Mate(receiver, incoming,
                receiver.Ports.First(p => !p.TypeB), incoming.Ports.First(p => !p.TypeB));
            if (!mate.Mates || mate.Pose is not { } pose) continue;

            var posed = DockPose.ReceiverParts(receiver, incoming, pose);
            Assert.Equal(receiver.Parts.Count, posed.Count);

            // Their airlock ends up against ours...
            var ours = incoming.Ports.First(p => !p.TypeB).DocTile;
            var theirs = posed.First(p => p.DefName == receiver.Ports.First(x => !x.TypeB).DefName);
            Assert.True(Math.Max(Math.Abs(theirs.X - ours.X), Math.Abs(theirs.Y - ours.Y)) <= 4,
                $"{receiver.Name}: posed airlock at ({theirs.X},{theirs.Y}) is nowhere near ours at {ours}");

            // ...and a mating pose never puts their hull on our tiles.
            foreach (var p in posed)
                Assert.DoesNotContain((p.X, p.Y), ourTiles);

            checkedPairs++;
        }

        Assert.True(checkedPairs > 0, "no stock pair mated, so the pose was never exercised");
    }

    [SkippableFact]
    public void Every_installed_docksys_in_a_stock_template_becomes_a_port()
    {
        // GatherDockingPortData reads a port's mating anchor off the grid, so a port whose cells were all taken
        // by another item would vanish from the list with no error anywhere. A port is bulky and writes
        // unconditionally, so nothing in the shipped data does that; this is what would notice if a change to
        // the grid builder started dropping them.
        var g = TestData.RequireGame();
        var lookup = DockDefs.For(g.Catalog);
        var trigger = g.Catalog.Triggers[ProblemScan.DocksysTrigger];

        var dropped = new List<string>();
        foreach (var file in TemplateImport.ListShipFiles(g.Index))
            foreach (var tmpl in ShipTemplate.ParseFileChecked(File.ReadAllText(file.Path), out _))
            {
                var expected = tmpl.Items.Count(i => !i.Contained
                    && lookup(i.DefName) is { } def && trigger.Reqs.All(def.Has));
                var actual = DockShip.FromTemplate(tmpl, g.Catalog, lookup).Ports.Count;
                if (expected != actual) dropped.Add($"{tmpl.Name}: {expected} docksys items, {actual} ports");
            }

        Assert.True(dropped.Count == 0, string.Join("; ", dropped.Take(5)));
    }
}

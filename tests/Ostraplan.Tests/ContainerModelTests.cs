using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Ostraplan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ostraplan.Tests;

/// <summary>The container/inventory model: def parsing (install-free) plus resolution against the live game data
/// and a real save (install-gated, no-op without an install).</summary>
public class ContainerModelTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    [SkippableFact]
    public void CondOwnerDef_parses_container_and_slot_fields()
    {
        var e = JsonDocument.Parse("""
        {
          "strName": "ItmBackpack01",
          "nContainerWidth": 4, "nContainerHeight": 4,
          "inventoryWidth": 2, "inventoryHeight": 3,
          "strContainerCT": "TIsFitContainerBackpack",
          "nStackLimit": 5,
          "aSlotsWeHave": ["pocket_pouchSm01", "pocket_pouchSm02"],
          "dictSlotsLayout": {
            "self": { "x": 5.0, "y": 0.0, "z": 0.0 },
            "pocket_pouchSm01": { "x": 0.0, "y": -68.0, "z": 0.0 }
          },
          "mapSlotEffects": ["body", "Blank"]
        }
        """).RootElement;

        var co = CondOwnerDef.Parse(e);
        Assert.Equal(4, co.ContainerW);
        Assert.Equal(4, co.ContainerH);
        Assert.Equal(2, co.InvW);
        Assert.Equal(3, co.InvH);
        Assert.Equal("TIsFitContainerBackpack", co.ContainerCT);
        Assert.Equal(5, co.StackLimit);
        Assert.Equal(new[] { "pocket_pouchSm01", "pocket_pouchSm02" }, co.SlotsWeHave);
        Assert.Equal((0.0, -68.0), co.SlotLayout["pocket_pouchSm01"]);
        Assert.Equal(new[] { "body" }, co.SlotKeys);   // mapSlotEffects keys (even indices)
    }

    [SkippableFact]
    public void A_real_decoy_launcher_holds_five_missiles_by_laying_two_flat()
    {
        var g = TestData.RequireGame();
        var launcher = g.Catalog.Lookup("ItmShipWeaponDecoyLauncher01");
        var missile = g.Catalog.Lookup("ItmAmmoDecoyMissile01");
        Skip.If(launcher is null || missile is null, "decoy launcher/missile not in this install");

        // Real defs: the launcher declares a 3×5 container grid, the missile is 1 col × 3 socket adds.
        Assert.Equal((3, 5), launcher!.ContainerGrid!.Value);
        Assert.Equal((1, 3), missile!.InvSize);

        // Three fit upright across the columns; the 3×2 band left over takes two more laid flat. Counting only
        // the upright orientation reports 3 and calls the launcher full, which is what it used to do.
        Assert.Equal(5, CargoEdit.MaxAddable([], null, launcher.ContainerGrid!.Value, missile));

        var filled = CargoEdit.Add([], null, launcher.ContainerGrid!.Value, missile, 5, g.Catalog);
        Assert.NotNull(filled);
        Assert.Equal(5, filled!.Count);
        Assert.Equal(2, filled.Count(c => c.EffW == 3 && c.EffH == 1));

        // and the result is a legal layout: every item inside the declared grid, none overlapping another
        var layout = InventoryGrid.Pack(3, 5, filled);
        Assert.Equal((3, 5), (layout.Width, layout.Height));   // no growth — it genuinely fits
        AssertNoOverlaps(layout);
    }

    private static void AssertNoOverlaps(GridLayoutResult r)
    {
        for (var i = 0; i < r.Items.Count; i++)
            for (var j = i + 1; j < r.Items.Count; j++)
            {
                var (a, b) = (r.Items[i], r.Items[j]);
                Assert.False(a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H,
                    $"{a.Item.DefName} at ({a.X},{a.Y}) overlaps {b.Item.DefName} at ({b.X},{b.Y})");
            }
    }

    [SkippableFact]
    public void Catalog_resolves_a_real_container_grid_and_slots()
    {
        var g = TestData.RequireGame();

        Assert.NotEmpty(g.Catalog.Slots);   // data/slots indexed

        var bp = g.Catalog.Lookup("ItmBackpack01");
        if (bp is null) return;   // backpack item def absent in this data set
        Assert.True(bp.IsContainer);
        Assert.Equal((4, 4), bp.ContainerGrid!.Value);
        Assert.Contains("pocket_pouchSm01", bp.SlotsWeHave);
        Assert.True(bp.SlotLayout.ContainsKey("pocket_pouchSm01"));
        // the slot metadata resolves through data/slots
        Assert.True(g.Catalog.Slots.ContainsKey("pocket_pouchSm01"));
    }

    [SkippableFact]
    public void Imported_save_cargo_resolves_and_packs()
    {
        var g = TestData.RequireGame();

        // Scan every save's player ship for one that actually has cargo on a STRUCTURAL (grid-placed) part —
        // a nav console with modules, a filled crate, etc. Most cargo in a save hangs off crew, which Ostraplan
        // doesn't place, so many saves legitimately have none; the test then no-ops rather than assert nothing.
        List<Placement> withCargo = [];
        foreach (var save in SaveImport.ListSaves(g.Env))
        {
            try
            {
                var got = SaveEditImport.ImportForEditing(save, g.Catalog).Doc.Placements.Where(p => p.Cargo.Count > 0).ToList();
                if (got.Count > withCargo.Count) withCargo = got;
            }
            catch { /* not a player-ship save */ }
        }
        _out.WriteLine($"{withCargo.Count} placed container(s) with cargo across all saves");
        if (withCargo.Count == 0) return;   // no player ship in any save has stocked structural containers

        foreach (var p in withCargo)
        {
            // every contained item has an id and a sane footprint
            foreach (var item in Flatten(p.Cargo))
            {
                Assert.False(string.IsNullOrEmpty(item.StrID));
                Assert.True(item.GridW >= 1 && item.GridH >= 1);
                Assert.True(item.Stack >= 1);
            }

            // the loose cargo packs onto the grid with no overlaps
            var def = g.Catalog.Lookup(p.DefName);
            var (gw, gh) = def?.ContainerGrid ?? (6, 6);
            var loose = p.Cargo.Where(c => !c.Slotted).ToList();
            var layout = InventoryGrid.Pack(gw, gh, loose);
            var cells = layout.Items
                .SelectMany(b => Enumerable.Range(0, b.W).SelectMany(dx => Enumerable.Range(0, b.H).Select(dy => (b.X + dx, b.Y + dy))))
                .ToList();
            Assert.Equal(cells.Count, cells.Distinct().Count());   // no two blocks share a cell
        }
    }

    private static IEnumerable<CargoItem> Flatten(IReadOnlyList<CargoItem> items)
    {
        foreach (var i in items)
        {
            yield return i;
            foreach (var c in Flatten(i.Children)) yield return c;
        }
    }

    // ---- intrinsic contents: the containers a def spawns with as part of itself ----

    /// <summary>
    /// Regression (Discord, tezzy4899): a pair of coveralls declares no container grid at all — its pockets come
    /// from strLoot — so Ostraplan modelled it as holding nothing, and wrote it into the save as a bare item.
    /// It spawned "with no pockets" and could never hold anything again.
    /// </summary>
    [SkippableFact]
    public void A_garment_carries_the_pockets_its_def_spawns_with()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("OutfitSuit03") is null, "OutfitSuit03 not in this install");

        var suit = g.Catalog.Lookup("OutfitSuit03")!;
        Assert.False(suit.IsContainer, "the garment itself declares no grid — the pockets are the capacity");

        var intrinsic = g.Catalog.IntrinsicContents(suit);
        Assert.Equal(4, intrinsic.Sum(c => c.Count));
        Assert.All(intrinsic, c => Assert.True(g.Catalog.Lookup(c.DefName)?.IsContainer == true));
    }

    [SkippableFact]
    public void Adding_a_garment_materialises_its_pockets_and_they_can_be_filled()
    {
        var g = TestData.RequireGame();
        Skip.If(g.Catalog.Lookup("OutfitSuit03") is null || g.Catalog.Lookup("ItmBackpack01") is null,
            "clothing / backpack not in this install");

        var pack = g.Catalog.Lookup("ItmBackpack01")!;
        IReadOnlyList<CargoItem> cargo = [];
        cargo = CargoEdit.Add(cargo, null, (6, 6), pack, 1, g.Catalog)!;

        // the backpack's own four pouches come with it
        var bag = Assert.Single(cargo);
        Assert.Equal(4, bag.Children.Count(c => c.Intrinsic));

        cargo = CargoEdit.Add(cargo, bag.StrID, pack.ContainerGrid!.Value, g.Catalog.Lookup("OutfitSuit03")!, 1, g.Catalog)!;
        var suit = Flatten(cargo).Single(c => c.DefName == "OutfitSuit03");
        Assert.False(suit.Intrinsic, "the garment is cargo the user added, not part of the backpack");
        Assert.Equal(4, suit.Children.Count);
        Assert.All(suit.Children, c => Assert.True(c.Intrinsic));

        // and the thing that could not be done before: put something in a pocket
        var pocket = suit.Children[0];
        var pocketGrid = g.Catalog.Lookup(pocket.DefName)!.ContainerGrid!.Value;
        var food = g.Catalog.Lookup("ItmTrencherNachoFiesta");
        Skip.If(food is null, "trencher not in this install");
        var filled = CargoEdit.Add(cargo, pocket.StrID, pocketGrid, food!, 1, g.Catalog);
        Assert.NotNull(filled);
        Assert.Contains(Flatten(filled!), c => c.DefName == "ItmTrencherNachoFiesta");
    }

    /// <summary>
    /// Regression (Discord, ergo46): an EVA suit reached the game "with no slots". Its four compartments are
    /// SLOTTED onto its paper-doll, not stored in a grid — the suit declares no container grid at all — so
    /// writing them as loose cargo left <c>Ship.SpawnItems</c> with no <c>objContainer</c> to put them in and
    /// they never attached. A backpack hid the same fault, because its own 4×4 grid works either way.
    /// </summary>
    [SkippableFact]
    public void An_EVA_suits_compartments_are_slotted_onto_it_not_stored_in_it()
    {
        var g = TestData.RequireGame();
        var suit = g.Catalog.Lookup("OutfitEVA01");
        Skip.If(suit is null, "OutfitEVA01 not in this install");
        Assert.False(suit!.IsContainer, "the suit has no grid — the compartments are the whole capacity");

        var cargo = CargoEdit.Add([], null, (6, 6), suit, 1, g.Catalog);
        var worn = Assert.Single(cargo!);
        Assert.Equal(4, worn.Children.Count);
        Assert.All(worn.Children, c => Assert.True(c.Slotted, $"{c.DefName} must be slotted, not loose"));
        // each compartment lands in the slot the suit declares for it, the way CondOwner.SetData's loot pass does
        Assert.Equal(
            new[] { "pocket_clip01", "pocket_EVABatt", "pocket_EVAO2", "pocket_EVACO2" }.Order(),
            worn.Children.Select(c => c.SlotName!).Order());
        Assert.All(worn.Children, c => Assert.Contains(c.SlotName!, suit.SlotsWeHave));
    }

    [SkippableFact]
    public void A_backpacks_four_identical_pouches_take_four_different_slots()
    {
        // All four are the same def, whose mapSlotEffects names all four slots. The game walks those keys and
        // takes the first slot with room (Slots.SlotItem refuses a full one), so they spread; resolving each one
        // independently put all four in pouch 1 and lost three of them.
        var g = TestData.RequireGame();
        var pack = g.Catalog.Lookup("ItmBackpack01");
        Skip.If(pack is null, "ItmBackpack01 not in this install");

        var bag = Assert.Single(CargoEdit.Add([], null, (6, 6), pack!, 1, g.Catalog)!);
        Assert.Equal(4, bag.Children.Count);
        Assert.All(bag.Children, c => Assert.True(c.Slotted));
        Assert.Equal(4, bag.Children.Select(c => c.SlotName).Distinct().Count());
        Assert.DoesNotContain(bag.Children, c => !c.Slotted);   // and none of them eats a cell of the 4×4 grid
    }

    /// <summary>The slot rule itself, without an install: first key the host declares that is still free.</summary>
    [Fact]
    public void A_pocket_takes_the_first_slot_its_host_declares_that_is_still_free()
    {
        var cat = new Fixtures()
            .ItemLoot("PouchLoot", ("Pouch", 3))
            .Part("Pouch", container: (1, 1), slotKeys: ["pouchA", "pouchB"])
            .Part("Pack", container: (2, 2), defaultLoot: "PouchLoot", slotsWeHave: ["pouchA", "pouchB"])
            .Build();

        var bag = Assert.Single(CargoEdit.Add([], null, (6, 6), cat.Lookup("Pack")!, 1, cat)!);
        Assert.Equal(3, bag.Children.Count);
        Assert.Equal(["pouchA", "pouchB"], bag.Children.Where(c => c.Slotted).Select(c => c.SlotName!));
        // the third has no slot left, so it falls back to the host's grid rather than vanishing
        var spare = Assert.Single(bag.Children, c => !c.Slotted);
        Assert.Null(spare.SlotName);
    }

    /// <summary>
    /// Regression (Discord, ergo46): a design saved before pockets were understood as slotted holds them as loose
    /// cargo, and would write an EVA suit back into the save with no compartments again. Reopening the design
    /// re-slots them.
    /// </summary>
    [Fact]
    public void Reopening_a_design_reslots_pockets_an_older_version_stored_as_cargo()
    {
        var cat = new Fixtures()
            .Container("Box")
            .ItemLoot("PocketLoot", ("Pocket", 2))
            .Part("Pocket", container: (1, 2), slotKeys: ["hipL", "hipR"])
            .Part("Coveralls", defaultLoot: "PocketLoot", slotsWeHave: ["hipL", "hipR"])
            .Build();
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ostraplan-test-{System.Guid.NewGuid():N}.oplan");
        try
        {
            // exactly what the old writer produced: intrinsic pockets, unslotted, sitting in grid cells
            new OplanFile
            {
                Parts =
                [
                    new OplanPart
                    {
                        Def = "Box", X = 0, Y = 0,
                        Cargo =
                        [
                            new OplanCargo
                            {
                                Def = "Coveralls", StrID = "suit", Authored = true,
                                Children =
                                [
                                    new OplanCargo { Def = "Pocket", StrID = "p1", Authored = true, Intrinsic = true, X = 0, Y = 0 },
                                    new OplanCargo { Def = "Pocket", StrID = "p2", Authored = true, Intrinsic = true, X = 1, Y = 0 },
                                ],
                            },
                        ],
                    },
                ],
            }.Save(tmp);

            var (doc, missing) = OplanFile.Load(tmp).ToDocument(cat);

            Assert.Empty(missing);
            var suit = Assert.Single(doc.Placements[0].Cargo);
            Assert.All(suit.Children, c => Assert.True(c.Slotted, "an old design's pockets are re-slotted on open"));
            Assert.Equal(["hipL", "hipR"], suit.Children.Select(c => c.SlotName!).Order());
        }
        finally { System.IO.File.Delete(tmp); }
    }

    [SkippableFact]
    public void Default_loot_that_is_not_a_container_is_left_to_the_user_to_author()
    {
        // The same strLoot field carries genuine STOCK — a Coilgun's 15 rounds, a body part's wounds. Only the
        // container children are the object's own anatomy, so only those are materialised; otherwise every
        // weapon would arrive pre-loaded with ammo nobody authored and the edit cost would move.
        var g = TestData.RequireGame();
        foreach (var def in new[] { "ItmShipWeaponMassThrower01", "BodyarmUpperLA" })
            if (g.Catalog.Lookup(def) is { } p)
            {
                Assert.NotNull(p.DefaultLoot);           // it does declare loot...
                Assert.Empty(g.Catalog.IntrinsicContents(p));   // ...none of which is a container
            }
    }
}

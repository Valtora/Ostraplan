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

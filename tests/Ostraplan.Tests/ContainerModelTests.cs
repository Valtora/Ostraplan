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

    [Fact]
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

    [Fact]
    public void Catalog_resolves_a_real_container_grid_and_slots()
    {
        if (TestData.Game is not { } g) return;

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

    [Fact]
    public void Imported_save_cargo_resolves_and_packs()
    {
        if (TestData.Game is not { } g) return;

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
}

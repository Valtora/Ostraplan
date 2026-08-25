using System;
using System.Collections.Generic;
using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The item manifest (#36): the walk that finds every item on a design wherever it is, and the zone scoping over
/// it. Game-free — the walk is about the design's own structure, not about real game data.
/// </summary>
public class ItemManifestTests
{
    private const string Locker = "Locker";
    private const string Crate = "Crate";
    private const string Pouch = "Pouch";
    private const string Round = "Round";
    private const string Coveralls = "Coveralls";
    private const string Pocket = "Pocket";
    private const string Antenna = "Antenna";

    private static Catalog Cat() => new Fixtures()
        // an installed container: structure, so it is the bill of materials' business and never a manifest line
        .Part(Locker, w: 2, h: 2, startingConds: ["IsContainer"], container: (6, 6), basePrice: 400)
        // a container you can also drop on the deck
        .Part(Crate, startingConds: ["IsContainer"], container: (4, 4), basePrice: 90)
        .Part(Pouch, startingConds: ["IsContainer"], container: (2, 2), basePrice: 20)
        .Part(Round, basePrice: 5)
        // a deck item taller than the tile it is anchored to — the shape most loose items in the game actually are
        .Part(Antenna, w: 1, h: 4, basePrice: 600)
        .Part(Pocket, startingConds: ["IsContainer"], container: (1, 2), basePrice: 3)
        // a garment that spawns carrying its own pockets — intrinsic contents (see CargoItem.Intrinsic)
        .ItemLoot("PocketLoot", (Pocket, 2))
        .Part(Coveralls, defaultLoot: "PocketLoot", slotsWeHave: ["hipL", "hipR"], basePrice: 60)
        .Build();

    private static CargoItem Item(string def, string? name = null, params CargoItem[] children) =>
        new(Guid.NewGuid().ToString(), def, def, false, children) { CustomName = name };

    private static CargoItem Stack(string def, int count) =>
        new(Guid.NewGuid().ToString(), def, def, false,
            Enumerable.Range(0, count - 1).Select(_ => Item(def)).ToList())
        { Stack = count, IsStack = true };

    private static Placement Container(ShipDocument doc, int x, int y, params CargoItem[] cargo)
    {
        var p = new Placement { DefName = Locker, X = x, Y = y, Cargo = cargo };
        new PlaceCommand(p).Do(doc);
        return p;
    }

    private static LooseObject Drop(ShipDocument doc, string def, int x, int y, int quantity = 1,
        string? name = null, params CargoItem[] cargo)
    {
        var o = new LooseObject { DefName = def, X = x, Y = y, Quantity = quantity, CustomName = name, Cargo = cargo };
        new PlaceLooseCommand(o).Do(doc);
        return o;
    }

    private static LooseObject DropFacing(ShipDocument doc, string def, int x, int y, int rot)
    {
        var o = new LooseObject { DefName = def, X = x, Y = y, Rot = rot };
        new PlaceLooseCommand(o).Do(doc);
        return o;
    }

    private static ShipZone Named(ShipDocument doc, string name, params (int X, int Y)[] tiles)
    {
        var z = new ShipZone { Name = name, Tiles = [.. tiles] };
        new CreateZoneCommand(z).Do(doc);
        return z;
    }

    private static ManifestLine Line(Manifest m, string def) =>
        Assert.Single(m.Lines, l => l.DefName == def);

    // ---- what the walk finds ----

    [Fact]
    public void A_design_with_nothing_on_it_manifests_nothing()
    {
        var m = ItemManifest.Build(new ShipDocument(Cat()));

        Assert.True(m.IsEmpty);
        Assert.Equal(0, m.TotalCount);
        Assert.Equal(0, m.TotalValue, 3);
    }

    [Fact]
    public void Loose_deck_items_are_listed_with_their_stack_as_the_count()
    {
        var doc = new ShipDocument(Cat());
        Drop(doc, Round, 1, 1, quantity: 20);

        var m = ItemManifest.Build(doc);
        var line = Line(m, Round);

        Assert.Equal(20, line.Count);
        Assert.Equal(20, m.OnDeckCount);
        Assert.Equal(0, m.ContainedCount);
        var entry = Assert.Single(line.Entries);
        Assert.True(entry.OnDeck);
        Assert.Null(entry.ItemId);            // the entry IS the deck object
        Assert.Equal("on the deck", entry.Where);
    }

    [Fact]
    public void Cargo_inside_an_installed_container_is_listed_and_says_which_container()
    {
        var doc = new ShipDocument(Cat());
        Container(doc, 4, 4, Item(Round));

        var m = ItemManifest.Build(doc);
        var entry = Assert.Single(Line(m, Round).Entries);

        Assert.False(entry.OnDeck);
        Assert.NotNull(entry.ItemId);
        Assert.Equal($"in {Locker}", entry.Where);
        Assert.Equal(1, m.ContainedCount);
    }

    [Fact]
    public void The_installed_container_itself_is_not_a_manifest_line()
    {
        // A locker is built from an install kit: it is structure, and the bill of materials counts it. The manifest
        // is about what is IN it, which no other report answers. A crate lying on the deck is the other case: it is
        // an item, so it gets a line of its own.
        var doc = new ShipDocument(Cat());
        Container(doc, 4, 4, Item(Round));
        Drop(doc, Crate, 1, 1);

        var m = ItemManifest.Build(doc);

        Assert.DoesNotContain(m.Lines, l => l.DefName == Locker);
        Assert.Equal(1, Line(m, Crate).Count);
    }

    [Fact]
    public void An_empty_installed_container_contributes_nothing()
    {
        var doc = new ShipDocument(Cat());
        Container(doc, 4, 4);

        Assert.True(ItemManifest.Build(doc).IsEmpty);
    }

    [Fact]
    public void Nesting_is_walked_to_the_bottom_and_names_the_whole_path()
    {
        var doc = new ShipDocument(Cat());
        Container(doc, 4, 4, Item(Crate, "Electrical", Item(Pouch, "Fuses", Item(Round))));

        var m = ItemManifest.Build(doc);

        Assert.Equal($"in {Locker}", Assert.Single(Line(m, Crate).Entries).Where);
        Assert.Equal($"in {Locker} ▸ Electrical", Assert.Single(Line(m, Pouch).Entries).Where);
        Assert.Equal($"in {Locker} ▸ Electrical ▸ Fuses", Assert.Single(Line(m, Round).Entries).Where);
        Assert.Equal(3, m.TotalCount);
    }

    [Fact]
    public void A_deck_container_lists_itself_and_everything_inside_it()
    {
        var doc = new ShipDocument(Cat());
        Drop(doc, Crate, 2, 3, name: "Spares", cargo: [Item(Round)]);

        var m = ItemManifest.Build(doc);

        Assert.Equal("on the deck", Assert.Single(Line(m, Crate).Entries).Where);
        Assert.Equal("in Spares", Assert.Single(Line(m, Round).Entries).Where);
        Assert.Equal(1, m.OnDeckCount);
        Assert.Equal(1, m.ContainedCount);
    }

    [Fact]
    public void A_cargo_stack_is_one_entry_counting_its_members()
    {
        // A stack persists as a lead item plus copies of itself as children, so descending into one would report
        // twenty rounds as one round holding nineteen more.
        var doc = new ShipDocument(Cat());
        Container(doc, 4, 4, Stack(Round, 20));

        var m = ItemManifest.Build(doc);
        var line = Line(m, Round);

        Assert.Equal(20, line.Count);
        Assert.Single(line.Entries);
    }

    // ---- grouping ----

    [Fact]
    public void Identical_defs_group_onto_one_line_wherever_they_are()
    {
        var doc = new ShipDocument(Cat());
        Drop(doc, Round, 1, 1, quantity: 3);
        Container(doc, 4, 4, Item(Round), Item(Crate, null, Item(Round)));

        var m = ItemManifest.Build(doc);
        var line = Line(m, Round);

        Assert.Equal(5, line.Count);
        Assert.Equal(3, line.Entries.Count);
        Assert.Equal(3, line.OnDeckCount);
        Assert.Equal(2, line.ContainedCount);
    }

    [Fact]
    public void A_line_keeps_the_stock_name_while_an_entry_shows_the_one_it_was_given()
    {
        // Which is what makes a renamed crate findable: you look it up under "Crate" and read what it is called.
        var doc = new ShipDocument(Cat());
        Drop(doc, Crate, 1, 1, name: "Electrical");
        Drop(doc, Crate, 2, 1);

        var line = Line(ItemManifest.Build(doc), Crate);

        string[] names = ["Electrical", Crate];
        Assert.Equal(Crate, line.Friendly);
        Assert.Equal(names, line.Entries.Select(e => e.Name));
        Assert.Equal("Electrical", line.Entries[0].CustomName);
        Assert.Null(line.Entries[1].CustomName);
    }

    [Fact]
    public void Lines_are_sorted_by_name_so_two_runs_read_the_same()
    {
        var doc = new ShipDocument(Cat());
        Drop(doc, Round, 1, 1);
        Drop(doc, Crate, 2, 1);
        Drop(doc, Pouch, 3, 1);

        string[] expected = [Crate, Pouch, Round];

        Assert.Equal(expected, ItemManifest.Build(doc).Lines.Select(l => l.Friendly));
    }

    [Fact]
    public void Deck_items_are_walked_in_reading_order_rather_than_dictionary_order()
    {
        // LooseObjects comes off a tile-keyed dictionary, so without an explicit order the same design could list
        // its strays differently twice.
        var doc = new ShipDocument(Cat());
        Drop(doc, Crate, 5, 9);
        Drop(doc, Crate, 1, 2);
        Drop(doc, Crate, 4, 2);

        (int X, int Y)[] expected = [(1, 2), (4, 2), (5, 9)];

        Assert.Equal(expected, Line(ItemManifest.Build(doc), Crate).Entries.Select(e => (e.Host.X, e.Host.Y)));
    }

    // ---- intrinsic contents ----

    [Fact]
    public void A_hosts_own_pockets_are_listed_and_counted_apart()
    {
        // They are in the tree and they are written to the save, so the manifest shows them; the separate tally is
        // what lets the window say which part of the total is pockets nobody put there.
        var doc = new ShipDocument(Cat());
        Drop(doc, Coveralls, 1, 1);

        var m = ItemManifest.Build(doc);
        var pockets = Line(m, Pocket);

        Assert.Equal(2, pockets.Count);
        Assert.True(pockets.AllIntrinsic);
        Assert.Equal(2, m.IntrinsicCount);
        Assert.Equal(3, m.TotalCount);                    // the coveralls and their two pockets
        Assert.False(Line(m, Coveralls).AllIntrinsic);
    }

    // ---- value ----

    [Fact]
    public void Value_is_the_base_price_across_the_stack()
    {
        var doc = new ShipDocument(Cat());
        Drop(doc, Round, 1, 1, quantity: 20);          // 20 × 5
        Container(doc, 4, 4, Item(Crate));             // 90; the locker itself is structure and is not priced here

        var m = ItemManifest.Build(doc);

        Assert.Equal(100, Line(m, Round).Value, 3);
        Assert.Equal(190, m.TotalValue, 3);
    }

    // ---- zone scoping ----

    private static ShipZone Zone(ShipDocument doc, params (int X, int Y)[] tiles)
    {
        var z = new ShipZone { Name = "Hold", Tiles = [.. tiles] };
        new CreateZoneCommand(z).Do(doc);
        return z;
    }

    [Fact]
    public void A_zone_scope_keeps_only_the_deck_items_standing_in_it()
    {
        var doc = new ShipDocument(Cat());
        Drop(doc, Crate, 1, 1);
        Drop(doc, Round, 8, 8);
        var zone = Zone(doc, (1, 1), (2, 1));

        var m = ItemManifest.Build(doc, zone.Tiles);

        Assert.Equal(1, m.TotalCount);
        Assert.Equal(Crate, Assert.Single(m.Lines).DefName);
    }

    [Fact]
    public void A_container_in_the_zone_brings_its_whole_tree_with_it()
    {
        // Contents sit where their host does, however deep they nest. That is also what a shop window means by
        // scoping to a zone: everything the zone holds, not only what is lying on it.
        var doc = new ShipDocument(Cat());
        Container(doc, 4, 4, Item(Crate, "Electrical", Item(Round)));
        var zone = Zone(doc, (4, 4));

        var m = ItemManifest.Build(doc, zone.Tiles);

        Assert.Equal(2, m.TotalCount);
        Assert.Contains(m.Lines, l => l.DefName == Round);
    }

    [Fact]
    public void A_container_is_in_the_zone_when_any_of_its_body_is()
    {
        // The locker is 2x2 at (4,4), so its origin tile is outside a zone covering only (5,5). A single-tile test
        // would drop the whole locker's contents off the manifest.
        var doc = new ShipDocument(Cat());
        Container(doc, 4, 4, Item(Round));
        var zone = Zone(doc, (5, 5));

        Assert.Equal(1, ItemManifest.Build(doc, zone.Tiles).TotalCount);
    }

    [Fact]
    public void A_deck_item_is_in_the_zone_when_any_of_its_footprint_is()
    {
        // The antenna is 1x4 at (4,4), so it reaches (4,7) while its anchor sits outside a zone covering that tile
        // alone. Testing the anchor only — which is what the report did — dropped it off the zone entirely.
        var doc = new ShipDocument(Cat());
        Drop(doc, Antenna, 4, 4);
        var zone = Zone(doc, (4, 7));

        Assert.Equal(1, ItemManifest.Build(doc, zone.Tiles).TotalCount);
    }

    [Fact]
    public void Rotating_a_deck_item_can_change_the_zone_it_is_filed_under()
    {
        // The fault RedTwinkleToes reported: a deck item straddling two zones always took the one on its top-left
        // tile, whichever way it was facing, because the anchor IS the top-left of the rotated footprint. Filed by
        // the whole footprint, the 1x4 antenna at (0,0) reaches down into "South" unrotated and east into "East"
        // at 90 degrees, and the answer changes with it.
        //
        // "South" is created first, so it also wins the tie on the unrotated pose under the document-order rule —
        // which is only visible BECAUSE the footprint is what is tested. The anchor tile (0,0) is in neither zone.
        var doc = new ShipDocument(Cat());
        Named(doc, "South", (0, 3));
        Named(doc, "East", (3, 0));

        var flat = ItemManifest.Build(doc);   // nothing dropped yet
        Assert.True(flat.IsEmpty);

        DropFacing(doc, Antenna, 0, 0, 0);
        Assert.Equal("South", Assert.Single(Line(ItemManifest.Build(doc), Antenna).Entries).Zone);

        var doc90 = new ShipDocument(Cat());
        Named(doc90, "South", (0, 3));
        Named(doc90, "East", (3, 0));
        DropFacing(doc90, Antenna, 0, 0, 90);
        Assert.Equal("East", Assert.Single(Line(ItemManifest.Build(doc90), Antenna).Entries).Zone);
    }

    [Fact]
    public void A_container_outside_the_zone_contributes_nothing()
    {
        var doc = new ShipDocument(Cat());
        Container(doc, 4, 4, Item(Round));
        var zone = Zone(doc, (9, 9));

        Assert.True(ItemManifest.Build(doc, zone.Tiles).IsEmpty);
    }

    [Fact]
    public void An_empty_zone_scope_is_an_empty_manifest_rather_than_the_whole_ship()
    {
        var doc = new ShipDocument(Cat());
        Drop(doc, Crate, 1, 1);

        Assert.True(ItemManifest.Build(doc, new HashSet<(int X, int Y)>()).IsEmpty);
        Assert.False(ItemManifest.Build(doc).IsEmpty);   // null means the whole ship, and an empty set does not
    }

    // ---- pointing at the grid, and finding an item again ----

    [Fact]
    public void A_parts_tiles_are_its_above_floor_body()
    {
        var doc = new ShipDocument(Cat());
        var locker = Container(doc, 4, 4, Item(Round));

        var tiles = ItemManifest.TilesOf(doc, new RenderItem(locker, null));

        Assert.Equal(4, tiles.Count);
        Assert.Contains((4, 4), tiles);
        Assert.Contains((5, 5), tiles);
    }

    [Fact]
    public void A_deck_items_tiles_are_the_one_it_lies_on()
    {
        var doc = new ShipDocument(Cat());
        var crate = Drop(doc, Crate, 2, 3);

        (int X, int Y)[] expected = [(2, 3)];

        Assert.Equal(expected, ItemManifest.TilesOf(doc, new RenderItem(null, crate)));
    }

    [Fact]
    public void An_item_is_found_again_by_id_and_lost_when_it_is_removed()
    {
        // The cargo tree is immutable, so an entry's own CargoItem reference goes stale the moment anything is
        // edited. The id is the handle that survives, which is what every per-row action re-reads through.
        var doc = new ShipDocument(Cat());
        var round = Item(Round);
        var locker = Container(doc, 4, 4, Item(Crate, "Electrical", round));
        var host = new RenderItem(locker, null);

        Assert.Equal(Round, ItemManifest.Resolve(host, round.StrID)?.DefName);

        locker.Cargo = CargoEdit.RemoveWhole(locker.Cargo, round.StrID);
        Assert.Null(ItemManifest.Resolve(host, round.StrID));
    }

    [Fact]
    public void An_entry_carries_the_host_a_rename_or_a_delete_has_to_write_through()
    {
        var doc = new ShipDocument(Cat());
        var locker = Container(doc, 4, 4, Item(Round));
        var crate = Drop(doc, Crate, 1, 1);

        var m = ItemManifest.Build(doc);

        Assert.Same(locker, Assert.Single(Line(m, Round).Entries).Host.Placement);
        Assert.Same(crate, Assert.Single(Line(m, Crate).Entries).Host.Loose);
    }

    // ---- the location tree ----

    private static ManifestNode Node(IEnumerable<ManifestNode> among, string label) =>
        Assert.Single(among, n => n.Label == label);

    [Fact]
    public void The_location_tree_keeps_the_nesting_a_flat_list_throws_away()
    {
        // The shape RedTwinkleToes asked for: zone, then the thing it is in, then what that is in, then the items.
        var doc = new ShipDocument(Cat());
        Container(doc, 1, 1, Item(Crate, "Electrical", Stack(Round, 4), Item(Pouch, "Spares", Stack(Round, 2))));
        var zone = Zone(doc, (1, 1));
        zone.Name = "Engineering";

        var roots = ItemManifest.ByLocation(ItemManifest.Build(doc).Lines);

        var eng = Node(roots, "Engineering");   // a real zone, so the level stays
        Assert.Equal(ManifestNodeKind.Zone, eng.Kind);
        var locker = Node(eng.Children, Locker);
        Assert.Equal(ManifestNodeKind.Host, locker.Kind);
        var crate = Node(locker.Children, "Electrical");
        var pouch = Node(crate.Children, "Spares");

        // Four rounds beside the pouch, two inside it: the depth is the answer, not an accident of the walk order.
        Assert.Equal(4, Node(crate.Children, Round).Count);
        Assert.Equal(2, Node(pouch.Children, Round).Count);

        // A container is one item and a place at once, so its own figure and its subtree total are both readable.
        Assert.Equal(1, crate.OwnCount);
        Assert.Equal(8, crate.Count);          // the crate, 4 rounds, the pouch, 2 rounds
        Assert.Equal(7, crate.ContainedCount);
        Assert.Equal(8, locker.Count);
        Assert.Equal(8, eng.Count);
    }

    [Fact]
    public void Things_standing_in_no_zone_get_a_bucket_of_their_own()
    {
        var doc = new ShipDocument(Cat());
        Drop(doc, Crate, 1, 1, cargo: Stack(Round, 3));
        Drop(doc, Round, 9, 9);
        var zone = Zone(doc, (1, 1));
        zone.Name = "Hold";

        var roots = ItemManifest.ByLocation(ItemManifest.Build(doc).Lines);

        Assert.Equal(4, Node(roots, "Hold").Count);            // the crate and its three rounds
        Assert.Equal(1, Node(roots, ItemManifest.NoZone).Count);
    }

    [Fact]
    public void A_deck_object_is_its_own_host_rather_than_sitting_inside_a_container_that_does_not_exist()
    {
        var doc = new ShipDocument(Cat());
        Drop(doc, Crate, 4, 4, name: "Odds and ends", cargo: Stack(Round, 2));

        var roots = ItemManifest.ByLocation(ItemManifest.Build(doc).Lines);

        var crate = Node(roots, "Odds and ends");   // no zones on this design, so the bucket is not a level
        Assert.Equal(ManifestNodeKind.Host, crate.Kind);
        Assert.NotNull(crate.Entry);                 // it is an item as well as a place
        Assert.Equal(1, crate.OwnCount);
        Assert.Equal(3, crate.Count);
        Assert.Equal(2, Node(crate.Children, Round).Count);
    }

    [Fact]
    public void Both_groupings_answer_for_exactly_the_same_items()
    {
        // The tree is built from the lines the other view is showing, so a filter or a scope cannot mean one thing
        // in one grouping and something else in the other.
        var doc = new ShipDocument(Cat());
        Container(doc, 1, 1, Item(Crate, null, Stack(Round, 6)), Item(Pouch));
        Drop(doc, Round, 9, 9, quantity: 3);

        var m = ItemManifest.Build(doc);
        var roots = ItemManifest.ByLocation(m.Lines);

        Assert.Equal(m.TotalCount, roots.Sum(r => r.Count));
        Assert.Equal(m.TotalValue, roots.Sum(r => r.Value), 6);
    }

    [Fact]
    public void A_container_is_one_node_whether_it_is_met_as_an_item_or_walked_through()
    {
        // A crate is both a thing you own and a place things are. Keying the two differently put every non-empty
        // crate on the tree twice: once as the route to its cargo, once as the item it also is.
        var doc = new ShipDocument(Cat());
        Container(doc, 1, 1, Item(Crate, "Electrical", Stack(Round, 3)));

        var roots = ItemManifest.ByLocation(ItemManifest.Build(doc).Lines);
        var locker = Node(roots, Locker);

        var crate = Node(locker.Children, "Electrical");   // Single(): two would fail here
        Assert.Equal(ManifestNodeKind.Container, crate.Kind);
        Assert.NotNull(crate.Entry);
        Assert.Equal(1, crate.OwnCount);
        Assert.Equal(4, crate.Count);
    }

    [Fact]
    public void Two_containers_sharing_a_name_stay_two_places()
    {
        // The location path is written with names for reading, but identity is what the tree is keyed on, or a
        // pair of crates both called "Spares" would pool their contents into one row that describes neither.
        var doc = new ShipDocument(Cat());
        Container(doc, 1, 1,
            Item(Crate, "Spares", Stack(Round, 2)),
            Item(Crate, "Spares", Stack(Round, 5)));

        var roots = ItemManifest.ByLocation(ItemManifest.Build(doc).Lines);
        var locker = Node(roots, Locker);

        var crates = locker.Children.Where(c => c.Label == "Spares").ToList();
        Assert.Equal(2, crates.Count);
        Assert.Equal([3, 6], crates.Select(c => c.Count).OrderBy(n => n));
    }

    [Fact]
    public void The_fullest_level_leads_at_every_depth()
    {
        // The rollup is the point of this view, so the thing holding most of the ship should be what the eye lands
        // on. Sorted by name alone, a hold opened on whatever happened to start with an A.
        var doc = new ShipDocument(Cat());
        Container(doc, 1, 1, Stack(Round, 2));
        Container(doc, 5, 5, Stack(Round, 9));

        var roots = ItemManifest.ByLocation(ItemManifest.Build(doc).Lines);

        Assert.Equal([9, 2], roots.Select(r => r.Count));
    }

    [Fact]
    public void A_design_with_no_zones_does_not_open_on_an_empty_heading()
    {
        // One "Not in a zone" root holding the whole ship separates nothing and indents everything, so it is
        // dropped. It stays wherever it is one bucket among several, because there it does separate something.
        var doc = new ShipDocument(Cat());
        Drop(doc, Crate, 1, 1, cargo: Stack(Round, 2));

        var roots = ItemManifest.ByLocation(ItemManifest.Build(doc).Lines);

        Assert.Equal(ManifestNodeKind.Host, Assert.Single(roots).Kind);

        var zoned = new ShipDocument(Cat());
        Drop(zoned, Crate, 1, 1, cargo: Stack(Round, 2));
        Drop(zoned, Round, 9, 9);
        Zone(zoned, (1, 1));
        Assert.Contains(ItemManifest.ByLocation(ItemManifest.Build(zoned).Lines),
                        r => r.Label == ItemManifest.NoZone);
    }
}

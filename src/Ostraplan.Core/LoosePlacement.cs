namespace Ostraplan.Core;

/// <summary>
/// "The Law" for laying a loose item (the Items palette) on the ship: an item can lie on the deck, or go into a
/// container covering the tile under the cursor that accepts it. It keeps the planner from authoring an item
/// clipped inside a wall or through the fitting beside it.
///
/// <para><b>The unit is the footprint, not the anchor tile.</b> 521 of the 888 loose items the game ships are
/// bigger than one tile (<c>ItmAntenna01Loose</c> is 1×4, <c>ItmAntenna02Loose</c> 2×4), and the canvas has
/// always drawn them across the whole of it. Testing only the anchor let an antenna be laid with three quarters
/// of itself inside a wall or through another item — and made an item's zone the zone of its top-left tile
/// whichever way it was facing, which is how it was reported.</para>
///
/// <para><b>This governs a palette drop and nothing else.</b> The game runs <c>Item.CheckFit</c> on the
/// <i>interactive</i> hand-drop only; <c>Ship.SpawnItems</c> places a template's deck cargo with no check at all,
/// and the shipped ships take full advantage — <c>Station_MTRS_Nuked</c> strews 254 pieces of scrap over
/// unfloored wreckage, <c>Station_Ground</c> lies regolith on a station exterior, and <c>Babak</c> piles fifteen
/// separate pill objects on one tile. So a design that <i>arrives</i> (an import, an <c>.oplan</c>, a save) is
/// never judged against this, exactly as <see cref="ProblemScan"/> exempts given/locked structure from the
/// placement law. What is authored here is held to the rule the in-game drop is held to; what came from the game
/// is left as the game wrote it.</para>
///
/// <para><b>The socket law is the game's; one item per tile is Ostraplan's.</b> An item's
/// <c>aSocketForbids</c> carries <c>TILItemForbids</c> (<c>IsFixture</c> / <c>IsObstruction</c> /
/// <c>IsItemTile</c>) over its whole footprint, so the game's own drop refuses a bunk, a locker, or a tile
/// another item claims — that is <see cref="CheckFit"/>, run against the deck-item condition layer as well as the
/// structural one. One item per tile is a planner convention on top, kept because a pile the plan cannot show you
/// is not a plan; the game does stack them, so it is enforced at the cursor and never against a design that
/// already holds one.</para>
///
/// <para><b>There is no floor requirement.</b> No loose def declares one (<c>aSocketReqs</c> is blank on all of
/// them) and core content lies items on unfloored tiles as a matter of course, so requiring deck underneath would
/// refuse the exteriors, regolith fields and wreckage the game itself authors.</para>
///
/// <para><b>What is deliberately not ported: the reverse direction.</b> An installed part whose forbid mask names
/// <c>IsItemTile</c> would, in game, refuse to be built over a deck item. Ostraplan lets it, because the deck
/// layer is kept out of <see cref="ShipDocument.Conds"/> on purpose — rooms, airtightness and the rating must not
/// see a crate — and because a planner builds the ship before it dresses it. Move the crate, not the wall.</para>
/// </summary>
public static class LoosePlacement
{
    /// <summary>
    /// Whether <paramref name="item"/> may be laid down at (<paramref name="x"/>,<paramref name="y"/>) facing
    /// <paramref name="rot"/>: one item per tile and the game's socket law, both over the whole rotated
    /// footprint. The failing tiles come back for the red ghost, exactly as they do for a placement.
    /// </summary>
    /// <param name="self">The item being moved, when re-testing one already on the deck. Its own footprint is
    /// lifted out of the deck-condition layer for the test, so it does not fail against where it currently is —
    /// the same trick <see cref="CheckFit"/>'s <c>self</c> plays for a placement.</param>
    public static FitResult Check(ShipDocument doc, PartDef item, int x, int y, int rot, LooseObject? self = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(item);

        var tiles = ShipDocument.LooseTiles(item, x, y, rot).ToList();

        // One item per tile is Ostraplan's own invariant and is tested here rather than left to the socket law,
        // because a def the catalogue only knows as a cooverlay carries no masks at all — the law would have
        // nothing to say about it and the item would land on top of whatever was already there. It also reads
        // better than the mask's generic "tile is already occupied".
        var moving = self is null ? null : (IReadOnlySet<Guid>)new HashSet<Guid> { self.Id };
        var taken = tiles.Where(t => !doc.LooseFreeAt(t.X, t.Y, moving)).ToList();
        if (taken.Count > 0) return new FitResult(false, taken, "another deck item is already there");

        var selfItem = self is not null ? doc.Catalog.Lookup(self.DefName)?.Item : null;
        if (selfItem is not null) doc.LooseConds.Apply(self!.X, self.Y, self.Rot, selfItem, -1);
        try
        {
            // The envelope is the primary airlock's construction bound, and it bounds CONSTRUCTION. A template
            // spawns its floor cargo wherever the file says, so a planner has no business refusing a crate for
            // standing past the mating face of a port it will never be built through.
            return CheckFit.Check(doc, item, x, y, rot, self: null, includeEnvelope: false, overlay: doc.LooseConds);
        }
        finally
        {
            if (selfItem is not null) doc.LooseConds.Apply(self!.X, self.Y, self.Rot, selfItem, +1);
        }
    }

    /// <summary>True if a loose item facing <paramref name="rot"/> may lie at this pose — <see cref="Check"/>
    /// reduced to a yes/no, for the callers that only branch on it.</summary>
    public static bool CanLieAt(ShipDocument doc, PartDef item, int x, int y, int rot = 0) =>
        Check(doc, item, x, y, rot).Ok;

    /// <summary>
    /// The container covering (<paramref name="x"/>,<paramref name="y"/>) that would accept one more
    /// <paramref name="item"/> — the topmost placement that is a container, passes the item's
    /// <see cref="ContainerFilter"/>, and still has room (<see cref="CargoEdit.MaxAddable"/> &gt; 0). Null when no
    /// such container is under the cursor, so the caller falls back to laying the item on the deck.
    /// </summary>
    public static Placement? AcceptingContainerAt(ShipDocument doc, Catalog catalog, int x, int y, PartDef item)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (var p in doc.HitTestStack(x, y))   // topmost first
        {
            if (doc.Part(p) is not { IsContainer: true } container) continue;
            if (!ContainerFilter.Accepts(catalog, container, item)) continue;
            var grid = container.ContainerGrid ?? (6, 6);
            if (CargoEdit.MaxAddable(p.Cargo, null, grid, item) > 0) return p;
        }
        return null;
    }

    /// <summary>
    /// The loose item covering (<paramref name="x"/>,<paramref name="y"/>) that would take one more
    /// <paramref name="item"/> — a crate or a backpack lying on the deck, tested exactly as an installed container
    /// is. Null when the tile is clear, holds something that cannot store this, or is full, so the caller falls
    /// back to laying the item on the deck and reports the tile as taken.
    ///
    /// <para>Checked <b>after</b> <see cref="AcceptingContainerAt"/>: an installed container wins a tile it shares
    /// with a deck item, matching the topmost-first rule there.</para>
    /// </summary>
    public static LooseObject? AcceptingLooseAt(ShipDocument doc, Catalog catalog, int x, int y, PartDef item)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalog);
        if (doc.LooseAt(x, y) is not { } lo) return null;
        if (catalog.Lookup(lo.DefName) is not { IsContainer: true } host) return null;
        if (!ContainerFilter.Accepts(catalog, host, item)) return null;
        var grid = host.ContainerGrid ?? (6, 6);
        return CargoEdit.MaxAddable(lo.Cargo, null, grid, item) > 0 ? lo : null;
    }
}

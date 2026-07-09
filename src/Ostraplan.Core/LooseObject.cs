namespace Ostraplan.Core;

/// <summary>
/// A loose item lying on a ship tile — cargo dropped straight onto the floor (food, ammo, clothing, tools, a
/// personal effect), as opposed to a <see cref="Placement"/> (installed structure) or a <see cref="CargoItem"/>
/// (an item inside a container). Loose objects are a <b>non-structural overlay</b>: like <see cref="ShipZone"/>s
/// they carry no tile conditions and take no part in the socket law, room flood-fill, airtightness, or rating —
/// they only render and export. At most one sits on a tile (the design model is one-per-tile), so its
/// <see cref="X"/>/<see cref="Y"/> identify it. Immutable; a move is a remove-then-place.
/// </summary>
public sealed class LooseObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The loose item's condowner/cooverlay <c>strName</c> (what the export references). Resolves to a
    /// <see cref="PartDef"/> via <see cref="Catalog.Lookup"/> for its sprite, footprint and friendly name.</summary>
    public required string DefName { get; init; }

    public required int X { get; init; }
    public required int Y { get; init; }

    /// <summary>Ostraplan rotation in {0,90,180,270}. Loose items are almost always dropped un-rotated; kept so a
    /// design can face an item and the export can bake its <c>fRotation</c>.</summary>
    public int Rot { get; init; }

    /// <summary>How many of this item sit stacked on the tile (a stackable item like ammo or rations). 1 for a
    /// single. Mutable so "Change Quantity" can retune it in place (keeping the object's identity for selection);
    /// the caller clamps it to the item's <see cref="PartDef.StackLimit"/>.</summary>
    public int Quantity { get; set; } = 1;
}

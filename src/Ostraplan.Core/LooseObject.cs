using System.Collections.Generic;

namespace Ostraplan.Core;

/// <summary>
/// A loose item lying on a ship tile — cargo dropped straight onto the floor (food, ammo, clothing, tools, a
/// personal effect), as opposed to a <see cref="Placement"/> (installed structure) or a <see cref="CargoItem"/>
/// (an item inside a container). Loose objects are a <b>non-structural overlay</b>: like <see cref="ShipZone"/>s
/// they carry no tile conditions and take no part in the socket law, room flood-fill, airtightness, or rating —
/// they only render and export. At most one sits on a tile (the design model is one-per-tile), which is the one
/// invariant a mover has to respect: <see cref="ShipDocument.LooseFreeAt"/> is how it asks.
///
/// <para>Its pose is mutable, the same as <see cref="Placement"/>'s, so a move keeps the object's identity and the
/// selection pointing at it survives being dragged, turned or flipped. Go through
/// <see cref="ShipDocument.MoveLooseTo"/> rather than assigning the fields: the document indexes loose items by
/// tile, and a pose written behind its back leaves the index pointing at the old one.</para>
/// </summary>
public sealed class LooseObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The loose item's condowner/cooverlay <c>strName</c> (what the export references). Resolves to a
    /// <see cref="PartDef"/> via <see cref="Catalog.Lookup"/> for its sprite, footprint and friendly name.</summary>
    public required string DefName { get; init; }

    /// <summary>The tile it lies on. Settable only through <see cref="ShipDocument.MoveLooseTo"/>, which keeps the
    /// document's tile index in step (see the class remarks).</summary>
    public required int X { get; set; }

    /// <inheritdoc cref="X"/>
    public required int Y { get; set; }

    /// <summary>Ostraplan rotation in {0,90,180,270}. Loose items are almost always dropped un-rotated; kept so a
    /// design can face an item and the export can bake its <c>fRotation</c>.</summary>
    public int Rot { get; set; }

    /// <summary>How many of this item sit stacked on the tile (a stackable item like ammo or rations). 1 for a
    /// single. Mutable so "Change Quantity" can retune it in place (keeping the object's identity for selection);
    /// the caller clamps it to the item's <see cref="PartDef.StackLimit"/>.</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>
    /// What this item holds, exactly as <see cref="Placement.Cargo"/> does for an installed container: a backpack
    /// on the deck has pouches, an EVA suit has its four compartments, a crate has whatever was put in it. Empty
    /// for the great majority of loose items, which hold nothing.
    ///
    /// <para>Seeded with the item's own intrinsic containers when it is created or loaded (see
    /// <see cref="CargoEdit.IntrinsicContentsOf"/>), because the game spawns those with the object and a save
    /// restores an item as recorded rather than respawning it.</para>
    /// </summary>
    public IReadOnlyList<CargoItem> Cargo { get; set; } = [];

    /// <summary>
    /// A name the user gave this item, replacing its stock one everywhere the item is named. Null (and omitted
    /// from the .oplan) when it carries the name its def came with.
    ///
    /// <para>The same rename a <see cref="Placement"/> carries (see <see cref="Rename"/>), for the same reason: the
    /// game renames <b>anything that is not a person</b>, a tool on the deck included, so a crate can read
    /// "Electrical" and a SuperHandy can be labelled with the section it belongs to (#38). It travels as the
    /// item's own <c>Rename</c> GPM panel on export and on a save write-back, and an import reads it back.</para>
    ///
    /// <para>On a stack (<see cref="Quantity"/> &gt; 1) the name lands on the <b>head</b>, which is the object the
    /// design has: the extra copies are written as members of the head's stack and carry no name, exactly as the
    /// game keeps a renamed stack.</para>
    /// </summary>
    public string? CustomName { get; set; }

    /// <summary>A manual draw-order bias, exactly as <see cref="Placement.ZBias"/> — loose items share the one
    /// render order with placed parts (see <see cref="ShipDocument.RenderOrder"/>), so a dropped canister can be
    /// pushed behind the fixture it leans on. Mutable for the same reason <see cref="Quantity"/> is: the nudge
    /// retunes it in place, keeping the object's identity for the selection pointing at it.</summary>
    public int ZBias { get; set; }
}

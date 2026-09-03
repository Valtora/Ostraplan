namespace Ostraplan.Core;

/// <summary>
/// One grid drawn in a container view: the host's own, or a slotted child's shown with it. A tree, because a
/// backpack's pouches and an EVA suit's compartments nest and the game draws every level at once.
/// </summary>
/// <param name="ContainerId">The <see cref="CargoItem.StrID"/> this panel draws, or null for the window's own
/// host (the placed part or deck item the view was opened on).</param>
/// <param name="DefName">The def whose grid this is.</param>
/// <param name="SlotName">The slot on the parent panel this one occupies; null for the host.</param>
/// <param name="Grid">The panel's own container grid, or null when it has none of its own and exists only to
/// carry sub-panels (an EVA suit declares compartments and no grid at all).</param>
/// <param name="Offset">Where this panel's origin sits relative to its parent's, <b>in cells</b>, y positive
/// downward. Null on the figure's root, which is the origin, and null on a child whose parent declares no
/// position for its slot: that is the game's untethered case, laid out in a flow rather than pinned.</param>
/// <param name="SelfOffset">Where this panel's own grid sits within the panel, in cells
/// (<c>dictSlotsLayout["self"]</c>). Almost always (0,0). It is a second offset rather than folded into
/// <see cref="Offset"/> because the game applies the two at different levels: a child window is positioned
/// relative to its parent <b>window</b>, and <c>self</c> then shifts the grid image inside that window
/// (<c>GUIInventoryWindow.SetData</c>).</param>
/// <param name="Children">The sub-panels drawn with this one.</param>
public sealed record InventoryPanel(
    string? ContainerId,
    string DefName,
    string? SlotName,
    (int W, int H)? Grid,
    (double X, double Y)? Offset,
    (double X, double Y) SelfOffset,
    IReadOnlyList<InventoryPanel> Children)
{
    /// <summary>This panel plus every descendant, depth-first.</summary>
    public IEnumerable<InventoryPanel> Flatten()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var p in child.Flatten())
                yield return p;
    }
}

/// <summary>
/// Which contained items get a grid of their own drawn alongside their host, and where that grid sits — the port
/// of <c>GUIInventory.SpawnInventoryWindow</c>'s recursion over a condowner's slots (verified 1.0.0.13).
///
/// <para><b>The game has no unified inventory.</b> It opens one window per container, each with its own
/// <c>GridLayout</c>, and arranges them together. <c>SpawnInventoryWindow</c> walks <c>CO.GetSlots(bDeep: true)</c>
/// and spawns a window for every slotted child that passes <c>TIsOpenInInv</c> and holds anything. Where the host
/// declares a position for that slot in <c>dictSlotsLayout</c>, the child is parented to the host window, moved to
/// that offset and given <c>ToggleTab(false)</c>, which strips its tab, background and border; a child whose slot
/// has no entry gets an ordinary titled window beside the parent instead. Four bare pocket grids pinned under a
/// backpack's 4×4 is the first branch, and that is what reads in game as one unified inventory (#59).</para>
///
/// <para>So Ostraplan's model was never wrong: a pocket is a separate slotted container here exactly as it is
/// there. What differed was only that the view drew one level at a time, which is why a fully stocked EVA suit
/// opened on four empty boxes and none of its contents.</para>
/// </summary>
public static class InventoryLayout
{
    /// <summary>The trigger a contained item must pass to be drawn at all (<c>GUIInventory.CTOpenInv</c>). It
    /// forbids <c>IsHiddenInv</c> and <c>IsLocked</c>, so a concealed or locked compartment shows as a slot rather
    /// than opening its contents onto the host.</summary>
    public const string OpenInInvTrigger = "TIsOpenInInv";

    /// <summary>The key <c>dictSlotsLayout</c> uses for the host's OWN grid, as against one of its slots.</summary>
    public const string SelfKey = "self";

    /// <summary>The conversation slot, which <c>SpawnInventoryWindow</c> diverts to its own window and never draws
    /// as inventory.</summary>
    private const string SocialSlot = "social";

    /// <summary>
    /// How many <c>dictSlotsLayout</c> units make one grid cell.
    ///
    /// <para>The game's offsets live in a space where one cell is <c>(int)(24f * CanvasManager.CanvasRatio)</c>
    /// (<c>GUIInventoryWindow.PairXYFromLocalPoint</c>) and a child window is moved by
    /// <c>dictSlotsLayout[slot] * 1.5f * CanvasManager.CanvasRatio</c>. The canvas ratio cancels, so an offset is
    /// <c>layout × 1.5 ÷ 24</c> cells, whatever a cell is drawn at here. That makes the arrangement exact at any
    /// zoom rather than approximated.</para>
    ///
    /// <para><c>ItmBackpack01</c> is the worked example. Its four pockets sit at <c>y = -68</c>, which is 4.25
    /// cells below a 4×4 grid, so the pocket row clears the grid by a quarter of a cell; <c>x</c> 0/20/40/60 is
    /// 1.25 cells apart, so four 1×1 pouches sit in a row with the same quarter-cell gap between them. That is the
    /// row under the grid the issue's screenshots show in game.</para>
    /// </summary>
    public const double UnitsPerCell = 16.0;

    private static readonly string[] HiddenConds = ["IsHiddenInv", "IsLocked"];

    /// <summary>A <c>dictSlotsLayout</c> point converted to cells. The game's +y is up and WPF's is down, so the
    /// sign of y flips here and nowhere else.</summary>
    public static (double X, double Y) ToCells((double X, double Y) point) =>
        (point.X / UnitsPerCell, -point.Y / UnitsPerCell);

    /// <summary>
    /// True when a def's contents are shown rather than concealed — the game's <c>TIsOpenInInv</c> against the
    /// def's starting conds. A catalog with no condtrigs in it (the synthetic <c>Fixtures</c>) falls back to the
    /// two conds the trigger forbids, the same shape as <see cref="Catalog.IsVessel"/>.
    /// </summary>
    public static bool OpensInInventory(Catalog catalog, PartDef? def)
    {
        if (def is null) return false;
        return catalog.Triggers.TryGetValue(OpenInInvTrigger, out var ct)
            ? CondEval.Triggered(ct, def.StartingConds, catalog)
            : !def.StartingConds.Any(HiddenConds.Contains);
    }

    /// <summary>
    /// True when <paramref name="child"/>, slotted into <paramref name="slotName"/> on <paramref name="host"/>,
    /// has its grid drawn with the host rather than only on drilling into it.
    ///
    /// <para>Every clause is <c>SpawnInventoryWindow</c>'s: the slot is not hidden and is not the conversation
    /// slot, the child passes <c>TIsOpenInInv</c> and so does its host, and the child either has a container grid
    /// or declares a slot layout of its own. The last one is why a rifle's magazine does not open onto the rack it
    /// is in while a coat's pockets open onto the coat.</para>
    /// </summary>
    public static bool ShowsWithHost(Catalog catalog, PartDef? host, PartDef? child, string? slotName)
    {
        if (host is null || child is null || slotName is null) return false;
        if (string.Equals(slotName, SocialSlot, StringComparison.Ordinal)) return false;
        if (catalog.Slots.GetValueOrDefault(slotName)?.Hide == true) return false;
        if (!OpensInInventory(catalog, child) || !OpensInInventory(catalog, host)) return false;
        return child.IsContainer || child.SlotLayout.Count > 0;
    }

    /// <summary>The deepest a figure recurses. Core data nests two levels (a suit's compartment holding a pouch);
    /// the cap is only so a mod that nests without end cannot spin the renderer.</summary>
    private const int MaxDepth = 6;

    /// <summary>
    /// The figure for one host: its own grid, plus a panel for every slotted child that
    /// <see cref="ShowsWithHost"/> accepts, recursively.
    /// </summary>
    /// <param name="hostId">The cargo id of the host, or null when it is the window's own root.</param>
    public static InventoryPanel Compose(
        Catalog catalog, string hostDefName, string? hostId, IReadOnlyList<CargoItem> children) =>
        Compose(catalog, hostDefName, hostId, slotName: null, children, depth: 0);

    private static InventoryPanel Compose(
        Catalog catalog, string hostDefName, string? hostId, string? slotName,
        IReadOnlyList<CargoItem> children, int depth)
    {
        var host = catalog.Lookup(hostDefName);
        var kids = new List<InventoryPanel>();
        if (depth < MaxDepth)
            foreach (var child in children)
            {
                // A stack's members are copies of itself rather than cargo, so it is never a container to draw.
                if (!child.Slotted || child.IsStack) continue;
                if (!ShowsWithHost(catalog, host, catalog.Lookup(child.DefName), child.SlotName)) continue;
                var panel = Compose(catalog, child.DefName, child.StrID, child.SlotName, child.Children, depth + 1);
                kids.Add(panel with { Offset = OffsetOf(host, child.SlotName) });
            }
        return new InventoryPanel(
            hostId, hostDefName, slotName, host?.ContainerGrid, Offset: null, SelfOffset(host), kids);
    }

    /// <summary>Where the host puts the grid for <paramref name="slotName"/>, in cells, or null when it declares
    /// no position for it. Null is the game's second branch, an untethered window with a tab of its own.</summary>
    private static (double X, double Y)? OffsetOf(PartDef? host, string? slotName) =>
        host is not null && slotName is not null && host.SlotLayout.TryGetValue(slotName, out var pt)
            ? ToCells(pt)
            : null;

    /// <summary>Where a host puts its own grid within its figure (<c>dictSlotsLayout["self"]</c>), in cells.
    /// (0,0) when it names none, which is every def but the backpacks.</summary>
    private static (double X, double Y) SelfOffset(PartDef? host) =>
        host is not null && host.SlotLayout.TryGetValue(SelfKey, out var pt) ? ToCells(pt) : (0, 0);
}

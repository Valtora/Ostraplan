namespace Ostraplan.Core;

/// <summary>
/// Moving one drawable up or down the pile of things sharing a tile — the manual override over the automatic
/// render order (see <see cref="ShipDocument.RenderOrder"/>).
///
/// <para>The pile a nudge works in is <b>everything drawn on that tile in the same render layer</b>: a fixture is
/// never shuffled against the deck plate under it or the conduit over it, because those layers are the part of
/// the order that keeps floors from occluding what stands on them.</para>
///
/// <para>A nudge swaps the target with its neighbour and then <b>writes the resulting order out</b> as consecutive
/// <see cref="Placement.ZBias"/> values across that pile, rather than trying to shift one value and hope. Ties are
/// the normal case here (two fixtures sharing a row, both at bias 0), and a single shifted value can only break a
/// tie by jumping the target past everything else tied with it. The cost is that the neighbours in that pile also
/// get an explicit bias, which <see cref="Reset"/> exists to undo.</para>
/// </summary>
public static class ZOrder
{
    /// <summary>One drawable's bias before and after an operation. The command stack replays these to redo/undo.</summary>
    public readonly record struct BiasChange(RenderItem Item, int Before, int After);

    /// <summary>
    /// The pile <paramref name="item"/> is nudged within at (<paramref name="x"/>,<paramref name="y"/>), bottom to
    /// top: everything drawn on that tile that shares its render layer. Empty when the item isn't drawn there.
    /// </summary>
    public static IReadOnlyList<RenderItem> StackAt(ShipDocument doc, int x, int y, RenderItem item)
    {
        var layer = LayerOf(doc, item);
        var stack = doc.RenderStackAt(x, y).Where(i => LayerOf(doc, i) == layer).Reverse().ToList();
        return stack.Any(i => i.Id == item.Id) ? stack : [];
    }

    /// <summary>
    /// Move <paramref name="item"/> one step towards the viewer (<paramref name="forward"/>) or away from it,
    /// within the pile on (<paramref name="x"/>,<paramref name="y"/>). Returns the bias changes to apply, empty
    /// when it is already at that end of the pile (or alone there) so the caller can leave the menu entry disabled
    /// and push no undo step.
    /// </summary>
    public static IReadOnlyList<BiasChange> Nudge(ShipDocument doc, RenderItem item, int x, int y, bool forward)
    {
        var stack = StackAt(doc, x, y, item).ToList();
        var i = stack.FindIndex(e => e.Id == item.Id);
        var j = forward ? i + 1 : i - 1;
        if (i < 0 || j < 0 || j >= stack.Count) return [];

        (stack[i], stack[j]) = (stack[j], stack[i]);

        // Renumber from the pile's own floor, so repeated nudges shuffle within the same small range instead of
        // drifting further from 0 each time.
        var baseBias = stack.Min(e => e.ZBias);
        var changes = new List<BiasChange>();
        for (var k = 0; k < stack.Count; k++)
            if (stack[k].ZBias != baseBias + k)
                changes.Add(new BiasChange(stack[k], stack[k].ZBias, baseBias + k));
        return changes;
    }

    /// <summary>
    /// Clear the manual bias off the whole pile at (<paramref name="x"/>,<paramref name="y"/>), putting it back
    /// under the automatic order. It acts on the pile rather than the one item because a nudge writes biases
    /// across it, so clearing only the item you clicked would leave the rest pinned and the stack still manual.
    /// Empty when nothing there carries a bias.
    /// </summary>
    public static IReadOnlyList<BiasChange> Reset(ShipDocument doc, RenderItem item, int x, int y) =>
        [.. StackAt(doc, x, y, item).Where(e => e.ZBias != 0).Select(e => new BiasChange(e, e.ZBias, 0))];

    private static int LayerOf(ShipDocument doc, RenderItem item) =>
        doc.Catalog.RenderLayer(doc.Catalog.Lookup(item.DefName));
}

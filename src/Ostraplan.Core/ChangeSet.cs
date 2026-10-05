namespace Ostraplan.Core;

/// <summary>
/// What one or more steps of the undo stack touch (#76): the parts and loose items they change, and the tiles they
/// change with nothing selectable on them, which is a zone's paint. Filled by <see cref="IDocCommand.Touch"/>.
///
/// <para>It exists because an undo used to change the design and say nothing about where. A step that edits a
/// container's contents or a device's panel moves nothing on the plan at all, and one that touched something off
/// screen left the view exactly as it was. After an undo or a redo the window selects what is still there, marks
/// where the rest was, and frames the lot, all from <see cref="Resolve"/>.</para>
/// </summary>
public sealed class ChangeSet
{
    // Reference identity, which is what the commands hold: an undone removal puts back the same objects.
    private readonly HashSet<Placement> _parts = [];
    private readonly HashSet<LooseObject> _loose = [];
    private readonly HashSet<(int X, int Y)> _tiles = [];

    public void Add(Placement p) => _parts.Add(p);

    public void Add(IEnumerable<Placement> parts) => _parts.UnionWith(parts);

    public void Add(LooseObject o) => _loose.Add(o);

    public void Add(RenderItem item)
    {
        if (item.Placement is { } p) Add(p);
        else if (item.Loose is { } lo) Add(lo);
    }

    /// <summary>Tiles a step changed with nothing on them to select: a zone's paint.</summary>
    public void AddTiles(IEnumerable<(int X, int Y)> tiles) => _tiles.UnionWith(tiles);

    public bool IsEmpty => _parts.Count == 0 && _loose.Count == 0 && _tiles.Count == 0;

    /// <summary>Everything <paramref name="commands"/> touch, together: a jump down the history list is shown as one
    /// change rather than as its last step.</summary>
    public static ChangeSet Of(IEnumerable<IDocCommand> commands)
    {
        var set = new ChangeSet();
        foreach (var cmd in commands) cmd.Touch(set);
        return set;
    }

    /// <summary>
    /// Where this change lands on <paramref name="doc"/> as it now stands. A part or loose item still on the plan is
    /// one to select. One the step took off the plan cannot be selected, so its tiles are marked instead, from the
    /// pose it held when it left. Zone tiles are always marked.
    /// </summary>
    public ChangeView Resolve(ShipDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var parts = new List<Placement>();
        var loose = new List<LooseObject>();
        var marked = new HashSet<(int X, int Y)>(_tiles);
        var covered = new HashSet<(int X, int Y)>(_tiles);

        // One pass over the document for membership. Asking ById per part is a scan each, and an undone delete of
        // a whole deck is thousands of parts against a design of tens of thousands.
        if (_parts.Count > 0)
        {
            var onPlan = doc.Placements.Select(p => p.Id).ToHashSet();
            foreach (var p in _parts)
            {
                var tiles = ItemManifest.TilesOf(doc, new RenderItem(p, null));
                covered.UnionWith(tiles);
                if (onPlan.Contains(p.Id)) parts.Add(p);
                else marked.UnionWith(tiles);
            }
        }
        if (_loose.Count > 0)
        {
            var onPlan = doc.LooseObjects.Select(o => o.Id).ToHashSet();
            foreach (var o in _loose)
            {
                var tiles = doc.LooseTiles(o).ToList();
                covered.UnionWith(tiles);
                if (onPlan.Contains(o.Id)) loose.Add(o);
                else marked.UnionWith(tiles);
            }
        }
        return new ChangeView(parts, loose, marked, covered);
    }
}

/// <summary>
/// A <see cref="ChangeSet"/> placed on the document: the parts and loose items to select, the tiles to mark because
/// nothing on them can be selected, and every tile the change covers, which is what the view is framed on.
/// </summary>
public sealed record ChangeView(
    IReadOnlyList<Placement> Parts,
    IReadOnlyList<LooseObject> Loose,
    IReadOnlyCollection<(int X, int Y)> Marked,
    IReadOnlyCollection<(int X, int Y)> Covered);

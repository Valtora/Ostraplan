namespace Ostraplan.Core;

/// <summary>
/// Per-tile accumulated conditions - the planner's stand-in for the game's
/// Tile.coProps. Every placed part contributes the conditions of its per-cell
/// aSocketAdds loot (+1 per cond on install, -1 on removal - Ship.UpdateTiles).
/// This is what condtriggers (autotiling today, CheckFit in P1) test against.
/// </summary>
public sealed class TileConds
{
    private readonly Dictionary<(int X, int Y), Dictionary<string, double>> _tiles = new();
    private readonly Catalog _catalog;

    public TileConds(Catalog catalog) => _catalog = catalog;

    public void Apply(Placement p, ItemDef item, int sign)
    {
        var (w, h, adds) = GridMath.Rotate(item.SocketAdds, item.Width, item.Height, p.Rot);
        for (var r = 0; r < h; r++)
            for (var c = 0; c < w; c++)
            {
                if (r * w + c >= adds.Length) break;
                var lootName = adds[r * w + c];
                if (string.IsNullOrEmpty(lootName) || lootName == "Blank") continue;
                AddLoot((p.X + c, p.Y + r), lootName, sign, null);
            }
    }

    private void AddLoot((int, int) cell, string lootName, int sign, HashSet<string>? visited)
    {
        if (!_catalog.Loots.TryGetValue(lootName, out var loot)) return;
        visited ??= [];
        if (!visited.Add(lootName)) return;   // loot graphs can nest; never cycle
        foreach (var cond in loot.Conds)
            Add(cell, cond, sign);
        foreach (var child in loot.Loots)
            AddLoot(cell, child, sign, visited);
    }

    private void Add((int, int) cell, string cond, double amount)
    {
        if (!_tiles.TryGetValue(cell, out var conds))
        {
            if (amount <= 0) return;
            _tiles[cell] = conds = new Dictionary<string, double>(StringComparer.Ordinal);
        }
        var now = conds.GetValueOrDefault(cond) + amount;
        if (now > 0) conds[cond] = now;
        else
        {
            conds.Remove(cond);
            if (conds.Count == 0) _tiles.Remove(cell);
        }
    }

    /// <summary>
    /// Presence-level trigger test against a tile's accumulated conditions
    /// (Item.SetSpriteSheetIndex / CheckFit's use of CondTrigger.Triggered), honouring
    /// <c>bAND</c>: AND fires when every req is present and no forbid is; the <c>bAND=false</c>
    /// OR path fires when <b>any</b> req is present (no forbid) — the difference between a wall
    /// (<c>TIsWall</c>, one AND req) and a conduit (<c>TIsConduitSprite</c>, an OR of
    /// IsPowerConduit/Switch/Jack), so a conduit connects to any of them and autotiles as a
    /// straight run rather than always rendering as an isolated junction. The rare sprite-sheet
    /// trigger with nested <c>aTriggers</c>/<c>aTriggersForbid</c> defers to the full evaluator.
    /// </summary>
    public bool Triggered(CondTriggerDef ct, int x, int y)
    {
        _tiles.TryGetValue((x, y), out var conds);

        if (ct.Triggers.Length > 0 || ct.TriggersForbid.Length > 0)   // nested — hand to the full CondTrigger port
            return CondEval.Triggered(ct, conds is null ? [] : new HashSet<string>(conds.Keys, StringComparer.Ordinal), _catalog);

        if (conds is not null)
            foreach (var forbid in ct.Forbids)
                if (conds.ContainsKey(forbid)) return false;

        if (ct.Reqs.Length == 0) return true;   // blank / forbid-only trigger

        if (ct.BAnd)
        {
            foreach (var req in ct.Reqs)
                if (conds is null || !conds.ContainsKey(req)) return false;
            return true;
        }

        // OR (bAND == false): any single req present
        if (conds is null) return false;
        foreach (var req in ct.Reqs)
            if (conds.ContainsKey(req)) return true;
        return false;
    }

    public bool TriggeredByName(string ctName, int x, int y) =>
        _catalog.Triggers.TryGetValue(ctName, out var ct) && Triggered(ct, x, y);

    public IReadOnlyDictionary<string, double>? At(int x, int y) =>
        _tiles.GetValueOrDefault((x, y));

    /// <summary>Every tile that currently carries at least one condition.</summary>
    public IEnumerable<(int X, int Y)> Cells => _tiles.Keys;

    public void Clear() => _tiles.Clear();
}

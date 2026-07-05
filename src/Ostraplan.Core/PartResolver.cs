using System.Collections.Concurrent;
using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>
/// A placed condowner resolved to everything the analysis engine needs: its item
/// geometry/sockets, its starting conditions (names + values), and its map points.
/// </summary>
public sealed record ResolvedPart(
    string DefName,
    string Friendly,
    ItemDef Item,
    string[] StartingConds,
    IReadOnlyDictionary<string, double> StartingCondValues,
    IReadOnlyDictionary<string, (double X, double Y)> MapPoints)
{
    private HashSet<string>? _condSet;
    /// <summary>Starting-condition names as a set (for CondTrigger evaluation / IsFloorGrate tests).</summary>
    public HashSet<string> CondSet => _condSet ??= new HashSet<string>(StartingConds, StringComparer.Ordinal);

    public bool Has(string cond) => CondSet.Contains(cond);
}

/// <summary>
/// Resolves <b>any</b> placed condowner name to its geometry and conditions —
/// buildable or not. Ship templates reference far more than the install menu
/// (raw hull, walls, ship-special systems, tiles), and rooms/rating need every
/// one of them. Mirrors the palette join (<see cref="Catalog"/>): strStartInstall/
/// placed name → condowner (directly or via a cooverlay's strCOBase) → strItemDef →
/// items. Cached by name; safe to share across threads.
/// </summary>
public sealed class PartResolver
{
    private readonly IReadOnlyDictionary<string, (JsonElement El, string Origin)> _items;
    private readonly IReadOnlyDictionary<string, (JsonElement El, string Origin)> _owners;
    private readonly IReadOnlyDictionary<string, (JsonElement El, string Origin)> _overlays;
    private readonly ConcurrentDictionary<string, ResolvedPart?> _cache = new(StringComparer.Ordinal);

    public PartResolver(DataIndex index)
    {
        _items = index.Type("items");
        _owners = index.Type("condowners");
        _overlays = index.Type("cooverlays");
    }

    /// <summary>Resolve a placed condowner name, or null if its item geometry can't be found.</summary>
    public ResolvedPart? Resolve(string? coName)
    {
        if (string.IsNullOrEmpty(coName)) return null;
        return _cache.GetOrAdd(coName, Build);
    }

    private ResolvedPart? Build(string name)
    {
        // name → real condowner, directly or through a cooverlay skin (DataHandler.LoadCO fallback)
        CondOwnerDef? co = null;
        CoOverlayDef? overlay = null;
        if (_overlays.TryGetValue(name, out var ov))
        {
            overlay = CoOverlayDef.Parse(ov.El);
            var baseName = string.IsNullOrWhiteSpace(overlay.COBase) ? name : overlay.COBase!;
            if (_owners.TryGetValue(baseName, out var baseCo)) co = CondOwnerDef.Parse(baseCo.El);
        }
        else if (_owners.TryGetValue(name, out var direct))
        {
            co = CondOwnerDef.Parse(direct.El);
        }

        // item def: the condowner's strItemDef, else the placed name itself (some items place directly)
        var itemName = string.IsNullOrWhiteSpace(co?.ItemDefName) ? name : co!.ItemDefName!;
        if (!_items.TryGetValue(itemName, out var itemRaw)
            && !_items.TryGetValue(name, out itemRaw))   // fall back to the placed name as an item def
            return null;

        var item = ItemDef.Parse(itemRaw.El);
        var friendly =
            !string.IsNullOrWhiteSpace(overlay?.NameFriendly) ? overlay!.NameFriendly! :
            !string.IsNullOrWhiteSpace(co?.NameFriendly) ? co!.NameFriendly! : name;

        return new ResolvedPart(
            name, friendly, item,
            co?.StartingCondNames ?? [],
            co?.StartingCondValues ?? new Dictionary<string, double>(),
            co?.MapPoints ?? new Dictionary<string, (double, double)>());
    }
}

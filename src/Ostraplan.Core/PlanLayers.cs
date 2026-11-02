namespace Ostraplan.Core;

/// <summary>
/// The rows of the Layers panel: the four kinds of deck element (<see cref="Catalog.RenderLayer"/>), then the two
/// kinds of thing lying on the deck rather than built into it. Ordered top of the panel to bottom.
/// </summary>
public enum PlanLayer
{
    Floors,
    Walls,
    Fixtures,
    Conduits,
    Loose,
    Spawners,
}

/// <summary>
/// Which layers of the plan are hidden and which are locked: the Layers panel's state, and the one rule every pick
/// on the canvas goes through.
///
/// <para><b>Hidden</b> takes a layer off the plan and out of the way of the mouse. <b>Locked</b> leaves it drawn
/// but out of the way of the mouse, which is how a floor under an airlock is clicked without hiding the airlock.
/// Either way the layer cannot be selected, box-selected, erased or picked up with the eyedropper, so nothing you
/// cannot reach can be changed by a gesture. Neither changes the design: the placement law still sees a hidden
/// wall, an export still writes it, and the mod's ship portrait still draws it.</para>
///
/// <para>Immutable, so a canvas can hand its state to the window and the window can hand it to settings without
/// either worrying about the other changing it underneath them.</para>
/// </summary>
public sealed class LayerView
{
    /// <summary>Every layer shown and unlocked: the plan as it always was.</summary>
    public static readonly LayerView Default = new([], []);

    private readonly HashSet<PlanLayer> _hidden;
    private readonly HashSet<PlanLayer> _locked;

    public LayerView(IEnumerable<PlanLayer> hidden, IEnumerable<PlanLayer> locked)
    {
        _hidden = [.. hidden];
        _locked = [.. locked];
    }

    public bool IsHidden(PlanLayer layer) => _hidden.Contains(layer);
    public bool IsLocked(PlanLayer layer) => _locked.Contains(layer);

    /// <summary>True when the mouse can reach this layer: shown and unlocked.</summary>
    public bool IsPickable(PlanLayer layer) => !_hidden.Contains(layer) && !_locked.Contains(layer);

    /// <summary>True while every layer is shown and unlocked.</summary>
    public bool IsDefault => _hidden.Count == 0 && _locked.Count == 0;

    public IReadOnlyCollection<PlanLayer> Hidden => _hidden;
    public IReadOnlyCollection<PlanLayer> Locked => _locked;

    public LayerView WithHidden(PlanLayer layer, bool hidden) => With(_hidden, layer, hidden, h => new LayerView(h, _locked));
    public LayerView WithLocked(PlanLayer layer, bool locked) => With(_locked, layer, locked, l => new LayerView(_hidden, l));

    private LayerView With(HashSet<PlanLayer> set, PlanLayer layer, bool on, Func<HashSet<PlanLayer>, LayerView> make)
    {
        if (set.Contains(layer) == on) return this;
        var next = new HashSet<PlanLayer>(set);
        if (on) next.Add(layer); else next.Remove(layer);
        return make(next);
    }

    /// <summary>
    /// One line saying what is off, for the status-bar marker and the bug-report header: "Walls hidden, Floors
    /// locked". Null while nothing is. A layer both hidden and locked is reported as hidden, the state you can see.
    /// </summary>
    public string? Summary()
    {
        if (IsDefault) return null;
        var parts = Enum.GetValues<PlanLayer>()
            .Where(l => _hidden.Contains(l) || _locked.Contains(l))
            .Select(l => $"{Name(l)} {(_hidden.Contains(l) ? "hidden" : "locked")}");
        return string.Join(", ", parts);
    }

    /// <summary>The name a layer goes by in the panel, the right-click filter and the status bar.</summary>
    public static string Name(PlanLayer layer) => layer switch
    {
        PlanLayer.Floors => "Floors",
        PlanLayer.Walls => "Walls & doors",
        PlanLayer.Fixtures => "Fixtures",
        PlanLayer.Conduits => "Conduits",
        PlanLayer.Loose => "Loose items",
        PlanLayer.Spawners => "Spawners",
        _ => layer.ToString(),
    };

    /// <summary>The layer an installed part belongs to, from its render layer.</summary>
    public static PlanLayer Of(Catalog catalog, Placement p) => catalog.RenderLayer(catalog.Lookup(p.DefName)) switch
    {
        Catalog.LayerFloor => PlanLayer.Floors,
        Catalog.LayerWall => PlanLayer.Walls,
        Catalog.LayerConduit => PlanLayer.Conduits,
        _ => PlanLayer.Fixtures,
    };

    /// <summary>The layer a deck item belongs to: a spawner is an editor object standing for cargo, so it has a row
    /// of its own, the same split the box-select filter makes.</summary>
    public static PlanLayer Of(LooseObject o) => o.Spawner is null ? PlanLayer.Loose : PlanLayer.Spawners;

    public static PlanLayer Of(Catalog catalog, RenderItem item) =>
        item.Placement is { } p ? Of(catalog, p) : Of(item.Loose!);

    /// <summary>Read the persisted state. Names this build does not know (a layer a newer build added) are
    /// skipped rather than failing the load.</summary>
    public static LayerView Parse(IEnumerable<string>? hidden, IEnumerable<string>? locked)
    {
        static IEnumerable<PlanLayer> Read(IEnumerable<string>? names) =>
            (names ?? []).Select(n => Enum.TryParse<PlanLayer>(n, out var l) ? (PlanLayer?)l : null)
                .Where(l => l is not null).Select(l => l!.Value);
        return new LayerView(Read(hidden), Read(locked));
    }
}

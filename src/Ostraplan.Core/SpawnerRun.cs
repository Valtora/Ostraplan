namespace Ostraplan.Core;

/// <summary>
/// Which of the three ways the game may instantiate a ship a run is rolling for. A spawner carries a flag per
/// case (<see cref="SpawnerSettings.WhenNew"/> and its two siblings), which is how one template yields a clean
/// ship with supplies and a wreck strewn with scrap, so the answer decides which spawners fire at all.
/// </summary>
public enum ShipCondition
{
    /// <summary>The ship as the game builds it new. The case a design is normally written for.</summary>
    New = 0,

    /// <summary>Instantiated damaged. <c>Ship.Damage.Used</c> reads the same flag in the game.</summary>
    Damaged = 1,

    /// <summary>Instantiated derelict, which is how a wreck gets its scrap.</summary>
    Derelict = 2,
}

/// <summary>An existing deck item a run added to rather than laying a fresh one beside it.</summary>
/// <param name="Obj">The item on the deck whose stack grew.</param>
/// <param name="Before">Its quantity before the run.</param>
/// <param name="After">Its quantity after.</param>
public sealed record SpawnerStack(LooseObject Obj, int Before, int After);

/// <summary>
/// What running a set of spawners would do to the design. Nothing here has been applied: hand
/// <see cref="ToCommand"/> to the command stack to make it so, which is what keeps it undoable in one step.
/// </summary>
/// <param name="Seed">The seed the run used. Re-entering it reproduces the run exactly, which is the point of
/// reporting it even when the user did not choose it.</param>
/// <param name="Placed">New deck items, in the order they were laid.</param>
/// <param name="Stacked">Existing deck items whose quantity the run raised.</param>
/// <param name="Consumed">The spawners that fired, which the run removes (see <see cref="SpawnerRun"/>).</param>
/// <param name="Rolled">How many items the loot tables named in total, before any of them were placed.</param>
/// <param name="NoRoom">Rolled items with nowhere to go inside the scatter square. The game destroys these
/// outright (<c>LootSpawner.DoLoot</c> walks the leftovers from <c>DropCOsNearby</c> and calls
/// <c>Destroy</c>), so they are lost here too and only counted.</param>
/// <param name="Unfired">Spawners left alone because their flag for this <see cref="ShipCondition"/> is off. They
/// are not consumed: they still have work to do in the case they are written for.</param>
/// <param name="MissingTables">Loot tables the roll named that this install does not declare.</param>
/// <param name="MissingItems">Item defs the roll named that this install cannot resolve, so nothing could be laid
/// for them. Distinct from <see cref="NoRoom"/>, which is about space rather than data.</param>
/// <param name="Truncated">The roll hit <see cref="LootRoll.MaxItems"/> or <see cref="LootRoll.MaxDepth"/>.</param>
public sealed record SpawnerRunPlan(
    int Seed,
    IReadOnlyList<LooseObject> Placed,
    IReadOnlyList<SpawnerStack> Stacked,
    IReadOnlyList<LooseObject> Consumed,
    int Rolled,
    int NoRoom,
    IReadOnlyList<LooseObject> Unfired,
    IReadOnlyList<string> MissingTables,
    IReadOnlyList<string> MissingItems,
    bool Truncated)
{
    /// <summary>True when the run would change nothing, so the caller can say so rather than pushing an empty
    /// step onto the undo stack. A spawner that rolls nothing is a real outcome, not a failure.</summary>
    public bool IsEmpty => Placed.Count == 0 && Stacked.Count == 0 && Consumed.Count == 0;

    /// <summary>How many items the run actually put on the deck, stack members included.</summary>
    public int Delivered => Placed.Sum(o => o.Quantity) + Stacked.Sum(s => s.After - s.Before);

    /// <summary>
    /// The whole run as one undoable step: the spawners come out, the cargo goes in, and any stack it grew is
    /// retuned. Removals go first so a spawner's own tile is free by the time its cargo lands on it, which with
    /// the commonest scatter range of 0 is the only tile there is.
    /// </summary>
    public IDocCommand ToCommand()
    {
        var steps = new List<IDocCommand>(Consumed.Count + Placed.Count + Stacked.Count);
        foreach (var spawner in Consumed) steps.Add(new RemoveLooseCommand(spawner));
        foreach (var stack in Stacked) steps.Add(new SetLooseQuantityCommand(stack.Obj, stack.Before, stack.After));
        foreach (var item in Placed) steps.Add(new PlaceLooseCommand(item));
        return new CompositeCommand(steps);
    }
}

/// <summary>
/// Running a loot spawner: rolling what it would make and laying that on the deck as ordinary cargo, so a design
/// can hold the items themselves rather than the editor object that stands for them (#65).
///
/// <para><b>A spawner that fires is consumed.</b> The alternative is a design that carries both the rolled cargo
/// and the spawner that rolled it, which exports a ship that arrives with the cargo twice. Undo puts the spawner
/// back exactly where it was, so the run is a conversion rather than a commitment.</para>
///
/// <para><b>Only object spawners can be run.</b> <see cref="SpawnerType.Pspec"/> and
/// <see cref="SpawnerType.PspecLoot"/> make people, and a person is not something a design holds: crew are never
/// imported and never exported as cargo. The reporter drew the same line ("at least for those that actually spawn
/// loose items rather than npcs").</para>
///
/// <para><b>The placement is Ostraplan's, the roll is the game's.</b> <see cref="LootRoll"/> is a faithful port of
/// <c>Loot.GetCOLoot</c>, so what a run names is what the game would name. Where it lands is a planner's answer
/// rather than a port: the game's <c>TileUtils.DropCOsNearby</c> also pushes items into containers standing in the
/// zone and walks a stack-merge over every nearby object, and reproducing that would mean reproducing container
/// overflow rules that have nothing to do with a design. What is kept is the part that shows: the shuffled
/// scatter square, the stack merge onto an item of the same def, and the game's own rule that anything with
/// nowhere to go is destroyed rather than piled up.</para>
///
/// <para><b>A spawner routinely rolls more than its square can hold, and that is the game's behaviour too.</b>
/// <c>TryFitItem</c> walks the zone running <c>Item.CheckFit</c>, whose <c>TILItemForbids</c> mask names
/// <c>IsItemTile</c>, so the game refuses a tile another object already claims and destroys what it could not
/// place. With a scatter range of 0, which is 2,849 of the 3,631 spawners the game's own ships carry, that means
/// one unstacked object and the remainder lost. It is worth reporting rather than hiding: this is the one place
/// where the number a table names and the number a ship ends up with come apart.</para>
/// </summary>
public static class SpawnerRun
{
    /// <summary>
    /// The deck items in <paramref name="doc"/> that a run could do something with: object spawners, and only
    /// those pointed at something. A spawner still on the template default
    /// (<see cref="SpawnerSettings.DefaultTarget"/>) names a real loot entry that yields nothing, so running it is
    /// a no-op worth keeping out of the offer.
    /// </summary>
    public static IReadOnlyList<LooseObject> Runnable(ShipDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return doc.LooseObjects.Where(IsRunnable).ToList();
    }

    /// <inheritdoc cref="Runnable(ShipDocument)"/>
    public static bool IsRunnable(LooseObject o) =>
        o.Spawner is { } s && !s.IsPersonSpawn && s.Target != SpawnerSettings.DefaultTarget
        && !string.IsNullOrWhiteSpace(s.Target) && s.Count > 0;

    /// <summary>
    /// Work out what running <paramref name="spawners"/> would do, without touching the document. Apply it with
    /// <see cref="SpawnerRunPlan.ToCommand"/>.
    /// </summary>
    /// <param name="condition">Which instantiation of the ship to roll for. A spawner whose flag for this case is
    /// off does not fire and is left in place.</param>
    /// <param name="seed">The seed to reproduce a previous run, or null to take a fresh one. Either way the seed
    /// used comes back on the plan.</param>
    public static SpawnerRunPlan Plan(
        ShipDocument doc, Catalog catalog, IReadOnlyList<LooseObject> spawners,
        ShipCondition condition = ShipCondition.New, int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(spawners);

        var used = seed ?? Random.Shared.Next();
        var rng = new Random(used);

        var firing = new List<LooseObject>();
        var unfired = new List<LooseObject>();
        foreach (var s in spawners)
        {
            if (!IsRunnable(s)) continue;
            (s.Spawner!.FiresWhen(condition) ? firing : unfired).Add(s);
        }

        var placed = new List<LooseObject>();
        var stacked = new Dictionary<Guid, SpawnerStack>();
        var missingTables = new List<string>();
        var missingItems = new List<string>();
        var claimed = new HashSet<(int X, int Y)>();
        var rolled = 0;
        var noRoom = 0;
        var truncated = false;

        foreach (var spawner in firing)
        {
            var settings = spawner.Spawner!;
            var roll = LootRoll.Roll(catalog, settings.Target, settings.Count, rng);
            rolled += roll.Names.Count;
            truncated |= roll.Truncated;
            foreach (var table in roll.MissingTables)
                if (!missingTables.Contains(table, StringComparer.Ordinal)) missingTables.Add(table);

            // The game shuffles the zone before walking it, so two spawners with the same settings do not both
            // fill from the same corner. The shuffle draws from the run's own rng, which keeps the whole run
            // reproducible from the one seed.
            var zone = settings.ScatterTiles(spawner.X, spawner.Y).ToList();
            Shuffle(zone, rng);

            foreach (var defName in roll.Names)
            {
                if (catalog.Lookup(defName) is not { } def)
                {
                    if (!missingItems.Contains(defName, StringComparer.Ordinal)) missingItems.Add(defName);
                    continue;
                }

                if (TryStack(defName, def, zone)) continue;
                if (TryLay(defName, def, zone, spawner)) continue;
                noRoom++;
            }
        }

        return new SpawnerRunPlan(
            used, placed, stacked.Values.ToList(), firing, rolled, noRoom, unfired,
            missingTables, missingItems, truncated);

        // The stack merge, kept from the game's DropCOsNearby: an incoming object joins one of the same def that
        // is already in the zone rather than claiming a tile of its own. Items this run has laid count as much as
        // ones that were already there, which is what makes a spawner rolling twenty rounds of ammo produce one
        // stack of twenty instead of twenty objects fighting over the scatter square.
        bool TryStack(string defName, PartDef def, List<(int X, int Y)> zone)
        {
            if (def.StackLimit <= 1) return false;
            foreach (var (x, y) in zone)
            {
                // One this run laid. It is not in the document yet, so its quantity is edited in place and the
                // single PlaceLooseCommand carries the whole stack.
                if (placed.FirstOrDefault(p => p.DefName == defName && p.X == x && p.Y == y) is { } mine)
                {
                    if (mine.Quantity >= def.StackLimit) continue;
                    mine.Quantity++;
                    return true;
                }

                // One that was already on the deck. It is recorded as a quantity change instead, so undo returns
                // it to the number the design had rather than removing an object that was always there.
                if (doc.LooseAt(x, y) is not { } host) continue;
                if (host.DefName != defName || host.Spawner is not null) continue;
                var before = stacked.TryGetValue(host.Id, out var s) ? s.Before : host.Quantity;
                var after = stacked.TryGetValue(host.Id, out var t) ? t.After : host.Quantity;
                if (after >= def.StackLimit) continue;
                stacked[host.Id] = new SpawnerStack(host, before, after + 1);
                return true;
            }
            return false;
        }

        // The first tile of the shuffled zone the item actually fits on. The spawners being consumed are lifted
        // out of the test: with a range of 0 the spawner's own tile is the only candidate there is.
        bool TryLay(string defName, PartDef def, List<(int X, int Y)> zone, LooseObject spawner)
        {
            foreach (var (x, y) in zone)
            {
                var tiles = ShipDocument.LooseTiles(def, x, y, 0).ToList();
                if (tiles.Any(claimed.Contains)) continue;
                if (!LoosePlacement.Check(doc, def, x, y, 0, self: spawner, leaving: firing).Ok) continue;
                foreach (var t in tiles) claimed.Add(t);
                placed.Add(new LooseObject { DefName = defName, X = x, Y = y });
                return true;
            }
            return false;
        }
    }

    /// <summary>Fisher-Yates over the run's own random source, matching <c>MathUtils.ShuffleArray</c>.</summary>
    private static void Shuffle(List<(int X, int Y)> tiles, Random rng)
    {
        for (var i = tiles.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (tiles[i], tiles[j]) = (tiles[j], tiles[i]);
        }
    }
}

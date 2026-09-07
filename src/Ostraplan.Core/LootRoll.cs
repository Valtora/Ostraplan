namespace Ostraplan.Core;

/// <summary>
/// One alternative inside a loot's unit group: <c>ItmScrapTrash=0.1x1-2</c> is a 10% chance of one or two pieces
/// of scrap. The game's own <c>LootUnit</c>, parsed by <c>Loot.ParseLootDef</c>.
///
/// <para>A group is one string of the <c>aCOs</c> / <c>aLoots</c> array, its alternatives separated by <c>|</c>.
/// The chances inside a group are <b>cumulative rather than independent</b>: one number is drawn for the whole
/// group and the first alternative whose running total covers it wins, so
/// <c>ItmRandomEngineeringLoot=0.8x1|ItmScrapTrash=0.1x1-2</c> is 80% good salvage, 10% junk and 10% nothing at
/// all. Two separate strings in the array are two independent draws.</para>
/// </summary>
/// <param name="Name">What the alternative names: an item def in an <c>aCOs</c> group, another loot table in an
/// <c>aLoots</c> one.</param>
/// <param name="Chance">This alternative's share of the group's 0..1 draw.</param>
/// <param name="Min">Low end of the count, the number after the <c>x</c>.</param>
/// <param name="Max">High end, from a <c>1-2</c> range. Equal to <see cref="Min"/> when the entry names one
/// number.</param>
/// <param name="Positive">False for an entry the group wrote with a leading <c>-</c>. It changes nothing on this
/// path (see <see cref="LootRoll"/>), and no item loot in the game's data uses one.</param>
public sealed record LootUnit(string Name, double Chance, double Min, double Max, bool Positive)
{
    /// <summary>
    /// Parse an <c>aCOs</c> or <c>aLoots</c> array into its groups. Ported from <c>Loot.ParseLootDef</c>, including
    /// its two oddities: an entry with no <c>=</c> is dropped rather than read as a certainty, and the leading
    /// <c>-</c> is tested on the <b>whole group string</b> while the character it strips comes off <b>each</b>
    /// alternative in that group. So a group beginning with a minus loses the first character of every entry after
    /// the first one too. That is the game's behaviour and a mod's data would be read the same way here.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<LootUnit>> ParseGroups(IReadOnlyList<string>? entries)
    {
        if (entries is null || entries.Count == 0) return [];
        var groups = new List<IReadOnlyList<LootUnit>>(entries.Count);
        foreach (var raw in entries)
        {
            if (raw is null or { Length: 0 }) continue;
            var negated = raw[0] == '-';
            var units = new List<LootUnit>();
            foreach (var alternative in raw.Split('|'))
            {
                var text = negated ? alternative[Math.Min(1, alternative.Length)..] : alternative;
                var eq = text.IndexOf('=');
                if (eq < 0) continue;                       // "shorter than expected": the game logs and skips it
                var name = text[..eq];
                var rest = text[(eq + 1)..];
                var x = rest.IndexOf('x');
                if (!double.TryParse(x < 0 ? rest : rest[..x], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var chance) || chance == 0)
                    continue;                               // a zero chance is dropped, not kept as an impossibility
                var counts = (x < 0 ? "" : rest[(x + 1)..]).Split('-');
                var min = Num(counts.Length > 0 ? counts[0] : "");
                var max = counts.Length > 1 ? Num(counts[1]) : 0;
                units.Add(new LootUnit(name, chance, min, Math.Max(min, max), !negated));
            }
            groups.Add(units);
        }
        return groups;

        static double Num(string s) =>
            double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

/// <summary>What one roll of a loot table produced.</summary>
/// <param name="Names">Every item def the roll named, one entry per copy and in the order the game produces
/// them.</param>
/// <param name="MissingTables">Loot tables the roll referenced that this install does not declare. Reported
/// rather than thrown: a design can name a table from a mod that is no longer enabled.</param>
/// <param name="Truncated">True when the recursion hit <see cref="LootRoll.MaxItems"/> or
/// <see cref="LootRoll.MaxDepth"/> and stopped early.</param>
public sealed record LootRollResult(
    IReadOnlyList<string> Names, IReadOnlyList<string> MissingTables, bool Truncated);

/// <summary>
/// The game's loot roll, ported from <c>Loot.GetCOLoot</c>: given a <c>data/loot</c> table, which item defs it
/// makes this time. This is what a <c>SysLootSpawner</c> runs when the game instantiates a ship, and porting it is
/// what lets a design turn a spawner into the cargo it stands for (#65).
///
/// <para><b>Two flags on the game's <c>Loot</c> are deliberately not ported.</b> <c>bNested</c> (which would put
/// each rolled object inside an earlier one) and <c>bSuppress</c> (which suppresses a def's condition overrides)
/// are declared on the class and read in <c>GetCOLoot</c>, but nothing in the shipped assembly ever assigns
/// either, and <c>JsonLoot</c> carries no field for them. They are dead in the build this was read against, so
/// reproducing them here would be reproducing an intent rather than a behaviour.</para>
///
/// <para><b>The double negation is faithful.</b> <c>LootUnit.GetAmount</c> negates the amount for an entry
/// written with a leading <c>-</c>, and <c>GetCOLoot</c> then negates it a second time, so the two cancel and a
/// negative entry yields an ordinary positive count. It is reproduced rather than tidied because a mod could
/// author one; no item loot in the game's own data does.</para>
///
/// <para><b>The two arrays round differently, and that is the game's own asymmetry.</b> An <c>aCOs</c> count is
/// floored (<c>1-2</c> yields one item most of the time), while an <c>aLoots</c> count drives a
/// <c>for (j = 0; j &lt; amount; j++)</c> loop, so 1.5 recurses twice. Both are ported as written.</para>
/// </summary>
public static class LootRoll
{
    /// <summary>How deep <c>aLoots</c> recursion may go. The game has no limit at all and would hang on a cyclic
    /// table; nothing in core data is cyclic, but a mod's could be, and a planner must not lock up over it.</summary>
    public const int MaxDepth = 16;

    /// <summary>The most items one roll may produce, for the same reason as <see cref="MaxDepth"/>. Far above
    /// anything the game's own tables reach: the widest core spawner target tops out in the low tens.</summary>
    public const int MaxItems = 4096;

    /// <summary>
    /// Roll <paramref name="target"/> <paramref name="count"/> times, exactly as <c>LootSpawner.DoLoot</c> does
    /// for a spawner's <c>strCount</c>. A count of zero or less makes nothing, which is a legitimate thing to
    /// author on a template that only fills when damaged.
    /// </summary>
    /// <param name="rng">The run's random source. Seeded by the caller so a result can be reproduced.</param>
    public static LootRollResult Roll(Catalog catalog, string? target, int count, Random rng)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(rng);

        var names = new List<string>();
        var missing = new List<string>();
        var truncated = false;
        if (!string.IsNullOrWhiteSpace(target))
            for (var i = 0; i < count && !truncated; i++)
                Gather(target, 0);
        return new LootRollResult(names, missing, truncated);

        void Gather(string name, int depth)
        {
            if (truncated) return;
            if (depth > MaxDepth) { truncated = true; return; }
            if (!catalog.Loots.TryGetValue(name, out var loot))
            {
                if (!missing.Contains(name, StringComparer.Ordinal)) missing.Add(name);
                return;
            }

            // aCOs: each group names items directly, and the count is floored.
            foreach (var group in loot.CoUnits)
                foreach (var unit in Draw(group))
                {
                    var copies = (int)Math.Floor(unit.Amount);
                    for (var i = 0; i < copies; i++)
                    {
                        if (names.Count >= MaxItems) { truncated = true; return; }
                        names.Add(unit.Unit.Name);
                    }
                }

            // aLoots: each group names another table, and the count is walked rather than floored.
            foreach (var group in loot.LootUnits)
                foreach (var unit in Draw(group))
                    for (var j = 0; j < unit.Amount; j++)
                    {
                        Gather(unit.Unit.Name, depth + 1);
                        if (truncated) return;
                    }
        }

        // One draw per group, the first alternative whose cumulative share covers it winning outright. Yields at
        // most one unit, and none when the draw fell past every alternative (the "nothing at all" case that the
        // chances in a group deliberately leave room for).
        IEnumerable<(LootUnit Unit, double Amount)> Draw(IReadOnlyList<LootUnit> group)
        {
            var running = 0.0;
            var roll = rng.NextDouble();
            foreach (var unit in group)
            {
                var amount = roll > running + unit.Chance ? 0 : Between(unit.Min, unit.Max);
                running += unit.Chance;
                if (amount <= 0) continue;
                yield return (unit, amount);
                yield break;
            }
        }

        double Between(double min, double max) => min + rng.NextDouble() * (max - min);
    }
}

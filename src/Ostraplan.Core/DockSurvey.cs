namespace Ostraplan.Core;

/// <summary>One stock ship measured against the design. <paramref name="Mated"/> runs parallel to
/// <see cref="DockSurveyResult.Ports"/>, one entry per port on the design.</summary>
public sealed record DockSurveyShip(
    string ShipName, string Origin, string Path, DockPort Primary, IReadOnlyList<bool> Mated)
{
    public int MateCount => Mated.Count(m => m);
}

/// <summary>
/// The design's ports against every stock primary airlock in the install.
/// </summary>
/// <param name="Ports">The design's own ports, in document order.</param>
/// <param name="Ships">One row per ship that carries a primary port, in name order.</param>
/// <param name="Skipped">Ships that were read but carry no primary port, so nothing could be measured
/// against them. 42 core templates carry no docking port at all.</param>
public sealed record DockSurveyResult(
    IReadOnlyList<DockPort> Ports, IReadOnlyList<DockSurveyShip> Ships, int Skipped)
{
    /// <summary>How many of the surveyed ships one of the design's ports can mate with.</summary>
    public int MateCount(int portIndex) => Ships.Count(s => s.Mated[portIndex]);
}

/// <summary>
/// Answers the half of the request that the game has no answer for: whether a Secondary airlock "can dock with
/// a primary dock in general".
///
/// <para>The game only ever answers pairwise, because the answer depends on both hulls rather than on the two
/// ports (see <see cref="DockMating"/>). So "in general" is defined here as a survey: run the design's ports
/// against the <b>primary</b> airlock of every ship the install ships, and report what fraction accept it. That
/// invents no rule and reads no data the game does not already carry, and a port that mates with none of them
/// is one that will not mate with anything.</para>
///
/// <para>The design is always the <b>incoming</b> ship and each stock ship the receiver, which is the way round
/// a player meets it and the same convention the pairwise report uses.</para>
/// </summary>
public static class DockSurvey
{
    /// <summary>
    /// Sweep every ship template in the install. <paramref name="progress"/> is reported as (done, total) so a
    /// caller can drive a progress bar; <paramref name="cancel"/> aborts between ships.
    /// </summary>
    public static DockSurveyResult Run(
        DockShip design, DataIndex index, Catalog catalog,
        Action<int, int>? progress = null, CancellationToken cancel = default)
    {
        var lookup = DockDefs.For(catalog);
        var files = TemplateImport.ListShipFiles(index);
        var ships = new List<DockSurveyShip>();
        var skipped = 0;
        var done = 0;

        foreach (var entry in files)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Invoke(done++, files.Count);

            foreach (var tmpl in ReadTemplates(entry.Path))
            {
                var receiver = DockShip.FromTemplate(tmpl, catalog, lookup);
                // The Primary is the non-TypeB port. A ship carrying only Secondaries has no primary to
                // survey against, and is counted as skipped rather than as a refusal.
                if (receiver.Ports.FirstOrDefault(p => !p.TypeB) is not { } primary)
                {
                    skipped++;
                    continue;
                }
                ships.Add(new DockSurveyShip(receiver.Name, entry.Origin, entry.Path, primary,
                    [.. design.Ports.Select(p => DockMating.Mate(receiver, design, primary, p).Mates)]));
            }
        }

        progress?.Invoke(files.Count, files.Count);
        return new DockSurveyResult(design.Ports,
            [.. ships.OrderBy(s => s.ShipName, StringComparer.OrdinalIgnoreCase)], skipped);
    }

    /// <summary>Re-run one row so its blocking cells and its pose can be shown. The survey stores verdicts
    /// alone, because holding every collision for 162 ships times every airlock is a lot of memory for a list
    /// nobody has asked to see yet. The receiver comes back with the verdict because drawing the pose needs it.</summary>
    public static (DockMate Mate, DockShip? Receiver) Explain(
        DockShip design, DockSurveyShip row, DockPort designPort, Catalog catalog)
    {
        var lookup = DockDefs.For(catalog);
        foreach (var tmpl in ReadTemplates(row.Path))
        {
            var receiver = DockShip.FromTemplate(tmpl, catalog, lookup);
            if (receiver.Ports.FirstOrDefault(p => p.ItemId == row.Primary.ItemId) is { } primary)
                return (DockMating.Mate(receiver, design, primary, designPort), receiver);
        }
        return (new DockMate(row.Primary, designPort, false, [], null), null);
    }

    /// <summary>A ship file that will not parse is skipped rather than thrown from: the survey walks the whole
    /// install including mods, and one bad file should not take the report down with it.</summary>
    private static IReadOnlyList<ShipTemplate> ReadTemplates(string path)
    {
        try
        {
            return ShipTemplate.ParseFileChecked(File.ReadAllText(path), out _);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}

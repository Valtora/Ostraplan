namespace Ostraplan.App.Wizard;

/// <summary>One fact on the Review pane: a label and the value it will actually have when written.</summary>
public sealed record ReviewFact(string Label, string Value);

/// <summary>
/// What a Review build found. <see cref="Facts"/> is what the export will do, <see cref="Warnings"/> is what it
/// will do anyway but the user should know about, and <see cref="Acknowledgements"/> is what it will destroy —
/// each one a checkbox the user has to tick before the commit button arms.
/// </summary>
public sealed record BuildOutcome(
    IReadOnlyList<ReviewFact> Facts,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Acknowledgements);

/// <summary>What a commit did, reported on the Done pane instead of in a popup.</summary>
public sealed record DoneReport(string Headline, IReadOnlyList<string> Lines);

/// <summary>
/// How one destination builds and writes. The wizard shell knows nothing about ships, saves or mod folders: it
/// asks the driver to prepare when the destination is picked, to build when Review is reached, and to write when
/// the user commits.
///
/// <para><b>The build and the write are separate on purpose.</b> Review runs the real engine and reports what will
/// happen; the commit then performs only the write. Anything randomised has to be pinned between the two, or the
/// report is a lie: wear rolls per part, a granted ship draws a spawn point, and a registration is minted from a
/// GUID. The two save destinations therefore keep the built artifact and write it verbatim, and the mod
/// destination, whose write rebuilds internally, pins the wear seed so the rebuild lands in the same place.</para>
///
/// <para><b>Background work goes through a static helper whose parameters are all plain data.</b> Never call
/// <see cref="Ui.OffThread"/> with a lambda that can see a driver, a session or a control: the capture guard will
/// throw, and in a Release build it would instead be the thread-affinity failure the guard exists to prevent.</para>
/// </summary>
public abstract class ExportDriver
{
    /// <summary>Which destination this drives.</summary>
    public abstract ExportDestination Destination { get; }

    /// <summary>What this destination is called in the rail's first step.</summary>
    public abstract string Name { get; }

    /// <summary>A one-line description shown under the destination's name.</summary>
    public abstract string Blurb { get; }

    /// <summary>
    /// <see cref="Name"/> and <see cref="Blurb"/> as this particular design would have them. The destination
    /// tiles are the first thing the user reads, and for a residence the ship wording is not merely imprecise
    /// but wrong: a residence is not parked a few kilometres out and does not take the P.A.S.S. ferry. Overridden
    /// by the two save destinations; the default is the kind-free text.
    /// </summary>
    public virtual string NameFor(WizardSession session) => Name;

    /// <inheritdoc cref="NameFor"/>
    public virtual string BlurbFor(WizardSession session) => Blurb;

    /// <summary>The commit button's label: "Export", "Add ship", "Write".</summary>
    public abstract string CommitVerb { get; }

    /// <summary>Non-null when this destination cannot be used at all, with the reason shown on the disabled tile.
    /// Evaluated before anything is selected, so it may only consult what is cheaply known.</summary>
    public abstract string? Unavailable(WizardSession session);

    /// <summary>
    /// Run once when the destination is selected. Returns null on success, or a reason that blocks Next. May be
    /// slow — the update destination relocates its save context here — so it is awaited behind a wait cursor.
    /// </summary>
    public virtual Task<string?> PrepareAsync(WizardSession session) => Task.FromResult<string?>(null);

    /// <summary>
    /// What a commit would produce, built if anything has changed since the last time and returned from the cache
    /// if not. Walking Back and Next past Review therefore does not re-run the engine, while any actual edit does.
    /// </summary>
    public async Task<BuildOutcome> ReviewAsync(WizardSession session)
    {
        if (!NeedsRebuild(session) && _outcome is { } cached) return cached;

        var built = await BuildAsync(session);
        var blocking = await ScanOffThread(session.Doc, session.Catalog);
        _outcome = blocking.Count == 0
            ? built
            : built with { Acknowledgements = [.. built.Acknowledgements, .. blocking] };
        MarkBuilt(session);
        return _outcome;
    }

    /// <summary>
    /// The design problems Ostraplan already rates as blocking, turned into acknowledgements so no export leaves
    /// with one unmentioned. The PROBLEMS list has always shown these; nothing ever put them in front of a write.
    ///
    /// <para>They acknowledge rather than refuse because a blocking problem is not equally fatal everywhere: a
    /// hull with no docking port is a broken purchase and a perfectly good derelict. Ostraplan says plainly what
    /// is wrong and lets the person who knows the design decide.</para>
    /// </summary>
    private static Task<IReadOnlyList<string>> ScanOffThread(
        Ostraplan.Core.ShipDocument doc, Ostraplan.Core.Catalog catalog) =>
        Ui.OffThread<IReadOnlyList<string>>(() =>
            [.. Ostraplan.Core.ProblemScan.Scan(doc, catalog)
                .Where(p => p.Severity == Ostraplan.Core.ProblemSeverity.Blocking)
                .Select(p => $"{p.Title}. {p.Detail}")]);

    private BuildOutcome? _outcome;

    /// <summary>Run the engine and report what a commit would produce. Called through
    /// <see cref="ReviewAsync"/>, which owns the caching.</summary>
    public abstract Task<BuildOutcome> BuildAsync(WizardSession session);

    /// <summary>Perform the write, from what <see cref="BuildAsync"/> produced. Throws for the caller to report;
    /// the shell catches the explainable I/O failures and lets anything else reach the crash handler.</summary>
    public abstract Task<DoneReport> WriteAsync(WizardSession session);

    /// <summary>Whether this destination has changed the design itself, as opposed to only collecting settings.
    /// Cancelling then has something to offer to take back out.</summary>
    public virtual bool HasDocumentEdits => false;

    /// <summary>Take those edits back out. Only called when the user asks, and never after a commit.</summary>
    public virtual void UndoDocumentEdits(WizardSession session) { }

    /// <summary>The plan revision the cached build was made at, or -1 when nothing is cached.</summary>
    private int BuiltAt { get; set; } = -1;

    private bool NeedsRebuild(WizardSession session) => BuiltAt != session.Plan.Revision;

    private void MarkBuilt(WizardSession session) => BuiltAt = session.Plan.Revision;

    /// <summary>Draw the seed that pins a wear roll for this build, so the commit damages exactly the parts Review
    /// showed. Drawn fresh on every build, because the target condition may have changed since the last one.</summary>
    protected static Ostraplan.Core.WearOptions PinSeed(Ostraplan.Core.WearOptions wear) =>
        wear with { Seed = Random.Shared.Next() };
}

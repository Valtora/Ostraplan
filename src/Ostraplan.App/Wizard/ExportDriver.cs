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
    /// Run the engine and report what a commit would produce. Called on entering Review, and again whenever the
    /// plan's revision has moved on since the last build.
    /// </summary>
    public abstract Task<BuildOutcome> BuildAsync(WizardSession session);

    /// <summary>Perform the write, from what <see cref="BuildAsync"/> produced. Throws for the caller to report;
    /// the shell catches the explainable I/O failures and lets anything else reach the crash handler.</summary>
    public abstract Task<DoneReport> WriteAsync(WizardSession session);

    /// <summary>The plan revision the cached build was made at, or -1 when nothing is cached.</summary>
    protected int BuiltAt { get; private set; } = -1;

    protected bool NeedsRebuild(WizardSession session) => BuiltAt != session.Plan.Revision;

    protected void MarkBuilt(WizardSession session) => BuiltAt = session.Plan.Revision;

    /// <summary>Draw the seed that pins a wear roll for this build, so the commit damages exactly the parts Review
    /// showed. Drawn fresh on every build, because the target condition may have changed since the last one.</summary>
    protected static Ostraplan.Core.WearOptions PinSeed(Ostraplan.Core.WearOptions wear) =>
        wear with { Seed = Random.Shared.Next() };
}

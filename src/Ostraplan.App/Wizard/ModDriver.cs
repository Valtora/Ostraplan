using System.Globalization;
using System.IO;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// The mod destination: a <c>data/ships</c> mod folder, shareable and save-independent.
///
/// <para>Unlike the two save destinations, this one's write is monolithic — <see cref="ShipExport.Write"/> builds
/// and writes in a single call — so Review builds with <see cref="ShipExport.Build"/> for the report and the
/// commit rebuilds internally. That is only honest because the wear seed is pinned at Review and handed to the
/// write, so the rebuild damages exactly the parts the report described.</para>
/// </summary>
public sealed class ModDriver : ExportDriver
{
    private WearOptions _pinnedWear = WearOptions.Pristine;
    private string? _replaceTarget;
    private string _modDir = "";
    private ShipDelivery _delivery = ShipDelivery.None;

    public override ExportDestination Destination => ExportDestination.Mod;
    public override string Name => "As a mod";
    public override string Blurb =>
        "A mod folder the game loads: shareable, works in any save, and can be sold at a kiosk or handed to a new Shipbreaker.";
    public override string CommitVerb => "Export";

    /// <summary>Always available. A design with parts can always be written as a mod, which is what makes this the
    /// fallback the other two destinations point at when they cannot be used.</summary>
    public override string? Unavailable(WizardSession session) => null;

    // ---- review ----

    public override async Task<BuildOutcome> BuildAsync(WizardSession session)
    {
        var plan = session.Plan;
        _pinnedWear = PinSeed(plan.Wear);

        // When replacing, the override key is the target ship's real strName, resolved from the file rather than
        // assumed from its filename: a mod or multi-ship file need not match.
        _replaceTarget = plan.Mod.ReplaceShip is { } rs
            ? TemplateImport.ResolveShipStrName(rs.Path) ?? rs.Name
            : null;
        var strName = _replaceTarget ?? plan.ShipName;
        var publicName = ShipExport.ResolvePublicName(plan.Identity.PublicName, plan.ShipName, _replaceTarget is not null);
        _delivery = BuildDelivery(plan, publicName);

        var parent = Parent(session);
        _modDir = Path.Combine(parent, ShipExport.SanitizeName(
            ShipExport.ResolveModName(plan.Mod.ModName, plan.ShipName, _replaceTarget)));

        var meta = plan.Identity with { PublicName = publicName };
        var (ship, rating, roomCount, buildWarnings) =
            await BuildOffThread(session.Doc, session.Catalog, session.Specs, strName, meta, _pinnedWear);

        var facts = new List<ReviewFact>
        {
            new("Ship", $"{plan.ShipName}  ({ship.AItems.Length} parts, {roomCount} certified room(s))"),
            new("Rating", string.IsNullOrEmpty(rating.Display) ? "None" : rating.Display),
            new("In-game name", publicName == "$TEMPLATE" ? "the game's usual varied names" : publicName),
            new("Condition", _pinnedWear.Enabled
                ? $"worn to ~{_pinnedWear.TargetCondition * 100:0}% average (parts vary, none below 10%)"
                : "pristine"),
        };
        if (_replaceTarget is not null)
            facts.Add(new ReviewFact("Replaces", $"the existing ship \"{_replaceTarget}\""));
        facts.Add(new ReviewFact("Obtainable via", Describe(_delivery) is { Length: > 0 } d
            ? d
            : "nothing. You chose to wire this up yourself, so the ship file goes out on its own"));
        facts.Add(new ReviewFact("Preview art",
            "a ship image plus a thumbnail per certified room, so the ship shows a picture at the kiosk and in " +
            "character creation instead of a missing-image X"));
        facts.Add(new ReviewFact("Writes to", _modDir));
        facts.Add(new ReviewFact("Registering", plan.Mod.RegisterWithOstrasort
            ? "handed to Ostrasort right after the write"
            : "left to you (Ostraplan never edits loading_order.json)"));

        var warnings = new List<string>(buildWarnings);
        if (_delivery.Derelicts.Count > 0)
            warnings.Add("Derelict fields are filled when a world is generated, so this reaches a NEW GAME only. " +
                         "A save you already have will never grow one.");

        var acks = new List<string>();
        if (Directory.Exists(_modDir) && Directory.EnumerateFileSystemEntries(_modDir).Any())
            acks.Add($"A folder named \"{Path.GetFileName(_modDir)}\" already exists here. Its data files (ship, and " +
                     $"any loot/lifeevents/interactions) will be replaced, as will the preview art in " +
                     $"images\\ships\\{strName}. Other files in the folder are left alone.");

        return new BuildOutcome(facts, warnings, acks);
    }

    /// <summary>Every parameter is plain data, so the lambda's closure holds nothing UI-owned and the capture guard
    /// has nothing to reject. See <see cref="ExportDriver"/>.</summary>
    private static Task<(ExportedShip Ship, ShipRating Rating, int RoomCount, List<string> Warnings)> BuildOffThread(
        ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs, string strName,
        ExportMetadata meta, WearOptions wear) =>
        Ui.OffThread(() =>
        {
            var warnings = new List<string>();
            var (ship, rating, rooms) = ShipExport.Build(doc, catalog, specs, strName, warnings, meta, wear);
            return (ship, rating, rooms, warnings);
        });

    // ---- commit ----

    public override async Task<DoneReport> WriteAsync(WizardSession session)
    {
        var plan = session.Plan;

        // Rendered here, on the UI thread, and handed over as plain PNG bytes: the canvas and its sprite atlas are
        // thread-affine, so nothing about the renderer may cross into the background write.
        var preview = session.RenderPreview?.Invoke();

        var opts = new ExportOptions(
            plan.ShipName, plan.Mod.Author, plan.Mod.Notes, plan.Mod.Version,
            session.Env.InstalledVersion ?? GameEnv.VerifiedGameVersion, Parent(session),
            plan.Identity.PublicName, plan.Identity.Make, plan.Identity.Model, plan.Identity.Year,
            plan.Identity.Designation, plan.Identity.Description, _delivery, _replaceTarget, plan.Mod.ModName,
            _pinnedWear,   // the seed Review built with, so the rebuild inside Write lands in the same place
            preview);

        var result = await WriteOffThread(session.Doc, session.Catalog, session.Specs, opts, session.Index);

        session.Settings.ExportAuthor = plan.Mod.Author;
        if (!plan.Mod.StagedIntoMods) session.Settings.LastExportDir = plan.Mod.Folder;
        session.Settings.Save();
        AuditLog.Add($"Exported mod \"{plan.ShipName}\" to {result.ModDir}.");

        var lines = new List<string>
        {
            $"{result.PartCount} parts, {result.RoomCount} certified room(s), rating " +
            $"{(string.IsNullOrEmpty(result.Rating.Display) ? "None" : result.Rating.Display)}.",
        };
        if (_pinnedWear.Enabled)
            lines.Add($"Worn to ~{_pinnedWear.TargetCondition * 100:0}% average condition (parts vary, none below 10%).");
        if (result.PreviewCount > 0)
            lines.Add($"Preview art: 1 ship image and {result.PreviewCount - 1} room thumbnail(s).");
        if (_replaceTarget is not null) lines.Add($"Replaces the existing ship \"{_replaceTarget}\".");
        if (Describe(_delivery) is { Length: > 0 } delivery) lines.Add("Obtainable via: " + delivery + ".");
        lines.Add("");
        lines.Add($"Written to {result.ModDir}");

        if (plan.Mod.RegisterWithOstrasort)
        {
            lines.Add("");
            lines.AddRange(await RegisterWithOstrasort(session, plan.ShipName, result));
            return new DoneReport($"Exported {plan.ShipName}.", lines);
        }

        lines.Add("");
        lines.AddRange(plan.Mod.StagedIntoMods
            ?
            [
                "It is staged into the game's Mods folder.",
                "Register it with Ostrasort (or ModTools) before it appears in game.",
                "Ostraplan never writes loading_order.json itself.",
            ]
            : new[]
            {
                "Copy this folder into Ostranauts_Data\\Mods.",
                "Then register it with Ostrasort (or ModTools) to spawn it in game.",
            });
        return new DoneReport($"Exported {plan.ShipName}.", lines);
    }

    private static Task<ExportResult> WriteOffThread(
        ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs, ExportOptions opts, DataIndex index) =>
        Ui.OffThread(() => ShipExport.Write(doc, catalog, specs, opts, index));

    // ---- Ostrasort ----

    /// <summary>
    /// Hand the staged export to Ostrasort: locate it (prompting once and remembering the path), register the mod
    /// (<c>--apply</c>), then merge kiosk-loot conflicts (<c>--patch</c>) if the export wrote any loot pools.
    /// Ostraplan never writes <c>loading_order.json</c> itself; this only drives the tool that owns it.
    /// </summary>
    private static async Task<IReadOnlyList<string>> RegisterWithOstrasort(
        WizardSession session, string shipName, ExportResult result)
    {
        var exe = OstrasortLauncher.Detect(session.Settings);
        if (exe is null)
        {
            if (!Dlg.Confirm(session.Owner, DlgKind.Info, "Locate Ostrasort",
                    "Ostraplan couldn't find Ostrasort.exe. Point it at your Ostrasort.exe to register the mod " +
                    "(or cancel and register it yourself later).", "Locate…"))
                return NotRegistered;
            exe = OstrasortLauncher.Prompt(session.Owner);
            if (exe is null) return NotRegistered;
            session.Settings.OstrasortPath = exe;
            session.Settings.Save();
        }

        OstrasortRun apply, patch = new(false, 0, "", null);
        apply = await OstrasortLauncher.RunAsync(exe, session.Env.GameRoot, session.Env.ModsDir, patch: false);
        if (apply.Ok && result.TouchedLootPools)
            patch = await OstrasortLauncher.RunAsync(exe, session.Env.GameRoot, session.Env.ModsDir, patch: true);

        // a remembered path that failed to launch is likely stale, so clear it and re-detect or prompt next time
        if (!apply.Launched && session.Settings.OstrasortPath == exe)
        {
            session.Settings.OstrasortPath = null;
            session.Settings.Save();
        }

        AuditLog.Add($"Ostrasort register \"{shipName}\": apply exit {apply.ExitCode}" +
                     (result.TouchedLootPools ? $", patch exit {patch.ExitCode}" : ""));

        if (!apply.Launched)
            return
            [
                $"Ostrasort could not be launched: {apply.Error}",
                "Register the mod yourself with Ostrasort or ModTools.",
            ];

        var lines = new List<string>
        {
            apply.Ok ? "Registered with Ostrasort." : $"Ostrasort reported exit {apply.ExitCode}.",
        };
        if (result.TouchedLootPools)
            lines.Add(patch.Ok
                ? "Kiosk-loot conflicts patched (if any)."
                : $"The loot patch step reported exit {patch.ExitCode}. Check Ostrasort if another ship mod shares those kiosks.");
        lines.Add("Launch Ostranauts and check the MODS screen to confirm it loaded.");
        return lines;
    }

    private static readonly string[] NotRegistered =
    [
        "Not registered: you cancelled the Ostrasort step.",
        "It won't appear in game until you register it. Run Ostrasort (or ModTools), or export again with " +
        "\"Register with Ostrasort\" ticked.",
    ];

    // ---- helpers ----

    private static string Parent(WizardSession session) =>
        session.Plan.Mod.StagedIntoMods ? session.Env.ModsDir : session.Plan.Mod.Folder!;

    private static ShipDelivery BuildDelivery(ExportPlan plan, string publicName) => new(
        plan.Mod.BrokerPools, plan.Mod.BrokerWeight ?? 0.05, plan.Mod.SpecialOfferPools,
        plan.Mod.StartingShip, plan.Mod.StartWeight, plan.Mod.StartStation, plan.Mod.StartMortgage,
        publicName is { Length: > 0 } && publicName != "$TEMPLATE" ? publicName : plan.ShipName,
        plan.Identity.Description, plan.Mod.StartingShipExclusive,
        plan.Mod.DerelictPools, plan.Mod.DerelictWeight ?? 0.05);

    /// <summary>A one-line human summary of the chosen delivery options, or "" when the ship file goes out on its
    /// own.</summary>
    private static string Describe(ShipDelivery d)
    {
        var parts = new List<string>();
        if (d.BrokerPools.Count > 0) parts.Add($"{d.BrokerPools.Count} broker kiosk(s)");
        if (d.SpecialOfferPools.Count > 0) parts.Add($"{d.SpecialOfferPools.Count} Special Offer slot(s)");
        if (d.StartingShip)
            parts.Add(d.StartingShipExclusive ? "Shipbreaker starting ship (guaranteed)" : "Shipbreaker starting ship");
        if (d.Derelicts.Count > 0)
            parts.Add($"{d.Derelicts.Count} derelict field(s): " + string.Join(", ", d.Derelicts.Select(Band)));
        return string.Join(", ", parts);
    }

    private static string Band(string pool) =>
        KioskExport.DerelictPools.FirstOrDefault(p => p.Pool == pool).Label ?? pool;
}

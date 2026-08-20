using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// The update destination: a ship in a save, rewritten to this design's layout with its crew, cargo and world
/// position intact.
///
/// <para>Usually that ship is the one the design came from. <see cref="ShipDocument.SourceSave"/> already names the
/// save and the ship, so selecting this destination re-locates that context instead of asking, and a design
/// reopened from a <c>.oplan</c> relocates it from disk, which is why that happens on selection rather than at the
/// write.</para>
///
/// <para><b>A design with no source is asked where to go</b> rather than refused. The inject treats a placement with
/// no <see cref="Placement.OriginStrID"/> as new construction, so a design that never came from that ship (a stock
/// template, one drawn from scratch) writes as a wholesale replacement of its layout: everything on the ship is torn
/// out and the design built in its place, with the crew, cargo, position and identity that make it that ship all
/// preserved. That is the only way to move a live ship onto a different hull without redrawing it by hand.</para>
///
/// <para>This is the one destination that overwrites something the user already has, so it is also the one that
/// keeps a confirmation popup: the in-place write is irreversible and carries a check for a running game.</para>
/// </summary>
public sealed class UpdateDriver : ExportDriver
{
    private SaveShipContext? _ctx;
    private ShipDiff? _diff;
    private EditCostBreakdown? _baseCost;
    private double? _balance;

    private JsonObject? _ship;
    private InjectReport? _report;
    private WearOptions _pinnedWear = WearOptions.Pristine;
    private CompositeCommand? _standIns;

    public override ExportDestination Destination => ExportDestination.UpdateShipInSave;
    public override string Name => "Update something in a save";
    public override string Blurb =>
        "Rewrites what is in a save to this design, keeping everything about it except the layout.";
    public override string CommitVerb => "Write";

    public override string NameFor(WizardSession session) =>
        session.ByKind("Update a ship in a save", "Update an apartment in a save");

    public override string BlurbFor(WizardSession session) => session.ByKind(
        "Rewrites a ship in a save to this design, keeping its crew, cargo, world position and identity. Uses the "
        + "ship the design came from, or asks which one to replace. Writes a copy by default; can edit the original "
        + "in place.",
        "Rewrites an apartment in a save to this design, keeping its crew, cargo, identity, its place at the "
        + "station and the transit route that reaches it. Uses the apartment the design came from, or asks which "
        + "one to replace. Writes a copy by default; can edit the original in place.");

    public override string? Unavailable(WizardSession session) =>
        session.Saves.Count == 0 ? "No save games found." : null;

    /// <summary>Shown when the user backs out of the target picker. Not an error, but Next stays blocked: there is
    /// no ship to write to until they answer.</summary>
    private const string NoTarget = "Pick the save and the ship or apartment this design should replace.";

    /// <summary>The located ship in its save, or null until this destination is selected.</summary>
    public SaveShipContext? Context => _ctx;

    /// <summary>The structural change counts, for the cost step's header.</summary>
    public ShipDiff? Diff => _diff;

    /// <summary>The edit's cost at multipliers of 1.0, which the cost step scales live via
    /// <see cref="EditCost.Total(EditCostBreakdown, double, double)"/>.</summary>
    public EditCostBreakdown? BaseCost => _baseCost;

    /// <summary>The player's current credits, or null when this save has no balance to deduct from.</summary>
    public double? Balance => _balance;

    // ---- selection ----

    /// <summary>
    /// Locate the ship in its save and cost the edit. The in-session context is used when the design was imported
    /// this run; a reopened <c>.oplan</c> relocates it from the save named in the design. A failure here is the
    /// reason Next is blocked, so the user hears "that save has moved" now rather than after committing.
    /// </summary>
    public override async Task<string?> PrepareAsync(WizardSession session)
    {
        // The cached context is only reusable when it is the ship being asked for. A design rebound earlier in the
        // session leaves a context behind for whatever it was bound to then, and reusing that would write the
        // design over a ship the user did not pick this time.
        if (_ctx is null && session.SaveContext is { } cached
            && (session.UpdateTarget is not { } wanted || IsSameShip(cached, wanted)))
            _ctx = cached;

        if (_ctx is null)
        {
            SaveEntry save;
            string regId;

            // The design's own source, then a target the caller already asked for (the Update Ship in Save menu
            // action asks before the wizard exists, so cancelling there abandons the wizard rather than opening it
            // onto a blocked step), and only then ask here.
            if ((session.SourceSave ?? session.UpdateTarget) is { } src)
            {
                var match = session.Saves.FirstOrDefault(s => string.Equals(s.Name, src.SaveName, StringComparison.Ordinal));
                if (match is null)
                    return $"The source save \"{src.SaveName}\" is no longer in your Saves folder, so this design can't " +
                           "be written back. You can still export it as a mod.";
                (save, regId) = (match, src.RegId);
            }
            else if (session.Saves.Count == 0)
            {
                return "No save games found.";
            }
            else if (PickTarget(PickerOwner(session), session.Saves, session.Doc.Kind) is { } picked)
            {
                (save, regId) = picked;
            }
            else
            {
                return NoTarget;
            }

            try
            {
                _ctx = await RelocateOffThread(save.ZipPath, save.Name, regId, session.Catalog);
            }
            catch (Exception ex)
            {
                return $"Couldn't re-locate the {session.Noun} in that save:\n" + ex.Message;
            }
            session.SaveContext = _ctx;   // cache for the rest of the session, as the menu action always did
        }

        RefreshImportedCargo(session.Doc, _ctx);
        SeedIdentityFromShip(session);
        Recost(session);
        return null;
    }

    /// <summary>
    /// Re-read each container's contents from the ship this write is about to go over, for every container whose
    /// contents the user has not authored.
    ///
    /// <para>The write-back emits what the placement holds, so whatever is not in that tree is dropped from the
    /// save. A design reopened from an <c>.oplan</c> holds the contents as they were when it was imported, and the
    /// player has usually been playing since: without this, writing back would revert every locker they had
    /// rearranged, and report the difference as cargo lost. Refreshing here rather than at open is also what lets
    /// the file stop naming a save at all — the ship is known by then, because the user has just chosen it.</para>
    ///
    /// <para>A container the user edited in the inventory editor is left alone: that snapshot is the design, and
    /// overwriting it with the ship's contents would throw away the edit.</para>
    /// </summary>
    private static void RefreshImportedCargo(ShipDocument doc, SaveShipContext ctx)
    {
        foreach (var p in doc.Placements)
            if (p.OriginStrID is { } id && !doc.IsCargoEdited(p) && ctx.CargoByOrigin.TryGetValue(id, out var forest))
                p.Cargo = forest;
    }

    private static bool IsSameShip(SaveShipContext ctx, SaveSourceRef target) =>
        string.Equals(ctx.Source.SaveName, target.SaveName, StringComparison.Ordinal)
        && string.Equals(ctx.Source.RegId, target.RegId, StringComparison.Ordinal);

    // ---- picking a target for a design that carries no source ----

    /// <summary>
    /// Ask which ship in which save this design should replace: the same two pickers the save-edit import uses, so
    /// the choice reads the same way in both places, followed by a warning that says what a wholesale replacement
    /// actually does. Null when the user backs out of any of them.
    ///
    /// <para>The shell holds a wait cursor over the whole prepare, which is right while a save is being read and
    /// wrong over a dialog waiting on the user. It is dropped for the duration and put back, rather than removed
    /// from the shell, because everything else the prepare does really is work.</para>
    /// </summary>
    internal static (SaveEntry Save, string RegId)? PickTarget(
        Window? owner, IReadOnlyList<SaveEntry> saves, DocumentKind kind = DocumentKind.Ship)
    {
        var busy = Mouse.OverrideCursor;
        Mouse.OverrideCursor = null;
        try { return Ask(owner, saves, kind); }
        finally { Mouse.OverrideCursor = busy; }
    }

    private static (SaveEntry Save, string RegId)? Ask(
        Window? owner, IReadOnlyList<SaveEntry> saves, DocumentKind kind)
    {
        // A residence design replaces a residence. Offering it the ship list would be offering the one operation
        // that is nearly always a mistake, and the layout it would write is an apartment's.
        var residence = kind == DocumentKind.Residence;
        var noun = residence ? "apartment" : "ship";

        var picker = new SavePickerDialog(saves, $"Which {noun} should this design replace?",
            $"The design is written over {(residence ? "an apartment" : "a ship")} in this save, keeping its crew, "
            + $"cargo, {(residence ? "place at the station" : "position")} and identity.",
            "Choose save") { Owner = owner };
        if (picker.ShowDialog() != true || picker.Selected is not { } save) return null;

        // the ship the player is standing on may be a station, so offer what they own first, as the import does
        var all = SaveImport.ListPlayerShips(save.ZipPath);
        var ships = all.Where(s => s.IsResidence == residence).ToList();
        if (ships.Count == 0)
        {
            Dlg.Show(owner,
                residence
                    ? all.Count > 0
                        ? $"No apartments in \"{save.Name}\". Ostraplan found {all.Count} ship(s) there, so the save "
                          + "read fine — you just don't own a residence in it. Use \"Into a save game\" to add this "
                          + "design as a new apartment instead."
                        : $"Couldn't find anything you own in \"{save.Name}\"."
                    : $"Couldn't find a ship to write to in \"{save.Name}\" (no owned ships and no current ship on record).",
                $"Update {noun} in save", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var shipDlg = new ShipChoiceDialog(save.Name, ships, kind) { Owner = owner };
        if (shipDlg.ShowDialog() != true || shipDlg.Selected is not { } chosen) return null;

        if (!chosen.Owned && !ConfirmUnsupportedShip(owner, chosen)) return null;
        return ConfirmReplacement(owner, save, chosen) ? (save, chosen.RegId) : null;
    }

    /// <summary>
    /// The window to hang the pickers off.
    ///
    /// <para>Not simply <see cref="WizardSession.Owner"/>: the shell prepares its opening destination from the
    /// wizard's own constructor, so when the update destination is preselected (Analyse ▸ Update Ship in Save…) the
    /// wizard window exists but has not been shown, and WPF refuses to make an unshown window an owner. The main
    /// window is the right parent at that moment anyway, since it is what the user is looking at. Null when neither
    /// is on screen, which is every test: an unowned dialog is legal and simply centres itself.</para>
    /// </summary>
    private static Window? PickerOwner(WizardSession session) =>
        session.Owner is { IsLoaded: true } wizard ? wizard
        : Application.Current?.MainWindow is { IsLoaded: true } main ? main
        : null;

    /// <summary>The stern gate before writing to a ship the player doesn't own (a station, another vessel), matching
    /// the one the save-edit import puts in front of the same choice.</summary>
    private static bool ConfirmUnsupportedShip(Window? owner, SaveShipChoice c) =>
        Dlg.Confirm(owner, DlgKind.Danger, "This isn't your ship",
            $"{c.Name} ({c.RegId}) is a station or another vessel, not one of your ships.\n\n" +
            "Writing to something you don't own is not supported, and it can corrupt or break your save.\n\n" +
            "Only continue if you understand that and have a backup.",
            "I understand, use it anyway");

    /// <summary>
    /// What a design with no shared history with the target actually does to it, said once before the wizard costs
    /// anything. The Review step restates the counts and lists any cargo that would be destroyed, but by then the
    /// user has walked three steps on the assumption that this was an edit rather than a replacement.
    /// </summary>
    private static bool ConfirmReplacement(Window? owner, SaveEntry save, SaveShipChoice ship)
    {
        var residence = ship.IsResidence;
        var noun = residence ? "apartment" : "ship";
        return Dlg.Confirm(owner, DlgKind.Warning, $"Replace {ship.Name}'s layout with this design?",
            $"{(residence ? "Apartment" : "Ship")} {ship.RegId} in save \"{save.Name}\".\n\n" +
            $"This design didn't come from that {noun}, so nothing on it is recognised as already built: every part " +
            $"currently on the {noun} is torn out and this design is built in its place.\n\n" +
            $"The {noun} stays the same {noun}. Its crew, cargo, registration, identity and " +
            (residence ? "its place at the station" : "world position") + " are kept, and " +
            "cargo is carried over wherever the container it sits in survives the swap. Cargo in a container the " +
            "design does not have is destroyed, and Review lists that before anything is written.\n\n" +
            "The write goes to a copy of the save by default, leaving the original untouched.",
            $"Choose this {noun}");
    }

    /// <summary>
    /// Fill a blank identity from the ship's own record. An import seeds this already, so this is for a design
    /// reopened from a <c>.oplan</c> — including every one saved before the identity became writable, which
    /// carries no identity at all. Without it those designs would write six blanks over a real make, model and
    /// designation the moment they were updated.
    ///
    /// <para>Only a wholly blank identity is filled, so a design that carries a deliberate one keeps it: the
    /// user's saved answer beats the ship's current state.</para>
    /// </summary>
    private void SeedIdentityFromShip(WizardSession session)
    {
        if (_ctx is not { } ctx || session.Plan.Identity != new ExportMetadata()) return;
        session.Plan.Identity = SaveEdit.ReadIdentity(ctx);
    }

    private static Task<SaveShipContext> RelocateOffThread(string zipPath, string saveName, string regId, Catalog catalog) =>
        Ui.OffThread(() => SaveEditImport.RelocateContext(zipPath, saveName, regId, catalog));

    /// <summary>Recompute the diff and its cost. Cheap, and it has to follow any stand-in the user applied, which
    /// changes what counts as new.</summary>
    public void Recost(WizardSession session)
    {
        if (_ctx is not { } ctx) return;
        _diff = ShipDiff.Compute(session.Doc, ctx);
        _baseCost = EditCost.Compute(_diff, session.Catalog, 1.0, 1.0, session.Doc.LooseObjects);
        _balance = SaveEdit.CurrentBalance(ctx);
    }

    /// <summary>The items on this ship whose defs aren't in the loaded data. Only this destination can do anything
    /// about them: a stand-in needs the save context to know what it is replacing.</summary>
    public IReadOnlyList<UnresolvedItem> Outstanding(WizardSession session) =>
        _ctx is { } ctx ? Substitution.Outstanding(session.Doc, ctx, session.Catalog) : [];

    // ---- stand-ins, which are real edits to the design ----

    /// <summary>Apply the chosen stand-ins to the live document, remembering them so a cancel can offer to take
    /// them back out. Applied through <see cref="PlaceCommand"/> like any other edit, so the ship really does
    /// change: a stand-in <b>replaces</b> the modded item in the save that gets written.</summary>
    public int ApplyStandIns(WizardSession session, IReadOnlyDictionary<string, PartDef> choices)
    {
        if (_ctx is not { } ctx || choices.Count == 0) return 0;

        var placed = new List<IDocCommand>();
        using (session.Doc.SuspendChanged())
            foreach (var item in Substitution.Outstanding(session.Doc, ctx, session.Catalog))
                if (choices.TryGetValue(item.DefName, out var part))
                {
                    var cmd = new PlaceCommand(Substitution.StandIn(item, part.DefName, session.Catalog, ctx));
                    cmd.Do(session.Doc);
                    placed.Add(cmd);
                }

        if (placed.Count == 0) return 0;

        _standIns = _standIns is null
            ? new CompositeCommand(placed)
            : new CompositeCommand([_standIns, .. placed]);
        AuditLog.Add($"Stood in for {placed.Count} unresolved item(s): "
                     + string.Join(", ", choices.Select(kv => $"{kv.Key} → {kv.Value.DefName}")));
        Recost(session);
        session.Plan.Touch();
        return placed.Count;
    }

    public override bool HasDocumentEdits => _standIns is not null;

    public override void UndoDocumentEdits(WizardSession session)
    {
        _standIns?.Undo(session.Doc);
        _standIns = null;
    }

    // ---- review ----

    public override async Task<BuildOutcome> BuildAsync(WizardSession session)
    {
        if (_ctx is not { } ctx) throw new InvalidDataException("The target hasn't been located in its save.");

        var plan = session.Plan;
        _pinnedWear = PinSeed(plan.Wear);
        var charge = plan.Update.Deduct && ctx.PlayerCoId is { } coId && _balance is { } bal
            ? new EditCharge(coId, Cost(plan), bal - Cost(plan))
            : null;

        (_ship, _report) = await BuildOffThread(session.Doc, ctx, session.Catalog, session.Specs, charge, _pinnedWear,
            plan.Identity);

        var target = plan.Update.InPlace
            ? $"the original save \"{ctx.Source.SaveName}\", in place"
            : $"a new save named {Path.GetFileName(SaveEdit.SuggestCopyDir(ctx))}";

        var facts = new List<ReviewFact>
        {
            new(session.NounCap, $"{ctx.Source.RegId} in \"{ctx.Source.SaveName}\""),
            new("Identity", IdentitySummary(ctx, plan.Identity)),
            new("Changes", ChangeSummary(_report)),
            new("Grid", _report.GridReframed
                ? $"reframed to {_report.NCols} x {_report.NRows}"
                : $"{_report.NCols} x {_report.NRows}, unchanged"),
            new("Condition", _pinnedWear.Enabled
                ? $"every installed part re-worn to ~{_pinnedWear.TargetCondition * 100:0}% average, replacing its existing damage"
                : "each part keeps the wear it already has"),
            new("Cost", _report.Charged is { } c
                ? $"{Money(c)}, leaving {Money(_report.ResultingBalance ?? 0)}"
                : "not deducted"),
            new("Atmosphere", "refills on load, about 22 kPa O2 and 80 kPa N2"),
            new("Writes to", target),
        };
        if (_report.PowerFixed > 0)
            facts.Add(new ReviewFact("Power", $"{_report.PowerFixed} device(s) rearmed after losing their power ticker"));
        if (plan.Update.InPlace)
            facts.Add(new ReviewFact("Backup", plan.Update.Backup
                ? "the original is copied to a separate save first"
                : "none. This overwrites the original with nothing to roll back to"));

        var acks = new List<string>();
        if (_report.CargoDropped.Count > 0)
        {
            var total = _report.CargoDropped.Sum(l => l.Items.Count);
            var named = string.Join("; ", _report.CargoDropped.Take(4).Select(l =>
                $"{l.ContainerName} ({string.Join(", ", l.Items.Take(4))}" +
                (l.Items.Count > 4 ? $", plus {l.Items.Count - 4} more" : "") + ")"));
            acks.Add($"You deleted {_report.CargoDropped.Count} container(s) still holding {total} cargo item(s). " +
                     $"Writing this permanently deletes that cargo: {named}" +
                     (_report.CargoDropped.Count > 4 ? ", and more" : "") +
                     ". To keep it, go back, empty those containers in game, then import and edit again.");
        }
        if (Substitution.OutstandingDefs(session.Doc, ctx, session.Catalog) is { Count: > 0 } unresolved)
        {
            var items = unresolved.Sum(d => d.Count);
            acks.Add($"{items} item(s) still use parts that aren't in your loaded data. Ostraplan works out the " +
                     $"{session.Noun}'s rooms and grid as if they weren't there, so writing back now can leave it with " +
                     "ghost rooms and shifted zones in game.");
        }

        // A residence design going over a vessel, or a vessel design over an apartment. Neither corrupts the
        // save — the target keeps its own registration, its objSS and its station lock, so only the layout
        // changes — but it is nearly always a mistake, and after the write the target reads as whatever it was
        // before regardless of what the design thought it was. Worth saying, not worth blocking.
        var warnings = new List<string>(_report.Warnings);
        var targetIsResidence = SaveZip.IsSubStation(ctx.Source.RegId);
        if (session.Doc.IsResidence != targetIsResidence)
            warnings.Add(targetIsResidence
                ? $"This design is a ship, but {ctx.Source.RegId} is a station residence. The apartment keeps its "
                  + "registration, its place at the station and its transit route; only the layout is replaced."
                : $"This design is a residence, but {ctx.Source.RegId} is a ship. The ship keeps its registration "
                  + "and its position; only the layout is replaced, so it will be a vessel laid out as an apartment.");

        return new BuildOutcome(facts, warnings, acks);
    }

    private static Task<(JsonObject Ship, InjectReport Report)> BuildOffThread(
        ShipDocument doc, SaveShipContext ctx, Catalog catalog, IReadOnlyList<RoomSpecDef> specs,
        EditCharge? charge, WearOptions wear, ExportMetadata identity) =>
        Ui.OffThread(() => SaveEdit.BuildInjectedShip(doc, ctx, catalog, specs, charge, wear, identity));

    // ---- commit ----

    public override async Task<DoneReport> WriteAsync(WizardSession session)
    {
        if (_ctx is not { } ctx || _ship is not { } ship || _report is not { } report)
            throw new InvalidDataException("Nothing has been built yet.");

        var plan = session.Plan;
        string writtenName;
        string? backupName = null;

        if (plan.Update.InPlace)
        {
            // The one confirmation the wizard keeps. It is irreversible, and it is the only place that can check
            // whether the game is running, which the Review pane cannot usefully do minutes earlier.
            if (!ConfirmInPlace(session, ctx.Source.SaveName, plan.Update.Backup))
                throw new OperationCanceledException();
            var backupPath = await WriteInPlaceOffThread(ctx, ship, report.ResultingBalance, plan.Update.Backup);
            backupName = backupPath is null ? null : Path.GetFileName(backupPath);
            writtenName = ctx.Source.SaveName;
        }
        else
        {
            var outDir = SaveEdit.SuggestCopyDir(ctx);
            await WriteCopyOffThread(ctx, ship, outDir, report.ResultingBalance);
            writtenName = Path.GetFileName(outDir);
        }

        _ship = null;   // written; a second commit would need a fresh build
        AuditLog.Add($"Updated ship in save — wrote \"{writtenName}\".");

        var lines = new List<string>
        {
            ChangeSummary(report) + ".",
        };
        if (report.Charged is { } charged)
            lines.Add($"{Money(charged)} was deducted. Your balance is now {Money(report.ResultingBalance ?? 0)}.");
        if (_pinnedWear.Enabled)
            lines.Add($"Every installed part was worn to ~{_pinnedWear.TargetCondition * 100:0}% average condition " +
                      "(parts vary, none below 10%), replacing any existing damage.");
        if (report.PowerFixed > 0)
            lines.Add($"Rearmed {report.PowerFixed} powered device(s) that had lost their power ticker.");
        lines.Add("The ship refills with breathable atmosphere when you load it.");
        lines.Add("");
        lines.Add($"Written to the save {writtenName}.");
        lines.Add("Open the in game Load menu and press Refresh first: Ostranauts won't list a just-written save " +
                  "until you do.");
        lines.Add($"Then load {writtenName} to see your edited ship, with crew and cargo intact.");
        lines.Add("");
        lines.Add(InPlaceWrite.Outcome(plan.Update.InPlace, backupName, "edit"));

        return new DoneReport($"\"{writtenName}\" has your edited ship.", lines);
    }

    private static Task<string?> WriteInPlaceOffThread(SaveShipContext ctx, JsonObject ship, double? balance, bool backup) =>
        Ui.OffThread(() => SaveEdit.WriteInPlace(ctx, ship, balance, backup));

    private static Task WriteCopyOffThread(SaveShipContext ctx, JsonObject ship, string outDir, double? balance) =>
        Ui.OffThread(() => SaveEdit.WriteCopy(ctx, ship, outDir, overwrite: false, balance));

    /// <summary>The loud in-place confirmation. Detects a running Ostranauts and gates on the user confirming they
    /// are at the Main Menu, because editing a loaded save would be clobbered by the next autosave.</summary>
    private static bool ConfirmInPlace(WizardSession session, string saveName, bool backup) =>
        Dlg.Confirm(session.Owner, DlgKind.Danger, $"Overwrite {saveName} in place?",
            InPlaceWrite.GameRunningWarning() + InPlaceWrite.BackupExplanation(backup, "edit"),
            "Overwrite in place");

    /// <summary>How the ship will read in game, and whether that is a change. This destination writes the identity
    /// rather than preserving it, and the in-place write is irreversible, so an unintended edit is worth seeing
    /// before it lands rather than after.</summary>
    private static string IdentitySummary(SaveShipContext ctx, ExportMetadata id)
    {
        var was = SaveEdit.ReadIdentity(ctx);
        // a blank in-game name keeps the ship's own, so compare (and report) what will actually be written
        var name = id.PublicName.Trim() is { Length: > 0 } typed && typed != "$TEMPLATE" ? typed : was.PublicName;
        var effective = id with { PublicName = name };

        var flavor = string.Join(" ", new[] { effective.Make, effective.Model, effective.Designation }
            .Where(s => s.Length > 0));
        var text = string.Join("  ·  ", new[] { name.Length > 0 ? name : "unnamed", flavor }.Where(s => s.Length > 0));
        return effective == was ? $"{text}, unchanged" : $"{text} (changed)";
    }

    /// <summary>The structural change summary. Re-stated parts (uninstalled, installed, a door toggled) are named
    /// separately from added ones, because the save gets a fresh item for them either way but nothing was built.</summary>
    private static string ChangeSummary(InjectReport r)
    {
        var parts = new List<string> { $"{r.Kept} kept", $"{r.Moved} moved" };
        if (r.Reformed > 0) parts.Add($"{r.Reformed} un/installed");
        parts.Add($"{r.Added} added");
        parts.Add($"{r.Deleted} deleted");
        return string.Join(", ", parts);
    }

    private double Cost(ExportPlan plan) =>
        plan.Update.Deduct && _baseCost is { } b
            ? EditCost.Total(b, plan.Update.NewMultiplier, plan.Update.MovedMultiplier)
            : 0;

    private static string Money(double v) =>
        "$" + v.ToString("#,##0.##", System.Globalization.CultureInfo.InvariantCulture);
}

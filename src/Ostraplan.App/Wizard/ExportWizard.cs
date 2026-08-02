using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// The export wizard: one step at a time, with a rail on the left showing where you are and what is left.
///
/// <para>It replaces a single scrolling dialog whose two destinations sat behind a <c>TabControl</c> — headers
/// that were near invisible against the dark background, above about twenty controls that had to be scrolled past
/// whichever destination you wanted. Removing the tabs entirely is what fixes that, rather than restyling
/// them.</para>
///
/// <para>The shell knows nothing about ships, saves or mod folders. It moves between panes, enforces the
/// navigation rules (<see cref="WizardFlow"/>), and hands the work to the destination's
/// <see cref="ExportDriver"/> — prepare on selection, build on Review, write on commit.</para>
/// </summary>
public sealed class ExportWizard : Window
{
    private readonly WizardSession _session;
    private readonly List<ExportDriver> _drivers;
    private readonly Dictionary<StepId, WizardStep> _panes = [];

    private readonly StackPanel _rail;
    private readonly ContentControl _host;
    private readonly ScrollViewer _scroll;
    private readonly Button _back, _next, _cancel;

    private WizardFlow _flow;
    private bool _busy;

    /// <summary>True once a commit landed, so the caller knows the design was actually written somewhere.</summary>
    public bool Committed { get; private set; }

    /// <summary>
    /// True when the wizard changed the <b>design</b> and the user kept it, which today means stand-in parts.
    ///
    /// <para>The caller has to know, because those edits go on the document directly rather than through the undo
    /// stack, so nothing else marks the design as having unsaved changes. Without this the user could close
    /// Ostraplan with no prompt and lose them.</para>
    /// </summary>
    public bool DocumentEdited { get; private set; }

    public ExportWizard(WizardSession session, ExportDestination? preselect = null)
    {
        _session = session;
        session.Owner = this;

        _drivers =
        [
            new ModDriver(),
            new NewShipDriver(),
            new UpdateDriver(),
        ];

        if (preselect is { } d) session.Plan.Destination = d;
        session.Driver = DriverFor(session.Plan.Destination);
        _flow = new WizardFlow(session.Plan.Destination, HasUnresolvedParts());

        Title = "Export";
        Width = 720;
        Height = 600;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        // ---- rail ----
        _rail = new StackPanel { Margin = new Thickness(12, 16, 12, 16) };
        var railHost = new Border
        {
            Width = 190, Background = ThemeManager.PanelBg, BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = ThemeManager.PanelBorder, Child = _rail,
        };

        // ---- content + buttons ----
        _host = new ContentControl();
        _scroll = new ScrollViewer
        {
            Content = _host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(22, 18, 18, 18),
        };

        _back = new Button { Content = "Back", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0) };
        _next = new Button { Content = "Next", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        _cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        _back.Click += (_, _) => GoBack();
        _next.Click += async (_, _) => await GoNext();
        _cancel.Click += (_, _) => Close();
        Closing += OnClosing;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(18, 12, 18, 16),
            Children = { _back, _next, _cancel },
        };

        var right = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        right.Children.Add(buttons);
        right.Children.Add(_scroll);

        var root = new DockPanel();
        DockPanel.SetDock(railHost, Dock.Left);
        root.Children.Add(railHost);
        root.Children.Add(right);
        Content = root;

        BuildPanes();
        _ = OpenAsync(resume: preselect is null && session.Settings.LastExport is not null);
    }

    /// <summary>Exposed for tests: run the open sequence to completion rather than fire-and-forget, so a test can
    /// assert on a wizard whose destination has actually prepared.</summary>
    internal Task OpenedAsync(bool resume = false) => OpenAsync(resume);

    /// <summary>Exposed for tests: whether the user could move on right now.</summary>
    internal bool NextEnabled => _next.IsEnabled;

    /// <summary>Exposed for tests: the pane on screen.</summary>
    internal WizardStep? CurrentPane => Current;

    /// <summary>
    /// Open on the right step. With settings remembered from last time, every step is <b>revalidated against the
    /// world as it is now</b> before anything is shown: the save may have been deleted, the output folder may be
    /// gone. The first step that fails wins, so the user lands somewhere that can explain itself, and only a run
    /// where everything still holds jumps straight to Review.
    /// </summary>
    private async Task OpenAsync(bool resume)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            // Always prepare the destination the wizard opens on, whether it was remembered or preselected by the
            // Analyse menu. Without this a preselected update destination would advance with no located save
            // context behind it, and its cost step would have nothing to cost.
            var blocked = await _session.Driver.PrepareAsync(_session);
            RebuildFlow();
            ((DestinationStep)_panes[StepId.Destination]).SetBlocker(blocked);

            if (resume && blocked is null)
            {
                var valid = new List<bool>();
                foreach (var id in _flow.Steps)
                {
                    if (id is StepId.Review or StepId.Done) { valid.Add(true); continue; }
                    var pane = _panes[id];
                    pane.Populate(_session);
                    valid.Add(pane.Validate() is null);
                    if (valid[^1]) pane.Leave(_session);
                }

                var target = WizardFlow.ResumeIndex(_session.Plan.Destination, valid, HasUnresolvedParts());
                for (var i = 0; i < target; i++) _flow.Complete(i);
                _flow.JumpTo(target);
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        await ShowStep();
    }

    /// <summary>A design whose <c>.oplan</c> was reopened without its mods still has items Ostraplan can't see.
    /// Only the update destination can do anything about them, so only it grows a step.</summary>
    private bool HasUnresolvedParts() =>
        _session.SaveContext is { } ctx
        && Substitution.OutstandingDefs(_session.Doc, ctx, _session.Catalog).Count > 0;

    // ---- panes ----

    /// <summary>
    /// Build every pane the current destination shows. Eager, because the rail needs their titles and because a
    /// pane costs about what one section of the old dialog did.
    ///
    /// <para>Panes already built are kept, not replaced. Switching destination therefore leaves the shared steps
    /// standing with whatever the user typed in them, and leaves the destination step itself alive to finish the
    /// async prepare it started when its own tile was clicked.</para>
    /// </summary>
    private void BuildPanes()
    {
        foreach (var id in _flow.Steps)
        {
            if (_panes.ContainsKey(id)) continue;
            var pane = Create(id);
            pane.Changed += OnPaneChanged;
            _panes[id] = pane;
        }
    }

    private ExportDriver DriverFor(ExportDestination destination) =>
        _drivers.First(d => d.Destination == destination);

    private WizardStep Create(StepId id) => id switch
    {
        StepId.Destination => new DestinationStep(_drivers, OnDestinationPickedAsync),
        StepId.Ship => new ShipStep(),
        StepId.MissingParts => new MissingPartsStep { Palette = _session.Palette },
        StepId.ModDetails => new ModDetailsStep(),
        StepId.Obtainable => new ObtainableStep(),
        StepId.ModTarget => new ModTargetStep(),
        StepId.SavePrice => new SavePriceStep(),
        StepId.UpdateTarget => new UpdateTargetStep(),
        StepId.Review => new ReviewStep(),
        StepId.Done => new DoneStep(),
        _ => throw new NotSupportedException($"No pane for {id} in this build."),
    };

    /// <summary>
    /// A pane's state moved, so anything derived from it is stale. The steps in between keep their completion:
    /// only the build behind Review is thrown away.
    ///
    /// <para>The buttons are refreshed here too, and that is not cosmetic. A pane's <see cref="WizardStep.CanAdvance"/>
    /// can change asynchronously — a destination preparing, a save being read — and the shell has no other moment
    /// to notice. Without it, Next goes dead the first time a slow step finishes and never comes back.</para>
    /// </summary>
    private void OnPaneChanged()
    {
        _session.Plan.Touch();
        _flow.InvalidateReview();
        RefreshRail();
        RefreshButtons();
    }

    /// <summary>
    /// The destination changed, so the whole rail changes with it. Everything already typed survives, because it
    /// lives in the plan rather than in the panes.
    ///
    /// <para>The rail is rebuilt twice on purpose. Once immediately, so the user sees the new steps without waiting,
    /// and once after the driver has prepared, because whether the update path needs a missing-parts step is only
    /// answerable after its save context has been located. Returns the reason the destination cannot be used, which
    /// the step shows inline and which blocks Next.</para>
    /// </summary>
    private async Task<string?> OnDestinationPickedAsync(ExportDestination destination)
    {
        Current?.Leave(_session);
        _session.Plan.Destination = destination;
        _session.Plan.Touch();
        _session.Driver = DriverFor(destination);
        RebuildFlow();
        await ShowStep();

        var reason = await _session.Driver.PrepareAsync(_session);
        RebuildFlow();
        RefreshRail();
        return reason;
    }

    private void RebuildFlow()
    {
        var at = _flow.CurrentStep;
        _flow = new WizardFlow(_session.Plan.Destination, HasUnresolvedParts());
        BuildPanes();
        _flow.GoTo(_flow.IndexOf(at) >= 0 ? at : StepId.Destination);
    }

    private WizardStep? Current => _panes.GetValueOrDefault(_flow.CurrentStep);

    /// <summary>Render whatever step the flow is on. Moving is the caller's job; this only paints.</summary>
    private async Task ShowStep()
    {
        var pane = _panes[_flow.CurrentStep];
        pane.Populate(_session);
        _host.Content = pane;
        _scroll.ScrollToTop();
        RefreshRail();
        RefreshButtons();
        await pane.EnterAsync(_session);
        RefreshButtons();
    }

    // ---- navigation ----

    private void GoBack()
    {
        if (_busy) return;
        Current?.Leave(_session);
        if (_flow.Back()) _ = ShowStep();
    }

    private async Task GoNext()
    {
        if (_busy || Current is not { } pane) return;
        if (pane.Validate() is not null) return;   // the pane has already said why, beside the field
        pane.Leave(_session);

        if (_flow.CurrentStep == StepId.Review)
        {
            await Commit();
            return;
        }

        if (_flow.Advance()) await ShowStep();
    }

    private void JumpFromRail(int index)
    {
        if (_busy || !_flow.CanJumpTo(index)) return;
        Current?.Leave(_session);
        if (_flow.JumpTo(index)) _ = ShowStep();
    }

    // ---- commit ----

    private async Task Commit()
    {
        var review = (ReviewStep)_panes[StepId.Review];
        _busy = true;
        RefreshButtons();
        review.ShowCommitting(_session.Driver.CommitVerb);
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var report = await _session.Driver.WriteAsync(_session);
            Committed = true;
            _session.Plan.SaveTo(_session.Settings);
            _session.Settings.Save();
            _flow.GoTo(StepId.Done);
            ((DoneStep)_panes[StepId.Done]).Set(report);
            _busy = false;
            await ShowStep();
        }
        // the user backed out of a destination's own last confirmation (the in-place overwrite): not a failure,
        // so say nothing and leave them on Review with the button live
        catch (OperationCanceledException)
        {
            review.ShowCancelled();
        }
        // an explainable failure is a write that didn't land, or a save that turned out not to take the edit;
        // anything else is our bug and belongs in error.log with its stack rather than behind a friendly line
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            review.ShowFailure(ex.Message);
        }
        finally
        {
            // in the finally, not per-branch: an exception we deliberately let through to the crash handler would
            // otherwise leave the window busy forever, refusing even to close
            _busy = false;
            Mouse.OverrideCursor = null;
            RefreshButtons();
        }
    }

    /// <summary>
    /// Leaving without writing. A stand-in placed on the missing-parts step is a <b>real edit to the design</b>
    /// (a <see cref="PlaceCommand"/>), not a wizard setting, so it does not simply vanish with the window: the
    /// user is asked whether to keep it. Everything else the wizard collected is settings, and settings going away
    /// when you cancel is what cancelling means.
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }
        if (!_session.Driver.HasDocumentEdits) return;
        if (Committed) { DocumentEdited = true; return; }

        var choice = Dlg.Choose(this, DlgKind.Warning, "Keep the stand-in parts?",
            "You put real parts in place of ones your loaded data doesn't have. That changed the design itself, " +
            "not just this export, so it stays unless you say otherwise.\n\n" +
            "Keeping them leaves the design complete and saveable. Discarding puts the unresolved items back.",
            "Keep them", "Discard them");

        if (choice == MessageDialog.Choice.Cancel) { e.Cancel = true; return; }
        if (choice == MessageDialog.Choice.Secondary) _session.Driver.UndoDocumentEdits(_session);
        else DocumentEdited = true;
    }

    // ---- chrome ----

    private void RefreshButtons()
    {
        var done = _flow.CurrentStep == StepId.Done;
        var review = _flow.CurrentStep == StepId.Review;
        _back.Visibility = done || _flow.Current == 0 ? Visibility.Collapsed : Visibility.Visible;
        _next.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = review ? _session.Driver.CommitVerb : "Next";
        // Enter advances a step, but never performs the write: a reflexive Enter must not be what commits an
        // export, the same reason Dlg keeps its risky confirmations off the default button.
        _next.IsDefault = !review;
        _next.IsEnabled = !_busy && (Current?.CanAdvance ?? true);
        _cancel.Content = done ? "Close" : "Cancel";
        _cancel.IsEnabled = !_busy;
    }

    /// <summary>
    /// Paint the rail. Completed steps read in <see cref="ThemeManager.Ink"/>, the current one is an
    /// <see cref="ThemeManager.AccentBg"/> pill, and anything still ahead is <see cref="ThemeManager.Dim"/>.
    ///
    /// <para>Rows are borders with a click handler, not buttons. A custom <c>Button</c> or <c>ToggleButton</c>
    /// style has to be based on the Fluent implicit style or the visual state manager washes the active state out,
    /// and a rail is not worth that risk when a border does the same job.</para>
    /// </summary>
    private void RefreshRail()
    {
        _rail.Children.Clear();
        _rail.Children.Add(new TextBlock
        {
            Text = "EXPORT", Foreground = ThemeManager.Dim, FontWeight = FontWeights.Bold, FontSize = 11,
            Margin = new Thickness(8, 0, 0, 12),
        });

        for (var i = 0; i < _flow.Steps.Count; i++)
        {
            var id = _flow.Steps[i];
            var current = i == _flow.Current;
            var complete = _flow.IsComplete(i);
            var jumpable = _flow.CanJumpTo(i);

            var row = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 2),
                Background = current ? ThemeManager.AccentBg : Brushes.Transparent,
                Cursor = jumpable ? Cursors.Hand : null,
                Child = new TextBlock
                {
                    Text = _panes[id].Title,
                    Foreground = current ? ThemeManager.AccentText : complete ? ThemeManager.Ink : ThemeManager.Dim,
                    FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            if (jumpable)
            {
                var target = i;
                row.MouseLeftButtonUp += (_, _) => JumpFromRail(target);
            }
            _rail.Children.Add(row);
        }
    }
}

/// <summary>
/// A destination that is real but not wired up in this build. It appears in the list, disabled, saying why —
/// hiding it would teach nobody that it exists, and the reason is what points the user at the thing that does work
/// today.
/// </summary>
internal sealed class PendingDriver(ExportDestination destination, string name, string blurb, string reason)
    : ExportDriver
{
    public override ExportDestination Destination { get; } = destination;
    public override string Name { get; } = name;
    public override string Blurb { get; } = blurb;
    public override string CommitVerb => "Export";
    public override string? Unavailable(WizardSession session) => reason;
    public override Task<BuildOutcome> BuildAsync(WizardSession session) => throw new NotSupportedException();
    public override Task<DoneReport> WriteAsync(WizardSession session) => throw new NotSupportedException();
}

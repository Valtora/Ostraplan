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

    public ExportWizard(WizardSession session, ExportDestination? preselect = null)
    {
        _session = session;
        session.Owner = this;

        _drivers =
        [
            new ModDriver(),
            new NewShipDriver(),
            new PendingDriver(
                ExportDestination.UpdateShipInSave, "Update a ship in a save",
                "Rewrites the ship this design was imported from, keeping its crew and cargo.",
                "Use Analyse ▸ \"Update Ship in Save…\" for now."),
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

    /// <summary>
    /// Open on the right step. With settings remembered from last time, every step is <b>revalidated against the
    /// world as it is now</b> before anything is shown: the save may have been deleted, the output folder may be
    /// gone. The first step that fails wins, so the user lands somewhere that can explain itself, and only a run
    /// where everything still holds jumps straight to Review.
    /// </summary>
    private async Task OpenAsync(bool resume)
    {
        if (!resume)
        {
            await ShowStep();
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await _session.Driver.PrepareAsync(_session);   // reads the remembered save, so its step can validate

            var valid = new List<bool>();
            foreach (var id in _flow.Steps)
            {
                if (id is StepId.Review or StepId.Done) { valid.Add(true); continue; }
                var pane = _panes[id];
                pane.Enter(_session);
                valid.Add(pane.Validate() is null);
                if (valid[^1]) pane.Leave(_session);
            }

            var target = WizardFlow.ResumeIndex(_session.Plan.Destination, valid, HasUnresolvedParts());
            for (var i = 0; i < target; i++) _flow.Complete(i);
            _flow.JumpTo(target);
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
        StepId.Destination => new DestinationStep(_drivers, OnDestinationPicked),
        StepId.Ship => new ShipStep(),
        StepId.ModDetails => new ModDetailsStep(),
        StepId.Obtainable => new ObtainableStep(),
        StepId.ModTarget => new ModTargetStep(),
        StepId.SavePrice => new SavePriceStep(),
        StepId.Review => new ReviewStep(),
        StepId.Done => new DoneStep(),
        _ => throw new NotSupportedException($"No pane for {id} in this build."),
    };

    /// <summary>A pane's state moved, so anything derived from it is stale. The steps in between keep their
    /// completion — only the build behind Review is thrown away.</summary>
    private void OnPaneChanged()
    {
        _session.Plan.Touch();
        _flow.InvalidateReview();
        RefreshRail();
    }

    /// <summary>The destination changed, so the whole rail changes with it. Everything already typed survives,
    /// because it lives in the plan rather than in the panes.</summary>
    private void OnDestinationPicked(ExportDestination destination)
    {
        Current?.Leave(_session);
        _session.Plan.Destination = destination;
        _session.Plan.Touch();
        _session.Driver = DriverFor(destination);
        _flow = new WizardFlow(destination, HasUnresolvedParts());
        BuildPanes();
        _ = ShowStep();
    }

    private WizardStep? Current => _panes.GetValueOrDefault(_flow.CurrentStep);

    /// <summary>Render whatever step the flow is on. Moving is the caller's job; this only paints.</summary>
    private async Task ShowStep()
    {
        var pane = _panes[_flow.CurrentStep];
        pane.Enter(_session);
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
        // an explainable failure is a write that didn't land, or a save that turned out not to take the edit;
        // anything else is our bug and belongs in error.log with its stack rather than behind a friendly line
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _busy = false;
            review.ShowFailure(ex.Message);
            RefreshButtons();
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // ---- chrome ----

    private void RefreshButtons()
    {
        var done = _flow.CurrentStep == StepId.Done;
        _back.Visibility = done || _flow.Current == 0 ? Visibility.Collapsed : Visibility.Visible;
        _next.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = _flow.CurrentStep == StepId.Review ? _session.Driver.CommitVerb : "Next";
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

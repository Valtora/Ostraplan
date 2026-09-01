using System.Windows.Controls;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// The wizard's "Obtainable in game" step: one design's routes, asked the same way the bundle editor asks each of
/// its ships (see <see cref="ObtainablePanel"/>, which is the whole of the pane).
///
/// <para>What is left here is the one thing that is the <b>step's</b> business rather than the panel's: a design
/// aimed only at the derelict fields turns its own condition slider off, and the condition lives on another step.
/// </para>
/// </summary>
public sealed class ObtainableStep : WizardStep
{
    private readonly ObtainablePanel _panel = new();

    public override string Title => "Obtainable in game";

    public ObtainableStep()
    {
        _panel.Changed += OnChanged;
        Content = new ContentControl { Content = _panel };
    }

    public override void Enter(WizardSession session) =>
        _panel.Load(session.Index, session.Plan.Mod.Delivery, session.Doc.Placements.Count, session.BuyEstimate);

    public override string? Validate() => _panel.Validate();

    public override void Leave(WizardSession session)
    {
        var delivery = session.Plan.Mod.Delivery;
        _panel.Save(delivery);

        // A wreck is damaged by the game when it first loads, so baking wear on top would double-damage every
        // part. Only the untouched default is overridden: a user who set the slider themselves keeps their answer.
        if (!session.Plan.WearChosen && delivery.DerelictOnly) session.Plan.Wear = WearOptions.Pristine;
    }
}

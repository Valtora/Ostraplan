using System.Windows;
using System.Windows.Controls;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// Items on this ship whose defs aren't in the loaded data, and what to put in their place.
///
/// <para>Loud by design. Such an item is <b>invisible</b> to Ostraplan yet still sits in the save, and every engine
/// here reads the document, so a missing modded wall no longer divides a room and a missing part at the hull edge
/// no longer sizes the grid. Either writes a ship the game rebuilds differently on load: ghost rooms and shifted
/// zones. The honest fixes are to enable the mod and re-import, or to stand a real part in; this step offers the
/// second, and Review carries an acknowledgement if anything is left unresolved.</para>
///
/// <para>Update-only, and only when there is something to resolve. A stand-in needs the save context to know what
/// it is replacing, and a mod export cannot have unresolved parts at all: a design with missing mods is held
/// read-only from the moment it loads.</para>
///
/// <para>Applying a stand-in is a <b>real edit to the design</b>, not a wizard setting, which is why cancelling
/// the wizard afterwards asks whether to keep it.</para>
/// </summary>
public sealed class MissingPartsStep : WizardStep
{
    private readonly StackPanel _body;
    private readonly TextBlock _headline;
    private readonly StackPanel _rowHost;

    private MissingPartsPanel? _rows;
    private IReadOnlyList<PartVM> _palette = [];

    public override string Title => "Missing parts";

    /// <summary>The palette the stand-in picker offers, handed over by the shell because it is the main window's
    /// list rather than anything the wizard builds.</summary>
    public IReadOnlyList<PartVM> Palette { set => _palette = value; }

    public MissingPartsStep()
    {
        _body = Body();
        _headline = Add(_body, new TextBlock
        {
            Foreground = ThemeManager.Warn, FontSize = 15, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        Note(_body,
            "Ostraplan can't see them, so it can't lay them out or work out the ship's rooms and grid around them. " +
            "Writing back in this state can corrupt rooms and zones.\n\n" +
            "Best fix: cancel, enable the mods these parts come from, and import again.\n\n" +
            "Otherwise stand a real part in for each. A stand-in REPLACES the item in the save you write, so pick " +
            "something the same size where you can. Leaving them is allowed, and Review will say so.");

        _rowHost = Add(_body, new StackPanel { Margin = new Thickness(0, 14, 0, 0) });
        Content = _body;
    }

    public override void Enter(WizardSession session)
    {
        // recomputed every entry: a stand-in applied on the way out is no longer outstanding on the way back in
        var outstanding = session.Driver is UpdateDriver d ? d.Outstanding(session) : [];
        var defs = outstanding
            .GroupBy(u => u.DefName, StringComparer.Ordinal)
            .Select(g => new MissingDefVM(g.Key, g.Count()))
            .OrderByDescending(v => v.Count).ThenBy(v => v.DefName, StringComparer.Ordinal)
            .ToList();

        var items = defs.Sum(d2 => d2.Count);
        _headline.Text = items == 0
            ? "Nothing left unresolved."
            : $"{items} item{(items == 1 ? "" : "s")} use parts that aren't in your loaded data";

        _rowHost.Children.Clear();
        _rows = new MissingPartsPanel(defs, _palette);
        _rows.ChoiceChanged += OnChanged;
        _rowHost.Children.Add(_rows);
    }

    public override void Leave(WizardSession session)
    {
        if (_rows is not { Choices.Count: > 0 } rows || session.Driver is not UpdateDriver d) return;
        d.ApplyStandIns(session, rows.Choices);
    }
}

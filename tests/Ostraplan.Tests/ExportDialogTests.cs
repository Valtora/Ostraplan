using System.Windows.Controls;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The export dialog's two-destination shape. A WPF dialog can't be driven headlessly, but its constructor is
/// where a layout mistake actually lands, and the mode/validation logic around the tabs is plain state — so
/// building it on an STA thread catches both without a human clicking anything.
/// </summary>
public class ExportDialogTests
{
    private static ExportDialog Build(IReadOnlyList<SaveEntry>? saves = null, bool linkedToSave = false) =>
        new("Test Ship", "Author", modsDir: null, lastFolder: null,
            index: null, buyEstimate: 0, ostrasortKnown: false, meta: null,
            saves: saves, linkedToSave: linkedToSave);

    private static TabControl Tabs(ExportDialog dlg) =>
        ((dlg.Content as ScrollViewer)!.Content as Panel)!.Children.OfType<TabControl>().Single();

    [Fact]
    public void Dialog_builds_with_both_destinations()
    {
        RunSta(() =>
        {
            var dlg = Build();

            var tabs = Tabs(dlg);
            Assert.Equal(2, tabs.Items.Count);
            Assert.Equal("As a mod", ((TabItem)tabs.Items[0]!).Header);
            Assert.Equal("Into a save game", ((TabItem)tabs.Items[1]!).Header);
            Assert.Equal(ExportMode.Mod, dlg.Mode);   // the mod path stays the default, as it always was
        });
    }

    [Fact]
    public void Switching_to_the_save_tab_changes_the_mode()
    {
        RunSta(() =>
        {
            var dlg = Build();

            Tabs(dlg).SelectedIndex = 1;

            Assert.Equal(ExportMode.Save, dlg.Mode);
        });
    }

    [Fact]
    public void Identity_and_condition_are_shared_by_both_destinations()
    {
        RunSta(() =>
        {
            var dlg = Build();

            // read on the mod tab...
            var name = dlg.ShipName;
            var wear = dlg.Wear;
            Tabs(dlg).SelectedIndex = 1;

            // ...and unchanged on the save tab: these live above the tabs precisely so they don't diverge
            Assert.Equal(name, dlg.ShipName);
            Assert.Equal(wear, dlg.Wear);
            Assert.Equal("Test Ship", dlg.ShipName);
        });
    }

    [Fact]
    public void Wear_defaults_to_the_vanilla_used_condition()
    {
        RunSta(() =>
        {
            var dlg = Build();

            Assert.True(dlg.Wear.Enabled);
            Assert.Equal(WearModel.VanillaUsedCondition, dlg.Wear.TargetCondition, 2);
        });
    }

    [Fact]
    public void A_grant_is_a_gift_until_the_user_asks_to_be_charged()
    {
        RunSta(() =>
        {
            var dlg = Build();

            Assert.Equal(0, dlg.Price);
            Assert.Null(dlg.GrantContext);   // nothing picked, so nothing to grant into
        });
    }

    [Fact]
    public void An_unreadable_save_reports_itself_instead_of_arming_the_button()
    {
        RunSta(() =>
        {
            // a save entry pointing at nothing: selecting it must surface the failure here, not after the user
            // has committed to a write
            var bogus = new SaveEntry("Ghost Save", "Ship", "Player", "now", @"C:\nope\ghost\ghost.zip");
            var dlg = Build([bogus]);
            var tabs = Tabs(dlg);
            tabs.SelectedIndex = 1;

            var picker = ((TabItem)tabs.Items[1]!).Content as Panel;
            picker!.Children.OfType<ComboBox>().Single().SelectedIndex = 0;

            Assert.Null(dlg.GrantContext);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}

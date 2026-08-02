using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Ostraplan.App.Wizard;

/// <summary>
/// Where the mod folder is written, and whether to hand it to Ostrasort afterwards.
///
/// <para>Ostraplan writes the mod folder only. Registering it in <c>loading_order.json</c> belongs to Ostrasort or
/// ModTools, and this step never pretends otherwise — the tick drives that tool, it does not do its job.</para>
/// </summary>
public sealed class ModTargetStep : WizardStep
{
    private readonly RadioButton _toMods, _toFolder;
    private readonly CheckBox _register;
    private readonly TextBlock _folderPath, _problem;

    private string? _modsDir;
    private string? _picked;
    private bool _loaded;

    public override string Title => "Where to write";

    public ModTargetStep()
    {
        var body = Body();

        _toMods = Add(body, new RadioButton
        {
            Content = "Stage into the game's Mods folder (ready to register & test)",
            Foreground = Ink, IsChecked = true, Margin = new Thickness(0, 2, 0, 2),
        });
        _toFolder = Add(body, new RadioButton
        {
            Content = "Write to a folder…", Foreground = Ink, Margin = new Thickness(0, 2, 0, 2),
        });
        _toMods.Checked += (_, _) => OnChanged();
        _toFolder.Checked += (_, _) => OnChanged();

        var folderRow = Add(body, new DockPanel { Margin = new Thickness(20, 2, 0, 4) });
        var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 2, 10, 2) };
        browse.Click += (_, _) => PickFolder();
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        _folderPath = new TextBlock
        {
            Foreground = Dim, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Text = "(no folder chosen)",
        };
        folderRow.Children.Add(_folderPath);

        _problem = Problem(body, indent: 20);

        _register = Add(body, new CheckBox
        {
            Content = "Register with Ostrasort after exporting (recommended)",
            Foreground = Ink, Margin = new Thickness(0, 14, 0, 2),
        });
        Note(body,
            "Ostraplan writes the mod folder only, and never edits loading_order.json. Ostrasort registers the mod " +
            "(and patches kiosk-loot conflicts) so the ship appears in game. Leave this unticked to register it " +
            "yourself later.", indent: 20);

        Content = body;
    }

    public override void Enter(WizardSession session)
    {
        var mod = session.Plan.Mod;
        _modsDir = session.Env.ModsDir;
        _picked = mod.Folder ?? session.Settings.LastExportDir;

        // First run ever, with nothing remembered: recommend the hand-off if Ostrasort is actually here. Once the
        // user has exported once, their own answer is the one that stands, ticked or not.
        if (!_loaded)
        {
            if (session.Settings.LastExport is null) mod.RegisterWithOstrasort = session.OstrasortKnown;
            _loaded = true;
        }

        _toMods.IsEnabled = _modsDir is not null;
        _toMods.IsChecked = mod.StagedIntoMods && _modsDir is not null;
        _toFolder.IsChecked = !_toMods.IsChecked!.Value;
        _folderPath.Text = _picked ?? "(no folder chosen)";
        _register.IsChecked = mod.RegisterWithOstrasort && _modsDir is not null;
        _register.IsEnabled = _modsDir is not null;
    }

    private void PickFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to write the mod folder" };
        if (_picked is not null) dlg.InitialDirectory = _picked;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        _picked = dlg.FolderName;
        _folderPath.Text = _picked;
        _toFolder.IsChecked = true;
        ShowProblem(_problem, null);
        OnChanged();
    }

    public override string? Validate()
    {
        if (_toFolder.IsChecked != true) return ShowProblem(_problem, null);
        if (string.IsNullOrWhiteSpace(_picked)) return ShowProblem(_problem, "Choose a folder to write to.");
        // a remembered folder can have been moved or deleted since the last export, which is one of the things the
        // wizard revalidates on reopening rather than discovering at the write
        return Directory.Exists(_picked)
            ? ShowProblem(_problem, null)
            : ShowProblem(_problem, $"That folder no longer exists:\n{_picked}");
    }

    public override void Leave(WizardSession session)
    {
        var mod = session.Plan.Mod;
        mod.StagedIntoMods = _toMods.IsChecked == true;
        mod.Folder = _picked;
        // only meaningful for a staged export: a plain folder export is not something Ostrasort can register
        mod.RegisterWithOstrasort = _register.IsChecked == true && mod.StagedIntoMods;
    }
}

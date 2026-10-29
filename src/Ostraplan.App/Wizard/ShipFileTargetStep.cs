using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// Where the bare ship file is written. The folder is remembered between runs; the file name is not, and is taken
/// from the design each time, so exporting a second design never offers to overwrite the first one's file. A name
/// the user picks with Browse stands for the rest of the run.
/// </summary>
public sealed class ShipFileTargetStep : WizardStep
{
    private readonly TextBlock _path, _problem;
    private string? _picked;
    private bool _pickedThisRun;

    public override string Title => "Where to write";

    public ShipFileTargetStep()
    {
        var body = Body();

        var row = Add(body, new DockPanel { Margin = new Thickness(0, 2, 0, 4) });
        var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 2, 10, 2) };
        browse.Click += (_, _) => PickFile();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        _path = new TextBlock
        {
            Foreground = Ink, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Text = "(no file chosen)",
        };
        row.Children.Add(_path);

        _problem = Problem(body);

        Note(body,
            "Put the file in a mod's data/ships folder. The game knows the ship by its strName, which is the design " +
            "name, whatever the file is called. Nothing sells or spawns it until a loot pool in your mod names it.");

        Content = body;
    }

    public override void Enter(WizardSession session)
    {
        var plan = session.Plan;
        if (!_pickedThisRun)
        {
            var folder = Path.GetDirectoryName(plan.ShipFilePath)
                         ?? session.Settings.LastExportDir
                         ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _picked = Path.Combine(folder, ShipExport.SanitizeName(plan.ShipName) + ".json");
        }
        _path.Text = _picked ?? "(no file chosen)";
    }

    private void PickFile()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save the ship file",
            Filter = "Ostranauts ship file (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = false,   // Review asks, alongside everything else it asks before a write
        };
        if (_picked is not null)
        {
            if (Path.GetDirectoryName(_picked) is { } dir && Directory.Exists(dir)) dlg.InitialDirectory = dir;
            dlg.FileName = Path.GetFileName(_picked);
        }
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        _picked = dlg.FileName;
        _pickedThisRun = true;
        _path.Text = _picked;
        ShowProblem(_problem, null);
        OnChanged();
    }

    public override string? Validate()
    {
        if (string.IsNullOrWhiteSpace(_picked)) return ShowProblem(_problem, "Choose where to save the ship file.");
        // a remembered folder can have been moved or deleted since the last export
        return Path.GetDirectoryName(_picked) is { } dir && Directory.Exists(dir)
            ? ShowProblem(_problem, null)
            : ShowProblem(_problem, $"That folder no longer exists:\n{Path.GetDirectoryName(_picked)}");
    }

    public override void Leave(WizardSession session) => session.Plan.ShipFilePath = _picked;
}

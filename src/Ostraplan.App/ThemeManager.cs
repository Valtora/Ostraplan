using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Ostraplan.App;

/// <summary>
/// Light/dark theming for Ostraplan's chrome (toolbar, side panels, dialogs, Help). WPF's Fluent
/// <c>ThemeMode</c> supplies the control chrome — buttons, text boxes, combo boxes, scrollbars,
/// context menus — and this class supplies the app's own panel/text/accent brushes for both palettes,
/// detects the OS theme for "system" mode, and pushes the brushes into <see cref="Application"/>
/// resources so every window's <c>DynamicResource</c> reference tracks the theme. Code-built dialogs
/// read the static brush properties (which return the current palette) at construction.
///
/// <para>The ship <b>canvas stays dark always</b> (<see cref="ShipCanvas"/> owns its own colours): the
/// sprites are pixel art drawn for dark space, so only the surrounding chrome themes.</para>
/// </summary>
public static class ThemeManager
{
    public static string Mode { get; private set; } = "system";   // "system" | "light" | "dark"
    public static bool Dark { get; private set; } = true;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    // Panels / surfaces
    private static readonly Brush DWindow = Frozen(0x23, 0x26, 0x2C), LWindow = Frozen(0xF3, 0xF4, 0xF6);
    private static readonly Brush DPanel = Frozen(0x2B, 0x2F, 0x36), LPanel = Frozen(0xE7, 0xE9, 0xED);
    private static readonly Brush DField = Frozen(0x1C, 0x1E, 0x23), LField = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly Brush DBorder = Frozen(0x3A, 0x3F, 0x47), LBorder = Frozen(0xCD, 0xD1, 0xD7);
    public static Brush WindowBg   => Dark ? DWindow : LWindow;
    public static Brush PanelBg    => Dark ? DPanel  : LPanel;
    public static Brush FieldBg    => Dark ? DField  : LField;
    public static Brush PanelBorder => Dark ? DBorder : LBorder;

    // Text
    private static readonly Brush DInk = Frozen(0xD8, 0xDD, 0xE4), LInk = Frozen(0x1B, 0x1D, 0x21);
    private static readonly Brush DDim = Frozen(0x9A, 0xA3, 0xAF), LDim = Frozen(0x5E, 0x66, 0x72);
    public static Brush Ink => Dark ? DInk : LInk;
    public static Brush Dim => Dark ? DDim : LDim;

    // Accent (Ship Rating button + report headlines) and severity
    private static readonly Brush DAccent = Frozen(0x6E, 0xB6, 0xFF), LAccent = Frozen(0x2A, 0x6F, 0xCF);
    private static readonly Brush DAccentBg = Frozen(0x3A, 0x5A, 0x82), LAccentBg = Frozen(0x2A, 0x6F, 0xCF);
    private static readonly Brush DAccentText = Frozen(0xEA, 0xF2, 0xFB), LAccentText = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly Brush DWarn = Frozen(0xE0, 0xA3, 0x4E), LWarn = Frozen(0xB4, 0x72, 0x0A);
    private static readonly Brush DGood = Frozen(0x5D, 0xBB, 0x7D), LGood = Frozen(0x2E, 0x7D, 0x32);
    private static readonly Brush DBad = Frozen(0xE0, 0x71, 0x5B), LBad = Frozen(0xC0, 0x39, 0x2B);
    private static readonly Brush DKey = Frozen(0xD8, 0xA0, 0x3C), LKey = Frozen(0x9A, 0x6B, 0x00);
    public static Brush Accent     => Dark ? DAccent     : LAccent;
    public static Brush AccentBg   => Dark ? DAccentBg   : LAccentBg;
    public static Brush AccentText => Dark ? DAccentText : LAccentText;
    public static Brush Warn       => Dark ? DWarn : LWarn;
    public static Brush Good       => Dark ? DGood : LGood;
    public static Brush Bad        => Dark ? DBad  : LBad;
    public static Brush KeyAccent  => Dark ? DKey  : LKey;   // the gold keybind labels in Help

    /// <summary>
    /// Set the mode, resolve dark vs light (reading the OS for "system"), and apply it at the
    /// APPLICATION level — Fluent chrome plus the custom brushes every window's DynamicResource
    /// references resolve against. Applying at the app level (before the first window loads) is what
    /// makes the first render fully themed.
    /// </summary>
    public static void Apply(string mode)
    {
        Mode = mode is "light" or "dark" ? mode : "system";
        Dark = Mode switch { "dark" => true, "light" => false, _ => OsIsDark() };

        var app = Application.Current;
        if (app is null) return;   // the --smoke path may have no Application yet
#pragma warning disable WPF0001
        try { app.ThemeMode = Dark ? ThemeMode.Dark : ThemeMode.Light; } catch { }
#pragma warning restore WPF0001

        var r = app.Resources;
        r["WindowBg"] = WindowBg;
        r["PanelBg"] = PanelBg;
        r["FieldBg"] = FieldBg;
        r["PanelBorder"] = PanelBorder;
        r["Ink"] = Ink;
        r["Dim"] = Dim;
        r["Accent"] = Accent;
        r["AccentBg"] = AccentBg;
        r["AccentText"] = AccentText;
        r["Warn"] = Warn;
        r["Bad"] = Bad;
        r["KeyAccent"] = KeyAccent;
    }

    /// <summary>The Windows "apps" theme: AppsUseLightTheme = 0 means dark. Defaults to light if unreadable.</summary>
    private static bool OsIsDark()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int i && i == 0;
        }
        catch { return false; }
    }
}

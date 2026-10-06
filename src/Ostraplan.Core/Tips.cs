using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ostraplan.Core;

/// <summary>What a tip is about (#39). A tip can carry several, and the user can switch any of them off, which
/// hides every tip that carries it. Stored by name in <see cref="TipSettings.HiddenTags"/>, so renaming a member
/// here forgets that choice for anyone who made it.</summary>
public enum TipTag
{
    Basics,
    Editing,
    Shortcuts,
    Workspace,
    Overlays,
    Reports,
    Devices,
    Cargo,
    Simulate,
    ImportExport,
    Apartments,
    Mods,
}

/// <summary>When a tip appears on its own at launch.</summary>
public enum TipStartup
{
    /// <summary>At most once every <see cref="TipSettings.IntervalHours"/>. The default: a tip a launch is a lot
    /// for somebody who restarts the app several times in a sitting.</summary>
    Interval,

    /// <summary>On every launch that is not already showing What's New.</summary>
    EveryLaunch,

    /// <summary>Never. The bulb still shows one on request.</summary>
    Never,
}

/// <summary>One tip: a stable id, a sentence or two for the card, and what it is about.</summary>
/// <param name="Id">What settings remember the tip by. Never reuse one for a different tip: a user who hid the
/// old one would never see the new one.</param>
public sealed record Tip(string Id, string Text, IReadOnlyList<TipTag> Tags);

/// <summary>
/// Every tip, in the order a user meets them (#39). The order runs from the first things a new user needs to the
/// corners of the app, so stepping back through the card goes back over ground already covered.
///
/// <para><b>A feature a player could miss gets its tip in the same commit</b>, the same as its CHANGELOG entry
/// (DEVELOPMENT.md). Put it where it belongs in the order rather than at the end: an existing user sees an unseen
/// tip wherever it sits, because <see cref="TipPicker.NextUnseen"/> works from what has been seen rather than from a
/// position in the list.</para>
///
/// <para>Text on screen is short (CONVENTIONS), and a tip is held to <see cref="MaxLength"/> by a test. Name a key
/// in the tip when the key is the point, and tag it <see cref="TipTag.Shortcuts"/> only then.</para>
/// </summary>
public static class Tips
{
    /// <summary>The longest a tip may be. The card is a fixed width, and anything longer reads as a manual page.</summary>
    public const int MaxLength = 180;

    private static Tip T(string id, string text, params TipTag[] tags) => new(id, text, tags);

    public static IReadOnlyList<Tip> All { get; } =
    [
        T("welcome", "Tips like this one appear at startup now and then. The bulb in the toolbar shows the next one, and Settings ▸ Tips turns them off.",
            TipTag.Basics),
        T("arm-place", "Click a part in the palette to arm it, then click the plan to place it. Esc disarms.",
            TipTag.Basics),
        T("palette-search", "The search box above the palette finds a part by its in-game name or by its internal one.",
            TipTag.Basics),
        T("red-ghost", "A red ghost means the game would refuse the part there. The status bar says why, such as needing a wall alongside.",
            TipTag.Basics),
        T("airlock", "Every design starts with a primary airlock that can't be moved or deleted. Nothing can be built in the red-striped area beyond it.",
            TipTag.Basics),
        T("paint-box", "Drag to paint a part along a line. Shift+drag fills a box, and Ctrl+Shift+drag places only its outline, for a hollow room.",
            TipTag.Basics, TipTag.Shortcuts),
        T("rotate", "R rotates the armed part and Shift+R turns it back. The angle sticks when you arm the next part, so a row of consoles can all face the same way.",
            TipTag.Basics, TipTag.Shortcuts),
        T("use-points", "Blue footprints under the build cursor mark the side a part is worked from, the same mark the game shows while you install it.",
            TipTag.Basics),
        T("navigate", "WASD pans the plan, faster with Shift held. Q and E turn the view in 90° steps, like the in-game camera.",
            TipTag.Basics, TipTag.Shortcuts),
        T("controls-list", "F1 lists every control and keybind.",
            TipTag.Basics, TipTag.Shortcuts),
        T("favourites", "Click the ☆ on a palette row to pin the part. Pinned parts and the last eight you placed are on the FAV/REC tab.",
            TipTag.Basics),
        T("special-tab", "The SPECIAL tab holds structure the game places but never lets you build, such as asteroid cores, signs, station kiosks and transit lifts.",
            TipTag.Basics),
        T("problems", "The Problems list in the inspector holds everything wrong with the design. View on an entry takes the plan straight to it.",
            TipTag.Basics),
        T("undo-history", "Hold or right-click Undo or Redo for the list of edits, and pick one to go back to it in one step.",
            TipTag.Editing),
        T("flood-select", "Double-click a wall or floor to select the whole connected run. Ctrl+double-click adds it to the selection.",
            TipTag.Editing),
        T("fill-room", "Double-click the empty space inside a sealed room to select it, then arm a floor and press Enter to fill the room.",
            TipTag.Editing),
        T("eyedropper", "Alt+click a part on the plan to arm it at its own rotation and carry on painting with it.",
            TipTag.Editing, TipTag.Shortcuts),
        T("replace", "Ctrl+R swaps the selection for another part of the same layer and size. Find and Replace All on the right-click menu does every copy in the ship.",
            TipTag.Editing),
        T("duplicate", "Ctrl+D duplicates the selection just beside the original, with its names, contents and device settings.",
            TipTag.Editing, TipTag.Shortcuts),
        T("flip", "H mirrors the selection left to right and Shift+H top to bottom. Flip a whole room to mirror it.",
            TipTag.Editing, TipTag.Shortcuts),
        T("symmetry", "M turns on Symmetry at the tile under the cursor, and everything you place is mirrored. Drag the diamond handle to move the axes.",
            TipTag.Editing),
        T("filter-select", "With nothing armed, Shift+drag to box-select, then use the chips to keep only some layers: the walls alone, say, or only what's lying on the floor.",
            TipTag.Editing),
        T("step-down", "Press ` to select the next thing down the pile under the cursor, such as the floor under a crate.",
            TipTag.Editing, TipTag.Shortcuts),
        T("restack", "Ctrl+[ and Ctrl+] move the selected part back or forward in its tile's draw order. Right-click ▸ Reset order puts the pile back.",
            TipTag.Editing, TipTag.Shortcuts),
        T("surfaces", "Surfaces mode (T) ghosts everything but walls and floors, so you can re-skin the deck without touching rooms or the rating.",
            TipTag.Editing),
        T("surface-patterns", "In Surfaces mode, set a second skin in the B slot and pick Checker, Rows or Columns to paint two-tone patterns.",
            TipTag.Editing),
        T("reskin-ship", "Design ▸ Ship Re-skin swaps every wall or floor skin on the ship at once, without touching rooms or the rating.",
            TipTag.Editing),
        T("naming", "Click the name at the top of the inspector to rename a part or a deck item. The name goes into the game with it.",
            TipTag.Editing),
        T("zones", "Zones (Z) shows haul, barter and forbid zones. Add one in the Zones panel and paint it the way you paint parts.",
            TipTag.Editing),
        T("zone-paint", "While painting a zone, Ctrl+drag erases it, and a double-click inside walls fills the whole room.",
            TipTag.Editing),
        T("build-last", "When two parts can only go up in one order, right-click the one that has to be built last ▸ Build last.",
            TipTag.Editing),
        T("force", "Force places parts the placement law refuses. Try Build last first: Force never makes a placement legal.",
            TipTag.Editing),
        T("open-several", "File ▸ Open takes several designs at once, each in its own tab. A design that's already open comes forward instead.",
            TipTag.Workspace),
        T("copy-tabs", "Copy and paste work between tabs, and a paste keeps names, contents, fills and device settings.",
            TipTag.Workspace),
        T("switch-tabs", "Ctrl+Tab steps through your open designs, and Ctrl+W closes the one on screen.",
            TipTag.Workspace, TipTag.Shortcuts),
        T("lock", "The padlock beside Undo locks a saved design so nothing can change it. Alt+Shift+O opens designs already locked.",
            TipTag.Workspace),
        T("backup", "Unsaved changes are backed up every 30 seconds, so a crash or a power cut loses nothing. Settings ▸ Tabs sets how often.",
            TipTag.Workspace),
        T("auto-save", "File ▸ Auto-save keeps a few rotating snapshots of each design. It's off until you turn it on, and never writes your own file.",
            TipTag.Workspace),
        T("hide-panels", "F2 hides the palette and F3 the inspector, giving the plan the whole window. Press again to bring them back.",
            TipTag.Workspace, TipTag.Shortcuts),
        T("backdrop", "Settings ▸ The plan's backdrop draws the plan on a colour, a checkerboard or one of the game's own places. PNG snapshots use it too.",
            TipTag.Workspace),
        T("report-bug", "Help ▸ Report a Bug opens a filled-in GitHub issue and writes a diagnostics file to attach, with your name and paths taken out.",
            TipTag.Workspace),
        T("rooms", "Rooms (C) labels each compartment with what it certifies as. An uncertified one names the item in it that's spoiling it.",
            TipTag.Overlays),
        T("walk", "Walk (K) colours the tiles crew can reach. Two colours mean there's no route between them, and a red ring marks a fitting nobody can use.",
            TipTag.Overlays),
        T("walk-options", "The ▾ beside Walk adds routes over the hull in a suit (EVA Access), and decides whether Forbid zones stop crew.",
            TipTag.Overlays),
        T("walk-doors", "In Walk, a closed door only cuts a ship in two if it's unpowered, locked or damaged. Crew simply open a powered one.",
            TipTag.Overlays),
        T("access", "Access (J) shows the tile crew would work a fitting from. Point at a part, or select it to pin the mark.",
            TipTag.Overlays),
        T("power", "Power (P) shows which conduit runs are live. Dim red runs reach no source, and amber marks a device with no feed.",
            TipTag.Overlays),
        T("light", "Light (L) lights the plan the way the game does. The ▾ beside it adds sunlight from outside the hull.",
            TipTag.Overlays),
        T("rating", "Ship Rating explains every room, leak and rating slot. It stays open while you edit, and Re-run updates it.",
            TipTag.Reports),
        T("value-opportunities", "Value Opportunities, folded shut in the Ship Rating, says what each room could become and what it would add. Show marks the room.",
            TipTag.Reports),
        T("o2-bonus", "An air pump fed by an O2 canister on its gas-input tile triples what the ship sells for.",
            TipTag.Reports),
        T("dead-weight", "Dead weight to haul, beside the Ship Rating's propulsion figures, models a ship under tow or a hold of salvage.",
            TipTag.Reports),
        T("delta-v", "More thrusters buy acceleration, not delta-v. Delta-v is reaction mass over ship mass.",
            TipTag.Reports),
        T("reaction-mass", "Only tanks on an RCS distributor's gas inputs count as reaction mass. A canister in a rack feeds nothing.",
            TipTag.Reports, TipTag.Devices),
        T("diagnostics", "Diagnostics runs the nav console's checklist against the plan, so you find out you forgot the antenna before you build.",
            TipTag.Reports),
        T("backup-power", "Diagnostics reads backup power at the nav console's own inputs, so a battery your conduits never reach counts for nothing.",
            TipTag.Reports),
        T("materials", "Materials (Ctrl+B) counts install kits. Retrofit from… prices the conversion from a ship you already have.",
            TipTag.Reports),
        T("power-budget", "Design ▸ Power Budget says how long the batteries last against everything switched on.",
            TipTag.Reports),
        T("flight", "Design ▸ Flight Dynamics says whether the design flies at a body and altitude you pick. Doubling its mass cuts lift to a quarter.",
            TipTag.Reports),
        T("docking", "Design ▸ Docking Compatibility checks whether an airlock will mate with another ship, and draws the two docked on the plan.",
            TipTag.Reports),
        T("docking-cargo", "A crate near an airlock can stop a dock the same as a wall can, so a ship that docks empty may not once it's loaded.",
            TipTag.Reports),
        T("connector-badges", "While you place a powered part, IN and OUT badges mark its plugs, so you can turn it to meet a conduit before you click.",
            TipTag.Devices),
        T("sensor-wiring", "A pump, scrubber, heater or cooler does nothing until it follows a sensor. Right-click it ▸ Wiring… and click its alarm or thermostat.",
            TipTag.Devices),
        T("one-sensor", "One sensor can drive as many devices as you like, so a single thermostat can run every heater and cooler on a deck.",
            TipTag.Devices),
        T("bus-knob", "A device's Bus in the inspector is Auto to follow its sensor, On to run regardless, or Off to stop it dead.",
            TipTag.Devices),
        T("signal-box", "A signal box can switch anything installed on and off, from lights to doors and pumps. Right-click it ▸ Wiring… and click each one.",
            TipTag.Devices),
        T("switch-on", "Right-click a device for Switch on or Switch off. The rating and Diagnostics ignore anything switched off, a transponder included.",
            TipTag.Devices),
        T("reactor", "Select a fusion core to set its panel, and the ship can spawn with its reactor already lit.",
            TipTag.Devices),
        T("firing-groups", "Design ▸ Firing Groups sets every weapon's group at once, sorted by the side each one faces.",
            TipTag.Devices),
        T("arrange-console", "Right-click a nav console ▸ Arrange screen… to lay its modules out, drawn as the panels you'd see at the console.",
            TipTag.Devices),
        T("items-tab", "The ITEMS tab lays loose cargo on the deck, or straight into the container under the cursor if it takes it.",
            TipTag.Cargo),
        T("contents", "Select a container and press Enter to see and edit what's inside. Its contents go into the game with the ship.",
            TipTag.Cargo),
        T("pockets", "A backpack's pouches and a suit's compartments show in the contents window, and you can drag items straight between them.",
            TipTag.Cargo),
        T("make-loose", "Right-click a fitting ▸ Make Loose Item to uninstall it where it stands, and Install item to put it back.",
            TipTag.Cargo, TipTag.Editing),
        T("fill-tanks", "Right-click a canister or tank ▸ Fill… to set what it holds. The gases share one pressure budget, as they do in game.",
            TipTag.Cargo),
        T("manifest", "Design ▸ Item Manifest lists every item aboard, wherever it is. Right-click a type to remove all of them in one step.",
            TipTag.Cargo),
        T("manifest-location", "Item Manifest can list by location rather than by type, and show one zone at a time.",
            TipTag.Cargo),
        T("run-spawners", "Right-click a loot spawner ▸ Run spawner… to roll what it would make and lay it on the deck.",
            TipTag.Cargo),
        T("spawner-seed", "Running a loot spawner reports the seed it used. Type it back in to get exactly the same roll again.",
            TipTag.Cargo),
        T("strike", "Simulate ▸ Micrometeoroid Strike: drag a line across the plan to fire, and see what breaks. The damage is never saved.",
            TipTag.Simulate),
        T("weapon-impact", "Simulate ▸ Weapon Impact fires any weapon your install declares. A missile can take a wall from whole to gone in one shot.",
            TipTag.Simulate),
        T("strike-report", "After a strike, the window says which rooms lost their air, what crew can no longer reach and what lost power.",
            TipTag.Simulate),
        T("damage-brush", "Simulate ▸ Damage Brush paints wear that's kept and exported. A range such as 25 to 70% looks lived-in, and nothing shows above 80%.",
            TipTag.Simulate),
        T("repair-all", "Design ▸ Repair All swaps every broken part for its working form, using the game's own repair jobs.",
            TipTag.Editing),
        T("repair-part", "To repair only part of a ship, select it and right-click ▸ Repair. The entry appears when something in the selection is broken.",
            TipTag.Editing),
        T("import-save", "File ▸ Import can bring your own ship out of a save, cargo and wiring included, as a design to edit.",
            TipTag.ImportExport),
        T("import-options", "Importing a template or a save's layout asks whether to bring container contents, deck items and loot spawners, and remembers.",
            TipTag.ImportExport),
        T("edit-live", "Import your ship for editing, redesign it, and Export writes it back into a copy of the save with crew and cargo kept.",
            TipTag.ImportExport),
        T("main-menu", "Write to a save from the game's Main Menu, not with that save loaded, or the game's next autosave writes over your change.",
            TipTag.ImportExport),
        T("save-copy", "A write into a save goes to a copy unless you choose otherwise. Editing in place keeps a backup unless you untick it.",
            TipTag.ImportExport),
        T("export-mod", "Export (Ctrl+E) turns the design into a mod. Give it at least one way into the game: a broker, a special offer, a starting ship or derelict salvage.",
            TipTag.ImportExport),
        T("replace-ship", "A mod export can replace an existing ship, so the game spawns your design in its place everywhere.",
            TipTag.ImportExport, TipTag.Mods),
        T("add-to-save", "Export can add a design to a copy of a save as a ship you own. It arrives a few km out, and the P.A.S.S. ferry takes you to it.",
            TipTag.ImportExport),
        T("transfer", "A ship can be copied from one save into another with its cargo, wiring and condition. Neither original save is changed.",
            TipTag.ImportExport),
        T("ship-pack", "Several designs can go into one mod as a ship pack, so two ships sharing a kiosk both turn up.",
            TipTag.ImportExport, TipTag.Mods),
        T("apartment", "The game stores an apartment as a ship. Open a residence template or your own out of a save, redesign it, and write it back.",
            TipTag.Apartments),
        T("apartment-report", "On an apartment, Ship Rating becomes Residence Report, and the nav checklist and flight figures step aside.",
            TipTag.Apartments),
        T("missing-mods", "A design whose mods are missing opens read-only, so nothing is dropped. Enable the mods, or save to drop those parts for good.",
            TipTag.Mods),
        T("mod-overrides", "Settings ▸ Mod overrides lets a modded part go where the core rules say it doesn't fit. Check it in game.",
            TipTag.Mods),
        T("modded-warning", "A modded part that breaks the core placement rules is a yellow warning, not an error, because a mod can bring rules of its own.",
            TipTag.Mods),
    ];

    /// <summary>What a topic is called on screen.</summary>
    public static string Label(TipTag tag) => tag switch
    {
        TipTag.ImportExport => "Import & export",
        _ => tag.ToString(),
    };
}

/// <summary>
/// The tip system's settings and memory (#39): whether tips show at all, when, which ones the user has turned
/// off, and which they have already seen. Lives in <see cref="AppSettings.Tips"/>.
///
/// <para>Tips are remembered by id and topics by name, never by position, so adding, removing or reordering tips
/// in a later build keeps every choice a user made. An id or a name this build does not know is kept and ignored:
/// it belongs to a tip that was removed, or to one a newer build has.</para>
/// </summary>
public sealed class TipSettings
{
    public const int DefaultIntervalHours = 8;
    public const int MinIntervalHours = 1;
    public const int MaxIntervalHours = 48;

    /// <summary>The whole feature. Off means no tip at startup and no bulb; Help ▸ Tips still lists them.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>When a tip appears on its own, by <see cref="TipStartup"/> name. Read through
    /// <see cref="StartupMode"/>, which puts anything unrecognised back to the default.</summary>
    [JsonPropertyName("atStartup")] public string? AtStartup { get; set; }

    /// <summary>Hours between startup tips under <see cref="TipStartup.Interval"/>. Read through
    /// <see cref="Hours"/>, which clamps a hand-edited value.</summary>
    [JsonPropertyName("intervalHours")] public int IntervalHours { get; set; } = DefaultIntervalHours;

    /// <summary>Whether the bulb sits in the toolbar.</summary>
    [JsonPropertyName("showBulb")] public bool ShowBulb { get; set; } = true;

    /// <summary>Topics switched off, by <see cref="TipTag"/> name.</summary>
    [JsonPropertyName("hiddenTags")] public List<string> HiddenTags { get; set; } = [];

    /// <summary>Single tips switched off, by id.</summary>
    [JsonPropertyName("hiddenTips")] public List<string> HiddenTips { get; set; } = [];

    /// <summary>Every tip the card has shown, by id. What decides the next tip and whether there is one to show at
    /// startup at all.</summary>
    [JsonPropertyName("seen")] public List<string> Seen { get; set; } = [];

    /// <summary>The tip the card showed last, so the bulb carries on from it once everything has been seen.</summary>
    [JsonPropertyName("last")] public string? Last { get; set; }

    /// <summary>When the card last showed a tip, by any route. The startup interval is measured from it.</summary>
    [JsonPropertyName("lastShownUtc")] public DateTime? LastShownUtc { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public TipStartup StartupMode =>
        Enum.TryParse<TipStartup>(AtStartup, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : TipStartup.Interval;

    public int Hours => ClampHours(IntervalHours);

    public static int ClampHours(int hours) => Math.Clamp(hours, MinIntervalHours, MaxIntervalHours);

    public bool IsTagHidden(TipTag tag) => HiddenTags.Contains(tag.ToString(), StringComparer.OrdinalIgnoreCase);

    public void SetTagHidden(TipTag tag, bool hidden) => Toggle(HiddenTags, tag.ToString(), hidden);

    public bool IsTipHidden(string id) => HiddenTips.Contains(id, StringComparer.Ordinal);

    public void SetTipHidden(string id, bool hidden) => Toggle(HiddenTips, id, hidden);

    private static void Toggle(List<string> list, string value, bool present)
    {
        list.RemoveAll(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
        if (present) list.Add(value);
    }
}

/// <summary>
/// Which tip comes next, and whether one is due (#39). Works on any list of tips so the tests can hand it a short
/// one; the app passes <see cref="Tips.All"/>.
/// </summary>
public static class TipPicker
{
    /// <summary>Whether a tip may be shown: the user has not hidden it, nor any topic it carries.</summary>
    public static bool IsShown(Tip tip, TipSettings s) =>
        !s.IsTipHidden(tip.Id) && !tip.Tags.Any(s.IsTagHidden);

    public static IReadOnlyList<Tip> Shown(IReadOnlyList<Tip> tips, TipSettings s) =>
        [.. tips.Where(t => IsShown(t, s))];

    /// <summary>The first tip, in list order, that may be shown and has not been. Null once every one has.</summary>
    public static Tip? NextUnseen(IReadOnlyList<Tip> tips, TipSettings s) =>
        tips.FirstOrDefault(t => IsShown(t, s) && !s.Seen.Contains(t.Id, StringComparer.Ordinal));

    /// <summary>
    /// Whether a tip should appear by itself at this launch. Only while there is an unseen one: once everything has
    /// been seen the startup tip stops, and the next build that adds a tip starts it again.
    /// </summary>
    public static bool DueAtStartup(IReadOnlyList<Tip> tips, TipSettings s, DateTime nowUtc)
    {
        if (!s.Enabled || NextUnseen(tips, s) is null) return false;
        return s.StartupMode switch
        {
            TipStartup.Never => false,
            TipStartup.EveryLaunch => true,
            // A time in the future means the clock went back since. Measuring from it would hold tips off until the
            // clock caught up again, which could be days, so it counts as due.
            _ => s.LastShownUtc is not { } last || last > nowUtc || nowUtc - last >= TimeSpan.FromHours(s.Hours),
        };
    }

    /// <summary>
    /// The shown tip before or after <paramref name="from"/> in list order, wrapping at either end. Works from a tip
    /// that is no longer shown itself (the user has just hidden it), which is how hiding one moves on to the next.
    /// Null when nothing may be shown.
    /// </summary>
    public static Tip? Step(IReadOnlyList<Tip> tips, TipSettings s, Tip from, int direction)
    {
        var at = IndexOf(tips, from.Id);
        if (at < 0) return Shown(tips, s).FirstOrDefault();
        var step = direction < 0 ? -1 : 1;
        for (var i = 1; i <= tips.Count; i++)
        {
            var t = tips[((at + step * i) % tips.Count + tips.Count) % tips.Count];
            if (IsShown(t, s)) return t;
        }
        return null;
    }

    /// <summary>What the bulb opens on: the next unseen tip, and once there is none, the one after the last shown,
    /// so it carries on round the list. Null when nothing may be shown.</summary>
    public static Tip? ForBulb(IReadOnlyList<Tip> tips, TipSettings s)
    {
        if (NextUnseen(tips, s) is { } next) return next;
        if (s.Last is { } last && tips.FirstOrDefault(t => t.Id == last) is { } previous) return Step(tips, s, previous, +1);
        return Shown(tips, s).FirstOrDefault();
    }

    /// <summary>Record that the card is showing <paramref name="tip"/>. The caller saves the settings.</summary>
    public static void MarkShown(TipSettings s, Tip tip, DateTime nowUtc)
    {
        if (!s.Seen.Contains(tip.Id, StringComparer.Ordinal)) s.Seen.Add(tip.Id);
        s.Last = tip.Id;
        s.LastShownUtc = nowUtc;
    }

    /// <summary>The tip's place among the tips that may be shown, 1-based, and how many there are. Position 0 for a
    /// tip that is not shown itself.</summary>
    public static (int Position, int Count) PositionOf(IReadOnlyList<Tip> tips, TipSettings s, Tip tip)
    {
        var shown = Shown(tips, s);
        var at = shown.ToList().FindIndex(t => t.Id == tip.Id);
        return (at + 1, shown.Count);
    }

    private static int IndexOf(IReadOnlyList<Tip> tips, string id)
    {
        for (var i = 0; i < tips.Count; i++)
            if (tips[i].Id == id) return i;
        return -1;
    }
}

namespace Ostraplan.Core;

/// <summary>
/// Which contacts a point-defence cannon engages. The game stores this as <b>two</b> mutually exclusive
/// conditions rather than one value (<c>MFDWeaponDetails.OnButtonDown</c> cycles none → <c>MMMOnly</c> →
/// <c>ShipsOnly</c> → none, and never sets both), so it is modelled here as the tri-state it actually is: a design
/// that said "ships only" and "non-ships only" at once would have no meaning to write out.
/// </summary>
public enum PdcTargetMode
{
    /// <summary>Neither cond set. The cannon engages ships, missiles and meteoroids alike — the state every def
    /// ships in, since none of them declare either condition.</summary>
    All = 0,

    /// <summary><c>IsPDCTargetModeMMMOnly</c>. <c>NavModWeaponsControl</c> skips the weapon unless the combat
    /// target is a <c>Projectile</c>, so it holds fire against ships and saves its rounds for what is incoming.</summary>
    NonShips = 1,

    /// <summary><c>IsPDCTargetModeShipsOnly</c>. Load-bearing in the other direction:
    /// <c>WeaponsSystem.ActivateDefenseSystems</c> drops a weapon carrying it from the point-defence volley
    /// entirely, so a ship whose cannons are all set this way has no missile or meteoroid defence at all.</summary>
    Ships = 2,
}

/// <summary>
/// Where a weapon points, ship-relative — the axis the Firing Groups editor sorts by, since a group that mixes
/// both beams is a group that can only ever half-fire at one target.
///
/// <para>Derived from the placement's rotation and nothing else, exactly as the game derives it:
/// <c>WeaponsSystem.GetItemsDefaultFiringAngle</c> is <c>ship.fRot + rad(item.fLastRotation)</c>, and the item's
/// own rotation is the only per-weapon term. See <see cref="WeaponPanel.Facing"/> for the mapping out of document
/// rotation.</para>
/// </summary>
public enum WeaponFacing
{
    /// <summary>Up the plan, toward the nose.</summary>
    Fore,
    /// <summary>Right of the plan.</summary>
    Starboard,
    /// <summary>Down the plan, toward the tail.</summary>
    Aft,
    /// <summary>Left of the plan.</summary>
    Port,

    /// <summary>The weapon has no meaningful bearing: its arc covers the whole circle (both launcher families are
    /// <c>IsShipWeaponArcAngle</c> 360) or it declares no arc at all. Sorting one of these under a heading would
    /// be inventing a fact about it.</summary>
    Any,
}

/// <summary>What kind of weapon a def is — one type condition each, on all fifty stock weapon defs.</summary>
public enum WeaponClass
{
    /// <summary>Not a weapon, or a weapon carrying no recognised type cond.</summary>
    Unknown,
    /// <summary><c>IsShipWeaponPDC</c>. 85° arc, the only class offered a target mode.</summary>
    PointDefence,
    /// <summary><c>IsShipWeaponMassThrower</c>. The narrow, long-ranged main gun: 15-20° arc out to 120 km.</summary>
    MassThrower,
    /// <summary><c>IsShipWeaponMissileLauncher</c>. 360° arc.</summary>
    MissileLauncher,
    /// <summary><c>IsShipWeaponDecoyLauncher</c>. 360° arc, defensive.</summary>
    DecoyLauncher,
}

/// <summary>
/// What the designer set on a weapon's own page of the Weapons MFD: which firing group it answers to, whether it
/// waits to be fired by hand, and — on a point-defence cannon — what it will shoot at.
///
/// <para><b>These are conditions on the weapon, not a structure on the ship.</b> There is no firing-group object
/// anywhere in the game: nine weapons in group 3 is nine weapons each carrying <c>IsShipWeaponFiringGroup</c> at
/// amount 2. <c>WeaponsSystem.ShootManual(g)</c> walks the ship's installed, undamaged, powered, not-off weapons
/// and fires the ones whose amount is <c>g - 1</c>. That is the whole mechanism, which is why this rides the same
/// per-instance condition route a container's fill does rather than needing a panel of its own.</para>
///
/// <para><b>Why a design should carry it.</b> Not one of the 220 core <c>data/ships</c> files authors a firing
/// group, so every ship in the game spawns with its PDCs in group 3 and its launchers in group 2 whatever its
/// designer intended, and the player re-does the work by hand in a menu that changes one weapon at a time. A
/// design that says nothing here is not neutral; it is a design that hands the player a chore.</para>
///
/// <para>Unlike <see cref="DeviceSettings"/> and <see cref="ReactorSettings"/> the def's own value is <b>not</b>
/// the same for every def, so <see cref="Group"/> is nullable and null means "whatever this def ships with" (see
/// <see cref="WeaponPanel.DefaultGroup"/>). The other two are absent from every stock def, so their defaults are
/// ordinary constants.</para>
/// </summary>
public sealed record WeaponSettings
{
    /// <summary>
    /// The firing group, <b>0-based, as the game stores it</b>: <c>IsShipWeaponFiringGroup</c>'s amount, 0 to 8.
    /// Null for a weapon left at its def's own group, which is what nearly every weapon in a design is.
    ///
    /// <para>Stored 0-based and shown 1-based, because that is the split the game itself makes: both MFD readouts
    /// print <c>RoundToInt(GetCondAmount(...)) + 1</c> and <c>ShootManual</c> matches <c>firingGroup == amount + 1</c>.
    /// Storing the displayed number would put a translation between this record and every cond it writes, and one
    /// of those translations would eventually be forgotten. <see cref="WeaponPanel.ToDisplay"/> and
    /// <see cref="WeaponPanel.FromDisplay"/> are the only places the +1 lives.</para>
    /// </summary>
    public int? Group { get; init; }

    /// <summary>
    /// Manual firing mode (<c>IsShipWeaponFiringModeManual</c>): the weapon is left out of
    /// <c>NavModWeaponsControl</c>'s auto-fire list and answers only to its firing group's key or button. False —
    /// automatic, firing itself once its targeting solution converges — is what every stock def ships as, since
    /// none of them declare the condition at all.
    /// </summary>
    public bool Manual { get; init; }

    /// <summary>What a point-defence cannon will engage. Meaningless on anything else and dropped by
    /// <see cref="ClampTo"/>, matching the game's own page, which shows the control only when the weapon has
    /// <c>IsShipWeaponPDC</c>.</summary>
    public PdcTargetMode TargetMode { get; init; }

    /// <summary>Everything as the game's own data ships it: the def's group, automatic fire, engage anything. The
    /// document stores null rather than one of these, so an untouched weapon costs nothing.</summary>
    public static readonly WeaponSettings Default = new();

    public bool IsDefault => Equals(Default);

    /// <summary>This object, or null when it is the default — the form the document and the <c>.oplan</c> store.</summary>
    public WeaponSettings? OrNull() => IsDefault ? null : this;

    /// <summary>
    /// This object with anything out of range or inapplicable dropped: the group forced into 0..8, and the target
    /// mode cleared on a weapon that is not a point-defence cannon.
    ///
    /// <para>The group is <b>not</b> gated on the def declaring the condition, which is a deliberate departure
    /// from <see cref="DeviceSettings.Applicable"/>. Eleven of the twelve mass-thrower defs declare no
    /// <c>IsShipWeaponFiringGroup</c> at all, and a ship's main gun that cannot be assigned to a group is a hole
    /// in the feature rather than a safety measure. Authoring it is also exactly what the game does:
    /// <c>CondOwner.AddCondAmount</c> falls back to <c>DataHandler.GetCond</c> when the owner's own map has no
    /// such cond, and the MFD's own Apply To All writes all five keys onto every weapon of the type without
    /// checking either.</para>
    ///
    /// <para>The target mode is gated, because the difference is real: <c>NavModWeaponsControl</c> and
    /// <c>ActivateDefenseSystems</c> read those two conds only down paths a cannon reaches, so setting one on a
    /// missile launcher writes a fact nothing will ever act on.</para>
    /// </summary>
    public WeaponSettings ClampTo(PartDef? part) => this with
    {
        Group = Group is { } g ? Math.Clamp(g, WeaponPanel.MinGroup, WeaponPanel.MaxGroup) : null,
        TargetMode = WeaponPanel.OffersTargetMode(part) && Enum.IsDefined(TargetMode) ? TargetMode : PdcTargetMode.All,
    };
}

/// <summary>
/// The weapons half of a nav console's Weapons MFD, ported (verified game <c>1.0.0.13</c>): which parts are
/// weapons, what firing group each answers to, and the two other per-weapon switches that page carries.
///
/// <para><b>The editing problem this exists to solve.</b> In game the only editor is
/// <c>MFDWeaponDetails</c>: one weapon at a time, its group cycled by a button that steps 0..8 and wraps
/// (<c>CycleFiringGroup</c>). The single bulk affordance is <c>ApplyToAll</c>, which copies group, on/off, firing
/// mode and target select from the open weapon to <b>every weapon of the same type</b> on the ship — so "these
/// three PDCs in group 2, those two in group 4" cannot be expressed at all. A planner can do this the obvious way
/// and hand the finished arrangement to the game.</para>
///
/// <para><b>How it reaches the game.</b> As per-instance conditions, on the routes that already exist for a
/// container's fill: an <c>aCondOverrides</c> entry per changed cond on the mod export
/// (<c>JsonItem.ApplyOverrideCondsToCO</c> feeds each to <c>CondOwner.SetCondAmount</c>, so an override
/// <b>replaces</b> the def's amount), and on a save write-back the same entries on the item <b>plus</b> the
/// condition owner's own <c>aConds</c> — the CO is what a loaded ship reads, and its conds are frozen by the time
/// the overrides are applied (see <c>SaveEdit.SetFillConds</c> for the full account of why one channel is not
/// enough).</para>
/// </summary>
public static class WeaponPanel
{
    // ---- the conditions ----

    /// <summary>The starting condition that marks a part as a ship weapon. Data-driven detection, so a modded
    /// weapon is found too rather than hard-coding the fifty stock names.</summary>
    public const string WeaponCond = "IsShipWeapon";

    /// <summary>The firing group, 0-based (<see cref="WeaponSettings.Group"/>).</summary>
    public const string GroupCond = "IsShipWeaponFiringGroup";

    /// <summary>Manual firing mode (<see cref="WeaponSettings.Manual"/>).</summary>
    public const string ManualCond = "IsShipWeaponFiringModeManual";

    /// <summary><see cref="PdcTargetMode.Ships"/>.</summary>
    public const string ShipsOnlyCond = "IsPDCTargetModeShipsOnly";

    /// <summary><see cref="PdcTargetMode.NonShips"/>.</summary>
    public const string NonShipsCond = "IsPDCTargetModeMMMOnly";

    /// <summary>Type conditions, one per weapon on all fifty stock defs.</summary>
    public const string PdcCond = "IsShipWeaponPDC";
    public const string MassThrowerCond = "IsShipWeaponMassThrower";
    public const string MissileLauncherCond = "IsShipWeaponMissileLauncher";
    public const string DecoyLauncherCond = "IsShipWeaponDecoyLauncher";

    /// <summary>The weapon's targeting arc in degrees, centred on where it points, and the range that arc reaches
    /// in metres. Both are read-only facts of the def; the editor shows them because they are what makes a
    /// grouping sensible or silly.</summary>
    public const string ArcAngleCond = "IsShipWeaponArcAngle";
    public const string ArcRangeCond = "IsShipWeaponArcRange";

    /// <summary>Every condition this panel may write, which is also the set an import reads and the set a
    /// write-back clears before restating. Order is the display order of the controls.</summary>
    public static readonly IReadOnlyList<string> AuthoredConds = [GroupCond, ManualCond, NonShipsCond, ShipsOnlyCond];

    // ---- the groups ----

    /// <summary>Lowest stored group amount.</summary>
    public const int MinGroup = 0;

    /// <summary>Highest stored group amount. <c>MFDWeaponDetails.CycleFiringGroup</c> wraps 8 to 0 and 0 to 8, and
    /// <c>NavModWeaponsControl</c> binds <c>CommandFireGroup1</c> through <c>CommandFireGroup9</c>, so nine is the
    /// whole range and it is fixed in code rather than in data.</summary>
    public const int MaxGroup = 8;

    /// <summary>How many firing groups there are.</summary>
    public const int GroupCount = MaxGroup - MinGroup + 1;

    /// <summary>A stored group as the game shows it: 1 to 9. The <b>only</b> place the offset is applied, along
    /// with <see cref="FromDisplay"/>.</summary>
    public static int ToDisplay(int stored) => stored + 1;

    /// <summary>A displayed group (1 to 9) as the game stores it.</summary>
    public static int FromDisplay(int display) => display - 1;

    /// <summary>Every group in display order, as the editor offers them.</summary>
    public static IEnumerable<int> AllGroups => Enumerable.Range(MinGroup, GroupCount);

    // ---- what a def is ----

    /// <summary>Whether this part is a ship weapon, and so has a page on the Weapons MFD.</summary>
    public static bool IsWeapon(PartDef? part) =>
        part is not null && Array.IndexOf(part.StartingConds, WeaponCond) >= 0;

    /// <inheritdoc cref="IsWeapon(PartDef?)"/>
    public static bool IsWeapon(ResolvedPart? part) => part is not null && part.Has(WeaponCond);

    /// <summary>Whether this part is offered a <see cref="PdcTargetMode"/> — a point-defence cannon, matching the
    /// <c>_isPDC</c> gate on the game's own page.</summary>
    public static bool OffersTargetMode(PartDef? part) =>
        part is not null && Array.IndexOf(part.StartingConds, PdcCond) >= 0;

    /// <summary>What kind of weapon this is. <see cref="WeaponClass.Unknown"/> for a non-weapon, and for a modded
    /// weapon that declares <see cref="WeaponCond"/> without a type cond — which is a weapon the editor still
    /// lists and still groups, since the type is only used for labelling.</summary>
    public static WeaponClass Classify(PartDef? part)
    {
        if (part is null) return WeaponClass.Unknown;
        if (Array.IndexOf(part.StartingConds, PdcCond) >= 0) return WeaponClass.PointDefence;
        if (Array.IndexOf(part.StartingConds, MassThrowerCond) >= 0) return WeaponClass.MassThrower;
        if (Array.IndexOf(part.StartingConds, MissileLauncherCond) >= 0) return WeaponClass.MissileLauncher;
        if (Array.IndexOf(part.StartingConds, DecoyLauncherCond) >= 0) return WeaponClass.DecoyLauncher;
        return WeaponClass.Unknown;
    }

    /// <summary>A one-word label for <see cref="Classify"/>, for the editor's type column.</summary>
    public static string ClassLabel(WeaponClass cls) => cls switch
    {
        WeaponClass.PointDefence => "PDC",
        WeaponClass.MassThrower => "Mass thrower",
        WeaponClass.MissileLauncher => "Missile launcher",
        WeaponClass.DecoyLauncher => "Decoy launcher",
        _ => "Weapon",
    };

    /// <summary>
    /// The firing group this def ships with — <c>IsShipWeaponFiringGroup</c>'s declared amount, rounded the way
    /// the game rounds it (<c>MathUtils.RoundToInt</c>), and <b>0</b> when the def declares no such condition.
    ///
    /// <para>Zero is not a fallback here, it is the game's answer: <c>GetCondAmount</c> returns 0 for a condition
    /// an owner does not carry, so the eleven mass-thrower defs that declare none really do all sit in displayed
    /// group 1. Stock: PDCs 2, missile launchers 1, the decoy launcher 3, mass throwers nothing at all.</para>
    /// </summary>
    public static int DefaultGroup(PartDef? part) =>
        part is null ? MinGroup
            : Math.Clamp((int)Math.Round(part.StartingCondValues.GetValueOrDefault(GroupCond),
                MidpointRounding.AwayFromZero), MinGroup, MaxGroup);

    /// <summary>The mode this def ships in, which is <see cref="PdcTargetMode.All"/> for every stock weapon — none
    /// of the fifty declare either condition. Read rather than assumed, so a modded cannon that ships restricted
    /// is not reported as having been changed by the designer.</summary>
    public static PdcTargetMode DefaultTargetMode(PartDef? part) =>
        part is null ? PdcTargetMode.All
            : part.StartingCondValues.GetValueOrDefault(ShipsOnlyCond) > 0 ? PdcTargetMode.Ships
            : part.StartingCondValues.GetValueOrDefault(NonShipsCond) > 0 ? PdcTargetMode.NonShips
            : PdcTargetMode.All;

    /// <summary>Whether this def ships in manual firing mode. False for every stock weapon.</summary>
    public static bool DefaultManual(PartDef? part) =>
        part is not null && part.StartingCondValues.GetValueOrDefault(ManualCond) > 0;

    /// <summary>The settings this def ships with — what <see cref="WeaponSettings.Default"/> means for one
    /// particular weapon, and what an editor shows for a weapon nobody has touched.</summary>
    public static WeaponSettings Stock(PartDef? part) => new()
    {
        Group = DefaultGroup(part),
        Manual = DefaultManual(part),
        TargetMode = DefaultTargetMode(part),
    };

    /// <summary>The settings in effect on a part: what the designer authored, with anything they left alone
    /// filled in from the def. This is what the editor displays and what the write side compares against.</summary>
    public static WeaponSettings Effective(WeaponSettings? authored, PartDef? part) =>
        authored is null ? Stock(part) : Stock(part) with
        {
            Group = authored.Group ?? DefaultGroup(part),
            Manual = authored.Manual,
            TargetMode = OffersTargetMode(part) ? authored.TargetMode : DefaultTargetMode(part),
        };

    // ---- where it points ----

    /// <summary>The def's targeting arc in degrees, or 0 when it declares none. Stock: 85 on a PDC, 15-20 on a
    /// mass thrower, 360 on both launcher families.</summary>
    public static double ArcAngle(PartDef? part) => part?.StartingCondValues.GetValueOrDefault(ArcAngleCond) ?? 0;

    /// <summary>The def's arc range in metres, or 0 when it declares none. Stock: 12-15 km on a PDC, 66-120 km on
    /// a mass thrower, and nothing at all on either launcher, whose missiles fly themselves.</summary>
    public static double ArcRange(PartDef? part) => part?.StartingCondValues.GetValueOrDefault(ArcRangeCond) ?? 0;

    /// <summary>
    /// True when the weapon has no meaningful bearing, so grouping it by side would be inventing a fact: its arc
    /// covers the circle, or it declares no arc at all.
    ///
    /// <para>The second case is not a rounding of the first. A launcher's <b>off</b> and <b>loose</b> defs declare
    /// no arc while their powered form declares 360, and the palette installs the off state — so a design's
    /// missile launchers would otherwise sort under a heading their live counterparts do not have. "No arc stated"
    /// and "arc is everything" are the same answer to the only question being asked.</para>
    /// </summary>
    public static bool IsOmnidirectional(PartDef? part) => ArcAngle(part) is not (> 0 and < 360);

    /// <summary>
    /// Which way a placed weapon points.
    ///
    /// <para>The game fires along <c>ship.fRot + rad(item.fLastRotation)</c> with angle 0 up the ship's own axis
    /// (<c>WeaponsSystem.GetItemsDefaultFiringAngle</c>, and <c>SpawnProjectile</c> resolves that angle against
    /// world +Y). An export writes <c>fRotation = Norm(-Rot)</c> and flips y, because the document is y-down and
    /// the game is y-up; the two inversions cancel on the x axis and compose on y, which leaves document rotation
    /// reading clockwise from up-plan. So <see cref="Placement.Rot"/> 0 is fore, 90 starboard, 180 aft, 270 port —
    /// the same quarter turns the plan is drawn in, which is the point.</para>
    /// </summary>
    public static WeaponFacing Facing(PartDef? part, int rot) =>
        IsOmnidirectional(part) ? WeaponFacing.Any
            : GridMath.Norm(rot) switch
            {
                90 => WeaponFacing.Starboard,
                180 => WeaponFacing.Aft,
                270 => WeaponFacing.Port,
                _ => WeaponFacing.Fore,
            };

    /// <summary>The heading's own name, for the editor's section headers.</summary>
    public static string FacingLabel(WeaponFacing facing) => facing switch
    {
        WeaponFacing.Fore => "Fore",
        WeaponFacing.Starboard => "Starboard",
        WeaponFacing.Aft => "Aft",
        WeaponFacing.Port => "Port",
        _ => "Any bearing",
    };

    // ---- writing ----

    /// <summary>
    /// The absolute condition amounts a set of settings means, whether or not they differ from the def. The target
    /// mode is always both conds, because it is one tri-state stored as two flags and half of it is not a state.
    /// </summary>
    public static IReadOnlyDictionary<string, double> CondValues(WeaponSettings? settings, PartDef? part)
    {
        var s = Effective(settings, part);
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [GroupCond] = s.Group ?? DefaultGroup(part),
            [ManualCond] = s.Manual ? 1 : 0,
            [ShipsOnlyCond] = s.TargetMode == PdcTargetMode.Ships ? 1 : 0,
            [NonShipsCond] = s.TargetMode == PdcTargetMode.NonShips ? 1 : 0,
        };
    }

    /// <summary>
    /// The per-instance overrides to write for a weapon: one entry per authored condition whose amount differs
    /// from what the def itself declares, in <see cref="AuthoredConds"/> order.
    ///
    /// <para>Only the differences, for the same reason a fill writes only its changed lines and a nav console
    /// stores no layout it did not choose: an override that restates the def is noise in the file, and on a save
    /// write-back it is worse than noise — it stamps a value onto an item the design had no opinion about.
    /// Empty for a weapon left alone, and for a part that is not a weapon at all.</para>
    ///
    /// <para>A difference is written even when the def declares no such condition, which is how a mass thrower
    /// gets a firing group (see <see cref="WeaponSettings.ClampTo"/>).</para>
    /// </summary>
    public static IReadOnlyList<(string Cond, double Amount)> Overrides(WeaponSettings? settings, PartDef? part)
    {
        if (!IsWeapon(part) || settings is null) return [];
        var wanted = CondValues(settings, part);
        var result = new List<(string, double)>(AuthoredConds.Count);
        foreach (var cond in AuthoredConds)
        {
            var amount = wanted[cond];
            if (Math.Abs(amount - (part!.StartingCondValues.GetValueOrDefault(cond))) > Epsilon)
                result.Add((cond, amount));
        }
        return result;
    }

    /// <summary>Slack on an amount comparison. Every value in play is a small integer, and the def's own side is
    /// parsed out of text, so this only has to survive the double round-trip.</summary>
    private const double Epsilon = 1e-9;

    // ---- reading ----

    /// <summary>
    /// The settings a weapon is actually carrying, from the condition amounts resolved off an imported item — or
    /// null when it says nothing its def does not already say.
    ///
    /// <para>Null is the common case and not an optimisation: no core template authors any of these, so an
    /// imported ship's weapons overwhelmingly sit at their def's values. Storing that would put a redundant
    /// override on every weapon, carry it into every <c>.oplan</c>, and make a save write-back stamp values onto
    /// items it should have left alone — the same rule <see cref="NavConsole.StoredLayout"/> follows.</para>
    ///
    /// <para><paramref name="values"/> is the resolved picture, the way the game resolves it: the condition
    /// owner's own <c>aConds</c> first, then the item's <c>aCondOverrides</c> on top, since
    /// <c>ApplyOverrideCondsToCO</c> runs after the owner is built. A condition absent from both is the def's.</para>
    /// </summary>
    public static WeaponSettings? FromConds(IReadOnlyDictionary<string, double> values, PartDef? part)
    {
        if (!IsWeapon(part)) return null;

        var group = values.TryGetValue(GroupCond, out var g)
            ? Math.Clamp((int)Math.Round(g, MidpointRounding.AwayFromZero), MinGroup, MaxGroup)
            : DefaultGroup(part);
        var manual = values.TryGetValue(ManualCond, out var m) ? m > 0 : DefaultManual(part);
        var mode =
            values.GetValueOrDefault(ShipsOnlyCond, part!.StartingCondValues.GetValueOrDefault(ShipsOnlyCond)) > 0
                ? PdcTargetMode.Ships
            : values.GetValueOrDefault(NonShipsCond, part.StartingCondValues.GetValueOrDefault(NonShipsCond)) > 0
                ? PdcTargetMode.NonShips
                : PdcTargetMode.All;

        return Authored(new WeaponSettings
        {
            Group = group == DefaultGroup(part) ? null : group,
            Manual = manual,
            TargetMode = mode,
        }, part);
    }

    /// <summary>
    /// A settings object reduced to what it actually says, or null when it says nothing the def does not already
    /// say. Compared against <see cref="Stock"/> rather than against <see cref="WeaponSettings.Default"/>, because
    /// what counts as "untouched" is a property of the def: no stock weapon ships restricted or manual, but a
    /// modded one could, and reading its own state back as an authored change would put a redundant entry on every
    /// such weapon in every design.
    /// </summary>
    public static WeaponSettings? Authored(WeaponSettings? settings, PartDef? part)
    {
        if (settings is null) return null;
        var clamped = settings.ClampTo(part);
        var stock = Stock(part);
        return (clamped.Group ?? DefaultGroup(part)) == stock.Group
               && clamped.Manual == stock.Manual
               && clamped.TargetMode == stock.TargetMode
            ? null
            : clamped;
    }
}

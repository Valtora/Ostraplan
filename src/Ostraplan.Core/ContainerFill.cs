namespace Ostraplan.Core;

/// <summary>
/// One editable payload line on a container: the condition that stores it, a label, the amount the def itself
/// ships with, and the ceiling this line may be taken to on its own.
///
/// <para>Gas lines all draw on <b>one shared</b> mol budget (see <see cref="PayloadSpec.MaxMols"/>), so
/// <see cref="Max"/> on a gas line is only the ceiling it could reach with every other gas emptied; the real
/// limit is the total. A bulk line (liquid / solid) has no shared budget and no pressure relationship at all,
/// so its <see cref="Max"/> is the whole story.</para>
/// </summary>
/// <param name="Cond">The condition name the amount is stored under, e.g. <c>StatGasMolO2</c>, <c>StatLiqD2O</c>.</param>
/// <param name="Label">Display name — the condition's own <c>strNameFriendly</c> when the data gives one.</param>
/// <param name="Stock">What the def ships with. "Reset to stock" returns the line to this.</param>
/// <param name="Max">The most this line alone may hold.</param>
/// <param name="IsGas">True for a <c>StatGasMol*</c> line (shares the pressure budget), false for bulk.</param>
public sealed record PayloadLine(string Cond, string Label, double Stock, double Max, bool IsGas);

/// <summary>
/// What one container may be filled with, and how much of it — everything the fill editor and the write-back
/// need, derived from the def alone.
///
/// <para>Built by <see cref="ContainerFill.Describe"/>; null there means the part holds nothing and is not
/// offered a fill at all.</para>
/// </summary>
/// <param name="VolumeM3">The def's <c>StatVolume</c>.</param>
/// <param name="PressureMaxKPa">The def's <c>StatGasPressureMax</c> — a real burst threshold, not a label
/// (see <see cref="ContainerFill"/>).</param>
/// <param name="TempK">The def's <c>StatGasTemp</c>. 293 K on the ordinary canisters, 4 K on the cryogenic
/// fuel tanks, and it divides into the capacity, so a cryo tank holds far more mols at the same pressure.</param>
/// <param name="Lines">Every editable line, gas first then bulk, in display order.</param>
public sealed record PayloadSpec(
    double VolumeM3, double PressureMaxKPa, double TempK, IReadOnlyList<PayloadLine> Lines)
{
    /// <summary>The container's total gas capacity in moles — <c>StatGasPressureMax × StatVolume / (R × T)</c>,
    /// the ideal gas law at the def's own temperature. Shared across every gas species at once.
    /// <para>0 when the container takes no gas, which covers both a def with no pressure rating and a fuel tank,
    /// whose shell has a rating but is not offered gas at all (see <see cref="ContainerFill.Describe"/>). Keying
    /// this off the lines rather than off the stats alone is what stops a fuel tank reporting a capacity nothing
    /// can be put into.</para></summary>
    public double MaxMols =>
        Lines.Any(l => l.IsGas) && PressureMaxKPa > 0 && VolumeM3 > 0 && TempK > 0
            ? PressureMaxKPa * VolumeM3 / (Atmosphere.GasConstant * TempK)
            : 0;

    /// <summary>The gas lines (the ones sharing <see cref="MaxMols"/>).</summary>
    public IEnumerable<PayloadLine> GasLines => Lines.Where(l => l.IsGas);

    /// <summary>The bulk liquid/solid lines (each capped on its own).</summary>
    public IEnumerable<PayloadLine> BulkLines => Lines.Where(l => !l.IsGas);

    /// <summary>True when this container can hold gas at all.</summary>
    public bool HasGas => MaxMols > 0;

    /// <summary>The pressure, in kPa, that <paramref name="totalMols"/> of gas exerts in this container —
    /// <c>n·R·T/V</c>, the game's own <c>GasContainer.Run</c>. 0 when the container takes no gas.</summary>
    public double PressureFor(double totalMols) =>
        VolumeM3 > 0 && TempK > 0 ? totalMols * Atmosphere.GasConstant * TempK / VolumeM3 : 0;

    /// <summary>The amounts the def itself ships with, keyed by condition — the "stock" fill.</summary>
    public IReadOnlyDictionary<string, double> Stock =>
        Lines.ToDictionary(l => l.Cond, l => l.Stock, StringComparer.Ordinal);
}

/// <summary>
/// How much of what a canister or tank holds, and the arithmetic the game does with it. A pure port of
/// <c>GasContainer</c> (verified against a 1.0.0.9 decompile); nothing here knows about a document or a window.
///
/// <para><b>Gas is molar, not volumetric.</b> A container stores one <c>StatGasMol&lt;gas&gt;</c> condition per
/// species and the game derives everything else from the sum: pressure is <c>Σn·R·T/V</c>, each
/// <c>StatGasPp&lt;gas&gt;</c> is that pressure times the species' share of the mols, and mass is
/// <c>Σ n·molarMass</c>. So the species do not each get a slice of the volume — they compete for <b>one</b>
/// mol budget, <c>StatGasPressureMax × StatVolume / (R × StatGasTemp)</c>, and which gases those mols are
/// made of changes the mass, the value and the reaction mass but never the capacity. Every ordinary canister
/// in the game is the same 0.787 m³ shell rated to 41,400 kPa, which is why an N2 can and an O2 can are
/// interchangeable.</para>
///
/// <para><b>The pressure rating is a burst threshold.</b> <c>GasContainer.CheckPressureDifference</c> runs once
/// a second: when the gap between the container's pressure and the room's exceeds
/// <c>StatGasPressureMax + 150</c> kPa the container takes random damage, and when its health runs out it fires
/// <c>AModeCanisterShrapnel</c> rays into the compartment. Vanilla's own full fill sits exactly at the rating
/// (<c>ItmRTAO2</c>: 0.787 m³ at 41,400 kPa and 293 K is 13,375 mol; its def carries 13,373), so
/// <see cref="Clamp"/> treats the rating as the ceiling rather than shipping a design that shreds itself.</para>
///
/// <para><b>Liquids and solids are not gas.</b> <c>StatLiqD2O</c>, <c>StatSolidHe3</c>, <c>StatLiqHe</c> and a
/// mod's own bulk conditions are kilogram payloads with no pressure relationship whatever — the torch tanks'
/// gas side is a token 0.0001 mol of N2. They get their own lines, capped at what the def ships with, which is
/// the only capacity figure the game publishes for them. And a tank that carries one of these is <b>not</b>
/// offered gas at all: it is built around a single reactant that the reactor matches by exact condowner name, so
/// gas in one would be dead weight the drive cannot use (see <see cref="Describe"/>).</para>
/// </summary>
public static class ContainerFill
{
    /// <summary>The condition prefix a gas amount is stored under.</summary>
    public const string MolPrefix = "StatGasMol";

    /// <summary>
    /// The gas species the game's code can handle at all — <c>FluidStrings.moleculeNames</c>, verbatim and in
    /// its order. This has to be a constant rather than a data read: <c>GasContainer.Run</c> resolves a gas's
    /// partial-pressure condition by <c>FluidStrings.mol.IndexOf(cond)</c> and indexes <c>FluidStrings.pps</c>
    /// with the result, so a species outside this list throws inside the game's own update loop however well
    /// declared it is in data.
    /// <para><b>Re-verify on a major game version.</b></para>
    /// </summary>
    public static readonly string[] KnownGases =
        ["CH4", "CO2", "H2", "H2O", "H2SO4", "He2", "N2", "NH3", "O2", "CO", "Smoke"];

    /// <summary>The order gas lines are shown in: the three with canisters of their own first, then the rest.
    /// Any species not named here sorts after these, alphabetically.</summary>
    private static readonly string[] DisplayOrder = ["O2", "N2", "CO2", "CH4", "CO", "NH3", "H2SO4", "Smoke"];

    /// <summary>Conditions matching the bulk prefixes that are <b>not</b> payloads. <c>StatSolidTemp</c> is a
    /// temperature, and would otherwise read as a solid cargo of degrees.</summary>
    private static bool IsBulkPayload(string cond) =>
        (cond.StartsWith("StatLiq", StringComparison.Ordinal) || cond.StartsWith("StatSolid", StringComparison.Ordinal))
        && !cond.EndsWith("Temp", StringComparison.Ordinal);

    /// <summary>
    /// What <paramref name="part"/> can be filled with, or null when it holds nothing editable.
    ///
    /// <para><b>A tank is either a gas container or a fuel tank, never both.</b> A def that declares a bulk
    /// payload gets its bulk lines and no gas lines at all. Those tanks are built around one reactant and the
    /// reactor matches them by <i>exact condowner name</i> (§20), so anything else put in one is dead weight the
    /// drive cannot use; their gas side is a 0.0001 mol token of N2 that exists so the container initialises,
    /// not storage. Nothing in core data or in the Ship's Water mod carries both a real gas load and a bulk
    /// payload, so this costs no case that exists.</para>
    ///
    /// <para>Gas lines are otherwise offered whenever the def has a volume, a temperature <b>and</b> a pressure
    /// rating. The rating is what bounds the fill, and the damaged canister shells drop it deliberately (the
    /// game's burst check skips a container whose <c>StatGasPressureMax</c> is 0), so there would be nothing to
    /// clamp against. Bulk lines come from the def's own declarations, so a modded tank's condition — Ship's
    /// Water's <c>StatLiqH2O</c>, say — is picked up with no list to maintain.</para>
    /// </summary>
    public static PayloadSpec? Describe(PartDef? part, Catalog catalog)
    {
        if (part is null) return null;
        var vals = part.StartingCondValues;
        var volume = vals.GetValueOrDefault("StatVolume");
        var pressureMax = vals.GetValueOrDefault("StatGasPressureMax");
        var temp = vals.GetValueOrDefault("StatGasTemp");

        var lines = new List<PayloadLine>();

        // Declaring a bulk payload is what makes something a fuel tank, whatever it holds right now — the test is
        // deliberately "declares the condition", not "declares a positive amount", so an empty waste tank is still
        // a fuel tank and is not offered a menu of gases.
        var isFuelTank = vals.Keys.Any(IsBulkPayload);

        if (!isFuelTank && volume > 0 && pressureMax > 0 && temp > 0)
        {
            var maxMols = pressureMax * volume / (Atmosphere.GasConstant * temp);
            foreach (var gas in Offerable(catalog))
            {
                var cond = MolPrefix + gas;
                lines.Add(new PayloadLine(cond, catalog.CondFriendly(cond) ?? gas,
                    vals.GetValueOrDefault(cond), maxMols, IsGas: true));
            }
        }

        foreach (var (cond, stock) in vals.Where(kv => IsBulkPayload(kv.Key) && kv.Value > 0)
                                          .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            lines.Add(new PayloadLine(cond, catalog.CondFriendly(cond) ?? cond, stock, stock, IsGas: false));

        return lines.Count == 0 ? null : new PayloadSpec(volume, pressureMax, temp, lines);
    }

    /// <summary>
    /// The gas species a container may actually be given: the ones the game's code knows
    /// (<see cref="KnownGases"/>) that the loaded data also declares a <c>StatGasMol*</c> condition for, in
    /// display order.
    ///
    /// <para>Both halves are load-bearing. A species the data does not declare cannot be stored at all —
    /// <c>CondOwner.AddCondAmount</c> returns the moment <c>DataHandler.GetCond</c> comes back null, and
    /// <c>GasContainer.AddGasMols</c> checks the same thing before it will move any — which is why H2, H2O and
    /// He2 are inert in core data despite being in the code's own list. A species the data declares but the
    /// code does not know would throw inside <c>GasContainer.Run</c>. On stock 1.0.0.9 the intersection is
    /// eight: O2, N2, CO2, CH4, CO, NH3, H2SO4 and Smoke.</para>
    /// </summary>
    public static IReadOnlyList<string> Offerable(Catalog catalog)
    {
        var declared = catalog.DeclaredConds;
        // No conditions data at all (a synthetic test catalog) means nothing can be verified, so fall back to
        // the code's list rather than offering an empty editor.
        var gases = declared.Count == 0
            ? KnownGases
            : KnownGases.Where(g => declared.Contains(MolPrefix + g)).ToArray();
        return [.. gases.OrderBy(g => Array.IndexOf(DisplayOrder, g) is var i && i >= 0 ? i : int.MaxValue)
                        .ThenBy(g => g, StringComparer.Ordinal)];
    }

    /// <summary>
    /// <paramref name="fill"/> made legal for <paramref name="spec"/>: every line floored at 0 and capped at its
    /// own maximum, then the gas lines scaled down together if they still exceed the shared mol budget. Entries
    /// naming a line the spec does not have are dropped. Never returns null.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Clamp(
        IReadOnlyDictionary<string, double> fill, PayloadSpec spec)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in spec.Lines)
        {
            if (!fill.TryGetValue(line.Cond, out var v)) continue;
            var clamped = Math.Clamp(double.IsFinite(v) ? v : 0, 0, line.Max);
            if (clamped > 0) result[line.Cond] = clamped;
        }

        var total = TotalMols(result);
        if (spec.MaxMols > 0 && total > spec.MaxMols)
        {
            var scale = spec.MaxMols / total;
            foreach (var line in spec.GasLines)
                if (result.TryGetValue(line.Cond, out var v)) result[line.Cond] = v * scale;
        }
        return result;
    }

    /// <summary>Total moles of gas in a fill — the sum of every <c>StatGasMol*</c> entry, which is what the
    /// pressure is computed from. <c>StatGasMolTotal</c> is the game's own running sum and is never a species,
    /// so it is excluded exactly as <c>GasContainer.Init</c> excludes it.</summary>
    public static double TotalMols(IReadOnlyDictionary<string, double> fill)
    {
        double total = 0;
        foreach (var (cond, amount) in fill)
            if (IsGasMol(cond)) total += amount;
        return total;
    }

    /// <summary>True for a per-species gas amount condition (and not the game's <c>StatGasMolTotal</c> sum).</summary>
    public static bool IsGasMol(string cond) =>
        cond.StartsWith(MolPrefix, StringComparison.Ordinal) && cond != "StatGasMolTotal";

    /// <summary>Every condition a fill may write: the per-species gas amounts and the bulk payloads. Used to
    /// decide which of a saved condowner's conditions are ours to rewrite.</summary>
    public static bool IsFillCond(string cond) => IsGasMol(cond) || IsBulkPayload(cond);

    /// <summary>
    /// The mass in kilograms of the gas in a fill — <c>GasContainer.Mass</c>, the sum over every species of
    /// mols × molar mass. All of it counts as RCS reaction mass, which is how a Katydid runs its thrusters off
    /// oxygen. A species the game's molar-mass switch does not know weighs nothing, in Ostraplan exactly as
    /// in the game (see <see cref="ShipValue.MolarMass"/>).
    /// <para>Bulk payloads are deliberately excluded: they are already kilograms, they are not gas, and the
    /// game weighs them nowhere near here.</para>
    /// </summary>
    public static double GasMassKg(IReadOnlyDictionary<string, double> fill)
    {
        double mass = 0;
        foreach (var (cond, amount) in fill)
            if (IsGasMol(cond)) mass += ShipValue.MolarMass(cond[MolPrefix.Length..]) * amount;
        return mass;
    }

    /// <summary>
    /// What a fill is worth in credits — the contents half of <c>GasContainer.GetTotalGasValue</c> plus the two
    /// fuel lines <c>GetBasePrice</c> adds: each gas at mass × its price per kilogram, <c>StatLiqD2O</c> priced
    /// as H2 and <c>StatSolidHe3</c> priced as He3. Prices come from the <c>GasPrices</c> loot, so a mod that
    /// retunes them is followed. A bulk condition with no price of its own is worth nothing, as it is in game.
    /// </summary>
    public static double Value(IReadOnlyDictionary<string, double> fill, Catalog catalog)
    {
        double value = 0;
        foreach (var (cond, amount) in fill)
            if (IsGasMol(cond))
            {
                var gas = cond[MolPrefix.Length..];
                value += catalog.GasPrices.GetValueOrDefault(gas) * ShipValue.MolarMass(gas) * amount;
            }
        value += fill.GetValueOrDefault("StatLiqD2O") * catalog.GasPrices.GetValueOrDefault("H2");
        value += fill.GetValueOrDefault("StatSolidHe3") * catalog.GasPrices.GetValueOrDefault("He3");
        return value;
    }

    /// <summary>
    /// <paramref name="baseValues"/> with <paramref name="fill"/> laid over it: each filled condition set to the
    /// authored amount, and every payload condition the spec knows about that the fill does <b>not</b> name set
    /// to zero. That second half is what makes an emptied canister actually empty — leaving the def's own
    /// <c>StatGasMolO2</c> in place would have "empty" still read as a full tank everywhere downstream.
    /// Returns <paramref name="baseValues"/> unchanged when there is no fill to apply.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Overlay(
        IReadOnlyDictionary<string, double> baseValues, IReadOnlyDictionary<string, double>? fill, PayloadSpec? spec)
    {
        if (fill is null || spec is null) return baseValues;
        var merged = new Dictionary<string, double>(baseValues, StringComparer.Ordinal);
        foreach (var line in spec.Lines)
            merged[line.Cond] = fill.GetValueOrDefault(line.Cond);
        return merged;
    }

    /// <summary>True when <paramref name="fill"/> is what the def itself ships with, to within
    /// <see cref="Epsilon"/> on every line — i.e. there is nothing worth persisting or writing out.</summary>
    public static bool IsStock(IReadOnlyDictionary<string, double> fill, PayloadSpec spec) =>
        spec.Lines.All(l => Math.Abs(fill.GetValueOrDefault(l.Cond) - l.Stock) <= Epsilon);

    /// <summary>How close two amounts have to be to count as the same. Gas amounts run to five figures and the
    /// defs themselves carry values like 0.0001, so this is relative rather than absolute at the top end.</summary>
    public const double Epsilon = 1e-6;
}

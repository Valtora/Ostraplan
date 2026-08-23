namespace Ostraplan.Core;

/// <summary>
/// A port of the wear the game draws on a damaged part — the fragment path of the <c>Sprites/AlbedoPass</c>
/// shader, which is what makes a worn bulkhead look worn rather than merely read 63% in a panel.
///
/// <para><b>This is a GPU shader, not a sprite swap.</b> A part's condition does not select a different image.
/// The game samples a 3D value-noise field in <b>world space</b>, slices it, and wherever the slice crosses the
/// part's damage rate it replaces that texel with the worn colour (and, for the 293 defs that name one, with a
/// second texture). So the wear pattern is a function of where the part sits on the grid, not of any per-instance
/// roll — see <see cref="Sample"/> for why that matters.</para>
///
/// <para>The constants live in compiled GPU code where no data check can reach them, so this was recovered the
/// way §16's lighting was: UnityPy over <c>sharedassets0.assets</c>, LZ4-decompress the subprogram blob,
/// disassemble the DXBC through <c>d3dcompiler_47</c>. Every constant-buffer slot below was then resolved
/// through the pass's own <c>NameIndices</c> table rather than inferred from the arithmetic, because a shader
/// that has <c>_Cut</c> and <c>_Trim</c> adjacent and both scalar is exactly the kind that reads plausibly when
/// two fields are swapped. See docs/GAME-INTERNALS.md §15.</para>
///
/// <para><b>Re-verify on a major game version</b> by re-extracting the shader: the tuning defaults in
/// <see cref="Tuning"/> come from the shader's own property table, not from the data, so a data check cannot
/// see them drift.</para>
/// </summary>
public static class WearShader
{
    /// <summary>
    /// The damage rate below which the shader draws nothing at all (<c>ge r0.x, cb0[7].x, l(0.2)</c>).
    ///
    /// <para>A part is only 80% of the way to pristine before any wear appears, which is why the game's own kiosk
    /// ships look clean: their parts average ~88% condition (<see cref="WearModel.VanillaUsedCondition"/>), so
    /// almost none of them cross this line. A design painted to look lived-in has to go below 80% before it looks
    /// like anything.</para>
    /// </summary>
    public const double Threshold = 0.2;

    /// <summary>Octaves the fBm runs (<c>_Octs</c>). Never overridden by a def or by the game's own code, so it
    /// is the shader's property default and nothing else.</summary>
    public const int Octaves = 8;

    /// <summary>The hash multiplier in <c>frac(sin(n) * _Seed)</c>. The in-world renderer shares one material
    /// across every instance of a given texture set and so never assigns <c>_Seed</c>, which leaves the shader's
    /// property default standing for every part on every ship. (The inventory path <i>does</i> set it, per object,
    /// from the object's <c>strID</c> hash — that is a different material and not this one.)</summary>
    public const double Seed = 453.5453186035156;

    /// <summary>The first octave's frequency. Amplitude starts at <see cref="StartAmplitude"/>; each octave
    /// doubles the frequency and halves the amplitude.</summary>
    public const double StartFrequency = 0.005;

    /// <summary>The first octave's amplitude.</summary>
    public const double StartAmplitude = 25.0;

    /// <summary>
    /// The wear tuning actually in force for one part: the def's own fields where it set them, and the shader's
    /// property defaults everywhere else.
    /// </summary>
    /// <param name="Cut">Where the noise field is sliced. Shader default 0.8.</param>
    /// <param name="Trim">Added after the slice. Shader default 0.2.</param>
    /// <param name="Intensity">Gain on the sliced noise. Shader default 1.</param>
    /// <param name="Complexity">Scales the sample point, so it is the spatial frequency of the pattern. Shader
    /// default 1000.</param>
    /// <param name="Lerp">Blend the worn colour in proportionally rather than replacing the texel outright.</param>
    /// <param name="Sinew">Take the absolute value of the sliced noise, which turns a one-sided stain into a
    /// two-sided vein.</param>
    public readonly record struct Tuning(
        double Cut, double Trim, double Intensity, double Complexity, bool Lerp, bool Sinew)
    {
        /// <summary>The shader's own property defaults, which is what a def that sets none of these gets.</summary>
        public static Tuning Default { get; } = new(0.8, 0.2, 1.0, 1000.0, Lerp: true, Sinew: true);

        /// <summary>
        /// Resolve a def's raw fields into the values the shader runs on, exactly as <c>Item.SetData</c> does:
        /// it pushes a value only when the def set one, so "unset" means the shader default rather than zero.
        /// The two sentinels differ by field — <c>-999</c> for the pair that may legitimately be zero or
        /// negative, plain zero for the pair that may not.
        /// </summary>
        public static Tuning For(WearFields? f)
        {
            if (f is null) return Default;
            var d = Default;
            return new Tuning(
                // Cut and Trim ride a -999 sentinel because a real value of either can be zero (ItmBartop01
                // ships Trim = -0.12), so zero cannot mean "unset" for them.
                Cut: Same(f.Cut, WearFields.Unspecified) ? d.Cut : f.Cut,
                Trim: Same(f.Trim, WearFields.Unspecified) ? d.Trim : f.Trim,
                // Intensity and Complexity use zero as the sentinel, which is the game's own choice and is safe
                // because neither is meaningful at zero: a zero gain erases the pattern and a zero frequency
                // collapses every texel onto one noise sample.
                Intensity: f.Intensity == 0 ? d.Intensity : f.Intensity,
                Complexity: f.Complexity == 0 ? d.Complexity : f.Complexity,
                Lerp: f.Lerp,
                Sinew: f.Sinew);
        }

        private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-6;
    }

    /// <summary>
    /// The two material quantities the wear path reads besides the tuning, named as the shader names them so a
    /// caller cannot get their order the wrong way round.
    /// </summary>
    /// <param name="AspectW"><c>_Aspect.x</c> — the part's footprint <b>width in tiles</b> (<c>nCols</c>). Note
    /// this is the socket footprint and not the sprite's own size; the shader scales the noise point by it.</param>
    /// <param name="AspectH"><c>_Aspect.y</c> — footprint height in tiles.</param>
    /// <param name="ScaleX"><c>_MainTex_ST.x</c> — the material's main-texture scale. 1 for an ordinary sprite;
    /// for a sheet it is <c>tileWidth·16 / textureWidth</c>, i.e. one cell's share of the sheet.</param>
    /// <param name="ScaleY"><c>_MainTex_ST.y</c> — the same for height.</param>
    public readonly record struct Frame(double AspectW, double AspectH, double ScaleX, double ScaleY)
    {
        /// <summary>An ordinary, non-sheet sprite of the given footprint: the material's texture scale is left
        /// at 1, which is what <c>DataHandler.GetMaterial</c> assigns.</summary>
        public static Frame Sprite(int tilesW, int tilesH) =>
            new(Math.Max(1, tilesW), Math.Max(1, tilesH), 1.0, 1.0);

        /// <summary>One cell of an autotiled sheet, as <c>DataHandler.GetMaterialSheet</c> sets it up: the
        /// texture scale is the cell's share of the whole sheet, which is what makes the quantisation below land
        /// on the sheet's texel grid rather than the cell's.</summary>
        public static Frame SheetCell(int tilesW, int tilesH, int textureW, int textureH) =>
            new(Math.Max(1, tilesW), Math.Max(1, tilesH),
                textureW <= 0 ? 1.0 : Math.Max(1, tilesW) * 16.0 / textureW,
                textureH <= 0 ? 1.0 : Math.Max(1, tilesH) * 16.0 / textureH);

        /// <summary>The quantisation grid, <c>trunc(_Aspect.xy · 16 / _MainTex_ST.xy)</c>: how many steps the
        /// texel coordinate is snapped to before the noise is sampled.</summary>
        internal (double X, double Y) Steps() => (
            Math.Max(1, Math.Truncate(AspectW * 16.0 / (ScaleX == 0 ? 1.0 : ScaleX))),
            Math.Max(1, Math.Truncate(AspectH * 16.0 / (ScaleY == 0 ? 1.0 : ScaleY))));
    }

    /// <summary>
    /// The shader's <c>_PositionOffset</c> for a part: the world coordinates of its footprint <b>centre</b>, plus
    /// its draw-order scalar as the third noise axis.
    ///
    /// <para>This is §7's inverse mapping, and it has to be, because the noise is sampled in world space: getting
    /// the frame wrong does not fail, it just draws a different ship's wear. Note the Y negation — document rows
    /// run down and world Y runs up — and that the centre offsets use the <b>rotated</b> footprint.</para>
    ///
    /// <para><paramref name="anchor"/> is where world origin falls in document coordinates, and is the same
    /// <see cref="StrikeAnchor"/> a strike converges on, for the same reason: a design drawn here will sit at the
    /// export grid's origin, while one imported from a save already has a frame of its own. A ship measured in
    /// the wrong one wears plausibly and differently from the ship in the player's save.</para>
    /// </summary>
    public static (double X, double Y, double Z) PositionOffset(
        double docX, double docY, int rotatedW, int rotatedH, double zScale, StrikeAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var col = docX - anchor.DocX;
        var row = docY - anchor.DocY;
        return (col + (rotatedW / 2.0 - 0.5), -(row + (rotatedH / 2.0 - 0.5)), zScale);
    }

    /// <summary>
    /// Whether one texel of a part's sprite has worn through, and by how much.
    ///
    /// <para><paramref name="u"/> / <paramref name="v"/> are the texel's centre in 0..1 <b>within the drawn
    /// cell</b> — for a sheet part that is the cell, not the whole sheet, which is the step the shader takes with
    /// its own <c>frac(uv · (_Columns, _Rows))</c> before anything else.</para>
    ///
    /// <para><paramref name="worldX"/> / <paramref name="worldY"/> are the part's position on the tile grid and
    /// <paramref name="zScale"/> its draw-order scalar, which together are the shader's <c>_PositionOffset</c>.
    /// <b>They are the whole reason two identical walls side by side do not wear identically</b>, and the reason
    /// this reproduces the game rather than merely resembling it: with <see cref="Seed"/> fixed for every part,
    /// world position is the only thing decorrelating them, so the same ship wears the same way here as in
    /// game.</para>
    /// </summary>
    /// <param name="rate">The part's damage rate, 0 pristine to 1 gone.</param>
    /// <returns>How far past the wear line this texel is, clamped to 0..1, or null when it has not worn through
    /// and the texel keeps its original colour. A returned 0 still means worn: it is the blend weight, and only
    /// <see cref="Tuning.Lerp"/> reads it.</returns>
    public static double? Sample(
        double rate, double u, double v, double worldX, double worldY, double zScale,
        Frame frame, Tuning tuning)
    {
        if (rate < Threshold) return null;

        // Snap to the material's texel grid before sampling, so the pattern sits still on the sprite instead of
        // crawling across it as the part moves. For a sheet cell the grid is the whole sheet's, not the cell's
        // (see Frame.SheetCell), which makes the pattern finer on an autotiled wall than on a loose fixture.
        var (stepX, stepY) = frame.Steps();
        var qx = Math.Floor(Frac(u) * stepX) / stepX;
        var qy = Math.Floor(Frac(v) * stepY) / stepY;

        // Then into world space, scaled by Complexity and by the footprint. Complexity multiplies the sample
        // POINT rather than the frequency; that is the same thing for a noise field, but it is where the game
        // puts it and keeping it here is what makes a def's own fDmgComplexity land identically.
        var x = (qx + worldX) * tuning.Complexity * frame.AspectW;
        var y = (qy + worldY) * tuning.Complexity * frame.AspectH;

        var n = Fbm(x, y, zScale);
        n = tuning.Sinew
            ? Math.Abs(n - tuning.Cut) * tuning.Intensity + tuning.Trim
            : (n - tuning.Cut) * tuning.Intensity + tuning.Trim;

        // The comparison is against the REMAINING condition, so a part at rate 1 wears everywhere the noise
        // reaches at all and a part at exactly Threshold wears only where the field peaks.
        return n >= 1.0 - rate ? Math.Clamp(n, 0.0, 1.0) : null;
    }

    /// <summary>The fractal sum: <see cref="Octaves"/> octaves of <see cref="Noise"/>, frequency doubling and
    /// amplitude halving, normalised by the summed amplitude.
    ///
    /// <para>The normaliser is summed from the <b>halved</b> amplitude, after the decrement rather than before,
    /// so it totals 25 rather than 50 across eight octaves. That is the shader's arithmetic
    /// (<c>mul r3.z, r3.x, l(0.5)</c> feeding both the next amplitude and the running total) and it scales the
    /// result by very nearly 2 against the textbook form. Normalising the textbook way would halve every noise
    /// value and put most parts under <see cref="Tuning.Cut"/> for good.</para></summary>
    public static double Fbm(double x, double y, double z)
    {
        var acc = 0.0;
        var norm = 0.0;
        var freq = StartFrequency;
        var amp = StartAmplitude;
        for (var i = 0; i < Octaves; i++)
        {
            acc += Noise(x * freq, y * freq, z * freq) * amp;
            freq *= 2.0;
            amp *= 0.5;
            norm += amp;
        }
        return norm == 0 ? 0 : acc / norm;
    }

    /// <summary>
    /// One octave: trilinear value noise over a lattice whose corners are hashed by
    /// <c>frac(sin(n) * <see cref="Seed"/>)</c>, with <c>n = i.x + 157·i.y + 113·i.z</c>.
    ///
    /// <para>The eight corner offsets (0, 1, 157, 158, 113, 114, 270, 271) fall straight out of that packing and
    /// are written as literals in the shader, so they are reproduced as literals here. Interpolation is the
    /// smoothstep weight <c>f²(3−2f)</c>.</para>
    /// </summary>
    public static double Noise(double x, double y, double z)
    {
        double ix = Math.Floor(x), iy = Math.Floor(y), iz = Math.Floor(z);
        double fx = x - ix, fy = y - iy, fz = z - iz;
        fx = fx * fx * (3.0 - 2.0 * fx);
        fy = fy * fy * (3.0 - 2.0 * fy);
        fz = fz * fz * (3.0 - 2.0 * fz);

        var n = ix + 157.0 * iy + 113.0 * iz;

        var v000 = Hash(n);
        var v100 = Hash(n + 1.0);
        var v010 = Hash(n + 157.0);
        var v110 = Hash(n + 158.0);
        var v001 = Hash(n + 113.0);
        var v101 = Hash(n + 114.0);
        var v011 = Hash(n + 270.0);
        var v111 = Hash(n + 271.0);

        var x00 = Lerp(v000, v100, fx);
        var x10 = Lerp(v010, v110, fx);
        var x01 = Lerp(v001, v101, fx);
        var x11 = Lerp(v011, v111, fx);
        return Lerp(Lerp(x00, x10, fy), Lerp(x01, x11, fy), fz);
    }

    private static double Hash(double n) => Frac(Math.Sin(n) * Seed);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>HLSL <c>frac</c>: the fractional part toward negative infinity, so a negative input comes back
    /// positive. C#'s <c>%</c> would keep the sign and put half the lattice out of range.</summary>
    private static double Frac(double v) => v - Math.Floor(v);
}

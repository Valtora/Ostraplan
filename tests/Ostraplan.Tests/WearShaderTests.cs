using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The wear shader port (<see cref="WearShader"/>). Almost all of this is game-free: the constants live in
/// compiled GPU code rather than in the data, so there is nothing for a live-install test to read them back from
/// and these assertions ARE the record of what was extracted. A game patch that re-tunes the shader will not fail
/// a data test; it has to be caught by re-extracting, which is why <see cref="WearShader"/> says so.
/// </summary>
public class WearShaderTests
{
    // ---- the recovered constants ----

    [Fact]
    public void Nothing_wears_above_eighty_percent_condition()
    {
        // ge r0.x, cb0[7].x, l(0.2) — the whole wear branch is skipped below this. It is why the game's own
        // kiosk ships look clean: WearModel.VanillaUsedCondition is ~0.876, so a vanilla "Used" part averages a
        // damage rate of ~0.124 and never crosses the line.
        Assert.Equal(0.2, WearShader.Threshold, 6);
        Assert.True(1.0 - WearModel.VanillaUsedCondition < WearShader.Threshold);

        var frame = WearShader.Frame.Sprite(1, 1);
        var t = WearShader.Tuning.Default;
        Assert.Null(WearShader.Sample(0.19, 0.5, 0.5, 3, 4, 1.0, frame, t));
    }

    [Fact]
    public void The_shader_property_defaults_are_what_an_untuned_def_gets()
    {
        // Read off the shader's own m_PropInfo table, not the item data — an item def that mentions none of
        // these fields runs on exactly these values.
        var d = WearShader.Tuning.Default;
        Assert.Equal(0.8, d.Cut, 6);
        Assert.Equal(0.2, d.Trim, 6);
        Assert.Equal(1.0, d.Intensity, 6);
        Assert.Equal(1000.0, d.Complexity, 6);
        Assert.True(d.Lerp);
        Assert.True(d.Sinew);

        Assert.Equal(8, WearShader.Octaves);
        Assert.Equal(453.5453186035156, WearShader.Seed, 9);
        Assert.Equal(0.005, WearShader.StartFrequency, 9);
        Assert.Equal(25.0, WearShader.StartAmplitude, 9);
    }

    // ---- tuning resolution ----

    [Fact]
    public void An_unset_def_field_means_the_shader_default_not_zero()
    {
        // Item.SetData pushes a value only when the def set one. The two sentinels differ by field and getting
        // either wrong is silent: a def would render with a Cut of 0 (worn everywhere) or a Complexity of 0
        // (every texel sampling one point, so flat).
        var t = WearShader.Tuning.For(WearFields.Unset);
        Assert.Equal(WearShader.Tuning.Default, t);
    }

    [Fact]
    public void A_def_that_sets_a_field_overrides_the_default()
    {
        // ItmBartop01's real values on stock 1.0.0.11. Trim is NEGATIVE, which is exactly why Cut and Trim
        // cannot use zero as their "unset" sentinel the way Intensity and Complexity do.
        var f = new WearFields(Mode: 0, Cut: 0.91, Trim: -0.12, Intensity: 0, Complexity: 200,
            Lerp: true, Sinew: true);
        var t = WearShader.Tuning.For(f);

        Assert.Equal(0.91, t.Cut, 6);
        Assert.Equal(-0.12, t.Trim, 6);
        Assert.Equal(200.0, t.Complexity, 6);
        Assert.Equal(1.0, t.Intensity, 6);      // unset -> shader default, NOT the 0 the def carries
    }

    [Fact]
    public void Lerp_and_sinew_default_true_because_JsonItemDef_constructs_them_true()
    {
        // The trap this test exists for: 107 core defs set bLerp false and ~800 omit it entirely, and omitting
        // it means TRUE. Reading it with the usual false fallback would flip the majority and look like a
        // uniform shading bug with nothing to point at.
        Assert.True(WearFields.Unset.Lerp);
        Assert.True(WearFields.Unset.Sinew);
    }

    // ---- the noise field ----

    [Fact]
    public void The_noise_is_deterministic_and_stays_in_unit_range()
    {
        // Value noise over a hashed lattice: every corner is frac(...) so the trilinear blend cannot leave 0..1.
        for (var i = 0; i < 200; i++)
        {
            var x = i * 0.37 - 30.0;
            var n = WearShader.Noise(x, x * 1.7, 0.5);
            Assert.InRange(n, 0.0, 1.0);
            Assert.Equal(n, WearShader.Noise(x, x * 1.7, 0.5), 12);
        }
    }

    [Fact]
    public void Negative_coordinates_stay_in_range()
    {
        // World Y is negated (document rows run down, world Y runs up), so half of every ship samples the noise
        // at negative coordinates. HLSL frac() floors rather than truncating; C#'s % would keep the sign and
        // push those corners out of the lattice.
        for (var i = 1; i <= 100; i++)
        {
            var n = WearShader.Noise(-i * 0.61, -i * 2.3, -0.4);
            Assert.InRange(n, 0.0, 1.0);
        }
    }

    [Fact]
    public void The_fractal_sum_normalises_by_the_halved_amplitude()
    {
        // The shader accumulates the normaliser AFTER halving the amplitude, so over 8 octaves it totals
        // 25·(1/2 + 1/4 + … + 1/256) = 24.902… rather than the textbook 49.805…. Normalising the textbook way
        // halves every value and would park most parts under Cut for good.
        var norm = 0.0;
        var amp = WearShader.StartAmplitude;
        var total = 0.0;
        for (var i = 0; i < WearShader.Octaves; i++) { total += amp; amp *= 0.5; norm += amp; }
        Assert.Equal(24.90234375, norm, 6);
        Assert.Equal(49.8046875, total, 6);

        // So the sum runs 0..2 rather than 0..1, since every octave's noise is in 0..1 but the normaliser is
        // half the amplitude actually applied. That factor of two is load-bearing: it is what lifts the field
        // over a default Cut of 0.8 at all, and normalising the textbook way would leave almost every part
        // permanently under it and therefore permanently clean.
        Assert.Equal(2.0, total / norm, 6);

        var max = 0.0;
        for (var i = 0; i < 400; i++)
        {
            var n = WearShader.Fbm(i * 11.3, i * -7.9, 1.0);
            Assert.InRange(n, 0.0, 2.0);
            max = Math.Max(max, n);
        }
        // And the field really does reach past Cut in practice, or nothing would ever wear.
        Assert.True(max > WearShader.Tuning.Default.Cut, $"field peaked at {max}, never reaching Cut");
    }

    // ---- world framing ----

    [Fact]
    public void The_position_offset_is_the_footprint_centre_with_world_y_flipped()
    {
        // Section 7's inverse mapping: fX = col + (wr/2 - 0.5), fY = -(row + (hr/2 - 0.5)).
        var anchor = new StrikeAnchor(0, 0, StrikeFrame.AsExported);

        var (x1, y1, z1) = WearShader.PositionOffset(4, 6, 1, 1, 1.0, anchor);
        Assert.Equal(4.0, x1, 6);
        Assert.Equal(-6.0, y1, 6);
        Assert.Equal(1.0, z1, 6);

        // A 3x3 part's centre is one tile in from its top-left, and the Y offset moves the same way as the row.
        var (x2, y2, _) = WearShader.PositionOffset(4, 6, 3, 3, 1.0, anchor);
        Assert.Equal(5.0, x2, 6);
        Assert.Equal(-7.0, y2, 6);
    }

    [Fact]
    public void The_anchor_shifts_the_whole_pattern()
    {
        // A design measured in the wrong frame does not fail, it wears like a different ship. This is the same
        // exported-vs-imported distinction a micrometeoroid strike converges on.
        var exported = new StrikeAnchor(0, 0, StrikeFrame.AsExported);
        var imported = new StrikeAnchor(-12, 5, StrikeFrame.AsImported);

        var a = WearShader.PositionOffset(4, 6, 1, 1, 1.0, exported);
        var b = WearShader.PositionOffset(4, 6, 1, 1, 1.0, imported);
        Assert.NotEqual(a.X, b.X, 6);
        Assert.NotEqual(a.Y, b.Y, 6);
    }

    // ---- the sample decision ----

    [Fact]
    public void A_destroyed_part_wears_wherever_the_field_reaches_at_all()
    {
        // At rate 1 the test is n >= 0, which the Sinew branch's abs() always satisfies. Every texel of a part
        // driven to nothing takes the worn colour.
        var frame = WearShader.Frame.Sprite(1, 1);
        var t = WearShader.Tuning.Default;
        for (var i = 0; i < 32; i++)
        {
            var u = (i + 0.5) / 32.0;
            Assert.NotNull(WearShader.Sample(1.0, u, 0.5, 2, -3, 1.0, frame, t));
        }
    }

    [Fact]
    public void Wear_is_a_function_of_where_the_part_sits()
    {
        // The in-world material is shared across every instance, so _Seed is the same for every part on every
        // ship and _PositionOffset is the ONLY thing decorrelating two identical walls. If this ever came back
        // equal, every wall on a deck would wear with one identical pattern.
        var frame = WearShader.Frame.Sprite(1, 1);
        var t = WearShader.Tuning.Default;

        var here = new List<double?>();
        var there = new List<double?>();
        for (var i = 0; i < 16; i++)
        {
            var u = (i + 0.5) / 16.0;
            here.Add(WearShader.Sample(0.6, u, 0.5, 10, -10, 1.0, frame, t));
            there.Add(WearShader.Sample(0.6, u, 0.5, 11, -10, 1.0, frame, t));
        }
        Assert.NotEqual(here, there);
    }

    [Fact]
    public void More_damage_never_removes_wear_from_a_texel()
    {
        // The threshold is n >= 1 - rate, so raising the rate can only ever admit more texels. A part that got
        // worse must not come back cleaner anywhere.
        var frame = WearShader.Frame.Sprite(2, 2);
        var t = WearShader.Tuning.Default;

        for (var i = 0; i < 40; i++)
        {
            var u = (i + 0.5) / 40.0;
            var light = WearShader.Sample(0.45, u, 0.35, 7, -2, 1.0, frame, t);
            var heavy = WearShader.Sample(0.95, u, 0.35, 7, -2, 1.0, frame, t);
            if (light is not null) Assert.NotNull(heavy);
        }
    }

    [Fact]
    public void A_sheet_cell_quantises_against_the_whole_sheet()
    {
        // GetMaterialSheet leaves _MainTex_ST at the cell's share of the texture, so trunc(_Aspect·16/_ST)
        // resolves to the sheet's full pixel width rather than the cell's. An autotiled wall therefore wears at
        // a finer grain than a loose fixture of the same footprint, which is the game's own behaviour.
        var cell = WearShader.Frame.SheetCell(1, 1, 64, 64);
        var plain = WearShader.Frame.Sprite(1, 1);

        Assert.Equal((64.0, 64.0), cell.Steps());
        Assert.Equal((16.0, 16.0), plain.Steps());
    }

    [Fact]
    public void Sinew_is_what_makes_the_pattern_two_sided()
    {
        // The only difference between the two branches: abs() around the slice. With it, noise on both sides of
        // Cut wears; without it, only the high side does. A def that sets bSinew false and is read as true grows
        // wear where the game has none.
        var frame = WearShader.Frame.Sprite(1, 1);
        var sinew = WearShader.Tuning.Default;
        var plain = sinew with { Sinew = false };

        var sinewHits = 0;
        var plainHits = 0;
        for (var i = 0; i < 64; i++)
        {
            var u = (i + 0.5) / 64.0;
            if (WearShader.Sample(0.7, u, 0.5, 1, -1, 1.0, frame, sinew) is not null) sinewHits++;
            if (WearShader.Sample(0.7, u, 0.5, 1, -1, 1.0, frame, plain) is not null) plainHits++;
        }
        Assert.True(sinewHits >= plainHits, $"sinew {sinewHits} should cover at least as much as plain {plainHits}");
    }

    // ---- the game-data half ----

    [SkippableFact]
    public void The_real_item_defs_parse_their_wear_fields()
    {
        var g = TestData.RequireGame();

        // ItmBartop01 is the worked example above and pins that the parse reads a negative Trim rather than
        // dropping it, and that Complexity comes across.
        var bartop = g.Catalog.Lookup("ItmBartop01");
        Skip.If(bartop is null, "ItmBartop01 not in this install");
        var t = WearShader.Tuning.For(bartop!.Item.Wear);
        Assert.Equal(0.91, t.Cut, 3);
        Assert.Equal(-0.12, t.Trim, 3);
        Assert.Equal(200.0, t.Complexity, 3);

        // And a def that mentions none of them still resolves to the shader defaults rather than to zeroes.
        var wall = g.Catalog.Lookup("ItmWall1x1");
        Skip.If(wall is null, "ItmWall1x1 not in this install");
        Assert.Equal(WearShader.Tuning.Default, WearShader.Tuning.For(wall!.Item.Wear));
    }

    [SkippableFact]
    public void Every_installed_part_resolves_a_usable_tuning()
    {
        var g = TestData.RequireGame();

        // A zero Complexity would collapse every texel of a part onto one noise sample, so the whole sprite
        // would wear at once or not at all. Nothing in the corpus may resolve to one.
        foreach (var p in g.Catalog.Parts)
        {
            var t = WearShader.Tuning.For(p.Item.Wear);
            Assert.True(t.Complexity != 0, $"{p.DefName} resolved Complexity 0");
            Assert.True(t.Intensity != 0, $"{p.DefName} resolved Intensity 0");
        }
    }
}

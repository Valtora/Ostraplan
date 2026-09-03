using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The nav modules' art, read out of the game's build (<see cref="NavModArt"/>). The layout maths is game-free;
/// the read itself is held against the real <c>resources.assets</c>, because the whole point of it is agreeing
/// with what the game draws, and there is no synthetic Unity file to stand in for that.
/// </summary>
public class NavModArtTests
{
    // ---- RectTransform geometry (game-free) ----

    [Fact]
    public void A_stretched_child_with_no_offsets_fills_its_anchor_rect()
    {
        var parent = new UguiLayout.PxRect(100, 50, 400, 200);
        var r = UguiLayout.Resolve(parent, (0.25, 0.5), (0.75, 1.0), (0, 0), (0, 0), (0.5, 0.5));
        Assert.Equal(200, r.X, 6);
        Assert.Equal(150, r.Y, 6);
        Assert.Equal(200, r.W, 6);
        Assert.Equal(100, r.H, 6);
    }

    [Fact]
    public void Size_delta_grows_about_the_pivot_and_anchored_position_moves_it()
    {
        // A 10-wide handle anchored to a point (min == max) at the parent's left edge, pivot centred: it straddles
        // the anchor, which is the shape of every slider handle in the prefabs.
        var parent = new UguiLayout.PxRect(0, 0, 100, 20);
        var r = UguiLayout.Resolve(parent, (0, 0), (0, 0), (10, 20), (0, 0), (0.5, 0.5));
        Assert.Equal(-5, r.X, 6);
        Assert.Equal(-10, r.Y, 6);
        Assert.Equal(10, r.W, 6);
        Assert.Equal(20, r.H, 6);

        var moved = UguiLayout.Resolve(parent, (0, 0), (0, 0), (10, 20), (30, 5), (0.5, 0.5));
        Assert.Equal(25, moved.X, 6);
        Assert.Equal(-5, moved.Y, 6);
    }

    [Fact]
    public void To_unit_flips_y_so_the_top_of_the_container_is_zero()
    {
        var container = new UguiLayout.PxRect(0, 0, 200, 100);
        // the top quarter of the container, in y-up pixels
        var top = new UguiLayout.PxRect(0, 75, 200, 25);
        var u = UguiLayout.ToUnit(top, container);
        Assert.Equal(0, u.Y, 6);
        Assert.Equal(0.25, u.H, 6);
        var bottom = new UguiLayout.PxRect(50, 0, 100, 25);
        var b = UguiLayout.ToUnit(bottom, container);
        Assert.Equal(0.25, b.X, 6);
        Assert.Equal(0.75, b.Y, 6);
        Assert.Equal(0.5, b.W, 6);
    }

    // ---- the read (game-gated) ----

    private static NavModArtPack? _pack;
    private static readonly Lock Gate = new();

    private static NavModArtPack Pack()
    {
        var g = TestData.RequireGame();
        lock (Gate) return _pack ??= NavModArt.Build(g.Env);
    }

    [SkippableFact]
    public void Every_stock_module_has_a_scene()
    {
        var pack = Pack();
        Assert.True(pack.Ok, pack.Problem);
        var g = TestData.RequireGame();
        foreach (var def in NavConsole.StandardModules)
        {
            var key = NavConsole.KeyFor(g.Catalog, def);
            Assert.NotNull(key);
            Assert.True(pack.Scenes.ContainsKey(key!), $"no scene for {def} ({key})");
            Assert.NotEmpty(pack.Scenes[key!].Ops);
        }
    }

    [SkippableFact]
    public void Time_zoom_carries_its_title_its_buttons_and_its_panel_chrome()
    {
        var scene = Pack().Scene("NavModTimeZoom");
        Assert.NotNull(scene);
        var texts = scene!.Ops.OfType<NavModTextOp>().ToList();
        // The prefab's label reads "time / zoom" in lower case with the upper-case style set, which is how the
        // game shows TIME / ZOOM; the read applies the style so a renderer does not have to know TMP's flags.
        Assert.Contains(texts, t => t.Text == "TIME / ZOOM");
        Assert.Contains(texts, t => t.Text == "STN");
        Assert.Contains(texts, t => t.Text == "RESET");
        Assert.All(texts, t => Assert.False(string.IsNullOrEmpty(t.FontKey), $"'{t.Text}' has no font"));

        var sprites = scene.Ops.OfType<NavModSpriteOp>().Select(s => Pack().Sprites[s.SpriteId]).ToList();
        Assert.Contains(sprites, s => s.Name == "GUIPanel256x256");
        Assert.Contains(sprites, s => s.Name == "GUIBtnRectGray");

        // the panel background: the cold blue every module fills its container with
        var fills = scene.Ops.OfType<NavModFill>().ToList();
        Assert.Contains(fills, f => f.Rgba == 0x404E61FF);
    }

    [SkippableFact]
    public void Sprites_decode_to_real_pixels_and_sliced_ones_carry_a_border()
    {
        var pack = Pack();
        Assert.True(pack.Ok, pack.Problem);
        var panel = pack.Sprites.First(s => s.Name == "GUIPanel256x256");
        Assert.Equal(panel.Width * panel.Height * 4, panel.Bgra.Length);
        Assert.False(panel.Border.IsEmpty);
        // not a blank: some pixel is opaque
        var opaque = 0;
        for (var i = 3; i < panel.Bgra.Length; i += 4) if (panel.Bgra[i] > 200) opaque++;
        Assert.True(opaque > 100, "the panel sprite decoded to nothing visible");
    }

    [SkippableFact]
    public void Fonts_come_out_as_truetype_data()
    {
        var pack = Pack();
        Assert.True(pack.Ok, pack.Problem);
        Assert.NotEmpty(pack.Fonts);
        foreach (var (name, data) in pack.Fonts)
        {
            // a TrueType or OpenType file opens with 0x00010000, 'true' or 'OTTO'
            var tag = BitConverter.ToUInt32([data[3], data[2], data[1], data[0]], 0);
            Assert.True(tag is 0x00010000 or 0x74727565 or 0x4F54544F, $"{name} is not a font file");
        }
    }
}

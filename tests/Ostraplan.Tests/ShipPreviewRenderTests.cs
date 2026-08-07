using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Rendering the mod preview art. The size and the room file names are the parts the game actually depends on: the
/// kiosk and chargen panels size their RawImage for the game's own 800×600 art, and the broker recovers a
/// thumbnail's room icon from its file name, so both are pinned here.
///
/// <para>The renderer is a WPF <see cref="ShipCanvas"/>, so every case runs on its own STA thread. The canvas is
/// never shown, which is fine: <c>RenderTargetBitmap</c> needs a dispatcher, not a window.</para>
/// </summary>
public class ShipPreviewRenderTests
{
    /// <summary>Render on an STA thread and hand the result back, rethrowing whatever it threw.</summary>
    private static ShipPreview? RenderSta(ShipDocument doc, System.Collections.Generic.IReadOnlyList<RoomSpecDef> specs)
    {
        ShipPreview? result = null;
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                var canvas = new ShipCanvas { Sprites = new SpriteCache() };
                canvas.SetDocument(doc);
                result = canvas.RenderGamePreview(specs);
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw failure;
        return result;
    }

    private static (int W, int H) SizeOf(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return (frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>The core salvage pod: a real ship, and one the game ships its own preview set for, so the room
    /// names this produces can be checked against the game's own.</summary>
    private static ShipDocument? LoadSalvagePod(out (GameEnv Env, DataIndex Index, Catalog Catalog) g)
    {
        g = TestData.RequireGame();
        var path = Path.Combine(g.Env.GameRoot, "Ostranauts_Data", "StreamingAssets", "data", "ships", "SalvagePod.json");
        return File.Exists(path) ? TemplateImport.LoadFile(path, g.Catalog).Doc : null;
    }

    [SkippableFact]
    public void Every_preview_is_the_800x600_frame_the_game_uses()
    {
        if (LoadSalvagePod(out var g) is not { } doc) return;

        var preview = RenderSta(doc, RoomCertifier.LoadSpecs(g.Index));

        Assert.NotNull(preview);
        Assert.Equal((800, 600), SizeOf(preview!.Ship));
        Assert.NotEmpty(preview.Rooms);
        foreach (var room in preview.Rooms) Assert.Equal((800, 600), SizeOf(room.Png));
    }

    [SkippableFact]
    public void Room_thumbnails_are_named_the_way_the_game_names_its_own()
    {
        if (LoadSalvagePod(out var g) is not { } doc) return;

        var preview = RenderSta(doc, RoomCertifier.LoadSpecs(g.Index));
        var names = preview!.Rooms.Select(r => r.Name).ToList();

        // the game's own SalvagePod folder holds Airlock.png and BridgeArea.png beside the ship image; the broker
        // maps each back to a room icon by that exact name, so drifting from it loses the icon
        Assert.Contains("Airlock", names);
        Assert.Contains("BridgeArea", names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(names, n => n is "Blank" or "");
    }

    [SkippableFact]
    public void Rendering_leaves_the_editing_view_exactly_as_it_was()
    {
        if (LoadSalvagePod(out var g) is not { } doc) return;
        var specs = RoomCertifier.LoadSpecs(g.Index);

        (double Zoom, int Rot)? before = null, after = null;
        var t = new Thread(() =>
        {
            var canvas = new ShipCanvas { Sprites = new SpriteCache() };
            canvas.SetDocument(doc);
            canvas.RotateView(90);
            before = (canvas.Zoom, canvas.ViewRot);
            canvas.RenderGamePreview(specs);
            after = (canvas.Zoom, canvas.ViewRot);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        // preview art is drawn unrotated whatever the editor is showing, and must put the view back afterwards
        Assert.Equal(before, after);
        Assert.Equal(90, after!.Value.Rot);
    }

    [SkippableFact]
    public void An_empty_design_renders_nothing()
    {
        var g = TestData.RequireGame();
        Assert.Null(RenderSta(new ShipDocument(g.Catalog), RoomCertifier.LoadSpecs(g.Index)));
    }
}

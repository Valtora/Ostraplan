using System;
using System.IO;
using System.Linq;
using Ostraplan.Core;
using Xunit;
using static Ostraplan.Tests.Fixtures;

namespace Ostraplan.Tests;

/// <summary>Temporary diagnostic: dump the light accumulation for a real wall lamp to a PPM + a scene log.</summary>
public class LightDebugDump
{
    [SkippableFact]
    public void Dump_wall_lamp_scene()
    {
        var g = TestData.RequireGame();
        var cat = g.Catalog;
        var outDir = Environment.GetEnvironmentVariable("LIGHT_DUMP_DIR");
        Skip.If(string.IsNullOrEmpty(outDir), "set LIGHT_DUMP_DIR to run");

        // a north wall run at y=5 (x=2..12), room below; lamp under the wall at (7,6) rot 0
        var placements = Enumerable.Range(2, 11).Select(x => P("ItmWall1x1", x, 5)).ToList();
        placements.Add(P("ItmLitWall1x0White", 7, 6, 0));
        // and a west wall run at x=20 (y=2..12) with a lamp east of it, try rot 90
        placements.AddRange(Enumerable.Range(2, 11).Select(y => P("ItmWall1x1", 20, y)));
        placements.Add(P("ItmLitWall1x0White", 21, 7, 90));
        var doc = Doc(cat, placements.ToArray());

        var grid = ShipGrid.FromDocument(doc, cat);
        var scene = LightNetwork.Build(grid, cat);

        var log = new System.Text.StringBuilder();
        log.AppendLine($"grid VShipPos=({grid.VShipPosX},{grid.VShipPosY}) cols={grid.NCols} rows={grid.NRows}");
        foreach (var l in scene.Lights)
            log.AppendLine($"LIGHT doc=({l.DocX:F3},{l.DocY:F3}) r={l.Radius} rgb=({l.R},{l.G},{l.B}) i={l.Intensity:F3}");
        foreach (var gl in scene.Glows)
            log.AppendLine($"GLOW doc=({gl.DocX:F3},{gl.DocY:F3}) rot={gl.Rot} img={Path.GetFileName(gl.SpriteAbs)}");
        log.AppendLine($"BLOCKS {scene.Blocks.Count}");
        foreach (var b in scene.Blocks.Take(30))
            log.AppendLine($"  block game=({b.X:F2},{b.Y:F2}) r=({b.Rx},{b.Ry}) wall={b.IsWall}");
        // lamp part resolution details
        var lampDef = cat.Lookup("ItmLitWall1x0White")!;
        foreach (var pl in cat.LightsFor(lampDef))
            log.AppendLine($"PARTLIGHT off=({pl.OffsetX},{pl.OffsetY}) px r={pl.Radius} casts={pl.CastsLight} glow={pl.GlowSprite}");
        log.AppendLine($"lamp shadowboxes={lampDef.Item.ShadowBoxes.Length} wallForLight={lampDef.Item.IsWallForLight}");

        const int ppt = 16;
        int minX = -2, minY = -2, tiles = 30;
        int w = tiles * ppt;
        var acc = LightComposite.AccumulateLights(scene, w, w, ppt, minX, minY, null);
        File.WriteAllText(Path.Combine(outDir, "lightdump.txt"), log.ToString());
        using var fs = new FileStream(Path.Combine(outDir, "lightdump.ppm"), FileMode.Create);
        var hdr = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {w}\n255\n");
        fs.Write(hdr);
        for (var i = 0; i < w * w; i++)
        {
            fs.WriteByte(acc[i * 3]);
            fs.WriteByte(acc[i * 3 + 1]);
            fs.WriteByte(acc[i * 3 + 2]);
        }

        // full composite over a flat white albedo, with the real glow PNGs decoded raw (no WPF here: manual PNG
        // decode via the test-local reader), to look for the streak artefact end-to-end
        var albedo = new byte[w * w * 4];
        for (var i = 0; i < albedo.Length; i++) albedo[i] = 255;
        var glowImgs = scene.Glows
            .Select(gl => (gl, img: PngReader.Load(gl.SpriteAbs)))
            .Where(t => t.img is not null)
            .Select(t => new GlowImage(t.gl.DocX, t.gl.DocY, t.img!.Value.W, t.img.Value.H, t.img.Value.Bgra))
            .ToList();
        var outPx = LightComposite.Compose(albedo, acc, w, w, ppt, minX, minY, glowImgs);
        using var fs2 = new FileStream(Path.Combine(outDir, "lightcompose.ppm"), FileMode.Create);
        fs2.Write(hdr);
        for (var i = 0; i < w * w; i++)
        {
            fs2.WriteByte(outPx[i * 4 + 2]);
            fs2.WriteByte(outPx[i * 4 + 1]);
            fs2.WriteByte(outPx[i * 4]);
        }
    }
}

public class LightDebugDumpReal
{
    [SkippableFact]
    public void Dump_real_ship_region()
    {
        var g = TestData.RequireGame();
        var cat = g.Catalog;
        var outDir = Environment.GetEnvironmentVariable("LIGHT_DUMP_DIR");
        var oplan = Environment.GetEnvironmentVariable("LIGHT_DUMP_OPLAN");
        Skip.If(string.IsNullOrEmpty(outDir) || string.IsNullOrEmpty(oplan), "set LIGHT_DUMP_DIR + LIGHT_DUMP_OPLAN");

        var (doc, _) = OplanFile.Load(oplan!).ToDocument(cat);
        var log = new System.Text.StringBuilder();

        // every placement whose def carries lights: def, pos, rot, resolved lights
        foreach (var p in doc.Placements)
        {
            if (cat.Lookup(p.DefName) is not { } def) continue;
            var lights = cat.LightsFor(def);
            if (lights.Count == 0) continue;
            log.AppendLine($"{p.DefName} at ({p.X},{p.Y}) rot={p.Rot}: " +
                string.Join("; ", lights.Select(l => $"cast={l.CastsLight} off=({l.OffsetX},{l.OffsetY})px glow={(l.GlowSprite is null ? "-" : Path.GetFileName(l.GlowSprite))}")));
        }
        File.WriteAllText(Path.Combine(outDir!, "realship.txt"), log.ToString());

        // composite the WHOLE ship over a flat white albedo
        var grid = ShipGrid.FromDocument(doc, cat);
        var scene = LightNetwork.Build(grid, cat);
        var b = doc.Bounds()!.Value;
        const int ppt = 16;
        int minX = b.MinX - 2, minY = b.MinY - 2;
        int tw = b.MaxX - b.MinX + 5, th = b.MaxY - b.MinY + 5;
        int w = tw * ppt, h = th * ppt;
        var acc = LightComposite.AccumulateLights(scene, w, h, ppt, minX, minY, null);
        var albedo = new byte[w * h * 4];
        for (var i = 0; i < albedo.Length; i++) albedo[i] = 255;
        var glowImgs = scene.Glows
            .Select(gl => (gl, img: PngReader.Load(gl.SpriteAbs)))
            .Where(t => t.img is not null)
            .Select(t => new GlowImage(t.gl.DocX, t.gl.DocY, t.img!.Value.W, t.img.Value.H, t.img.Value.Bgra))
            .ToList();
        var outPx = LightComposite.Compose(albedo, acc, w, h, ppt, minX, minY, glowImgs);
        using var fs = new FileStream(Path.Combine(outDir!, "realship.ppm"), FileMode.Create);
        fs.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n"));
        for (var i = 0; i < w * h; i++)
        {
            fs.WriteByte(outPx[i * 4 + 2]);
            fs.WriteByte(outPx[i * 4 + 1]);
            fs.WriteByte(outPx[i * 4]);
        }
        File.AppendAllText(Path.Combine(outDir!, "realship.txt"), $"\nBUFFER origin=({minX},{minY}) tiles={tw}x{th}\n");
    }
}

/// <summary>Minimal PNG reader (8-bit RGBA/RGB, non-interlaced) so the diagnostic runs without WPF.</summary>
internal static class PngReader
{
    public static (int W, int H, byte[] Bgra)? Load(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            int pos = 8, w = 0, h = 0, ctype = 0;
            var idat = new MemoryStream();
            while (pos < data.Length)
            {
                var len = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
                var typ = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
                if (typ == "IHDR")
                {
                    w = (data[pos + 8] << 24) | (data[pos + 9] << 16) | (data[pos + 10] << 8) | data[pos + 11];
                    h = (data[pos + 12] << 24) | (data[pos + 13] << 16) | (data[pos + 14] << 8) | data[pos + 15];
                    ctype = data[pos + 17];
                }
                else if (typ == "IDAT") idat.Write(data, pos + 8, len);
                else if (typ == "IEND") break;
                pos += len + 12;
            }
            var bpp = ctype == 6 ? 4 : 3;
            using var z = new System.IO.Compression.ZLibStream(new MemoryStream(idat.ToArray()), System.IO.Compression.CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            var raw = ms.ToArray();
            var bgra = new byte[w * h * 4];
            var stride = w * bpp;
            var prev = new byte[stride];
            var idx = 0;
            for (var y = 0; y < h; y++)
            {
                var f = raw[idx++];
                var line = new byte[stride];
                Array.Copy(raw, idx, line, 0, stride);
                idx += stride;
                for (var x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? line[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
                    line[x] = f switch
                    {
                        1 => (byte)(line[x] + a),
                        2 => (byte)(line[x] + b),
                        3 => (byte)(line[x] + (a + b) / 2),
                        4 => (byte)(line[x] + Paeth(a, b, c)),
                        _ => line[x],
                    };
                }
                prev = line;
                for (var x = 0; x < w; x++)
                {
                    var s = x * bpp;
                    var d = (y * w + x) * 4;
                    bgra[d] = line[s + 2];
                    bgra[d + 1] = line[s + 1];
                    bgra[d + 2] = line[s];
                    bgra[d + 3] = bpp == 4 ? line[s + 3] : (byte)255;
                }
            }
            return (w, h, bgra);
        }
        catch { return null; }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

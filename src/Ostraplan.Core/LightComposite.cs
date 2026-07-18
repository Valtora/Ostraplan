namespace Ostraplan.Core;

/// <summary>A glow decal's decoded pixels (plain BGRA32, not premultiplied), supplied by the app layer.</summary>
public sealed record GlowImage(double DocX, double DocY, int W, int H, byte[] Bgra);

/// <summary>
/// The software equivalent of the game's deferred light pass, pixel-exact at the game's native 16 px/tile:
/// <para>
/// For each light, the lit region comes from <see cref="VisibilityMesh"/> (the exact shadow-mesh port) and each
/// covered pixel is shaded with the game's <c>Sprites/LoSPass</c> fragment program (disassembled from the shipped
/// shader blob): with <c>u = (pixel − centre)/(2R)</c>, <c>F = 3</c> (<c>_LightFalloff</c>), <c>Z = 0.25</c>
/// (<c>_LightZ</c>) —
/// <c>L = normalize(−u.x·F, −u.y·F, F·Z)</c>, <c>atten = 1/(F²(|u|²+Z²) + 0.1)</c>,
/// <c>diffuse = max(0, N·L)</c> (N from the normal map, z forced 1, unnormalised),
/// contribution = <c>colour × alpha × diffuse × atten</c>, clamped to 8-bit per light (the LDR render target).
/// </para>
/// <para>
/// Lights accumulate with the pass's <c>Blend OneMinusDstColor One</c> — the screen blend
/// (<c>acc' = src(1−acc) + acc</c> per channel), so overlapping lights saturate softly toward white. The final
/// ship image is <c>albedo × accumulated light</c> (ambient is black and the game's main camera doesn't draw the
/// sprite layer at all — the lit mesh IS the visible ship), plus the additive glow decals
/// (<c>rgb × a²</c>, <c>Sprites/DefaultAdditive</c>). AO, fog-of-war and CRT post are deliberately out of scope.
/// </para>
/// </summary>
public static class LightComposite
{
    private const float F = 3f;      // _LightFalloff (Visibility.nLightFalloff)
    private const float Z = 0.25f;   // _LightZ (Visibility.fLightZ)

    /// <summary>
    /// Accumulate every light in <paramref name="scene"/> into a per-pixel light map (RGB bytes, screen-blended).
    /// The buffer covers <paramref name="w"/>×<paramref name="h"/> pixels at <paramref name="pxPerTile"/>, whose
    /// pixel (0,0) sits at document tile (<paramref name="originX"/>, <paramref name="originY"/>).
    /// <paramref name="normalBgra"/> may be null (flat surfaces); channels: r → nx (+right), g → ny (+down, doc
    /// convention), sampled only where its alpha is non-zero.
    /// </summary>
    public static byte[] AccumulateLights(
        LightScene scene, int w, int h, int pxPerTile, double originX, double originY, byte[]? normalBgra)
    {
        var acc = new byte[w * h * 3];
        foreach (var light in scene.Lights)
        {
            var region = VisibilityMesh.Build((float)light.DocX, (float)-light.DocY, (float)light.Radius, scene.Blocks);
            void Shade(float ax, float ay, float bx, float by, float cx, float cy) =>
                ShadeTriangle(acc, w, h, pxPerTile, originX, originY, normalBgra, light, ax, ay, bx, by, cx, cy);

            // fan triangles centre→A→B, then lit-block quads (two triangles each) — light-local game coords
            foreach (var (a, b) in region.Fan)
                Shade(0f, 0f, a.X, a.Y, b.X, b.Y);
            foreach (var (p1, p2, p3, p4) in region.Quads)
            {
                Shade(p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);
                Shade(p4.X, p4.Y, p1.X, p1.Y, p3.X, p3.Y);
            }
        }
        return acc;
    }

    /// <summary>
    /// Compose the final Light Viz image (premultiplied BGRA32): <c>albedo × light</c> plus additive glow decals.
    /// <paramref name="albedoPbgra"/> is the ship rendered plainly (premultiplied, as WPF renders it); the output
    /// keeps its alpha, so unlit hull reads as a black silhouette over the canvas background — exactly the
    /// in-game look of an unlit ship against space.
    /// </summary>
    public static byte[] Compose(byte[] albedoPbgra, byte[] lightAcc, int w, int h, int pxPerTile,
        double originX, double originY, IReadOnlyList<GlowImage> glows)
    {
        var output = new byte[w * h * 4];
        for (int i = 0, j = 0; j < output.Length; i += 3, j += 4)
        {
            output[j] = (byte)(albedoPbgra[j] * lightAcc[i + 2] / 255);          // B × light.b
            output[j + 1] = (byte)(albedoPbgra[j + 1] * lightAcc[i + 1] / 255);  // G × light.g
            output[j + 2] = (byte)(albedoPbgra[j + 2] * lightAcc[i] / 255);      // R × light.r
            output[j + 3] = albedoPbgra[j + 3];                                  // alpha: the ship's silhouette
        }

        // glow decals: rgb × a² added (Sprites/DefaultAdditive: out = (t.rgb·t.a, t.a), Blend SrcAlpha One).
        // Adding emission to a premultiplied buffer is well-formed: it shows additively over whatever is behind.
        foreach (var g in glows)
        {
            var px0 = (int)Math.Round((g.DocX - originX) * pxPerTile - g.W / 2.0);
            var py0 = (int)Math.Round((g.DocY - originY) * pxPerTile - g.H / 2.0);
            for (var gy = 0; gy < g.H; gy++)
            {
                var oy = py0 + gy;
                if (oy < 0 || oy >= h) continue;
                for (var gx = 0; gx < g.W; gx++)
                {
                    var ox = px0 + gx;
                    if (ox < 0 || ox >= w) continue;
                    var gi = (gy * g.W + gx) * 4;
                    var a = g.Bgra[gi + 3];
                    if (a == 0) continue;
                    var oi = (oy * w + ox) * 4;
                    var a2 = a * a;   // t.a² (the shader multiplies rgb by a, the blend by src alpha again)
                    output[oi] = ClampAdd(output[oi], g.Bgra[gi] * a2 / (255 * 255));
                    output[oi + 1] = ClampAdd(output[oi + 1], g.Bgra[gi + 1] * a2 / (255 * 255));
                    output[oi + 2] = ClampAdd(output[oi + 2], g.Bgra[gi + 2] * a2 / (255 * 255));
                }
            }
        }
        return output;
    }

    private static byte ClampAdd(byte a, int add) => (byte)Math.Min(255, a + add);

    /// <summary>
    /// Scanline-rasterize one triangle (light-local game coords) and screen-blend the shaded contribution into
    /// <paramref name="acc"/>. Pixel centres at +0.5 with half-open spans, so triangles sharing an edge never
    /// double-blend a pixel (the GPU's watertight fill rule).
    /// </summary>
    private static void ShadeTriangle(
        byte[] acc, int w, int h, int pxPerTile, double originX, double originY, byte[]? normalBgra,
        SceneLight light, float ax, float ay, float bx, float by, float cx, float cy)
    {
        // light-local game coords → buffer pixels (doc y = −game y)
        var lx = (light.DocX - originX) * pxPerTile;
        var ly = (light.DocY - originY) * pxPerTile;
        Span<double> xs = [lx + ax * pxPerTile, lx + bx * pxPerTile, lx + cx * pxPerTile];
        Span<double> ys = [ly - ay * pxPerTile, ly - by * pxPerTile, ly - cy * pxPerTile];

        var yMin = Math.Min(ys[0], Math.Min(ys[1], ys[2]));
        var yMax = Math.Max(ys[0], Math.Max(ys[1], ys[2]));
        var rowFirst = Math.Max(0, (int)Math.Ceiling(yMin - 0.5));
        var rowLast = Math.Min(h - 1, (int)Math.Ceiling(yMax - 0.5) - 1);

        var invTiles = 1.0 / pxPerTile;
        var twoR = 2.0 * light.Radius;

        for (var py = rowFirst; py <= rowLast; py++)
        {
            var yc = py + 0.5;
            // crossings of the scanline with the triangle edges (half-open: y0 ≤ yc < y1 or y1 ≤ yc < y0)
            double x0 = double.MaxValue, x1 = double.MinValue;
            for (var e = 0; e < 3; e++)
            {
                var f = (e + 1) % 3;
                if (ys[e] <= yc == ys[f] <= yc) continue;
                var x = xs[e] + (yc - ys[e]) * (xs[f] - xs[e]) / (ys[f] - ys[e]);
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
            }
            if (x0 > x1) continue;

            var colFirst = Math.Max(0, (int)Math.Ceiling(x0 - 0.5));
            var colLast = Math.Min(w - 1, (int)Math.Ceiling(x1 - 0.5) - 1);
            for (var px = colFirst; px <= colLast; px++)
            {
                // the LoSPass fragment program, per pixel centre
                var ux = ((px + 0.5) - lx) * invTiles / twoR;   // u = (pixel − centre)/(2R), doc convention
                var uy = ((py + 0.5) - ly) * invTiles / twoR;
                var lenSq = F * F * (ux * ux + uy * uy + Z * Z);
                var atten = 1.0 / (lenSq + 0.1);
                var invLen = 1.0 / Math.Sqrt(lenSq);

                double diffuse;
                var idx = (py * w + px) * 4;
                if (normalBgra is not null && normalBgra[idx + 3] != 0)
                {
                    var nx = normalBgra[idx + 2] * (2.0 / 255) - 1.0;   // r → nx (+right)
                    var ny = normalBgra[idx + 1] * (2.0 / 255) - 1.0;   // g → ny (+down)
                    diffuse = Math.Max(0.0, (nx * (-ux * F) + ny * (-uy * F) + F * Z) * invLen);
                }
                else
                    diffuse = F * Z * invLen;   // flat surface: N = (0, 0, 1)

                var k = light.Intensity * diffuse * atten;
                var oi = (py * w + px) * 3;
                Blend(acc, oi, light.R, k);
                Blend(acc, oi + 1, light.G, k);
                Blend(acc, oi + 2, light.B, k);
            }
        }
    }

    /// <summary>One channel of the pass's <c>Blend OneMinusDstColor One</c> (screen blend), in the render
    /// target's 8-bit arithmetic: the per-light contribution clamps to [0, 255] first (LDR), then
    /// <c>acc' = src×(255−acc)/255 + acc</c>.</summary>
    private static void Blend(byte[] acc, int i, byte channel, double k)
    {
        var src = (int)Math.Round(Math.Clamp(channel * k, 0.0, 255.0));
        if (src == 0) return;
        acc[i] = (byte)Math.Min(255, acc[i] + (src * (255 - acc[i]) + 127) / 255);
    }
}

namespace Ostraplan.Core;

/// <summary>One casting light in the scene, in <b>document</b> coords (tiles, +y down): centre (sub-tile — a wall
/// lamp's light sits offset into its room), radius in tiles, colour and intensity (colour alpha / 255).</summary>
public sealed record SceneLight(double DocX, double DocY, double Radius, byte R, byte G, byte B, double Intensity);

/// <summary>One additive glow decal (a lamp's <c>strImg</c> quad): centre in document coords + the resolved PNG +
/// the part's rotation (the game parents the quad to the item, so the decal's pixels turn with it — a wall lamp's
/// glow bar lies along its wall). The game draws these into its lights RT with <c>Sprites/DefaultAdditive</c> —
/// <c>rgb × a²</c> added over the lit scene at native 16 px/tile. Every <c>aLights</c> entry with an image gets
/// one, casting or not.</summary>
public sealed record GlowDecal(double DocX, double DocY, string SpriteAbs, int Rot);

/// <summary>Light Viz's exterior daylight settings: which parallax location's sun lights shine, and the world
/// rotation of the sun constellation (the game's <c>fRotationWorld</c>, degrees CCW in game coords).</summary>
public sealed record SunSettings(string ParallaxName, double AngleDeg);

/// <summary>
/// Everything the light compositor needs from the document, resolved by <see cref="LightNetwork.Build"/> on the
/// scan thread: the casting lights and glow decals (document coords), and the occluder blocks in <b>game</b>
/// coords (+y up — the visibility geometry runs in the game's own space so windings and skirt normals stay
/// sign-exact; y is negated at the boundary).
/// </summary>
public sealed record LightScene(
    IReadOnlyList<SceneLight> Lights,
    IReadOnlyList<LightBlock> Blocks,
    IReadOnlyList<GlowDecal> Glows)
{
    public static readonly LightScene Empty = new([], [], []);

    public bool IsEmpty => Lights.Count == 0 && Glows.Count == 0;
}

/// <summary>
/// Builds the Light Viz scene from a resolved grid — the game's lighting model exactly (decompiled
/// <c>Item.SetData</c>/<c>Visibility</c>/<c>ParallaxController</c>, 0.34.x):
/// <list type="bullet">
/// <item>A part's lights come from its item def's <c>aLights</c>; a light illuminates iff its colour is not
/// <c>"Blank"</c>, at the def's <c>fRadius</c> (default 6; real lamps carry 18), positioned at <c>ptPos</c>/16
/// tiles from the item centre, rotated with the part. Intensity = colour alpha / 255.</item>
/// <item>Every <c>aLights</c> entry with a <c>strImg</c> adds an additive glow decal at the same point —
/// illuminating or not (status LEDs are glow-only decals).</item>
/// <item>Occluders are the item defs' <c>aShadowBoxes</c> — <b>not</b> <c>IsWall</c>: windows are glass (light
/// passes), thin/aero walls carry no boxes (no occlusion), open doors block only their end caps, and beds,
/// canisters, reactor pods… do occlude. Boxes rotate with the part (half-extents swap at 90°/270°, the game's
/// <c>Block.RotateCW</c>).</item>
/// <item>Sun lights (optional): the chosen parallax location's <c>aSunLights</c>, radius 1000, positioned at
/// their raw <c>ptPos</c> (tiles) rotated by the sun angle around the ship's centre — the game parents them to a
/// far sun transform whose rotation tracks the world background.</item>
/// </list>
/// Lighting gates nothing in-game (no darkness stat), so this remains a faithful preview, not a Law port.
/// </summary>
public static class LightNetwork
{
    public static LightScene Build(ShipGrid grid, Catalog catalog, SunSettings? sun = null)
    {
        var lights = new List<SceneLight>();
        var glows = new List<GlowDecal>();
        var blocks = new List<LightBlock>();

        foreach (var part in grid.Parts)
        {
            if (catalog.Lookup(part.Part.DefName) is not { } def) continue;
            foreach (var light in catalog.LightsFor(def))
            {
                var (ox, oy) = grid.MapPointPos(part, (light.OffsetX, light.OffsetY));
                double docX = ox + grid.VShipPosX, docY = oy + grid.VShipPosY;
                if (light.CastsLight && light.Intensity > 0 && light.Radius > 0)
                    lights.Add(new SceneLight(docX, docY, light.Radius, light.R, light.G, light.B, light.Intensity));
                if (light.GlowSprite is not null)
                    glows.Add(new GlowDecal(docX, docY, light.GlowSprite, GridMath.Norm(part.Rot)));
            }

            var swap = part.Rot is 90 or 270;
            foreach (var box in def.Item.ShadowBoxes)
            {
                if (box.Glass) continue;   // glass never occludes (Visibility.AddOccludersFromCrewSimBlocks)
                var (bx, by) = grid.MapPointPos(part, (box.Dx * 16.0, box.Dy * 16.0));
                blocks.Add(new LightBlock(
                    (float)(bx + grid.VShipPosX), (float)-(by + grid.VShipPosY),
                    (float)(swap ? box.Ry : box.Rx), (float)(swap ? box.Rx : box.Ry),
                    def.Item.IsWallForLight));
            }
        }

        if (sun is not null && catalog.ParallaxDefs.TryGetValue(sun.ParallaxName, out var px))
        {
            // anchor the constellation at the grid centre: the game parents suns to a world-origin transform, and
            // at 250+ tiles out the direction across a ship barely varies, so the anchor choice is invisible
            double anchorX = grid.VShipPosX + grid.NCols / 2.0, anchorY = grid.VShipPosY + grid.NRows / 2.0;
            var rad = sun.AngleDeg * Math.PI / 180.0;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);
            foreach (var name in px.SunLightNames)
            {
                if (!catalog.LightDefs.TryGetValue(name, out var ld) || ld.Color == "Blank") continue;
                if (!catalog.ColorTable.TryGetValue(ld.Color, out var c) || c.A == 0) continue;
                // sun ptPos is used RAW (world tiles, not /16 — ParallaxController sets LocalPosition = ptPos)
                var gx = ld.PosX * cos - ld.PosY * sin;   // rotate in game coords (+y up, CCW)
                var gy = ld.PosX * sin + ld.PosY * cos;
                lights.Add(new SceneLight(anchorX + gx, anchorY - gy, ld.SunRadius, c.R, c.G, c.B, c.A / 255.0));
            }
        }

        return lights.Count == 0 && glows.Count == 0 && blocks.Count == 0
            ? LightScene.Empty
            : new LightScene(lights, blocks, glows);
    }
}

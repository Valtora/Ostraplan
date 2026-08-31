using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Ostraplan.Core;

/// <summary>
/// A part's sprite size in tiles — the game's <c>vScale</c> from <c>Item.SetData</c>, and the one measurement the
/// damage solvers need that is neither the socket footprint nor anything the palette already carries.
///
/// <para><b>Why this is the collider.</b> Every item in the game is instantiated from one prefab,
/// <c>prefabQuad</c>, whose <c>BoxCollider</c> is size <c>(1,1,1)</c> at centre <c>(0,0,0.5)</c>;
/// <c>Item.ResetTransforms</c> overwrites only the z terms and sets <c>localScale = (vScale.x, vScale.y, 1)</c>.
/// So an item's damage collider is exactly its sprite rectangle, centred on its transform, and a micrometeoroid
/// raycast hits the sprite rather than the footprint (§26). For the 7×7 fuel tanks that is a 3×3 target.</para>
///
/// <para><b>Footprint is a different number and stays a different number</b> (§4). The Law, rooms and everything
/// else read <see cref="ItemDef.Width"/>/<see cref="ItemDef.Height"/>; only the damage geometry reads this.</para>
///
/// <para>Reads the PNG header rather than decoding the image, so it belongs in Core and costs nothing: a PNG's
/// dimensions are two big-endian <c>uint32</c>s at byte 16 of the IHDR chunk, which is always the first chunk.
/// Results are cached per absolute path.</para>
/// </summary>
public static class SpriteExtent
{
    private static readonly ConcurrentDictionary<string, (int W, int H)> Cache = new(StringComparer.Ordinal);

    /// <summary>Fallback for a part with no sprite on disk. One tile, which is what the game's own
    /// <c>Mathf.Max(..., 1)</c> floor would give for a missing or sub-16px texture.</summary>
    public static readonly (int W, int H) Unknown = (1, 1);

    /// <summary>
    /// The part's sprite size in tiles, exactly as <c>Item.SetData</c> derives <c>vScale</c>:
    /// <c>1 × 1</c> for a sprite-sheet part (walls, floors, anything autotiled — the game hard-sets it and never
    /// looks at the texture), otherwise <c>max(round(texturePx / 16), 1)</c> per axis.
    ///
    /// <para>The game has a third branch for an animated def, which divides the texture by the animation's rows and
    /// columns first. No shipped item def declares <c>objAnimation</c> (0 of 1,034 on stock 1.0.0.11), so it is not
    /// modelled; a mod that used one would read one tile too large per axis here.</para>
    /// </summary>
    public static (int W, int H) Tiles(PartDef part)
    {
        // A sheet part's scale never depends on its texture: Item.SetData sets vScale.x = vScale.y = 1 outright,
        // because the sheet holds every autotile variant side by side and one cell is one tile.
        if (part.Item.HasSpriteSheet) return (1, 1);
        return part.SpriteAbs is { } path ? Tiles(path) : Unknown;
    }

    /// <summary>
    /// The tile size of a PNG on disk: <c>max(round(px / 16), 1)</c> per axis, or <see cref="Unknown"/> when the
    /// file is missing or is not a readable PNG.
    ///
    /// <para><b>Only a real measurement is cached.</b> A failed read returns <see cref="Unknown"/> and is measured
    /// again next time, because the cache is static and lives as long as the process: caching the failure turned
    /// one transient read error into a part drawn at one tile until the app was restarted, which is exactly what
    /// issue #57 was. Doors were what showed it, since a closed door's sprite is the rare one measured lazily in
    /// the middle of a session rather than during the startup bake, and it is measured the instant a door is shut.
    /// The retry costs a 24-byte read on a path that is failing anyway.</para>
    /// </summary>
    public static (int W, int H) Tiles(string absPath)
    {
        if (Cache.TryGetValue(absPath, out var hit)) return hit;
        var (pw, ph) = PixelSize(absPath);
        if (pw <= 0 || ph <= 0) return Unknown;
        var tiles = FromPixels(pw, ph);
        Cache[absPath] = tiles;
        return tiles;
    }

    /// <summary>The tile size of a sprite of a known pixel size — <c>Item.SetData</c>'s <c>vScale</c> rule, in one
    /// place so a caller measuring a decoded bitmap and one reading a PNG header cannot round differently.</summary>
    public static (int W, int H) FromPixels(int pixelW, int pixelH) =>
        (Math.Max(1, (int)Math.Round(pixelW / 16.0)), Math.Max(1, (int)Math.Round(pixelH / 16.0)));

    /// <summary>A PNG's pixel dimensions straight out of its IHDR, or (0,0) when the file is unreadable or does not
    /// carry the PNG signature. Reads 24 bytes and never decodes.</summary>
    public static (int W, int H) PixelSize(string absPath)
    {
        try
        {
            // The widest sharing the API offers, rather than File.OpenRead's FileShare.Read. These files are the
            // game's own and we only ever read them, so nothing here needs to exclude anybody — while a scanner or
            // an indexer holding the file open for write is enough to fail an OpenRead, and did (#57).
            using var fs = new FileStream(absPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[24];
            if (fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return (0, 0);
            // 8-byte signature, then the IHDR chunk: 4 length, 4 type, then width and height.
            if (!head[..8].SequenceEqual<byte>([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
                return (0, 0);
            if (!head[12..16].SequenceEqual("IHDR"u8)) return (0, 0);
            return ((int)BinaryPrimitives.ReadUInt32BigEndian(head[16..20]),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(head[20..24]));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (0, 0);
        }
    }
}

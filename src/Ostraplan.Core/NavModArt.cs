using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace Ostraplan.Core;

/// <summary>
/// The nav modules' on-screen art, read out of the game's own build.
///
/// <para>A module's screen is not in the JSON. <c>GUIOrbitDraw.LoadModules</c> does
/// <c>Resources.Load("GUIShip/GUIOrbitDraw/" + strGUIPrefab)</c>, and that prefab, one <c>NavMod*</c> per module,
/// is serialized into <c>Ostranauts_Data\resources.assets</c> along with the sprites it draws and the fonts its
/// labels use. Nothing here runs the engine: the file is parsed as data (AssetsTools.NET, with a class database for
/// the engine's own types and the game's managed DLLs read for the layout of its script fields), the prefab's
/// hierarchy is walked, and each visible piece becomes an op in a <see cref="NavModScene"/>. That is the same
/// standing as reading a PNG out of <c>StreamingAssets</c>: the user's install, at runtime, distributed by nobody
/// (SCOPE.md).</para>
///
/// <para><b>What is reproduced:</b> <c>RectTransform</c> placement, <c>Image</c> fills and sprites (simple and
/// nine-sliced, tinted), and <c>TextMeshProUGUI</c> labels (text, font, size, auto-size range, colour, alignment,
/// the upper-case style). <b>What is not:</b> layout groups and aspect fitters, whose children are drawn where the
/// prefab last saved them; <c>RawImage</c> screens (the map and the MFDs), which are drawn as black glass; the
/// font materials' glow; and every value the game writes at runtime. Lamps and toggles show whatever state the
/// prefab was saved in. The point is a module the eye can name, not a screen it can read.</para>
///
/// <para>The read is pinned to the game's <b>engine</b> version rather than its data: the class database knows
/// the serialized layout of Unity's own types per engine version, and a game update that moves to a newer engine
/// than the database covers makes <see cref="Build"/> fail with a reason rather than throw, and the arrange window
/// falls back to its flat panels. Re-verify: the Unity version in <c>resources.assets</c> (last read
/// <c>6000.3.10f1</c>, game 1.0.0.13) against <c>Assets\classdata.tpk</c>.</para>
/// </summary>
public static class NavModArt
{
    /// <summary>Where <c>Resources.Load</c> finds a module, for the record. The prefab's name is the
    /// <c>strGUIPrefab</c> the console's <c>NavModConfig</c> is keyed by (<see cref="NavConsole.KeyFor"/>), which
    /// is what makes a scene addressable by the same key the arrange window already uses.</summary>
    public const string PrefabPrefix = "NavMod";

    /// <summary>The board the modules sit on: <c>GUIOrbitDraw</c>'s own tint, a blue-grey a shade darker than the
    /// modules' panels.</summary>
    public const uint BoardRgba = 0x394556FF;

    private const string ClassDatabaseResource = "Ostraplan.classdata.tpk";

    /// <summary>Read every module's scene, the sprites they draw and the fonts they use. Never throws: a build
    /// that cannot proceed returns a pack whose <see cref="NavModArtPack.Problem"/> says why.</summary>
    /// <param name="screenSizes">How much of the screen each module takes at stock, by key
    /// (<see cref="NavConsole.ScreenSizes"/>). A prefab is laid out at that size, since its pixel-sized pieces were
    /// drawn for it; a module with no entry is laid out at the size its prefab saved its container at, which for
    /// all but one of them is the same thing.</param>
    public static NavModArtPack Build(GameEnv env, IReadOnlyDictionary<string, (double W, double H)>? screenSizes = null)
    {
        var dataDir = Path.Combine(env.GameRoot, "Ostranauts_Data");
        var assets = Path.Combine(dataDir, "resources.assets");
        var managed = Path.Combine(dataDir, "Managed");
        if (!File.Exists(assets)) return NavModArtPack.Failed($"{assets} is not there.");
        if (!Directory.Exists(managed)) return NavModArtPack.Failed($"{managed} is not there.");

        var am = new AssetsManager();
        try
        {
            using (var tpk = typeof(NavModArt).Assembly.GetManifestResourceStream(ClassDatabaseResource))
            {
                if (tpk is null) return NavModArtPack.Failed("The class database is missing from the build.");
                am.LoadClassPackage(tpk);
            }
            var inst = am.LoadAssetsFile(assets, loadDeps: true);
            var version = inst.file.Metadata.UnityVersion;
            if (am.LoadClassDatabaseFromPackage(version) is null)
                return NavModArtPack.Failed($"No class database for Unity {version}.");
            am.MonoTempGenerator = new MonoCecilTempGenerator(managed);

            var reader = new Reader(am, inst, screenSizes);
            return reader.ReadAll(version);
        }
        catch (Exception ex)
        {
            return NavModArtPack.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            am.UnloadAll(unloadClassData: true);
        }
    }

    /// <summary>One pass over the file. Holds the caches a walk needs: decoded textures by asset, and the sprites
    /// and fonts the scenes have referenced so far.</summary>
    private sealed class Reader(AssetsManager am, AssetsFileInstance inst,
        IReadOnlyDictionary<string, (double W, double H)>? screenSizes)
    {
        private readonly Dictionary<(int File, long Path), int> _spriteIds = new();
        private readonly List<NavModSprite> _sprites = [];
        // A pointer is relative to the file it was read from, and a sprite the UI shares with a scene lives in
        // sharedassets0 rather than resources, so a texture is keyed by the sprite's file as well as the pointer.
        private readonly Dictionary<(string Owner, int File, long Path), DecodedTexture?> _textures = new();
        private readonly Dictionary<string, byte[]> _fonts = new(StringComparer.Ordinal);
        private readonly Dictionary<(int File, long Path), string> _fontKeys = new();
        private readonly Dictionary<(int File, long Path), string?> _classNames = new();
        private readonly List<string> _notes = [];

        private sealed record DecodedTexture(int Width, int Height, byte[] Bgra);

        /// <summary>The board the modules are shown on. <c>LoadModules</c> looks for a module under it before it
        /// loads the module's prefab, and two modules (Map and Controls) exist nowhere else.</summary>
        private const string BoardName = "GUIOrbitDraw";

        public NavModArtPack ReadAll(string unityVersion)
        {
            // A module's name can be carried by more than one GameObject: its prefab root (no parent), the copy
            // the board prefab embeds (parent GUIOrbitDraw, which is what the console shows), and the copy the
            // PDA's nav app embeds (parent GUIPDANAV, laid out for a hand-held and no use here). Every child of a
            // prefab is a GameObject too, and the damaged twins draw a module the planner never places.
            var candidates = new Dictionary<string, List<(AssetTypeValueField Go, string? Parent)>>(StringComparer.Ordinal);
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.GameObject))
            {
                var go = am.GetBaseField(inst, info);
                var name = go["m_Name"].AsString;
                if (!name.StartsWith(PrefabPrefix, StringComparison.Ordinal)) continue;
                if (name.EndsWith("Dmg", StringComparison.Ordinal) || name == "NavModWrapper") continue;
                string? parent;
                try { parent = ParentName(go); }
                catch { continue; }
                if (!candidates.TryGetValue(name, out var list)) candidates[name] = list = [];
                list.Add((go, parent));
            }

            var scenes = new Dictionary<string, NavModScene>(StringComparer.Ordinal);
            foreach (var (name, list) in candidates)
            {
                var pick = list.FirstOrDefault(c => c.Parent == BoardName).Go ?? list.FirstOrDefault(c => c.Parent is null).Go;
                if (pick is null) continue;
                try
                {
                    if (ReadScene(name, pick) is { } scene) scenes[name] = scene;
                }
                catch (Exception ex)
                {
                    _notes.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (scenes.Count == 0)
                return NavModArtPack.Failed($"No {PrefabPrefix}* prefabs found (Unity {unityVersion}).");
            return new NavModArtPack(scenes, _sprites, _fonts, unityVersion, _notes);
        }

        /// <summary>The name of the GameObject this one's transform hangs off, or null for a prefab root.</summary>
        private string? ParentName(AssetTypeValueField go)
        {
            var rt = FirstTransform(go);
            if (rt is null) return null;
            var father = rt["m_Father"];
            if (father.IsDummy || father["m_PathID"].AsLong == 0) return null;
            var parentRt = am.GetExtAsset(inst, father).baseField;
            if (parentRt is null) return null;
            return am.GetExtAsset(inst, parentRt["m_GameObject"]).baseField?["m_Name"].AsString;
        }

        /// <summary>A module root is a full-screen <c>RectTransform</c> whose one meaningful child is
        /// <c>Container</c>, the rect the console positions. Everything visible hangs off the container, so the
        /// container is the unit square and the root is only walked to find it.</summary>
        private NavModScene? ReadScene(string key, AssetTypeValueField rootGo)
        {
            var rootRt = FirstTransform(rootGo);
            if (rootRt is null) return null;
            AssetTypeValueField? container = null;
            foreach (var child in rootRt["m_Children.Array"])
            {
                var rt = am.GetExtAsset(inst, child).baseField;
                var go = am.GetExtAsset(inst, rt["m_GameObject"]).baseField;
                if (go["m_Name"].AsString == "Container") { container = rt; break; }
            }
            if (container is null) return null;

            // The container is laid out at the size the console gives the module, which gives its children real
            // pixel dimensions (a button's fixed offset means nothing as a fraction of an unknown width). The
            // prefab's own anchors stand in when the data has no size for it; they agree for every stock module
            // but Controls, whose prefab keeps its container full-screen.
            var board = new UguiLayout.PxRect(0, 0, NavModScene.ReferenceBoardWidth, NavModScene.ReferenceBoardHeight);
            var containerPx = screenSizes is not null && screenSizes.TryGetValue(key, out var stock)
                ? new UguiLayout.PxRect(0, 0, stock.W * board.W, stock.H * board.H)
                : UguiLayout.Resolve(board,
                    Vec(container["m_AnchorMin"]), Vec(container["m_AnchorMax"]),
                    (0, 0), (0, 0), Vec(container["m_Pivot"]));
            if (containerPx.W <= 0 || containerPx.H <= 0) return null;

            var ops = new List<NavModOp>();
            var containerGo = am.GetExtAsset(inst, container["m_GameObject"]).baseField;
            Walk(containerGo, container, containerPx, UguiLayout.Affine.Identity, containerPx, ops, depth: 0);
            return new NavModScene(key, ops);
        }

        /// <summary>
        /// One node and everything under it. <paramref name="parent"/> is the parent's rect in the parent's own
        /// (unrotated) frame, which is what a child's anchors are relative to; <paramref name="toContainer"/> maps
        /// that frame into the container's, carrying every rotation and scale above this node, since Unity applies
        /// a node's transform to its whole subtree.
        /// </summary>
        private void Walk(AssetTypeValueField go, AssetTypeValueField rt, UguiLayout.PxRect parent,
            UguiLayout.Affine toContainer, UguiLayout.PxRect container, List<NavModOp> ops, int depth)
        {
            if (!go["m_IsActive"].AsBool) return;
            if (depth > 24) return;   // a prefab is a tree; this only guards a cycle in a damaged file

            var rect = parent;
            var map = toContainer;
            if (depth > 0)
            {
                var pivot = Vec(rt["m_Pivot"]);
                rect = UguiLayout.Resolve(parent, Vec(rt["m_AnchorMin"]), Vec(rt["m_AnchorMax"]),
                    Vec(rt["m_SizeDelta"]), Vec(rt["m_AnchoredPosition"]), pivot);
                var scale = rt["m_LocalScale"].IsDummy ? (1.0, 1.0) : Vec(rt["m_LocalScale"]);
                map = UguiLayout.Affine.Local(rect, pivot, ZDegrees(rt["m_LocalRotation"]), scale).Then(toContainer);
            }
            var unit = UguiLayout.ToUnit(map.Bounds(rect), container);
            var orient = map.ToOrient();

            foreach (var slot in go["m_Component.Array"])
            {
                var ext = am.GetExtAsset(inst, slot["component"]);
                if (ext.info is null || ext.info.TypeId != (int)AssetClassID.MonoBehaviour) continue;
                var mb = ext.baseField;
                if (mb is null) continue;
                switch (ClassName(mb))
                {
                    case "Image": ReadImage(mb, unit, orient, ops); break;
                    case "RawImage": ops.Add(new NavModFill(unit, 0x000000FF)); break;   // a screen with nothing on it
                    case "TextMeshProUGUI": ReadText(mb, unit, orient, ops); break;
                }
            }

            // Unity draws a canvas depth-first in hierarchy order, parents under children, which is the order the
            // ops are appended in.
            foreach (var child in rt["m_Children.Array"])
            {
                var childRt = am.GetExtAsset(inst, child).baseField;
                if (childRt is null) continue;
                var childGo = am.GetExtAsset(inst, childRt["m_GameObject"]).baseField;
                if (childGo is null) continue;
                Walk(childGo, childRt, rect, map, container, ops, depth + 1);
            }
        }

        /// <summary>The turn about z a local rotation quaternion amounts to, in degrees. UI never turns about the
        /// other axes, and a quaternion that did would come out as whatever its z component says.</summary>
        private static double ZDegrees(AssetTypeValueField q)
        {
            if (q.IsDummy) return 0;
            double x = q["x"].AsFloat, y = q["y"].AsFloat, z = q["z"].AsFloat, w = q["w"].AsFloat;
            return Math.Atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z)) * 180 / Math.PI;
        }

        private void ReadImage(AssetTypeValueField mb, UnitRect unit, Orient orient, List<NavModOp> ops)
        {
            var tint = Rgba(mb["m_Color"]);
            var spriteField = mb["m_Sprite"];
            if (spriteField.IsDummy || spriteField["m_PathID"].AsLong == 0)
            {
                if ((tint & 0xFF) != 0) ops.Add(new NavModFill(unit, tint));
                return;
            }
            var id = SpriteId(spriteField);
            if (id < 0) return;
            // Image.Type: 0 Simple, 1 Sliced, 2 Tiled, 3 Filled. Tiled and Filled are stretched, which is what
            // they look like at rest on every module that uses them (a slider's fill, at zero).
            var multiplier = mb["m_PixelsPerUnitMultiplier"].IsDummy ? 1.0 : Math.Max(0.01, mb["m_PixelsPerUnitMultiplier"].AsFloat);
            var ppu = Math.Max(1.0, _sprites[id].PixelsPerUnit) * multiplier;
            ops.Add(new NavModSpriteOp(unit, id, tint,
                Sliced: mb["m_Type"].AsInt == 1,
                PreserveAspect: !mb["m_PreserveAspect"].IsDummy && mb["m_PreserveAspect"].AsBool,
                UnitsPerPixel: NavModScene.ReferencePixelsPerUnit / ppu) { Orient = orient });
        }

        private void ReadText(AssetTypeValueField mb, UnitRect unit, Orient orient, List<NavModOp> ops)
        {
            var text = mb["m_text"].AsString;
            if (string.IsNullOrWhiteSpace(text)) return;
            var style = mb["m_fontStyle"].IsDummy ? 0 : mb["m_fontStyle"].AsInt;
            // TMPro.FontStyles: Bold 1, Italic 2, Underline 4, LowerCase 8, UpperCase 16, SmallCaps 32.
            if ((style & 16) != 0) text = text.ToUpperInvariant();
            else if ((style & 8) != 0) text = text.ToLowerInvariant();

            var size = (double)mb["m_fontSize"].AsFloat;
            var auto = !mb["m_enableAutoSizing"].IsDummy && mb["m_enableAutoSizing"].AsBool;
            var min = mb["m_fontSizeMin"].IsDummy ? size : mb["m_fontSizeMin"].AsFloat;
            var max = mb["m_fontSizeMax"].IsDummy ? size : mb["m_fontSizeMax"].AsFloat;

            // TMP 3.x keeps the two axes apart; older versions packed both into m_textAlignment.
            int h, v;
            if (!mb["m_HorizontalAlignment"].IsDummy)
            {
                h = mb["m_HorizontalAlignment"].AsInt;
                v = mb["m_VerticalAlignment"].AsInt;
            }
            else
            {
                var packed = mb["m_textAlignment"].IsDummy ? 0x202 : mb["m_textAlignment"].AsInt;
                h = packed & 0xFF;
                v = packed & 0xFF00;
            }
            var horizontal = h switch { 1 => NavTextAlign.Start, 4 => NavTextAlign.End, _ => NavTextAlign.Middle };
            var vertical = v switch { 256 => NavTextAlign.Start, 1024 => NavTextAlign.End, _ => NavTextAlign.Middle };

            ops.Add(new NavModTextOp(unit, text, FontKey(mb["m_fontAsset"]), size, min, max, auto,
                Bold: (style & 1) != 0, Rgba(mb["m_fontColor"]), horizontal, vertical) { Orient = orient });
        }

        // ---- sprites and textures ----

        private int SpriteId(AssetTypeValueField pptr)
        {
            var key = (pptr["m_FileID"].AsInt, pptr["m_PathID"].AsLong);
            if (_spriteIds.TryGetValue(key, out var id)) return id;
            id = -1;
            try
            {
                var sprite = am.GetExtAsset(inst, pptr);
                if (sprite.baseField is { } sp && Texture(sprite.file, sp["m_RD.texture"]) is { } tex)
                {
                    var r = sp["m_RD.textureRect"];
                    var crop = Crop(tex, (int)r["x"].AsFloat, (int)r["y"].AsFloat, (int)r["width"].AsFloat, (int)r["height"].AsFloat);
                    if (crop is { } c)
                    {
                        var b = sp["m_Border"];
                        var border = new SpriteBorder((int)b["x"].AsFloat, (int)b["y"].AsFloat, (int)b["z"].AsFloat, (int)b["w"].AsFloat);
                        var ppu = sp["m_PixelsToUnits"].IsDummy ? 100.0 : sp["m_PixelsToUnits"].AsFloat;
                        id = _sprites.Count;
                        _sprites.Add(new NavModSprite(id, sp["m_Name"].AsString, c.Width, c.Height, c.Bgra, border, ppu));
                    }
                }
            }
            catch (Exception ex)
            {
                _notes.Add($"sprite {key.Item2}: {ex.GetType().Name}: {ex.Message}");
            }
            _spriteIds[key] = id;
            return id;
        }

        private DecodedTexture? Texture(AssetsFileInstance owner, AssetTypeValueField pptr)
        {
            var key = (owner.name, pptr["m_FileID"].AsInt, pptr["m_PathID"].AsLong);
            if (_textures.TryGetValue(key, out var hit)) return hit;
            DecodedTexture? decoded = null;
            try
            {
                var ext = am.GetExtAsset(owner, pptr);
                if (ext.baseField is { } bf)
                {
                    var tex = TextureFile.ReadTextureFile(bf);
                    // The pixels of a streamed texture live in the .resS beside the file the asset is in.
                    var raw = tex.FillPictureData(Path.GetDirectoryName(ext.file.path) ?? "");
                    // BGRA32, in Unity's own row order: the first row is the bottom of the image.
                    var bgra = tex.DecodeTextureRaw(raw, useBgra: true);
                    if (bgra is { Length: > 0 } && bgra.Length == tex.m_Width * tex.m_Height * 4)
                        decoded = new DecodedTexture(tex.m_Width, tex.m_Height, bgra);
                    else
                        _notes.Add($"texture {tex.m_Name}: format {tex.m_TextureFormat} did not decode");
                }
            }
            catch (Exception ex)
            {
                _notes.Add($"texture {key.Item3} in {key.Item1}: {ex.GetType().Name}: {ex.Message}");
            }
            _textures[key] = decoded;
            return decoded;
        }

        /// <summary>A sprite's rect is in texture pixels with y up from the bottom edge, which is also the order
        /// the decoded rows come in, so the crop reads rows <c>y + h - 1</c> down to <c>y</c> to come out
        /// top-down.</summary>
        private static DecodedTexture? Crop(DecodedTexture tex, int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) return null;
            x = Math.Clamp(x, 0, tex.Width); y = Math.Clamp(y, 0, tex.Height);
            w = Math.Min(w, tex.Width - x); h = Math.Min(h, tex.Height - y);
            if (w <= 0 || h <= 0) return null;
            var outBytes = new byte[w * h * 4];
            for (var row = 0; row < h; row++)
            {
                var src = y + h - 1 - row;
                Buffer.BlockCopy(tex.Bgra, (src * tex.Width + x) * 4, outBytes, row * w * 4, w * 4);
            }
            return new DecodedTexture(w, h, outBytes);
        }

        // ---- fonts ----

        /// <summary>The TrueType behind a TextMeshPro font asset. The asset itself is a signed-distance atlas the
        /// renderer cannot use, but the build also carries the source <c>Font</c> it was generated from, under the
        /// same name up to the " SDF" suffix ("Jura-Regular SDF Glow Blue" is "Jura-Regular").</summary>
        private string FontKey(AssetTypeValueField pptr)
        {
            var key = (pptr["m_FileID"].AsInt, pptr["m_PathID"].AsLong);
            if (_fontKeys.TryGetValue(key, out var hit)) return hit;
            var result = "";
            try
            {
                var asset = am.GetExtAsset(inst, pptr);
                if (asset.baseField is { } fa)
                {
                    var name = fa["m_Name"].AsString;
                    var at = name.IndexOf(" SDF", StringComparison.Ordinal);
                    var wanted = at > 0 ? name[..at] : name;
                    // The asset may still point at its source; when it does not, the Font of that name does.
                    var src = fa["m_SourceFontFile"];
                    if (!src.IsDummy && src["m_PathID"].AsLong != 0 && FontBytes(asset.file, src) is { } viaRef)
                        result = Remember(viaRef.Name, viaRef.Data);
                    else if (FindFont(wanted) is { } byName)
                        result = Remember(byName.Name, byName.Data);
                }
            }
            catch (Exception ex)
            {
                _notes.Add($"font {key.Item2}: {ex.GetType().Name}: {ex.Message}");
            }
            _fontKeys[key] = result;
            return result;
        }

        private string Remember(string name, byte[] data)
        {
            _fonts.TryAdd(name, data);
            return name;
        }

        private (string Name, byte[] Data)? FontBytes(AssetsFileInstance owner, AssetTypeValueField pptr)
        {
            var ext = am.GetExtAsset(owner, pptr, onlyGetInfo: true);
            return ext.info is { } info ? FontBytes(ext.file, info) : null;
        }

        /// <summary>
        /// A <c>Font</c>'s <c>m_FontData</c> is the TrueType file itself, as a byte vector. The class database
        /// types its element as a signed char, which the reader materialises one field per byte: eight million
        /// objects for the CJK face most labels use. Retyping the element as an unsigned byte before the read is
        /// what makes the reader pack it into one array instead, and it is the only reason the template is touched.
        /// </summary>
        private (string Name, byte[] Data)? FontBytes(AssetsFileInstance file, AssetFileInfo info)
        {
            var template = am.GetTemplateBaseField(file, info);
            var fontData = template.Children.FirstOrDefault(c => c.Name == "m_FontData");
            var array = fontData?.Children.FirstOrDefault(c => c.IsArray);
            if (array is { Children.Count: 2 })
            {
                array.ValueType = AssetValueType.ByteArray;
                array.Children[1].ValueType = AssetValueType.UInt8;
            }

            var font = template.MakeValue(file.file.Reader, info.GetAbsoluteByteOffset(file.file));
            var bytes = font["m_FontData.Array"];
            if (bytes.IsDummy) return null;
            var data = bytes.Value?.ValueType == AssetValueType.ByteArray
                ? bytes.AsByteArray
                : bytes.Children.Select(c => (byte)c.AsInt).ToArray();   // the slow road, should the retype not take
            return data is { Length: > 0 } ? (font["m_Name"].AsString, data) : null;
        }

        private Dictionary<string, (AssetsFileInstance File, AssetFileInfo Info)>? _fontsByName;

        /// <summary>The <c>Font</c> of a given name, from any file the build loaded. Names are read without
        /// deserialising the fonts, since a font is its whole TrueType file.</summary>
        private (string Name, byte[] Data)? FindFont(string name)
        {
            if (_fontsByName is null)
            {
                _fontsByName = new Dictionary<string, (AssetsFileInstance, AssetFileInfo)>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in am.Files)
                    foreach (var info in file.file.GetAssetsOfType(AssetClassID.Font))
                    {
                        var n = AssetHelper.GetAssetNameFast(file.file, am.ClassDatabase, info);
                        if (n is { Length: > 0 }) _fontsByName.TryAdd(n, (file, info));
                    }
            }
            if (!_fontsByName.TryGetValue(name, out var found)
                && !_fontsByName.TryGetValue(name.Replace("-", ""), out found)) return null;
            return FontBytes(found.File, found.Info);
        }

        // ---- helpers ----

        private AssetTypeValueField? FirstTransform(AssetTypeValueField go)
        {
            foreach (var slot in go["m_Component.Array"])
            {
                var ext = am.GetExtAsset(inst, slot["component"]);
                if (ext.info is { } i && (i.TypeId == (int)AssetClassID.RectTransform || i.TypeId == (int)AssetClassID.Transform))
                    return ext.baseField;
            }
            return null;
        }

        /// <summary>A script's class name, through its <c>MonoScript</c> (which lives in another file of the
        /// build, hence the cache).</summary>
        private string? ClassName(AssetTypeValueField mb)
        {
            var script = mb["m_Script"];
            if (script.IsDummy) return null;
            var key = (script["m_FileID"].AsInt, script["m_PathID"].AsLong);
            if (_classNames.TryGetValue(key, out var hit)) return hit;
            string? name = null;
            try { name = am.GetExtAsset(inst, script).baseField?["m_ClassName"].AsString; }
            catch { /* an unresolvable script is not a component this reader draws */ }
            _classNames[key] = name;
            return name;
        }

        private static (double X, double Y) Vec(AssetTypeValueField v) => (v["x"].AsFloat, v["y"].AsFloat);

        private static uint Rgba(AssetTypeValueField c)
        {
            static uint B(float f) => (uint)Math.Clamp((int)Math.Round(f * 255), 0, 255);
            return B(c["r"].AsFloat) << 24 | B(c["g"].AsFloat) << 16 | B(c["b"].AsFloat) << 8 | B(c["a"].AsFloat);
        }
    }
}

/// <summary>Everything <see cref="NavModArt.Build"/> read: one scene per module key, the sprites those scenes
/// index, and the TrueType data for the fonts their labels name. <see cref="Problem"/> is set instead when the
/// read could not proceed at all; <see cref="Notes"/> collects the pieces that were skipped on the way.</summary>
public sealed class NavModArtPack
{
    public IReadOnlyDictionary<string, NavModScene> Scenes { get; }
    public IReadOnlyList<NavModSprite> Sprites { get; }
    public IReadOnlyDictionary<string, byte[]> Fonts { get; }
    public string? UnityVersion { get; }
    public string? Problem { get; }
    public IReadOnlyList<string> Notes { get; }

    public bool Ok => Problem is null;

    internal NavModArtPack(IReadOnlyDictionary<string, NavModScene> scenes, IReadOnlyList<NavModSprite> sprites,
        IReadOnlyDictionary<string, byte[]> fonts, string unityVersion, IReadOnlyList<string> notes)
    {
        Scenes = scenes;
        Sprites = sprites;
        Fonts = fonts;
        UnityVersion = unityVersion;
        Notes = notes;
    }

    private NavModArtPack(string problem)
    {
        Scenes = new Dictionary<string, NavModScene>();
        Sprites = [];
        Fonts = new Dictionary<string, byte[]>();
        Problem = problem;
        Notes = [];
    }

    public static NavModArtPack Failed(string problem) => new(problem);

    public NavModScene? Scene(string key) => Scenes.GetValueOrDefault(key);
}

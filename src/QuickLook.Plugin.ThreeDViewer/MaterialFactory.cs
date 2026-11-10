using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Assimp;
using QuickLook.Common.Plugin;
using WpfMaterial = System.Windows.Media.Media3D.Material;
using AiMaterial = Assimp.Material;

namespace QuickLook.Plugin.ThreeDViewer
{
    /// <summary>
    /// Builds WPF 3D materials from Assimp materials.
    ///
    /// WPF's Viewport3D is a fixed-function renderer: it has no metalness/roughness/
    /// IBL/normal-map pipeline. We map glTF PBR data onto the closest approximation:
    ///   baseColor texture  -> DiffuseMaterial (ImageBrush)
    ///   baseColorFactor    -> DiffuseMaterial colour
    ///   metallicFactor     -> SpecularMaterial colour
    ///   roughnessFactor    -> SpecularPower
    ///   emissiveFactor     -> EmissiveMaterial
    /// Values ARE read from the real glTF PBR keys, so the preview reflects the
    /// material parameters instead of a flat grey (what HelixViewer does).
    ///
    /// Threading: every returned material/brush/geometry is frozen before leaving
    /// this class, so it can be built on a worker thread and attached on the UI
    /// thread. BitmapImage is created with CacheOption.OnLoad and frozen.
    /// </summary>
    internal sealed class MaterialFactory
    {
        private readonly Scene _scene;
        private readonly string _modelDirectory;
        private readonly Themes _theme;
        private readonly Dictionary<int, WpfMaterial> _cache = new Dictionary<int, WpfMaterial>();
        private readonly Dictionary<string, BitmapSource> _imageCache = new Dictionary<string, BitmapSource>();
        private bool _hasAnyTexture;

        internal bool HasAnyTexture { get { return _hasAnyTexture; } }

        internal MaterialFactory(Scene scene, string modelPath, Themes theme)
        {
            _scene = scene;
            _modelDirectory = Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? string.Empty;
            _theme = theme;
        }

        /// <summary>Returns a frozen WPF material, safe to use from any thread.</summary>
        internal WpfMaterial GetFrozen(int materialIndex)
        {
            WpfMaterial cached;
            if (_cache.TryGetValue(materialIndex, out cached)) return cached;

            WpfMaterial m = scene_material(materialIndex) == null
                ? CreateFallback()
                : Build(scene_material(materialIndex));
            Freeze(m);
            _cache[materialIndex] = m;
            return m;
        }

        private static void Freeze(WpfMaterial m)
        {
            var group = m as MaterialGroup;
            if (group != null)
            {
                foreach (var child in group.Children) Freeze(child);
            }
            var f = m as Freezable;
            if (f != null && f.CanFreeze) f.Freeze();
        }

        private AiMaterial scene_material(int index)
        {
            if (_scene == null || _scene.Materials == null || index < 0 || index >= _scene.Materials.Count)
                return null;
            return _scene.Materials[index];
        }

        private WpfMaterial CreateFallback()
        {
            return new DiffuseMaterial(FrozenBrush(Theme.ModelColor(_theme)));
        }

        private WpfMaterial Build(AiMaterial mat)
        {
            Color diffuse = Theme.ModelColor(_theme);
            if (mat.HasColorDiffuse)
            {
                Color4D c = mat.ColorDiffuse;
                diffuse = Color.FromScRgb(Clamp(c.A), Clamp(c.R), Clamp(c.G), Clamp(c.B));
            }

            Color baseColor = diffuse;
            MaterialProperty pbr = mat.GetNonTextureProperty("$mat.gltf.pbrMetallicRoughness.baseColorFactor");
            if (pbr != null && pbr.PropertyType == PropertyType.Float)
            {
                try
                {
                    float[] v = pbr.GetFloatArrayValue(4);
                    if (v != null && v.Length >= 4)
                        baseColor = Color.FromScRgb(Clamp(v[3]), Clamp(v[0]), Clamp(v[1]), Clamp(v[2]));
                }
                catch { }
            }

            float metallic = ReadFloat(mat, "$mat.gltf.pbrMetallicRoughness.metallicFactor", -1f);
            float roughness = ReadFloat(mat, "$mat.gltf.pbrMetallicRoughness.roughnessFactor", -1f);
            bool isPbr = metallic >= 0f || roughness >= 0f;
            if (roughness < 0f) roughness = 0.6f;
            if (metallic < 0f) metallic = 0f;

            BitmapSource texture = ResolveTexture(mat, TextureType.Diffuse, TextureType.BaseColor);
            Brush diffuseBrush;
            if (texture != null)
            {
                _hasAnyTexture = true;
                diffuseBrush = new ImageBrush(texture)
                {
                    TileMode = TileMode.Tile,
                    Stretch = Stretch.Fill,
                    ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                    Viewport = new Rect(0, 0, 1, 1),
                };
                if (isPbr && !IsWhite(baseColor)) diffuseBrush = MultiplyByColor(diffuseBrush, baseColor);
            }
            else diffuseBrush = new SolidColorBrush(baseColor);
            FreezeBrush(diffuseBrush);

            var group = new MaterialGroup();
            group.Children.Add(new DiffuseMaterial(diffuseBrush));

            float specPower = 12f + (1f - Clamp(roughness)) * 110f;
            Color specColor = isPbr
                ? Color.FromScRgb(1f, 0.35f + 0.65f * Clamp(metallic), 0.35f + 0.65f * Clamp(metallic), 0.35f + 0.65f * Clamp(metallic))
                : Color.FromScRgb(1f, 0.55f, 0.55f, 0.58f);
            var specBrush = FrozenBrush(specColor);
            group.Children.Add(new SpecularMaterial(specBrush, specPower));

            if (mat.HasColorEmissive)
            {
                Color4D e = mat.ColorEmissive;
                if (e.R > 0.001f || e.G > 0.001f || e.B > 0.001f)
                    group.Children.Add(new EmissiveMaterial(FrozenBrush(Color.FromScRgb(1f, Clamp(e.R), Clamp(e.G), Clamp(e.B)))));
            }
            BitmapSource emissiveTex = ResolveTexture(mat, TextureType.Emissive, TextureType.EmissionColor);
            if (emissiveTex != null)
            {
                _hasAnyTexture = true;
                var eb = new ImageBrush(emissiveTex);
                FreezeBrush(eb);
                group.Children.Add(new EmissiveMaterial(eb));
            }

            return group;
        }

        private static float ReadFloat(AiMaterial mat, string key, float fallback)
        {
            try
            {
                MaterialProperty p = mat.GetNonTextureProperty(key);
                if (p != null && p.PropertyType == PropertyType.Float) return p.GetFloatValue();
            }
            catch { }
            return fallback;
        }

        private BitmapSource ResolveTexture(AiMaterial mat, params TextureType[] types)
        {
            foreach (TextureType t in types)
            {
                try
                {
                    if (mat.GetMaterialTextureCount(t) <= 0) continue;
                    TextureSlot slot;
                    if (!mat.GetMaterialTexture(t, 0, out slot)) continue;
                    string path = slot.FilePath;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    BitmapSource bmp = LoadImage(path);
                    if (bmp != null) return bmp;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("ResolveTexture(" + t + ") failed: " + ex.Message);
                }
            }
            return null;
        }

        private BitmapSource LoadImage(string path)
        {
            BitmapSource cached;
            if (_imageCache.TryGetValue(path, out cached)) return cached;
            BitmapSource result = null;
            try
            {
                byte[] bytes = null;
                if (path.StartsWith("*"))
                {
                    int idx;
                    if (int.TryParse(path.Substring(1), out idx) &&
                        _scene != null && _scene.HasTextures && idx >= 0 && idx < _scene.Textures.Count)
                    {
                        EmbeddedTexture et = _scene.Textures[idx];
                        // AssimpNet 4.1.0 AND 5.0.0-beta1 both have a bug:
                        // HasCompressedData / HasNonCompressedData dereference a null
                        // array when the other representation is in use. Never read
                        // them. Use IsCompressed + the raw fields instead.
                        if (et.IsCompressed)
                        {
                            bytes = et.CompressedData;
                        }
                        else if (et.CompressedData != null && et.CompressedData.Length > 0)
                        {
                            bytes = et.CompressedData;
                        }
                        else if (et.NonCompressedData != null)
                        {
                            result = DecodeRaw(et);
                        }
                    }
                }
                else if (path.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = path.IndexOf(',');
                    if (comma > 0) bytes = Convert.FromBase64String(path.Substring(comma + 1));
                }
                else
                {
                    string file = Path.IsPathRooted(path) ? path : Path.Combine(_modelDirectory, path);
                    file = Path.GetFullPath(file);
                    if (File.Exists(file)) bytes = File.ReadAllBytes(file);
                    else Diagnostics.Log("External texture not found: " + file);
                }
                if (bytes != null) result = Decode(bytes);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("LoadImage('" + path + "') failed: " + ex.Message);
            }
            _imageCache[path] = result;
            return result;
        }

        private static BitmapSource Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = new MemoryStream(bytes);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        private static BitmapSource DecodeRaw(EmbeddedTexture et)
        {
            int w = et.Width, h = et.Height;
            if (w <= 0 || h <= 0 || et.NonCompressedData == null) return null;
            int count = Math.Min(w * h, et.NonCompressedData.Length);
            var pixels = new byte[count * 4];
            for (int i = 0; i < count; i++)
            {
                Texel t = et.NonCompressedData[i];
                pixels[i * 4 + 0] = t.B; pixels[i * 4 + 1] = t.G;
                pixels[i * 4 + 2] = t.R; pixels[i * 4 + 3] = t.A;
            }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            bmp.Freeze();
            return bmp;
        }

        private static Brush MultiplyByColor(Brush texture, Color tint)
        {
            var group = new DrawingGroup();
            using (DrawingContext dc = group.Open())
            {
                dc.DrawRectangle(texture, null, new Rect(0, 0, 1, 1));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(96, tint.R, tint.G, tint.B)), null, new Rect(0, 0, 1, 1));
            }
            return new DrawingBrush(group) { Viewport = new Rect(0, 0, 1, 1), TileMode = TileMode.Tile };
        }

        private static void FreezeBrush(Brush b)
        {
            if (b != null && b.CanFreeze) b.Freeze();
        }

        private static SolidColorBrush FrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static bool IsWhite(Color c) { return c.R >= 250 && c.G >= 250 && c.B >= 250 && c.A >= 250; }
        private static float Clamp(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Assimp;

namespace QuickLook.Plugin.ThreeDViewer
{
    /// <summary>Result of a single model import.</summary>
    internal sealed class LoadedModel : IDisposable
    {
        internal Scene Scene;
        internal string SourcePath;
        internal string Extension;
        internal bool Degraded;
        internal long FaceCount;
        internal long SampledFaceCount;
        internal string Notice;
        internal TimeSpan ImportTime;

        public void Dispose()
        {
            Scene = null;
        }
    }

    /// <summary>
    /// Thin wrapper around AssimpNet 5.0.0-beta1.
    ///
    /// Why 5.0.0-beta1 rather than 4.1.0 (verified on this machine):
    ///   * AssimpNet 4.1.0's glTF2 importer returns MeshCount=0 for valid GLB/GLTF
    ///     files (Blender 5.2 exports). It cannot deliver the required PBR/animation.
    ///   * 5.0.0-beta1 imports them correctly: meshes, embedded textures,
    ///     $mat.gltf.pbrMetallicRoughness.* keys and node animation channels.
    ///   * QuickLook 4.2.2's own built-in HelixViewer ships exactly this same
    ///     AssimpNet 5.0.0.0 binary (byte-identical), so it is the host's own choice.
    ///
    /// Post-processing: ONLY Triangulate | FlipUVs | GlobalScale.
    ///   * GenerateNormals / GenerateSmoothNormals / JoinIdenticalVertices /
    ///     ImproveCacheLocality / FindDegenerates / FindInvalidData crash
    ///     (AccessViolationException inside assimp.dll) on some X3D scenes.
    ///     Verified flag-by-flag; the minimal safe set imports all 11 formats.
    ///   * Normals are computed in managed code when the source lacks them, which
    ///     avoids the native post-processor entirely.
    /// </summary>
    internal static class ModelLoader
    {
        /// <summary>Faces above this threshold switch to a downsampled/wireframe view.</summary>
        internal const long HeavyFaceThreshold = 500000;

        /// <summary>Cap on faces actually handed to the renderer in degraded mode.</summary>
        internal const int MaxRenderedFaces = 120000;

        internal const PostProcessSteps SafePostProcess =
            PostProcessSteps.Triangulate | PostProcessSteps.FlipUVs | PostProcessSteps.GlobalScale;

        private static readonly string[] SupportedExtensions =
        {
            ".glb", ".gltf", ".fbx", ".obj", ".stl", ".ply",
            ".3ds", ".dae", ".off", ".x3d", ".3mf"
        };

        internal static IReadOnlyList<string> Extensions { get { return SupportedExtensions; } }

        internal static bool IsSupported(string path)
        {
            if (string.IsNullOrEmpty(path) || Directory.Exists(path)) return false;
            string ext;
            try { ext = Path.GetExtension(path); }
            catch { return false; }
            if (string.IsNullOrEmpty(ext)) return false;
            foreach (string e in SupportedExtensions)
                if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static LoadedModel Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Model file not found", path);

            NativeLibrary.EnsureInitialized();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Scene scene;
            using (var context = new AssimpContext())
            {
                scene = context.ImportFile(path, SafePostProcess);
            }
            sw.Stop();

            if (scene == null || !scene.HasMeshes)
                throw new InvalidDataException("Assimp 未能从该文件中读取到任何网格： " + Path.GetFileName(path));

            long faces = 0;
            foreach (Mesh m in scene.Meshes) faces += m.FaceCount;

            var result = new LoadedModel
            {
                Scene = scene,
                SourcePath = path,
                Extension = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant(),
                FaceCount = faces,
                ImportTime = sw.Elapsed,
                Degraded = faces > HeavyFaceThreshold,
            };

            if (result.Degraded)
            {
                // Uniform triangle sampling across the whole scene (not a wireframe):
                // WPF 3D has no cheap screen-space line primitive, and building
                // 600k line segments would cost more than the shaded mesh itself.
                // The budget is an upper bound; the exact count is reported by the
                // viewer after geometry is built.
                result.SampledFaceCount = Math.Min(faces, MaxRenderedFaces);
                result.Notice = string.Format(
                    "模型共 {0:N0} 个面，已超过 50 万面阈值；已自动降级为均匀抽样预览（最多显示 {1:N0} 个面）。",
                    faces, result.SampledFaceCount);
            }

            Diagnostics.Log(string.Format(
                "Loaded {0}: {1:N0} faces in {2}ms, meshes={3}, textures={4}, animations={5}, degraded={6}",
                Path.GetFileName(path), faces, sw.ElapsedMilliseconds, scene.MeshCount,
                scene.TextureCount, scene.AnimationCount, result.Degraded));
            return result;
        }
    }
}

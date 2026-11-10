using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Assimp;
using QuickLook.Common.Plugin;
using AiVector3D = Assimp.Vector3D;
using WpfVector3D = System.Windows.Media.Media3D.Vector3D;
using WpfMaterial = System.Windows.Media.Media3D.Material;

namespace QuickLook.Plugin.ThreeDViewer
{
    /// <summary>A single node's frozen content, produced on a worker thread.</summary>
    internal sealed class NodeDesc
    {
        internal string Name;
        internal Matrix3D LocalTransform;
        internal Model3DGroup FrozenContent;
        internal List<NodeDesc> Children = new List<NodeDesc>();
    }

    /// <summary>Everything the viewer/animator needs from a built scene.</summary>
    internal sealed class BuiltScene
    {
        internal NodeDesc RootNode;
        internal Model3D FrozenLights;
        internal Rect3D Bounds;
        internal long RenderedFaceCount;
        internal bool Degraded;
        internal bool HasTextures;
        internal bool HasAnimations;
        internal int MeshCount;

        // Filled in on the UI thread by Attach().
        internal ModelVisual3D Root;
        internal Dictionary<string, ModelVisual3D> Nodes = new Dictionary<string, ModelVisual3D>(StringComparer.Ordinal);
        internal Dictionary<string, Matrix3D> RestTransforms = new Dictionary<string, Matrix3D>(StringComparer.Ordinal);

        /// <summary>
        /// Builds the ModelVisual3D tree on the UI thread from the frozen worker
        /// data. ModelVisual3D is NOT a Freezable, so it cannot be created on the
        /// worker; everything else is already frozen and safe to share.
        /// </summary>
        internal void Attach()
        {
            var rootGroup = new Model3DGroup();
            Root = new ModelVisual3D { Content = rootGroup };
            if (FrozenLights != null) rootGroup.Children.Add(FrozenLights);
            if (RootNode != null)
            {
                // Root node content becomes a child of the shared root group; nested
                // nodes become ModelVisual3D children so their transforms can animate.
                foreach (Model3D m in RootNode.FrozenContent.Children)
                    rootGroup.Children.Add(m);
                foreach (NodeDesc child in RootNode.Children)
                    AddVisual(child, Root);
                Register(RootNode.Name, Root, RootNode.LocalTransform);
            }
        }

        private void AddVisual(NodeDesc desc, ModelVisual3D parent)
        {
            var visual = new ModelVisual3D
            {
                Content = desc.FrozenContent,
                Transform = new MatrixTransform3D(desc.LocalTransform),
            };
            parent.Children.Add(visual);
            Register(desc.Name, visual, desc.LocalTransform);
            foreach (NodeDesc child in desc.Children)
                AddVisual(child, visual);
        }

        private void Register(string name, ModelVisual3D visual, Matrix3D rest)
        {
            if (string.IsNullOrEmpty(name) || Nodes.ContainsKey(name)) return;
            Nodes[name] = visual;
            RestTransforms[name] = rest;
        }
    }

    /// <summary>
    /// Converts an Assimp Scene into frozen WPF 3D content on a worker thread.
    /// All constructed WPF objects are Freezable and are explicitly frozen, which
    /// makes them legal to attach to a UI-thread visual tree later. The only
    /// exceptions (ModelVisual3D / MatrixTransform3D) are created in BuiltScene.Attach().
    /// </summary>
    internal static class SceneBuilder
    {
        internal static BuiltScene Build(LoadedModel model, Themes theme)
        {
            Scene scene = model.Scene;
            var result = new BuiltScene { Degraded = model.Degraded, MeshCount = scene.MeshCount };
            var factory = new MaterialFactory(scene, model.SourcePath, theme);

            long stride = 1;
            if (model.Degraded && model.FaceCount > 0)
                stride = Math.Max(1L, (long)Math.Ceiling((double)model.FaceCount / ModelLoader.MaxRenderedFaces));
            var counter = new long[1];

            NodeDesc root = scene.RootNode != null
                ? BuildNode(scene.RootNode, scene, factory, theme, stride, counter, result)
                : new NodeDesc { Name = "root", LocalTransform = Matrix3D.Identity, FrozenContent = new Model3DGroup() };
            result.RootNode = root;

            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Theme.IsDark(theme)
                ? Color.FromRgb(0x50, 0x50, 0x58)
                : Color.FromRgb(0x70, 0x70, 0x78)));
            lights.Children.Add(new DirectionalLight(Colors.White, new WpfVector3D(-0.45, -0.6, -0.65)));
            lights.Children.Add(new DirectionalLight(Theme.IsDark(theme)
                ? Color.FromRgb(0x88, 0x9A, 0xC0)
                : Color.FromRgb(0xB8, 0xC4, 0xDC), new WpfVector3D(0.5, 0.25, 0.8)));
            lights.Freeze();
            result.FrozenLights = lights;

            result.HasTextures = factory.HasAnyTexture;
            result.HasAnimations = scene.HasAnimations && scene.AnimationCount > 0;
            result.RenderedFaceCount = CountRenderedTriangles(root);
            result.Bounds = ComputeBounds(root);
            if (result.Bounds.IsEmpty) result.Bounds = new Rect3D(-1, -1, -1, 2, 2, 2);

            return result;
        }

        private static NodeDesc BuildNode(Node node, Scene scene, MaterialFactory factory, Themes theme,
            long stride, long[] counter, BuiltScene result)
        {
            var desc = new NodeDesc
            {
                Name = node.Name ?? string.Empty,
                LocalTransform = ToWpfMatrix(node.Transform),
                FrozenContent = new Model3DGroup(),
            };

            foreach (int meshIndex in node.MeshIndices)
            {
                if (meshIndex < 0 || meshIndex >= scene.Meshes.Count) continue;
                GeometryModel3D gm = BuildMeshGeometry(scene.Meshes[meshIndex], factory, stride, counter);
                if (gm != null) desc.FrozenContent.Children.Add(gm);
            }
            foreach (Node child in node.Children)
                desc.Children.Add(BuildNode(child, scene, factory, theme, stride, counter, result));

            // Frozen content can be shared across threads; Transform is added later
            // on the UI thread (a mutable MatrixTransform3D per visual).
            desc.FrozenContent.Freeze();
            return desc;
        }

        private static GeometryModel3D BuildMeshGeometry(Mesh mesh, MaterialFactory factory, long stride, long[] counter)
        {
            if (!mesh.HasVertices || !mesh.HasFaces || mesh.FaceCount == 0) return null;

            var positions = new Point3DCollection(mesh.VertexCount);
            foreach (AiVector3D v in mesh.Vertices)
                positions.Add(new Point3D(v.X, v.Y, v.Z));

            var indices = new Int32Collection();
            foreach (Face f in mesh.Faces)
            {
                if (f.IndexCount < 3) continue;
                // Fan-triangulate n-gons, then take every stride'th triangle
                // *globally* across the whole scene so the rendered count is
                // bounded even when each Face is a single triangle (STL/PLY).
                for (int i = 1; i < f.IndexCount - 1; i++)
                {
                    if ((counter[0]++ % stride) != 0) continue;
                    indices.Add(f.Indices[0]);
                    indices.Add(f.Indices[i]);
                    indices.Add(f.Indices[i + 1]);
                }
            }
            if (indices.Count == 0) return null;

            var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };

            if (mesh.HasNormals && mesh.Normals.Count >= mesh.VertexCount)
            {
                var normals = new Vector3DCollection(mesh.VertexCount);
                for (int i = 0; i < mesh.VertexCount; i++)
                {
                    AiVector3D n = mesh.Normals[i];
                    normals.Add(new WpfVector3D(n.X, n.Y, n.Z));
                }
                geometry.Normals = normals;
            }

            if (mesh.HasTextureCoords(0))
            {
                List<AiVector3D> uv = mesh.TextureCoordinateChannels[0];
                if (uv != null && uv.Count >= mesh.VertexCount)
                {
                    var coords = new PointCollection(mesh.VertexCount);
                    for (int i = 0; i < mesh.VertexCount; i++)
                        coords.Add(new Point(uv[i].X, uv[i].Y));
                    geometry.TextureCoordinates = coords;
                }
            }

            geometry.Freeze();
            WpfMaterial material = factory.GetFrozen(mesh.MaterialIndex);
            var gm = new GeometryModel3D(geometry, material) { BackMaterial = material };
            gm.Freeze();
            return gm;
        }

        /// <summary>
        /// Assimp is column-vector (p' = M*p), WPF is row-vector (p' = p*M), so the
        /// WPF matrix is the transpose of the Assimp matrix.
        /// </summary>
        internal static Matrix3D ToWpfMatrix(Matrix4x4 m)
        {
            return new Matrix3D(
                m.A1, m.B1, m.C1, m.D1,
                m.A2, m.B2, m.C2, m.D2,
                m.A3, m.B3, m.C3, m.D3,
                m.A4, m.B4, m.C4, m.D4);
        }

        private static long CountRenderedTriangles(NodeDesc node)
        {
            long n = 0;
            if (node == null) return n;
            foreach (var m in node.FrozenContent.Children)
            {
                var gm = m as GeometryModel3D;
                var g = gm == null ? null : gm.Geometry as MeshGeometry3D;
                if (g != null && g.TriangleIndices != null) n += g.TriangleIndices.Count / 3;
            }
            foreach (NodeDesc c in node.Children) n += CountRenderedTriangles(c);
            return n;
        }

        private static Rect3D ComputeBounds(NodeDesc node)
        {
            Rect3D bounds = Rect3D.Empty;
            if (node == null) return bounds;
            foreach (var m in node.FrozenContent.Children)
            {
                var gm = m as GeometryModel3D;
                if (gm != null && gm.Geometry is MeshGeometry3D)
                {
                    Rect3D b = gm.Geometry.Bounds;
                    if (!b.IsEmpty) bounds.Union(b);
                }
            }
            foreach (NodeDesc c in node.Children)
            {
                Rect3D b = ComputeBounds(c);
                if (!b.IsEmpty) bounds.Union(b);
            }
            return bounds;
        }
    }
}

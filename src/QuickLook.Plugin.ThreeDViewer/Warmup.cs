using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;

namespace QuickLook.Plugin.ThreeDViewer
{
    /// <summary>
    /// Moves one-time WPF / HelixToolkit / AssimpNet JIT and assembly-load cost out
    /// of the first preview's critical path.
    ///
    /// Measured on this machine without warmup: first View() of a small GLB took
    /// 290-390ms total, of which the actual Assimp import was only ~33ms; the rest
    /// was first-use loading/JIT of PresentationCore, HelixToolkit.Wpf and
    /// HelixViewport3D's default template. QuickLook calls Init() at startup on the
    /// UI thread, so that is the right place to pay the cost once.
    ///
    /// IMPORTANT: never build a Scene in managed code and call ExportToBlob() here.
    /// Assimp 5.0.0-beta1 crashes (AccessViolationException) when exporting a
    /// hand-constructed Scene. We only IMPORT a real on-disk file.
    /// </summary>
    internal static class Warmup
    {
        private static readonly object Gate = new object();
        private static bool _done;

        internal static void Run()
        {
            lock (Gate)
            {
                if (_done) return;
                _done = true;
                try
                {
                    // 1) Touch WPF 3D types so their assemblies load/JIT once.
                    var g = new MeshGeometry3D();
                    g.Positions.Add(new Point3D(0, 0, 0));
                    g.TriangleIndices.Add(0);
                    var geometry = new GeometryModel3D(g, new DiffuseMaterial(Brushes.Gray));
                    var camera = new PerspectiveCamera { Position = new Point3D(1, 1, 1) };
                    var light = new AmbientLight(Colors.White);
                    var group = new Model3DGroup();
                    group.Children.Add(light);
                    group.Children.Add(geometry);
                    var visual = new ModelVisual3D { Content = group };

                    // 2) Construct a throwaway HelixViewport3D and run a FULL layout
                    //    pass. A bare Measure/Arrange is not enough: the first real
                    //    ViewerPanel costs ~2s because the HelixViewport3D template,
                    //    CameraController, ViewCube and WPF imaging stack initialize
                    //    lazily when the control is connected to a presentation
                    //    source. Hosting it in a real Dispatcher-driven window during
                    //    Init() forces that one-time cost out of the first preview.
                    var vp = new HelixViewport3D { Width = 1, Height = 1, Camera = camera };
                    vp.Children.Add(visual);
                    var host = new System.Windows.Controls.Grid();
                    host.Children.Add(vp);
                    var probe = new ViewerPanelProbe(host);
                    probe.Pump();
                    vp.Children.Clear();

                    // 3) Import a tiny real model so AssimpNet's marshalling and
                    //    Scene/Mesh/Material paths are JITted. Prefer the bundled
                    //    warmup.glb; fall back to any small model next to the plugin.
                    string dir = NativeLibrary.PluginDirectory;
                    string glb = Path.Combine(dir, "warmup.glb");
                    if (!File.Exists(glb))
                    {
                        foreach (string f in Directory.GetFiles(dir, "*.glb"))
                        {
                            if (new FileInfo(f).Length < 256 * 1024) { glb = f; break; }
                        }
                    }
                    if (File.Exists(glb))
                    {
                        using (var ctx = new Assimp.AssimpContext())
                        {
                            var scene = ctx.ImportFile(glb, ModelLoader.SafePostProcess);
                            Diagnostics.Log("Warmup import OK: " + Path.GetFileName(glb) +
                                            " meshes=" + (scene == null ? -1 : scene.MeshCount));
                        }
                    }
                    Diagnostics.Log("Warmup done");
                }
                catch (Exception ex)
                {
                    // Warmup is best-effort; a failure here must not break the plugin.
                    Diagnostics.Log("Warmup (best-effort) failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Runs a real WPF layout + render pass using an offscreen HwndSource.
        /// A plain Measure/Arrange on an unhosted control is NOT enough: HelixViewport3D's
        /// template (and thus CameraController / ViewCube / imaging stack) is only
        /// loaded when the control is connected to a PresentationSource. We attach it
        /// to a 1x1 invisible HwndSource instead of showing a Window, so this can
        /// never steal focus or flash during QuickLook startup.
        /// </summary>
        private sealed class ViewerPanelProbe
        {
            private readonly System.Windows.Controls.Grid _content;

            internal ViewerPanelProbe(System.Windows.Controls.Grid content) { _content = content; }

            internal void Pump()
            {
                var parameters = new System.Windows.Interop.HwndSourceParameters("QL3DWarmup")
                {
                    Width = 1,
                    Height = 1,
                    PositionX = -10000,
                    PositionY = -10000,
                    WindowStyle = 0,
                    ExtendedWindowStyle = 0x08000000, // WS_EX_NOACTIVATE
                    TreatAsInputRoot = false,
                };
                using (var source = new System.Windows.Interop.HwndSource(parameters))
                {
                    source.RootVisual = _content;
                    _content.Measure(new Size(1, 1));
                    _content.Arrange(new Rect(0, 0, 1, 1));
                    _content.UpdateLayout();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                        new Action(() => { }),
                        System.Windows.Threading.DispatcherPriority.Render);
                    source.RootVisual = null;
                }
            }
        }
    }
}

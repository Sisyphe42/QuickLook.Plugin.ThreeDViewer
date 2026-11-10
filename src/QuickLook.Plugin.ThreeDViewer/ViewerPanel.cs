using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using QuickLook.Common.Plugin;

namespace QuickLook.Plugin.ThreeDViewer
{
    /// <summary>
    /// The WPF UserControl returned via ContextObject.ViewerContent.
    ///
    /// Design: ViewerContent must be set synchronously inside Plugin.View() (the host
    /// calls it on the Dispatcher at Input priority and shows a busy overlay until
    /// IsBusy=false). So we create the HelixViewport3D + a themed background +
    /// placeholder immediately, then build the model on a background thread and
    /// attach the resulting ModelVisual3D on the UI thread. That yields a real
    /// first frame in a few milliseconds even for large files.
    /// </summary>
    internal sealed class ViewerPanel : UserControl, IDisposable
    {
        private readonly Themes _theme;
        private readonly Grid _root;
        private readonly HelixViewport3D _viewport;
        private readonly TextBlock _status;
        private readonly Border _notice;
        private readonly TextBlock _noticeText;
        private AnimationPlayer _animator;
        private bool _disposed;

        /// <summary>
        /// Synchronous constructor: creates only the themed shell + placeholder.
        /// Model import and geometry building run on the thread pool (see Start).
        /// </summary>
        internal ViewerPanel(Themes theme)
        {
            _theme = theme;
            Background = new SolidColorBrush(Theme.Background(theme));
            Foreground = new SolidColorBrush(Theme.Foreground(theme));

            _root = new Grid();
            Content = _root;

            _viewport = CreateViewport(theme);
            _root.Children.Add(_viewport);

            _status = new TextBlock
            {
                Margin = new Thickness(14, 0, 14, 10),
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = 12,
                Foreground = new SolidColorBrush(Theme.Foreground(theme)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = "正在构建预览…",
                IsHitTestVisible = false,
            };
            _root.Children.Add(_status);

            _noticeText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = new SolidColorBrush(Theme.Foreground(theme)),
            };
            _notice = new Border
            {
                Margin = new Thickness(14, 12, 14, 0),
                Padding = new Thickness(10, 6, 10, 6),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Theme.IsDark(theme)
                    ? Color.FromArgb(210, 0x7A, 0x52, 0x0A)
                    : Color.FromArgb(220, 0xFF, 0xE7, 0xB0)),
                Child = _noticeText,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            _root.Children.Add(_notice);

            Unloaded += OnUnloaded;
        }

        /// <summary>Kicks off asynchronous import + build for the given file.</summary>
        internal void Start(string path)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ => LoadAndBuild(path));
        }

        private void LoadAndBuild(string path)
        {
            LoadedModel model = null;
            BuiltScene built = null;
            Exception error = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                NativeLibrary.EnsureInitialized();
                model = ModelLoader.Load(path);
                built = SceneBuilder.Build(model, _theme);
            }
            catch (Exception ex)
            {
                error = ex;
                Diagnostics.LogException("LoadAndBuild", ex);
            }
            sw.Stop();
            LoadedModel m = model;
            BuiltScene b = built;
            Exception err = error;
            long ms = sw.ElapsedMilliseconds;
            try
            {
                Dispatcher.BeginInvoke(new Action(() => Apply(b, err, m, ms)),
                    System.Windows.Threading.DispatcherPriority.Send);
            }
            catch { }
        }

        /// <summary>
        /// Used by Warmup: builds a tiny already-loaded model synchronously, forcing
        /// the geometry + material code paths to JIT without touching the UI shell.
        /// </summary>
        internal static void WarmBuild(LoadedModel model, Themes theme)
        {
            try { var b = SceneBuilder.Build(model, theme); if (b != null) b.Root = null; }
            catch (Exception ex) { Diagnostics.Log("WarmBuild: " + ex.Message); }
        }

        private HelixViewport3D CreateViewport(Themes theme)
        {
            var foreground = new SolidColorBrush(Theme.Foreground(theme));
            var vp = new HelixViewport3D
            {
                Background = new SolidColorBrush(Theme.Background(theme)),
                ShowCoordinateSystem = false,
                ShowViewCube = true,
                ShowCameraInfo = false,
                ShowFrameRate = false,
                IsRotationEnabled = true,
                IsPanEnabled = true,
                IsZoomEnabled = true,
                ZoomExtentsWhenLoaded = false,
                IsInertiaEnabled = true,
                RotationSensitivity = 0.9,
                ZoomSensitivity = 0.9,
                ClipToBounds = true,
                // HelixToolkit's DEFAULT rotate gesture is RIGHT-click, not left.
                // The old HelixViewer overrode this in XAML; we must do it in code,
                // otherwise "mouse rotate" would be right-drag only.
                RotateGesture = new MouseGesture(MouseAction.LeftClick),
                PanGesture = new MouseGesture(MouseAction.RightClick),
                PanGesture2 = new MouseGesture(MouseAction.MiddleClick),
                // Assimp / glTF are Y-up; HelixToolkit's default is Z-up. Without
                // this, models appear lying on their side.
                ModelUpDirection = new Vector3D(0, 1, 0),
                // ViewCube / info text defaults are black-on-white; make them
                // legible in dark theme too.
                TextBrush = foreground,
                InfoForeground = foreground,
                CoordinateSystemLabelForeground = foreground,
            };
            var perspective = new PerspectiveCamera
            {
                Position = new Point3D(4, 3, 5),
                LookDirection = new Vector3D(-4, -3, -5),
                UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = 45,
                NearPlaneDistance = 0.01,
                FarPlaneDistance = 10000,
            };
            vp.Camera = perspective;
            return vp;
        }

        private void Apply(BuiltScene built, Exception error, LoadedModel model, long buildMs)
        {
            if (_disposed) { if (built != null) DisposeBuilt(built); return; }

            if (error != null || built == null)
            {
                _status.Text = "预览失败：" + (error == null ? "未知错误" : error.Message);
                _status.Foreground = new SolidColorBrush(Theme.Accent(_theme));
                return;
            }
            if (model == null) return;

            try
            {
                built.Attach();
                _viewport.Children.Add(built.Root);
                FitCamera(built.Bounds);
                _animator = new AnimationPlayer(built, model.Scene, this);

                _status.Text = string.Format("{0:N0} 面 · {1} · 左键旋转 / 右键平移 / 滚轮缩放",
                    built.RenderedFaceCount, _animator.Label);
                if (built.HasTextures) _status.Text += " · 贴图";
                if (built.Degraded)
                {
                    _noticeText.Text = model.Notice ?? string.Empty;
                    _notice.Visibility = Visibility.Visible;
                }
                Diagnostics.Log("First frame ready, build " + buildMs + "ms, faces " + built.RenderedFaceCount);
            }
            catch (Exception ex)
            {
                Diagnostics.LogException("ViewerPanel.Apply", ex);
                _status.Text = "预览失败：" + ex.Message;
            }
        }

        private void FitCamera(Rect3D bounds)
        {
            if (bounds.IsEmpty) { _viewport.ZoomExtents(); return; }
            Point3D center = new Point3D(
                bounds.X + bounds.SizeX / 2,
                bounds.Y + bounds.SizeY / 2,
                bounds.Z + bounds.SizeZ / 2);
            double radius = Math.Max(Math.Max(bounds.SizeX, bounds.SizeY), bounds.SizeZ) / 2.0;
            if (radius < 1e-6) radius = 1.0;
            double distance = radius / Math.Tan(45.0 * Math.PI / 360.0) * 1.7;

            var cam = _viewport.Camera as PerspectiveCamera;
            if (cam == null) { _viewport.ZoomExtents(); return; }
            var dir = new Vector3D(-0.6, -0.45, -0.75);
            dir.Normalize();
            cam.Position = center - dir * distance;
            cam.LookDirection = dir;
            cam.UpDirection = new Vector3D(0, 1, 0);
            cam.NearPlaneDistance = Math.Max(distance / 1000.0, 0.001);
            cam.FarPlaneDistance = distance * 100.0;
            _viewport.Camera = cam;
            _viewport.ZoomExtents(0.9);
        }

        private static void DisposeBuilt(BuiltScene built)
        {
            // Nothing unmanaged to release; just drop references so GC can collect.
            if (built != null) built.Root = null;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_animator != null) _animator.Dispose();
                _animator = null;
                _viewport.Children.Clear();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("ViewerPanel.Dispose: " + ex.Message);
            }
        }
    }
}

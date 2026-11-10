using System;
using System.IO;
using System.Windows;
using QuickLook.Common.Plugin;

namespace QuickLook.Plugin.ThreeDViewer
{
    /// <summary>
    /// QuickLook IViewer entry point.
    ///
    /// Namespace MUST equal &lt;Namespace&gt; in QuickLook.Plugin.Metadata.config,
    /// because QuickLook.Plugin.PluginInstaller uses that value as the installation
    /// directory name and validates the "QuickLook.Plugin." prefix.
    ///
    /// Priority = int.MaxValue is required: QuickLook 4.2.2 ships a built-in
    /// HelixViewer (Priority=-5) covering 12 3D extensions, and the user folder has
    /// an older HelixViewer (Priority=int.MaxValue) covering .obj/.stl/.ply/.3ds.
    /// With the same priority, LINQ's stable OrderByDescending keeps the load order,
    /// and "HelixViewer" sorts before "ThreeDViewer" in Directory.GetFiles, so the
    /// old plugin would win unless its whole directory is moved out of the scan tree
    /// (to UserData\DisabledPlugins\). The deployment script does that.
    /// </summary>
    public sealed class Plugin : IViewer
    {
        public int Priority { get { return int.MaxValue; } }

        public void Init()
        {
            // Note: QuickLook calls Init() on the instance it loaded at startup, but
            // FindMatch() then creates a NEW instance for each preview. Hence all
            // native initialization must be idempotent and guarded by a static flag
            // (NativeLibrary.EnsureInitialized), NOT by instance state.
            try
            {
                NativeLibrary.EnsureInitialized();
                Warmup.Run();
                Diagnostics.Log("Init() OK, plugin dir=" + NativeLibrary.PluginDirectory);
            }
            catch (Exception ex)
            {
                // Swallow here: CanHandle/Prepare/View must remain functional so the
                // host can show a friendly error instead of a failed-plugin dialog.
                Diagnostics.LogException("Init()", ex);
            }
        }

        /// <summary>
        /// Explicit whitelist, matching Assimp's *verified* import set.
        /// step / iges / usdz are deliberately absent: Assimp's IFC importer only
        /// claims "stp" (not step/iges), and USDZ is not an Assimp import format.
        /// </summary>
        public bool CanHandle(string path)
        {
            return ModelLoader.IsSupported(path);
        }

        public void Prepare(string path, ContextObject context)
        {
            if (context == null) return;
            // Square viewport suits 3D. Fit to at most 70% of the desktop.
            context.PreferredSize = new Size(820, 720);
            context.CanResize = true;
            context.TitlebarOverlap = false;
            context.FullWindowDragging = false; // keep mouse events for HelixViewport3D
            // Explicitly tell the host which theme we rendered for. The host's
            // ContextObject_PropertyChanged then calls SwitchTheme(), so the title
            // bar matches the viewer background.
            context.Theme = Theme.Resolve(context);
        }

        public void View(string path, ContextObject context)
        {
            if (context == null) return;
            Themes theme;
            try { theme = Theme.Resolve(context); }
            catch { theme = Themes.Light; }

            try
            {
                NativeLibrary.EnsureInitialized();
                var panel = new ViewerPanel(theme);
                context.ViewerContent = panel;
                // Import + geometry building happen off the UI thread. ViewerContent
                // is already set and IsBusy cleared, so the themed viewport appears
                // immediately even for a 600k-face model.
                panel.Start(path);
                context.Title = Path.GetFileName(path);
                context.IsBusy = false;
                context.Theme = theme;
                Diagnostics.Log("View(" + Path.GetFileName(path) + ") first frame set");
            }
            catch (Exception ex)
            {
                Diagnostics.LogException("View(" + path + ")", ex);
                // Never throw out of View(): the host catches it but shows an ugly
                // generic failure. Return a readable error panel instead.
                context.ViewerContent = CreateErrorPanel(ex, theme);
                context.Title = Path.GetFileName(path);
                context.IsBusy = false;
                context.Theme = theme;
            }
        }

        private static System.Windows.Controls.UserControl CreateErrorPanel(Exception ex, Themes theme)
        {
            var tb = new System.Windows.Controls.TextBlock
            {
                Text = "无法预览此 3D 模型。\n\n" + ex.Message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24),
                FontSize = 14,
                Foreground = new System.Windows.Media.SolidColorBrush(Theme.Foreground(theme)),
            };
            return new System.Windows.Controls.UserControl
            {
                Background = new System.Windows.Media.SolidColorBrush(Theme.Background(theme)),
                Content = tb,
            };
        }

        public void Cleanup()
        {
            // ViewerPanel disposes itself on Unloaded. Nothing process-global here:
            // the native assimp.dll stays loaded for the lifetime of QuickLook, which
            // is intentional (reloading it per preview would be slow and risky).
            GC.SuppressFinalize(this);
        }
    }
}

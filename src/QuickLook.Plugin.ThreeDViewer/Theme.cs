using System;
using System.Windows.Media;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;

namespace QuickLook.Plugin.ThreeDViewer
{
    /// <summary>
    /// Resolves the effective preview theme.
    ///
    /// Important (verified against QuickLook 4.2.2): ContextObject.Theme is reset to
    /// Themes.None by ViewerWindow.UnloadPlugin() and is NOT set by the host before
    /// Plugin.Prepare() / Plugin.View() are called. The host only reacts when a plugin
    /// *sets* ctx.Theme (ContextObject_PropertyChanged -> SwitchTheme). So the plugin
    /// must resolve the OS theme itself via OSThemeHelper.AppsUseDarkTheme(), which is
    /// the exact same helper the host uses for Themes.None.
    /// </summary>
    internal static class Theme
    {
        internal static Themes Resolve(ContextObject context)
        {
            if (context != null && context.Theme != Themes.None)
                return context.Theme;

            try
            {
                return OSThemeHelper.AppsUseDarkTheme() ? Themes.Dark : Themes.Light;
            }
            catch
            {
                return Themes.Light;
            }
        }

        internal static bool IsDark(Themes theme)
        {
            return theme == Themes.Dark ||
                   (theme == Themes.None && OSThemeHelper.AppsUseDarkTheme());
        }

        internal static Color Background(Themes theme)
        {
            return IsDark(theme)
                ? Color.FromRgb(0x1E, 0x1E, 0x20)
                : Color.FromRgb(0xF3, 0xF3, 0xF5);
        }

        internal static Color Foreground(Themes theme)
        {
            return IsDark(theme)
                ? Color.FromRgb(0xEC, 0xEC, 0xF0)
                : Color.FromRgb(0x22, 0x24, 0x2A);
        }

        internal static Color Accent(Themes theme)
        {
            return IsDark(theme)
                ? Color.FromRgb(0xF0, 0xB4, 0x4C)
                : Color.FromRgb(0x8A, 0x5A, 0x00);
        }

        internal static Color ModelColor(Themes theme)
        {
            return IsDark(theme)
                ? Color.FromRgb(0xC7, 0xCC, 0xD6)
                : Color.FromRgb(0x6B, 0x72, 0x80);
        }
    }
}

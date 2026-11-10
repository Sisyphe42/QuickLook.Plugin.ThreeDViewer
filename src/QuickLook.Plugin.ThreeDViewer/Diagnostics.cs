using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace QuickLook.Plugin.ThreeDViewer
{
    /// <summary>
    /// Lightweight, opt-in diagnostics. QuickLook swallows plugin exceptions and only
    /// writes them to its own log, so we keep our own trace next to the plugin DLL.
    /// Disabled unless the file "ThreeDViewer.debug" exists beside the plugin DLL,
    /// or the QL3D_DEBUG environment variable is set.
    /// </summary>
    internal static class Diagnostics
    {
        private static readonly object Gate = new object();
        private static readonly string LogPath = ResolveLogPath();
        private static readonly bool Enabled = ResolveEnabled();

        private static string ResolveLogPath()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(Diagnostics).Assembly.Location);
                return Path.Combine(dir ?? Path.GetTempPath(), "ThreeDViewer.log");
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "ThreeDViewer.log");
            }
        }

        private static bool ResolveEnabled()
        {
            try
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QL3D_DEBUG")))
                    return true;
                string dir = Path.GetDirectoryName(typeof(Diagnostics).Assembly.Location);
                return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "ThreeDViewer.debug"));
            }
            catch
            {
                return false;
            }
        }

        internal static void Log(string message)
        {
            if (!Enabled) return;
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [" + Process.GetCurrentProcess().Id + "] " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics must never break the preview.
            }
        }

        internal static void LogException(string where, Exception ex)
        {
            Log(where + " -> " + ex);
        }
    }
}

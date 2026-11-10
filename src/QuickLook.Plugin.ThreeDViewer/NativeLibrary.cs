using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Assimp.Unmanaged;

namespace QuickLook.Plugin.ThreeDViewer
{
    /// <summary>
    /// Pins the unmanaged assimp.dll to an absolute path inside the plugin folder.
    ///
    /// Why: on .NET Framework the CLR does not probe the plugin's own directory for
    /// native DLLs. AssimpNet's default resolver looks in
    /// {AppBaseDirectory}/runtimes/{rid}/native/, where AppBaseDirectory is the
    /// QuickLook.exe directory, NOT the plugin directory. Setting probing paths via
    /// the library's own resolver is the only approach that neither changes process
    /// global state (SetDllDirectory / PATH both do) nor depends on CLR probing.
    /// Verified: AssimpLibrary.Instance.Resolver.ResolveLibraryPath / LoadLibrary.
    /// </summary>
    internal static class NativeLibrary
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr hModule);

        private static readonly object Gate = new object();
        private static bool _initialized;
        private static string _pluginDirectory;

        internal static string PluginDirectory
        {
            get
            {
                if (_pluginDirectory == null)
                {
                    try
                    {
                        _pluginDirectory = Path.GetDirectoryName(
                            typeof(NativeLibrary).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory;
                    }
                    catch
                    {
                        _pluginDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    }
                }
                return _pluginDirectory;
            }
        }

        /// <summary>
        /// Selects win-x64 / win-x86 by the *process* bitness (IntPtr.Size), not the
        /// OS bitness. QuickLook is x64 on this machine but ships a 32-bit build too.
        /// </summary>
        internal static string NativeDirectory
        {
            get { return Path.Combine(PluginDirectory, IntPtr.Size == 8 ? "x64" : "x86"); }
        }

        internal static void EnsureInitialized()
        {
            lock (Gate)
            {
                if (_initialized) return;
                _initialized = true;

                string dir = NativeDirectory;
                string full = Path.Combine(dir, "assimp.dll");
                Diagnostics.Log("Native init: " + full);

                if (!File.Exists(full))
                    throw new FileNotFoundException("assimp.dll not found next to the plugin (expected " + full + ")", full);

                // 1) Let AssimpNet resolve from our directory (no process-global side effects).
                AssimpLibrary.Instance.Resolver.SetProbingPaths(dir);

                // 2) Eagerly load by absolute path so failures surface in Init(), not
                //    halfway through a preview. AssimpNet uses LoadLibrary(full path)
                //    internally, so this also registers the module in the OS loader.
                if (!AssimpLibrary.Instance.IsLibraryLoaded)
                {
                    if (!AssimpLibrary.Instance.LoadLibrary(full))
                        throw new InvalidOperationException("Failed to load native Assimp library: " + full);
                }

                Diagnostics.Log("Native OK: " + AssimpLibrary.Instance.LibraryPath +
                                " (Is64Bit=" + AssimpLibrary.Is64Bit + ")");
            }
        }

        /// <summary>
        /// Sanity probe used by the build/test harness: confirms that the exact file
        /// we ship can be loaded by the OS loader at the deployed location.
        /// </summary>
        internal static bool ProbeLoad(out string error)
        {
            error = null;
            try
            {
                string full = Path.Combine(NativeDirectory, "assimp.dll");
                IntPtr h = LoadLibrary(full);
                if (h == IntPtr.Zero)
                {
                    error = "LoadLibrary failed with Win32 error " + Marshal.GetLastWin32Error();
                    return false;
                }
                FreeLibrary(h);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}

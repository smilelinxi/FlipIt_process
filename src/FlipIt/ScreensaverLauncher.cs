using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ScreenSaver
{
    /// <summary>
    /// Locates and starts the FlipIt.scr screensaver for the desktop clock (hotkey, tray menu and
    /// the timed schedule all come through here).
    ///
    /// Resolution order: the user-configured path (if it exists), then FlipIt.scr next to the running
    /// exe, then the Windows system directories (where the post-build step installs it). If no .scr
    /// can be found at all, the clock's own exe is launched with /s — it is the very same program, so
    /// the screensaver still shows.
    /// </summary>
    internal static class ScreensaverLauncher
    {
        // The process we last started, so the schedule doesn't stack a second copy on top of a
        // screensaver that is still showing.
        private static Process _lastStarted;

        public static bool IsSaverActive
        {
            get
            {
                try
                {
                    return _lastStarted != null && !_lastStarted.HasExited;
                }
                catch
                {
                    return false;   // e.g. access denied querying the process: assume it is gone
                }
            }
        }

        /// <summary>The .scr that would be started, or null when only the own-exe fallback remains.</summary>
        public static string ResolvePath(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                return configuredPath;

            var exeDir = Path.GetDirectoryName(Application.ExecutablePath) ?? "";
            var candidates = new[]
            {
                Path.Combine(exeDir, "FlipIt.scr"),
                Path.Combine(Environment.SystemDirectory, "FlipIt.scr"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "FlipIt.scr"),
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>Starts the screensaver full-screen. Returns false only when launching failed.</summary>
        public static bool Start(string configuredPath)
        {
            if (IsSaverActive)
                return true;   // already showing

            var path = ResolvePath(configuredPath) ?? Application.ExecutablePath;
            try
            {
                // A .scr is an ordinary exe; start it directly (UseShellExecute would run the shell's
                // default .scr verb, which is "install", not "show").
                _lastStarted = Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "/s",
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

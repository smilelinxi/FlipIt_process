using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenSaver
{
    /// <summary>
    /// Settings for the desktop clock window. Deliberately a separate class and a separate file
    /// (DesktopClock.ini) from <see cref="FlipItSettings"/>/Settings.ini, so the clock and the
    /// screensaver can be configured independently of each other.
    ///
    /// The window bounds live in the same ini file but in their own [Window] section, written by
    /// <see cref="DesktopClockForm"/>; this class only owns the [General] section.
    /// </summary>
    public class DesktopClockSettings
    {
        // Display
        public bool Display24HrTime { get; set; }
        public bool ShowSeconds { get; set; }
        public int Scale { get; set; } = 85;
        public int HoursScale { get; set; } = 100;
        public int MinutesScale { get; set; } = 100;
        public int SecondsScale { get; set; } = 72;
        public bool FlipAnimation { get; set; } = true;
        public bool ShowDate { get; set; } = true;
        public bool ShowWeather { get; set; }
        // Empty = auto-locate by IP; otherwise the city name to look up the weather for.
        public string WeatherCity { get; set; } = "";
        public bool ShowSystemInfo { get; set; }
        public ClockBackgroundMode BackgroundMode { get; set; } = ClockBackgroundMode.Black;

        // Window behaviour
        public bool TopMost { get; set; }
        public bool DesktopBottom { get; set; }
        public bool ClickThrough { get; set; }
        // Snap the window flush to a screen edge when dragged close to it.
        public bool EdgeSnap { get; set; } = true;

        // Screensaver integration
        // Global hotkeys: Ctrl+Alt+S starts the screensaver, Ctrl+Alt+P toggles click-through.
        public bool HotkeysEnabled { get; set; } = true;
        // Empty = auto-locate FlipIt.scr (exe folder, then the Windows system directories).
        public string ScreensaverPath { get; set; } = "";
        public SaverScheduleMode SaverScheduleMode { get; set; } = SaverScheduleMode.Off;
        // Daily mode: the "HH:mm" the screensaver starts at.
        public string SaverScheduleTime { get; set; } = "22:00";
        // Interval mode: start the screensaver every this many minutes.
        public int SaverIntervalMinutes { get; set; } = 30;

        private static string SettingsFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlipIt");

        internal static string FilePath => Path.Combine(SettingsFolder, "DesktopClock.ini");

        public static DesktopClockSettings Load()
        {
            var settings = new DesktopClockSettings();
            try
            {
                if (!File.Exists(FilePath))
                    return settings;

                var ini = new IniFile(FilePath);
                settings.Display24HrTime = ini.GetBool("General", "Display24Hr", settings.Display24HrTime);
                settings.ShowSeconds = ini.GetBool("General", "ShowSeconds", settings.ShowSeconds);
                settings.Scale = ini.GetInt("General", "Scale", settings.Scale);
                settings.HoursScale = ini.GetInt("General", "HoursScale", settings.HoursScale);
                settings.MinutesScale = ini.GetInt("General", "MinutesScale", settings.MinutesScale);
                settings.SecondsScale = ini.GetInt("General", "SecondsScale", settings.SecondsScale);
                settings.FlipAnimation = ini.GetBool("General", "FlipAnimation", settings.FlipAnimation);
                settings.ShowDate = ini.GetBool("General", "ShowDate", settings.ShowDate);
                settings.ShowWeather = ini.GetBool("General", "ShowWeather", settings.ShowWeather);
                settings.WeatherCity = ini.GetString("General", "WeatherCity") ?? settings.WeatherCity;
                settings.ShowSystemInfo = ini.GetBool("General", "ShowSystemInfo", settings.ShowSystemInfo);
                settings.BackgroundMode = (ClockBackgroundMode)ini.GetInt("General", "Background", (int)settings.BackgroundMode);

                // TopMost used to live in the [Window] section (written by older builds); fall back
                // to it so the preference survives the upgrade.
                settings.TopMost = ini.GetBool("General", "TopMost", ini.GetBool("Window", "TopMost", false));
                settings.DesktopBottom = ini.GetBool("General", "DesktopBottom", settings.DesktopBottom);
                settings.ClickThrough = ini.GetBool("General", "ClickThrough", settings.ClickThrough);
                settings.EdgeSnap = ini.GetBool("General", "EdgeSnap", settings.EdgeSnap);

                settings.HotkeysEnabled = ini.GetBool("General", "HotkeysEnabled", settings.HotkeysEnabled);
                settings.ScreensaverPath = ini.GetString("General", "SaverPath") ?? settings.ScreensaverPath;
                settings.SaverScheduleMode = (SaverScheduleMode)ini.GetInt("General", "SaverScheduleMode", (int)settings.SaverScheduleMode);
                if (settings.SaverScheduleMode < SaverScheduleMode.Off || settings.SaverScheduleMode > SaverScheduleMode.Interval)
                    settings.SaverScheduleMode = SaverScheduleMode.Off;
                settings.SaverScheduleTime = ini.GetString("General", "SaverScheduleTime") ?? settings.SaverScheduleTime;
                settings.SaverIntervalMinutes = Math.Min(24 * 60, Math.Max(1,
                    ini.GetInt("General", "SaverIntervalMinutes", settings.SaverIntervalMinutes)));
            }
            catch
            {
                // A corrupt settings file falls back to defaults rather than stopping the clock.
            }
            return settings;
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                    Directory.CreateDirectory(SettingsFolder);
                if (!File.Exists(FilePath))
                    File.WriteAllText(FilePath, "");

                // IniFile keeps every section it read, so the [Window] bounds written by the form survive.
                var ini = new IniFile(FilePath);
                ini.SetBool("General", "Display24Hr", Display24HrTime);
                ini.SetBool("General", "ShowSeconds", ShowSeconds);
                ini.SetInt("General", "Scale", Scale);
                ini.SetInt("General", "HoursScale", HoursScale);
                ini.SetInt("General", "MinutesScale", MinutesScale);
                ini.SetInt("General", "SecondsScale", SecondsScale);
                ini.SetBool("General", "FlipAnimation", FlipAnimation);
                ini.SetBool("General", "ShowDate", ShowDate);
                ini.SetBool("General", "ShowWeather", ShowWeather);
                ini.SetString("General", "WeatherCity", WeatherCity ?? "");
                ini.SetBool("General", "ShowSystemInfo", ShowSystemInfo);
                ini.SetInt("General", "Background", (int)BackgroundMode);
                ini.SetBool("General", "TopMost", TopMost);
                ini.SetBool("General", "DesktopBottom", DesktopBottom);
                ini.SetBool("General", "ClickThrough", ClickThrough);
                ini.SetBool("General", "EdgeSnap", EdgeSnap);
                ini.SetBool("General", "HotkeysEnabled", HotkeysEnabled);
                ini.SetString("General", "SaverPath", ScreensaverPath ?? "");
                ini.SetInt("General", "SaverScheduleMode", (int)SaverScheduleMode);
                ini.SetString("General", "SaverScheduleTime", SaverScheduleTime ?? "");
                ini.SetInt("General", "SaverIntervalMinutes", SaverIntervalMinutes);
                ini.Save();
            }
            catch
            {
                // Not being able to persist settings must never crash the clock.
            }
        }
    }

    /// <summary>
    /// When (if ever) the clock starts the screensaver by itself.
    /// The numeric values are stored in DesktopClock.ini and shown in this order in the settings UI.
    /// </summary>
    public enum SaverScheduleMode
    {
        Off = 0,        // never start automatically
        Daily = 1,      // every day at SaverScheduleTime
        Interval = 2,   // every SaverIntervalMinutes minutes
    }

    /// <summary>
    /// "Start with Windows" for the desktop clock, via the per-user Run registry key (no admin needed).
    /// The state lives in the registry only, so it is intentionally not part of DesktopClock.ini.
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "FlipItDesktopClock";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                    return key?.GetValue(ValueName) != null;
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null)
                        return;
                    if (enabled)
                        // Quote the path (it may contain spaces). Running this exe with no arguments
                        // opens the desktop clock when the exe is the renamed clock copy (e.g. 桌面时钟.exe),
                        // so pass /d explicitly to be correct for any exe name.
                        key.SetValue(ValueName, $"\"{Application.ExecutablePath}\" /d");
                    else
                        key.DeleteValue(ValueName, false);
                }
            }
            catch
            {
                // e.g. registry access denied; the checkbox simply won't stick.
            }
        }
    }
}

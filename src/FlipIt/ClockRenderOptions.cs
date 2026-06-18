using System.Drawing;

namespace ScreenSaver
{
    /// <summary>How the clock background is painted (desktop clock only; the screensaver is always dark).</summary>
    public enum ClockBackgroundMode
    {
        Black = 0,
        White = 1,
        Transparent = 2,
        FollowSystem = 3,
    }

    /// <summary>
    /// The resolved palette a <see cref="CurrentTimeScreen"/> renders with. "Resolved" means
    /// FollowSystem/Transparent have already been turned into concrete colors by the caller.
    /// </summary>
    internal class ClockColors
    {
        public Color Background;   // window background; also the card split-line color
        public Color CardTop;      // flip-card gradient, top
        public Color CardBottom;   // flip-card gradient, bottom
        public Color Text;         // digits, date and info line

        public static ClockColors Dark()
        {
            return new ClockColors
            {
                Background = Color.Black,
                CardTop = Color.FromArgb(255, 18, 18, 18),
                CardBottom = Color.FromArgb(255, 10, 10, 10),
                Text = Color.FromArgb(255, 183, 183, 183),
            };
        }

        public static ClockColors Light()
        {
            return new ClockColors
            {
                Background = Color.White,
                CardTop = Color.FromArgb(255, 238, 238, 238),
                CardBottom = Color.FromArgb(255, 219, 219, 219),
                Text = Color.FromArgb(255, 55, 55, 55),
            };
        }
    }

    /// <summary>
    /// Everything <see cref="CurrentTimeScreen"/> needs to know to lay out and draw one clock.
    /// Replaces the old ever-growing constructor parameter list.
    /// </summary>
    internal class ClockRenderOptions
    {
        public bool Display24HrTime;
        public bool IsPreviewMode;
        public int ScalePercent = 70;
        public bool ShowSeconds;
        public int HoursScalePercent = 100;
        public int MinutesScalePercent = 100;
        public int SecondsScalePercent = 72;
        public bool FlipAnimation = true;
        public bool ShowDate = true;
        public bool ShowWeather;
        public bool ShowSystemInfo;
        public ClockColors Colors = ClockColors.Dark();
    }

    /// <summary>Reads the Windows light/dark preference (for ClockBackgroundMode.FollowSystem).</summary>
    internal static class SystemThemeDetector
    {
        public static bool IsLightTheme()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key?.GetValue("AppsUseLightTheme");
                    if (value is int i)
                        return i != 0;
                }
            }
            catch
            {
                // Registry unavailable: assume dark, which matches the app's classic look.
            }
            return false;
        }
    }
}

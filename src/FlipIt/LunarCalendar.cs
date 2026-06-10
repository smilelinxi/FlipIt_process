using System;
using System.Globalization;

namespace ScreenSaver
{
    /// <summary>
    /// Converts a Gregorian date to a Chinese lunar date string, e.g. "丙午年五月廿四".
    /// Uses the framework's <see cref="ChineseLunisolarCalendar"/> (supports ~1901-2100).
    /// </summary>
    internal static class LunarCalendar
    {
        private static readonly string[] CelestialStems = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
        private static readonly string[] TerrestrialBranches = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };
        private static readonly string[] MonthNames = { "正", "二", "三", "四", "五", "六", "七", "八", "九", "十", "冬", "腊" };
        private static readonly string[] DayOnes = { "一", "二", "三", "四", "五", "六", "七", "八", "九" };

        public static string GetLunarDate(DateTime date)
        {
            try
            {
                var cal = new ChineseLunisolarCalendar();
                if (date < cal.MinSupportedDateTime || date > cal.MaxSupportedDateTime)
                    return string.Empty;

                var sexagenaryYear = cal.GetSexagenaryYear(date);        // 1..60
                var stem = cal.GetCelestialStem(sexagenaryYear);         // 1..10 (天干)
                var branch = cal.GetTerrestrialBranch(sexagenaryYear);   // 1..12 (地支)

                var lunarYear = cal.GetYear(date);
                var leapMonth = cal.GetLeapMonth(lunarYear);             // 0 = none, else the leap month's index (2..13)
                var monthIndex = cal.GetMonth(date);                     // 1..12, or 1..13 in a leap year
                var day = cal.GetDayOfMonth(date);

                var isLeap = false;
                var month = monthIndex;
                if (leapMonth > 0)
                {
                    if (monthIndex == leapMonth)
                    {
                        isLeap = true;
                        month = monthIndex - 1;
                    }
                    else if (monthIndex > leapMonth)
                    {
                        month = monthIndex - 1;
                    }
                }

                var yearText = $"{CelestialStems[stem - 1]}{TerrestrialBranches[branch - 1]}年";
                var monthText = (isLeap ? "闰" : "") + MonthNames[month - 1] + "月";
                return yearText + monthText + GetDayText(day);
            }
            catch
            {
                // Out of the calendar's supported range, or an unexpected value: just skip the lunar part.
                return string.Empty;
            }
        }

        private static string GetDayText(int day)
        {
            if (day == 10) return "初十";
            if (day == 20) return "二十";
            if (day == 30) return "三十";

            var tens = day / 10; // 0, 1 or 2
            var ones = day % 10; // 1..9 (the exact-ten cases are handled above)
            var prefix = tens == 0 ? "初" : (tens == 1 ? "十" : "廿");
            return prefix + DayOnes[ones - 1];
        }
    }
}

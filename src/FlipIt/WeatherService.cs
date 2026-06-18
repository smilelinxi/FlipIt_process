using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ScreenSaver
{
    /// <summary>
    /// Fetches the current weather for the machine's location (geolocated by IP) and exposes it as a
    /// ready-to-draw line like "深圳 多云 28°C".
    ///
    /// Design constraints: the clock paints on the UI thread, so this never blocks — GetDisplayText
    /// returns the last known value immediately (null before the first fetch finishes) and refreshes
    /// in the background. Successful results are cached for 30 minutes; failures retry after 2.
    ///
    /// Uses two keyless public APIs: ip-api.com for the location and open-meteo.com for the weather,
    /// parsed with small regexes so no JSON library is needed.
    /// </summary>
    internal static class WeatherService
    {
        private static readonly object Lock = new object();
        private static string _display;
        private static DateTime _lastAttemptUtc = DateTime.MinValue;
        private static bool _fetching;
        private static bool _lastFailed;
        // null/empty = auto-locate by IP; otherwise the user-chosen city to geocode.
        private static string _configuredCity;

        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);

        static WeatherService()
        {
            // .NET 4.8 on Win11 negotiates TLS 1.2 by default, but be explicit so a machine-level
            // policy override can't silently break the HTTPS call.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        /// <summary>
        /// Set the city the weather is fetched for. Empty/null returns to auto IP-geolocation.
        /// Changing it clears the cache so the next <see cref="GetDisplayText"/> refetches immediately.
        /// </summary>
        public static void SetCity(string city)
        {
            var normalized = string.IsNullOrWhiteSpace(city) ? null : city.Trim();
            lock (Lock)
            {
                if (normalized == _configuredCity)
                    return;
                _configuredCity = normalized;
                _display = null;
                _lastAttemptUtc = DateTime.MinValue;   // force a fresh fetch on next access
                _lastFailed = false;
            }
        }

        /// <summary>Latest weather line, or null while nothing has been fetched yet. Never blocks.</summary>
        public static string GetDisplayText()
        {
            EnsureFresh();
            lock (Lock)
                return _display;
        }

        private static void EnsureFresh()
        {
            lock (Lock)
            {
                if (_fetching)
                    return;
                var interval = _lastFailed ? RetryInterval : RefreshInterval;
                if (DateTime.UtcNow - _lastAttemptUtc < interval)
                    return;
                _fetching = true;
            }
            ThreadPool.QueueUserWorkItem(_ => Fetch());
        }

        private static void Fetch()
        {
            string result = null;
            try
            {
                result = FetchOnce();
            }
            catch
            {
                // No network / API down: keep showing the previous value and retry later.
            }
            lock (Lock)
            {
                _fetching = false;
                _lastAttemptUtc = DateTime.UtcNow;
                _lastFailed = result == null;
                if (result != null)
                    _display = result;
            }
        }

        private static string FetchOnce()
        {
            string configuredCity;
            lock (Lock)
                configuredCity = _configuredCity;

            string city = null;
            double? lat = null, lon = null;

            if (!string.IsNullOrEmpty(configuredCity))
            {
                // Manual city: geocode the name to coordinates via open-meteo's geocoding API.
                city = configuredCity;
                try
                {
                    var geo = HttpGet("https://geocoding-api.open-meteo.com/v1/search?count=1&language=zh&format=json&name="
                        + Uri.EscapeDataString(configuredCity));
                    if (geo != null)
                    {
                        lat = JsonNumber(geo, "latitude");
                        lon = JsonNumber(geo, "longitude");
                        city = JsonString(geo, "name") ?? configuredCity;
                    }
                }
                catch
                {
                    // Fall through; the wttr.in fallback below can locate the city by name.
                }
            }
            else
            {
                // Geolocate by IP (gives a Chinese city name). Optional: without it we still show weather.
                try
                {
                    var geo = HttpGet("http://ip-api.com/json/?fields=status,city,lat,lon&lang=zh-CN");
                    if (geo != null && JsonString(geo, "status") == "success")
                    {
                        city = JsonString(geo, "city");
                        lat = JsonNumber(geo, "lat");
                        lon = JsonNumber(geo, "lon");
                    }
                }
                catch
                {
                    // Fall through; wttr.in locates by IP itself.
                }
            }

            // Primary source: open-meteo (small response, stable schema). Some networks block it,
            // so wttr.in (which locates by IP on its own) is the fallback.
            if (lat != null && lon != null)
            {
                try
                {
                    var url = string.Format(CultureInfo.InvariantCulture,
                        "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}&current_weather=true",
                        lat.Value, lon.Value);
                    var wx = HttpGet(url);
                    var temperature = JsonNumber(wx, "temperature");
                    var code = JsonNumber(wx, "weathercode");
                    if (temperature != null)
                        return ComposeDisplay(city, code != null ? WeatherCodeText((int)code.Value) : "",
                            Math.Round(temperature.Value));
                }
                catch
                {
                    // Try the fallback below.
                }
            }

            // wttr.in fallback: locate by the configured city name, or by IP when none is set.
            var wttrUrl = string.IsNullOrEmpty(configuredCity)
                ? "https://wttr.in/?format=j1"
                : "https://wttr.in/" + Uri.EscapeDataString(configuredCity) + "?format=j1";
            var json = HttpGet(wttrUrl);
            if (json == null)
                return null;
            // current_condition is the first object in the j1 payload, so the first match is the
            // current weather (later matches are the hourly forecast).
            var tempC = JsonNumber(json, "temp_C");
            var wwoCode = JsonNumber(json, "weatherCode");
            if (tempC == null)
                return null;
            return ComposeDisplay(city, wwoCode != null ? WwoCodeText((int)wwoCode.Value) : "",
                Math.Round(tempC.Value));
        }

        private static string ComposeDisplay(string city, string condition, double temperature)
        {
            var parts = new StringBuilder();
            if (!string.IsNullOrEmpty(city))
                parts.Append(city).Append(' ');
            if (!string.IsNullOrEmpty(condition))
                parts.Append(condition).Append(' ');
            parts.Append(temperature).Append("°C");
            return parts.ToString();
        }

        private static string HttpGet(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.UserAgent = "FlipIt-Clock";
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            {
                if (stream == null)
                    return null;
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        private static string JsonString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        // Accepts both bare numbers (open-meteo) and quoted numbers (wttr.in writes "temp_C": "22").
        private static double? JsonNumber(string json, string key)
        {
            if (json == null)
                return null;
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"?(-?[0-9]+(?:\\.[0-9]+)?)\"?");
            if (!m.Success)
                return null;
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        // WMO weather interpretation codes, as documented by open-meteo.
        private static string WeatherCodeText(int code)
        {
            switch (code)
            {
                case 0: return "晴";
                case 1: return "晴间多云";
                case 2: return "多云";
                case 3: return "阴";
                case 45:
                case 48: return "雾";
                case 51:
                case 53:
                case 55:
                case 56:
                case 57: return "毛毛雨";
                case 61:
                case 63:
                case 66: return "小雨";
                case 65:
                case 67: return "大雨";
                case 71:
                case 73: return "小雪";
                case 75:
                case 77: return "大雪";
                case 80:
                case 81: return "阵雨";
                case 82: return "强阵雨";
                case 85:
                case 86: return "阵雪";
                case 95: return "雷阵雨";
                case 96:
                case 99: return "雷阵雨伴冰雹";
                default: return "";
            }
        }

        // World Weather Online condition codes, as returned by wttr.in's j1 format.
        private static string WwoCodeText(int code)
        {
            switch (code)
            {
                case 113: return "晴";
                case 116: return "多云";
                case 119:
                case 122: return "阴";
                case 143:
                case 248:
                case 260: return "雾";
                case 176:
                case 263:
                case 266:
                case 293:
                case 296:
                case 353: return "小雨";
                case 299:
                case 302:
                case 356: return "中雨";
                case 305:
                case 308:
                case 359: return "大雨";
                case 185:
                case 281:
                case 284:
                case 311:
                case 314: return "冻雨";
                case 182:
                case 317:
                case 320:
                case 362:
                case 365: return "雨夹雪";
                case 179:
                case 323:
                case 326:
                case 368: return "小雪";
                case 329:
                case 332: return "中雪";
                case 227:
                case 230:
                case 335:
                case 338:
                case 371: return "大雪";
                case 350:
                case 374:
                case 377: return "冰粒";
                case 200:
                case 386:
                case 389: return "雷阵雨";
                case 392:
                case 395: return "雷阵雪";
                default: return "";
            }
        }
    }
}

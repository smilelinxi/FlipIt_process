using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace ScreenSaver
{
    internal class CurrentTimeScreen : TimeScreen
    {
        private readonly ClockRenderOptions _options;

        private bool Display24HourTime => _options.Display24HrTime;
        private bool IsPreviewMode => _options.IsPreviewMode;
        private bool ShowSeconds => _options.ShowSeconds;
        private bool UseFlipAnimation => _options.FlipAnimation;
        private bool ShowDate => _options.ShowDate;
        private bool ShowInfoLine => _options.ShowWeather || _options.ShowSystemInfo;

        private const int SplitWidth = 4;
        private const double BoxSeparationPercent = 0.05; // ie. 5%

        // How long a single card takes to flip over.
        private const double FlipDurationSeconds = 0.28;

        // Heights of the date / info strips relative to the (largest) box height.
        private const double DateStripFactor = 0.30;
        private const double InfoStripFactor = 0.22;

        // Size of each box relative to the "base" box size. 1.0 = full size, 0.72 = 72%.
        private readonly double _hoursScale;
        private readonly double _minutesScale;
        private readonly double _secondsScale;

        private Font _hoursFont;
        private Font _minutesFont;
        private Font _secondsFont;
        private Font _smallFont;
        private Font _dateFont;
        private Font _infoFont;

        private Font HoursFont => _hoursFont ?? (_hoursFont = MakeFont(_hoursBoxSize, 85));
        private Font MinutesFont => _minutesFont ?? (_minutesFont = MakeFont(_minutesBoxSize, 85));
        private Font SecondsFont => _secondsFont ?? (_secondsFont = MakeFont(_secondsBoxSize, 85));
        private Font SmallFont => _smallFont ?? (_smallFont = MakeFont(_hoursBoxSize, 9));
        // The date is in Chinese, so it needs a system font with CJK glyphs (the embedded Helvetica has none).
        private Font DateFont => _dateFont ?? (_dateFont = new Font("Microsoft YaHei", _dateFontSize, FontStyle.Regular, GraphicsUnit.Pixel));
        private Font InfoFont => _infoFont ?? (_infoFont = new Font("Microsoft YaHei", _infoFontSize, FontStyle.Regular, GraphicsUnit.Pixel));

        private Font MakeFont(int boxSize, int percent)
        {
            return new Font(FontFamily, boxSize.Percent(percent), FontStyle.Bold, GraphicsUnit.Pixel);
        }

        private readonly Brush _fontBrush;
        private readonly Brush _infoBrush;   // slightly dimmer than the digits
        private readonly Pen _splitPen;

        private readonly int _hoursBoxSize;
        private readonly int _minutesBoxSize;
        private readonly int _secondsBoxSize;
        private readonly int _separatorWidth;

        private readonly Rectangle _hoursRect;
        private readonly Rectangle _minutesRect;
        private readonly Rectangle _secondsRect;
        private readonly Rectangle _dateRect;
        private readonly Rectangle _infoRect;
        private readonly int _dateFontSize;
        private readonly int _infoFontSize;

        public CurrentTimeScreen(Control form, ClockRenderOptions options)
        {
            _options = options;
            _form = form;

            _fontBrush = new SolidBrush(options.Colors.Text);
            _infoBrush = new SolidBrush(Color.FromArgb(200, options.Colors.Text));
            _splitPen = new Pen(options.Colors.Background, SplitWidth);

            // Clamp each scale to a sensible range so a stray setting can't make a box vanish or overflow.
            _hoursScale = ClampScale(options.HoursScalePercent);
            _minutesScale = ClampScale(options.MinutesScalePercent);
            _secondsScale = ClampScale(options.SecondsScalePercent);

            // The border is between 5% and 30% of the screen
            //  * A scale of 0 = 5%
            //  * A scale of 100 = 30%
            var borderPercent = (100 - options.ScalePercent) / 4 + 5;
            var borderW = form.Width.Percent(borderPercent);
            var borderH = form.Height.Percent(borderPercent);
            var remainingWidth = form.Width - (borderW * 2);
            var remainingHeight = form.Height - (borderH * 2);

            // Pick the largest "base" box size (the size a scale-1.0 box would be) that fits both the
            // available width (hours + minutes + seconds + separators) and the available height (the
            // tallest box plus the date / info strips below it).
            var separators = ShowSeconds ? BoxSeparationPercent * 2 : BoxSeparationPercent;
            var widthParts = _hoursScale + _minutesScale + (ShowSeconds ? _secondsScale : 0) + separators;
            var baseFromWidth = remainingWidth / widthParts;

            var maxScale = Math.Max(_hoursScale, Math.Max(_minutesScale, ShowSeconds ? _secondsScale : 0));
            var stripFactors = (ShowDate ? DateStripFactor : 0) + (ShowInfoLine ? InfoStripFactor : 0);
            var baseFromHeight = remainingHeight / (maxScale * (1 + stripFactors));

            var baseSize = Math.Min(baseFromWidth, baseFromHeight);

            _hoursBoxSize = (int)Math.Round(baseSize * _hoursScale);
            _minutesBoxSize = (int)Math.Round(baseSize * _minutesScale);
            _secondsBoxSize = ShowSeconds ? (int)Math.Round(baseSize * _secondsScale) : 0;
            _separatorWidth = (int)Math.Round(baseSize * BoxSeparationPercent);

            // Treat the clock row plus the date / info strips as one block and centre that whole block
            // vertically, so the layout stays balanced whatever shape the window is stretched to
            // (previously the date hugged the bottom edge, leaving a lopsided gap above).
            var maxBoxSize = Math.Max(_hoursBoxSize, Math.Max(_minutesBoxSize, _secondsBoxSize));
            var dateHeight = ShowDate ? (int)Math.Round(maxBoxSize * DateStripFactor) : 0;
            var infoHeight = ShowInfoLine ? (int)Math.Round(maxBoxSize * InfoStripFactor) : 0;
            var blockHeight = maxBoxSize + dateHeight + infoHeight;

            var rowTop = borderH + Math.Max(0, (remainingHeight - blockHeight) / 2);
            var rowBottom = rowTop + maxBoxSize;

            // Boxes sit in a single row, all aligned along a common bottom line. The smaller seconds
            // box therefore sits at the bottom-right, its base level with the hours/minutes boxes.
            var totalWidth = _hoursBoxSize + _separatorWidth + _minutesBoxSize
                + (ShowSeconds ? _separatorWidth + _secondsBoxSize : 0);
            var startingX = (form.Width - totalWidth) / 2;

            _hoursRect = new Rectangle(startingX, rowBottom - _hoursBoxSize, _hoursBoxSize, _hoursBoxSize);
            var minutesX = startingX + _hoursBoxSize + _separatorWidth;
            _minutesRect = new Rectangle(minutesX, rowBottom - _minutesBoxSize, _minutesBoxSize, _minutesBoxSize);

            if (ShowSeconds)
            {
                var secondsX = _minutesRect.Right + _separatorWidth;
                _secondsRect = new Rectangle(secondsX, rowBottom - _secondsBoxSize, _secondsBoxSize, _secondsBoxSize);
            }

            if (ShowDate)
            {
                _dateRect = Rectangle.FromLTRB(0, rowBottom, form.Width, rowBottom + dateHeight);
                // Size by the available height, but then shrink so the whole line fits the width on one
                // row (otherwise a wide-but-short region produces a huge font that wraps onto two lines).
                var byHeight = Math.Max(12, (int)(dateHeight * 0.48));
                _dateFontSize = FitDateFontSize(byHeight, form.Width);
            }

            if (ShowInfoLine)
            {
                var infoTop = rowBottom + dateHeight;
                _infoRect = Rectangle.FromLTRB(0, infoTop, form.Width, infoTop + infoHeight);
                _infoFontSize = Math.Max(11, (int)(infoHeight * 0.52));
            }
        }

        // Largest font (<= maxFontPx) at which a worst-case date string still fits within ~94% of the
        // width on a single line.
        private static int FitDateFontSize(int maxFontPx, int formWidth)
        {
            const string template = "2026年12月29日  星期三  农历甲子年闰十二月廿九";
            var maxWidth = formWidth * 0.94f;
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
            {
                var size = maxFontPx;
                for (var i = 0; i < 8 && size > 12; i++)
                {
                    using (var font = new Font("Microsoft YaHei", size, FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        var width = g.MeasureString(template, font).Width;
                        if (width <= maxWidth)
                            break;
                        size = (int)(size * (maxWidth / width)); // scale straight to the target width
                    }
                }
                return Math.Max(12, size);
            }
        }

        private static double ClampScale(int percent)
        {
            return Math.Min(150, Math.Max(10, percent)) / 100.0;
        }

        protected override byte[] GetFontResource()
        {
            return Properties.Resources.HelveticaLTStd_BoldCond;
        }

        protected override Color ClearColor => _options.Colors.Background;

        protected override void DrawCore()
        {
            var now = SystemTime.Now;
            var durationMs = FlipDurationSeconds * 1000;

            // Hours
            var prevHour = now.AddHours(-1);
            var hoursCur = Display24HourTime ? now.ToString("HH") : now.ToString("%h");
            var hoursPrev = Display24HourTime ? prevHour.ToString("HH") : prevHour.ToString("%h");
            var hoursMs = (now.Minute * 60 + now.Second) * 1000.0 + now.Millisecond;
            DrawFlipBox(_hoursRect, HoursFont, hoursCur, hoursPrev, Math.Min(1.0, hoursMs / durationMs));
            if (!Display24HourTime)
                DrawAmPm(_hoursRect, now);

            // Minutes
            var minutesMs = now.Second * 1000.0 + now.Millisecond;
            DrawFlipBox(_minutesRect, MinutesFont, now.ToString("mm"), now.AddMinutes(-1).ToString("mm"), Math.Min(1.0, minutesMs / durationMs));

            // Seconds (small box in the bottom-right corner of the minutes box)
            if (ShowSeconds)
            {
                DrawFlipBox(_secondsRect, SecondsFont, now.ToString("ss"), now.AddSeconds(-1).ToString("ss"), Math.Min(1.0, now.Millisecond / durationMs));
            }

            if (ShowDate)
                DrawDate(now);

            if (ShowInfoLine)
                DrawInfoLine();
        }

        private void DrawFlipBox(Rectangle rect, Font font, string currentText, string previousText, double progress)
        {
            // Static (no animation): just the current value, like the classic non-animated render.
            if (!UseFlipAnimation || progress >= 1.0 || currentText == previousText)
            {
                DrawCardContent(rect, font, currentText);
                DrawSplit(rect);
                return;
            }

            // Behind the moving flap: the new value's top half and the old value's bottom half.
            DrawHalf(rect, font, currentText, topHalf: true);
            DrawHalf(rect, font, previousText, topHalf: false);

            // The flap is hinged on the centre line. In the first half of the flip the old top folds
            // down (foreshortening to nothing); in the second half the new bottom folds in from nothing.
            var cos = Math.Cos(progress * Math.PI);
            if (progress < 0.5)
                DrawFlap(rect, font, previousText, topContent: true, vScale: cos);
            else
                DrawFlap(rect, font, currentText, topContent: false, vScale: -cos);

            DrawSplit(rect);
        }

        private void DrawHalf(Rectangle rect, Font font, string text, bool topHalf)
        {
            var half = rect.Height / 2;
            var clip = topHalf
                ? new Rectangle(rect.X, rect.Y, rect.Width, half)
                : new Rectangle(rect.X, rect.Y + half, rect.Width, rect.Height - half);
            Gfx.SetClip(clip);
            DrawCardContent(rect, font, text);
            Gfx.ResetClip();
        }

        private void DrawFlap(Rectangle rect, Font font, string text, bool topContent, double vScale)
        {
            if (vScale <= 0.001)
                return;

            var centerY = rect.Y + rect.Height / 2;
            var half = rect.Height / 2;
            var flapH = (int)Math.Round(half * vScale);
            if (flapH <= 0)
                return;

            var flapRect = topContent
                ? new Rectangle(rect.X, centerY - flapH, rect.Width, flapH)
                : new Rectangle(rect.X, centerY, rect.Width, flapH);

            // Squash the card vertically about the centre line and clip to the flap, so only the relevant
            // (foreshortened) half shows.
            var state = Gfx.Save();
            Gfx.SetClip(flapRect);
            Gfx.TranslateTransform(0, centerY);
            Gfx.ScaleTransform(1f, (float)vScale);
            Gfx.TranslateTransform(0, -centerY);
            DrawCardContent(rect, font, text);
            Gfx.Restore(state);

            // Fake the lighting: the flap darkens slightly as it turns edge-on (kept subtle so the
            // mid-flip card doesn't look like it's being covered by a dark band).
            var alpha = (int)(70 * (1 - vScale));
            if (alpha > 0)
            {
                using (var shade = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
                    Gfx.FillRectangle(shade, flapRect);
            }
        }

        private void DrawCardContent(Rectangle rect, Font font, string s)
        {
            DrawBox(rect);

            var diff = rect.Width / 10;
            // Some hacky adjustments to center the text in the box
            var xOffset = rect.Width.Percent(1);
            var yOffset = rect.Height.Percent(4);
            var textRect = new Rectangle(rect.Left - diff + xOffset, rect.Y + yOffset, rect.Width + diff * 2, rect.Height);

            var stringFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            Gfx.DrawString(s, font, _fontBrush, textRect, stringFormat);
        }

        private void DrawBox(Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                return;
            // A very small box (e.g. seconds at 10% in the little preview) gives width/20 == 0, and a
            // zero radius makes RoundedRectangle's AddArc throw "invalid parameter". Keep it at least 1.
            var radius = Math.Max(1, rect.Width / 20);
            using (var path = RoundedRectangle.Create(rect, radius))
            using (var brush = new LinearGradientBrush(rect, _options.Colors.CardTop, _options.Colors.CardBottom, LinearGradientMode.Vertical))
            {
                Gfx.FillPath(brush, path);
            }
        }

        private void DrawSplit(Rectangle rect)
        {
            if (!IsPreviewMode)
            {
                var y = rect.Y + (rect.Height / 2) - (SplitWidth / 2);
                Gfx.DrawLine(_splitPen, rect.Left, y, rect.Right, y);
            }
            else
            {
                var y = rect.Y + (rect.Height / 2);
                using (var thinPen = new Pen(_options.Colors.Background))
                    Gfx.DrawLine(thinPen, rect.Left, y, rect.Right, y);
            }
        }

        private void DrawAmPm(Rectangle rect, DateTime now)
        {
            var diff = rect.Width / 10;
            var leftOffset = diff / 2;
            if (now.Hour >= 12)
                Gfx.DrawString("PM", SmallFont, _fontBrush, rect.X + leftOffset, rect.Bottom - diff - SmallFont.Height);
            else
                Gfx.DrawString("AM", SmallFont, _fontBrush, rect.X + leftOffset, rect.Y + diff);
        }

        private void DrawDate(DateTime now)
        {
            var sb = new StringBuilder();
            sb.Append(now.ToString("yyyy年M月d日"));
            sb.Append("  ");
            sb.Append(WeekdayText(now));
            var lunar = LunarCalendar.GetLunarDate(now);
            if (!string.IsNullOrEmpty(lunar))
            {
                sb.Append("  农历");
                sb.Append(lunar);
            }

            var stringFormat = new StringFormat(StringFormatFlags.NoWrap)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            Gfx.DrawString(sb.ToString(), DateFont, _fontBrush, _dateRect, stringFormat);
        }

        // Weather and/or CPU+memory, centred on one line below the date.
        private void DrawInfoLine()
        {
            var sb = new StringBuilder();
            if (_options.ShowWeather)
            {
                var weather = WeatherService.GetDisplayText();
                sb.Append(weather ?? "天气获取中…");
            }
            if (_options.ShowSystemInfo)
            {
                if (sb.Length > 0)
                    sb.Append("    ");
                sb.Append(SystemInfoService.GetDisplayText());
            }
            if (sb.Length == 0)
                return;

            var stringFormat = new StringFormat(StringFormatFlags.NoWrap)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            Gfx.DrawString(sb.ToString(), InfoFont, _infoBrush, _infoRect, stringFormat);
        }

        private static string WeekdayText(DateTime now)
        {
            string[] names = { "日", "一", "二", "三", "四", "五", "六" };
            return "星期" + names[(int)now.DayOfWeek];
        }

        // True while a card is part-way through a flip. The desktop clock uses this to stay completely
        // idle (burning no CPU) except during the brief moments something is actually animating.
        internal bool IsFlipActive(DateTime now)
        {
            if (!UseFlipAnimation)
                return false;
            var durationMs = FlipDurationSeconds * 1000;
            if (ShowSeconds)
                return now.Millisecond < durationMs;                    // the seconds card flips every second
            return now.Second == 0 && now.Millisecond < durationMs;     // only the minute/hour cards flip
        }

        internal override void DisposeResources()
        {
            _hoursFont?.Dispose();
            _minutesFont?.Dispose();
            _secondsFont?.Dispose();
            _smallFont?.Dispose();
            _dateFont?.Dispose();
            _infoFont?.Dispose();
            _fontBrush?.Dispose();
            _infoBrush?.Dispose();
            _splitPen?.Dispose();
            base.DisposeResources();
        }
    }
}

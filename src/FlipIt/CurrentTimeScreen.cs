using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace ScreenSaver
{
    internal class CurrentTimeScreen : TimeScreen
    {
        private readonly bool _display24HourTime;
        private readonly bool _isPreviewMode;
        private readonly bool _showSeconds;
        private readonly bool _flipAnimation;
        private readonly bool _showDate;

        private const int SplitWidth = 4;
        private const double BoxSeparationPercent = 0.05; // ie. 5%

        // How long a single card takes to flip over.
        private const double FlipDurationSeconds = 0.28;

        // Size of each box relative to the "base" box size. 1.0 = full size, 0.72 = 72%.
        private readonly double _hoursScale;
        private readonly double _minutesScale;
        private readonly double _secondsScale;

        private Font _hoursFont;
        private Font _minutesFont;
        private Font _secondsFont;
        private Font _smallFont;
        private Font _dateFont;

        private Font HoursFont => _hoursFont ?? (_hoursFont = MakeFont(_hoursBoxSize, 85));
        private Font MinutesFont => _minutesFont ?? (_minutesFont = MakeFont(_minutesBoxSize, 85));
        private Font SecondsFont => _secondsFont ?? (_secondsFont = MakeFont(_secondsBoxSize, 85));
        private Font SmallFont => _smallFont ?? (_smallFont = MakeFont(_hoursBoxSize, 9));
        // The date is in Chinese, so it needs a system font with CJK glyphs (the embedded Helvetica has none).
        private Font DateFont => _dateFont ?? (_dateFont = new Font("Microsoft YaHei", _dateFontSize, FontStyle.Regular, GraphicsUnit.Pixel));

        private Font MakeFont(int boxSize, int percent)
        {
            return new Font(FontFamily, boxSize.Percent(percent), FontStyle.Bold, GraphicsUnit.Pixel);
        }

        private readonly Brush _fontBrush = new SolidBrush(Color.FromArgb(255, 183, 183, 183));
        private readonly Pen _splitPen = new Pen(Color.Black, SplitWidth);

        private readonly int _hoursBoxSize;
        private readonly int _minutesBoxSize;
        private readonly int _secondsBoxSize;
        private readonly int _separatorWidth;

        private readonly Rectangle _hoursRect;
        private readonly Rectangle _minutesRect;
        private readonly Rectangle _secondsRect;
        private readonly Rectangle _dateRect;
        private readonly int _dateFontSize;

        public CurrentTimeScreen(Control form, bool display24HourTime, bool isPreviewMode, int scalePercent,
            bool showSeconds, int hoursScalePercent, int minutesScalePercent, int secondsScalePercent,
            bool flipAnimation, bool showDate)
        {
            _display24HourTime = display24HourTime;
            _isPreviewMode = isPreviewMode;
            _showSeconds = showSeconds;
            _flipAnimation = flipAnimation;
            _showDate = showDate;
            _form = form;

            // Clamp each scale to a sensible range so a stray setting can't make a box vanish or overflow.
            _hoursScale = ClampScale(hoursScalePercent);
            _minutesScale = ClampScale(minutesScalePercent);
            _secondsScale = ClampScale(secondsScalePercent);

            // The border is between 5% and 30% of the screen
            //  * A scale of 0 = 5%
            //  * A scale of 100 = 30%
            var borderPercent = (100 - scalePercent) / 4 + 5;
            var borderW = form.Width.Percent(borderPercent);
            var borderH = form.Height.Percent(borderPercent);
            var remainingWidth = form.Width - (borderW * 2);
            var remainingHeight = form.Height - (borderH * 2);

            // Pick the largest "base" box size (the size a scale-1.0 box would be) that fits both the
            // available width (hours + minutes + seconds + separators) and the available height (the
            // tallest box), leaving a slice at the bottom for the date.
            var separators = _showSeconds ? BoxSeparationPercent * 2 : BoxSeparationPercent;
            var widthParts = _hoursScale + _minutesScale + (_showSeconds ? _secondsScale : 0) + separators;
            var baseFromWidth = remainingWidth / widthParts;

            var dateFraction = _showDate ? 0.16 : 0.0;
            var clockHeightBudget = remainingHeight * (1 - dateFraction);
            var maxScale = Math.Max(_hoursScale, Math.Max(_minutesScale, _showSeconds ? _secondsScale : 0));
            var baseFromHeight = clockHeightBudget / maxScale;

            var baseSize = Math.Min(baseFromWidth, baseFromHeight);

            _hoursBoxSize = (int)Math.Round(baseSize * _hoursScale);
            _minutesBoxSize = (int)Math.Round(baseSize * _minutesScale);
            _secondsBoxSize = _showSeconds ? (int)Math.Round(baseSize * _secondsScale) : 0;
            _separatorWidth = (int)Math.Round(baseSize * BoxSeparationPercent);

            // Lay the boxes out in a single row, all aligned along a common bottom line. The smaller
            // seconds box therefore sits at the bottom-right, its base level with the hours/minutes boxes.
            var maxBoxSize = Math.Max(_hoursBoxSize, Math.Max(_minutesBoxSize, _secondsBoxSize));
            var rowTop = borderH + (int)((clockHeightBudget - maxBoxSize) / 2);
            var rowBottom = rowTop + maxBoxSize;

            var totalWidth = _hoursBoxSize + _separatorWidth + _minutesBoxSize
                + (_showSeconds ? _separatorWidth + _secondsBoxSize : 0);
            var startingX = (form.Width - totalWidth) / 2;

            _hoursRect = new Rectangle(startingX, rowBottom - _hoursBoxSize, _hoursBoxSize, _hoursBoxSize);
            var minutesX = startingX + _hoursBoxSize + _separatorWidth;
            _minutesRect = new Rectangle(minutesX, rowBottom - _minutesBoxSize, _minutesBoxSize, _minutesBoxSize);

            if (_showSeconds)
            {
                var secondsX = _minutesRect.Right + _separatorWidth;
                _secondsRect = new Rectangle(secondsX, rowBottom - _secondsBoxSize, _secondsBoxSize, _secondsBoxSize);
            }

            if (_showDate)
            {
                var dateBottom = Math.Max(rowBottom + 8, form.Height - borderH);
                _dateRect = Rectangle.FromLTRB(0, rowBottom, form.Width, dateBottom);
                // Size by the available height, but then shrink so the whole line fits the width on one
                // row (otherwise a wide-but-short region produces a huge font that wraps onto two lines).
                var byHeight = Math.Max(12, (int)((dateBottom - rowBottom) * 0.42));
                _dateFontSize = FitDateFontSize(byHeight, form.Width);
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

        protected override void DrawCore()
        {
            var now = SystemTime.Now;
            var durationMs = FlipDurationSeconds * 1000;

            // Hours
            var prevHour = now.AddHours(-1);
            var hoursCur = _display24HourTime ? now.ToString("HH") : now.ToString("%h");
            var hoursPrev = _display24HourTime ? prevHour.ToString("HH") : prevHour.ToString("%h");
            var hoursMs = (now.Minute * 60 + now.Second) * 1000.0 + now.Millisecond;
            DrawFlipBox(_hoursRect, HoursFont, hoursCur, hoursPrev, Math.Min(1.0, hoursMs / durationMs));
            if (!_display24HourTime)
                DrawAmPm(_hoursRect, now);

            // Minutes
            var minutesMs = now.Second * 1000.0 + now.Millisecond;
            DrawFlipBox(_minutesRect, MinutesFont, now.ToString("mm"), now.AddMinutes(-1).ToString("mm"), Math.Min(1.0, minutesMs / durationMs));

            // Seconds (small box in the bottom-right corner of the minutes box)
            if (_showSeconds)
            {
                DrawFlipBox(_secondsRect, SecondsFont, now.ToString("ss"), now.AddSeconds(-1).ToString("ss"), Math.Min(1.0, now.Millisecond / durationMs));
            }

            if (_showDate)
                DrawDate(now);
        }

        private void DrawFlipBox(Rectangle rect, Font font, string currentText, string previousText, double progress)
        {
            // Static (no animation): just the current value, like the classic non-animated render.
            if (!_flipAnimation || progress >= 1.0 || currentText == previousText)
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
            using (var brush = new LinearGradientBrush(rect, BackColorTop, BackColorBottom, LinearGradientMode.Vertical))
            {
                Gfx.FillPath(brush, path);
            }
        }

        private void DrawSplit(Rectangle rect)
        {
            if (!_isPreviewMode)
            {
                var y = rect.Y + (rect.Height / 2) - (SplitWidth / 2);
                Gfx.DrawLine(_splitPen, rect.Left, y, rect.Right, y);
            }
            else
            {
                var y = rect.Y + (rect.Height / 2);
                Gfx.DrawLine(Pens.Black, rect.Left, y, rect.Right, y);
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

        private static string WeekdayText(DateTime now)
        {
            string[] names = { "日", "一", "二", "三", "四", "五", "六" };
            return "星期" + names[(int)now.DayOfWeek];
        }

        // True while a card is part-way through a flip. The desktop clock uses this to stay completely
        // idle (burning no CPU) except during the brief moments something is actually animating.
        internal bool IsFlipActive(DateTime now)
        {
            if (!_flipAnimation)
                return false;
            var durationMs = FlipDurationSeconds * 1000;
            if (_showSeconds)
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
            _fontBrush?.Dispose();
            _splitPen?.Dispose();
            base.DisposeResources();
        }
    }
}

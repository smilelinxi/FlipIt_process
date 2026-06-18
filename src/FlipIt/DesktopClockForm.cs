using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenSaver
{
    /// <summary>
    /// A small, borderless flip clock for the desktop. It reuses the very same renderer as the
    /// screensaver's current-time screen, but lives in an ordinary window that can be dragged, resized
    /// and minimised to the system tray.
    ///
    /// It has its own settings (<see cref="DesktopClockSettings"/>, stored in DesktopClock.ini),
    /// completely independent of the screensaver's Settings.ini, plus a set of widget-style window
    /// behaviours: stay on top, pin to the desktop bottom (under all other windows), click-through,
    /// transparent / white / black / follow-system background, autostart with Windows.
    ///
    /// Kept deliberately lightweight: when the flip animation is off it only repaints when the
    /// displayed value actually changes. When the animation is on the timer wakes at frame-rate, but a
    /// frame is only *drawn* during the &lt;0.3s a card is physically flipping (see
    /// <see cref="CurrentTimeScreen.IsFlipActive"/>).
    ///
    /// This form is launched either by passing "/d" on the command line, or simply by running an
    /// executable whose name marks it as the clock (e.g. 桌面时钟.exe). It does not affect the
    /// screensaver (/s), preview (/p) or configuration (/c) modes in any way.
    /// </summary>
    public class DesktopClockForm : Form
    {
        private readonly DesktopClockSettings _settings;
        private CurrentTimeScreen _screen;
        private ClockColors _colors;
        private readonly Timer _timer = new Timer();
        private int _lastSecond = -1;
        private int _lastMinute = -1;
        private string _lastWeather;
        private FormWindowState _lastWindowState = FormWindowState.Normal;

        // Notification-area (system tray) icon + the menu shared by the icon and the window body.
        // When click-through is on the window ignores the mouse entirely, so the tray icon is then the
        // only way to reach the menu and turn it back off.
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _topMostItem;
        private ToolStripMenuItem _desktopBottomItem;
        private ToolStripMenuItem _clickThroughItem;
        private ToolStripMenuItem _edgeSnapItem;
        private ToolStripMenuItem _autoStartItem;
        private ToolStripMenuItem[] _backgroundItems;   // indexed by (int)ClockBackgroundMode

        // The colour that becomes see-through in transparent mode. Near-black, so the anti-aliased
        // edges of the dark cards blend toward it without visible fringes.
        private static readonly Color TransparencyKeyColor = Color.FromArgb(1, 1, 1);

        private static string SettingsFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlipIt");

        // Where the window's size/position is remembered between runs ([Window] section of the same
        // DesktopClock.ini that holds the clock's own settings).
        private static string BoundsFilePath => DesktopClockSettings.FilePath;

        public DesktopClockForm()
        {
            _settings = DesktopClockSettings.Load();
            WeatherService.SetCity(_settings.WeatherCity);
            _colors = ResolveColors();

            Text = "FlipIt 桌面时钟";
            FormBorderStyle = FormBorderStyle.None;   // no title bar (drag/resize handled in WndProc below)
            ShowInTaskbar = false;                    // not on the taskbar; it lives in the system tray instead
            MinimumSize = new Size(240, 140);
            DoubleBuffered = true;                    // avoid flicker while the window is resized
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;

            LoadWindowBounds();
            BuildContextMenu();
            ApplyAppearance();

            Load += (s, e) => { SetupTrayIcon(); ApplyWindowBehaviour(); PaintTime(); };
            Paint += (s, e) => PaintTime();
            ResizeEnd += (s, e) => RebuildForCurrentSize();   // fires once, when the user lets go of the edge
            Resize += DesktopClockForm_Resize;                // catches restore-from-minimised
            KeyDown += DesktopClockForm_KeyDown;
            MouseDown += DesktopClockForm_MouseDown;
            FormClosing += (s, e) => SaveWindowBounds();
            FormClosed += DesktopClockForm_FormClosed;

            _timer.Tick += Tick;
            _timer.Interval = TimerInterval();
            _timer.Start();
        }

        // 16ms (~60fps wake) only when the flip animation can run; otherwise a slow, cheap tick that is
        // just frequent enough to keep the per-second content (seconds box, CPU readout) current.
        private int TimerInterval()
        {
            if (_settings.FlipAnimation) return 16;
            return (_settings.ShowSeconds || _settings.ShowSystemInfo) ? 250 : 1000;
        }

        #region Win32 / window behaviour (drag, resize, click-through, desktop bottom)

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;

        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        // How close (px) an edge must get to a screen edge before it snaps flush to it.
        private const int SnapDistance = 20;

        // Nudge the proposed drag rectangle flush against any working-area edge it is hovering near.
        // Returns true if it changed the rectangle.
        private static bool SnapToEdges(ref RECT rc)
        {
            var width = rc.Right - rc.Left;
            var height = rc.Bottom - rc.Top;

            // Snap relative to the screen the window's centre is currently over.
            var center = new Point(rc.Left + width / 2, rc.Top + height / 2);
            var area = Screen.FromPoint(center).WorkingArea;

            var snapped = false;

            if (Math.Abs(rc.Left - area.Left) <= SnapDistance)
            {
                rc.Left = area.Left;
                snapped = true;
            }
            else if (Math.Abs(area.Right - rc.Right) <= SnapDistance)
            {
                rc.Left = area.Right - width;
                snapped = true;
            }

            if (Math.Abs(rc.Top - area.Top) <= SnapDistance)
            {
                rc.Top = area.Top;
                snapped = true;
            }
            else if (Math.Abs(area.Bottom - rc.Bottom) <= SnapDistance)
            {
                rc.Top = area.Bottom - height;
                snapped = true;
            }

            rc.Right = rc.Left + width;
            rc.Bottom = rc.Top + height;
            return snapped;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int WM_WINDOWPOSCHANGING = 0x0046;
            const int WM_SETTINGCHANGE = 0x001A;
            const int WM_MOVING = 0x0216;
            const int HTCLIENT = 1;

            // While the window is being dragged, snap it flush to a nearby screen edge.
            if (m.Msg == WM_MOVING && _settings != null && _settings.EdgeSnap)
            {
                var rc = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                if (SnapToEdges(ref rc))
                    Marshal.StructureToPtr(rc, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }

            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                // Turn the outer few pixels into resize grips; leave the rest as a normal client area so
                // right-click still raises the context menu and a left-press can start a move.
                if ((int)m.Result == HTCLIENT)
                    m.Result = (IntPtr)HitTest(m.LParam);
                return;
            }

            // "Pin to desktop": whatever tries to raise this window, push it back to the bottom of the
            // z-order, so it behaves like part of the wallpaper.
            if (m.Msg == WM_WINDOWPOSCHANGING && _settings != null && _settings.DesktopBottom)
            {
                var wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
                wp.hwndInsertAfter = HWND_BOTTOM;
                wp.flags &= ~SWP_NOZORDER;
                Marshal.StructureToPtr(wp, m.LParam, false);
            }

            // Follow the system light/dark theme live.
            if (m.Msg == WM_SETTINGCHANGE && _settings != null
                && _settings.BackgroundMode == ClockBackgroundMode.FollowSystem
                && Marshal.PtrToStringAuto(m.LParam) == "ImmersiveColorSet")
            {
                BeginInvoke((Action)ApplyAppearance);
            }

            base.WndProc(ref m);
        }

        private int HitTest(IntPtr lParam)
        {
            const int grip = 6;
            const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                      HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

            var x = unchecked((short)(long)lParam);
            var y = unchecked((short)((long)lParam >> 16));
            var p = PointToClient(new Point(x, y));

            bool left = p.X <= grip, right = p.X >= ClientSize.Width - grip;
            bool top = p.Y <= grip, bottom = p.Y >= ClientSize.Height - grip;

            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
            return HTCLIENT;
        }

        // Left-press anywhere on the body drags the whole window (there is no title bar to grab).
        private void DesktopClockForm_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                const int WM_NCLBUTTONDOWN = 0xA1;
                const int HTCAPTION = 2;
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
        }

        private void SetClickThrough(bool enabled)
        {
            if (!IsHandleCreated)
                return;
            var exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
            exStyle = enabled
                ? exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED
                : exStyle & ~WS_EX_TRANSPARENT;
            SetWindowLong(Handle, GWL_EXSTYLE, exStyle);
        }

        private void ApplyWindowBehaviour()
        {
            TopMost = _settings.TopMost;
            SetClickThrough(_settings.ClickThrough);
            if (_settings.DesktopBottom && IsHandleCreated)
                SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        #endregion

        #region Appearance (background mode / theme)

        private ClockColors ResolveColors()
        {
            switch (_settings.BackgroundMode)
            {
                case ClockBackgroundMode.White:
                    return ClockColors.Light();
                case ClockBackgroundMode.Transparent:
                {
                    var colors = ClockColors.Dark();
                    colors.Background = TransparencyKeyColor;
                    return colors;
                }
                case ClockBackgroundMode.FollowSystem:
                    return SystemThemeDetector.IsLightTheme() ? ClockColors.Light() : ClockColors.Dark();
                default:
                    return ClockColors.Dark();
            }
        }

        private void ApplyAppearance()
        {
            _colors = ResolveColors();
            if (_settings.BackgroundMode == ClockBackgroundMode.Transparent)
            {
                BackColor = TransparencyKeyColor;
                TransparencyKey = TransparencyKeyColor;
            }
            else
            {
                TransparencyKey = Color.Empty;
                BackColor = _colors.Background;
            }
            RebuildForCurrentSize();
        }

        #endregion

        private void DesktopClockForm_Resize(object sender, EventArgs e)
        {
            // ResizeEnd handles interactive edge-drags. Here we only need to react to restore from a
            // minimised state, which changes the client size without ever raising ResizeEnd.
            if (WindowState != _lastWindowState)
            {
                _lastWindowState = WindowState;
                if (WindowState != FormWindowState.Minimized)
                    RebuildForCurrentSize();
            }
        }

        private void RebuildForCurrentSize()
        {
            // The renderer computes its card layout from the form size in its constructor, so a resize
            // means building a fresh one. Dispose the old one first (it owns fonts/bitmaps) to avoid
            // leaking GDI handles across many resizes.
            _screen?.DisposeResources();
            _screen = null;
            _lastSecond = _lastMinute = -1;
            PaintTime();
        }

        private void PaintTime()
        {
            if (!Visible || WindowState == FormWindowState.Minimized) return;
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            try
            {
                if (_screen == null)
                {
                    _screen = new CurrentTimeScreen(this, new ClockRenderOptions
                    {
                        Display24HrTime = _settings.Display24HrTime,
                        IsPreviewMode = false,
                        ScalePercent = _settings.Scale,
                        ShowSeconds = _settings.ShowSeconds,
                        HoursScalePercent = _settings.HoursScale,
                        MinutesScalePercent = _settings.MinutesScale,
                        SecondsScalePercent = _settings.SecondsScale,
                        FlipAnimation = _settings.FlipAnimation,
                        ShowDate = _settings.ShowDate,
                        ShowWeather = _settings.ShowWeather,
                        ShowSystemInfo = _settings.ShowSystemInfo,
                        Colors = _colors,
                    });
                }
                _screen.Draw();
            }
            catch
            {
                // A transient drawing error (e.g. mid-resize) must never crash the clock; the next
                // tick repaints cleanly.
            }
        }

        private void Tick(object sender, EventArgs e)
        {
            if (!Visible) return;
            var now = SystemTime.Now;

            // Mid-flip: redraw every tick so the animation is smooth.
            if (_screen != null && _screen.IsFlipActive(now))
            {
                _lastSecond = now.Second;
                _lastMinute = now.Minute;
                PaintTime();
                return;
            }

            // A finished weather fetch should show up promptly, not at the next minute rollover.
            if (_settings.ShowWeather)
            {
                var weather = WeatherService.GetDisplayText();
                if (weather != _lastWeather)
                {
                    _lastWeather = weather;
                    _lastSecond = now.Second;
                    _lastMinute = now.Minute;
                    PaintTime();
                    return;
                }
            }

            // Idle: only repaint when the visible value actually changes. The CPU/memory readout is
            // per-second content, just like the seconds box.
            if (_settings.ShowSeconds || _settings.ShowSystemInfo)
            {
                if (now.Second != _lastSecond)
                {
                    _lastSecond = now.Second;
                    _lastMinute = now.Minute;
                    PaintTime();
                }
            }
            else if (now.Minute != _lastMinute)
            {
                _lastMinute = now.Minute;
                PaintTime();
            }
        }

        private void DesktopClockForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
                Close();
        }

        private void DesktopClockForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;   // remove it from the tray immediately
                _trayIcon.Dispose();
            }
            _screen?.DisposeResources();
            _timer.Dispose();
        }

        #region Context menu

        private void BuildContextMenu()
        {
            _menu = new ContextMenuStrip();

            var showHideItem = new ToolStripMenuItem("显示 / 隐藏窗口");
            showHideItem.Click += (s, e) => ToggleVisible();
            _menu.Items.Add(showHideItem);

            _menu.Items.Add(new ToolStripSeparator());

            _topMostItem = new ToolStripMenuItem("窗口置顶") { CheckOnClick = true };
            _topMostItem.CheckedChanged += (s, e) =>
            {
                if (_topMostItem.Checked == _settings.TopMost) return;
                _settings.TopMost = _topMostItem.Checked;
                if (_settings.TopMost) _settings.DesktopBottom = false;   // the two are opposites
                SaveAndApply();
            };
            _menu.Items.Add(_topMostItem);

            _desktopBottomItem = new ToolStripMenuItem("置于桌面底层") { CheckOnClick = true };
            _desktopBottomItem.CheckedChanged += (s, e) =>
            {
                if (_desktopBottomItem.Checked == _settings.DesktopBottom) return;
                _settings.DesktopBottom = _desktopBottomItem.Checked;
                if (_settings.DesktopBottom) _settings.TopMost = false;
                SaveAndApply();
            };
            _menu.Items.Add(_desktopBottomItem);

            _clickThroughItem = new ToolStripMenuItem("鼠标穿透") { CheckOnClick = true };
            _clickThroughItem.CheckedChanged += (s, e) =>
            {
                if (_clickThroughItem.Checked == _settings.ClickThrough) return;
                _settings.ClickThrough = _clickThroughItem.Checked;
                SaveAndApply();
                if (_settings.ClickThrough)
                    _trayIcon?.ShowBalloonTip(3000, "FlipIt 桌面时钟",
                        "鼠标穿透已开启：窗口不再响应鼠标。可通过托盘图标右键菜单关闭。", ToolTipIcon.Info);
            };
            _menu.Items.Add(_clickThroughItem);

            _edgeSnapItem = new ToolStripMenuItem("边缘吸附") { CheckOnClick = true };
            _edgeSnapItem.CheckedChanged += (s, e) =>
            {
                if (_edgeSnapItem.Checked == _settings.EdgeSnap) return;
                _settings.EdgeSnap = _edgeSnapItem.Checked;
                SaveAndApply();
            };
            _menu.Items.Add(_edgeSnapItem);

            var backgroundMenu = new ToolStripMenuItem("背景");
            string[] backgroundNames = { "黑色", "白色", "透明", "跟随系统" };
            _backgroundItems = new ToolStripMenuItem[backgroundNames.Length];
            for (var i = 0; i < backgroundNames.Length; i++)
            {
                var mode = (ClockBackgroundMode)i;
                var item = new ToolStripMenuItem(backgroundNames[i]);
                item.Click += (s, e) =>
                {
                    _settings.BackgroundMode = mode;
                    SaveAndApply();
                };
                _backgroundItems[i] = item;
                backgroundMenu.DropDownItems.Add(item);
            }
            _menu.Items.Add(backgroundMenu);

            _menu.Items.Add(new ToolStripSeparator());

            _autoStartItem = new ToolStripMenuItem("开机自启动") { CheckOnClick = true };
            _autoStartItem.CheckedChanged += (s, e) =>
            {
                if (_autoStartItem.Checked == AutoStart.IsEnabled()) return;
                AutoStart.SetEnabled(_autoStartItem.Checked);
                SyncMenuChecks();   // re-read, in case the registry write failed
            };
            _menu.Items.Add(_autoStartItem);

            var resetPositionItem = new ToolStripMenuItem("重置位置");
            resetPositionItem.Click += (s, e) => ResetPosition();
            _menu.Items.Add(resetPositionItem);

            var settingsItem = new ToolStripMenuItem("时钟设置…");
            settingsItem.Click += (s, e) => OpenSettings();
            _menu.Items.Add(settingsItem);

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (s, e) => Close();
            _menu.Items.Add(exitItem);

            SyncMenuChecks();

            // The same menu serves both the window body (right-click) and the tray icon (right-click).
            ContextMenuStrip = _menu;
        }

        // Make every checkable menu item reflect the actual current state (settings + registry).
        private void SyncMenuChecks()
        {
            _topMostItem.Checked = _settings.TopMost;
            _desktopBottomItem.Checked = _settings.DesktopBottom;
            _clickThroughItem.Checked = _settings.ClickThrough;
            _edgeSnapItem.Checked = _settings.EdgeSnap;
            _autoStartItem.Checked = AutoStart.IsEnabled();
            for (var i = 0; i < _backgroundItems.Length; i++)
                _backgroundItems[i].Checked = (int)_settings.BackgroundMode == i;
        }

        private void SaveAndApply()
        {
            _settings.Save();
            WeatherService.SetCity(_settings.WeatherCity);
            _timer.Interval = TimerInterval();
            ApplyAppearance();
            ApplyWindowBehaviour();
            SyncMenuChecks();
        }

        // Centre the window on the primary screen at its default size — the escape hatch for a window
        // that was dragged off-screen or resized into something unusable.
        public void ResetPosition()
        {
            Size = new Size(520, 240);
            CenterOnPrimaryScreen();
            RebuildForCurrentSize();
        }

        private void OpenSettings()
        {
            using (var dlg = new DesktopClockSettingsForm(_settings, SaveAndApply, ResetPosition))
            {
                dlg.StartPosition = FormStartPosition.CenterScreen;
                dlg.ShowDialog(this);
            }
        }

        #endregion

        #region System-tray icon

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private void SetupTrayIcon()
        {
            _trayIcon = new NotifyIcon
            {
                Text = "FlipIt 桌面时钟",
                Icon = CreateClockIcon(),
                Visible = true,
                ContextMenuStrip = _menu,
            };
            // Left-click toggles show/hide; right-click uses the shared context menu automatically.
            _trayIcon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleVisible(); };
        }

        private void ToggleVisible()
        {
            if (Visible)
                HideToTray();
            else
                ShowFromTray();
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            if (!_settings.DesktopBottom)
                Activate();
            _lastSecond = _lastMinute = -1;
            PaintTime();
            _timer.Start();
        }

        private void HideToTray()
        {
            _timer.Stop();   // nothing to draw while hidden -> use no CPU at all
            Hide();
        }

        // Build a small flip-clock-style tray icon at runtime (the project ships no .ico file).
        private static Icon CreateClockIcon()
        {
            try
            {
                using (var bmp = new Bitmap(32, 32))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);

                        var rect = new Rectangle(3, 7, 26, 18);
                        using (var path = RoundedRectangle.Create(rect, 4))
                        using (var brush = new LinearGradientBrush(rect,
                                   Color.FromArgb(60, 60, 60), Color.FromArgb(20, 20, 20),
                                   LinearGradientMode.Vertical))
                            g.FillPath(brush, path);

                        // the flip-clock centre split
                        using (var pen = new Pen(Color.Black, 2))
                            g.DrawLine(pen, rect.Left, rect.Top + rect.Height / 2,
                                            rect.Right, rect.Top + rect.Height / 2);

                        // a light colon so it reads as a clock even at 16px
                        using (var dot = new SolidBrush(Color.FromArgb(210, 210, 210)))
                        {
                            g.FillEllipse(dot, 15, 11, 3, 3);
                            g.FillEllipse(dot, 15, 18, 3, 3);
                        }
                    }

                    var hicon = bmp.GetHicon();
                    try { using (var tmp = Icon.FromHandle(hicon)) return (Icon)tmp.Clone(); }
                    finally { DestroyIcon(hicon); }   // GetHicon leaks the handle unless destroyed
                }
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        #endregion

        #region Window-bounds persistence

        private void LoadWindowBounds()
        {
            var width = 520;
            var height = 240;
            var location = Point.Empty;
            var hasLocation = false;

            try
            {
                if (File.Exists(BoundsFilePath))
                {
                    var ini = new IniFile(BoundsFilePath);
                    width = ini.GetInt("Window", "W", width);
                    height = ini.GetInt("Window", "H", height);
                    var x = ini.GetInt("Window", "X", int.MinValue);
                    var y = ini.GetInt("Window", "Y", int.MinValue);
                    if (x != int.MinValue && y != int.MinValue)
                    {
                        location = new Point(x, y);
                        hasLocation = true;
                    }
                }
            }
            catch
            {
                // Corrupt/unreadable bounds file: just fall back to the defaults below.
            }

            Size = new Size(
                Math.Max(MinimumSize.Width, width),
                Math.Max(MinimumSize.Height, height));

            if (hasLocation && IsVisibleOnAnyScreen(new Rectangle(location, Size)))
                Location = location;
            else
                CenterOnPrimaryScreen();
        }

        private void SaveWindowBounds()
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                    Directory.CreateDirectory(SettingsFolder);
                if (!File.Exists(BoundsFilePath))
                    File.WriteAllText(BoundsFilePath, "");

                // RestoreBounds gives the normal-state rectangle even when minimised.
                var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

                var ini = new IniFile(BoundsFilePath);
                ini.SetInt("Window", "X", b.X);
                ini.SetInt("Window", "Y", b.Y);
                ini.SetInt("Window", "W", b.Width);
                ini.SetInt("Window", "H", b.Height);
                ini.Save();
            }
            catch
            {
                // Failing to remember the window position must not stop the app from closing.
            }
        }

        private static bool IsVisibleOnAnyScreen(Rectangle rect)
        {
            return Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect));
        }

        private void CenterOnPrimaryScreen()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(
                area.X + (area.Width - Width) / 2,
                area.Y + (area.Height - Height) / 2);
        }

        #endregion
    }
}

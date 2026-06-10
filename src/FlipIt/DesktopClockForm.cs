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
    /// and minimised to the taskbar.
    ///
    /// Kept deliberately lightweight: when the flip animation is off it only repaints when the
    /// displayed value actually changes (once a second, or once a minute). When the animation is on
    /// the timer wakes at frame-rate, but a frame is only *drawn* during the &lt;0.3s a card is
    /// physically flipping (see <see cref="CurrentTimeScreen.IsFlipActive"/>); the rest of the time it
    /// just compares the clock value and goes straight back to sleep. So an idle desktop clock costs
    /// essentially nothing.
    ///
    /// It also watches the settings file so that saving from the configuration dialog (even in another
    /// process, e.g. FlipIt.exe) updates the running clock live.
    ///
    /// This form is launched either by passing "/d" on the command line, or simply by running an
    /// executable whose name marks it as the clock (e.g. 桌面时钟.exe). It does not affect the
    /// screensaver (/s), preview (/p) or configuration (/c) modes in any way.
    /// </summary>
    public class DesktopClockForm : Form
    {
        private FlipItSettings _settings;
        private CurrentTimeScreen _screen;
        private readonly Timer _timer = new Timer();
        private int _lastSecond = -1;
        private int _lastMinute = -1;
        private FormWindowState _lastWindowState = FormWindowState.Normal;

        // Watches Settings.ini so the clock updates the moment the settings dialog saves.
        private FileSystemWatcher _settingsWatcher;
        private readonly Timer _reloadDebounce = new Timer();

        // Notification-area (system tray) icon + the menu shared by the icon and the window body.
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _menu;

        private static string SettingsFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlipIt");

        // Where the window's size/position is remembered between runs. Kept separate from the main
        // Settings.ini so it never interferes with the screensaver's settings.
        private static string BoundsFilePath => Path.Combine(SettingsFolder, "DesktopClock.ini");

        public DesktopClockForm(FlipItSettings settings)
        {
            _settings = settings;

            Text = "FlipIt 桌面时钟";
            BackColor = Color.Black;
            FormBorderStyle = FormBorderStyle.None;   // no title bar (drag/resize handled in WndProc below)
            ShowInTaskbar = false;                    // not on the taskbar; it lives in the system tray instead
            MinimumSize = new Size(240, 140);
            DoubleBuffered = true;                    // avoid flicker while the window is resized
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;

            LoadWindowBounds();
            BuildContextMenu();

            Load += (s, e) => { PaintTime(); SetupTrayIcon(); SetupSettingsWatcher(); };
            Paint += (s, e) => PaintTime();
            ResizeEnd += (s, e) => RebuildForCurrentSize();   // fires once, when the user lets go of the edge
            Resize += DesktopClockForm_Resize;                // catches restore-from-minimised
            KeyDown += DesktopClockForm_KeyDown;
            MouseDown += DesktopClockForm_MouseDown;
            FormClosing += (s, e) => SaveWindowBounds();
            FormClosed += DesktopClockForm_FormClosed;

            _reloadDebounce.Interval = 250;
            _reloadDebounce.Tick += (s, e) => { _reloadDebounce.Stop(); ReloadSettings(); };

            _timer.Tick += Tick;
            _timer.Interval = TimerInterval();
            _timer.Start();
        }

        // 16ms (~60fps wake) only when the flip animation can run; otherwise a slow, cheap tick that is
        // just frequent enough to keep the seconds / minutes display current.
        private int TimerInterval()
        {
            if (_settings.FlipAnimation) return 16;
            return _settings.ShowSeconds ? 250 : 1000;
        }

        #region Drag / resize (borderless window)

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTCLIENT = 1;

            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                // Turn the outer few pixels into resize grips; leave the rest as a normal client area so
                // right-click still raises the context menu and a left-press can start a move.
                if ((int)m.Result == HTCLIENT)
                    m.Result = (IntPtr)HitTest(m.LParam);
                return;
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
                    _screen = new CurrentTimeScreen(this, _settings.Display24HrTime, false, _settings.Scale,
                        _settings.ShowSeconds, _settings.HoursScale, _settings.MinutesScale, _settings.SecondsScale,
                        _settings.FlipAnimation, _settings.ShowDate);
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

            // Idle: only repaint when the visible value actually changes.
            if (_settings.ShowSeconds)
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
            _settingsWatcher?.Dispose();
            _reloadDebounce.Dispose();
            _screen?.DisposeResources();
            _timer.Dispose();
        }

        #region Live settings reload

        private void SetupSettingsWatcher()
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                    Directory.CreateDirectory(SettingsFolder);

                _settingsWatcher = new FileSystemWatcher(SettingsFolder, "Settings.ini")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    // Marshal the (background-thread) file events straight onto the UI thread for us.
                    SynchronizingObject = this,
                };
                _settingsWatcher.Changed += OnSettingsFileChanged;
                _settingsWatcher.Created += OnSettingsFileChanged;
                _settingsWatcher.Renamed += OnSettingsFileChanged;
                _settingsWatcher.EnableRaisingEvents = true;
            }
            catch
            {
                // If we can't watch the file the clock simply won't auto-refresh; not fatal.
            }
        }

        // The settings file is often written in a couple of bursts; debounce so we reload once it settles.
        private void OnSettingsFileChanged(object sender, FileSystemEventArgs e)
        {
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        }

        private void ReloadSettings()
        {
            FlipItSettings reloaded;
            try
            {
                reloaded = FlipItSettings.Load(Screen.AllScreens);
            }
            catch
            {
                return;   // file mid-write or locked; the next change event will trigger another reload
            }

            _settings = reloaded;
            _timer.Interval = TimerInterval();
            RebuildForCurrentSize();
        }

        #endregion

        #region Context menu

        private void BuildContextMenu()
        {
            _menu = new ContextMenuStrip();

            var showHideItem = new ToolStripMenuItem("显示 / 隐藏窗口");
            showHideItem.Click += (s, e) => ToggleVisible();
            _menu.Items.Add(showHideItem);

            var topMostItem = new ToolStripMenuItem("窗口置顶") { CheckOnClick = true, Checked = TopMost };
            topMostItem.CheckedChanged += (s, e) => TopMost = topMostItem.Checked;
            _menu.Items.Add(topMostItem);

            _menu.Items.Add(new ToolStripSeparator());

            var settingsItem = new ToolStripMenuItem("设置…");
            settingsItem.Click += (s, e) => OpenSettings();
            _menu.Items.Add(settingsItem);

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (s, e) => Close();
            _menu.Items.Add(exitItem);

            // The same menu serves both the window body (right-click) and the tray icon (right-click).
            ContextMenuStrip = _menu;
        }

        private void OpenSettings()
        {
            using (var dlg = new SettingsForm(_settings))
            {
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ShowDialog(this);
            }
            // The dialog saves to disk on OK; the file-watcher will pick that up and refresh the clock.
            // Reload here too in case the watcher is unavailable.
            ReloadSettings();
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
            var topMost = false;

            try
            {
                if (File.Exists(BoundsFilePath))
                {
                    var ini = new IniFile(BoundsFilePath);
                    width = ini.GetInt("Window", "W", width);
                    height = ini.GetInt("Window", "H", height);
                    var x = ini.GetInt("Window", "X", int.MinValue);
                    var y = ini.GetInt("Window", "Y", int.MinValue);
                    topMost = ini.GetBool("Window", "TopMost", false);
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

            TopMost = topMost;
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
                ini.SetBool("Window", "TopMost", TopMost);
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

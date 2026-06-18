using System;
using System.Drawing;
using System.Windows.Forms;

namespace ScreenSaver
{
    /// <summary>
    /// Settings dialog for the desktop clock only. The screensaver keeps its own, separate dialog
    /// (<see cref="SettingsForm"/>) and its own Settings.ini; nothing here touches it.
    ///
    /// Built in code rather than with the WinForms designer — it is a simple, fixed dialog and this
    /// keeps it in one readable file.
    /// </summary>
    public class DesktopClockSettingsForm : Form
    {
        private readonly DesktopClockSettings _settings;
        private readonly Action _applyCallback;     // persists + applies the settings on the clock window
        private readonly Action _resetPosition;

        private RadioButton _format12Radio;
        private RadioButton _format24Radio;
        private CheckBox _showSecondsCheck;
        private CheckBox _flipAnimationCheck;
        private CheckBox _showDateCheck;
        private CheckBox _showWeatherCheck;
        private TextBox _weatherCityText;
        private CheckBox _showSystemInfoCheck;
        private TrackBar _scaleTrackBar;
        private NumericUpDown _hoursScaleUpDown;
        private NumericUpDown _minutesScaleUpDown;
        private NumericUpDown _secondsScaleUpDown;
        private ComboBox _backgroundCombo;
        private CheckBox _topMostCheck;
        private CheckBox _desktopBottomCheck;
        private CheckBox _clickThroughCheck;
        private CheckBox _edgeSnapCheck;
        private CheckBox _autoStartCheck;

        public DesktopClockSettingsForm(DesktopClockSettings settings, Action applyCallback, Action resetPosition)
        {
            _settings = settings;
            _applyCallback = applyCallback;
            _resetPosition = resetPosition;

            Text = "桌面时钟设置";
            Font = new Font("Microsoft YaHei UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BuildControls();
            LoadFromSettings();
        }

        private void BuildControls()
        {
            const int left = 24;
            const int indent = 40;
            const int col2 = indent + 180;
            var y = 16;

            // --- 时间显示 ---
            y = AddSectionLabel("时间显示", left, y);

            var formatLabel = new Label { Text = "时间格式：", Location = new Point(indent, y + 3), AutoSize = true };
            // Radios that are direct children of the form would group with every other radio on it;
            // a panel scopes these two to each other.
            var formatPanel = new Panel { Location = new Point(indent + 75, y - 3), Size = new Size(220, 26) };
            _format12Radio = new RadioButton { Text = "12 小时", Location = new Point(5, 3), AutoSize = true };
            _format24Radio = new RadioButton { Text = "24 小时", Location = new Point(95, 3), AutoSize = true };
            formatPanel.Controls.Add(_format12Radio);
            formatPanel.Controls.Add(_format24Radio);
            Controls.Add(formatLabel);
            Controls.Add(formatPanel);
            y += 30;

            _showSecondsCheck = AddCheck("显示秒", indent, y);
            _flipAnimationCheck = AddCheck("翻页动画", col2, y);
            y += 26;
            _showDateCheck = AddCheck("显示日期(农历)", indent, y);
            _showWeatherCheck = AddCheck("显示天气", col2, y);
            y += 26;
            _showSystemInfoCheck = AddCheck("显示系统信息 (CPU / 内存)", indent, y);
            y += 26;

            var cityLabel = new Label { Text = "天气城市：", Location = new Point(indent, y + 4), AutoSize = true };
            _weatherCityText = new TextBox { Location = new Point(indent + 80, y), Width = 160 };
            var cityHint = new Label { Text = "（留空自动定位）", Location = new Point(indent + 248, y + 4), AutoSize = true, ForeColor = SystemColors.GrayText };
            Controls.Add(cityLabel);
            Controls.Add(_weatherCityText);
            Controls.Add(cityHint);
            y += 32;

            // --- 大小 ---
            y = AddSectionLabel("大小", left, y);
            var scaleLabel = new Label { Text = "整体大小：", Location = new Point(indent, y + 6), AutoSize = true };
            _scaleTrackBar = new TrackBar
            {
                Location = new Point(indent + 80, y),
                Size = new Size(240, 40),
                Minimum = 1, Maximum = 10, LargeChange = 1,
            };
            Controls.Add(scaleLabel);
            Controls.Add(_scaleTrackBar);
            y += 46;

            var boxLabel = new Label { Text = "框大小 %：", Location = new Point(indent, y + 4), AutoSize = true };
            Controls.Add(boxLabel);
            _hoursScaleUpDown = AddBoxScale("时", indent + 80, y);
            _minutesScaleUpDown = AddBoxScale("分", indent + 180, y);
            _secondsScaleUpDown = AddBoxScale("秒", indent + 280, y);
            y += 40;

            // --- 背景 ---
            y = AddSectionLabel("背景", left, y);
            var bgLabel = new Label { Text = "背景颜色：", Location = new Point(indent, y + 4), AutoSize = true };
            _backgroundCombo = new ComboBox
            {
                Location = new Point(indent + 80, y),
                Width = 160,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            _backgroundCombo.Items.AddRange(new object[] { "黑色", "白色", "透明", "跟随系统" });
            Controls.Add(bgLabel);
            Controls.Add(_backgroundCombo);
            y += 40;

            // --- 窗口 ---
            y = AddSectionLabel("窗口", left, y);
            _topMostCheck = AddCheck("窗口置顶", indent, y);
            _desktopBottomCheck = AddCheck("置于桌面底层", col2, y);
            y += 26;
            // Top-most and pinned-to-bottom are mutually exclusive; ticking one unticks the other.
            _topMostCheck.CheckedChanged += (s, e) => { if (_topMostCheck.Checked) _desktopBottomCheck.Checked = false; };
            _desktopBottomCheck.CheckedChanged += (s, e) => { if (_desktopBottomCheck.Checked) _topMostCheck.Checked = false; };

            _clickThroughCheck = AddCheck("鼠标穿透（窗口不响应鼠标，可从托盘菜单关闭）", indent, y);
            y += 26;
            _edgeSnapCheck = AddCheck("边缘吸附（拖到屏幕边缘自动贴合）", indent, y);
            y += 26;
            _autoStartCheck = AddCheck("开机自启动", indent, y);
            y += 30;

            var resetButton = new Button { Text = "重置窗口位置", Location = new Point(indent, y), Size = new Size(110, 28) };
            resetButton.Click += (s, e) => _resetPosition?.Invoke();
            Controls.Add(resetButton);
            y += 44;

            // --- bottom buttons ---
            var okButton = new Button { Text = "确定", Size = new Size(80, 28) };
            var cancelButton = new Button { Text = "取消", Size = new Size(80, 28), DialogResult = DialogResult.Cancel };
            var applyButton = new Button { Text = "应用", Size = new Size(80, 28) };
            ClientSize = new Size(420, y + 28 + 16);
            okButton.Location = new Point(ClientSize.Width - 3 * 88 - 16, y);
            cancelButton.Location = new Point(ClientSize.Width - 2 * 88 - 16, y);
            applyButton.Location = new Point(ClientSize.Width - 88 - 16, y);
            okButton.Click += (s, e) => { Apply(); Close(); };
            applyButton.Click += (s, e) => Apply();
            Controls.Add(okButton);
            Controls.Add(cancelButton);
            Controls.Add(applyButton);
            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private int AddSectionLabel(string text, int left, int y)
        {
            var label = new Label
            {
                Text = text,
                Location = new Point(left, y),
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
            };
            Controls.Add(label);
            return y + 26;
        }

        private CheckBox AddCheck(string text, int left, int y)
        {
            var check = new CheckBox { Text = text, Location = new Point(left, y), AutoSize = true };
            Controls.Add(check);
            return check;
        }

        private NumericUpDown AddBoxScale(string labelText, int left, int y)
        {
            var label = new Label { Text = labelText, Location = new Point(left, y + 4), AutoSize = true };
            var upDown = new NumericUpDown
            {
                Location = new Point(left + 24, y),
                Width = 60,
                Minimum = 10, Maximum = 150, Increment = 5, Value = 100,
            };
            Controls.Add(label);
            Controls.Add(upDown);
            return upDown;
        }

        private void LoadFromSettings()
        {
            _format24Radio.Checked = _settings.Display24HrTime;
            _format12Radio.Checked = !_settings.Display24HrTime;
            _showSecondsCheck.Checked = _settings.ShowSeconds;
            _flipAnimationCheck.Checked = _settings.FlipAnimation;
            _showDateCheck.Checked = _settings.ShowDate;
            _showWeatherCheck.Checked = _settings.ShowWeather;
            _weatherCityText.Text = _settings.WeatherCity ?? "";
            _showSystemInfoCheck.Checked = _settings.ShowSystemInfo;
            _scaleTrackBar.Value = Math.Min(_scaleTrackBar.Maximum, Math.Max(_scaleTrackBar.Minimum, _settings.Scale / 10));
            _hoursScaleUpDown.Value = ClampToRange(_hoursScaleUpDown, _settings.HoursScale);
            _minutesScaleUpDown.Value = ClampToRange(_minutesScaleUpDown, _settings.MinutesScale);
            _secondsScaleUpDown.Value = ClampToRange(_secondsScaleUpDown, _settings.SecondsScale);
            _backgroundCombo.SelectedIndex = Math.Min(_backgroundCombo.Items.Count - 1,
                Math.Max(0, (int)_settings.BackgroundMode));
            _topMostCheck.Checked = _settings.TopMost;
            _desktopBottomCheck.Checked = _settings.DesktopBottom;
            _clickThroughCheck.Checked = _settings.ClickThrough;
            _edgeSnapCheck.Checked = _settings.EdgeSnap;
            _autoStartCheck.Checked = AutoStart.IsEnabled();
        }

        private void Apply()
        {
            _settings.Display24HrTime = _format24Radio.Checked;
            _settings.ShowSeconds = _showSecondsCheck.Checked;
            _settings.FlipAnimation = _flipAnimationCheck.Checked;
            _settings.ShowDate = _showDateCheck.Checked;
            _settings.ShowWeather = _showWeatherCheck.Checked;
            _settings.WeatherCity = _weatherCityText.Text.Trim();
            _settings.ShowSystemInfo = _showSystemInfoCheck.Checked;
            _settings.Scale = _scaleTrackBar.Value * 10;
            _settings.HoursScale = (int)_hoursScaleUpDown.Value;
            _settings.MinutesScale = (int)_minutesScaleUpDown.Value;
            _settings.SecondsScale = (int)_secondsScaleUpDown.Value;
            _settings.BackgroundMode = (ClockBackgroundMode)_backgroundCombo.SelectedIndex;
            _settings.TopMost = _topMostCheck.Checked;
            _settings.DesktopBottom = _desktopBottomCheck.Checked;
            _settings.ClickThrough = _clickThroughCheck.Checked;
            _settings.EdgeSnap = _edgeSnapCheck.Checked;
            AutoStart.SetEnabled(_autoStartCheck.Checked);

            _applyCallback?.Invoke();   // saves to DesktopClock.ini and refreshes the clock window
        }

        private static decimal ClampToRange(NumericUpDown control, int value)
        {
            return Math.Min(control.Maximum, Math.Max(control.Minimum, value));
        }
    }
}

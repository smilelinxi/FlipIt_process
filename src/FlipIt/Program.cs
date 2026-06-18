/* Originally based on project by Frank McCown in 2010 */

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenSaver
{
	static class Program
	{
		// Raise the system timer resolution to 1ms so the WinForms timer that drives the flip animation
		// fires evenly (by default it is quantised to ~15.6ms, which makes the animation look jerky).
		[DllImport("winmm.dll", ExactSpelling = true)]
		private static extern uint timeBeginPeriod(uint period);

		[DllImport("winmm.dll", ExactSpelling = true)]
		private static extern uint timeEndPeriod(uint period);

		/// <summary>
		/// The main entry point for the application.
		/// </summary>
		[STAThread]
		static void Main(string[] args)
		{
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);

			// For testing...
			// Application.CurrentCulture = new CultureInfo("nl-NL");

			var settings = FlipItSettings.Load(Screen.AllScreens);

			timeBeginPeriod(1);
			try
			{
				Run(args, settings);
			}
			finally
			{
				timeEndPeriod(1);
			}
		}

		private static void Run(string[] args, FlipItSettings settings)
		{
			if (args.Length > 0)
			{
				string firstArgument = args[0].ToLower().Trim();
				string secondArgument = null;

				// Handle cases where arguments are separated by colon.
				// Examples: /c:1234567 or /P:1234567
				if (firstArgument.Length > 2)
				{
					secondArgument = firstArgument.Substring(3).Trim();
					firstArgument = firstArgument.Substring(0, 2);
				}
				else if (args.Length > 1)
					secondArgument = args[1];

                if (firstArgument == "/c")           // Configuration mode
				{
					Application.Run(new SettingsForm(settings));
				}
				else if (firstArgument == "/p")      // Preview mode
				{
					if (secondArgument == null)
					{
						MessageBox.Show("抱歉，未提供预期的窗口句柄。",
							"ScreenSaver", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
						return;
					}

					IntPtr previewWndHandle = new IntPtr(long.Parse(secondArgument));
					Application.Run(new MainForm(previewWndHandle, settings, settings.ScreenSettings[0]));
				}
				else if (firstArgument == "/s")      // Full-screen mode
				{
					ShowScreenSaver(settings);
					Application.Run();
				}
				else if (firstArgument == "/d")      // Desktop clock mode (windowed, minimisable)
				{
					// The clock loads its own settings (DesktopClock.ini), separate from the screensaver's.
					Application.Run(new DesktopClockForm());
				}
				else    // Undefined argument
				{
					MessageBox.Show("抱歉，命令行参数 “" + firstArgument +
						"” 无效。", "FlipIt",
						MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
				}
			}
			else    // No arguments
			{
				// A copy of this exe named to mark it as the clock (e.g. 桌面时钟.exe) opens the desktop
				// clock straight from a double-click; the original FlipIt.exe still opens settings.
				if (IsDesktopClockExe())
					Application.Run(new DesktopClockForm());
				else
					Application.Run(new SettingsForm(settings));
			}
		}

		/// <summary>
		/// True when the running executable is named as the desktop-clock build (contains "clock" or
		/// the Chinese "时钟"), so that double-clicking it launches the clock rather than the settings.
		/// </summary>
		private static bool IsDesktopClockExe()
		{
			var name = System.IO.Path.GetFileNameWithoutExtension(Application.ExecutablePath) ?? "";
			return name.IndexOf("clock", StringComparison.OrdinalIgnoreCase) >= 0
				|| name.IndexOf("时钟", StringComparison.Ordinal) >= 0;
		}

		/// <summary>
		/// Display the form on each of the computer's monitors.
		/// </summary>
		static void ShowScreenSaver(FlipItSettings settings)
        {
			foreach (var screen in Screen.AllScreens)
            {
                var screenSettings = settings.GetScreen(screen.DeviceName);
				var form = new MainForm(screen.Bounds, settings, screenSettings);
				form.Show();
			}
		}
    }
}

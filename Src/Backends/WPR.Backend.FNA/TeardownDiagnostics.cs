using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WPR.Backend.FNA
{
	/// <summary>
	/// Post-teardown diagnostics for the close-failure investigation (2026-08-08).
	///
	/// <para>WER reports the failure as <c>AppHangB1</c> with <c>Hang Type = 0x8000000</c> — an
	/// UNRESPONSIVE TOP-LEVEL WINDOW — roughly 17 s after the game's teardown has already completed
	/// cleanly (see <c>wpr_teardown.log</c>). FNA creates its SDL game window inside the launcher's own
	/// process, so a window that outlives the game loop is a top-level window of
	/// <c>WPR.Platform.Windows.exe</c> with nothing pumping its messages — which is exactly what Windows
	/// kills a process for. The same lingering <c>Game</c> would also explain the ALC never unloading.
	/// </para>
	///
	/// <para>This enumerates the process's top-level windows and its thread count once teardown has
	/// finished, to confirm or kill that theory with data instead of inference. Diagnostics only —
	/// it never throws and changes no behaviour.</para>
	/// </summary>
	internal static class TeardownDiagnostics
	{
		private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

		[DllImport("user32.dll")]
		private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

		[DllImport("user32.dll")]
		private static extern bool IsWindowVisible(IntPtr hWnd);

		/// <summary>SDL's window class name — how a leaked FNA game window is identified.</summary>
		private const string SdlWindowClass = "SDL_app";

		/// <summary>
		/// OS thread count sampled at launch. A large positive delta at teardown means the game left
		/// threads running — which is a prime candidate for the ALC that never unloads, since a live
		/// thread's stack roots whatever game types it is executing.
		/// </summary>
		private static int _launchThreadCount = -1;

		internal static void RecordLaunchBaseline()
		{
			try { _launchThreadCount = Process.GetCurrentProcess().Threads.Count; }
			catch { _launchThreadCount = -1; }
		}

		/// <summary>
		/// True if a top-level SDL game window still exists in THIS process. Used by the teardown to
		/// detect (and then force-destroy) a window that <c>Game.Dispose</c> failed to release; a
		/// surviving window is proof the game never destroyed it, so acting on it is not a double-free.
		/// </summary>
		internal static bool HasLiveSdlWindow()
		{
			bool found = false;
			try
			{
				if (!OperatingSystem.IsWindows())
				{
					return false;
				}

				uint self = (uint) Environment.ProcessId;
				EnumWindows((hWnd, _) =>
				{
					try
					{
						GetWindowThreadProcessId(hWnd, out uint pid);
						if (pid != self)
						{
							return true;
						}
						var cls = new StringBuilder(160);
						GetClassName(hWnd, cls, cls.Capacity);
						if (cls.ToString() == SdlWindowClass)
						{
							found = true;
							return false; // stop enumerating
						}
					}
					catch { /* skip this window */ }
					return true;
				}, IntPtr.Zero);
			}
			catch { /* diagnostics must never affect teardown */ }
			return found;
		}

		/// <summary>
		/// Describes this process's surviving top-level windows and thread count. Returns a single
		/// log-ready line (never throws; returns a short note if unavailable).
		/// </summary>
		internal static string Describe()
		{
			try
			{
				var sb = new StringBuilder();

				int threadCount;
				try { threadCount = Process.GetCurrentProcess().Threads.Count; }
				catch { threadCount = -1; }
				sb.Append("threads=").Append(threadCount);
				if (_launchThreadCount >= 0)
				{
					sb.Append(" (atLaunch=").Append(_launchThreadCount)
					  .Append(" delta=").Append((threadCount - _launchThreadCount).ToString("+0;-0;0"))
					  .Append(')');
				}

				if (!OperatingSystem.IsWindows())
				{
					sb.Append(" windows=(non-Windows host)");
					return sb.ToString();
				}

				uint self = (uint) Environment.ProcessId;
				var found = new List<string>();
				EnumWindows((hWnd, _) =>
				{
					try
					{
						GetWindowThreadProcessId(hWnd, out uint pid);
						if (pid != self)
						{
							return true;
						}

						var cls = new StringBuilder(160);
						GetClassName(hWnd, cls, cls.Capacity);
						var title = new StringBuilder(160);
						GetWindowText(hWnd, title, title.Capacity);
						found.Add(
							$"{{hwnd=0x{hWnd.ToInt64():X} class=\"{cls}\" title=\"{title}\" visible={IsWindowVisible(hWnd)}}}");
					}
					catch { /* skip this window */ }
					return true;
				}, IntPtr.Zero);

				sb.Append(" topLevelWindows=").Append(found.Count);
				foreach (string w in found)
				{
					sb.Append(' ').Append(w);
				}
				return sb.ToString();
			}
			catch (Exception ex)
			{
				return "diagnostics unavailable: " + ex.GetType().Name + ": " + ex.Message;
			}
		}
	}
}

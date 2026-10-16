#nullable enable
using System;
using System.Diagnostics;

namespace WPR.Xna.Rhi
{
	/// <summary>What went wrong, for <see cref="ErrorScreen"/>. Plain strings: the screen is drawn
	/// by the engine tier, and the facts come from the framework and the WP8 runtime.</summary>
	public sealed class GameErrorReport
	{
		/// <summary>Short headline: the exception type and message, or the WP8 guest's stop reason.</summary>
		public string Headline { get; init; } = "";

		/// <summary>Where it happened: the top managed frame, or the guest's frame count.</summary>
		public string? Where { get; init; }

		/// <summary>A few more lines, for a screenshot in a bug report. May be long; the screen clips it.</summary>
		public string? Detail { get; init; }

		/// <summary>
		/// True for a definite stop (a WP8 guest that stopped): with no screen to show it on, the
		/// game exits as it always did. False for the frame watch's verdict, which does nothing
		/// without a screen.
		/// </summary>
		public bool Stopped { get; init; }

		/// <summary>True when a crash report for this was already queued for WPR Hub.</summary>
		public bool Reported { get; init; }
	}

	/// <summary>
	/// Puts a game's failure on the screen instead of leaving it black.
	/// </summary>
	/// <remarks>
	/// <para>Two ways in, both conservative. <see cref="Request"/> is a definite stop (a WP8 guest
	/// that faulted). The <b>frame watch</b> (<see cref="NoteFrameException"/> +
	/// <see cref="EndFrame"/>) trips only when every drawn frame for at least
	/// <see cref="TripAfter"/> and <see cref="TripFrames"/> frames both threw AND issued no draw
	/// call: a title that throws every frame but still paints (Feed Me Oil, Chickens Can't Fly,
	/// Brain Challenge) never trips it, and neither does one that draws nothing but throws nothing
	/// (a loading screen, Galactic Reign's undrawn menu).</para>
	///
	/// <para>The screen itself is a <see cref="IHostOverlay"/> built by <see cref="Factory"/>, which
	/// <c>ApplicationLaunch</c> registers per launch (it lives in the engine tier, beside the CPU
	/// text rasteriser). <see cref="Game.Tick"/> picks a pending request up on the game thread and
	/// shows it, so the game stops running underneath and Back leaves.</para>
	///
	/// <para><c>WPR_ERROR_SCREEN=0</c> turns the whole thing off (a WP8 guest stop then exits, as
	/// before). <c>WPR_TEST_FORCE_FRAME_FAILURE=1</c> makes every frame throw instead of running
	/// the game, to see the screen on demand.</para>
	/// </remarks>
	public static class ErrorScreen
	{
		/// <summary>How long nothing must be drawn while frames throw.</summary>
		public static readonly TimeSpan TripAfter = TimeSpan.FromSeconds(2);

		/// <summary>And how many such frames, so a slow device does not trip it on a few long ones.</summary>
		public const int TripFrames = 60;

		/// <summary>False when <c>WPR_ERROR_SCREEN=0</c>.</summary>
		public static readonly bool Enabled = Environment.GetEnvironmentVariable("WPR_ERROR_SCREEN") != "0";

		/// <summary>Test hook: every frame throws in place of the game's Update and Draw.</summary>
		internal static readonly bool ForceFrameFailure = Environment.GetEnvironmentVariable("WPR_TEST_FORCE_FRAME_FAILURE") == "1";

		/// <summary>Builds the screen. The action exits the game (Back). Per launch; null = no screen.</summary>
		public static Func<GameErrorReport, Action, IHostOverlay?>? Factory { get; set; }

		private static readonly object _gate = new object();
		private static GameErrorReport? _pending;

		/// <summary>The report on screen this run, if any.</summary>
		public static GameErrorReport? Shown { get; private set; }

		// The frame watch. Game-thread only.
		private static Exception? _frameException;
		private static Exception? _firstFailure;
		private static int _failingFrames;
		private static long _failingSince;

		/// <summary>
		/// Asks for <paramref name="report"/> to be shown from the next tick. Any thread. True when
		/// a screen will show it; false when there is none (disabled, or no factory registered), in
		/// which case the caller does what it did before.
		/// </summary>
		public static bool Request(GameErrorReport report)
		{
			if (!Enabled || Factory == null || report == null) return false;
			lock (_gate)
			{
				if (Shown != null || _pending != null) return true;
				_pending = report;
			}
			return true;
		}

		/// <summary>The pending request, taken. Game thread, once a tick.</summary>
		public static GameErrorReport? TakePending()
		{
			if (_pending == null) return null;
			lock (_gate)
			{
				GameErrorReport? r = _pending;
				_pending = null;
				if (r != null) Shown = r;
				return r;
			}
		}

		/// <summary>An exception out of the game's Update or Draw this tick.</summary>
		public static void NoteFrameException(Exception ex)
		{
			_frameException ??= ex;
		}

		/// <summary>
		/// The end of a drawn frame: <paramref name="drawCalls"/> is what it issued. A frame that
		/// drew, or that threw nothing, resets the watch.
		/// </summary>
		public static void EndFrame(int drawCalls)
		{
			Exception? ex = _frameException;
			_frameException = null;
			if (Shown != null || !Enabled) return;

			if (drawCalls > 0 || ex == null)
			{
				_failingFrames = 0;
				_firstFailure = null;
				return;
			}

			if (_failingFrames == 0)
			{
				_failingSince = Stopwatch.GetTimestamp();
				_firstFailure = ex;
			}
			_failingFrames++;

			if (_failingFrames < TripFrames) return;
			TimeSpan elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - _failingSince) / (double)Stopwatch.Frequency);
			if (elapsed < TripAfter) return;

			Exception first = _firstFailure ?? ex;
			XnaBackend.LogInfo($"[wpr-error-screen] {_failingFrames} frame(s) over {elapsed.TotalSeconds:0.0} s threw and drew nothing; showing {first.GetType().FullName}");
			Request(Describe(first, $"every frame for {elapsed.TotalSeconds:0} s threw and drew nothing"));
			_failingFrames = 0;
		}

		/// <summary>A report for a managed exception: type and message, top frame, inner exception.</summary>
		public static GameErrorReport Describe(Exception ex, string? context = null)
		{
			Exception root = ex;
			while (root is System.Reflection.TargetInvocationException { InnerException: { } inner }) root = inner;

			string? where = TopFrame(root);
			string detail = context ?? "";
			if (root.InnerException is { } innerEx)
			{
				detail += (detail.Length > 0 ? "\n" : "") + "caused by " + innerEx.GetType().FullName + ": " + innerEx.Message;
			}
			string? stack = root.StackTrace;
			if (!string.IsNullOrEmpty(stack))
			{
				// The frames below the top one (that one is already "Where").
				string[] lines = stack!.Split('\n');
				int take = Math.Min(lines.Length, 7);
				for (int i = 1; i < take; i++)
				{
					detail += (detail.Length > 0 ? "\n" : "") + ShortenFrame(lines[i].Trim());
				}
			}

			return new GameErrorReport
			{
				Headline = root.GetType().FullName + ": " + root.Message,
				Where = where,
				Detail = detail,
			};
		}

		private static string? TopFrame(Exception ex)
		{
			string? stack = ex.StackTrace;
			if (string.IsNullOrEmpty(stack)) return null;
			foreach (string raw in stack!.Split('\n'))
			{
				string line = raw.Trim();
				if (line.StartsWith("at ", StringComparison.Ordinal)) return ShortenFrame(line.Substring(3));
			}
			return null;
		}

		/// <summary>"M() in C:\long\path\File.cs:line 12" becomes "M() (File.cs:12)": a path is one
		/// unbreakable word, wider than a phone screen.</summary>
		public static string ShortenFrame(string frame)
		{
			int at = frame.LastIndexOf(" in ", StringComparison.Ordinal);
			if (at < 0) return frame;
			string location = frame.Substring(at + 4);
			int line = location.LastIndexOf(":line ", StringComparison.Ordinal);
			string file = line >= 0 ? location.Substring(0, line) : location;
			int slash = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
			if (slash >= 0) file = file.Substring(slash + 1);
			string number = line >= 0 ? ":" + location.Substring(line + 6) : "";
			return frame.Substring(0, at) + " (" + file + number + ")";
		}

		/// <summary>Per launch, at teardown (and at the start of the next).</summary>
		public static void Reset()
		{
			lock (_gate)
			{
				_pending = null;
				Shown = null;
			}
			_frameException = null;
			_firstFailure = null;
			_failingFrames = 0;
			Factory = null;
		}
	}
}

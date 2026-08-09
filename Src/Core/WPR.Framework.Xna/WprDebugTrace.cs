using System.Diagnostics;

namespace Microsoft.Xna.Framework
{
	/// <summary>
	/// Centralised gate for the per-frame / per-call WPR debug traces. The
	/// <c>[Conditional("DEBUG")]</c> attribute makes the C# compiler elide every
	/// call site — including the argument expressions — when DEBUG is not defined.
	/// In Release builds this means an interpolated string like
	/// <c>WprDebugTrace.WriteLine($"x={x}")</c> compiles to nothing at all: no
	/// string allocation, no Trace listener dispatch, no cost.
	/// </summary>
	/// <remarks>
	/// To enable: build the FNA project (and the WPR callers) in <c>Debug</c>
	/// configuration. The file listener that captures these traces to
	/// <c>wpr_game_debug.log</c> is itself gated by <c>#if DEBUG</c> in
	/// <c>WPR.ApplicationLaunch</c>, so end users on Release builds never see
	/// either the cost or the file.
	/// </remarks>
	public static class WprDebugTrace
	{
		[Conditional("DEBUG")]
		public static void WriteLine(string message)
		{
			Trace.WriteLine(message);
		}
	}
}

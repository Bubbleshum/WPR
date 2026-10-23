using System.Threading;

namespace Microsoft.Xna.Framework.Graphics
{
	/// <summary>
	/// WPR: a count of native effects, for diagnosis. A title that creates an effect per draw
	/// and never disposes one (AE Fruit Slash, a BasicEffect per particle and blade-trail draw)
	/// relies on <c>~Effect</c> to give them back. Logged every 128 created as
	/// <c>[wpr-effects] created=N live=N</c> (Trace: logcat and the session log). A live count
	/// that climbs without end means effects are being kept alive; a flat one with a native
	/// failure means the driver ran out of something else (in AE's case FNA3D's uniform descriptor
	/// pool, one set per shader for life; see VULKAN_INTERNAL_AllocateDescriptorSets).
	/// </summary>
	internal static class NativeEffects
	{
		private static int _live;
		private static long _created;

		internal static int Live => Volatile.Read(ref _live);

		internal static long CreatedTotal => Interlocked.Read(ref _created);

		internal static void Created(Effect effect)
		{
			Interlocked.Increment(ref _live);
			long created = Interlocked.Increment(ref _created);
			if (created % 128 == 0)
			{
				System.Diagnostics.Trace.WriteLine($"[wpr-effects] created={created} live={Live}");
			}
		}

		internal static void Released()
		{
			Interlocked.Decrement(ref _live);
		}
	}
}

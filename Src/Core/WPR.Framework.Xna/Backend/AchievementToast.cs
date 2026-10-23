#nullable enable
using System;
using System.Collections.Concurrent;
using Microsoft.Xna.Framework.Graphics;

namespace WPR.Xna.Rhi
{
	/// <summary>One achievement to announce on screen.</summary>
	/// <param name="Name">The achievement's name.</param>
	/// <param name="Score">Its gamerscore.</param>
	/// <param name="IconPath">Its icon as an absolute file path, or null.</param>
	public sealed record AchievementToastRequest(string Name, int Score, string? IconPath);

	/// <summary>
	/// The on-screen "achievement unlocked" pop-up, drawn over the game's own frame for a few
	/// seconds, as the Xbox did. It replaced the system notification, which took the game's
	/// focus on the desktop and covered the screen on a phone.
	/// </summary>
	/// <remarks>
	/// <para><see cref="Microsoft.Xna.Framework.GamerServices.SignedInGamer"/> queues an unlock
	/// from any thread; <see cref="GraphicsDevice.Present()"/> calls <see cref="Draw"/> on the game
	/// thread just before the frame goes to the screen, and the engine's drawer (registered per
	/// launch by the launch sequence) draws the current pop-up over it. Every kind of game presents
	/// through that device, so XNA, Silverlight and WP8 titles all get it.</para>
	///
	/// <para>No drawer (between launches, or a harness that registers none): nothing is drawn and
	/// the queue waits; <see cref="Reset"/> empties it at each launch and teardown.</para>
	/// </remarks>
	public static class AchievementToast
	{
		private static readonly ConcurrentQueue<AchievementToastRequest> Pending = new ConcurrentQueue<AchievementToastRequest>();

		/// <summary>Draws over the finished frame. Set per launch by the engine; must not throw.</summary>
		public static Action<GraphicsDevice>? Drawer { get; set; }

		/// <summary>Queues an unlock. Any thread.</summary>
		public static void Show(AchievementToastRequest request) => Pending.Enqueue(request);

		/// <summary>The next unlock to show, for the drawer.</summary>
		public static bool TryTake(out AchievementToastRequest? request)
		{
			bool taken = Pending.TryDequeue(out AchievementToastRequest? next);
			request = next;
			return taken;
		}

		/// <summary>Anything still waiting (so the drawer keeps running until the queue is empty).</summary>
		public static bool HasPending => !Pending.IsEmpty;

		internal static void Draw(GraphicsDevice device)
		{
			Action<GraphicsDevice>? drawer = Drawer;
			if (drawer == null) return;
			try { drawer(device); }
			catch (Exception ex) { XnaBackend.LogWarn("[wpr-achievement] pop-up drawer threw: " + ex.Message); }
		}

		public static void Reset()
		{
			Drawer = null;
			while (Pending.TryDequeue(out _)) { }
		}
	}
}

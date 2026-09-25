#region License
/* FNA - XNA4 Reimplementation for Desktop Platforms
 * Copyright 2009-2022 Ethan Lee and the MonoGame Team
 *
 * Released under the Microsoft Public License.
 * See LICENSE for details.
 */
#endregion

#region Using Statements
using System.Collections.Generic;

using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Input.Touch;
using MediaPlayer = Microsoft.Xna.Framework.Media.MediaPlayer;
#endregion

namespace Microsoft.Xna.Framework
{
	public static class FrameworkDispatcher
	{
		#region Internal Variables

		/* WPR 5c-3a/5c-3c: the DynamicSoundEffectInstance registry AND the two media dirty flags
		 * (ActiveSongChanged / MediaStateChanged) that used to live here moved into WPR.Framework.Xna
		 * with their owning types. This dispatcher STAYS in FNA because it still pumps TouchPanel,
		 * which has not moved — and the moved code may not reference FNA, so the state had to invert
		 * onto the owners rather than the pump moving. It becomes a pure ordering shell, and moves in
		 * 5c-5 once input lands. See DynamicSoundEffectInstance.UpdateAll and MediaPlayer.PumpUpdate
		 * (both reachable via InternalsVisibleTo).
		 */

		#endregion

		#region Public Methods

		/// <summary>
		/// Number of times <see cref="Update"/> has been called this process.
		/// </summary>
		/// <remarks>
		/// Read by the mixed-mode host to answer one question: did the app pump this frame?
		/// A Silverlight/XNA app has no <c>Game</c>, so the WP7 template makes the app itself
		/// responsible — it creates a <c>GameTimer</c> whose <c>FrameAction</c> does nothing but
		/// call <see cref="Update"/>. The host therefore must NOT pump as well: two pumps in one
		/// tick promote a touch from Pressed straight to Moved, so <c>TouchPanel.GetState</c>
		/// never reports Pressed and every tap is swallowed. That is the same defect the comment
		/// in <c>Game.Tick</c> records against Asphalt 5. Counting lets the host pump only for an
		/// app that turns out not to.
		/// </remarks>
		internal static int PumpCount;

		public static void Update()
		{
			PumpCount += 1;

			/* Updates the status of various framework components
			 * (such as power state and media), and raises related events.
			 */
			DynamicSoundEffectInstance.UpdateAll();
			if (Microphone.micList != null)
			{
				for (int i = 0; i < Microphone.micList.Count; i += 1)
				{
					Microphone.micList[i].CheckBuffer();
				}
			}

			MediaPlayer.PumpUpdate();

			if (TouchPanel.TouchDeviceExists)
			{
				TouchPanel.Update();
			}
		}

		#endregion
	}
}

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

		public static void Update()
		{
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

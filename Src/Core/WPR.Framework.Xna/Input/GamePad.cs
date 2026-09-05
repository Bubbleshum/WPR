#region License
/* FNA - XNA4 Reimplementation for Desktop Platforms
 * Copyright 2009-2022 Ethan Lee and the MonoGame Team
 *
 * Released under the Microsoft Public License.
 * See LICENSE for details.
 */
#endregion

#region Using Statements
using System;
using WPR.Xna.Rhi;
#endregion

namespace Microsoft.Xna.Framework.Input
{
	public static class GamePad
	{
		#region Internal Constants

		/* Based on the XInput constants */
		internal const float LeftDeadZone = 7849.0f / 32768.0f;
		internal const float RightDeadZone = 8689.0f / 32768.0f;
		internal const float TriggerThreshold = 30.0f / 255.0f;

		#endregion

		#region Internal Static Variables

		/* Determines how many controllers we should be tracking.
		 * Per XNA4 we track 4 by default, but if you want to track more you can
		 * do this by changing PlayerIndex.cs to include more index names.
		 * -flibit
		 */
		internal static readonly int GAMEPAD_COUNT = DetermineNumGamepads();

		private static int DetermineNumGamepads()
		{
			string numGamepadString = Environment.GetEnvironmentVariable(
				"FNA_GAMEPAD_NUM_GAMEPADS"
			);
			if (!String.IsNullOrEmpty(numGamepadString))
			{
				int numGamepads;
				if (int.TryParse(numGamepadString, out numGamepads))
				{
					if (numGamepads >= 0)
					{
						return numGamepads;
					}
				}
			}
			return Enum.GetNames(typeof(PlayerIndex)).Length;
		}

		#endregion

		#region Public GamePad API

		public static GamePadCapabilities GetCapabilities(PlayerIndex playerIndex)
		{
			return XnaBackend.Input.GetGamePadCapabilities((int) playerIndex);
		}

		public static GamePadState GetState(PlayerIndex playerIndex)
		{
			return XnaBackend.Input.GetGamePadState(
				(int) playerIndex,
				GamePadDeadZone.IndependentAxes
			);
		}

		public static GamePadState GetState(PlayerIndex playerIndex, GamePadDeadZone deadZoneMode)
		{
			return XnaBackend.Input.GetGamePadState(
				(int) playerIndex,
				deadZoneMode
			);
		}

		public static bool SetVibration(PlayerIndex playerIndex, float leftMotor, float rightMotor)
		{
			/* The user's global vibration switch (Configuration.VibrationEnabled, set on the
			 * Android settings page) covers rumble too — a toggle labelled "vibration" that left
			 * a connected pad shaking would be a bug. Note this path does NOT go through
			 * VibrationBackend's provider at all; it is SDL, via IInputBackend. Only the
			 * preference is shared.
			 *
			 * Motors are zeroed rather than the call being skipped, so the return value keeps
			 * meaning what XNA says it means — "false when the pad has no rumble motors" — rather
			 * than conflating a muted pad with a motorless one. It also actively stops a rumble
			 * that a game started before the switch was read.
			 */
			if (!WPR.Engine.Vibration.VibrationBackend.IsEnabled)
			{
				leftMotor = 0.0f;
				rightMotor = 0.0f;
			}

			return XnaBackend.Input.SetGamePadVibration(
				(int) playerIndex,
				leftMotor,
				rightMotor
			);
		}

		#endregion

		#region Public GamePad API, FNA Extensions

		public static string GetGUIDEXT(PlayerIndex playerIndex)
		{
			return XnaBackend.Input.GetGamePadGUID((int) playerIndex);
		}

		public static void SetLightBarEXT(PlayerIndex playerIndex, Color color)
		{
			XnaBackend.Input.SetGamePadLightBar((int) playerIndex, color);
		}

		public static bool SetTriggerVibrationEXT(PlayerIndex playerIndex, float leftTrigger, float rightTrigger)
		{
			/* Same global switch as SetVibration above — trigger rumble is still rumble. */
			if (!WPR.Engine.Vibration.VibrationBackend.IsEnabled)
			{
				leftTrigger = 0.0f;
				rightTrigger = 0.0f;
			}

			return XnaBackend.Input.SetGamePadTriggerVibration(
				(int) playerIndex,
				leftTrigger,
				rightTrigger
			);
		}

		public static bool GetGyroEXT(PlayerIndex playerIndex, out Vector3 gyro)
		{
			return XnaBackend.Input.GetGamePadGyro(
				(int) playerIndex,
				out gyro
			);
		}

		public static bool GetAccelerometerEXT(PlayerIndex playerIndex, out Vector3 accel)
		{
			return XnaBackend.Input.GetGamePadAccelerometer(
				(int) playerIndex,
				out accel
			);
		}

		#endregion

		#region Internal Static Methods

		internal static float ExcludeAxisDeadZone(float value, float deadZone)
		{
			if (value < -deadZone)
			{
				value += deadZone;
			}
			else if (value > deadZone)
			{
				value -= deadZone;
			}
			else
			{
				return 0.0f;
			}
			return value / (1.0f - deadZone);
		}

		#endregion
	}
}

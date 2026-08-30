using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using WPR.Xna.Rhi;

using FNAPlatform = Microsoft.Xna.Framework.FNAPlatform;

namespace WPR.Backend.FNA
{
    /// <summary>
    /// FNA implementation of <see cref="IInputBackend"/> — Stage 5c-5 (Plans/STAGE5C-SCOPE.md).
    ///
    /// <para>A straight forward to FNA's <c>FNAPlatform</c> delegate table, which SDL2 populates at
    /// static-init. Deliberately calls the table rather than SDL directly: the table is where FNA's
    /// platform selection and its SDL quirk handling live (scancode mapping, dead-zone application,
    /// the touch-event drain), and re-deriving any of that here would fork behaviour the games have
    /// already been tested against.</para>
    ///
    /// <para>Stateless — every operation either polls the OS or pushes into the moved XNA input
    /// types' own statics.</para>
    /// </summary>
    public sealed class FnaInputBackend : IInputBackend
    {
        // ---- Keyboard ----

        public Keys GetKeyFromScancode(Keys scancode) => FNAPlatform.GetKeyFromScancode(scancode);

        // ---- Text input ----

        public void StartTextInput() => FNAPlatform.StartTextInput();

        public void StopTextInput() => FNAPlatform.StopTextInput();

        public void SetTextInputRectangle(Rectangle rectangle) =>
            FNAPlatform.SetTextInputRectangle(rectangle);

        // ---- Mouse ----

        public void GetMouseState(
            IntPtr window,
            out int x,
            out int y,
            out ButtonState left,
            out ButtonState middle,
            out ButtonState right,
            out ButtonState x1,
            out ButtonState x2
        ) => FNAPlatform.GetMouseState(window, out x, out y, out left, out middle, out right, out x1, out x2);

        public void SetMousePosition(IntPtr window, int x, int y) =>
            FNAPlatform.SetMousePosition(window, x, y);

        public bool GetRelativeMouseMode() => FNAPlatform.GetRelativeMouseMode();

        public void SetRelativeMouseMode(bool enable) => FNAPlatform.SetRelativeMouseMode(enable);

        // ---- GamePad ----

        public GamePadCapabilities GetGamePadCapabilities(int index) =>
            FNAPlatform.GetGamePadCapabilities(index);

        public GamePadState GetGamePadState(int index, GamePadDeadZone deadZoneMode) =>
            FNAPlatform.GetGamePadState(index, deadZoneMode);

        public bool SetGamePadVibration(int index, float leftMotor, float rightMotor) =>
            FNAPlatform.SetGamePadVibration(index, leftMotor, rightMotor);

        public bool SetGamePadTriggerVibration(int index, float leftTrigger, float rightTrigger) =>
            FNAPlatform.SetGamePadTriggerVibration(index, leftTrigger, rightTrigger);

        public string GetGamePadGUID(int index) => FNAPlatform.GetGamePadGUID(index);

        public void SetGamePadLightBar(int index, Color color) =>
            FNAPlatform.SetGamePadLightBar(index, color);

        public bool GetGamePadGyro(int index, out Vector3 gyro) =>
            FNAPlatform.GetGamePadGyro(index, out gyro);

        public bool GetGamePadAccelerometer(int index, out Vector3 accel) =>
            FNAPlatform.GetGamePadAccelerometer(index, out accel);

        // ---- Touch ----

        public TouchPanelCapabilities GetTouchCapabilities() => FNAPlatform.GetTouchCapabilities();

        public void UpdateTouchPanelState() => FNAPlatform.UpdateTouchPanelState();
    }
}

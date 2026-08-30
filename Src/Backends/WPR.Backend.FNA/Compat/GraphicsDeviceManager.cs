using Microsoft.Xna.Framework;
using System;

namespace WPR.Backend.FNA.Compat
{
    public class GraphicsDeviceManager : Microsoft.Xna.Framework.GraphicsDeviceManager
    {
        public static Action<DisplayOrientation>? RequestOrientation;

        // The patcher redirects every Microsoft.Xna.Framework.GraphicsDeviceManager
        // reference in the game IL to this type, including the static field reads
        // GraphicsDeviceManager.DefaultBackBufferWidth/Height. In real XNA these are
        // public static fields; the game emits `ldsfld GraphicsDeviceManager2::Default...`
        // which the runtime resolves on the EXACT declared type and does not walk to the
        // FNA base class — so without these declarations launch dies with
        // MissingFieldException (Star Wars: The Battle for Hoth crashed on the first line
        // of CGame.InitializeMain reading these). Shadow the base fields with identical
        // 800x480 phone-surface values so the redirected ldsfld resolves here.
        public new static readonly int DefaultBackBufferWidth = PhoneLongDim;
        public new static readonly int DefaultBackBufferHeight = PhoneShortDim;

        // WP7 phones had a fixed 800x480 hardware surface. Games like Zuma's Revenge
        // request a larger preferred backbuffer (1066x640 in the Nokia build) but
        // hardcode their internal viewport to 800x480, so on a real phone the OS
        // clamped the surface and content filled the screen; on FNA's desktop SDL
        // window the larger backbuffer is honored literally and the game renders
        // into the upper-left 800x480 of an oversized window. Mirror the phone
        // clamp here so requested buffers never exceed the phone surface.
        private const int PhoneLongDim = 800;
        private const int PhoneShortDim = 480;

        public GraphicsDeviceManager(Game game)
            : base(game)
        {

        }

#if !__MOBILE__
        public new bool IsFullScreen
        {
            get => false;
            set => base.IsFullScreen = false;
        }
#endif

        public new int PreferredBackBufferWidth
        {
            get => base.PreferredBackBufferWidth;
            set => base.PreferredBackBufferWidth = ClampToPhoneSurface(value, base.PreferredBackBufferHeight);
        }

        public new int PreferredBackBufferHeight
        {
            get => base.PreferredBackBufferHeight;
            set => base.PreferredBackBufferHeight = ClampToPhoneSurface(value, base.PreferredBackBufferWidth);
        }

        private static int ClampToPhoneSurface(int requested, int otherDim)
        {
            int max = requested >= otherDim ? PhoneLongDim : PhoneShortDim;
            return requested > max ? max : requested;
        }

        public new void ApplyChanges()
        {
            base.ApplyChanges();
            RequestOrientationChange(PreferredBackBufferWidth, PreferredBackBufferHeight);
        }

        public static void RequestOrientationChange(int width, int height)
        {
            DisplayOrientation device_orientation = default;

            if (width > height)
            {
                device_orientation = DisplayOrientation.LandscapeRight;
            }
            else
            {
                device_orientation = DisplayOrientation.Portrait;
            }
            RequestOrientation?.Invoke(device_orientation);
        }
    }
}

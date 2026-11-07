using Microsoft.Xna.Framework;

namespace WPR.Xna.Rhi
{
    /// <summary>
    /// XNA on Windows Phone: a backbuffer shape that none of the game's
    /// <c>GraphicsDeviceManager.SupportedOrientations</c> can show is turned to one that can.
    /// The phone's default backbuffer is 800x480, so a game that only says
    /// <c>SupportedOrientations = Portrait</c> got a 480x800 backbuffer, and its window reported
    /// <c>CurrentOrientation = Portrait</c> from the start.
    /// <para>Doodle Jump is the reference case: it sets Portrait and never a size, and sizes its
    /// backbuffer itself only from <c>PhoneApplicationService.Activated</c> and
    /// <c>Window.OrientationChanged</c>, neither of which a cold start raises (WPR stopped
    /// inventing a cold-start Activated in 0.2.09). Left at 800x480 it ran landscape: an 800x480
    /// window on the desktop, and on Android the activity turned landscape.</para>
    /// <para>Default, or a set holding both shapes, leaves the size alone: the backbuffer then
    /// decides, as it always did.</para>
    /// </summary>
    public static class SupportedOrientationFit
    {
        public static void Apply(DisplayOrientation supported, ref int width, ref int height)
        {
            bool portrait = (supported & DisplayOrientation.Portrait) != 0;
            bool landscape = (supported &
                (DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight)) != 0;
            if (portrait == landscape)
            {
                return;
            }

            if (portrait ? width > height : height > width)
            {
                (width, height) = (height, width);
            }
        }
    }
}

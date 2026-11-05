using Microsoft.Xna.Framework;
using WPR.Xna.Rhi;
using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    public class SupportedOrientationFitTests
    {
        [Theory]
        // Portrait only: the phone's default 800x480 becomes 480x800 (Doodle Jump).
        [InlineData(DisplayOrientation.Portrait, 800, 480, 480, 800)]
        [InlineData(DisplayOrientation.Portrait, 480, 800, 480, 800)]
        // Landscape only: a portrait size is turned.
        [InlineData(DisplayOrientation.LandscapeLeft, 480, 800, 800, 480)]
        [InlineData(DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight, 800, 480, 800, 480)]
        // Default or both shapes: the backbuffer decides.
        [InlineData(DisplayOrientation.Default, 800, 480, 800, 480)]
        [InlineData(DisplayOrientation.Default, 480, 800, 480, 800)]
        [InlineData(DisplayOrientation.Portrait | DisplayOrientation.LandscapeRight, 800, 480, 800, 480)]
        [InlineData(DisplayOrientation.Portrait | DisplayOrientation.LandscapeRight, 480, 800, 480, 800)]
        // Square is a shape every orientation shows.
        [InlineData(DisplayOrientation.Portrait, 600, 600, 600, 600)]
        public void TurnsOnlyAShapeNoSupportedOrientationShows(
            DisplayOrientation supported, int w, int h, int expectW, int expectH)
        {
            SupportedOrientationFit.Apply(supported, ref w, ref h);
            Assert.Equal((expectW, expectH), (w, h));
        }
    }
}

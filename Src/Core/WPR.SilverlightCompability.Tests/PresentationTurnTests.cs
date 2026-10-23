using XMatrix = Microsoft.Xna.Framework.Matrix;
using XMathHelper = Microsoft.Xna.Framework.MathHelper;
using WPR.Xna.Rhi;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// <see cref="PresentationTurn"/>: when a portrait backbuffer is shown turned on a landscape
/// window, and how window positions map back onto it. The mapping must be the exact inverse of
/// what <c>GraphicsDevice.Present</c> draws, or every click lands beside the button the player
/// sees.
/// </summary>
public class PresentationTurnTests
{
    private const float W = 480, H = 800;

    [Theory]
    [InlineData(BackBufferTurn.Anticlockwise, 480, 800, false, BackBufferTurn.Anticlockwise)]
    [InlineData(BackBufferTurn.Clockwise, 480, 800, false, BackBufferTurn.Clockwise)]
    [InlineData(BackBufferTurn.None, 480, 800, false, BackBufferTurn.None)]
    [InlineData(BackBufferTurn.Anticlockwise, 800, 480, false, BackBufferTurn.None)]   // already landscape
    [InlineData(BackBufferTurn.Anticlockwise, 480, 800, true, BackBufferTurn.None)]    // a phone: the player turns it
    public void A_turn_applies_only_to_a_portrait_backbuffer_on_a_platform_that_cannot_rotate(
        BackBufferTurn requested, int w, int h, bool platformRotates, BackBufferTurn expected)
    {
        Assert.Equal(expected, PresentationTurn.For(requested, w, h, platformRotates));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(55, 740)]
    [InlineData(479, 1)]
    [InlineData(240, 400)]
    public void Anticlockwise_window_positions_map_back_to_the_portrait_pixel_shown_there(float px, float py)
    {
        // Present draws portrait (px, py) at window (py, W - px) on an H x W window.
        float u = py / H, v = (W - px) / W;
        PresentationTurn.WindowToBackBuffer(BackBufferTurn.Anticlockwise, ref u, ref v);
        Assert.Equal(px / W, u, 4);
        Assert.Equal(py / H, v, 4);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(55, 740)]
    [InlineData(240, 400)]
    public void Clockwise_window_positions_map_back_to_the_portrait_pixel_shown_there(float px, float py)
    {
        // Present draws portrait (px, py) at window (H - py, px).
        float u = (H - py) / H, v = px / W;
        PresentationTurn.WindowToBackBuffer(BackBufferTurn.Clockwise, ref u, ref v);
        Assert.Equal(px / W, u, 4);
        Assert.Equal(py / H, v, 4);
    }

    [Fact]
    public void A_click_on_Need_for_Speed_Undercovers_skip_button_reaches_the_button()
    {
        // The game draws its 800x480 picture through CreateRotationZ(PI/2) * Translation(480, 0)
        // and maps a touch back with x = py, y = 480 - px. Its ">>" button sits at (740, 425)
        // in that picture, which is where the turned window shows it.
        float u = 740f / H, v = 425f / W;
        PresentationTurn.WindowToBackBuffer(BackBufferTurn.Anticlockwise, ref u, ref v);
        float px = u * W, py = v * H;
        Assert.Equal(740f, py, 2);
        Assert.Equal(425f, 480 - px, 2);
    }

    [Theory]
    [InlineData(BackBufferTurn.Anticlockwise)]
    [InlineData(BackBufferTurn.Clockwise)]
    [InlineData(BackBufferTurn.None)]
    public void BackBufferToWindow_is_the_inverse(BackBufferTurn turn)
    {
        float u = 0.2f, v = 0.7f;
        PresentationTurn.BackBufferToWindow(turn, ref u, ref v);
        PresentationTurn.WindowToBackBuffer(turn, ref u, ref v);
        Assert.Equal(0.2f, u, 5);
        Assert.Equal(0.7f, v, 5);
    }

    [Fact]
    public void The_window_is_the_backbuffer_size_the_other_way_round()
    {
        int w = 480, h = 800;
        PresentationTurn.WindowSize(BackBufferTurn.Anticlockwise, ref w, ref h);
        Assert.Equal((800, 480), (w, h));

        w = 480; h = 800;
        PresentationTurn.WindowSize(BackBufferTurn.None, ref w, ref h);
        Assert.Equal((480, 800), (w, h));
    }

    [Fact]
    public void The_midp_landscape_transform_reads_as_an_anticlockwise_turn()
    {
        // EA's midp layer: CreateRotationZ(PI/2) * CreateTranslation(width, 0, 0), at 480x800
        // (Need for Speed Undercover) and 320x533 (The Sims 3).
        XMatrix nfs = XMatrix.CreateRotationZ(XMathHelper.Pi / 2f) * XMatrix.CreateTranslation(480, 0, 0);
        Assert.Equal(BackBufferTurn.Anticlockwise, PresentationTurn.QuarterTurnOf(nfs, 480, 800));
        XMatrix sims = XMatrix.CreateRotationZ(XMathHelper.Pi / 2f) * XMatrix.CreateTranslation(320, 0, 0);
        Assert.Equal(BackBufferTurn.Anticlockwise, PresentationTurn.QuarterTurnOf(sims, 320, 533));
    }

    [Fact]
    public void The_opposite_quarter_turn_reads_as_clockwise()
    {
        XMatrix m = XMatrix.CreateRotationZ(-XMathHelper.Pi / 2f) * XMatrix.CreateTranslation(0, 800, 0);
        Assert.Equal(BackBufferTurn.Clockwise, PresentationTurn.QuarterTurnOf(m, 480, 800));
    }

    [Fact]
    public void Other_transforms_are_not_a_turned_picture()
    {
        Assert.Equal(BackBufferTurn.None, PresentationTurn.QuarterTurnOf(XMatrix.Identity, 480, 800));
        // A sprite turned about some other point, or scaled, or a camera pan.
        XMatrix aboutCentre = XMatrix.CreateTranslation(-240, -400, 0) * XMatrix.CreateRotationZ(XMathHelper.Pi / 2f) * XMatrix.CreateTranslation(240, 400, 0);
        Assert.Equal(BackBufferTurn.None, PresentationTurn.QuarterTurnOf(aboutCentre, 480, 800));
        XMatrix scaled = XMatrix.CreateScale(2f) * XMatrix.CreateRotationZ(XMathHelper.Pi / 2f) * XMatrix.CreateTranslation(480, 0, 0);
        Assert.Equal(BackBufferTurn.None, PresentationTurn.QuarterTurnOf(scaled, 480, 800));
        Assert.Equal(BackBufferTurn.None, PresentationTurn.QuarterTurnOf(XMatrix.CreateTranslation(-30, 0, 0), 480, 800));
        // A landscape backbuffer is never turned.
        XMatrix nfs = XMatrix.CreateRotationZ(XMathHelper.Pi / 2f) * XMatrix.CreateTranslation(800, 0, 0);
        Assert.Equal(BackBufferTurn.None, PresentationTurn.QuarterTurnOf(nfs, 800, 480));
    }

    [Fact]
    public void The_window_turns_after_a_run_of_turned_frames_and_back_after_a_longer_upright_run()
    {
        PresentationTurn.Reset();
        try
        {
            for (int i = 1; i < PresentationTurn.TurnOnFrames; i++)
            {
                Assert.False(PresentationTurn.Observe(BackBufferTurn.Anticlockwise, false, platformRotates: false));
                // A frame with no backbuffer batch (a 3D scene alone) breaks nothing.
                Assert.False(PresentationTurn.Observe(BackBufferTurn.None, false, platformRotates: false));
            }
            Assert.True(PresentationTurn.Observe(BackBufferTurn.Anticlockwise, false, platformRotates: false));
            Assert.Equal(BackBufferTurn.Anticlockwise, PresentationTurn.Requested);

            // A turned batch beside an upright one (a HUD over the picture) is a turned frame.
            Assert.False(PresentationTurn.Observe(BackBufferTurn.Anticlockwise, true, platformRotates: false));

            for (int i = 1; i < PresentationTurn.TurnOffFrames; i++)
            {
                Assert.False(PresentationTurn.Observe(BackBufferTurn.None, true, platformRotates: false));
            }
            Assert.True(PresentationTurn.Observe(BackBufferTurn.None, true, platformRotates: false));
            Assert.Equal(BackBufferTurn.None, PresentationTurn.Requested);
        }
        finally
        {
            PresentationTurn.Reset();
        }
    }

    [Fact]
    public void A_phone_never_turns()
    {
        PresentationTurn.Reset();
        try
        {
            for (int i = 0; i < PresentationTurn.TurnOnFrames * 3; i++)
            {
                Assert.False(PresentationTurn.Observe(BackBufferTurn.Anticlockwise, false, platformRotates: true));
            }
            Assert.Equal(BackBufferTurn.None, PresentationTurn.Requested);
        }
        finally
        {
            PresentationTurn.Reset();
        }
    }
}

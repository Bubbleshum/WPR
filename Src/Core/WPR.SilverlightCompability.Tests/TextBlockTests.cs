using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    public class TextBlockTests
    {
        [Fact]
        public void Defaults_AreSilverlightShaped()
        {
            var tb = new TextBlock();
            Assert.Equal(string.Empty, tb.Text);
            Assert.Equal(14.0, tb.FontSize);
            Assert.Equal("Segoe UI", tb.FontFamily);
            Assert.Null(tb.Foreground);
            Assert.Equal(TextAlignment.Left, tb.TextAlignment);
            Assert.Equal(TextWrapping.NoWrap, tb.TextWrapping);
        }

        [Fact]
        public void Measure_EmptyText_ReturnsEmpty()
        {
            var tb = new TextBlock();
            tb.Measure(new Size(100, 100));
            Assert.True(tb.DesiredSize.IsEmpty);
        }

        [Fact]
        public void Measure_NonEmptyText_ProducesNonZeroSize()
        {
            var tb = new TextBlock { Text = "Hello world", FontSize = 20 };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(tb.DesiredSize.Width > 0);
            Assert.True(tb.DesiredSize.Height > 0);
        }

        [Fact]
        public void TextChanged_InvalidatesMeasure()
        {
            var tb = new TextBlock { Text = "abc" };
            tb.Measure(new Size(100, 100));
            Assert.True(tb.IsMeasureValid);

            tb.Text = "different";
            Assert.False(tb.IsMeasureValid);
        }

        [Fact]
        public void XamlLoad_TextBlockProperties_AllApply()
        {
            string xaml = @"
<TextBlock xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
           Text=""Hello"" FontSize=""24"" FontFamily=""Arial""
           Foreground=""Red"" TextAlignment=""Center"" TextWrapping=""Wrap"" />";
            var tb = (TextBlock)XamlReader.Load(xaml);
            Assert.Equal("Hello", tb.Text);
            Assert.Equal(24, tb.FontSize);
            Assert.Equal("Arial", tb.FontFamily);
            Assert.IsType<SolidColorBrush>(tb.Foreground);
            Assert.Equal(TextAlignment.Center, tb.TextAlignment);
            Assert.Equal(TextWrapping.Wrap, tb.TextWrapping);
        }

        [Fact]
        public void Text_StoredAsNonString_ReadsThroughToString()
        {
            // A binding may store its source value unconverted (Minesweeper binds a TimeSpan);
            // reading Text must not throw from every paint of the page.
            var tb = new TextBlock();
            tb.SetValue(TextBlock.TextProperty, System.TimeSpan.FromSeconds(75));
            Assert.Equal("00:01:15", tb.Text);
        }
    }
}

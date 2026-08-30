using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    /// <summary>
    /// <see cref="Image.Source"/> is typed <see cref="ImageSource"/>, not <c>string</c>, because
    /// patched user IL calls <c>set_Source(System.Windows.Media.ImageSource)</c>. These tests were
    /// written against the earlier <c>string</c>-typed property and were left behind when it
    /// changed; they assign/read through <see cref="ImageSource"/> now. The raw-string form still
    /// works from XAML — that is what <see cref="XamlLoad_AppliesSourceAndStretch"/> covers, and
    /// XamlTypeConverter stores the attribute verbatim in <see cref="ImageSource.Path"/> without
    /// resolving it (resolution is deferred to render time, against the install folder).
    /// </summary>
    public class ImageTests
    {
        [Fact]
        public void Defaults_StretchUniform_SourceNull()
        {
            var img = new Image();
            Assert.Null(img.Source);
            Assert.Equal(Stretch.Uniform, img.Stretch);
        }

        [Fact]
        public void Source_NullOrUnloadable_MeasuresEmpty()
        {
            var img = new Image { Source = new ImageSource("/no/such/path/image.png") };
            img.Measure(new Size(200, 200));
            Assert.True(img.DesiredSize.IsEmpty);
        }

        [Fact]
        public void Source_Null_MeasuresEmpty()
        {
            var img = new Image();
            img.Measure(new Size(200, 200));
            Assert.True(img.DesiredSize.IsEmpty);
        }

        [Fact]
        public void XamlLoad_AppliesSourceAndStretch()
        {
            string xaml = @"
<Image xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
       Source=""logo.png"" Stretch=""Fill"" />";
            var img = (Image)XamlReader.Load(xaml);
            Assert.NotNull(img.Source);
            Assert.Equal("logo.png", img.Source!.Path);
            Assert.Equal(Stretch.Fill, img.Stretch);
        }
    }
}

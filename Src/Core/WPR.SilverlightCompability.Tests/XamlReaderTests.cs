using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    public class XamlReaderTests
    {
        private const string PresentationXmlns =
            "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";
        private const string XXmlns =
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

        [Fact]
        public void Load_ResolvesPresentationDefaultNamespace()
        {
            string xaml = $"<PhoneApplicationPage {PresentationXmlns} />";
            var result = XamlReader.Load(xaml);
            Assert.IsType<PhoneApplicationPage>(result);
        }

        [Fact]
        public void Load_SetsStringDP_FromAttribute()
        {
            string xaml = $"<PhoneApplicationPage {PresentationXmlns} Title=\"Hello\" />";
            var page = (PhoneApplicationPage)XamlReader.Load(xaml);
            Assert.Equal("Hello", page.Title);
        }

        [Fact]
        public void Load_SetsDoubleDP_FromAttribute()
        {
            string xaml = $"<PhoneApplicationPage {PresentationXmlns} Width=\"320\" Height=\"480\" />";
            var page = (PhoneApplicationPage)XamlReader.Load(xaml);
            Assert.Equal(320, page.Width);
            Assert.Equal(480, page.Height);
        }

        [Fact]
        public void Load_SetsBrushDP_FromHexColor()
        {
            string xaml = $"<PhoneApplicationPage {PresentationXmlns} Background=\"#FF112233\" />";
            var page = (PhoneApplicationPage)XamlReader.Load(xaml);
            var brush = Assert.IsType<SolidColorBrush>(page.Background);
            Assert.Equal(0xFF, brush.Color.A);
            Assert.Equal(0x11, brush.Color.R);
            Assert.Equal(0x22, brush.Color.G);
            Assert.Equal(0x33, brush.Color.B);
        }

        [Fact]
        public void Load_SetsBrushDP_FromNamedColor()
        {
            string xaml = $"<PhoneApplicationPage {PresentationXmlns} Background=\"Red\" />";
            var page = (PhoneApplicationPage)XamlReader.Load(xaml);
            var brush = Assert.IsType<SolidColorBrush>(page.Background);
            Assert.Equal(Color.FromRgb(0xFF, 0, 0), brush.Color);
        }

        [Fact]
        public void Load_SetsThicknessDP_FromCsv()
        {
            string xaml = $"<PhoneApplicationPage {PresentationXmlns} Margin=\"1,2,3,4\" />";
            var page = (PhoneApplicationPage)XamlReader.Load(xaml);
            Assert.Equal(new Thickness(1, 2, 3, 4), page.Margin);
        }

        [Fact]
        public void Load_SetsThicknessDP_FromUniform()
        {
            string xaml = $"<PhoneApplicationPage {PresentationXmlns} Margin=\"5\" />";
            var page = (PhoneApplicationPage)XamlReader.Load(xaml);
            Assert.Equal(new Thickness(5), page.Margin);
        }

        [Fact]
        public void Load_SetsEnumDP_FromCaseInsensitiveName()
        {
            string xaml = $"<PhoneApplicationPage {PresentationXmlns} HorizontalAlignment=\"left\" />";
            var page = (PhoneApplicationPage)XamlReader.Load(xaml);
            Assert.Equal(HorizontalAlignment.Left, page.HorizontalAlignment);
        }

        [Fact]
        public void Load_AutoOnDouble_ProducesNaN()
        {
            string xaml = $"<PhoneApplicationPage {PresentationXmlns} Width=\"Auto\" />";
            var page = (PhoneApplicationPage)XamlReader.Load(xaml);
            Assert.True(double.IsNaN(page.Width));
        }

        [Fact]
        public void Load_PropertyElementSyntax_OverridesAttribute()
        {
            string xaml = $@"
<PhoneApplicationPage {PresentationXmlns} Background=""Red"">
  <PhoneApplicationPage.Background>
    <SolidColorBrush Color=""Blue"" />
  </PhoneApplicationPage.Background>
</PhoneApplicationPage>";
            var page = (PhoneApplicationPage)XamlReader.Load(xaml);
            var brush = Assert.IsType<SolidColorBrush>(page.Background);
            Assert.Equal(Color.FromRgb(0, 0, 0xFF), brush.Color);
        }

        [Fact]
        public void Load_XName_RegistersAndSetsNameProperty()
        {
            string xaml = $"<PhoneApplicationPage {PresentationXmlns} {XXmlns} x:Name=\"MyPage\" />";
            var page = (PhoneApplicationPage)XamlReader.Load(xaml);
            Assert.Equal("MyPage", page.Name);
            Assert.Same(page, page.FindName("MyPage"));
        }

        [Fact]
        public void Load_UnknownProperty_IsSkipped()
        {
            // The reader is lenient by design (XamlReader pass 1/2): one bad attribute or child is logged
            // and skipped rather than losing the whole page, so a game whose XAML names something WPR
            // has not shimmed still gets the rest of its tree.
            string xaml = $"<PhoneApplicationPage {PresentationXmlns} NotAProperty=\"x\" />";
            Assert.IsType<PhoneApplicationPage>(XamlReader.Load(xaml));
        }

        [Fact]
        public void Load_UnknownType_Throws()
        {
            string xaml = $"<NotAType {PresentationXmlns} />";
            Assert.Throws<XamlParseException>(() => XamlReader.Load(xaml));
        }

        [Fact]
        public void Load_DirectChildElementOnNonContentControl_IsSkipped()
        {
            // ColumnDefinition has no [ContentProperty], so the child has nowhere to go.
            // The reader is lenient by design (XamlReader pass 1/2): one bad attribute or child is logged
            // and skipped rather than losing the whole page, so a game whose XAML names something WPR
            // has not shimmed still gets the rest of its tree.
            string xaml = $@"
<ColumnDefinition {PresentationXmlns}>
  <SolidColorBrush Color=""Red"" />
</ColumnDefinition>";
            Assert.IsType<ColumnDefinition>(XamlReader.Load(xaml));
        }

        [Fact]
        public void Load_ClrNamespaceWithRedirect_ResolvesShim()
        {
            // Real WP XAPs ship XAML referencing Microsoft.Phone.Controls/Microsoft.Phone.
            // After parsing (no patching of resources), the redirect table sends it to our shim.
            string xaml = @"<phone:PhoneApplicationPage
                              xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                              xmlns:phone=""clr-namespace:Microsoft.Phone.Controls;assembly=Microsoft.Phone"" />";
            var result = XamlReader.Load(xaml);
            Assert.IsType<PhoneApplicationPage>(result);
        }

        [Fact]
        public void Load_EnumPropertyElement_Works()
        {
            string xaml = $@"
<PhoneApplicationPage {PresentationXmlns}>
  <PhoneApplicationPage.HorizontalAlignment>Center</PhoneApplicationPage.HorizontalAlignment>
</PhoneApplicationPage>";
            var page = (PhoneApplicationPage)XamlReader.Load(xaml);
            Assert.Equal(HorizontalAlignment.Center, page.HorizontalAlignment);
        }
    }
}

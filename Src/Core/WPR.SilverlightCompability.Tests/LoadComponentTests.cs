using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    public class LoadComponentTests
    {
        // Mimics generated code-behind: a partial class extending PhoneApplicationPage
        // with named fields and an event handler method.
        public class FakeMainPage : PhoneApplicationPage
        {
            public StackPanel layoutRoot = null!;
            public Button actionBtn = null!;
            public TextBlock counter = null!;

            public int ClickCount;

            public void OnAction(object sender, RoutedEventArgs e)
            {
                ClickCount++;
                if (counter != null) counter.Text = "Count: " + ClickCount;
            }
        }

        [Fact]
        public void LoadComponent_LoadsIntoExistingInstance()
        {
            var page = new FakeMainPage();
            string xaml = @"
<PhoneApplicationPage xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                      xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                      x:Class=""WPR.SilverlightCompability.Tests.LoadComponentTests+FakeMainPage""
                      Title=""Test"" Background=""Black"" />";
            XamlReader.LoadComponent(page, xaml);
            Assert.Equal("Test", page.Title);
            Assert.IsType<SolidColorBrush>(page.Background);
        }

        [Fact]
        public void LoadComponent_WiresXNameFields()
        {
            var page = new FakeMainPage();
            string xaml = @"
<PhoneApplicationPage xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                      xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                      x:Class=""WPR.SilverlightCompability.Tests.LoadComponentTests+FakeMainPage"">
  <StackPanel x:Name=""layoutRoot"">
    <Button x:Name=""actionBtn"" Content=""Go"" />
    <TextBlock x:Name=""counter"" Text=""Count: 0"" />
  </StackPanel>
</PhoneApplicationPage>";
            XamlReader.LoadComponent(page, xaml);
            Assert.NotNull(page.layoutRoot);
            Assert.NotNull(page.actionBtn);
            Assert.NotNull(page.counter);
            Assert.Equal("Go", page.actionBtn.Content);
        }

        [Fact]
        public void LoadComponent_WiresEventHandlersToCodeBehindMethods()
        {
            var page = new FakeMainPage();
            string xaml = @"
<PhoneApplicationPage xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                      xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                      x:Class=""WPR.SilverlightCompability.Tests.LoadComponentTests+FakeMainPage"">
  <StackPanel>
    <Button x:Name=""actionBtn"" Content=""Go"" Click=""OnAction"" />
    <TextBlock x:Name=""counter"" Text=""Count: 0"" />
  </StackPanel>
</PhoneApplicationPage>";
            XamlReader.LoadComponent(page, xaml);

            Assert.Equal(0, page.ClickCount);
            page.actionBtn.RaiseClick();
            Assert.Equal(1, page.ClickCount);
            Assert.Equal("Count: 1", page.counter.Text);
        }

        [Fact]
        public void LoadComponent_UnknownEventHandlerMethod_IsSkipped()
        {
            var page = new FakeMainPage();
            string xaml = @"
<PhoneApplicationPage xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                      xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <Button x:Name=""actionBtn"" Click=""ThisMethodDoesNotExist"" />
</PhoneApplicationPage>";
            // The reader is lenient by design (XamlReader pass 1/2): one bad attribute or child is logged
            // and skipped rather than losing the whole page, so a game whose XAML names something WPR
            // has not shimmed still gets the rest of its tree.
            XamlReader.LoadComponent(page, xaml);
            Assert.NotNull(page.actionBtn);
        }

        [Fact]
        public void LoadComponent_FieldTypeMismatch_SkipsAssignment()
        {
            // If a XAML x:Name resolves to a different type than the field's declared type,
            // we silently skip rather than throw.
            var page = new FakeMainPage();
            string xaml = @"
<PhoneApplicationPage xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                      xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <TextBlock x:Name=""actionBtn"" />
</PhoneApplicationPage>";
            XamlReader.LoadComponent(page, xaml);
            Assert.Null(page.actionBtn); // wasn't assigned because TextBlock is not Button
        }
    }
}

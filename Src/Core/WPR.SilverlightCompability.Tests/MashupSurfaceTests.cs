using System;
using System.Reflection;
using WPR.WindowsCompability;
using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    /// <summary>Platform members the PointAbout Mashup framework (Fox News Latest Headlines)
    /// calls, each of which used to be missing or have the wrong signature.</summary>
    public class MashupSurfaceTests
    {
        private const string Ns = @"xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""";

        private sealed class CallbackOnly : DependencyObject
        {
            public static readonly DependencyProperty IndexProperty = DependencyProperty.Register(
                "Index", typeof(int), typeof(CallbackOnly), new PropertyMetadata((d, e) => { }));
            public static readonly DependencyProperty NameProperty = DependencyProperty.Register(
                "Name", typeof(string), typeof(CallbackOnly), new PropertyMetadata((d, e) => { }));
        }

        public sealed class Holder
        {
            public System.Collections.ObjectModel.ObservableCollection<string> Names { get; } = new();
            public System.Collections.Generic.Dictionary<string, string> ByKey { get; } = new() { ["a"] = "from key" };
        }

        [Fact]
        public void Binding_IndexerPath_ReadsListAndDictionary_AndFollowsTheCollection()
        {
            var holder = new Holder();
            var first = new TextBlock();
            first.SetBinding(TextBlock.TextProperty, new Binding("Names[0]"));
            var keyed = new TextBlock();
            keyed.SetBinding(TextBlock.TextProperty, new Binding("ByKey[a]"));
            first.DataContext = holder;
            keyed.DataContext = holder;
            Assert.Equal("", first.Text ?? "");
            Assert.Equal("from key", keyed.Text);

            holder.Names.Add("first");
            Assert.Equal("first", first.Text);
        }

        [Fact]
        public void DataContextBinding_OnChild_FollowsAParentContextSetLater()
        {
            var holder = new Holder();
            holder.Names.Add("module");
            var root = new Grid();
            var child = new Border();
            root.Children.Add(child);
            child.SetBinding(FrameworkElement.DataContextProperty, new Binding("Names[0]"));
            Assert.Null(child.DataContext);

            root.DataContext = holder;
            Assert.Equal("module", child.DataContext);
        }

        [Fact]
        public void CallbackOnlyMetadata_ValueTypeDefaultsToTypeDefault()
        {
            var o = new CallbackOnly();
            Assert.Equal(0, (int)o.GetValue(CallbackOnly.IndexProperty)!);
            Assert.Null(o.GetValue(CallbackOnly.NameProperty));
        }

        [Fact]
        public void ResourceDictionary_Remove_OverloadsReturnVoid()
        {
            Assert.Equal(typeof(void), typeof(ResourceDictionary).GetMethod("Remove", new[] { typeof(string) })!.ReturnType);
            Assert.Equal(typeof(void), typeof(ResourceDictionary).GetMethod("Remove", new[] { typeof(object) })!.ReturnType);
            var rd = new ResourceDictionary { ["a"] = 1, ["b"] = 2 };
            rd.Remove("a");
            rd.Remove((object)"b");
            Assert.Empty(rd);
        }

        [Fact]
        public void Xaml_StringAndPrimitiveResources_TakeElementText()
        {
            string xaml = $@"<ResourceDictionary {Ns} xmlns:sys=""clr-namespace:System;assembly=mscorlib"">
  <sys:String x:Key=""name"">Fox News</sys:String>
  <sys:Double x:Key=""size"">22.5</sys:Double>
  <sys:Boolean x:Key=""flag"">True</sys:Boolean>
</ResourceDictionary>";
            var rd = Assert.IsType<ResourceDictionary>(XamlReader.Load(xaml));
            Assert.Equal("Fox News", rd["name"]);
            Assert.Equal(22.5, rd["size"]);
            Assert.Equal(true, rd["flag"]);
        }

        [Fact]
        public void ItemsPanelTemplate_StackPanelOrientation_IsHonoured()
        {
            string xaml = $@"<ItemsPanelTemplate {Ns}><StackPanel Orientation=""Horizontal""/></ItemsPanelTemplate>";
            var template = Assert.IsType<ItemsPanelTemplate>(XamlReader.Load(xaml));
            Assert.IsType<StackPanel>(template.LoadContent());
            Assert.NotSame(template.LoadContent(), template.LoadContent());

            // A plain ItemsControl is its own panel and takes the template's orientation...
            var items = new ItemsControl { ItemsPanel = template };
            Assert.Equal(Orientation.Horizontal, items.Orientation);

            // ...a ListBox hosts the panel itself, under its ScrollViewer.
            var list = new ListBox { ItemsPanel = template };
            list.ItemsSource = new[] { "a", "b" };
            var host = Assert.IsType<StackPanel>(list.ItemsHost);
            Assert.Equal(Orientation.Horizontal, host.Orientation);
            Assert.Equal(2, host.Children.Count);
        }

        [Fact]
        public void VirtualizingStackPanel_IsAStackPanel()
        {
            string xaml = $@"<ItemsPanelTemplate {Ns}><VirtualizingStackPanel VirtualizingStackPanel.VirtualizationMode=""Recycling""/></ItemsPanelTemplate>";
            var panel = Assert.IsType<VirtualizingStackPanel>(Assert.IsType<ItemsPanelTemplate>(XamlReader.Load(xaml)).LoadContent());
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingStackPanel.GetVirtualizationMode(panel));
        }

        [Fact]
        public void ItemContainerStyle_AppliesWhenTargetTypeFits()
        {
            var style = new Style { TargetType = typeof(TextBlock) };
            var list = new ListBox { ItemContainerStyle = style };
            list.ItemsSource = new[] { "one", "two" };
            Assert.Equal(2, list.HostChildren.Count);
            foreach (UIElement child in list.HostChildren)
                Assert.Same(style, ((FrameworkElement)child).Style);

            var other = new ListBox { ItemContainerStyle = new Style { TargetType = typeof(ListBoxItem) } };
            other.ItemsSource = new[] { "one" };
            Assert.Null(((FrameworkElement)other.HostChildren[0]).Style);
        }

        [Fact]
        public void ApplicationBar_FromXaml_ButtonsAndMenu()
        {
            string xaml = $@"<phone:PhoneApplicationPage {Ns}
    xmlns:phone=""clr-namespace:Microsoft.Phone.Controls;assembly=Microsoft.Phone""
    xmlns:shell=""clr-namespace:Microsoft.Phone.Shell;assembly=Microsoft.Phone"">
  <phone:PhoneApplicationPage.ApplicationBar>
    <shell:ApplicationBar IsMenuEnabled=""True"" Opacity=""0.5"">
      <shell:ApplicationBarIconButton IconUri=""/Images/refresh.png"" Text=""refresh""/>
      <shell:ApplicationBar.MenuItems>
        <shell:ApplicationBarMenuItem Text=""settings""/>
        <shell:ApplicationBarMenuItem Text=""about"" IsEnabled=""False""/>
      </shell:ApplicationBar.MenuItems>
    </shell:ApplicationBar>
  </phone:PhoneApplicationPage.ApplicationBar>
</phone:PhoneApplicationPage>";
            var page = Assert.IsType<PhoneApplicationPage>(XamlReader.Load(xaml));
            var bar = Assert.IsType<ApplicationBar>(page.ApplicationBar);
            Assert.Equal(0.5, bar.Opacity);
            var button = Assert.IsType<ApplicationBarIconButton>(Assert.Single(bar.Buttons));
            Assert.Equal("refresh", button.Text);
            Assert.Equal("/Images/refresh.png", button.IconUri.OriginalString);
            Assert.Equal(2, bar.MenuItems.Count);

        }

        /// <summary>The tree LazyListBox walks: GetTemplateChild("ScrollViewer"), then a
        /// VirtualizingStackPanel among its visual descendants, then containers by index.</summary>
        [Fact]
        public void ListBox_HasSilverlightsDefaultVisualTree()
        {
            var list = new ListBox();
            list.ItemsSource = new[] { "one", "two", "three" };

            var scroller = Assert.IsType<ScrollViewer>(Control.FindTemplateChild(list, "ScrollViewer"));
            Assert.Same(scroller, VisualTreeHelper.GetChild(list, 0));
            var presenter = Assert.IsType<ItemsPresenter>(VisualTreeHelper.GetChild(scroller, 0));
            var panel = Assert.IsType<VirtualizingStackPanel>(VisualTreeHelper.GetChild(presenter, 0));
            Assert.Equal(3, VisualTreeHelper.GetChildrenCount(panel));

            Assert.Same(list, ItemsControl.GetItemsOwner(panel));
            var generator = Assert.IsType<ItemContainerGenerator>(panel.ItemContainerGenerator);
            Assert.Same(panel.Children[1], generator.ContainerFromIndex(1));
            Assert.Equal(2, generator.IndexFromContainer(panel.Children[2]));
            Assert.Equal("two", list.Items[1]);
        }

        [Fact]
        public void ListBox_AppliesItsTemplateOnceBeforeFirstMeasure()
        {
            var list = new CountingListBox();
            list.Measure(new Size(480, 800));
            list.Measure(new Size(480, 800));
            Assert.Equal(1, list.Applied);
            Assert.NotNull(list.PartAtApply);
        }

        private sealed class CountingListBox : ListBox
        {
            public int Applied;
            public ScrollViewer? PartAtApply;

            public override void OnApplyTemplate()
            {
                Applied++;
                PartAtApply = Control.FindTemplateChild(this, "ScrollViewer") as ScrollViewer;
            }
        }

        [Fact]
        public void ListBox_ScrollsItsItems_AndAPanelSwapKeepsThem()
        {
            var list = new ListBox();
            var rows = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 40; i++) rows.Add("row " + i);
            list.ItemsSource = rows;
            list.Measure(new Size(480, 400));
            list.Arrange(new Rect(0, 0, 480, 400));

            var scroller = (ScrollViewer)Control.FindTemplateChild(list, "ScrollViewer")!;
            Assert.Equal(400, scroller.ViewportHeight);
            Assert.True(scroller.ExtentHeight > 400);

            list.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load($@"<ItemsPanelTemplate {Ns}><StackPanel/></ItemsPanelTemplate>");
            Assert.IsType<StackPanel>(list.ItemsHost);
            Assert.Equal(40, list.HostChildren.Count);
        }

        [Fact]
        public void ListBox_XamlItems_GoToTheItemsPanel()
        {
            string xaml = $@"<ListBox {Ns}><ListBoxItem Content=""a""/><ListBoxItem Content=""b""/></ListBox>";
            var list = Assert.IsType<ListBox>(XamlReader.Load(xaml));
            Assert.Equal(2, list.Items.Count);
            Assert.Equal(2, list.HostChildren.Count);
            Assert.IsType<ScrollViewer>(Assert.Single(list.Children));
        }

        private sealed class Outer { public Inner Child { get; } = new Inner(); }
        private sealed class Inner { public string Name => "inner"; }

        /// <summary>A binding on an element's own DataContext reads the parent's context, and
        /// settles instead of overflowing the stack.</summary>
        [Fact]
        public void DataContextBinding_TakesItsSourceFromTheParent()
        {
            string xaml = $@"<Grid {Ns}><Grid x:Name=""inner"" DataContext=""{{Binding Child}}""><TextBlock Text=""{{Binding Name}}""/></Grid></Grid>";
            var outer = Assert.IsType<Grid>(XamlReader.Load(xaml));
            outer.DataContext = new Outer();
            var inner = (Grid)outer.Children[0];
            Assert.IsType<Inner>(inner.DataContext);
            Assert.Equal("inner", ((TextBlock)inner.Children[0]).Text);
        }

        /// <summary>A collection object with no content property takes its child elements as
        /// items, and an item's {StaticResource} sees keys declared earlier in the same dictionary.</summary>
        [Fact]
        public void Xaml_CollectionObjectItems_AndStaticResourceInTheSameDictionary()
        {
            string xaml = $@"<ResourceDictionary {Ns} xmlns:t=""clr-namespace:WPR.SilverlightCompability.Tests;assembly=WPR.SilverlightCompability.Tests"">
  <DataTemplate x:Key=""View""><TextBlock Text=""v""/></DataTemplate>
  <t:TemplateEntries x:Key=""Entries"">
    <t:TemplateEntry Name=""a"" Template=""{{StaticResource View}}""/>
    <t:TemplateEntry Name=""b"" Template=""{{StaticResource View}}""/>
  </t:TemplateEntries>
</ResourceDictionary>";
            var rd = Assert.IsType<ResourceDictionary>(XamlReader.Load(xaml));
            var entries = Assert.IsType<TemplateEntries>(rd["Entries"]);
            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.Same(rd["View"], e.Template));
        }

        [Fact]
        public void PhoneApplicationFrame_Orientation_IsThePagesOrDefault()
        {
            Assert.Equal(PageOrientation.PortraitUp, new PhoneApplicationFrame().Orientation);
        }
    }

    /// <summary>An app-defined collection written as a XAML object element (IdentityMine's
    /// TemplateTypeCollection has this shape).</summary>
    public class TemplateEntries : System.Collections.Generic.List<TemplateEntry> { }

    public class TemplateEntry
    {
        public string? Name { get; set; }
        public DataTemplate? Template { get; set; }
    }
}

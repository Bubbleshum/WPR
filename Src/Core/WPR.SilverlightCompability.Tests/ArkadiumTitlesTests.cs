using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using WPR.SilverlightCompability.Threading;
using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    public sealed class Model : INotifyPropertyChanged
    {
        private int _screenSize;
        public event PropertyChangedEventHandler? PropertyChanged;

        public int ScreenSize
        {
            get => _screenSize;
            set { _screenSize = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScreenSize))); }
        }
    }

    public sealed class Owner
    {
        public Model Model { get; } = new();
    }

    public sealed class TrueToVisible : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? Visibility.Visible : Visibility.Collapsed;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public sealed class Item : DependencyObject
    {
    }

    public sealed class ItemList : DependencyObjectCollection<Item>
    {
    }

    public sealed class Holder : FrameworkElement
    {
        public Holder() => Items = new ItemList();

        public ItemList Items { get; set; }
    }

    /// <summary>The Silverlight behaviour Microsoft Mahjong, Minesweeper and Solitaire (Arkadium) needed.</summary>
    [Collection("Dispatcher")]
    public class ArkadiumTitlesTests
    {
        private sealed class FixedSizeChild : FrameworkElement
        {
            private readonly Size _size;
            public FixedSizeChild(double w, double h) => _size = new Size(w, h);
            protected override Size MeasureOverride(Size availableSize) => _size;
        }

        private const string Ns = @"xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" " +
                                  @"xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"" " +
                                  @"xmlns:t=""clr-namespace:WPR.SilverlightCompability.Tests;assembly=WPR.SilverlightCompability.Tests""";

        [Fact]
        public void StarGrid_WantsItsContent_NotTheSpaceOffered()
        {
            var grid = new Grid { VerticalAlignment = VerticalAlignment.Top };
            grid.Children.Add(new FixedSizeChild(50, 30));

            grid.Measure(new Size(400, 200));

            Assert.Equal(30, grid.DesiredSize.Height);
            Assert.Equal(50, grid.DesiredSize.Width);
        }

        [Fact]
        public void StarGrid_StillFillsWhatItIsArranged()
        {
            var grid = new Grid();
            var child = new FixedSizeChild(50, 30);
            grid.Children.Add(child);

            grid.Measure(new Size(400, 200));
            grid.Arrange(new Rect(0, 0, 400, 200));

            Assert.Equal(400, child.ActualWidth);
            Assert.Equal(200, child.ActualHeight);
        }

        [Fact]
        public void Binding_DottedPath_FollowsTheInnerObjectsChanges()
        {
            var owner = new Owner();
            var tb = new TextBlock { DataContext = owner };
            tb.SetBinding(TextBlock.TextProperty, new Binding("Model.ScreenSize"));
            Assert.Equal("0", tb.Text);

            owner.Model.ScreenSize = 600;

            Assert.Equal("600", tb.Text);
        }

        [Fact]
        public void Binding_ConverterFromStaticResource_IsApplied()
        {
            string xaml = $@"
<Grid {Ns}>
  <Grid.Resources><t:TrueToVisible x:Key=""Vis"" /></Grid.Resources>
  <TextBlock x:Name=""Label"" Visibility=""{{Binding Shown, Converter={{StaticResource Vis}}}}"" />
</Grid>";
            var grid = (Grid)XamlReader.Load(xaml);
            var label = (TextBlock)grid.FindName("Label")!;

            grid.DataContext = new { Shown = false };
            Assert.Equal(Visibility.Collapsed, label.Visibility);

            grid.DataContext = new { Shown = true };
            Assert.Equal(Visibility.Visible, label.Visibility);
        }

        [Fact]
        public void Dispatcher_QueuesPostsForTheAttachedThread()
        {
            var ran = new List<int>();
            Dispatcher.AttachToCurrentThread();
            try
            {
                Dispatcher.Shared.BeginInvoke(() => ran.Add(1));
                Assert.Empty(ran);

                Dispatcher.PumpQueued();
                Assert.Equal(new[] { 1 }, ran);

                // A post made while pumping waits for the next pump.
                Dispatcher.Shared.BeginInvoke(() => Dispatcher.Shared.BeginInvoke(() => ran.Add(3)));
                Dispatcher.PumpQueued();
                Assert.Equal(new[] { 1 }, ran);
                Dispatcher.PumpQueued();
                Assert.Equal(new[] { 1, 3 }, ran);
            }
            finally
            {
                Dispatcher.Detach();
            }

            Dispatcher.Shared.BeginInvoke(() => ran.Add(4));
            Assert.Equal(new[] { 1, 3, 4 }, ran);
        }

        [Fact]
        public void RichTextBox_PresentsEachParagraphAsText()
        {
            var box = new RichTextBox { FontSize = 25 };
            var picture = new FixedSizeChild(35, 35);
            var red = new SolidColorBrush(Color.FromArgb(255, 255, 1, 1));
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new Run { Text = "A " });
            paragraph.Inlines.Add(new InlineUIContainer { Child = picture });
            paragraph.Inlines.Add(new Run { Text = " means " });
            paragraph.Inlines.Add(new Run { Text = "one", Foreground = red });
            box.Blocks.Add(paragraph);

            box.Measure(new Size(600, double.PositiveInfinity));
            box.Arrange(new Rect(0, 0, 600, box.DesiredSize.Height));

            var panel = Assert.IsType<StackPanel>(box.Content);
            var flow = Assert.IsType<InlineFlowPanel>(Assert.Single(panel.Children));
            var words = new List<string>();
            foreach (UIElement child in flow.Children)
            {
                if (child is TextBlock word) words.Add(word.Text);
            }

            Assert.Equal(new[] { "A", "means", "one" }, words);
            Assert.Contains(picture, flow.Children);
            Assert.Same(red, ((TextBlock)flow.Children[^1]).Foreground);

            // In line: the picture sits after "A" and before "means", on the same line.
            var a = (TextBlock)flow.Children[0];
            var means = (TextBlock)flow.Children[4];
            Assert.Equal("means", means.Text);
            Assert.True(picture.ArrangedRect.X > a.ArrangedRect.Right);
            Assert.True(means.ArrangedRect.X > picture.ArrangedRect.Right);

            box.Blocks.Clear();
            box.Measure(new Size(600, double.PositiveInfinity));
            Assert.Empty(((StackPanel)box.Content!).Children);
        }

        [Fact]
        public void Xaml_ChildrenOfAWritableCollectionProperty_AreAddedToIt()
        {
            string xaml = $@"
<t:Holder {Ns}>
  <t:Holder.Items><t:Item /><t:Item /><t:Item /></t:Holder.Items>
</t:Holder>";
            var holder = new Holder();
            ItemList created = holder.Items;

            var loaded = (Holder)XamlReader.Load(xaml);

            Assert.Equal(3, loaded.Items.Count);
            Assert.NotSame(created, loaded.Items);
        }

        [Fact]
        public void Xaml_TransformGroup_TakesItsChildrenAsContent()
        {
            string xaml = $@"
<Border {Ns}>
  <Border.RenderTransform>
    <TransformGroup><ScaleTransform ScaleX=""2"" /><TranslateTransform X=""5"" /></TransformGroup>
  </Border.RenderTransform>
</Border>";
            var border = (Border)XamlReader.Load(xaml);

            var group = Assert.IsType<TransformGroup>(border.RenderTransform);
            Assert.Equal(2, group.Children.Count);
        }
    }

    [CollectionDefinition("Dispatcher", DisableParallelization = true)]
    public class DispatcherCollection
    {
    }
}

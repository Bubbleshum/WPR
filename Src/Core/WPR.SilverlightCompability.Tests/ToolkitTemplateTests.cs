using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    /// <summary>A control whose implicit style is in this assembly's themes/generic.xaml.</summary>
    public class ThemedControl : Control
    {
        public ThemedControl() { DefaultStyleKey = typeof(ThemedControl); }
    }

    /// <summary>What the WP7 toolkit's Pivot (shipped in an app's own Microsoft.Phone.Controls.dll)
    /// needs from the platform: its default template, its containers prepared, and list sources
    /// that only half implement their interfaces (Fox News).</summary>
    public class ToolkitTemplateTests
    {
        [Fact]
        public void DefaultStyle_TemplateComesFromTheTypesOwnGenericXaml()
        {
            ControlTemplate? template = DefaultStyles.FindTemplate(typeof(ThemedControl));
            Assert.NotNull(template);
            var root = Assert.IsType<Grid>(template!.LoadContent());
            Assert.IsType<ItemsPresenter>(root.Children[0]);
            // A keyed style is not a default style.
            Assert.Null(DefaultStyles.FindTemplate(typeof(ToolkitTemplateTests)));
        }

        [Fact]
        public void ApplyTemplate_BuildsTheDefaultTemplateSoPartsAreFound()
        {
            var control = new ThemedControl();
            Assert.Equal(typeof(ThemedControl), control.DefaultStyleKey);
            Assert.True(control.ApplyTemplate());
            Assert.IsType<ItemsPresenter>(control.GetTemplateChild("PartPresenter"));
        }

        private sealed class CountingItems : ItemsControl
        {
            public readonly List<object> Prepared = new();
            protected override void PrepareContainerForItemOverride(DependencyObject element, object item) => Prepared.Add(item);
        }

        [Fact]
        public void ItemsDeclaredInXaml_ArePreparedOnce_BeforeMeasure()
        {
            var items = new CountingItems();
            var a = new Border();
            var b = new Border();
            items.Children.Add(a);
            items.Children.Add(b);
            items.Measure(new Size(100, 100));
            items.Measure(new Size(100, 100));
            Assert.Equal(new object[] { a, b }, items.Prepared);
        }

        [Fact]
        public void IsEnabledChanged_IsRaised()
        {
            var control = new ThemedControl();
            int raised = 0;
            control.IsEnabledChanged += (s, e) => raised++;
            control.IsEnabled = false;
            Assert.Equal(1, raised);
        }

        /// <summary>Fox News's RssItemObservableCollection: the generic enumerator works, the
        /// non-generic one throws; its Add events come late and one index past the item.</summary>
        private sealed class HalfList : IEnumerable<string>, INotifyCollectionChanged
        {
            public readonly List<string> Items = new();
            public event NotifyCollectionChangedEventHandler? CollectionChanged;
            public IEnumerator<string> GetEnumerator() => Items.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => throw new NotImplementedException();
            public void RaiseAdd(string item, int index) =>
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
        }

        private static object[] Shown(ItemsControl list)
        {
            var result = new object[list.Items.Count];
            for (int i = 0; i < result.Length; i++) result[i] = list.Items[i];
            return result;
        }

        [Fact]
        public void ItemsSource_WithOnlyAGenericEnumerator_AndLateWrongIndexAdds_ShowsTheSource()
        {
            var source = new HalfList();
            var list = new ItemsControl { ItemsSource = source };
            source.Items.Add("one");
            source.Items.Add("two");
            source.RaiseAdd("one", 1);   // does not fit: rebuilt, and the source now holds both
            source.RaiseAdd("two", 2);   // late: the rows already show it
            Assert.Equal(new object[] { "one", "two" }, Shown(list));

            source.Items.Add("three");
            source.RaiseAdd("three", 2); // in step: inserted where it is
            Assert.Equal(new object[] { "one", "two", "three" }, Shown(list));
        }
    }
}

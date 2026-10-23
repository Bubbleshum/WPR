using System.Reflection;
using WPR.WindowsCompability;
using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    public class ResourceDictionaryTests
    {
        [Fact]
        public void MergedDictionaries_HasSilverlightSignature()
        {
            // Game IL binds get_MergedDictionaries by its return type; it must be
            // PresentationFrameworkCollection<ResourceDictionary>, as in Silverlight.
            MethodInfo getter = typeof(ResourceDictionary).GetProperty(nameof(ResourceDictionary.MergedDictionaries))!.GetMethod!;
            Assert.Equal(typeof(PresentationFrameworkCollection<ResourceDictionary>), getter.ReturnType);
            Assert.Null(typeof(ResourceDictionary).GetProperty(nameof(ResourceDictionary.MergedDictionaries))!.SetMethod);
        }

        [Fact]
        public void Lookup_OwnKeysFirst_ThenLastMergedFirst()
        {
            var first = new ResourceDictionary { ["a"] = "first", ["b"] = "first" };
            var second = new ResourceDictionary { ["a"] = "second" };
            var nested = new ResourceDictionary { ["c"] = "nested" };
            first.MergedDictionaries.Add(nested);

            var root = new ResourceDictionary { ["own"] = "root", ["b"] = "root" };
            root.MergedDictionaries.Add(first);
            root.MergedDictionaries.Add(second);

            Assert.Equal("root", root["b"]);     // own key beats every merge
            Assert.Equal("second", root["a"]);   // last merged wins
            Assert.Equal("nested", root["c"]);   // merges nest
            Assert.True(root.Contains("c"));
            Assert.Null(root["missing"]);
        }

        [Fact]
        public void MergedDictionaries_AcceptsAdditionsThroughIList()
        {
            // XamlReader appends to a read-only collection property through IList.
            var root = new ResourceDictionary();
            System.Collections.IList list = root.MergedDictionaries;
            list.Add(new ResourceDictionary { ["k"] = 1 });
            Assert.Equal(1, root["k"]);
        }

        [Fact]
        public void Xaml_MergedDictionaries_AreLoaded()
        {
            const string xaml = @"<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <ResourceDictionary.MergedDictionaries>
    <ResourceDictionary><SolidColorBrush x:Key=""k"" Color=""Red""/></ResourceDictionary>
    <ResourceDictionary><SolidColorBrush x:Key=""k"" Color=""Blue""/></ResourceDictionary>
  </ResourceDictionary.MergedDictionaries>
</ResourceDictionary>";
            var rd = Assert.IsType<ResourceDictionary>(XamlReader.Load(xaml));
            Assert.Equal(2, rd.MergedDictionaries.Count);
            var brush = Assert.IsType<SolidColorBrush>(rd["k"]);
            Assert.Equal(Colors.Blue, brush.Color);
        }
    }
}

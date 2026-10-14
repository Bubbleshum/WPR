using System;
using System.IO;
using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    /// <summary>
    /// The framework gaps that kept Flowerz and Minesweeper from running on the XNA host
    /// (convergence plan Stage C). Each test is one fix.
    /// </summary>
    public class PureSilverlightHostTests
    {
        private class PageA : PhoneApplicationPage { }
        private class PageB : PhoneApplicationPage { }

        [Fact]
        public void MouseButtonEventArgs_IsAMouseEventArgs_WithPosition()
        {
            // Flowerz's board reads ((MouseEventArgs)e).GetPosition(canvas) in a
            // MouseLeftButtonDown handler.
            MouseEventArgs e = new MouseButtonEventArgs { PagePosition = new Point(30, 40) };
            Assert.Equal(new Point(30, 40), e.GetPosition(null));
        }

        [Fact]
        public void ListBox_IsASelector_AndSelectsByIndex()
        {
            var list = new ListBox { ItemsSource = new[] { "a", "b", "c" } };
            Selector selector = list;
            int changes = 0;
            selector.SelectionChanged += (_, _) => changes++;

            selector.SelectedIndex = 1;

            Assert.Equal("b", selector.SelectedItem);
            Assert.Same(ListBox.SelectedIndexProperty, Selector.SelectedIndexProperty);
            Assert.Equal(1, changes);
        }

        [Fact]
        public void ListBox_SelectContainer_SelectsTheTappedRow()
        {
            var list = new ListBox { ItemsSource = new[] { "a", "b", "c" } };
            Assert.True(list.SelectContainer(list.Children[2]));
            Assert.Equal("c", list.SelectedItem);
        }

        [Fact]
        public void PropertyPath_ParamsForm_SubstitutesDependencyProperties()
        {
            Assert.Equal("(UIElement.Opacity)", new PropertyPath("(UIElement.Opacity)", new object[0]).Path);
            Assert.Equal("(UIElement.Opacity)", new PropertyPath("(0)", UIElement.OpacityProperty).Path);
        }

        [Fact]
        public void Frame_ContentWrittenThroughContentControl_ReplacesThePage()
        {
            // Flowerz's AppFrame does ((ContentControl)this).Content = page.
            var frame = new PhoneApplicationFrame();
            var page = new PageB();
            int replaced = 0;
            frame.WprContentReplaced += (_, _) => replaced++;

            frame.SetValue(ContentControl.ContentProperty, page);

            Assert.Same(page, frame.Content);
            Assert.Equal(1, replaced);
        }

        [Fact]
        public void ElementAddedToALoadedTree_GetsLoaded_OnTheNextDrain()
        {
            var root = new Grid();
            FrameworkElement.RaiseLoadedTree(root);
            var child = new Border();
            bool loaded = false;
            child.Loaded += (_, _) => loaded = true;

            root.Children.Add(child);
            Assert.False(loaded); // deferred, as Silverlight does
            FrameworkElement.RaisePendingLoaded();

            Assert.True(loaded);
        }

        [Fact]
        public void XamlReader_Load_AcceptsALeadingByteOrderMark()
        {
            object o = XamlReader.Load("﻿<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />");
            Assert.IsType<Grid>(o);
        }

        [Fact]
        public void GetResourceStream_FallsBackToAFileInThePackage()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpr-getres-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(dir, "Content", "Strings"));
            File.WriteAllText(System.IO.Path.Combine(dir, "Content", "Strings", "x.xml"), "<x/>");
            string? previous = HostContext.CurrentInstallFolder;
            try
            {
                HostContext.CurrentInstallFolder = dir;
                var info = WPR.WindowsCompability.Application.GetResourceStream(
                    new Uri("Content/Strings/x.xml", UriKind.Relative), typeof(PureSilverlightHostTests).Assembly);
                Assert.NotNull(info?.Stream);
                info!.Stream!.Dispose();
            }
            finally
            {
                HostContext.CurrentInstallFolder = previous;
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ImagePaths_ClampLeadingParentSegmentsAtThePackageRoot()
        {
            Assert.Equal("Content/Images/TipStart.png",
                SilverlightImageDecoder.ClampToPackageRoot("../../../Content/Images/TipStart.png"));
        }
    }
}

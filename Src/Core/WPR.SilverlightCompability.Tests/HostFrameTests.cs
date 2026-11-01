using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Phone.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input.Touch;
using Xunit;
using Point = System.Windows.Point;

namespace WPR.SilverlightCompability.Tests
{
    /// <summary>
    /// Host coordinates on a landscape page are the phone's portrait frame (<see cref="HostFrame"/>):
    /// what <c>GetTouchPoints(null)</c>, <c>TransformToVisual</c> through the root and
    /// <c>FindElementsInHostCoordinates</c> answer, and they must agree with each other.
    /// </summary>
    [Collection("TouchPanel")]
    public class HostFrameTests : IDisposable
    {
        public void Dispose() => Touch.ResetForNewLaunch();

        /// <summary>An 800x480 landscape page with a painted 100x50 leaf at (40,60).</summary>
        private static (Canvas root, Border leaf) LandscapeTree()
        {
            var root = new Canvas { Background = new SolidColorBrush(Colors.Black) };
            var leaf = new Border { Width = 100, Height = 50, Background = new SolidColorBrush(Colors.White) };
            Canvas.SetLeft(leaf, 40);
            Canvas.SetTop(leaf, 60);
            root.Children.Add(leaf);
            root.Measure(new Size(800, 480));
            root.Arrange(new Rect(0, 0, 800, 480));
            return (root, leaf);
        }

        [Theory]
        [InlineData(false, 70, 750)]  // LandscapeRight (and plain Landscape): (y, 800 - x)
        [InlineData(true, 410, 50)]   // LandscapeLeft: (480 - y, x)
        public void TouchPointsRelativeToNothing_AreInThePortraitFrame(bool left, double hostX, double hostY)
        {
            HostFrame.Set(landscape: true, landscapeLeft: left, 800, 480);
            var (root, leaf) = LandscapeTree();
            Point host = default, local = default;
            Touch.FrameReported += (_, e) =>
            {
                host = e.GetTouchPoints(null)[0].Position;
                local = e.GetPrimaryTouchPoint(leaf)!.Position;
            };

            TouchFrameReporter.Report(root, new TouchCollection(new[] { new TouchLocation(3, TouchLocationState.Pressed, new Vector2(50, 70)) }));

            Assert.Equal(hostX, host.X, 9);
            Assert.Equal(hostY, host.Y, 9);
            Assert.Equal(10, local.X, 9);   // relative to an element: unchanged, page coordinates
            Assert.Equal(10, local.Y, 9);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void HostPoint_FindsTheElement_AndMapsBackIntoIt(bool left)
        {
            HostFrame.Set(landscape: true, landscapeLeft: left, 800, 480);
            var (root, leaf) = LandscapeTree();
            Point host = HostFrame.ToHost(new Point(50, 70));

            // The WP7 Toolkit: hit test the host point, then map it into the element.
            Assert.Same(leaf, VisualTreeHelper.FindElementsInHostCoordinates(host, root).First());
            Point inLeaf = leaf.TransformToVisual(null).Inverse!.Transform(host);
            Assert.Equal(10, inLeaf.X, 9);
            Assert.Equal(10, inLeaf.Y, 9);

            // The WP8 Toolkit: the root visual's space is the host's.
            Point viaRoot = root.TransformToVisual(leaf).Transform(host);
            Assert.Equal(10, viaRoot.X, 9);
            Assert.Equal(10, viaRoot.Y, 9);

            // Between two elements below the root the turn cancels.
            Point same = leaf.TransformToVisual(leaf).Transform(new Point(3, 4));
            Assert.Equal(3, same.X, 9);
            Assert.Equal(4, same.Y, 9);
        }

        [Fact]
        public void PortraitPage_IsUnchanged()
        {
            HostFrame.Set(landscape: false, landscapeLeft: false, 800, 480);
            var (root, leaf) = LandscapeTree();
            Point p = leaf.TransformToVisual(null).Transform(new Point(5, 5));
            Assert.Equal(45, p.X);
            Assert.Equal(65, p.Y);
            Assert.Same(leaf, VisualTreeHelper.FindElementsInHostCoordinates(new Point(50, 70), root).First());
        }

        [Fact]
        public void ChooserShow_IsVirtual_AndAnswersCancelled()
        {
            // Asphalt 7 calls Show through ChooserBase<TaskEventArgs>.
            var invite = new GameInviteTask { SessionId = "s" };
            TaskResult? result = null;
            invite.Completed += (_, e) => result = e.TaskResult;
            ((ChooserBase<TaskEventArgs>)invite).Show();
            Assert.Equal(TaskResult.Cancel, result);
        }
    }
}

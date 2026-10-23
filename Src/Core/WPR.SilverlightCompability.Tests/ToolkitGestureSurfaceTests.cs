using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input.Touch;
using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    /// <summary>
    /// What the WP Toolkit's own GestureListener calls into (patcher version 47): Touch.FrameReported,
    /// TouchAction's real values, FindElementsInHostCoordinates, TransformToVisual and MatrixTransform.
    /// </summary>
    public class ToolkitGestureSurfaceTests : IDisposable
    {
        public void Dispose() => Touch.ResetForNewLaunch();

        /// <summary>A 480x800 canvas with a painted 100x50 leaf at (40,60); unpainted elements are not hit.</summary>
        private static (Canvas root, Border leaf) Tree()
        {
            var root = new Canvas { Background = new SolidColorBrush(Colors.Black) };
            var leaf = new Border { Width = 100, Height = 50, Background = new SolidColorBrush(Colors.White) };
            Canvas.SetLeft(leaf, 40);
            Canvas.SetTop(leaf, 60);
            root.Children.Add(leaf);
            root.Measure(new Size(480, 800));
            root.Arrange(new Rect(0, 0, 480, 800));
            return (root, leaf);
        }

        [Fact]
        public void TouchAction_HasSilverlightValues()
        {
            // Game IL compares the constants: the Toolkit tests Action != 3 for "still down".
            Assert.Equal(1, (int)TouchAction.Down);
            Assert.Equal(2, (int)TouchAction.Move);
            Assert.Equal(3, (int)TouchAction.Up);
        }

        [Fact]
        public void Matrix_DefaultIsIdentity_AndInverts()
        {
            Assert.True(default(Matrix).IsIdentity);
            Assert.Equal(1, default(Matrix).M11);

            var m = new MatrixTransform(new Matrix(2, 0, 0, 4, 10, 20));
            Point p = m.Transform(new Point(1, 1));
            Assert.Equal(12, p.X);
            Assert.Equal(24, p.Y);

            var inverse = Assert.IsType<MatrixTransform>(m.Inverse);
            Point back = inverse.Transform(p);
            Assert.Equal(1, back.X, 9);
            Assert.Equal(1, back.Y, 9);
        }

        [Fact]
        public void Matrix_Singular_HasNoInverse()
        {
            Assert.Null(new MatrixTransform(new Matrix(0, 0, 0, 0, 5, 5)).Inverse);
        }

        [Fact]
        public void TransformToVisual_TranslatesByLayoutOffset()
        {
            var (root, leaf) = Tree();

            var toRoot = Assert.IsType<MatrixTransform>(leaf.TransformToVisual(null));
            Point p = toRoot.Transform(new Point(5, 5));
            Assert.Equal(45, p.X);
            Assert.Equal(65, p.Y);

            // The Toolkit's GestureEventArgs.GetPosition: root point -> element-relative.
            Point local = leaf.TransformToVisual(null).Inverse!.Transform(new Point(45, 65));
            Assert.Equal(5, local.X);
            Assert.Equal(5, local.Y);

            Point relative = root.TransformToVisual(leaf).Transform(new Point(45, 65));
            Assert.Equal(5, relative.X);
            Assert.Equal(5, relative.Y);
        }

        [Fact]
        public void FindElementsInHostCoordinates_ReturnsTopmostFirst()
        {
            var (root, leaf) = Tree();

            List<UIElement> hits = VisualTreeHelper.FindElementsInHostCoordinates(new Point(50, 70), root).ToList();
            Assert.Same(leaf, hits[0]);
            Assert.Contains(root, hits);

            List<UIElement> outside = VisualTreeHelper.FindElementsInHostCoordinates(new Point(300, 500), root).ToList();
            Assert.DoesNotContain(leaf, outside);
        }

        private static TouchCollection Touches(params TouchLocation[] locations) => new TouchCollection(locations);

        private static TouchLocation At(int id, TouchLocationState state, float x, float y) =>
            new TouchLocation(id, state, new Vector2(x, y));

        [Fact]
        public void Reporter_RaisesDownMoveUp_WithElementRelativePoints()
        {
            var (root, leaf) = Tree();
            var frames = new List<(TouchAction action, Point position, UIElement? over, Point primary)>();
            Touch.FrameReported += (_, e) =>
            {
                TouchPoint p = e.GetTouchPoints(leaf)[0];
                frames.Add((p.Action, p.Position, p.TouchDevice?.DirectlyOver, e.GetPrimaryTouchPoint(null)!.Position));
            };

            TouchFrameReporter.Report(root, Touches(At(7, TouchLocationState.Pressed, 50, 70)));
            TouchFrameReporter.Report(root, Touches(At(7, TouchLocationState.Moved, 60, 80)));
            TouchFrameReporter.Report(root, Touches(At(7, TouchLocationState.Released, 60, 80)));

            Assert.Equal(new[] { TouchAction.Down, TouchAction.Move, TouchAction.Up }, frames.Select(f => f.action));
            Assert.Equal(10, frames[0].position.X);   // 50 - leaf at 40
            Assert.Equal(10, frames[0].position.Y);   // 70 - leaf at 60
            Assert.Same(leaf, frames[0].over);
            Assert.Equal(60, frames[1].primary.X);    // root coordinates
        }

        [Fact]
        public void Reporter_LiftsAFingerThatVanishedWithoutARelease()
        {
            var (root, _) = Tree();
            var actions = new List<TouchAction>();
            Touch.FrameReported += (_, e) => actions.AddRange(e.GetTouchPoints(null).Select(p => p.Action));

            TouchFrameReporter.Report(root, Touches(At(3, TouchLocationState.Pressed, 50, 70)));
            TouchFrameReporter.Report(root, Touches());   // the drain cleared the slot: no Released sample
            TouchFrameReporter.Report(root, Touches());   // and nothing more after that

            Assert.Equal(new[] { TouchAction.Down, TouchAction.Up }, actions);
        }

        [Fact]
        public void Reporter_WithoutSubscribers_RaisesNothingAndKeepsNoState()
        {
            var (root, _) = Tree();
            TouchFrameReporter.Report(root, Touches(At(1, TouchLocationState.Pressed, 50, 70)));

            var actions = new List<TouchAction>();
            Touch.FrameReported += (_, e) => actions.AddRange(e.GetTouchPoints(null).Select(p => p.Action));
            TouchFrameReporter.Report(root, Touches());

            Assert.Empty(actions);   // no stale finger lifted from before anyone listened
        }
    }
}

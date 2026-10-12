using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    // The popup layer: open Popups composited above the page, in the order they opened.
    internal static partial class SoftwareVisualRasteriser
    {
        /// <summary>Where a popup's slot landed in DEVICE space during the tree pass, and the
        /// scale in effect there.</summary>
        private readonly struct PopupSlot
        {
            public PopupSlot(double x, double y, double scaleX, double scaleY)
            {
                X = x;
                Y = y;
                ScaleX = scaleX;
                ScaleY = scaleY;
            }

            public double X { get; }
            public double Y { get; }
            public double ScaleX { get; }
            public double ScaleY { get; }
        }

        [ThreadStatic] private static Dictionary<Popup, PopupSlot>? _popupSlots;
        [ThreadStatic] private static Surface? _popupRootSurface;

        /// <summary>Starts a paint: forgets the previous frame's popup slots.</summary>
        private static void BeginPopupPass(Surface surface)
        {
            (_popupSlots ??= new Dictionary<Popup, PopupSlot>(ReferenceEqualityComparer.Instance)).Clear();
            _popupRootSurface = surface;
        }

        /// <summary>
        /// Called where the tree pass meets a <see cref="Popup"/>. It paints nothing there — a
        /// popup takes no part in its parent's layout, so its slot is usually zero-sized — but
        /// the slot's position, with every ancestor transform already applied, is exactly where
        /// Silverlight puts the popup's child, so it is remembered for <see cref="PaintPopups"/>.
        /// </summary>
        /// <remarks>
        /// Only on the root surface. Inside an element being turned (see
        /// <c>PaintTransformed</c>) the tree is painted into an offscreen buffer whose
        /// coordinates mean nothing on screen; such a popup falls back to its layout position.
        /// </remarks>
        private static void NotePopupSlot(Popup popup, Rect bounds, double scaleX, double scaleY, Surface surface)
        {
            if (_popupSlots == null || !ReferenceEquals(surface, _popupRootSurface)) return;
            _popupSlots[popup] = new PopupSlot(bounds.X, bounds.Y, scaleX, scaleY);
        }

        /// <summary>
        /// Paints every open popup over <paramref name="root"/>, bottom first, unclipped by the
        /// tree they are declared in.
        /// </summary>
        /// <remarks>
        /// <para><b>Only when <paramref name="root"/> is the application's root</b> — the frame
        /// the mixed-mode host composites. Popups are a layer above the whole application, not
        /// part of any element, so a game that rasterises one of its own pages through
        /// <c>UIElementRenderer</c> gets that page and nothing floating over it. That also keeps
        /// every game-rasterised overlay byte-for-byte what it was before popups existed
        /// here.</para>
        /// <para><b>Popups that belong to no tree are included</b>, positioned from the root:
        /// <c>new Popup { Child = dialog, IsOpen = true }</c> is how WP7 code — and the toolkit's
        /// <c>ContextMenu</c> and <c>CustomMessageBox</c> — shows a dialog.</para>
        /// </remarks>
        private static void PaintPopups(UIElement root, Surface surface, int width, int height)
        {
            if (!HasPopupLayer(root)) return;

            List<PopupLayer.Entry> entries = PopupLayer.Collect(root, includeDetached: true);
            if (entries.Count == 0) return;

            var screen = new Rect(0, 0, width, height);
            Rect rootRect = root.ArrangedRect;

            foreach (PopupLayer.Entry entry in entries)
            {
                Popup popup = entry.Popup;
                if (popup.Child is not UIElement child) continue;
                if (popup.Visibility == Visibility.Collapsed) continue;

                double x, y, scaleX = 1, scaleY = 1;
                if (_popupSlots != null && _popupSlots.TryGetValue(popup, out PopupSlot slot))
                {
                    scaleX = slot.ScaleX;
                    scaleY = slot.ScaleY;
                    x = slot.X + (popup.HorizontalOffset * scaleX);
                    y = slot.Y + (popup.VerticalOffset * scaleY);
                }
                else
                {
                    // The root is painted at the surface origin whatever its own arranged
                    // offset, so a layout position relative to root's parent loses that offset.
                    x = entry.Origin.X - rootRect.X;
                    y = entry.Origin.Y - rootRect.Y;
                }

                // Not clipped by the parent: the popup layer's only bound is the screen. The
                // popup's own Opacity applies to its child; its ancestors' do not, because it is
                // not drawn inside them.
                PaintChild(child, new Rect(x, y, 0, 0), Math.Clamp(popup.Opacity, 0, 1), surface, 1, scaleX, scaleY, screen);
            }
        }

        /// <summary>Whether <paramref name="root"/> is the application's root and so carries
        /// the popup layer. See <see cref="PaintPopups"/>.</summary>
        private static bool HasPopupLayer(UIElement? root) => root != null && root.Parent == null;

        /// <summary>
        /// Folds the popup layer into the change-detection signature. Without it a dialog that
        /// opens over an otherwise unchanged page hashes identically and is never drawn, and one
        /// that closes is never erased.
        /// </summary>
        private static void SignPopups(UIElement? root, ref HashCode hash)
        {
            if (!HasPopupLayer(root)) return;

            List<PopupLayer.Entry> entries = PopupLayer.Collect(root, includeDetached: true);
            hash.Add(entries.Count);
            foreach (PopupLayer.Entry entry in entries)
            {
                Popup popup = entry.Popup;
                hash.Add(popup.OpenSequence);
                hash.Add(entry.Origin.X);
                hash.Add(entry.Origin.Y);
                hash.Add(popup.Opacity);
                hash.Add((int)popup.Visibility);
                Sign(popup.Child, ref hash, 1);
            }
        }
    }
}

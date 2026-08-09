using System.Runtime.CompilerServices;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Per-PanoramaItem vertical-scroll offset. Real WP7 panorama items become
    /// scrollable when their content exceeds the panel height (the toolkit's
    /// default template wraps the content in a ScrollViewer); we don't apply
    /// templates so the content visibly overflows. The pointer pipeline
    /// detects vertical-dominant drags inside a PanoramaItem and accumulates
    /// <see cref="ScrollY"/> here; the renderer applies it as a translation
    /// when drawing the item's Presenter and clamps it to a sensible range
    /// once the content height is known.
    /// </summary>
    internal sealed class PanoramaItemScrollState
    {
        public double ScrollY;
    }

    /// <summary>Static side-table: <c>PanoramaItem instance → its scroll state</c>.
    /// Lives until the PanoramaItem is GC'd.</summary>
    internal static class PanoramaItemScrollTable
    {
        private static readonly ConditionalWeakTable<object, PanoramaItemScrollState> _map = new();

        public static PanoramaItemScrollState GetOrCreate(object item)
            => _map.GetValue(item, _ => new PanoramaItemScrollState());

        public static PanoramaItemScrollState? TryGet(object item)
            => _map.TryGetValue(item, out var s) ? s : null;
    }
}

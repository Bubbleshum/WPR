using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace WPR.SilverlightCompability
{
    /// <summary>Static side-table: <c>Panorama instance → its PanoramaState</c>.
    /// Lives until the Panorama is GC'd.</summary>
    internal static class PanoramaStateTable
    {
        private static readonly ConditionalWeakTable<object, PanoramaState> _map = new();

        public static PanoramaState GetOrCreate(object panorama)
            => _map.GetValue(panorama, _ => new PanoramaState());

        public static PanoramaState? TryGet(object panorama)
            => _map.TryGetValue(panorama, out var s) ? s : null;
    }
}

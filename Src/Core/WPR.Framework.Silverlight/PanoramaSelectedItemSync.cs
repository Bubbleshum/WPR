using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Helper to keep the toolkit's <c>Panorama.SelectedItem</c> in sync with our
    /// own <see cref="PanoramaState.CurrentIndex"/>. WP7 user code routinely checks
    /// <c>if (panorama.SelectedItem == myItem)</c> to gate per-page actions
    /// (Minesweeper's leaderboard / achievement / help tap handlers all do this).
    /// Our renderer drives the visible item from PanoramaState alone, so without
    /// this bridge the toolkit's SelectedItem stays stuck on the first item
    /// regardless of swipes — and those gate checks all fail.
    /// </summary>
    internal static class PanoramaSelectedItemSync
    {
        // Cache per-runtime-type to handle Panorama and Pivot in the same process.
        private static readonly System.Collections.Generic.Dictionary<Type, FieldInfo?> _fieldByType = new();

        // One-shot diagnostic — log on the first successful Set per type so we can
        // see whether the sync actually ran and what got written.
        private static readonly System.Collections.Generic.HashSet<Type> _loggedTypes = new();

        /// <summary>
        /// Write <paramref name="item"/> to the panorama's SelectedItem dependency
        /// property via reflection (we can't reference Microsoft.Phone.Controls from
        /// the SilverlightCompability project). No-op if the field can't be located
        /// — leaves the toolkit's internal default in place.
        /// Also keeps the corresponding <c>SelectedIndex</c> DP in sync if present,
        /// because some user code gates on the index rather than the item.
        /// </summary>
        public static void Set(DependencyObject panorama, object? item)
        {
            if (panorama == null) return;
            Type panoType = panorama.GetType();
            FieldInfo? f = ResolveDpField(panoType, "SelectedItemProperty");
            if (f == null)
            {
                if (_loggedTypes.Add(panoType))
                {
                    Console.WriteLine(
                        $"[PanoramaSync] No SelectedItemProperty field found on '{panoType.FullName}' " +
                        "or any base. SelectedItem-gated handlers will mis-fire.");
                }
                return;
            }
            try
            {
                if (f.GetValue(null) is DependencyProperty dp)
                {
                    object? previous = panorama.GetValue(dp);
                    if (!Equals(previous, item))
                    {
                        panorama.SetValue(dp, item);
                        if (_loggedTypes.Add(panoType))
                        {
                            object? readback = panorama.GetValue(dp);
                            Console.WriteLine(
                                $"[PanoramaSync] Set SelectedItem on {panoType.Name} -> " +
                                $"{item?.GetType().Name ?? "null"} (readback={readback?.GetType().Name ?? "null"}, " +
                                $"refEq={ReferenceEquals(readback, item)})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PanoramaSync] SetValue threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static FieldInfo? ResolveDpField(Type panoramaType, string fieldName)
        {
            if (_fieldByType.TryGetValue(panoramaType, out var cached)) return cached;

            // Walk the type chain; SelectedItemProperty may live on a base
            // (TemplatedItemsControl<T>) rather than Panorama itself.
            FieldInfo? found = null;
            for (Type? t = panoramaType; t != null; t = t.BaseType)
            {
                FieldInfo? f = t.GetField(fieldName,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (f != null) { found = f; break; }
            }
            _fieldByType[panoramaType] = found;
            return found;
        }
    }
}

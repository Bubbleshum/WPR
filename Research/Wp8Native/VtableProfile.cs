using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Records which vtable slots the image calls on a class this runtime only improvises,
    /// and what shape the arguments were.
    /// </summary>
    /// <remarks>
    /// A stand-in that answers S_OK to everything keeps an image running and tells you almost
    /// nothing about what it should have done. This turns the calls into the thing that is
    /// actually needed to implement the class: a slot number, a call count, and an argument
    /// shape.
    /// <para>
    /// The slot number is the useful part. A WinRT vtable is <c>IInspectable</c> at 0-5 and
    /// the interface's own members from 6 in **metadata declaration order**, so "slot 11" is
    /// the sixth member of the interface and can be read straight off the metadata for the
    /// class. That is how <c>Microsoft.Xbox.XboxLIVEService::slot11</c> becomes a name.
    /// </para>
    /// <para>
    /// The argument shapes are what say which calls are asynchronous, which is the question
    /// that matters here: this title's loading screen rests until Xbox callbacks arrive
    /// (<c>restUntilCallback</c>, <c>loadingScreenCallbacks</c> - recovered by
    /// <see cref="ScriptDumper"/>), so a slot taking a delegate is a slot that owes the image
    /// a completion it is never going to get.
    /// </para>
    /// </remarks>
    public sealed class VtableProfile
    {
        private readonly Dictionary<string, Dictionary<int, SlotRecord>> _classes = new(StringComparer.Ordinal);

        private sealed class SlotRecord
        {
            public int Calls { get; set; }

            public string Shape { get; set; } = string.Empty;

            public int Order { get; init; }

            public bool TakesDelegate { get; set; }
        }

        private int _next;

        /// <summary>Whether anything was recorded.</summary>
        public bool Any => _classes.Count > 0;

        /// <summary>Records one call. The shape is only kept from the first one.</summary>
        public void Record(string className, int slot, Func<string> describe, bool takesDelegate)
        {
            if (!_classes.TryGetValue(className, out Dictionary<int, SlotRecord>? slots))
            {
                slots = new Dictionary<int, SlotRecord>();
                _classes[className] = slots;
            }

            if (!slots.TryGetValue(slot, out SlotRecord? record))
            {
                record = new SlotRecord { Order = _next++, Shape = describe(), TakesDelegate = takesDelegate };
                slots[slot] = record;
            }

            record.Calls++;
            record.TakesDelegate |= takesDelegate;
        }

        /// <summary>
        /// The report, one block per class, slots in numeric order so they line up with
        /// metadata.
        /// </summary>
        public IEnumerable<string> Report(string? filter = null)
        {
            IEnumerable<KeyValuePair<string, Dictionary<int, SlotRecord>>> classes = _classes
                .Where(c => filter is null || c.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Value.Values.Min(v => v.Order));

            foreach ((string className, Dictionary<int, SlotRecord> slots) in classes)
            {
                yield return $"{className}  ({slots.Count} slot(s) called)";

                foreach ((int slot, SlotRecord record) in slots.OrderBy(e => e.Key))
                {
                    // Members start after IInspectable, so this is the index into the
                    // interface's own declaration order.
                    int member = slot - 6;
                    string flag = record.TakesDelegate ? " <- takes a delegate" : string.Empty;
                    yield return $"    slot {slot,2} (member {member,2})  x{record.Calls,-4} " +
                                 $"{record.Shape}{flag}";
                }
            }
        }

        /// <summary>
        /// Describes one argument by what it points at, which is as close to a type as this
        /// can get without metadata.
        /// </summary>
        public static string Describe(ArmEmulator emulator, HStringHeap strings, long value)
        {
            if (value == 0)
            {
                return "null";
            }

            if (ArmEmulator.IsStackAddress(value))
            {
                return "out*";
            }

            if (strings.IsKnown(value))
            {
                string text = strings.ReadText(value);
                return $"\"{(text.Length > 40 ? text[..40] + "..." : text)}\"";
            }

            if (LooksLikeDelegate(emulator, value))
            {
                return "delegate";
            }

            if (LooksLikeObject(emulator, value))
            {
                return "obj";
            }

            return value is > 0 and < 0x10000 ? $"{value}" : $"0x{value:X8}";
        }

        /// <summary>A heap object whose vtable has code at the delegate Invoke slot.</summary>
        public static bool LooksLikeDelegate(ArmEmulator emulator, long value)
        {
            if (value == 0 || ArmEmulator.IsStackAddress(value))
            {
                return false;
            }

            long vtable = emulator.ReadUInt32(value, 0);
            return vtable != 0 && emulator.IsExecutableCode(emulator.ReadUInt32(vtable + 12, 0));
        }

        private static bool LooksLikeObject(ArmEmulator emulator, long value)
        {
            long vtable = emulator.ReadUInt32(value, 0);
            return vtable != 0 && emulator.IsExecutableCode(emulator.ReadUInt32(vtable, 0));
        }

        /// <summary>Renders the first four registers plus a note on the whole call.</summary>
        public static string DescribeCall(
            ArmEmulator emulator, HStringHeap strings, long r1, long r2, long r3)
        {
            var parts = new StringBuilder("(");
            parts.Append(Describe(emulator, strings, r1));
            parts.Append(", ").Append(Describe(emulator, strings, r2));
            parts.Append(", ").Append(Describe(emulator, strings, r3));
            parts.Append(')');
            return parts.ToString();
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Windows.Foundation
{
    /// <summary>Shim for <c>Windows.Foundation.IWwwFormUrlDecoderEntry</c>.</summary>
    public interface IWwwFormUrlDecoderEntry
    {
        string Name { get; }

        string Value { get; }
    }

    /// <summary>Shim for <c>Windows.Foundation.WwwFormUrlDecoderEntry</c>.</summary>
    public sealed class WwwFormUrlDecoderEntry : IWwwFormUrlDecoderEntry
    {
        internal WwwFormUrlDecoderEntry(string name, string value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }

        public string Value { get; }
    }

    /// <summary>Shim for <c>Windows.Foundation.WwwFormUrlDecoder</c>: a query string's pairs, decoded.</summary>
    public sealed class WwwFormUrlDecoder : IReadOnlyList<IWwwFormUrlDecoderEntry>
    {
        private readonly List<IWwwFormUrlDecoderEntry> _entries;

        public WwwFormUrlDecoder(string query)
        {
            string q = (query ?? string.Empty).TrimStart('?');
            _entries = q.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair =>
                {
                    string[] kv = pair.Split('=', 2);
                    return (IWwwFormUrlDecoderEntry)new WwwFormUrlDecoderEntry(Decode(kv[0]), kv.Length > 1 ? Decode(kv[1]) : string.Empty);
                })
                .ToList();
        }

        private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

        /// <summary>As WinRT: throws when the name is absent.</summary>
        public string GetFirstValueByName(string name) =>
            _entries.FirstOrDefault(e => e.Name == name)?.Value
            ?? throw new ArgumentException("The parameter is incorrect.", nameof(name)) { HResult = unchecked((int)0x80070057) };

        public uint Size => (uint)_entries.Count;

        public int Count => _entries.Count;

        public IWwwFormUrlDecoderEntry this[int index] => _entries[index];

        public IEnumerator<IWwwFormUrlDecoderEntry> GetEnumerator() => _entries.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

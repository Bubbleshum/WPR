using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    public class JournalEntry
    {
        public Uri Source { get; }
        public string? Name { get; }

        public JournalEntry(Uri source, string? name = null)
        {
            Source = source;
            Name = name;
        }
    }
}

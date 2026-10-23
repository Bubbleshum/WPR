using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    public class JournalEntry
    {
        /// <summary>Where Back from the next page goes. Settable, as in Silverlight: an app may
        /// rewrite a back entry (YouTube's <c>UIHelper.UpdateBackEntryIfExits</c>), and the frame
        /// navigates to the entry's Source when it is popped.</summary>
        public Uri Source { get; set; }
        public string? Name { get; }

        public JournalEntry(Uri source, string? name = null)
        {
            Source = source;
            Name = name;
        }
    }
}

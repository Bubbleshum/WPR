using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    public class NavigationService
    {
        private readonly Frame _frame;

        internal NavigationService(Frame frame)
        {
            _frame = frame;
        }

        public Uri? CurrentSource => _frame.CurrentSource;
        public Uri? Source
        {
            get => _frame.Source;
            set { if (value != null) _frame.Navigate(value); }
        }

        public bool CanGoBack => _frame.CanGoBack;
        public bool CanGoForward => _frame.CanGoForward;

        public IEnumerable<JournalEntry> BackStack => _frame.BackStack;
        public IEnumerable<JournalEntry> ForwardStack => _frame.ForwardStack;

        public bool Navigate(Uri source) => _frame.Navigate(source);

        public void GoBack() => _frame.GoBack();
        public void GoForward() => _frame.GoForward();

        public void StopLoading() => _frame.StopLoading();

        /// <summary>
        /// Drops the most recent back-stack entry, so Back skips the page the app has just
        /// finished with.
        /// </summary>
        /// <remarks>
        /// The standard WP7 idiom for a splash or sign-in page: navigate away from it and then
        /// remove it, so Back from the next page leaves the app rather than returning to a
        /// screen that would immediately navigate forward again. Returns the entry removed, or
        /// null when the stack is empty — which is not an error, and is the normal case for the
        /// first page of a run.
        /// </remarks>
        public JournalEntry? RemoveBackEntry() => _frame.RemoveBackEntry();
    }
}

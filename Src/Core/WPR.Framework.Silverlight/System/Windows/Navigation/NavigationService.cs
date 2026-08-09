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
    }
}

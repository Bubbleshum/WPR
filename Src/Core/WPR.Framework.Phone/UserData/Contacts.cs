using System;
using System.Collections.Generic;
using System.Threading;

namespace Microsoft.Phone.UserData
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.UserData.Contacts</c>, the phone's address book. WPR has no
    /// address book, so every search finds nothing: the answer a phone with no contacts gave.
    /// </summary>
    public sealed class Contacts
    {
        public event EventHandler<ContactsSearchEventArgs>? SearchCompleted;

        public IEnumerable<Account> Accounts => Array.Empty<Account>();

        /// <summary>
        /// Searches the address book. The answer is raised later, on the caller's context, as on
        /// the phone, where it came back asynchronously on the UI thread.
        /// </summary>
        public void SearchAsync(string filter, FilterKind filterKind, object? state)
        {
            var args = new ContactsSearchEventArgs(filter, filterKind, state, Array.Empty<Contact>());
            SynchronizationContext? context = SynchronizationContext.Current;
            if (context != null)
            {
                context.Post(_ => SearchCompleted?.Invoke(this, args), null);
            }
            else
            {
                ThreadPool.QueueUserWorkItem(_ => SearchCompleted?.Invoke(this, args));
            }
        }
    }

    /// <summary>Shim for <c>Microsoft.Phone.UserData.ContactsSearchEventArgs</c>.</summary>
    public sealed class ContactsSearchEventArgs : EventArgs
    {
        internal ContactsSearchEventArgs(string filter, FilterKind filterKind, object? state, IEnumerable<Contact> results)
        {
            Filter = filter;
            FilterKind = filterKind;
            State = state;
            Results = results;
        }

        public string Filter { get; }

        public FilterKind FilterKind { get; }

        public object? State { get; }

        public IEnumerable<Contact> Results { get; }
    }

    /// <summary>Shim for <c>Microsoft.Phone.UserData.Contact</c>.</summary>
    public sealed class Contact
    {
        internal Contact()
        {
        }

        public string DisplayName { get; internal set; } = string.Empty;

        public IEnumerable<Account> Accounts { get; internal set; } = Array.Empty<Account>();
    }

    /// <summary>Shim for <c>Microsoft.Phone.UserData.Account</c>: where a contact came from.</summary>
    public sealed class Account
    {
        internal Account()
        {
        }

        public StorageKind Kind { get; internal set; }

        public string Name { get; internal set; } = string.Empty;
    }

    /// <summary>Shim for <c>Microsoft.Phone.UserData.FilterKind</c>.</summary>
    public enum FilterKind
    {
        None = 0,
        PinnedToStart = 1,
        EmailAddress = 2,
        PhoneNumber = 3,
        DisplayName = 4,
        Identifier = 5,
    }

    /// <summary>Shim for <c>Microsoft.Phone.UserData.StorageKind</c>.</summary>
    public enum StorageKind
    {
        Phone = 0,
        WindowsLive = 1,
        Outlook = 2,
        Facebook = 3,
        Other = 4,
    }
}

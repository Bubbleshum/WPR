using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Security.Authentication.Web;
using Windows.Storage;
using WPR.WinRT;

namespace Windows.Foundation.Collections
{
    /// <summary>Shim for <c>Windows.Foundation.Collections.ValueSet</c>.</summary>
    public sealed class ValueSet : Dictionary<string, object>
    {
    }
}

namespace Windows.ApplicationModel.Activation
{
    /// <summary>Shim for <c>Windows.ApplicationModel.Activation.IContinuationActivatedEventArgs</c>.</summary>
    public interface IContinuationActivatedEventArgs : IActivatedEventArgs
    {
        ValueSet ContinuationData { get; }
    }

    /// <summary>Shim for <c>Windows.ApplicationModel.Activation.IWebAuthenticationBrokerContinuationEventArgs</c>.</summary>
    public interface IWebAuthenticationBrokerContinuationEventArgs : IContinuationActivatedEventArgs
    {
        WebAuthenticationResult WebAuthenticationResult { get; }
    }

    /// <summary>Shim for <c>Windows.ApplicationModel.Activation.IFileSavePickerContinuationEventArgs</c>.</summary>
    public interface IFileSavePickerContinuationEventArgs : IContinuationActivatedEventArgs
    {
        StorageFile? File { get; }
    }

    /// <summary>Shim for <c>Windows.ApplicationModel.Activation.WebAuthenticationBrokerContinuationEventArgs</c>.</summary>
    public sealed class WebAuthenticationBrokerContinuationEventArgs : IWebAuthenticationBrokerContinuationEventArgs
    {
        internal WebAuthenticationBrokerContinuationEventArgs(WebAuthenticationResult result, ValueSet data)
        {
            WebAuthenticationResult = result;
            ContinuationData = data;
        }

        public WebAuthenticationResult WebAuthenticationResult { get; }

        public ValueSet ContinuationData { get; }

        public ActivationKind Kind => ActivationKind.WebAuthenticationBrokerContinuation;

        public ApplicationExecutionState PreviousExecutionState => ApplicationExecutionState.Running;
    }

    /// <summary>Shim for <c>Windows.ApplicationModel.Activation.FileSavePickerContinuationEventArgs</c>.</summary>
    public sealed class FileSavePickerContinuationEventArgs : IFileSavePickerContinuationEventArgs
    {
        internal FileSavePickerContinuationEventArgs(StorageFile? file, ValueSet data)
        {
            File = file;
            ContinuationData = data;
        }

        public StorageFile? File { get; }

        public ValueSet ContinuationData { get; }

        public ActivationKind Kind => ActivationKind.PickSaveFileContinuation;

        public ApplicationExecutionState PreviousExecutionState => ApplicationExecutionState.Running;
    }

    /// <summary>
    /// How a WP8.1 "AndContinue" call comes back: the app is re-activated with the result. WPR
    /// shows no system picker or browser, so each comes back at once as cancelled, the answer a
    /// title already handles. Posted to the caller's synchronization context (the page's thread),
    /// after the call has returned, as the phone did.
    /// </summary>
    internal static class Continuations
    {
        public static void Return(IActivatedEventArgs args)
        {
            SynchronizationContext? context = SynchronizationContext.Current;
            SendOrPostCallback raise = _ =>
            {
                Windows.ApplicationModel.Core.CoreApplication.GetCurrentView().RaiseActivated(args);
                WinRtHost.RaiseContractActivation(args);
            };
            if (context != null) context.Post(raise, null);
            else ThreadPool.QueueUserWorkItem(_ => raise(null));
        }
    }
}

namespace Windows.ApplicationModel.Core
{
    /// <summary>Shim for <c>Windows.ApplicationModel.Core.CoreApplicationView</c>: its Activated event carries continuations.</summary>
    public sealed class CoreApplicationView
    {
        private EventRegistrationTokenTable<TypedEventHandler<CoreApplicationView, IActivatedEventArgs>>? _activated;

        internal CoreApplicationView()
        {
        }

        public bool IsMain => true;

        public bool IsHosted => false;

        public EventRegistrationToken add_Activated(TypedEventHandler<CoreApplicationView, IActivatedEventArgs> handler) =>
            EventRegistrationTokenTable<TypedEventHandler<CoreApplicationView, IActivatedEventArgs>>.GetOrCreateEventRegistrationTokenTable(ref _activated).AddEventHandler(handler);

        public void remove_Activated(EventRegistrationToken token) => _activated?.RemoveEventHandler(token);

        internal void RaiseActivated(IActivatedEventArgs args) => _activated?.InvocationList?.Invoke(this, args);
    }

    /// <summary>Shim for <c>Windows.ApplicationModel.Core.CoreApplication</c>.</summary>
    public static class CoreApplication
    {
        private static CoreApplicationView _view = new();
        private static string _productId = string.Empty;

        /// <summary>One view per launch: a handler a previous title left behind must not hear the next one's continuations.</summary>
        public static CoreApplicationView GetCurrentView()
        {
            if (_productId != WinRtHost.ProductId)
            {
                _productId = WinRtHost.ProductId;
                _view = new CoreApplicationView();
            }

            return _view;
        }

        public static CoreApplicationView MainView => GetCurrentView();

        public static IDictionary<string, object> Properties { get; } = new Dictionary<string, object>();
    }
}

namespace Windows.Security.Authentication.Web
{
    /// <summary>Shim for <c>Windows.Security.Authentication.Web.WebAuthenticationStatus</c>.</summary>
    public enum WebAuthenticationStatus
    {
        Success = 0,
        UserCancel = 1,
        ErrorHttp = 2,
    }

    /// <summary>Shim for <c>Windows.Security.Authentication.Web.WebAuthenticationOptions</c>.</summary>
    [Flags]
    public enum WebAuthenticationOptions : uint
    {
        None = 0,
        SilentMode = 1,
        UseTitle = 2,
        UseHttpPost = 4,
        UseCorporateNetwork = 8,
    }

    /// <summary>Shim for <c>Windows.Security.Authentication.Web.WebAuthenticationResult</c>.</summary>
    public sealed class WebAuthenticationResult
    {
        internal WebAuthenticationResult(WebAuthenticationStatus status, string data, uint errorDetail)
        {
            ResponseStatus = status;
            ResponseData = data;
            ResponseErrorDetail = errorDetail;
        }

        public WebAuthenticationStatus ResponseStatus { get; }

        public string ResponseData { get; }

        public uint ResponseErrorDetail { get; }
    }

    /// <summary>
    /// Shim for <c>Windows.Security.Authentication.Web.WebAuthenticationBroker</c>. WPR has no
    /// in-app browser to sign in through, so a sign-in comes back cancelled by the user; a title
    /// shows its own "sign-in was cancelled" path instead of waiting for ever.
    /// </summary>
    public static class WebAuthenticationBroker
    {
        public static Uri GetCurrentApplicationCallbackUri() =>
            new("ms-app://s-1-15-2-" + (uint)WinRtHost.ProductId.GetHashCode() + "/");

        public static void AuthenticateAndContinue(Uri requestUri) =>
            AuthenticateAndContinue(requestUri, GetCurrentApplicationCallbackUri(), new ValueSet(), WebAuthenticationOptions.None);

        public static void AuthenticateAndContinue(Uri requestUri, Uri callbackUri) =>
            AuthenticateAndContinue(requestUri, callbackUri, new ValueSet(), WebAuthenticationOptions.None);

        public static void AuthenticateAndContinue(Uri requestUri, Uri callbackUri, ValueSet continuationData, WebAuthenticationOptions options)
        {
            Trace.WriteLine($"[wpr-winrt] WebAuthenticationBroker: no browser to sign in at {requestUri.GetLeftPart(UriPartial.Path)}; answering UserCancel.");
            Continuations.Return(new WebAuthenticationBrokerContinuationEventArgs(
                new WebAuthenticationResult(WebAuthenticationStatus.UserCancel, string.Empty, 0), continuationData ?? new ValueSet()));
        }
    }
}

namespace Windows.Storage.Pickers
{
    /// <summary>Shim for <c>Windows.Storage.Pickers.PickerLocationId</c>.</summary>
    public enum PickerLocationId
    {
        DocumentsLibrary = 0,
        ComputerFolder = 1,
        Desktop = 2,
        Downloads = 3,
        HomeGroup = 4,
        MusicLibrary = 5,
        PicturesLibrary = 6,
        VideosLibrary = 7,
        Objects3D = 8,
        Unspecified = 9,
    }

    /// <summary>
    /// Shim for <c>Windows.Storage.Pickers.FileSavePicker</c>. WPR shows no picker: it comes back as
    /// cancelled (no file), which every caller handles.
    /// </summary>
    public sealed class FileSavePicker
    {
        public IDictionary<string, IList<string>> FileTypeChoices { get; } = new Dictionary<string, IList<string>>();

        public ValueSet ContinuationData { get; } = new();

        public string SuggestedFileName { get; private set; } = string.Empty;

        public void put_SuggestedFileName(string value) => SuggestedFileName = value;

        public string DefaultFileExtension { get; private set; } = string.Empty;

        public void put_DefaultFileExtension(string value) => DefaultFileExtension = value;

        public PickerLocationId SuggestedStartLocation { get; private set; }

        public void put_SuggestedStartLocation(PickerLocationId value) => SuggestedStartLocation = value;

        public string CommitButtonText { get; private set; } = string.Empty;

        public void put_CommitButtonText(string value) => CommitButtonText = value;

        public void PickSaveFileAndContinue()
        {
            Trace.WriteLine($"[wpr-winrt] FileSavePicker: no picker on this platform; '{SuggestedFileName}' comes back cancelled.");
            Continuations.Return(new FileSavePickerContinuationEventArgs(null, ContinuationData));
        }
    }
}

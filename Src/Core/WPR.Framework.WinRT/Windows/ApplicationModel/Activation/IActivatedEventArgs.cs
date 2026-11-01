namespace Windows.ApplicationModel.Activation
{
    /// <summary>
    /// Shim for <c>Windows.ApplicationModel.Activation.IActivatedEventArgs</c>: what a WP8.1
    /// Silverlight app's <c>PhoneApplicationService.ContractActivated</c> handler is given. WPR
    /// starts no app through a contract (file pickers, sharing targets, voice commands), so none
    /// is ever raised; the type exists so an app that subscribes can be constructed (Live Store).
    /// </summary>
    public interface IActivatedEventArgs
    {
        ActivationKind Kind { get; }

        ApplicationExecutionState PreviousExecutionState { get; }
    }

    /// <summary>Shim for <c>Windows.ApplicationModel.Activation.ActivationKind</c>.</summary>
    public enum ActivationKind
    {
        Launch = 0,
        Search = 1,
        ShareTarget = 2,
        File = 3,
        Protocol = 4,
        FileOpenPicker = 5,
        FileSavePicker = 6,
        CachedFileUpdater = 7,
        ContactPicker = 8,
        Device = 9,
        PrintTaskSettings = 10,
        CameraSettings = 11,
        RestrictedLaunch = 12,
        AppointmentsProvider = 13,
        Contact = 14,
        LockScreenCall = 15,
        VoiceCommand = 16,
        LockScreen = 17,
        PickerReturned = 1000,
        WalletAction = 1001,
        PickFileContinuation = 1002,
        PickSaveFileContinuation = 1003,
        PickFolderContinuation = 1004,
        WebAuthenticationBrokerContinuation = 1005,
    }

    /// <summary>Shim for <c>Windows.ApplicationModel.Activation.ApplicationExecutionState</c>.</summary>
    public enum ApplicationExecutionState
    {
        NotRunning = 0,
        Running = 1,
        Suspended = 2,
        Terminated = 3,
        ClosedByUser = 4,
    }
}

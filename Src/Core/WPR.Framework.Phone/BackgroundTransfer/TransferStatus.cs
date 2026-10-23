namespace Microsoft.Phone.BackgroundTransfer
{
    /// <summary>Shim for <c>Microsoft.Phone.BackgroundTransfer.TransferStatus</c>.</summary>
    public enum TransferStatus
    {
        None = 0,
        Transferring = 1,
        Waiting = 2,
        WaitingForWiFi = 3,
        WaitingForExternalPower = 4,
        WaitingForExternalPowerDueToBatterySaverMode = 5,
        WaitingForNonVoiceBlockingNetwork = 6,
        Paused = 7,
        Completed = 8,
        Unknown = 9,
    }
}

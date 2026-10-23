namespace Microsoft.Phone.BackgroundTransfer
{
    /// <summary>Shim for <c>Microsoft.Phone.BackgroundTransfer.TransferPreferences</c>. WPR's
    /// device is always on a network and on power, so every preference transfers at once.</summary>
    public enum TransferPreferences
    {
        None = 0,
        AllowCellular = 1,
        AllowBattery = 2,
        AllowCellularAndBattery = 3,
    }
}

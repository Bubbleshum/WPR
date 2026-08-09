namespace Microsoft.Phone.Tasks
{
    /// <summary>
    /// Shim for the WP7 <c>PhotoChooserTask</c>. Real WP7 launches a system
    /// chooser to pick (or capture, if <see cref="ShowCamera"/>) a photo, then
    /// raises <c>Completed</c> on the captured SynchronizationContext with a
    /// <see cref="PhotoResult"/>. The host has no camera/photo picker, so
    /// <see cref="Show"/> immediately raises Completed with
    /// <see cref="TaskResult.Cancel"/> and a null photo — the same shape a real
    /// user-cancel would produce, so game code's Completed handlers shouldn't
    /// need to special-case our environment.
    /// </summary>
    public class PhotoChooserTask : ChooserBase<PhotoResult>
    {
        /// <summary>If true, the chooser would offer a "take photo" path; ignored here.</summary>
        public bool ShowCamera { get; set; }

        /// <summary>Desired cropped width — ignored.</summary>
        public int PixelWidth { get; set; }

        /// <summary>Desired cropped height — ignored.</summary>
        public int PixelHeight { get; set; }

        /// <summary>
        /// Launch the chooser. Real WP7 returns immediately and raises Completed
        /// later from the chooser UI; we have no chooser, so synthesise a
        /// user-cancel result so any continuation logic in user code unblocks.
        /// </summary>
        public void Show()
        {
            RaiseCompleted(new PhotoResult(TaskResult.Cancel));
        }
    }
}

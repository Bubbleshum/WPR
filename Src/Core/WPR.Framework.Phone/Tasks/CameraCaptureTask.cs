namespace Microsoft.Phone.Tasks
{
    /// <summary>
    /// Shim for the WP7 <c>CameraCaptureTask</c>. Real WP7 opens the camera viewfinder and raises
    /// <c>Completed</c> with a <see cref="PhotoResult"/> holding the captured image. WPR has no
    /// camera, so <see cref="Show"/> synthesises the user-cancelled result — the same shape a real
    /// back-out produces, so a game's Completed handler needs no special case.
    ///
    /// Kinectimals constructs one in <c>MediaUtils.ImageSelector..ctor</c> and holds it in a field,
    /// so the type has to resolve even for players who never touch the camera feature.
    /// </summary>
    public class CameraCaptureTask : ChooserBase<PhotoResult>
    {
        /// <summary>
        /// Launch the capture UI. There isn't one, so raise a cancel immediately rather than
        /// leaving the caller waiting on a Completed that will never arrive.
        /// </summary>
        public void Show()
        {
            RaiseCompleted(new PhotoResult(TaskResult.Cancel));
        }
    }
}

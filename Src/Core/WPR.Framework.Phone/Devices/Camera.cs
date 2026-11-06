// Events a platform without the hardware never raises.
#pragma warning disable CS0067

using System;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.Devices
{
    /// <summary>Shim for <c>Microsoft.Devices.CameraType</c>.</summary>
    public enum CameraType
    {
        Primary = 0,
        FrontFacing = 1,
    }

    /// <summary>Shim for <c>Microsoft.Devices.FlashMode</c>.</summary>
    public enum FlashMode
    {
        On = 1,
        Off = 2,
        Auto = 3,
        RedEyeReduction = 4,
    }

    /// <summary>
    /// Shim for <c>Microsoft.Devices.Camera</c>, the base of <see cref="PhotoCamera"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>WPR has no camera, on either head</b>: <see cref="IsCameraTypeSupported"/> answers
    /// false for every type, which is what a WP7/WP8 phone without that camera answered, and what
    /// titles were written to check first. Marmalade's s3eCamera (Shadow Fight 2) does exactly
    /// that and reports the camera unavailable.</para>
    /// <para>These types had to exist even so: a class with a field of a missing type cannot be
    /// loaded, and Marmalade builds its camera wrapper in its page constructor, so the whole page
    /// failed to construct and the game showed nothing.</para>
    /// </remarks>
    public abstract class Camera : IDisposable
    {
        private protected Camera(CameraType type)
        {
            CameraType = type;
        }

        /// <summary>No camera of any type exists here.</summary>
        public static bool IsCameraTypeSupported(CameraType type) => false;

        public CameraType CameraType { get; }

        public IEnumerable<Size> AvailableResolutions => Array.Empty<Size>();

        public Size Resolution { get; set; }

        public Size PreviewResolution => default;

        /// <summary>Degrees the sensor is turned from the phone's natural portrait orientation.</summary>
        public double Orientation => 0;

        /// <summary>Raised once the camera is ready; never, here, since no camera can be constructed.</summary>
        public event EventHandler<CameraOperationCompletedEventArgs>? Initialized;

        public void Dispose()
        {
            Initialized = null;
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Shim for <c>Microsoft.Devices.PhotoCamera</c>.</summary>
    /// <remarks>
    /// The constructors throw <see cref="InvalidOperationException"/>: there is no camera of the
    /// type asked for (see <see cref="Camera"/>). A title that checks
    /// <see cref="Camera.IsCameraTypeSupported"/> first, as the platform documentation told it to,
    /// never gets here.
    /// </remarks>
    public sealed class PhotoCamera : Camera
    {
        public PhotoCamera()
            : this(CameraType.Primary)
        {
        }

        public PhotoCamera(CameraType type)
            : base(type)
        {
            throw new InvalidOperationException($"This device has no {type} camera.");
        }

        public FlashMode FlashMode { get; set; } = FlashMode.Off;

        public bool IsFlashModeSupported(FlashMode mode) => false;

        public bool IsFocusSupported => false;

        public bool IsFocusAtPointSupported => false;

        public void Focus()
        {
        }

        public void FocusAtPoint(double x, double y)
        {
        }

        public void CancelFocus()
        {
        }

        public void CaptureImage() => throw new InvalidOperationException("This device has no camera.");

        public void GetPreviewBufferArgb32(int[] pixelData)
        {
        }

        public void GetPreviewBufferY(byte[] pixelData)
        {
        }

        public void GetPreviewBufferYCbCr(byte[] pixelData)
        {
        }

        public event EventHandler<CameraOperationCompletedEventArgs>? AutoFocusCompleted;

        public event EventHandler? CaptureStarted;

        public event EventHandler<CameraOperationCompletedEventArgs>? CaptureCompleted;

        public event EventHandler<ContentReadyEventArgs>? CaptureImageAvailable;

        public event EventHandler<ContentReadyEventArgs>? CaptureThumbnailAvailable;
    }

    /// <summary>Shim for <c>Microsoft.Devices.CameraOperationCompletedEventArgs</c>.</summary>
    public sealed class CameraOperationCompletedEventArgs : EventArgs
    {
        public CameraOperationCompletedEventArgs(bool succeeded, Exception? exception)
        {
            Succeeded = succeeded;
            Exception = exception;
        }

        public bool Succeeded { get; }

        public Exception? Exception { get; }
    }

    /// <summary>Shim for <c>Microsoft.Devices.ContentReadyEventArgs</c>.</summary>
    public sealed class ContentReadyEventArgs : EventArgs
    {
        public ContentReadyEventArgs(Stream imageStream) => ImageStream = imageStream;

        public Stream ImageStream { get; }
    }

    /// <summary>
    /// Shim for <c>Microsoft.Devices.CameraButtons</c>: the phone's hardware shutter key. Never
    /// raised, because no WPR platform has one (a phone's volume or camera key is not routed to
    /// games). Marmalade subscribes from its keyboard wrapper's constructor.
    /// </summary>
    public static class CameraButtons
    {
        public static event EventHandler? ShutterKeyHalfPressed;

        public static event EventHandler? ShutterKeyPressed;

        public static event EventHandler? ShutterKeyReleased;
    }

    /// <summary>Shim for <c>Microsoft.Devices.CameraVideoBrushExtensions</c>.</summary>
    public static class CameraVideoBrushExtensions
    {
        /// <summary>Shows the camera's preview in the brush. No camera can exist here, so nothing is set.</summary>
        public static void SetSource(this VideoBrush brush, Camera camera)
        {
            ArgumentNullException.ThrowIfNull(brush);
        }
    }
}

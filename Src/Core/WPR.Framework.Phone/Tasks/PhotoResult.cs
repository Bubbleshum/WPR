using System.IO;

namespace Microsoft.Phone.Tasks
{
    /// <summary>
    /// Shim for the WP7 <c>PhotoResult</c>. Real WP7 hands back a <see cref="Stream"/>
    /// + original file name from the camera/gallery roll. Our host has no camera or
    /// photo picker, so <see cref="PhotoChooserTask.Show"/> never raises Completed,
    /// and this type's properties are never populated in practice. The class exists
    /// solely so games whose .ctors reference <c>PhotoResult</c> (event handler
    /// signatures, field types) can be loaded.
    /// </summary>
    public class PhotoResult : TaskEventArgs
    {
        public PhotoResult() : base(TaskResult.None) { }
        public PhotoResult(TaskResult result) : base(result) { }

        public Stream? ChosenPhoto { get; set; }
        public string? OriginalFileName { get; set; }
    }
}

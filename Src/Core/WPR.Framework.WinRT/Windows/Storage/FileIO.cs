using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding;
using Windows.Foundation;
using Windows.Storage.Streams;
using WPR.WinRT;

namespace Windows.Storage
{
    /// <summary>Shim for <c>Windows.Storage.FileIO</c>: whole-file reads and writes, answered at once.</summary>
    public static class FileIO
    {
        public static IAsyncOperation<string> ReadTextAsync(IStorageFile file) => WinRtStorage.Answer(() => File.ReadAllText(file.Path));

        public static IAsyncOperation<string> ReadTextAsync(IStorageFile file, UnicodeEncoding encoding) =>
            WinRtStorage.Answer(() => File.ReadAllText(file.Path, For(encoding)));

        public static IAsyncAction WriteTextAsync(IStorageFile file, string contents) => WinRtStorage.Act(() => File.WriteAllText(file.Path, contents));

        public static IAsyncAction WriteTextAsync(IStorageFile file, string contents, UnicodeEncoding encoding) =>
            WinRtStorage.Act(() => File.WriteAllText(file.Path, contents, For(encoding)));

        public static IAsyncAction AppendTextAsync(IStorageFile file, string contents) => WinRtStorage.Act(() => File.AppendAllText(file.Path, contents));

        public static IAsyncOperation<IList<string>> ReadLinesAsync(IStorageFile file) =>
            WinRtStorage.Answer<IList<string>>(() => File.ReadAllLines(file.Path).ToList());

        public static IAsyncAction WriteLinesAsync(IStorageFile file, IEnumerable<string> lines) => WinRtStorage.Act(() => File.WriteAllLines(file.Path, lines));

        public static IAsyncOperation<IBuffer> ReadBufferAsync(IStorageFile file) => WinRtStorage.Answer(() => File.ReadAllBytes(file.Path).AsBuffer());

        public static IAsyncAction WriteBufferAsync(IStorageFile file, IBuffer buffer) => WinRtStorage.Act(() => File.WriteAllBytes(file.Path, buffer.ToArray()));

        public static IAsyncAction WriteBytesAsync(IStorageFile file, byte[] buffer) => WinRtStorage.Act(() => File.WriteAllBytes(file.Path, buffer));

        private static Encoding For(UnicodeEncoding encoding) => encoding switch
        {
            UnicodeEncoding.Utf16LE => Encoding.Unicode,
            UnicodeEncoding.Utf16BE => Encoding.BigEndianUnicode,
            _ => new UTF8Encoding(false),
        };
    }

    /// <summary>
    /// Shim for <c>Windows.Storage.KnownFolders</c>. A phone's libraries are per title here,
    /// beside its local folder: a title sees what it saved itself and nothing else.
    /// </summary>
    public static class KnownFolders
    {
        private static StorageFolder Library(string name) =>
            StorageFolder.Ensure(Path.Combine(WinRtHost.LocalFolderPath, "..", "Libraries", name));

        public static StorageFolder DocumentsLibrary => Library("Documents");

        public static StorageFolder MusicLibrary => Library("Music");

        public static StorageFolder PicturesLibrary => Library("Pictures");

        public static StorageFolder VideosLibrary => Library("Videos");

        public static StorageFolder SavedPictures => Library("Pictures");

        public static StorageFolder CameraRoll => Library("Pictures");
    }
}

namespace Windows.Phone.Management.Deployment
{
    using Windows.ApplicationModel;

    /// <summary>
    /// Shim for <c>Windows.Phone.Management.Deployment.InstallationManager</c>. WP8 answered the
    /// calling publisher's installed apps; WPR answers the running title alone.
    /// </summary>
    public static class InstallationManager
    {
        public static IEnumerable<Package> FindPackages() => new[] { Package.Current };

        public static IEnumerable<Package> FindPackagesForCurrentPublisher() => new[] { Package.Current };
    }
}

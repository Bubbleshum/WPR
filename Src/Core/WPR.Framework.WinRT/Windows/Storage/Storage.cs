using System;
using global::System.Collections.Generic;
using global::System.IO;
using System.Linq;
using Windows.Foundation;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;
using Windows.Storage.Streams;
using WPR.WinRT;

namespace Windows.Storage
{
    /// <summary>Shim for <c>Windows.Storage.CreationCollisionOption</c>.</summary>
    public enum CreationCollisionOption
    {
        GenerateUniqueName = 0,
        ReplaceExisting = 1,
        FailIfExists = 2,
        OpenIfExists = 3,
    }

    /// <summary>Shim for <c>Windows.Storage.NameCollisionOption</c>.</summary>
    public enum NameCollisionOption
    {
        GenerateUniqueName = 0,
        ReplaceExisting = 1,
        FailIfExists = 2,
    }

    /// <summary>Shim for <c>Windows.Storage.FileAccessMode</c>.</summary>
    public enum FileAccessMode
    {
        Read = 0,
        ReadWrite = 1,
    }

    /// <summary>Shim for <c>Windows.Storage.IStorageItem</c>.</summary>
    public interface IStorageItem
    {
        string Name { get; }

        string Path { get; }

        DateTimeOffset DateCreated { get; }

        IAsyncAction DeleteAsync();

        IAsyncOperation<BasicProperties> GetBasicPropertiesAsync();
    }

    /// <summary>Shim for <c>Windows.Storage.IStorageFile</c>.</summary>
    public interface IStorageFile : IStorageItem
    {
        string FileType { get; }

        string ContentType { get; }

        IAsyncOperation<IRandomAccessStream> OpenAsync(FileAccessMode accessMode);
    }

    /// <summary>Shim for <c>Windows.Storage.IStorageFolder</c>.</summary>
    public interface IStorageFolder : IStorageItem
    {
        IAsyncOperation<StorageFile> CreateFileAsync(string desiredName);

        IAsyncOperation<StorageFile> CreateFileAsync(string desiredName, CreationCollisionOption options);

        IAsyncOperation<StorageFolder> CreateFolderAsync(string desiredName);

        IAsyncOperation<StorageFolder> CreateFolderAsync(string desiredName, CreationCollisionOption options);

        IAsyncOperation<StorageFile> GetFileAsync(string name);

        IAsyncOperation<StorageFolder> GetFolderAsync(string name);

        IAsyncOperation<IReadOnlyList<StorageFile>> GetFilesAsync();

        IAsyncOperation<IReadOnlyList<StorageFolder>> GetFoldersAsync();
    }

    /// <summary>
    /// Shim for <c>Windows.Storage.ApplicationData</c>. <c>LocalFolder</c> is the title's own
    /// isolated store, as on WP8, where the two were one folder: a title that writes through one
    /// and reads through the other finds its file (<see cref="WinRtHost.LocalFolderPath"/>).
    /// </summary>
    public sealed class ApplicationData
    {
        private static readonly ApplicationData _current = new();

        private ApplicationData()
        {
        }

        public static ApplicationData Current => _current;

        public StorageFolder LocalFolder => StorageFolder.Ensure(WinRtHost.LocalFolderPath);

        public StorageFolder RoamingFolder => StorageFolder.Ensure(global::System.IO.Path.Combine(WinRtHost.LocalFolderPath, "..", "Roaming"));

        public StorageFolder TemporaryFolder => StorageFolder.Ensure(global::System.IO.Path.Combine(WinRtHost.LocalFolderPath, "..", "Temp"));
    }

    /// <summary>
    /// Shim for <c>Windows.Storage.StorageFolder</c>, over a host folder. Every call answers at
    /// once (WPR's disk is local); a failure is the operation's error, as WinRT reported it, with
    /// the exception types the projection used (FileNotFoundException for a missing item,
    /// UnauthorizedAccessException ... ).
    /// </summary>
    public sealed class StorageFolder : IStorageFolder
    {
        public StorageFolder(string path) => Path = path ?? string.Empty;

        public string Path { get; }

        public string Name => global::System.IO.Path.GetFileName(Path.TrimEnd(global::System.IO.Path.DirectorySeparatorChar, global::System.IO.Path.AltDirectorySeparatorChar));

        public string DisplayName => Name;

        public DateTimeOffset DateCreated => Directory.Exists(Path) ? Directory.GetCreationTimeUtc(Path) : default;

        internal static StorageFolder Ensure(string path)
        {
            try { Directory.CreateDirectory(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return new StorageFolder(global::System.IO.Path.GetFullPath(path));
        }

        public static IAsyncOperation<StorageFolder> GetFolderFromPathAsync(string path) => WinRtStorage.Answer(() =>
        {
            string full = WinRtStorage.Resolve(path);
            if (!Directory.Exists(full))
            {
                throw new FileNotFoundException("The system cannot find the folder specified.", path);
            }

            return new StorageFolder(full);
        });

        public IAsyncOperation<StorageFile> CreateFileAsync(string desiredName) => CreateFileAsync(desiredName, CreationCollisionOption.FailIfExists);

        public IAsyncOperation<StorageFile> CreateFileAsync(string desiredName, CreationCollisionOption options) => WinRtStorage.Answer(() =>
        {
            string target = Child(desiredName);
            Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                switch (options)
                {
                    case CreationCollisionOption.OpenIfExists:
                        return new StorageFile(target);
                    case CreationCollisionOption.FailIfExists:
                        throw new IOException("Cannot create a file when that file already exists.", unchecked((int)0x800700B7));
                    case CreationCollisionOption.GenerateUniqueName:
                        target = WinRtStorage.UniqueName(target);
                        break;
                }
            }

            using (new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
            }

            return new StorageFile(target);
        });

        public IAsyncOperation<StorageFolder> CreateFolderAsync(string desiredName) => CreateFolderAsync(desiredName, CreationCollisionOption.FailIfExists);

        public IAsyncOperation<StorageFolder> CreateFolderAsync(string desiredName, CreationCollisionOption options) => WinRtStorage.Answer(() =>
        {
            string target = Child(desiredName);
            if (Directory.Exists(target))
            {
                switch (options)
                {
                    case CreationCollisionOption.OpenIfExists:
                        return new StorageFolder(target);
                    case CreationCollisionOption.FailIfExists:
                        throw new IOException("Cannot create a file when that file already exists.", unchecked((int)0x800700B7));
                    case CreationCollisionOption.ReplaceExisting:
                        Directory.Delete(target, recursive: true);
                        break;
                    case CreationCollisionOption.GenerateUniqueName:
                        target = WinRtStorage.UniqueName(target);
                        break;
                }
            }

            Directory.CreateDirectory(target);
            return new StorageFolder(target);
        });

        public IAsyncOperation<StorageFile> GetFileAsync(string name) => WinRtStorage.Answer(() =>
        {
            string target = Child(name);
            if (!File.Exists(target))
            {
                throw new FileNotFoundException("The system cannot find the file specified.", name);
            }

            return new StorageFile(target);
        });

        public IAsyncOperation<StorageFolder> GetFolderAsync(string name) => WinRtStorage.Answer(() =>
        {
            string target = Child(name);
            if (!Directory.Exists(target))
            {
                throw new FileNotFoundException("The system cannot find the folder specified.", name);
            }

            return new StorageFolder(target);
        });

        public IAsyncOperation<IStorageItem> GetItemAsync(string name) => WinRtStorage.Answer<IStorageItem>(() =>
        {
            string target = Child(name);
            if (File.Exists(target))
            {
                return new StorageFile(target);
            }

            if (Directory.Exists(target))
            {
                return new StorageFolder(target);
            }

            throw new FileNotFoundException("The system cannot find the file specified.", name);
        });

        public IAsyncOperation<IReadOnlyList<StorageFile>> GetFilesAsync() => WinRtStorage.Answer<IReadOnlyList<StorageFile>>(() =>
            Directory.Exists(Path)
                ? Directory.GetFiles(Path).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => new StorageFile(f)).ToList()
                : new List<StorageFile>());

        /// <summary>Every query answers the folder's own files, by name (WPR keeps no index).</summary>
        public IAsyncOperation<IReadOnlyList<StorageFile>> GetFilesAsync(CommonFileQuery query) => GetFilesAsync();

        public IAsyncOperation<IReadOnlyList<StorageFolder>> GetFoldersAsync() => WinRtStorage.Answer<IReadOnlyList<StorageFolder>>(() =>
            Directory.Exists(Path)
                ? Directory.GetDirectories(Path).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => new StorageFolder(f)).ToList()
                : new List<StorageFolder>());

        public IAsyncAction DeleteAsync() => WinRtStorage.Act(() =>
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        });

        public IAsyncOperation<BasicProperties> GetBasicPropertiesAsync() => WinRtStorage.Answer(() =>
            new BasicProperties(0, Directory.Exists(Path) ? Directory.GetLastWriteTimeUtc(Path) : default));

        private string Child(string name) => WinRtStorage.Resolve(global::System.IO.Path.Combine(Path, WinRtStorage.Native(name)));
    }

    /// <summary>Shim for <c>Windows.Storage.StorageFile</c>, over a host file.</summary>
    public sealed class StorageFile : IStorageFile
    {
        public StorageFile(string path) => Path = path ?? string.Empty;

        public string Path { get; }

        public string Name => global::System.IO.Path.GetFileName(Path);

        public string DisplayName => global::System.IO.Path.GetFileNameWithoutExtension(Path);

        public string FileType => global::System.IO.Path.GetExtension(Path);

        public string ContentType => WinRtStorage.ContentType(FileType);

        public DateTimeOffset DateCreated => File.Exists(Path) ? File.GetCreationTimeUtc(Path) : default;

        public static IAsyncOperation<StorageFile> GetFileFromPathAsync(string path) => WinRtStorage.Answer(() =>
        {
            string full = WinRtStorage.Resolve(path);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException("The system cannot find the file specified.", path);
            }

            return new StorageFile(full);
        });

        /// <summary>
        /// <c>ms-appx:///x</c> is the package's file x; <c>ms-appdata:///local/x</c> the local
        /// folder's. Anything else is not found.
        /// </summary>
        public static IAsyncOperation<StorageFile> GetFileFromApplicationUriAsync(Uri uri) => WinRtStorage.Answer(() =>
        {
            string? full = WinRtStorage.FromApplicationUri(uri);
            if (full == null || !File.Exists(full))
            {
                throw new FileNotFoundException("The system cannot find the file specified.", uri?.ToString());
            }

            return new StorageFile(full);
        });

        public IAsyncOperation<IRandomAccessStream> OpenAsync(FileAccessMode accessMode) => WinRtStorage.Answer<IRandomAccessStream>(() =>
            new RandomAccessStream(new FileStream(
                Path,
                accessMode == FileAccessMode.ReadWrite ? FileMode.OpenOrCreate : FileMode.Open,
                accessMode == FileAccessMode.ReadWrite ? FileAccess.ReadWrite : FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete),
                ContentType));

        public IAsyncOperation<IRandomAccessStreamWithContentType> OpenReadAsync() => WinRtStorage.Answer<IRandomAccessStreamWithContentType>(() =>
            new RandomAccessStream(new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), ContentType));

        public IAsyncOperation<IInputStream> OpenSequentialReadAsync() => WinRtStorage.Answer<IInputStream>(() =>
            new RandomAccessStream(new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), ContentType));

        public IAsyncAction DeleteAsync() => WinRtStorage.Act(() =>
        {
            if (!File.Exists(Path))
            {
                throw new FileNotFoundException("The system cannot find the file specified.", Path);
            }

            File.Delete(Path);
        });

        public IAsyncOperation<BasicProperties> GetBasicPropertiesAsync() => WinRtStorage.Answer(() =>
        {
            var info = new FileInfo(Path);
            return new BasicProperties(info.Exists ? (ulong)info.Length : 0, info.Exists ? info.LastWriteTimeUtc : default);
        });

        public IAsyncOperation<StorageFile> CopyAsync(IStorageFolder destinationFolder, string desiredNewName, NameCollisionOption option) => WinRtStorage.Answer(() =>
        {
            string target = global::System.IO.Path.Combine(destinationFolder.Path, WinRtStorage.Native(desiredNewName));
            if (File.Exists(target) && option == NameCollisionOption.GenerateUniqueName)
            {
                target = WinRtStorage.UniqueName(target);
            }

            File.Copy(Path, target, overwrite: option == NameCollisionOption.ReplaceExisting);
            return new StorageFile(target);
        });

        public IAsyncOperation<StorageFile> CopyAsync(IStorageFolder destinationFolder) => CopyAsync(destinationFolder, Name, NameCollisionOption.FailIfExists);

        public IAsyncAction RenameAsync(string desiredName) => RenameAsync(desiredName, NameCollisionOption.FailIfExists);

        public IAsyncAction RenameAsync(string desiredName, NameCollisionOption option) => WinRtStorage.Act(() =>
        {
            string target = global::System.IO.Path.Combine(global::System.IO.Path.GetDirectoryName(Path)!, WinRtStorage.Native(desiredName));
            File.Move(Path, target, overwrite: option == NameCollisionOption.ReplaceExisting);
        });
    }

    /// <summary>Helpers behind the storage classes.</summary>
    internal static class WinRtStorage
    {
        /// <summary>Runs <paramref name="work"/> now; a throw becomes the operation's error.</summary>
        public static IAsyncOperation<T> Answer<T>(Func<T> work)
        {
            try { return WinRtAsync.FromResult(work()); }
            catch (Exception ex) { return WinRtAsync.Failed<T>(ex); }
        }

        public static IAsyncAction Act(Action work)
        {
            try
            {
                work();
                return WinRtAsync.Completed();
            }
            catch (Exception ex)
            {
                return WinRtAsync.Failed(ex);
            }
        }

        /// <summary>A WP8 path with this platform's separators.</summary>
        public static string Native(string path) =>
            global::System.IO.Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', global::System.IO.Path.DirectorySeparatorChar);

        public static string Resolve(string path) => global::System.IO.Path.GetFullPath(Native(path));

        public static string UniqueName(string target)
        {
            string dir = global::System.IO.Path.GetDirectoryName(target)!;
            string stem = global::System.IO.Path.GetFileNameWithoutExtension(target);
            string ext = global::System.IO.Path.GetExtension(target);
            for (int i = 2; ; i++)
            {
                string candidate = global::System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        public static string? FromApplicationUri(Uri? uri)
        {
            if (uri == null || !uri.IsAbsoluteUri)
            {
                return null;
            }

            string relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            if (uri.Scheme.Equals("ms-appx", StringComparison.OrdinalIgnoreCase))
            {
                return global::System.IO.Path.Combine(WinRtHost.InstalledPath, Native(relative));
            }

            if (uri.Scheme.Equals("ms-appdata", StringComparison.OrdinalIgnoreCase)
                && relative.StartsWith("local/", StringComparison.OrdinalIgnoreCase))
            {
                return global::System.IO.Path.Combine(WinRtHost.LocalFolderPath, Native(relative.Substring("local/".Length)));
            }

            return null;
        }

        public static string ContentType(string extension) => extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".xml" => "text/xml",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            _ => "application/octet-stream",
        };
    }
}

namespace Windows.Storage.FileProperties
{
    /// <summary>Shim for <c>Windows.Storage.FileProperties.BasicProperties</c>.</summary>
    public sealed class BasicProperties
    {
        internal BasicProperties(ulong size, DateTimeOffset modified)
        {
            Size = size;
            DateModified = modified;
            ItemDate = modified;
        }

        public ulong Size { get; }

        public DateTimeOffset DateModified { get; }

        public DateTimeOffset ItemDate { get; }
    }
}

namespace Windows.Storage.Search
{
    /// <summary>Shim for <c>Windows.Storage.Search.CommonFileQuery</c>.</summary>
    public enum CommonFileQuery
    {
        DefaultQuery = 0,
        OrderByName = 1,
        OrderByTitle = 2,
        OrderByMusicProperties = 3,
        OrderBySearchRank = 4,
        OrderByDate = 5,
    }
}

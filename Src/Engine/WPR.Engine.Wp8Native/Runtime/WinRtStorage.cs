using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Windows.Storage as WP8 had it: <c>Package</c>, <c>StorageFolder</c>/<c>StorageFile</c> over the
    /// package (read-only) and the sandbox (the local, roaming and temporary folders),
    /// <c>ApplicationData</c> with persisted <c>LocalSettings</c>, <c>FileIO</c>, <c>PathIO</c> and
    /// <c>KnownFolders</c>.
    /// </summary>
    /// <remarks>
    /// Every path goes through <see cref="FileLibrary.ResolveGuestPath"/>, so a file a game reaches
    /// through WinRT and through <c>CreateFile2</c>/<c>fopen</c> is the same host file. Operations are
    /// performed when the method is called and handed back as finished async operations; a failure
    /// is an operation in the Error state carrying the documented HRESULT
    /// (<c>HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)</c> for a missing file, <c>E_ACCESSDENIED</c> for a
    /// write into the package), which C++/CX's task turns into the exception a game catches.
    /// </remarks>
    public sealed partial class WinRtRuntime
    {
        private sealed partial class SystemClasses
        {
            private FileLibrary Files => _e.Stubs.Files;

            // -----------------------------------------------------------------------------------
            // The package and its manifest.
            // -----------------------------------------------------------------------------------

            public sealed record PackageInfo(
                string ProductId, string Title, Version Version, string Publisher, string PublisherId,
                string Author, string DefaultLanguage, IReadOnlyList<string> Languages);

            private PackageInfo? _package;

            /// <summary>WMAppManifest.xml from the package root: the identity a WP8 package carried.</summary>
            public PackageInfo Package => _package ??= ReadManifest();

            private PackageInfo ReadManifest()
            {
                string path = Path.Combine(Files.PackageRoot, "WMAppManifest.xml");
                try
                {
                    XDocument doc = XDocument.Load(path);
                    XElement? app = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "App");
                    string Attr(string name) => app?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? string.Empty;
                    string defaultLanguage = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "DefaultLanguage")?.Attribute("code")?.Value ?? "en-US";
                    List<string> languages = doc.Descendants().Where(e => e.Name.LocalName == "Language")
                        .Select(e => e.Attribute("code")?.Value).OfType<string>().ToList();
                    if (!languages.Contains(defaultLanguage, StringComparer.OrdinalIgnoreCase))
                    {
                        languages.Insert(0, defaultLanguage);
                    }

                    return new PackageInfo(
                        Attr("ProductID").Trim('{', '}').ToLowerInvariant(),
                        Attr("Title"),
                        Version.TryParse(Attr("Version"), out Version? v) ? v : new Version(1, 0, 0, 0),
                        Attr("Publisher"),
                        Attr("PublisherID").Trim('{', '}').ToLowerInvariant(),
                        Attr("Author"),
                        defaultLanguage,
                        languages);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    Note($"WMAppManifest.xml unreadable ({ex.GetType().Name}); the package has a placeholder identity");
                    return new PackageInfo(Guid.Empty.ToString("D"), string.Empty, new Version(1, 0, 0, 0), string.Empty, string.Empty, string.Empty, "en-US", ["en-US"]);
                }
            }

            /// <summary>Where WP8 installed a package: <c>C:\Data\Programs\{PRODUCTID}\Install</c>.</summary>
            public string InstallPath => $@"C:\Data\Programs\{{{Package.ProductId.ToUpperInvariant()}}}\Install";

            private void RegisterPackage()
            {
                ComClass packageId = Class("Windows.ApplicationModel.PackageId", [
                    I(
                        Meta("Windows.ApplicationModel.IPackageId"),
                        ("get_Name", c => c.RetStr(Package.ProductId)),
                        ("get_Version", c =>
                        {
                            // PackageVersion: four UInt16, Major first.
                            Version v = Package.Version;
                            c.RetBytes([.. new[] { v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0) }.SelectMany(n => BitConverter.GetBytes((ushort)n))]);
                        }),
                        ("get_Architecture", c => c.RetU32(5)),   // ProcessorArchitecture.Arm
                        ("get_ResourceId", c => c.RetStr(string.Empty)),
                        ("get_Publisher", c => c.RetStr(Package.Publisher)),
                        ("get_PublisherId", c => c.RetStr(Package.PublisherId)),
                        ("get_FullName", c => c.RetStr($"{Package.ProductId}_{Package.Version}_arm__{Package.PublisherId}")),
                        ("get_FamilyName", c => c.RetStr($"{Package.ProductId}_{Package.PublisherId}"))),

                    // WP8's own members on PackageId.
                    I(
                        Meta("Windows.ApplicationModel.IPackageIdWithMetadata"),
                        ("get_ProductId", c => c.RetStr("{" + Package.ProductId.ToUpperInvariant() + "}")),
                        ("get_Author", c => c.RetStr(Package.Author))),
                ]);

                ComClass package = Class("Windows.ApplicationModel.Package", [
                    I(
                        Meta("Windows.ApplicationModel.IPackage"),
                        ("get_Id", c => c.RetObj(New(packageId, null).Primary)),
                        ("get_InstalledLocation", c => c.RetObj(InstalledLocation())),
                        ("get_IsFramework", c => c.RetBool(false)),
                        ("get_Dependencies", c => c.RetObj(ObjectView(Sig.Rc("Windows.ApplicationModel.Package", "Windows.ApplicationModel.IPackage"), [])))),
                ]);

                long current = 0;
                Factory(
                    "Windows.ApplicationModel.Package",
                    I(Meta("Windows.ApplicationModel.IPackageStatics"), ("get_Current", c => c.RetObj(current != 0 ? current : current = New(package, null).Primary))));
            }

            // -----------------------------------------------------------------------------------
            // StorageFolder and StorageFile.
            // -----------------------------------------------------------------------------------

            public sealed class ItemState
            {
                public required string GuestPath { get; set; }

                public required string HostPath { get; set; }

                public required bool IsFolder { get; init; }

                /// <summary>Inside the package, which WP8 mounted read-only.</summary>
                public required bool ReadOnly { get; init; }

                public string Name => GuestPath[(GuestPath.TrimEnd('\\').LastIndexOf('\\') + 1)..].TrimEnd('\\');
            }

            public static readonly string StorageFileSig = Sig.Rc("Windows.Storage.StorageFile", "Windows.Storage.IStorageFile");
            public static readonly string StorageFolderSig = Sig.Rc("Windows.Storage.StorageFolder", "Windows.Storage.IStorageFolder");
            private static readonly string StorageItemSig = Sig.Of("Windows.Storage.IStorageItem");
            private static readonly string BasicPropertiesSig = Sig.Rc("Windows.Storage.FileProperties.BasicProperties", new Guid("d05d55db-785e-4a66-be02-9beec58aea81"));

            private static readonly InterfaceDef BasicPropertiesDef = Manual(
                "Windows.Storage.FileProperties.IBasicProperties",
                new Guid("d05d55db-785e-4a66-be02-9beec58aea81"),
                ["get_Size() : UInt64", "get_DateModified() : System.DateTimeOffset", "get_ItemDate() : System.DateTimeOffset"]);

            private long _localFolder;
            private long _roamingFolder;
            private long _temporaryFolder;
            private long _installedLocation;

            public long InstalledLocation() => _installedLocation != 0
                ? _installedLocation
                : _installedLocation = Folder(new ItemState { GuestPath = InstallPath, HostPath = Files.PackageRoot, IsFolder = true, ReadOnly = true });

            public long LocalFolder() => _localFolder != 0 ? _localFolder : _localFolder = SandboxFolder(_rt.LocalFolderPath);

            public long RoamingFolder() => _roamingFolder != 0 ? _roamingFolder : _roamingFolder = SandboxFolder(_rt.LocalFolderPath[..^5] + "Roaming");

            public long TemporaryFolder() => _temporaryFolder != 0 ? _temporaryFolder : _temporaryFolder = SandboxFolder(_rt.LocalFolderPath[..^5] + "Temp");

            /// <summary>A writable app-data folder, created on the host if it is not there yet.</summary>
            private long SandboxFolder(string guestPath)
            {
                string host = Files.ResolveGuestPath(guestPath, out _);
                Directory.CreateDirectory(host);
                return Folder(new ItemState { GuestPath = guestPath, HostPath = host, IsFolder = true, ReadOnly = false });
            }

            /// <summary>The roots a guest path can be under, longest first.</summary>
            private IEnumerable<ItemState> Roots()
            {
                yield return StateOf<ItemState>(InstalledLocation())!;
                yield return StateOf<ItemState>(LocalFolder())!;
                yield return StateOf<ItemState>(RoamingFolder())!;
                yield return StateOf<ItemState>(TemporaryFolder())!;
            }

            private static readonly char[] InvalidNameCharacters = ['<', '>', ':', '"', '|', '?', '*', '\0'];

            /// <summary>A relative item name ("a.txt", "sub\a.txt"), or null when WinRT would refuse it as an argument.</summary>
            private static string? CleanRelative(string name)
            {
                // WinRT spells a relative path with '\'; '/' is accepted too, since refusing it would
                // stop a game for a spelling the files themselves do not care about.
                string relative = name.Replace('/', '\\').Trim().Trim('\\');
                if (relative.Length == 0 || relative.IndexOfAny(InvalidNameCharacters) >= 0 ||
                    relative.Split('\\').Any(s => s is "" or "." or ".."))
                {
                    return null;
                }

                return relative;
            }

            /// <summary>The item a relative path names below a folder (whether or not it exists).</summary>
            private ItemState ChildOf(ItemState folder, string relative, bool isFolder)
            {
                string guest = folder.GuestPath.TrimEnd('\\') + "\\" + relative;
                string host = folder.ReadOnly
                    ? Files.ResolveGuestPath(guest, out _)
                    : Path.Combine(folder.HostPath, relative.Replace('\\', Path.DirectorySeparatorChar));
                return new ItemState { GuestPath = guest, HostPath = host, IsFolder = isFolder, ReadOnly = folder.ReadOnly };
            }

            /// <summary>An absolute guest path under one of the app's roots, or null.</summary>
            private (ItemState Root, string Relative)? UnderRoot(string path)
            {
                string normal = path.Replace('/', '\\').TrimEnd('\\');
                foreach (ItemState root in Roots())
                {
                    if (normal.Equals(root.GuestPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return (root, string.Empty);
                    }

                    if (normal.StartsWith(root.GuestPath + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        return (root, normal[(root.GuestPath.Length + 1)..]);
                    }
                }

                return null;
            }

            /// <summary>An existing item at an absolute guest path: the result, or the HRESULT WinRT fails with.</summary>
            private (ItemState? Item, int HResult) ItemAtPath(string path, bool wantFolder)
            {
                if (path.Length < 3 || path[1] != ':')
                {
                    return (null, E_INVALIDARG);   // WinRT takes absolute paths only
                }

                ItemState item;
                if (UnderRoot(path) is { } under)
                {
                    if (under.Relative.Length == 0)
                    {
                        item = under.Root;
                    }
                    else if (CleanRelative(under.Relative) is { } relative)
                    {
                        item = ChildOf(under.Root, relative, wantFolder);
                    }
                    else
                    {
                        return (null, E_INVALIDARG);
                    }
                }
                else
                {
                    // Anywhere else resolves as the CRT does - the sandbox stands in for it.
                    string host = Files.ResolveGuestPath(path, out bool writable);
                    item = new ItemState { GuestPath = path.Replace('/', '\\'), HostPath = host, IsFolder = wantFolder, ReadOnly = !writable };
                }

                return Exists(item.HostPath, wantFolder, out int hr)
                    ? (new ItemState { GuestPath = item.GuestPath, HostPath = item.HostPath, IsFolder = wantFolder, ReadOnly = item.ReadOnly }, S_OK)
                    : (null, hr);
            }

            /// <summary>Whether a host path is an existing file (or folder); the HRESULT when it is not.</summary>
            private static bool Exists(string host, bool folder, out int hresult)
            {
                if (folder ? Directory.Exists(host) : System.IO.File.Exists(host))
                {
                    hresult = S_OK;
                    return true;
                }

                // The other kind of item under the name is a wrong-type argument, not a missing one.
                hresult = (folder ? System.IO.File.Exists(host) : Directory.Exists(host)) ? E_INVALIDARG : FileNotFound;
                return false;
            }

            private static long FileTime(DateTime utc) => utc.ToFileTimeUtc();

            private ComClass? _folderClass;
            private ComClass? _fileClass;

            public long Folder(ItemState state) => New(_folderClass ??= FolderClass(), state).Primary;

            public long File(ItemState state) => New(_fileClass ??= FileClass(), state).Primary;

            private static ItemState Item(RtCall c) => c.State<ItemState>();

            private Iface StorageItemMembers() => I(
                Meta("Windows.Storage.IStorageItem"),
                ("RenameAsync", c => c.RetObj(Rename(Item(c), c.Str(0), 2))),
                ("RenameAsync/2", c => c.RetObj(Rename(Item(c), c.Str(0), (int)c.P(1)))),
                ("DeleteAsync", c => c.RetObj(Delete(Item(c)))),
                ("DeleteAsync/2", c => c.RetObj(Delete(Item(c)))),   // Default and PermanentDelete are the same here: there is no recycle bin
                ("GetBasicPropertiesAsync", c => c.RetObj(BasicProperties(Item(c)))),
                ("get_Name", c => c.RetStr(Item(c).Name)),
                ("get_Path", c => c.RetStr(Item(c).GuestPath)),
                ("get_Attributes", c => c.RetU32(Attributes(Item(c)))),
                ("get_DateCreated", c => c.RetI64(FileTime(Item(c).IsFolder ? Directory.GetCreationTimeUtc(Item(c).HostPath) : System.IO.File.GetCreationTimeUtc(Item(c).HostPath)))),
                ("IsOfType", c => c.RetBool(c.P(0) == (Item(c).IsFolder ? 2 : 1))));   // StorageItemTypes: File 1, Folder 2

            private Iface ItemPropertiesMembers() => I(
                Meta("Windows.Storage.IStorageItemProperties"),
                ("get_DisplayName", c => c.RetStr(Item(c).IsFolder ? Item(c).Name : Path.GetFileNameWithoutExtension(Item(c).Name))),
                ("get_DisplayType", c => c.RetStr(Item(c).IsFolder ? "File folder" : Path.GetExtension(Item(c).Name).TrimStart('.').ToUpperInvariant() + " File")),
                ("get_FolderRelativeId", c => c.RetStr(string.Empty)));

            /// <summary>FileAttributes: ReadOnly 1, Directory 16, Archive 32.</summary>
            private static uint Attributes(ItemState item) => (item.IsFolder ? 16u : 32u) | (item.ReadOnly ? 1u : 0u);

            private ComClass FolderClass() => Class("Windows.Storage.StorageFolder", [
                I(
                    Meta("Windows.Storage.IStorageFolder"),
                    ("CreateFileAsync", c => c.RetObj(Create(Item(c), c.Str(0), 2, folder: false))),
                    ("CreateFileAsync/2", c => c.RetObj(Create(Item(c), c.Str(0), (int)c.P(1), folder: false))),
                    ("CreateFolderAsync", c => c.RetObj(Create(Item(c), c.Str(0), 2, folder: true))),
                    ("CreateFolderAsync/2", c => c.RetObj(Create(Item(c), c.Str(0), (int)c.P(1), folder: true))),
                    ("GetFileAsync", c => c.RetObj(Get(Item(c), c.Str(0), folder: false))),
                    ("GetFolderAsync", c => c.RetObj(Get(Item(c), c.Str(0), folder: true))),
                    ("GetItemAsync", c => c.RetObj(GetItem(Item(c), c.Str(0)))),
                    ("GetFilesAsync", c => c.RetObj(List(Item(c), files: true, folders: false))),
                    ("GetFoldersAsync", c => c.RetObj(List(Item(c), files: false, folders: true))),
                    ("GetItemsAsync", c => c.RetObj(List(Item(c), files: true, folders: true)))),
                StorageItemMembers(),
                ItemPropertiesMembers(),
            ]);

            private ComClass FileClass() => Class("Windows.Storage.StorageFile", [
                I(
                    Meta("Windows.Storage.IStorageFile"),
                    ("get_FileType", c => c.RetStr(Path.GetExtension(Item(c).Name))),
                    ("get_ContentType", c => c.RetStr(ContentType(Item(c).Name))),
                    ("OpenAsync", c => c.RetObj(Open(Item(c), (int)c.P(0)))),
                    ("CopyAsync", c => c.RetObj(Copy(Item(c), c.P(0), null, 2))),
                    ("CopyAsync/2", c => c.RetObj(Copy(Item(c), c.P(0), c.Str(1), 2))),
                    ("CopyAsync/3", c => c.RetObj(Copy(Item(c), c.P(0), c.Str(1), (int)c.P(2)))),
                    ("CopyAndReplaceAsync", c => c.RetObj(CopyAndReplace(Item(c), c.P(0), move: false))),
                    ("MoveAsync", c => c.RetObj(Move(Item(c), c.P(0), null, 2))),
                    ("MoveAsync/2", c => c.RetObj(Move(Item(c), c.P(0), c.Str(1), 2))),
                    ("MoveAsync/3", c => c.RetObj(Move(Item(c), c.P(0), c.Str(1), (int)c.P(2)))),
                    ("MoveAndReplaceAsync", c => c.RetObj(CopyAndReplace(Item(c), c.P(0), move: true)))),
                StorageItemMembers(),
                I(
                    Meta("Windows.Storage.Streams.IRandomAccessStreamReference"),
                    ("OpenReadAsync", c => c.RetObj(OpenRead(Item(c), withContentType: true)))),
                I(
                    Meta("Windows.Storage.Streams.IInputStreamReference"),
                    ("OpenSequentialReadAsync", c => c.RetObj(OpenRead(Item(c), withContentType: false)))),
                ItemPropertiesMembers(),
            ]);

            private static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".txt" => "text/plain",
                ".xml" => "text/xml",
                ".htm" or ".html" => "text/html",
                ".json" => "application/json",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".wma" => "audio/x-ms-wma",
                ".ogg" => "audio/ogg",
                ".mp4" => "video/mp4",
                ".wmv" => "video/x-ms-wmv",
                _ => "application/octet-stream",
            };

            /// <summary>CreationCollisionOption: GenerateUniqueName 0, ReplaceExisting 1, FailIfExists 2, OpenIfExists 3.</summary>
            private long Create(ItemState parent, string name, int option, bool folder)
            {
                string sig = folder ? StorageFolderSig : StorageFileSig;
                if (CleanRelative(name) is not { } relative)
                {
                    return FailedOperation(sig, E_INVALIDARG);
                }

                if (parent.ReadOnly)
                {
                    return FailedOperation(sig, E_ACCESSDENIED);
                }

                ItemState item = ChildOf(parent, relative, folder);
                bool taken = System.IO.File.Exists(item.HostPath) || Directory.Exists(item.HostPath);
                if (taken)
                {
                    switch (option)
                    {
                        case 0:
                            item = UniqueName(parent, relative, folder);
                            break;
                        case 1:
                            if (folder ? Directory.Exists(item.HostPath) : System.IO.File.Exists(item.HostPath))
                            {
                                if (folder)
                                {
                                    Directory.Delete(item.HostPath, recursive: true);
                                }
                                else
                                {
                                    System.IO.File.Delete(item.HostPath);
                                }
                            }
                            else
                            {
                                return FailedOperation(sig, AlreadyExists);
                            }

                            break;
                        case 3:
                            if (!Exists(item.HostPath, folder, out int hr))
                            {
                                return FailedOperation(sig, hr == E_INVALIDARG ? AlreadyExists : hr);
                            }

                            return OperationOfObject(sig, folder ? Folder(item) : File(item));
                        default:
                            return FailedOperation(sig, AlreadyExists);
                    }
                }

                string? directory = Path.GetDirectoryName(item.HostPath);
                if (directory is null || !Directory.Exists(directory))
                {
                    return FailedOperation(sig, PathNotFound);
                }

                try
                {
                    if (folder)
                    {
                        Directory.CreateDirectory(item.HostPath);
                    }
                    else
                    {
                        using FileStream _ = new(item.HostPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return FailedOperation(sig, E_ACCESSDENIED);
                }

                return OperationOfObject(sig, folder ? Folder(item) : File(item));
            }

            /// <summary>"name (2).ext", as WinRT's GenerateUniqueName spells it.</summary>
            private ItemState UniqueName(ItemState parent, string relative, bool folder)
            {
                string directory = Path.GetDirectoryName(relative.Replace('\\', '/'))?.Replace('/', '\\') ?? string.Empty;
                string leaf = Path.GetFileName(relative.Replace('\\', '/'));
                string stem = folder ? leaf : Path.GetFileNameWithoutExtension(leaf);
                string extension = folder ? string.Empty : Path.GetExtension(leaf);
                for (int n = 2; ; n++)
                {
                    string candidate = (directory.Length > 0 ? directory + "\\" : string.Empty) + $"{stem} ({n}){extension}";
                    ItemState item = ChildOf(parent, candidate, folder);
                    if (!System.IO.File.Exists(item.HostPath) && !Directory.Exists(item.HostPath))
                    {
                        return item;
                    }
                }
            }

            private long Get(ItemState parent, string name, bool folder)
            {
                string sig = folder ? StorageFolderSig : StorageFileSig;
                if (CleanRelative(name) is not { } relative)
                {
                    return FailedOperation(sig, E_INVALIDARG);
                }

                ItemState item = ChildOf(parent, relative, folder);
                return Exists(item.HostPath, folder, out int hr)
                    ? OperationOfObject(sig, folder ? Folder(item) : File(item))
                    : FailedOperation(sig, hr);
            }

            private long GetItem(ItemState parent, string name)
            {
                if (CleanRelative(name) is not { } relative)
                {
                    return FailedOperation(StorageItemSig, E_INVALIDARG);
                }

                ItemState item = ChildOf(parent, relative, isFolder: false);
                if (System.IO.File.Exists(item.HostPath))
                {
                    return OperationOfObject(StorageItemSig, PointerFor(Lookup(File(item))!, "Windows.Storage.IStorageItem"));
                }

                if (Directory.Exists(item.HostPath))
                {
                    item = ChildOf(parent, relative, isFolder: true);
                    return OperationOfObject(StorageItemSig, PointerFor(Lookup(Folder(item))!, "Windows.Storage.IStorageItem"));
                }

                return FailedOperation(StorageItemSig, FileNotFound);
            }

            /// <summary>GetFilesAsync / GetFoldersAsync / GetItemsAsync: the folder's direct children, folders first, each by name.</summary>
            private long List(ItemState folder, bool files, bool folders)
            {
                string element = files && folders ? StorageItemSig : files ? StorageFileSig : StorageFolderSig;
                string resultSig = Sig.P(Sig.VectorView, element);
                if (!Directory.Exists(folder.HostPath))
                {
                    return FailedOperation(resultSig, FileNotFound);
                }

                var items = new List<long>();
                if (folders)
                {
                    foreach (string name in Directory.EnumerateDirectories(folder.HostPath).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase))
                    {
                        long obj = Folder(ChildOf(folder, name, isFolder: true));
                        items.Add(files ? PointerFor(Lookup(obj)!, "Windows.Storage.IStorageItem") : obj);
                    }
                }

                if (files)
                {
                    foreach (string name in Directory.EnumerateFiles(folder.HostPath).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase))
                    {
                        long obj = File(ChildOf(folder, name, isFolder: false));
                        items.Add(folders ? PointerFor(Lookup(obj)!, "Windows.Storage.IStorageItem") : obj);
                    }
                }

                return OperationOfObject(resultSig, ObjectView(element, items));
            }

            /// <summary>NameCollisionOption: GenerateUniqueName 0, ReplaceExisting 1, FailIfExists 2.</summary>
            private long Rename(ItemState item, string newName, int option)
            {
                if (CleanRelative(newName) is not { } name || name.Contains('\\'))
                {
                    return AsyncAction(E_INVALIDARG);
                }

                if (item.ReadOnly)
                {
                    return AsyncAction(E_ACCESSDENIED);
                }

                string parentGuest = item.GuestPath[..item.GuestPath.TrimEnd('\\').LastIndexOf('\\')];
                string parentHost = Path.GetDirectoryName(item.HostPath)!;
                ItemState parent = new() { GuestPath = parentGuest, HostPath = parentHost, IsFolder = true, ReadOnly = false };
                ItemState target = ChildOf(parent, name, item.IsFolder);
                bool taken = (System.IO.File.Exists(target.HostPath) || Directory.Exists(target.HostPath)) &&
                             !string.Equals(target.HostPath, item.HostPath, StringComparison.OrdinalIgnoreCase);
                if (taken)
                {
                    if (option == 0)
                    {
                        target = UniqueName(parent, name, item.IsFolder);
                    }
                    else if (option == 1 && !item.IsFolder && System.IO.File.Exists(target.HostPath))
                    {
                        System.IO.File.Delete(target.HostPath);
                    }
                    else
                    {
                        return AsyncAction(AlreadyExists);
                    }
                }

                try
                {
                    if (item.IsFolder)
                    {
                        Directory.Move(item.HostPath, target.HostPath);
                    }
                    else
                    {
                        System.IO.File.Move(item.HostPath, target.HostPath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return AsyncAction(ex is FileNotFoundException or DirectoryNotFoundException ? FileNotFound : E_ACCESSDENIED);
                }

                item.GuestPath = target.GuestPath;
                item.HostPath = target.HostPath;
                return AsyncAction();
            }

            private long Delete(ItemState item)
            {
                if (item.ReadOnly)
                {
                    return AsyncAction(E_ACCESSDENIED);
                }

                try
                {
                    if (item.IsFolder && Directory.Exists(item.HostPath))
                    {
                        Directory.Delete(item.HostPath, recursive: true);
                    }
                    else if (!item.IsFolder && System.IO.File.Exists(item.HostPath))
                    {
                        System.IO.File.Delete(item.HostPath);
                    }
                    else
                    {
                        return AsyncAction(FileNotFound);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return AsyncAction(E_ACCESSDENIED);
                }

                return AsyncAction();
            }

            private long BasicProperties(ItemState item)
            {
                if (!(item.IsFolder ? Directory.Exists(item.HostPath) : System.IO.File.Exists(item.HostPath)))
                {
                    return FailedOperation(BasicPropertiesSig, FileNotFound);
                }

                ComClass cls = Class("Windows.Storage.FileProperties.BasicProperties", [
                    I(
                        BasicPropertiesDef,
                        ("get_Size", c => c.RetU64(Item(c).IsFolder ? 0 : (ulong)new FileInfo(Item(c).HostPath).Length)),
                        ("get_DateModified", c => c.RetI64(FileTime(Item(c).IsFolder ? Directory.GetLastWriteTimeUtc(Item(c).HostPath) : System.IO.File.GetLastWriteTimeUtc(Item(c).HostPath)))),
                        ("get_ItemDate", c => c.RetI64(FileTime(Item(c).IsFolder ? Directory.GetCreationTimeUtc(Item(c).HostPath) : System.IO.File.GetCreationTimeUtc(Item(c).HostPath))))),
                ]);
                return OperationOfObject(BasicPropertiesSig, New(cls, item).Primary);
            }

            private static readonly string RandomAccessStreamSig = Sig.Of("Windows.Storage.Streams.IRandomAccessStream");
            private static readonly string StreamWithContentTypeSig = Sig.Of("Windows.Storage.Streams.IRandomAccessStreamWithContentType");
            private static readonly string InputStreamSig = Sig.Of("Windows.Storage.Streams.IInputStream");

            /// <summary>FileAccessMode: Read 0, ReadWrite 1.</summary>
            private long Open(ItemState file, int mode)
            {
                bool write = mode == 1;
                if (write && file.ReadOnly)
                {
                    return FailedOperation(RandomAccessStreamSig, E_ACCESSDENIED);
                }

                if (!System.IO.File.Exists(file.HostPath))
                {
                    return FailedOperation(RandomAccessStreamSig, FileNotFound);
                }

                FileStream stream = new(
                    file.HostPath,
                    FileMode.Open,
                    write ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return OperationOfObject(RandomAccessStreamSig, RandomAccessStream(new SharedStream(stream), canRead: true, canWrite: write, ContentType(file.Name)).Primary);
            }

            private long OpenRead(ItemState file, bool withContentType)
            {
                string sig = withContentType ? StreamWithContentTypeSig : InputStreamSig;
                if (!System.IO.File.Exists(file.HostPath))
                {
                    return FailedOperation(sig, FileNotFound);
                }

                FileStream stream = new(file.HostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                ComObject obj = RandomAccessStream(new SharedStream(stream), canRead: true, canWrite: false, ContentType(file.Name));
                return OperationOfObject(sig, PointerFor(obj, withContentType ? "Windows.Storage.Streams.IRandomAccessStreamWithContentType" : "Windows.Storage.Streams.IInputStream"));
            }

            private long Copy(ItemState file, long destinationFolder, string? newName, int option)
            {
                if (StateOf<ItemState>(destinationFolder) is not { IsFolder: true } folder)
                {
                    return FailedOperation(StorageFileSig, E_INVALIDARG);
                }

                if (folder.ReadOnly)
                {
                    return FailedOperation(StorageFileSig, E_ACCESSDENIED);
                }

                if (!System.IO.File.Exists(file.HostPath))
                {
                    return FailedOperation(StorageFileSig, FileNotFound);
                }

                if (CleanRelative(newName ?? file.Name) is not { } name || name.Contains('\\'))
                {
                    return FailedOperation(StorageFileSig, E_INVALIDARG);
                }

                ItemState target = ChildOf(folder, name, isFolder: false);
                if (System.IO.File.Exists(target.HostPath) || Directory.Exists(target.HostPath))
                {
                    if (option == 0)
                    {
                        target = UniqueName(folder, name, folder: false);
                    }
                    else if (option != 1 || Directory.Exists(target.HostPath))
                    {
                        return FailedOperation(StorageFileSig, AlreadyExists);
                    }
                }

                System.IO.File.Copy(file.HostPath, target.HostPath, overwrite: true);
                return OperationOfObject(StorageFileSig, File(target));
            }

            private long Move(ItemState file, long destinationFolder, string? newName, int option)
            {
                if (file.ReadOnly)
                {
                    return AsyncAction(E_ACCESSDENIED);
                }

                if (StateOf<ItemState>(destinationFolder) is not { IsFolder: true } folder || CleanRelative(newName ?? file.Name) is not { } name || name.Contains('\\'))
                {
                    return AsyncAction(E_INVALIDARG);
                }

                if (folder.ReadOnly)
                {
                    return AsyncAction(E_ACCESSDENIED);
                }

                ItemState target = ChildOf(folder, name, isFolder: false);
                if (System.IO.File.Exists(target.HostPath) || Directory.Exists(target.HostPath))
                {
                    if (option == 0)
                    {
                        target = UniqueName(folder, name, folder: false);
                    }
                    else if (option != 1 || Directory.Exists(target.HostPath))
                    {
                        return AsyncAction(AlreadyExists);
                    }
                }

                if (!System.IO.File.Exists(file.HostPath))
                {
                    return AsyncAction(FileNotFound);
                }

                System.IO.File.Move(file.HostPath, target.HostPath, overwrite: true);
                file.GuestPath = target.GuestPath;
                file.HostPath = target.HostPath;
                return AsyncAction();
            }

            private long CopyAndReplace(ItemState file, long replaced, bool move)
            {
                if (StateOf<ItemState>(replaced) is not { IsFolder: false } target)
                {
                    return AsyncAction(E_INVALIDARG);
                }

                if (target.ReadOnly || (move && file.ReadOnly))
                {
                    return AsyncAction(E_ACCESSDENIED);
                }

                if (!System.IO.File.Exists(file.HostPath))
                {
                    return AsyncAction(FileNotFound);
                }

                if (move)
                {
                    System.IO.File.Move(file.HostPath, target.HostPath, overwrite: true);
                    file.GuestPath = target.GuestPath;
                    file.HostPath = target.HostPath;
                }
                else
                {
                    System.IO.File.Copy(file.HostPath, target.HostPath, overwrite: true);
                }

                return AsyncAction();
            }

            /// <summary>
            /// <c>ms-appx:///path</c> is the package; <c>ms-appdata:///local/</c>, <c>/roaming/</c> and
            /// <c>/temp/</c> the app-data folders. Anything else is not an application URI.
            /// </summary>
            private long FileFromApplicationUri(long uriPointer)
            {
                (ItemState? item, int hr) = FileAtApplicationUri(uriPointer);
                return item is null ? FailedOperation(StorageFileSig, hr) : OperationOfObject(StorageFileSig, File(item));
            }

            /// <summary>The existing file an application URI names, or the HRESULT WinRT fails with.</summary>
            private (ItemState? Item, int HResult) FileAtApplicationUri(long uriPointer)
            {
                if (UriText(uriPointer) is not { } text || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
                {
                    return (null, E_INVALIDARG);
                }

                string path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
                ItemState? root = null;
                if (uri.Scheme.Equals("ms-appx", StringComparison.OrdinalIgnoreCase))
                {
                    root = StateOf<ItemState>(InstalledLocation());
                }
                else if (uri.Scheme.Equals("ms-appdata", StringComparison.OrdinalIgnoreCase))
                {
                    int slash = path.IndexOf('/');
                    string area = slash < 0 ? path : path[..slash];
                    path = slash < 0 ? string.Empty : path[(slash + 1)..];
                    root = area.ToLowerInvariant() switch
                    {
                        "local" => StateOf<ItemState>(LocalFolder()),
                        "roaming" => StateOf<ItemState>(RoamingFolder()),
                        "temp" => StateOf<ItemState>(TemporaryFolder()),
                        _ => null,
                    };
                }

                if (root is null || CleanRelative(path) is not { } relative)
                {
                    return (null, E_INVALIDARG);
                }

                ItemState item = ChildOf(root, relative, isFolder: false);
                return Exists(item.HostPath, folder: false, out int hr) ? (item, S_OK) : (null, hr);
            }

            private void RegisterStorage()
            {
                RegisterPackage();
                RegisterApplicationDataClasses();

                Factory(
                    "Windows.Storage.StorageFile",
                    I(
                        Meta("Windows.Storage.IStorageFileStatics"),
                        ("GetFileFromPathAsync", c =>
                        {
                            (ItemState? item, int hr) = ItemAtPath(c.Str(0), wantFolder: false);
                            c.RetObj(item is null ? FailedOperation(StorageFileSig, hr) : OperationOfObject(StorageFileSig, File(item)));
                        }),
                        ("GetFileFromApplicationUriAsync", c => c.RetObj(FileFromApplicationUri(c.P(0))))));

                Factory(
                    "Windows.Storage.StorageFolder",
                    I(
                        Meta("Windows.Storage.IStorageFolderStatics"),
                        ("GetFolderFromPathAsync", c =>
                        {
                            (ItemState? item, int hr) = ItemAtPath(c.Str(0), wantFolder: true);
                            c.RetObj(item is null ? FailedOperation(StorageFolderSig, hr) : OperationOfObject(StorageFolderSig, Folder(item)));
                        })));

                // WP8.0 gave a native game none of the libraries: each one needs a capability the
                // store did not grant to these titles, and is refused.
                Factory(
                    "Windows.Storage.KnownFolders",
                    I(
                        Meta("Windows.Storage.IKnownFoldersStatics"),
                        ("get_MusicLibrary", c => c.Fail(E_ACCESSDENIED)),
                        ("get_PicturesLibrary", c => c.Fail(E_ACCESSDENIED)),
                        ("get_VideosLibrary", c => c.Fail(E_ACCESSDENIED)),
                        ("get_DocumentsLibrary", c => c.Fail(E_ACCESSDENIED)),
                        ("get_HomeGroup", c => c.Fail(E_ACCESSDENIED)),
                        ("get_RemovableDevices", c => c.Fail(E_ACCESSDENIED)),
                        ("get_MediaServerDevices", c => c.Fail(E_ACCESSDENIED))));

                RegisterFileIO();
            }

            // -----------------------------------------------------------------------------------
            // FileIO and PathIO: whole-file reads and writes.
            // -----------------------------------------------------------------------------------

            /// <summary>UnicodeEncoding: Utf8 0, Utf16LE 1, Utf16BE 2.</summary>
            private static System.Text.Encoding TextEncoding(int encoding) => encoding switch
            {
                1 => new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: false),
                2 => new System.Text.UnicodeEncoding(bigEndian: true, byteOrderMark: false),
                _ => new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };

            /// <summary>Text with its encoding taken from a byte-order mark, UTF-8 without one.</summary>
            private static string DecodeText(byte[] bytes, int? encoding)
            {
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
                }

                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                {
                    return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
                }

                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                {
                    return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
                }

                return TextEncoding(encoding ?? 0).GetString(bytes);
            }

            private static List<string> SplitLines(string text)
            {
                List<string> lines = text.Replace("\r\n", "\n").Split('\n').ToList();
                if (lines.Count > 0 && lines[^1].Length == 0)
                {
                    lines.RemoveAt(lines.Count - 1);
                }

                return lines;
            }

            /// <summary>The file a FileIO call names: one of these StorageFiles, or a guest path for PathIO.</summary>
            private (ItemState? File, int HResult) Target(RtCall c, bool byPath)
            {
                if (byPath)
                {
                    return ItemAtPath(c.Str(0), wantFolder: false) is { Item: { } item }
                        ? (item, S_OK)
                        : WritableTargetAtPath(c.Str(0));
                }

                return StateOf<ItemState>(c.P(0)) is { IsFolder: false } file ? (file, S_OK) : (null, E_INVALIDARG);
            }

            /// <summary>A PathIO write may name a file that does not exist yet only if its folder does; the write creates it.</summary>
            private (ItemState? File, int HResult) WritableTargetAtPath(string path)
            {
                (ItemState? item, int hr) = ItemAtPath(path, wantFolder: false);
                return (item, hr);
            }

            private Member Read(bool byPath, Func<byte[], int?, Action<RtCall>> result, string resultSig, bool hasEncoding)
                => c =>
                {
                    (ItemState? file, int hr) = Target(c, byPath);
                    if (file is null)
                    {
                        c.RetObj(FailedOperation(resultSig, hr));
                        return;
                    }

                    byte[] bytes;
                    try
                    {
                        bytes = System.IO.File.ReadAllBytes(file.HostPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        c.RetObj(FailedOperation(resultSig, ex is FileNotFoundException ? FileNotFound : E_ACCESSDENIED));
                        return;
                    }

                    c.RetObj(Operation(resultSig, result(bytes, hasEncoding ? (int)c.P(1) : null)));
                };

            /// <summary>A write: <paramref name="bytes"/> makes the bytes from the call, then they replace or extend the file.</summary>
            private Member Write(bool byPath, Func<RtCall, byte[]?> bytes, bool append)
                => c =>
                {
                    (ItemState? file, int hr) = Target(c, byPath);
                    if (file is null)
                    {
                        c.RetObj(AsyncAction(hr));
                        return;
                    }

                    if (file.ReadOnly)
                    {
                        c.RetObj(AsyncAction(E_ACCESSDENIED));
                        return;
                    }

                    if (bytes(c) is not { } data)
                    {
                        c.RetObj(AsyncAction(E_INVALIDARG));
                        return;
                    }

                    try
                    {
                        using FileStream stream = new(file.HostPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        stream.Write(data);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        c.RetObj(AsyncAction(E_ACCESSDENIED));
                        return;
                    }

                    c.RetObj(AsyncAction());
                };

            private byte[] Encode(string text, int encoding) => TextEncoding(encoding).GetBytes(text);

            /// <summary>The strings of an IIterable&lt;String&gt; this runtime made; a game's own collection cannot be read without running it, so it is noted and refused.</summary>
            private byte[]? Lines(long iterable, int encoding)
            {
                if (StringsOf(iterable) is not { } lines)
                {
                    Note("FileIO/PathIO.WriteLinesAsync: lines from a collection the game built itself are not readable here");
                    return null;
                }

                return Encode(string.Concat(lines.Select(l => l + "\r\n")), encoding);
            }

            private byte[]? BufferBytes(long buffer) => StateOf<BufferState>(buffer) is { } b ? _e.ReadMemory(b.Data, (int)b.Length) : null;

            private byte[] ArrayBytes(RtCall c, int index)
            {
                (uint length, long data) = c.Array(index);
                return length == 0 ? [] : _e.ReadMemory(data, (int)length);
            }

            private void RegisterFileIO()
            {
                foreach ((string className, string statics, bool byPath) in new[]
                {
                    ("Windows.Storage.FileIO", "Windows.Storage.IFileIOStatics", false),
                    ("Windows.Storage.PathIO", "Windows.Storage.IPathIOStatics", true),
                })
                {
                    string listSig = Sig.P(Sig.Vector, Sig.String);
                    Factory(
                        className,
                        I(
                            Meta(statics),
                            ("ReadTextAsync", Read(byPath, (b, e) => c => c.RetStr(DecodeText(b, e)), Sig.String, hasEncoding: false)),
                            ("ReadTextAsync/2", Read(byPath, (b, e) => c => c.RetStr(DecodeText(b, e)), Sig.String, hasEncoding: true)),
                            ("WriteTextAsync", Write(byPath, c => Encode(c.Str(1), 0), append: false)),
                            ("WriteTextAsync/2", Write(byPath, c => Encode(c.Str(1), (int)c.P(2)), append: false)),
                            ("AppendTextAsync", Write(byPath, c => Encode(c.Str(1), 0), append: true)),
                            ("AppendTextAsync/2", Write(byPath, c => Encode(c.Str(1), (int)c.P(2)), append: true)),
                            ("ReadLinesAsync", Read(byPath, (b, e) => c => c.RetObj(StringVector(SplitLines(DecodeText(b, e)))), listSig, hasEncoding: false)),
                            ("ReadLinesAsync/2", Read(byPath, (b, e) => c => c.RetObj(StringVector(SplitLines(DecodeText(b, e)))), listSig, hasEncoding: true)),
                            ("WriteLinesAsync", Write(byPath, c => Lines(c.P(1), 0), append: false)),
                            ("WriteLinesAsync/2", Write(byPath, c => Lines(c.P(1), (int)c.P(2)), append: false)),
                            ("AppendLinesAsync", Write(byPath, c => Lines(c.P(1), 0), append: true)),
                            ("AppendLinesAsync/2", Write(byPath, c => Lines(c.P(1), (int)c.P(2)), append: true)),
                            ("ReadBufferAsync", Read(byPath, (b, _) => c => c.RetObj(NewBuffer(b)), Sig.Of("Windows.Storage.Streams.IBuffer"), hasEncoding: false)),
                            ("WriteBufferAsync", Write(byPath, c => BufferBytes(c.P(1)), append: false)),
                            ("WriteBytesAsync", Write(byPath, c => ArrayBytes(c, 1), append: false))));
                }
            }

            // -----------------------------------------------------------------------------------
            // ApplicationData: the folders, and settings that survive the game.
            // -----------------------------------------------------------------------------------

            /// <summary>One settings container: its values and child containers, persisted as one tree per locality.</summary>
            public sealed class ContainerState
            {
                public required string Name { get; init; }

                public required int Locality { get; init; }

                public required MapState Values { get; init; }

                public required long ValuesObject { get; init; }

                public Dictionary<string, ContainerState> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

                public ContainerState? Parent { get; init; }

                public long Object { get; set; }
            }

            private readonly Dictionary<int, ContainerState> _settingsRoots = new();
            private uint _dataVersion;
            private bool _settingsLoaded;
            private readonly List<long> _dataChangedHandlers = new();

            /// <summary>Settings live beside the local folder rather than in it, so they never show up in a game's own folder listing.</summary>
            private string SettingsFile
            {
                get
                {
                    string local = Files.LocalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return Path.Combine(Path.GetDirectoryName(local) ?? local, Path.GetFileName(local) + ".settings.json");
                }
            }

            private ContainerState SettingsRoot(int locality)
            {
                LoadSettings();
                if (!_settingsRoots.TryGetValue(locality, out ContainerState? root))
                {
                    root = NewContainer(locality == 1 ? "Roaming" : "Local", locality, null);
                    _settingsRoots[locality] = root;
                }

                return root;
            }

            private ContainerState NewContainer(string name, int locality, ContainerState? parent)
            {
                ComObject values = New(MapClass("Windows.Storage.ApplicationDataContainerSettings"), new MapState(StringComparer.OrdinalIgnoreCase));
                MapState map = values.As<MapState>();
                map.Changed = SaveSettings;
                map.Accepts = AcceptsSetting;
                return new ContainerState { Name = name, Locality = locality, Values = map, ValuesObject = values.Primary, Parent = parent };
            }

            /// <summary>Settings take property values and composites; anything else is the documented ERROR_DATATYPE_MISMATCH.</summary>
            private int AcceptsSetting(long value)
            {
                if (value == 0 || Unbox(value) is not null || StateOf<MapState>(value) is not null)
                {
                    return S_OK;
                }

                // A value boxed by the game itself (its own IPropertyValue) is valid on a device; it is
                // kept for this run but cannot be read back to be persisted.
                Note($"ApplicationDataContainer: a value at 0x{value:X8} is not one this runtime boxed; kept, but not persisted");
                return S_OK;
            }

            private ComClass? _containerClass;

            private long ContainerObject(ContainerState state)
            {
                if (state.Object != 0)
                {
                    return state.Object;
                }

                _containerClass ??= Class("Windows.Storage.ApplicationDataContainer", [
                    I(
                        Meta("Windows.Storage.IApplicationDataContainer"),
                        ("get_Name", c => c.RetStr(c.State<ContainerState>().Name)),
                        ("get_Locality", c => c.RetU32((uint)c.State<ContainerState>().Locality)),
                        ("get_Values", c => c.RetObj(c.State<ContainerState>().ValuesObject)),
                        ("get_Containers", c => c.RetObj(MapView(c.State<ContainerState>().Children.ToDictionary(p => p.Key, p => ContainerObject(p.Value))))),
                        ("CreateContainer", c =>
                        {
                            ContainerState parent = c.State<ContainerState>();
                            string name = c.Str(0);
                            if (name.Length == 0 || name.Length > 255)
                            {
                                c.Fail(E_INVALIDARG);
                                return;
                            }

                            // ApplicationDataCreateDisposition: Always 0, Existing 1.
                            if (!parent.Children.TryGetValue(name, out ContainerState? child))
                            {
                                if (c.P(1) == 1)
                                {
                                    c.Fail(FileNotFound);
                                    return;
                                }

                                child = NewContainer(name, parent.Locality, parent);
                                parent.Children[name] = child;
                                SaveSettings();
                            }

                            c.RetObj(ContainerObject(child));
                        }),
                        ("DeleteContainer", c =>
                        {
                            if (c.State<ContainerState>().Children.Remove(c.Str(0)))
                            {
                                SaveSettings();
                            }

                            c.Ok();
                        })),
                    I(Closable, ("Close", c => c.Ok())),
                ]);
                return state.Object = New(_containerClass, state).Primary;
            }

            private void LoadSettings()
            {
                if (_settingsLoaded)
                {
                    return;
                }

                _settingsLoaded = true;
                try
                {
                    if (!System.IO.File.Exists(SettingsFile))
                    {
                        return;
                    }

                    JsonNode? root = JsonNode.Parse(System.IO.File.ReadAllText(SettingsFile));
                    _dataVersion = (uint?)root?["version"] ?? 0;
                    foreach ((string key, int locality) in new[] { ("local", 0), ("roaming", 1) })
                    {
                        if (root?[key] is JsonObject tree)
                        {
                            ContainerState container = NewContainer(locality == 1 ? "Roaming" : "Local", locality, null);
                            _settingsRoots[locality] = container;
                            LoadContainer(container, tree);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
                {
                    Note($"settings unreadable ({ex.GetType().Name}: {ex.Message}); starting empty");
                }
            }

            private void LoadContainer(ContainerState container, JsonObject tree)
            {
                if (tree["values"] is JsonObject values)
                {
                    foreach ((string key, JsonNode? value) in values)
                    {
                        if (value is not null && Deserialize(value) is { } pointer)
                        {
                            container.Values.Values[key] = pointer;
                        }
                    }
                }

                if (tree["containers"] is JsonObject children)
                {
                    foreach ((string name, JsonNode? child) in children)
                    {
                        ContainerState state = NewContainer(name, container.Locality, container);
                        container.Children[name] = state;
                        if (child is JsonObject childTree)
                        {
                            LoadContainer(state, childTree);
                        }
                    }
                }
            }

            private void SaveSettings()
            {
                try
                {
                    JsonObject root = new() { ["version"] = _dataVersion };
                    foreach ((int locality, ContainerState container) in _settingsRoots)
                    {
                        root[locality == 1 ? "roaming" : "local"] = SaveContainer(container);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                    string temporary = SettingsFile + ".tmp";
                    System.IO.File.WriteAllText(temporary, root.ToJsonString());
                    System.IO.File.Move(temporary, SettingsFile, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Note($"settings not saved: {ex.Message}");
                }
            }

            private JsonObject SaveContainer(ContainerState container)
            {
                JsonObject values = new();
                foreach ((string key, long pointer) in container.Values.Values)
                {
                    if (Serialize(pointer) is { } node)
                    {
                        values[key] = node;
                    }
                }

                JsonObject children = new();
                foreach ((string name, ContainerState child) in container.Children)
                {
                    children[name] = SaveContainer(child);
                }

                return new JsonObject { ["values"] = values, ["containers"] = children };
            }

            /// <summary>A boxed value or a composite as JSON: {"t": PropertyType, "v": value}, or {"composite": {...}}.</summary>
            private JsonNode? Serialize(long pointer)
            {
                if (Unbox(pointer) is { } boxed)
                {
                    JsonNode? value = boxed.IsArray
                        ? new JsonArray(((Array)boxed.Value).Cast<object>().Select(v => ScalarToJson(boxed.Element, v)).ToArray())
                        : ScalarToJson(boxed.Type, boxed.Value);
                    return new JsonObject { ["t"] = (int)boxed.Type, ["v"] = value };
                }

                if (StateOf<MapState>(pointer) is { } composite)
                {
                    JsonObject members = new();
                    foreach ((string key, long member) in composite.Values)
                    {
                        if (Serialize(member) is { } node)
                        {
                            members[key] = node;
                        }
                    }

                    return new JsonObject { ["composite"] = members };
                }

                return null;
            }

            private static JsonNode? ScalarToJson(PropertyType type, object value) => type switch
            {
                PropertyType.Guid => JsonValue.Create(((Guid)value).ToString("D")),
                PropertyType.Char16 => JsonValue.Create((int)(char)value),
                PropertyType.Point or PropertyType.Size or PropertyType.Rect => new JsonArray(((float[])value).Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                _ => JsonValue.Create(value),
            };

            private static object ScalarFromJson(PropertyType type, JsonNode node) => type switch
            {
                PropertyType.UInt8 => (byte)node,
                PropertyType.Int16 => (short)node,
                PropertyType.UInt16 => (ushort)node,
                PropertyType.Int32 => (int)node,
                PropertyType.UInt32 => (uint)node,
                PropertyType.Int64 or PropertyType.DateTime or PropertyType.TimeSpan => (long)node,
                PropertyType.UInt64 => (ulong)node,
                PropertyType.Single => (float)node,
                PropertyType.Double => (double)node,
                PropertyType.Char16 => (char)(int)node,
                PropertyType.Boolean => (bool)node,
                PropertyType.String => (string)node!,
                PropertyType.Guid => Guid.Parse((string)node!),
                PropertyType.Point or PropertyType.Size or PropertyType.Rect => node.AsArray().Select(n => (float)n!).ToArray(),
                _ => throw new FormatException($"property type {type} is not persisted"),
            };

            private long? Deserialize(JsonNode node)
            {
                if (node["composite"] is JsonObject members)
                {
                    ComObject composite = PropertySet("Windows.Storage.ApplicationDataCompositeValue", StringComparer.OrdinalIgnoreCase);
                    foreach ((string key, JsonNode? member) in members)
                    {
                        if (member is not null && Deserialize(member) is { } pointer)
                        {
                            composite.As<MapState>().Values[key] = pointer;
                        }
                    }

                    return composite.Primary;
                }

                var type = (PropertyType)(int)node["t"]!;
                Boxed boxed;
                if (type > PropertyType.ArrayOffset)
                {
                    PropertyType element = type - (int)PropertyType.ArrayOffset;
                    JsonArray items = node["v"]!.AsArray();
                    Array array = NewArray(element, items.Count);
                    for (int i = 0; i < items.Count; i++)
                    {
                        array.SetValue(ScalarFromJson(element, items[i]!), i);
                    }

                    boxed = new Boxed(type, array);
                }
                else
                {
                    boxed = new Boxed(type, ScalarFromJson(type, node["v"]!));
                }

                return Box(boxed);
            }

            private void ClearSettings(int? locality)
            {
                foreach (int which in locality is { } only ? [only] : new[] { 0, 1 })
                {
                    ContainerState root = SettingsRoot(which);
                    root.Values.Values.Clear();
                    root.Children.Clear();
                }

                SaveSettings();
            }

            private static void ClearFolder(string host)
            {
                if (!Directory.Exists(host))
                {
                    return;
                }

                foreach (string entry in Directory.EnumerateFileSystemEntries(host))
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else
                    {
                        System.IO.File.Delete(entry);
                    }
                }
            }

            /// <summary>ApplicationDataLocality: Local 0, Roaming 1, Temporary 2.</summary>
            private long Clear(int? locality)
            {
                try
                {
                    if (locality is null or 0)
                    {
                        // The local folder's host directory also holds the redirected roaming and temp
                        // folders (the CRT's path mapping puts them there), so only its own entries go.
                        string roamingHost = StateOf<ItemState>(RoamingFolder())!.HostPath;
                        string tempHost = StateOf<ItemState>(TemporaryFolder())!.HostPath;
                        foreach (string entry in Directory.EnumerateFileSystemEntries(Files.LocalRoot))
                        {
                            if (roamingHost.StartsWith(entry, StringComparison.OrdinalIgnoreCase) || tempHost.StartsWith(entry, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (Directory.Exists(entry))
                            {
                                Directory.Delete(entry, recursive: true);
                            }
                            else
                            {
                                System.IO.File.Delete(entry);
                            }
                        }
                    }

                    if (locality is null or 1)
                    {
                        ClearFolder(StateOf<ItemState>(RoamingFolder())!.HostPath);
                    }

                    if (locality is null or 2)
                    {
                        ClearFolder(StateOf<ItemState>(TemporaryFolder())!.HostPath);
                    }

                    if (locality is not 2)
                    {
                        ClearSettings(locality);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return AsyncAction(E_ACCESSDENIED);
                }

                return AsyncAction();
            }

            private void RegisterApplicationDataClasses()
            {
                ComClass setVersionRequest = Class("Windows.Storage.SetVersionRequest", [
                    I(
                        Meta("Windows.Storage.ISetVersionRequest"),
                        ("get_CurrentVersion", c => c.RetU32((((uint, uint))c.Self.State!).Item1)),
                        ("get_DesiredVersion", c => c.RetU32((((uint, uint))c.Self.State!).Item2)),
                        ("GetDeferral", c => c.RetObj(New(
                            Class("Windows.Storage.SetVersionDeferral", [I(Meta("Windows.Storage.ISetVersionDeferral"), ("Complete", d => d.Ok()))]),
                            null).Primary))),
                ]);

                ComClass applicationData = Class("Windows.Storage.ApplicationData", [
                    I(
                        Meta("Windows.Storage.IApplicationData"),
                        ("get_Version", c =>
                        {
                            LoadSettings();
                            c.RetU32(_dataVersion);
                        }),
                        ("SetVersionAsync", c =>
                        {
                            // The handler migrates the data from the current version to the desired
                            // one; it runs before the action completes.
                            LoadSettings();
                            uint current = _dataVersion;
                            uint desired = (uint)c.P(0);
                            long handler = c.P(1);
                            _dataVersion = desired;
                            SaveSettings();
                            c.RetObj(AsyncAction());
                            long request = New(setVersionRequest, (current, desired)).Primary;
                            InvokeThenReturn([handler], h => [h, request], S_OK, "ApplicationDataSetVersionHandler");
                        }),
                        ("ClearAsync", c => c.RetObj(Clear(null))),
                        ("ClearAsync/2", c => c.RetObj(Clear((int)c.P(0)))),
                        ("get_LocalSettings", c => c.RetObj(ContainerObject(SettingsRoot(0)))),
                        ("get_RoamingSettings", c => c.RetObj(ContainerObject(SettingsRoot(1)))),
                        ("get_LocalFolder", c => c.RetObj(LocalFolder())),
                        ("get_RoamingFolder", c => c.RetObj(RoamingFolder())),
                        ("get_TemporaryFolder", c => c.RetObj(TemporaryFolder())),
                        ("add_DataChanged", c =>
                        {
                            _dataChangedHandlers.Add(c.P(0));
                            c.RetU64((ulong)_dataChangedHandlers.Count);
                            _rt.KeepAlive(c.P(0));
                        }),
                        ("remove_DataChanged", c =>
                        {
                            long token = c.P64(0);
                            if (token >= 1 && token <= _dataChangedHandlers.Count)
                            {
                                _dataChangedHandlers[(int)token - 1] = 0;
                            }

                            c.Ok();
                        }),
                        ("SignalDataChanged", c =>
                        {
                            long sender = c.Self.Primary;
                            if (!InvokeThenReturn(_dataChangedHandlers, h => [h, sender, 0], S_OK, "ApplicationData.DataChanged"))
                            {
                                c.Ok();
                            }
                        }),
                        ("get_RoamingStorageQuota", c => c.RetU64(100))),   // kilobytes, WP8's roaming quota
                ]);

                long current = 0;
                Factory(
                    "Windows.Storage.ApplicationData",
                    I(Meta("Windows.Storage.IApplicationDataStatics"), ("get_Current", c => c.RetObj(current != 0 ? current : current = New(applicationData, null).Primary))));

                Factory(
                    "Windows.Storage.ApplicationDataCompositeValue",
                    I(ActivationFactory, ("ActivateInstance", c => c.RetObj(PropertySet("Windows.Storage.ApplicationDataCompositeValue", StringComparer.OrdinalIgnoreCase).Primary))));
            }
        }
    }
}

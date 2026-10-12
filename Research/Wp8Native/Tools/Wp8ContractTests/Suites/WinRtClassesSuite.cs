using System.Collections;
using System.Reflection;
using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The WinRT system classes (WinRtObjects.cs and its partials): Package, StorageFolder/StorageFile,
/// FileIO/PathIO, streams, DataReader/DataWriter, ApplicationData settings, PropertyValue,
/// PropertySet, Uri, Launcher, ThreadPoolTimer, VibrationDevice, AudioRoutingManager, NetworkInformation, Globalization.
/// Every call goes through the object's real vtable slot, the way guest code makes it.
/// </summary>
public sealed class WinRtClassesSuite : IContractSuite
{
    public string Name => "winrt classes";

    private const int SOk = 0;
    private const long ENoInterface = 0x80004002;
    private const long EBounds = 0x8000000B;
    private const long EInvalidArg = 0x80070057;
    private const long EAccessDenied = 0x80070005;
    private const long FileNotFound = 0x80070002;
    private const long TypeMismatch = 0x80028CA0;

    private Harness _h = null!;

    private ArmEmulator E => _h.E;

    public void Run(Harness h)
    {
        _h = h;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);

        // Settings persist beside the sandbox, which the runner clears; start them clean too.
        File.Delete(h.Sandbox.TrimEnd('\\', '/') + ".settings.json");

        // ---- the machinery ----
        Check("pinterface IID: IVector<String>", WinRtRuntime.ParameterizedIid("pinterface({913337e9-11a1-4345-a3a2-4e7f956e222d};string)") == new Guid("98b9acc1-4b56-532e-ac73-03d5291cca90"));
        Check("pinterface IID: IIterable<String>", WinRtRuntime.ParameterizedIid("pinterface({faa585ea-6214-4217-afda-7f46de5869b3};string)") == new Guid("e2fcc7c1-3bfc-5a0b-b2b0-72e769d1cb7e"));
        Check("pinterface IID: IReference<Int32>", WinRtRuntime.ParameterizedIid("pinterface({61c17706-2d65-11e0-9ae8-d48564015472};i4)") == new Guid("548cefbd-bc8a-5fa0-8df2-957440fc8bf4"));
        Check("no slot drift (every implemented member is the member the metadata puts there)", E.WinRt.SlotDrift.Count == 0, string.Join("; ", E.WinRt.SlotDrift.Take(5)));

        string packageRoot = Path.GetDirectoryName(h.Image)!;

        // The expected identity comes from the package's own manifest, so the suite holds for any title.
        System.Xml.Linq.XDocument manifest = System.Xml.Linq.XDocument.Load(Path.Combine(packageRoot, "WMAppManifest.xml"));
        string Attribute(string name) => manifest.Descendants().First(e => e.Name.LocalName == "App").Attributes().First(a => a.Name.LocalName == name).Value;
        string productId = Attribute("ProductID").Trim('{', '}').ToLowerInvariant();
        Version manifestVersion = Version.Parse(Attribute("Version"));
        string defaultLanguage = manifest.Descendants().First(e => e.Name.LocalName == "DefaultLanguage").Attribute("code")!.Value;

        // ---- Package ----
        long package = Static("Windows.ApplicationModel.Package", "Windows.ApplicationModel.IPackageStatics", "get_Current");
        Check("Package.Current is an object", package != 0);
        long installed = Get(package, "Windows.ApplicationModel.IPackage", "get_InstalledLocation");
        long installedItem = QI(installed, "Windows.Storage.IStorageItem");
        string installPath = Str(Get(installedItem, "Windows.Storage.IStorageItem", "get_Path"));
        Check("InstalledLocation.Path is the WP8 install folder", installPath == @"C:\Data\Programs\{" + productId.ToUpperInvariant() + @"}\Install", installPath);
        Check("InstalledLocation.Name is Install", Str(Get(installedItem, "Windows.Storage.IStorageItem", "get_Name")) == "Install");
        long id = Get(package, "Windows.ApplicationModel.IPackage", "get_Id");
        Check("Package.Id.Name is the product id", Str(Get(id, "Windows.ApplicationModel.IPackageId", "get_Name")) == productId);
        long version = Out(8);
        Invoke(id, Slot("Windows.ApplicationModel.IPackageId", "get_Version"), version);
        Check("Package.Id.Version is the manifest's", h.U16(version) == manifestVersion.Major && h.U16(version + 2) == manifestVersion.Minor && h.U16(version + 4) == manifestVersion.Build, $"{h.U16(version)}.{h.U16(version + 2)}.{h.U16(version + 4)}.{h.U16(version + 6)}");
        Check("Package.Id.Publisher is the manifest's", Str(Get(id, "Windows.ApplicationModel.IPackageId", "get_Publisher")) == Attribute("Publisher"));
        Check("a StorageFolder is not an IStorageFile (QueryInterface says no)", QIResult(installed, "Windows.Storage.IStorageFile") == ENoInterface);
        long className = Out(4);
        Invoke(installed, 4, className);
        Check("GetRuntimeClassName names the class", Str(E.ReadUInt32(className)) == "Windows.Storage.StorageFolder");

        // ---- StorageFolder.GetFileAsync on the package ----
        string assetFull = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .First(f => Path.GetDirectoryName(f) != packageRoot && new FileInfo(f).Length is > 100 and < 2_000_000);
        string assetRelative = Path.GetRelativePath(packageRoot, assetFull);
        string assetFolderName = assetRelative.Split(Path.DirectorySeparatorChar)[0];
        byte[] assetBytes = File.ReadAllBytes(assetFull);

        long op = Call(installed, "Windows.Storage.IStorageFolder", "GetFileAsync", HS(assetRelative));
        Check("GetFileAsync completes", Status(op) == 1, $"status {Status(op)}");
        long file = Results(op);
        long fileItem = QI(file, "Windows.Storage.IStorageItem");
        Check("the StorageFile's Name is the file's", Str(Get(fileItem, "Windows.Storage.IStorageItem", "get_Name")) == Path.GetFileName(assetFull));
        Check("the StorageFile's Path is under the install folder", Str(Get(fileItem, "Windows.Storage.IStorageItem", "get_Path")) == installPath + "\\" + assetRelative);
        Check("FileType is the extension", Str(Get(file, "Windows.Storage.IStorageFile", "get_FileType")) == Path.GetExtension(assetFull));
        long props = Results(Call(fileItem, "Windows.Storage.IStorageItem", "GetBasicPropertiesAsync"));
        long size = Out(8);
        Invoke(props, 6, size);
        Check("BasicProperties.Size is the file's size", (long)BitConverter.ToUInt64(E.ReadMemory(size, 8)) == assetBytes.Length);
        long mixedCase = Results(Call(installed, "Windows.Storage.IStorageFolder", "GetFileAsync", HS(assetRelative.ToUpperInvariant())));
        Check("a package path in the wrong case is still found", mixedCase != 0);

        long missing = Call(installed, "Windows.Storage.IStorageFolder", "GetFileAsync", HS("no-such-file.bin"));
        Check("a missing file: the operation ends in Error", Status(missing) == 3);
        long errorCode = Out(4);
        Invoke(QI(missing, "Windows.Foundation.IAsyncInfo"), Slot("Windows.Foundation.IAsyncInfo", "get_ErrorCode"), errorCode);
        Check("a missing file: ErrorCode is ERROR_FILE_NOT_FOUND", E.ReadUInt32(errorCode) == FileNotFound, $"0x{E.ReadUInt32(errorCode):X8}");
        Check("a missing file: GetResults fails with it", Invoke(missing, 8, Out(4)) == FileNotFound);
        Check("a folder asked for as a file is refused", Status(Call(installed, "Windows.Storage.IStorageFolder", "GetFileAsync", HS(assetFolderName))) == 3);
        long assetsFolder = Results(Call(installed, "Windows.Storage.IStorageFolder", "GetFolderAsync", HS(assetFolderName)));
        Check("GetFolderAsync finds a package folder", assetsFolder != 0);
        long listing = Results(Call(installed, "Windows.Storage.IStorageFolder", "GetFilesAsync"));
        uint listed = U32(Get(listing, "Windows.Foundation.Collections.IVectorView`1", "get_Size"));
        int expectedFiles = Directory.EnumerateFiles(packageRoot).Count();
        Check("GetFilesAsync lists the folder's files", listed == expectedFiles, $"{listed} vs {expectedFiles}");

        // ---- reading: FileIO.ReadBufferAsync, IBufferByteAccess ----
        long buffer = Results(Call(Static("Windows.Storage.FileIO", "Windows.Storage.IFileIOStatics"), "Windows.Storage.IFileIOStatics", "ReadBufferAsync", file));
        Check("ReadBufferAsync: IBuffer.Length is the file size", U32(Get(buffer, "Windows.Storage.Streams.IBuffer", "get_Length")) == assetBytes.Length);
        long access = QI(buffer, new Guid("905a0fef-bc53-11df-8c49-001e4fc686da"));
        long data = Out(4);
        Invoke(access, 3, data);
        Check("IBufferByteAccess::Buffer gives the real bytes", E.ReadMemory(E.ReadUInt32(data), assetBytes.Length).SequenceEqual(assetBytes));

        // ---- reading: OpenReadAsync + DataReader ----
        long stream = Results(Call(QI(file, "Windows.Storage.Streams.IRandomAccessStreamReference"), "Windows.Storage.Streams.IRandomAccessStreamReference", "OpenReadAsync"));
        long ras = QI(stream, "Windows.Storage.Streams.IRandomAccessStream");
        Check("the stream's Size is the file's", (long)U64(Get(ras, "Windows.Storage.Streams.IRandomAccessStream", "get_Size")) == assetBytes.Length);
        long readerFactory = Factory("Windows.Storage.Streams.DataReader", "Windows.Storage.Streams.IDataReaderFactory");
        long reader = Ret(readerFactory, Slot("Windows.Storage.Streams.IDataReaderFactory", "CreateDataReader"), QI(stream, "Windows.Storage.Streams.IInputStream"));
        long load = Call(reader, "Windows.Storage.Streams.IDataReader", "LoadAsync", (long)assetBytes.Length);
        Check("DataReader.LoadAsync loads the whole file", U32At(load) == assetBytes.Length);
        long first = Out(4);
        Invoke(reader, Slot("Windows.Storage.Streams.IDataReader", "ReadUInt32"), first);
        Check("ReadUInt32 reads little-endian by default", E.ReadUInt32(first) == BitConverter.ToUInt32(assetBytes, 0));
        long into = h.Buffer(16);
        Invoke(reader, Slot("Windows.Storage.Streams.IDataReader", "ReadBytes"), 16, into);
        Check("ReadBytes reads the next bytes", E.ReadMemory(into, 16).SequenceEqual(assetBytes.Skip(4).Take(16)));
        Check("UnconsumedBufferLength counts down", U32(Get(reader, "Windows.Storage.Streams.IDataReader", "get_UnconsumedBufferLength")) == assetBytes.Length - 20);
        Invoke(reader, Slot("Windows.Storage.Streams.IDataReader", "ReadBuffer"), assetBytes.Length - 20, Out(4));
        Check("reading past what is loaded fails E_BOUNDS", Invoke(reader, Slot("Windows.Storage.Streams.IDataReader", "ReadByte"), Out(4)) == EBounds);

        // ---- writing: the local folder ----
        long appData = Static("Windows.Storage.ApplicationData", "Windows.Storage.IApplicationDataStatics", "get_Current");
        long local = Get(appData, "Windows.Storage.IApplicationData", "get_LocalFolder");
        string localPath = Str(Get(QI(local, "Windows.Storage.IStorageItem"), "Windows.Storage.IStorageItem", "get_Path"));
        Check("LocalFolder.Path is the WP8 local folder", localPath == E.WinRt.LocalFolderPath && localPath == WinRtRuntime.LocalFolderPathFor(productId));
        long created = Results(Call(local, "Windows.Storage.IStorageFolder", "CreateFileAsync/2", HS("save.txt"), 1));
        Check("CreateFileAsync(ReplaceExisting) in the local folder", created != 0 && File.Exists(Path.Combine(h.Sandbox, "save.txt")));
        long fileIO = Static("Windows.Storage.FileIO", "Windows.Storage.IFileIOStatics");
        long wrote = Call(fileIO, "Windows.Storage.IFileIOStatics", "WriteTextAsync", created, HS("hello, world"));
        Check("FileIO.WriteTextAsync writes the host file", Status(wrote) == 1 && File.ReadAllText(Path.Combine(h.Sandbox, "save.txt")) == "hello, world");
        Check("FileIO.ReadTextAsync reads it back", Str(Results(Call(fileIO, "Windows.Storage.IFileIOStatics", "ReadTextAsync", created))) == "hello, world");
        Call(fileIO, "Windows.Storage.IFileIOStatics", "AppendTextAsync", created, HS("\nsecond"));
        long lines = Results(Call(fileIO, "Windows.Storage.IFileIOStatics", "ReadLinesAsync", created));
        Check("ReadLinesAsync splits lines", U32(Get(lines, "Windows.Foundation.Collections.IVector`1", "get_Size")) == 2);
        Check("CreateFileAsync with FailIfExists on an existing file fails", Status(Call(local, "Windows.Storage.IStorageFolder", "CreateFileAsync", HS("save.txt"))) == 3);
        long unique = Results(Call(local, "Windows.Storage.IStorageFolder", "CreateFileAsync/2", HS("save.txt"), 0));
        Check("GenerateUniqueName makes \"save (2).txt\"", Str(Get(QI(unique, "Windows.Storage.IStorageItem"), "Windows.Storage.IStorageItem", "get_Name")) == "save (2).txt");
        long denied = Call(installed, "Windows.Storage.IStorageFolder", "CreateFileAsync/2", HS("new.bin"), 1);
        Check("the package is read-only: CreateFileAsync there is E_ACCESSDENIED", Status(denied) == 3 && Invoke(denied, 8, Out(4)) == EAccessDenied);
        long sub = Results(Call(local, "Windows.Storage.IStorageFolder", "CreateFolderAsync/2", HS("saves"), 3));
        Check("CreateFolderAsync makes a folder", sub != 0 && Directory.Exists(Path.Combine(h.Sandbox, "saves")));

        // ---- writing: a stream, DataWriter, StoreAsync ----
        long rw = Results(Call(created, "Windows.Storage.IStorageFile", "OpenAsync", 1));
        long writerFactory = Factory("Windows.Storage.Streams.DataWriter", "Windows.Storage.Streams.IDataWriterFactory");
        long writer = Ret(writerFactory, Slot("Windows.Storage.Streams.IDataWriterFactory", "CreateDataWriter"), QI(rw, "Windows.Storage.Streams.IOutputStream"));
        Invoke(writer, Slot("Windows.Storage.Streams.IDataWriter", "WriteUInt32"), 0x04030201);
        long written = Out(4);
        Invoke(writer, Slot("Windows.Storage.Streams.IDataWriter", "WriteString"), HS("abc"), written);
        Check("WriteString returns the bytes written", E.ReadUInt32(written) == 3);
        Check("StoreAsync stores the unstored bytes", U32At(Call(writer, "Windows.Storage.Streams.IDataWriter", "StoreAsync")) == 7);
        Invoke(QI(rw, "Windows.Foundation.IClosable"), 6);
        Invoke(QI(writer, "Windows.Foundation.IClosable"), 6);
        byte[] onDisk = File.ReadAllBytes(Path.Combine(h.Sandbox, "save.txt"));
        Check("the stream wrote the host file", onDisk.Take(7).SequenceEqual(new byte[] { 1, 2, 3, 4, (byte)'a', (byte)'b', (byte)'c' }), BitConverter.ToString(onDisk.Take(8).ToArray()));

        long plain = Ret(Factory("Windows.Storage.Streams.DataWriter", new Guid("00000035-0000-0000-c000-000000000046")), 6);
        h.SetD64(0, 1.5);
        Invoke(plain, Slot("Windows.Storage.Streams.IDataWriter", "WriteDouble"));
        long detached = Get(plain, "Windows.Storage.Streams.IDataWriter", "DetachBuffer");
        long fromBuffer = Ret(Factory("Windows.Storage.Streams.DataReader", "Windows.Storage.Streams.IDataReaderStatics"), 6, detached);
        long readBack = Out(8);
        Invoke(fromBuffer, Slot("Windows.Storage.Streams.IDataReader", "ReadDouble"), readBack);
        Check("DataWriter.WriteDouble (d0) round-trips through DetachBuffer and DataReader.FromBuffer", BitConverter.ToDouble(E.ReadMemory(readBack, 8)) == 1.5);

        // ---- application URIs and paths ----
        long storageFile = Static("Windows.Storage.StorageFile", "Windows.Storage.IStorageFileStatics");
        long uriFactory = Factory("Windows.Foundation.Uri", "Windows.Foundation.IUriRuntimeClassFactory");
        long appx = Ret(uriFactory, 6, HS("ms-appx:///WMAppManifest.xml"));
        long fromUri = Results(Call(storageFile, "Windows.Storage.IStorageFileStatics", "GetFileFromApplicationUriAsync", appx));
        Check("GetFileFromApplicationUriAsync(ms-appx:///) finds a package file", fromUri != 0 && Str(Get(QI(fromUri, "Windows.Storage.IStorageItem"), "Windows.Storage.IStorageItem", "get_Name")) == "WMAppManifest.xml");
        long appdata = Ret(uriFactory, 6, HS("ms-appdata:///local/save.txt"));
        Check("GetFileFromApplicationUriAsync(ms-appdata:///local/) finds a local file", Results(Call(storageFile, "Windows.Storage.IStorageFileStatics", "GetFileFromApplicationUriAsync", appdata)) != 0);
        long fromPath = Results(Call(storageFile, "Windows.Storage.IStorageFileStatics", "GetFileFromPathAsync", HS(installPath + "\\WMAppManifest.xml")));
        Check("GetFileFromPathAsync on the install path", fromPath != 0);
        long pathIO = Static("Windows.Storage.PathIO", "Windows.Storage.IPathIOStatics");
        string manifestText = Str(Results(Call(pathIO, "Windows.Storage.IPathIOStatics", "ReadTextAsync", HS(installPath + "\\WMAppManifest.xml"))));
        Check("PathIO.ReadTextAsync reads the manifest (BOM stripped)", manifestText.StartsWith("<?xml", StringComparison.Ordinal), manifestText[..Math.Min(20, manifestText.Length)]);

        // ---- LocalSettings ----
        long values = Get(Get(appData, "Windows.Storage.IApplicationData", "get_LocalSettings"), "Windows.Storage.IApplicationDataContainer", "get_Values");
        long pv = Static("Windows.Foundation.PropertyValue", "Windows.Foundation.IPropertyValueStatics");
        long boxed = Ret(pv, Slot("Windows.Foundation.IPropertyValueStatics", "CreateInt32"), 42);
        long map = QI(values, WinRtRuntime.ParameterizedIid("pinterface({3c2925fe-8519-45c1-aa79-197b6718c1c1};string;cinterface(IInspectable))"));
        Check("Values is an IMap<String, Object>", map != 0);
        long replaced = Out(4);
        Invoke(map, Slot("Windows.Foundation.Collections.IMap`2", "Insert"), HS("score"), boxed, replaced);
        Invoke(map, Slot("Windows.Foundation.Collections.IMap`2", "Insert"), HS("name"), Ret(pv, Slot("Windows.Foundation.IPropertyValueStatics", "CreateString"), HS("Red")), replaced);
        long looked = Ret(map, Slot("Windows.Foundation.Collections.IMap`2", "Lookup"), HS("SCORE"));
        Check("settings keys are case-insensitive, and the value is the box", I32(Get(looked, "Windows.Foundation.IPropertyValue", "GetInt32")) == 42);
        Check("a missing key is E_BOUNDS", Invoke(map, Slot("Windows.Foundation.Collections.IMap`2", "Lookup"), HS("absent"), Out(4)) == EBounds);
        string settingsFile = h.Sandbox.TrimEnd('\\', '/') + ".settings.json";
        Check("settings are written to disk", File.Exists(settingsFile) && File.ReadAllText(settingsFile).Contains("score"));

        // ---- a second runtime reads them back ----
        Harness second = new(h.Image, h.Sandbox);
        long secondData = second.E.WinRt.GetActivationFactory("Windows.Storage.ApplicationData")!.Value;
        _h = second;
        long secondValues = Get(Get(Get(secondData, "Windows.Storage.IApplicationDataStatics", "get_Current"), "Windows.Storage.IApplicationData", "get_LocalSettings"), "Windows.Storage.IApplicationDataContainer", "get_Values");
        long secondMap = QI(secondValues, WinRtRuntime.ParameterizedIid("pinterface({3c2925fe-8519-45c1-aa79-197b6718c1c1};string;cinterface(IInspectable))"));
        long persisted = Ret(secondMap, Slot("Windows.Foundation.Collections.IMap`2", "Lookup"), HS("score"));
        long persistedName = Ret(secondMap, Slot("Windows.Foundation.Collections.IMap`2", "Lookup"), HS("name"));
        Check("LocalSettings persist across runs", persisted != 0 && I32(Get(persisted, "Windows.Foundation.IPropertyValue", "GetInt32")) == 42 &&
                                                   Str(Get(persistedName, "Windows.Foundation.IPropertyValue", "GetString")) == "Red");
        _h = h;

        // ---- PropertyValue ----
        h.SetD64(0, 3.0);
        long three = Ret(pv, Slot("Windows.Foundation.IPropertyValueStatics", "CreateDouble"));
        Check("PropertyValue.CreateDouble reads d0, and Type is Double", U32(Get(three, "Windows.Foundation.IPropertyValue", "get_Type")) == 9);
        Check("GetInt32 on 3.0 converts", I32(Get(three, "Windows.Foundation.IPropertyValue", "GetInt32")) == 3);
        h.SetD64(0, 3.5);
        long half = Ret(pv, Slot("Windows.Foundation.IPropertyValueStatics", "CreateDouble"));
        Check("GetInt32 on 3.5 is TYPE_E_TYPEMISMATCH", Invoke(half, Slot("Windows.Foundation.IPropertyValue", "GetInt32"), Out(4)) == TypeMismatch);
        long reference = QI(boxed, WinRtRuntime.ParameterizedIid("pinterface({61c17706-2d65-11e0-9ae8-d48564015472};i4)"));
        Check("a boxed Int32 is an IReference<Int32> with the value", U32(Get(reference, "Windows.Foundation.IReference`1", "get_Value")) == 42);
        long bytesArray = h.Buffer(4);
        E.WriteMemory(bytesArray, [9, 8, 7, 6]);
        long boxedArray = Ret(pv, Slot("Windows.Foundation.IPropertyValueStatics", "CreateUInt8Array"), 4, bytesArray);
        long arrayLength = Out(4), arrayData = Out(4);
        Invoke(boxedArray, Slot("Windows.Foundation.IPropertyValue", "GetUInt8Array"), arrayLength, arrayData);
        Check("CreateUInt8Array / GetUInt8Array round-trip", E.ReadUInt32(arrayLength) == 4 && E.ReadMemory(E.ReadUInt32(arrayData), 4).SequenceEqual(new byte[] { 9, 8, 7, 6 }));

        // ---- PropertySet ----
        long set = Ret(Factory("Windows.Foundation.Collections.PropertySet", new Guid("00000035-0000-0000-c000-000000000046")), 6);
        long setMap = QI(set, WinRtRuntime.ParameterizedIid("pinterface({3c2925fe-8519-45c1-aa79-197b6718c1c1};string;cinterface(IInspectable))"));
        Invoke(setMap, Slot("Windows.Foundation.Collections.IMap`2", "Insert"), HS("k"), boxed, replaced);
        Check("PropertySet.Insert of a new key answers false", E.ReadMemory(replaced, 1)[0] == 0);
        Invoke(setMap, Slot("Windows.Foundation.Collections.IMap`2", "Insert"), HS("k"), three, replaced);
        Check("PropertySet.Insert of an existing key answers true", E.ReadMemory(replaced, 1)[0] == 1);
        Check("PropertySet.Size", U32(Get(setMap, "Windows.Foundation.Collections.IMap`2", "get_Size")) == 1);
        Check("PropertySet.Remove of a missing key is E_BOUNDS", Invoke(setMap, Slot("Windows.Foundation.Collections.IMap`2", "Remove"), HS("nope")) == EBounds);
        Check("PropertySet keys are case-sensitive", Invoke(setMap, Slot("Windows.Foundation.Collections.IMap`2", "Lookup"), HS("K"), Out(4)) == EBounds);

        // ---- Uri ----
        long uri = Ret(uriFactory, 6, HS("http://www.example.com:8080/a/b.txt?x=1&y=two%20words#frag"));
        Check("Uri.Host", Str(Get(uri, "Windows.Foundation.IUriRuntimeClass", "get_Host")) == "www.example.com");
        Check("Uri.Domain", Str(Get(uri, "Windows.Foundation.IUriRuntimeClass", "get_Domain")) == "example.com");
        Check("Uri.Port", U32(Get(uri, "Windows.Foundation.IUriRuntimeClass", "get_Port")) == 8080);
        Check("Uri.Path", Str(Get(uri, "Windows.Foundation.IUriRuntimeClass", "get_Path")) == "/a/b.txt");
        Check("Uri.Extension", Str(Get(uri, "Windows.Foundation.IUriRuntimeClass", "get_Extension")) == ".txt");
        Check("Uri.Query", Str(Get(uri, "Windows.Foundation.IUriRuntimeClass", "get_Query")) == "?x=1&y=two%20words");
        Check("Uri.Fragment", Str(Get(uri, "Windows.Foundation.IUriRuntimeClass", "get_Fragment")) == "#frag");
        long parsed = Get(uri, "Windows.Foundation.IUriRuntimeClass", "get_QueryParsed");
        Check("QueryParsed.GetFirstValueByName unescapes", Str(Ret(parsed, Slot("Windows.Foundation.IWwwFormUrlDecoderRuntimeClass", "GetFirstValueByName"), HS("y"))) == "two words");
        Check("a relative URI is refused with E_INVALIDARG", Invoke(uriFactory, 6, HS("not a uri"), Out(4)) == EInvalidArg);
        long escape = Factory("Windows.Foundation.Uri", "Windows.Foundation.IUriEscapeStatics");
        Check("activation by IID routes to IUriEscapeStatics (EscapeComponent)", Str(Ret(escape, Slot("Windows.Foundation.IUriEscapeStatics", "EscapeComponent"), HS("a b&c"))) == "a%20b%26c");

        // ---- async completion delivers to a real delegate ----
        // A delegate is recognised by its Invoke being image code; this one is never run.
        PeImage pe = PeImage.Load(h.Image);
        long fakeInvoke = (pe.ImageBase + pe.Sections.First(s => s.Name.StartsWith(".text", StringComparison.Ordinal)).VirtualAddress + 0x10) | 1;
        long fakeVtable = h.Buffer(16);
        E.WriteUInt32(fakeVtable + 12, (uint)fakeInvoke);
        long fakeDelegate = h.Buffer(8);
        E.WriteUInt32(fakeDelegate, (uint)fakeVtable);
        long pending = Call(installed, "Windows.Storage.IStorageFolder", "GetFileAsync", HS("WMAppManifest.xml"));
        Invoke(pending, Slot("Windows.Foundation.IAsyncOperation`1", "put_Completed"), fakeDelegate);
        Check("put_Completed on a finished operation invokes the handler at once (handler, operation, Completed)",
            (E.ReadRegister(78) & ~1L) == (fakeInvoke & ~1L) && E.ReadRegister(Harness.R1) == pending && E.ReadRegister(Harness.R2) == 1,
            $"r12=0x{E.ReadRegister(78):X} r1=0x{E.ReadRegister(Harness.R1):X} r2={E.ReadRegister(Harness.R2)}");
        Check("a second put_Completed is E_ILLEGAL_DELEGATE_ASSIGNMENT", Invoke(pending, Slot("Windows.Foundation.IAsyncOperation`1", "put_Completed"), fakeDelegate) == 0x80000018);

        // ---- Launcher ----
        string? launched = null;
        E.WinRt.UriLauncher = u =>
        {
            launched = u;
            return true;
        };
        long launcher = Static("Windows.System.Launcher", "Windows.System.ILauncherStatics");
        long launchOp = Call(launcher, "Windows.System.ILauncherStatics", "LaunchUriAsync", Ret(uriFactory, 6, HS("https://example.com/")));
        Check("LaunchUriAsync goes to the host launcher and answers its result", launched == "https://example.com/" && E.ReadMemory(ResultBytes(launchOp, 1), 1)[0] == 1);

        // ---- ThreadPoolTimer ----
        long timers = Static("Windows.System.Threading.ThreadPoolTimer", "Windows.System.Threading.IThreadPoolTimerStatics");
        long timerOut = Out(4);
        // CreateTimer(handler r1, TimeSpan r2:r3, out on the stack)
        Invoke(timers, Slot("Windows.System.Threading.IThreadPoolTimerStatics", "CreateTimer"), fakeDelegate, 500_000, 0, timerOut);
        long timer = E.ReadUInt32(timerOut);
        Check("CreateTimer answers a timer with its delay", timer != 0 && (long)U64(Get(timer, "Windows.System.Threading.IThreadPoolTimer", "get_Delay")) == 500_000);
        int pendingBefore = E.PendingDeferredCalls;
        QueueTimers();
        Check("a timer is not due before its delay", E.PendingDeferredCalls == pendingBefore);
        typeof(WinRtRuntime).GetProperty("ProcessEventsCalls")!.SetValue(E.WinRt, E.WinRt.ProcessEventsCalls + 3);
        QueueTimers();
        Check("a due timer is queued to run its handler", E.PendingDeferredCalls == pendingBefore + 1);
        QueueTimers();
        Check("a one-shot timer fires once", E.PendingDeferredCalls == pendingBefore + 1);

        // ---- VibrationDevice ----
        TimeSpan? vibrated = null;
        E.WinRt.VibrateHost = d => vibrated = d;
        long vibration = E.WinRt.GetActivationFactory("Windows.Phone.Devices.Notification.VibrationDevice")!.Value;
        long device = Ret(vibration, 6);
        Check("VibrationDevice.GetDefault answers a device", device != 0);
        Invoke(device, 6, 0, 5_000_000, 0);   // Vibrate(TimeSpan 0.5 s) in r2:r3
        Check("Vibrate drives the host motor for the duration", vibrated == TimeSpan.FromMilliseconds(500));
        Check("Vibrate longer than 5 s is E_INVALIDARG", Invoke(device, 6, 0, 60_000_000, 0) == EInvalidArg);

        // ---- AudioRoutingManager (Windows.Phone.Media.Devices) ----
        // IAudioRoutingManager slots: 6 GetAudioEndpoint, 7 SetAudioEndpoint, 8 add_AudioEndpointChanged,
        // 9 remove_AudioEndpointChanged, 10 get_AvailableAudioEndpoints. Statics: 6 GetDefault.
        long routingFactory = E.WinRt.GetActivationFactory("Windows.Phone.Media.Devices.AudioRoutingManager", new Guid("977fb2a4-5590-4a6f-adde-6a3d0ad58250"))!.Value;
        long routing = Ret(routingFactory, 6);
        Check("AudioRoutingManager.GetDefault answers the manager", routing != 0);
        Check("...the same one every time", Ret(routingFactory, 6) == routing);
        Check("...it is an IAudioRoutingManager", QI(routing, new Guid("79340d20-71cc-4526-9f29-fc8d2486418b")) != 0);
        Check("...and not a stand-in", !E.WinRt.ImprovisedClasses.Contains("Windows.Phone.Media.Devices.AudioRoutingManager"));
        Check("GetAudioEndpoint: the loudspeaker (Speakerphone, 2)", Ret(routing, 6) == 2);
        Check("AvailableAudioEndpoints: Earpiece | Speakerphone (3)", Ret(routing, 10) == 3);

        // A delegate whose Invoke is code in the image (what LooksLikeDelegate asks for); it is only
        // queued here, never run.
        LoadedModule main = E.Modules[0];
        long invoked = (main.ImageBase + main.Image.EntryPointRva) | 1;
        long handlerVtable = _h.Buffer(16), handler = _h.Buffer(8);
        for (int i = 0; i < 4; i++)
        {
            E.WriteUInt32(handlerVtable + (i * 4), (uint)invoked);
        }

        E.WriteUInt32(handler, (uint)handlerVtable);
        long tokenOut = Out(8);
        Check("add_AudioEndpointChanged: S_OK and a token", Invoke(routing, 8, handler, tokenOut) == SOk && E.ReadUInt32(tokenOut) != 0);
        long token = E.ReadUInt32(tokenOut);
        int queuedBefore = E.PendingDeferredCalls;
        Check("SetAudioEndpoint(Earpiece) succeeds", Invoke(routing, 7, 1) == SOk && Ret(routing, 6) == 1);
        Check("...and the change is raised later, not on the caller's stack", E.PendingDeferredCalls == queuedBefore + 1);
        Check("SetAudioEndpoint to the endpoint already in use raises nothing", Invoke(routing, 7, 1) == SOk && E.PendingDeferredCalls == queuedBefore + 1);
        Check("SetAudioEndpoint(Bluetooth): nothing is paired, E_INVALIDARG", Invoke(routing, 7, 3) == EInvalidArg && Ret(routing, 6) == 1);
        Check("SetAudioEndpoint(WiredHeadset): nothing is plugged in, E_INVALIDARG", Invoke(routing, 7, 4) == EInvalidArg);
        Check("remove_AudioEndpointChanged(token) = S_OK", Invoke(routing, 9, 0, token, 0) == SOk);   // an int64 starts at an even word: r2:r3
        Check("SetAudioEndpoint(Default) routes to the loudspeaker and, unsubscribed, raises nothing",
            Invoke(routing, 7, 0) == SOk && Ret(routing, 6) == 2 && E.PendingDeferredCalls == queuedBefore + 1);

        // ---- NetworkInformation, CoreApplication, Globalization ----
        long network = Static("Windows.Networking.Connectivity.NetworkInformation", "Windows.Networking.Connectivity.INetworkInformationStatics");
        Check("NetworkInformation: no internet profile (this runtime is offline)", Get(network, "Windows.Networking.Connectivity.INetworkInformationStatics", "GetInternetConnectionProfile") == 0);
        long core = Static("Windows.ApplicationModel.Core.CoreApplication", "Windows.ApplicationModel.Core.ICoreApplication");
        Check("CoreApplication.Properties is a property set", Get(core, "Windows.ApplicationModel.Core.ICoreApplication", "get_Properties") != 0);
        Check("CoreApplication.GetCurrentView is the view", Get(core, "Windows.ApplicationModel.Core.ICoreApplication", "GetCurrentView") != 0);
        long languages = Static("Windows.Globalization.ApplicationLanguages", "Windows.Globalization.IApplicationLanguagesStatics");
        long manifestLanguages = Get(languages, "Windows.Globalization.IApplicationLanguagesStatics", "get_ManifestLanguages");
        Check("ApplicationLanguages.ManifestLanguages is the manifest's", Str(Ret(manifestLanguages, 6, 0)) == defaultLanguage);
        long launchArgs = E.WinRt.CreateLaunchActivatedEventArgs();
        Check("launch activation args: Kind is Launch", U32(Get(QI(launchArgs, "Windows.ApplicationModel.Activation.IActivatedEventArgs"), "Windows.ApplicationModel.Activation.IActivatedEventArgs", "get_Kind")) == 0);
    }

    // -------------------------------------------------------------------------------------------
    // Calling WinRT the way guest code does: through the vtable slot, with the trap's handler.
    // -------------------------------------------------------------------------------------------

    private static readonly FieldInfo Traps = typeof(ArmEmulator).GetField("_traps", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Calls slot <paramref name="slot"/> of the interface pointer: r0 = this, then the words; answers r0.</summary>
    private long Invoke(long self, int slot, params long[] words)
    {
        long function = E.ReadUInt32(E.ReadUInt32(self) + (slot * 4)) & ~1L;
        object trap = ((IDictionary)Traps.GetValue(E)!)[function] ?? throw new InvalidOperationException($"slot {slot} of 0x{self:X} is not a trap");
        Action handler = (Action)trap.GetType().GetProperty("Handler")!.GetValue(trap)!;

        int[] registers = [Harness.R0, Harness.R1, Harness.R2, Harness.R3];
        E.WriteRegister(Harness.R0, self);
        for (int i = 0; i < words.Length && i < 3; i++)
        {
            E.WriteRegister(registers[i + 1], words[i]);
        }

        for (int i = 3; i < words.Length; i++)
        {
            E.WriteUInt32(_h.Stack + ((i - 3) * 4), (uint)words[i]);
        }

        E.WriteRegister(Harness.SP, _h.Stack);
        E.WriteRegister(10, 0x1001);   // lr: somewhere to "return" to
        handler();
        return E.ReadRegister(Harness.R0) & 0xFFFFFFFFL;
    }

    private static int Slot(string iface, string member)
    {
        int n = member.Contains('/') ? int.Parse(member[(member.IndexOf('/') + 1)..]) - 1 : 0;
        return WinRtSystemInterfaces.Slot(iface, member.Split('/')[0]) + n;
    }

    /// <summary>A member with arguments then an out-pointer: the object it writes.</summary>
    private long Ret(long self, int slot, params long[] words)
    {
        long result = Out(8);
        long hr = Invoke(self, slot, [.. words, result]);
        return hr == SOk ? E.ReadUInt32(result) : 0;
    }

    /// <summary>A getter: the word it writes.</summary>
    private long Get(long self, string iface, string member) => Ret(self, Slot(iface, member));

    /// <summary>A method returning an object (usually an async operation).</summary>
    private long Call(long self, string iface, string member, params long[] words) => Ret(self, Slot(iface, member), words);

    private long Out(int size) => _h.Buffer(Math.Max(size, 8));

    private long Factory(string className, Guid iid) => E.WinRt.GetActivationFactory(className, iid)!.Value;

    private long Factory(string className, string iface) => Factory(className, WinRtSystemInterfaces.Find(iface)!.Iid);

    private long Static(string className, string iface) => Factory(className, iface);

    private long Static(string className, string iface, string getter) => Get(Factory(className, iface), iface, getter);

    private long QI(long self, string iface) => QI(self, WinRtSystemInterfaces.Find(iface)!.Iid);

    private long QI(long self, Guid iid)
    {
        long iidPointer = _h.Buffer(16);
        E.WriteMemory(iidPointer, iid.ToByteArray());
        long result = Out(4);
        return Invoke(self, 0, iidPointer, result) == SOk ? E.ReadUInt32(result) : 0;
    }

    private long QIResult(long self, string iface)
    {
        long iidPointer = _h.Buffer(16);
        E.WriteMemory(iidPointer, WinRtSystemInterfaces.Find(iface)!.Iid.ToByteArray());
        return Invoke(self, 0, iidPointer, Out(4));
    }

    /// <summary>IAsyncInfo.Status of an operation.</summary>
    private long Status(long operation) => operation == 0 ? -1 : Get(QI(operation, "Windows.Foundation.IAsyncInfo"), "Windows.Foundation.IAsyncInfo", "get_Status");

    /// <summary>IAsyncOperation&lt;T&gt;.GetResults (slot 8) for an object result.</summary>
    private long Results(long operation) => operation == 0 ? 0 : Ret(operation, 8);

    private long ResultBytes(long operation, int size)
    {
        long result = Out(size);
        Invoke(operation, 8, result);
        return result;
    }

    private uint U32At(long operation) => E.ReadUInt32(ResultBytes(operation, 4));

    private static uint U32(long word) => (uint)word;

    private static int I32(long word) => (int)word;

    private ulong U64(long low) => (ulong)low;   // getters here return through an 8-byte out; Ret reads the low word

    private long HS(string text)
    {
        long result = Out(4);
        _h.Call("WindowsCreateString", _h.Wide(text), text.Length, result);
        return E.ReadUInt32(result);
    }

    private string Str(long hstring)
    {
        if (hstring == 0)
        {
            return string.Empty;
        }

        long length = Out(4);
        long chars = _h.Call("WindowsGetStringRawBuffer", hstring, length);
        return Encoding.Unicode.GetString(E.ReadMemory(chars, (int)E.ReadUInt32(length) * 2));
    }

    private void QueueTimers() => typeof(WinRtRuntime).GetMethod("QueueDueTimers", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(E.WinRt, null);
}

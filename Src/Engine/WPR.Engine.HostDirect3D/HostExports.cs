using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WPR.HostDirect3D
{
    /// <summary>
    /// The native exports a managed WP8 title reaches through a helper component (SharpDX.WP8's
    /// <c>Interop</c>), as function pointers: Direct3D 11 and DXGI creation, the Win32 file calls
    /// SharpDX's NativeFileStream makes, and the XAudio2/COM entry points. Each is handed out as a
    /// jump below 2 GB, because the helper returns them as a 32-bit int (<see cref="NativeCode"/>).
    /// </summary>
    public static unsafe class HostExports
    {
        private static readonly Lazy<Dictionary<string, int>> Addresses = new(Build);

        /// <summary>The address of export <paramref name="name"/> as the int the helper returns.</summary>
        public static int Address(string name)
        {
            if (Addresses.Value.TryGetValue(name, out int address))
            {
                return address;
            }

            HostLog.WriteOnce("export " + name, $"export {name} is not provided");
            return 0;
        }

        /// <summary>At the end of a game run: every XAudio2 engine the title left alive stops.</summary>
        public static void ReleaseAudio() => HostXAudio2.DisposeAll();

        private static Dictionary<string, int> Build()
        {
            var targets = new (string Name, IntPtr Target)[]
            {
                ("D3D11CreateDevice", (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, uint, int*, uint, uint, IntPtr*, int*, IntPtr*, int>)&D3D11CreateDevice),
                ("CreateDXGIFactory1", (IntPtr)(delegate* unmanaged[Stdcall]<Guid*, IntPtr*, int>)&CreateDXGIFactory1),
                ("CreateFile2", (IntPtr)(delegate* unmanaged[Stdcall]<char*, uint, uint, uint, IntPtr, IntPtr>)&HostFiles.CreateFile2),
                ("ReadFile", (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, int, int*, IntPtr, int>)&HostFiles.ReadFile),
                ("WriteFile", (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, int, int*, IntPtr, int>)&HostFiles.WriteFile),
                ("FlushFileBuffers", (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&HostFiles.FlushFileBuffers),
                ("SetFilePointerEx", (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, long, long*, int, int>)&HostFiles.SetFilePointerEx),
                ("SetEndOfFile", (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&HostFiles.SetEndOfFile),
                ("GetFileInformationByHandleEx", (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, byte*, int, int>)&HostFiles.GetFileInformationByHandleEx),
                ("CloseHandle", (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&HostFiles.CloseHandle),
                ("GetFileAttributesExW", (IntPtr)(delegate* unmanaged[Stdcall]<char*, int, byte*, int>)&HostFiles.GetFileAttributesExW),
                ("FormatMessageW", (IntPtr)(delegate* unmanaged[Stdcall]<uint, IntPtr, uint, uint, char*, uint, IntPtr, uint>)&FormatMessageW),
                ("LoadPackagedLibrary", (IntPtr)(delegate* unmanaged[Stdcall]<char*, uint, IntPtr>)&LoadPackagedLibrary),
                ("GetProcAddress", (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr>)&GetProcAddress),
                ("CoCreateInstanceFromApp", (IntPtr)(delegate* unmanaged[Stdcall]<Guid*, IntPtr, uint, IntPtr, uint, byte*, int>)&CoCreateInstanceFromApp),
                ("XAudio2Create", (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr*, uint, uint, int>)&XAudio2Create),
                ("CreateFX", (IntPtr)(delegate* unmanaged[Stdcall]<Guid*, IntPtr*, IntPtr, uint, int>)&CreateFX),
                ("CreateAudioReverb", (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr*, int>)&CreateAudioEffect),
                ("CreateAudioVolumeMeter", (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr*, int>)&CreateAudioEffect),
                ("X3DAudioInitialize", (IntPtr)(delegate* unmanaged[Stdcall]<uint, float, byte*, int>)&X3DAudioInitialize),
                ("X3DAudioCalculate", (IntPtr)(delegate* unmanaged[Stdcall]<byte*, IntPtr, IntPtr, uint, IntPtr, void>)&X3DAudioCalculate),
            };

            var batch = new NativeCode.Batch();
            foreach (var (_, target) in targets)
            {
                batch.Jump(target);
            }

            var notProvided = (IntPtr)(delegate* unmanaged[Stdcall]<int>)&NotProvided;
            for (int i = 0; i < NotProvidedNames.Length; i++)
            {
                batch.Reporter(notProvided, NotProvidedTag | i);
            }

            IntPtr[] thunks = batch.Commit();
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < targets.Length; i++)
            {
                result[targets[i].Name] = (int)(long)thunks[i];
            }

            for (int i = 0; i < NotProvidedNames.Length; i++)
            {
                result[NotProvidedNames[i]] = (int)(long)thunks[targets.Length + i];
            }

            return result;
        }

        private const int NotProvidedTag = 0x40000000;

        /// <summary>
        /// Media Foundation, which SharpDX.MediaFoundation reaches through the same component. No
        /// installed title has been seen calling it; each answers E_NOTIMPL and is logged by name,
        /// so the first one that does names itself.
        /// </summary>
        private static readonly string[] NotProvidedNames =
        [
            "MFCreate2DMediaBuffer", "MFCreateAlignedMemoryBuffer", "MFCreateAsyncResult", "MFCreateAttributes",
            "MFCreateCollection", "MFCreateDXGIDeviceManager", "MFCreateDXGISurfaceBuffer", "MFCreateMediaBufferFromMediaType",
            "MFCreateMediaBufferWrapper", "MFCreateMediaTypeFromProperties", "MFCreateMemoryBuffer", "MFCreateMFByteStreamOnStreamEx",
            "MFCreatePropertiesFromMediaType", "MFCreateSample", "MFCreateStreamOnMFByteStreamEx", "MFCreateVideoSampleAllocatorEx",
            "MFDeserializeAttributesFromStream", "MFGetAttributesAsBlob", "MFGetAttributesAsBlobSize", "MFInvokeCallback",
            "MFllMulDiv", "MFLockDXGIDeviceManager", "MFLockPlatform", "MFSerializeAttributesToStream", "MFShutdown",
            "MFStartup", "MFUnlockDXGIDeviceManager", "MFUnlockPlatform",
        ];

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int NotProvided()
        {
            int index = *NativeCode.LastSlot & ~NotProvidedTag;
            string name = (uint)index < (uint)NotProvidedNames.Length ? NotProvidedNames[index] : "export #" + index;
            HostLog.WriteOnce("notprovided " + name, $"{name}: not provided (E_NOTIMPL)");
            return unchecked((int)0x80004001);
        }

        /// <summary>D3D11CreateDevice: a 9_3 device, whatever was asked, on the host's recorder.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, int* levels, uint levelCount, uint sdk, IntPtr* device, int* level, IntPtr* context)
        {
            var created = new HostDevice();
            HostLog.Write($"D3D11CreateDevice (driver {driverType}, flags 0x{flags:X}) -> feature level 9_3");
            if (level != null)
            {
                *level = HostDevice.FeatureLevel;
            }

            HostObject.Out(context, created.Context);
            if (device != null)
            {
                *device = created.Pointer;
            }

            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateDXGIFactory1(Guid* iid, IntPtr* factory)
        {
            var created = new HostFactory(new HostAdapter());
            int hr = HostAdapter.Query(created, iid, factory);
            created.ReleaseReference();
            return hr;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint FormatMessageW(uint flags, IntPtr source, uint message, uint language, char* buffer, uint size, IntPtr arguments) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static IntPtr LoadPackagedLibrary(char* name, uint reserved)
        {
            HostLog.WriteOnce("lib" + new string(name), $"LoadPackagedLibrary({new string(name)}): no native libraries are loaded");
            return IntPtr.Zero;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static IntPtr GetProcAddress(IntPtr module, byte* name) => IntPtr.Zero;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CoCreateInstanceFromApp(Guid* clsid, IntPtr outer, uint context, IntPtr reserved, uint count, byte* results)
        {
            HostLog.WriteOnce("cocreate" + *clsid, $"CoCreateInstanceFromApp({{{*clsid}}}): class not provided");
            return unchecked((int)0x80040154); // REGDB_E_CLASSNOTREG
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int XAudio2Create(IntPtr* xaudio, uint flags, uint processor) => HostXAudio2.Create(xaudio);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateFX(Guid* clsid, IntPtr* effect, IntPtr data, uint size)
        {
            *effect = IntPtr.Zero;
            return unchecked((int)0x80004001);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateAudioEffect(IntPtr* effect)
        {
            *effect = IntPtr.Zero;
            return unchecked((int)0x80004001);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int X3DAudioInitialize(uint speakers, float speedOfSound, byte* handle)
        {
            new Span<byte>(handle, 20).Clear();
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void X3DAudioCalculate(byte* handle, IntPtr listener, IntPtr emitter, uint flags, IntPtr settings)
        {
        }
    }

    /// <summary>
    /// The Win32 file calls behind SharpDX's NativeFileStream, over host files. A relative path is
    /// the package's (WP8's current directory was the install folder), and Windows separators are
    /// turned into this platform's.
    /// </summary>
    public static unsafe class HostFiles
    {
        private static readonly Dictionary<long, FileStream> Open = new();
        private static long _next = 0x1000;

        private static FileStream? Get(IntPtr handle)
        {
            lock (Open)
            {
                return Open.GetValueOrDefault((long)handle);
            }
        }

        internal static string Resolve(string path)
        {
            string normalised = Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', Path.DirectorySeparatorChar);
            return Path.IsPathRooted(normalised) ? normalised : Path.Combine(WPR.WinRT.WinRtHost.InstalledPath ?? AppContext.BaseDirectory, normalised);
        }

        private static int Fail(int error)
        {
            Marshal.SetLastPInvokeError(error);
            Marshal.SetLastSystemError(error);
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        internal static IntPtr CreateFile2(char* name, uint access, uint share, uint disposition, IntPtr extended)
        {
            string path = Resolve(new string(name));
            FileMode mode = disposition switch
            {
                1 => FileMode.CreateNew,
                2 => FileMode.Create,
                4 => FileMode.OpenOrCreate,
                5 => FileMode.Truncate,
                _ => FileMode.Open,
            };
            FileAccess fileAccess = (access & 0x40000000) != 0
                ? ((access & 0x80000000) != 0 ? FileAccess.ReadWrite : FileAccess.Write)
                : FileAccess.Read;
            try
            {
                var stream = new FileStream(path, mode, fileAccess, FileShare.ReadWrite | FileShare.Delete);
                lock (Open)
                {
                    long handle = _next++;
                    Open[handle] = stream;
                    return (IntPtr)handle;
                }
            }
            catch (Exception ex)
            {
                int error = ex is FileNotFoundException ? 2 : ex is DirectoryNotFoundException ? 3 : ex is UnauthorizedAccessException ? 5 : ex is IOException ? 32 : 31;
                HostLog.WriteOnce("open " + path, $"CreateFile2({path}) failed: {ex.GetType().Name}");
                Fail(error);
                return new IntPtr(-1);
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        internal static int ReadFile(IntPtr handle, byte* buffer, int count, int* read, IntPtr overlapped)
        {
            if (Get(handle) is not { } stream)
            {
                return Fail(6);
            }

            int n = stream.Read(new Span<byte>(buffer, count));
            if (read != null)
            {
                *read = n;
            }

            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        internal static int WriteFile(IntPtr handle, byte* buffer, int count, int* written, IntPtr overlapped)
        {
            if (Get(handle) is not { } stream)
            {
                return Fail(6);
            }

            stream.Write(new ReadOnlySpan<byte>(buffer, count));
            if (written != null)
            {
                *written = count;
            }

            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        internal static int FlushFileBuffers(IntPtr handle)
        {
            Get(handle)?.Flush();
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        internal static int SetFilePointerEx(IntPtr handle, long distance, long* position, int method)
        {
            if (Get(handle) is not { } stream)
            {
                return Fail(6);
            }

            long at = stream.Seek(distance, method switch { 1 => SeekOrigin.Current, 2 => SeekOrigin.End, _ => SeekOrigin.Begin });
            if (position != null)
            {
                *position = at;
            }

            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        internal static int SetEndOfFile(IntPtr handle)
        {
            if (Get(handle) is not { } stream)
            {
                return Fail(6);
            }

            stream.SetLength(stream.Position);
            return 1;
        }

        /// <summary>FileBasicInfo (0) and FileStandardInfo (1), what NativeFileStream asks for its length.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        internal static int GetFileInformationByHandleEx(IntPtr handle, int kind, byte* info, int size)
        {
            if (Get(handle) is not { } stream)
            {
                return Fail(6);
            }

            new Span<byte>(info, size).Clear();
            switch (kind)
            {
                case 1 when size >= 22:
                    *(long*)info = stream.Length;
                    *(long*)(info + 8) = stream.Length;
                    *(int*)(info + 16) = 1;
                    return 1;
                case 0 when size >= 36:
                    *(uint*)(info + 32) = 0x80; // FILE_ATTRIBUTE_NORMAL
                    return 1;
                default:
                    return Fail(87);
            }
        }

        /// <summary>GetFileExInfoStandard: WIN32_FILE_ATTRIBUTE_DATA (attributes, three FILETIMEs, size high/low).</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        internal static int GetFileAttributesExW(char* name, int level, byte* info)
        {
            string path = Resolve(new string(name));
            new Span<byte>(info, 36).Clear();
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                *(uint*)info = 0x80;
                *(long*)(info + 4) = file.CreationTimeUtc.ToFileTimeUtc();
                *(long*)(info + 12) = file.LastAccessTimeUtc.ToFileTimeUtc();
                *(long*)(info + 20) = file.LastWriteTimeUtc.ToFileTimeUtc();
                *(uint*)(info + 28) = (uint)(file.Length >> 32);
                *(uint*)(info + 32) = (uint)file.Length;
                return 1;
            }

            if (Directory.Exists(path))
            {
                *(uint*)info = 0x10;
                return 1;
            }

            return Fail(2);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        internal static int CloseHandle(IntPtr handle)
        {
            FileStream? stream;
            lock (Open)
            {
                Open.Remove((long)handle, out stream);
            }

            stream?.Dispose();
            return 1;
        }

        /// <summary>Closes whatever a title left open, at teardown.</summary>
        public static void CloseAll()
        {
            lock (Open)
            {
                foreach (FileStream stream in Open.Values)
                {
                    stream.Dispose();
                }

                Open.Clear();
            }
        }
    }
}

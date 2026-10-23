using WPR.HostDirect3D;

namespace Wp8ContractTests.Suites;

/// <summary>
/// WPR.Engine.HostDirect3D, called the way a managed WP8 title's own SharpDX calls it: through
/// the 32-bit export addresses SharpDX.WP8.Interop hands out, and COM vtable slots from there. No
/// guest code; these are host functions.
/// </summary>
public sealed unsafe class HostDirect3DSuite : IContractSuite
{
    public string Name => "host Direct3D 11 (managed SharpDX titles)";

    private static nint Slot(IntPtr obj, int slot) => (*(nint**)obj)[slot];

    private static uint Release(IntPtr obj) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, 2))(obj);

    public void Run(Harness h)
    {
        int create = HostExports.Address("D3D11CreateDevice");
        h.Check("D3D11CreateDevice is a non-zero address that fits SharpDX's int", create > 0);
        h.Check("every SharpDX.WP8 export SharpDX names has an address, Media Foundation included",
            new[] { "CreateDXGIFactory1", "CreateFile2", "ReadFile", "WriteFile", "SetFilePointerEx", "GetFileInformationByHandleEx",
                    "GetFileAttributesExW", "CloseHandle", "XAudio2Create", "MFStartup", "MFllMulDiv" }.All(n => HostExports.Address(n) > 0));

        IntPtr device = IntPtr.Zero, context = IntPtr.Zero;
        int level = 0;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, uint, int*, uint, uint, IntPtr*, int*, IntPtr*, int>)(nint)create)(
            IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &device, &level, &context);
        h.Check("D3D11CreateDevice answers a device and its immediate context at feature level 9_3",
            hr == 0 && device != IntPtr.Zero && context != IntPtr.Zero && level == 0x9300, $"hr {hr:X8}, level {level:X}");

        // A depth-stencil state made twice is the same object (Direct3D deduplicates states).
        byte* desc = stackalloc byte[52];
        new Span<byte>(desc, 52).Clear();
        ((int*)desc)[0] = 1;  // DepthEnable
        ((int*)desc)[1] = 1;  // DepthWriteMask ALL
        ((int*)desc)[2] = 4;  // LESS_EQUAL
        var createState = (delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr*, int>)Slot(device, 21);
        IntPtr first = IntPtr.Zero, second = IntPtr.Zero;
        createState(device, desc, &first);
        createState(device, desc, &second);
        h.Check("an equal depth-stencil state comes back as the same object", first != IntPtr.Zero && first == second);

        // Bound, then every reference the title holds released: the context keeps it alive, as
        // SharpDX's EAGLView relies on (it binds a state and disposes its wrapper at once).
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(context, 36))(context, first, 0);
        Release(first);
        Release(second);
        IntPtr bound = IntPtr.Zero;
        uint reference = 99;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, void>)Slot(context, 92))(context, &bound, &reference);
        byte* back = stackalloc byte[52];
        ((delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)Slot(bound, 8))(bound, back);
        h.Check("a bound state outlives the title's references and still answers GetDesc",
            bound == first && reference == 0 && ((int*)back)[2] == 4);
        Release(bound);

        // Unbinding drops the context's reference; the state can then be made afresh.
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(context, 36))(context, IntPtr.Zero, 0);
        IntPtr again = IntPtr.Zero;
        createState(device, desc, &again);
        h.Check("an unbound, released state is gone; an equal one is created anew", again != IntPtr.Zero);
        Release(again);

        Files(h);
        Release(context);
        Release(device);

        // A C# bool copied byte for byte into a constant buffer is the denormal 1.4e-45; GPUs flush
        // it to 0, so Cocos2D's "is textured" flag read false and every sprite was a white box.
        h.Check("the replay reads a denormal constant as the integer it holds",
            WPR.Wp8Native.Wp8GpuReplay.Ordered(BitConverter.Int32BitsToSingle(1)) == 1f
            && WPR.Wp8Native.Wp8GpuReplay.Ordered(-BitConverter.Int32BitsToSingle(1)) == -1f);
        h.Check("... and leaves ordinary floats alone", WPR.Wp8Native.Wp8GpuReplay.Ordered(0.25f) == 0.25f
            && WPR.Wp8Native.Wp8GpuReplay.Ordered(0f) == 0f);
    }

    private static void Files(Harness h)
    {
        string folder = Path.Combine(h.Sandbox, "host-files");
        Directory.CreateDirectory(Path.Combine(folder, "Content"));
        string previous = WPR.WinRT.WinRtHost.InstalledPath;
        WPR.WinRT.WinRtHost.InstalledPath = folder;
        try
        {
            var createFile = (delegate* unmanaged[Stdcall]<char*, uint, uint, uint, IntPtr, IntPtr>)(nint)HostExports.Address("CreateFile2");
            var write = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int, int*, IntPtr, int>)(nint)HostExports.Address("WriteFile");
            var read = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int, int*, IntPtr, int>)(nint)HostExports.Address("ReadFile");
            var seek = (delegate* unmanaged[Stdcall]<IntPtr, long, long*, int, int>)(nint)HostExports.Address("SetFilePointerEx");
            var info = (delegate* unmanaged[Stdcall]<IntPtr, int, byte*, int, int>)(nint)HostExports.Address("GetFileInformationByHandleEx");
            var close = (delegate* unmanaged[Stdcall]<IntPtr, int>)(nint)HostExports.Address("CloseHandle");

            IntPtr handle;
            fixed (char* name = "Content\\save.bin")
            {
                handle = createFile(name, 0xC0000000, 1, 2, IntPtr.Zero); // read/write, CREATE_ALWAYS
            }

            h.Check("CreateFile2 resolves a Windows-separated relative path against the package", handle != new IntPtr(-1)
                && File.Exists(Path.Combine(folder, "Content", "save.bin")));

            byte* data = stackalloc byte[5] { 1, 2, 3, 4, 5 };
            int n = 0;
            write(handle, data, 5, &n, IntPtr.Zero);
            long at = -1;
            seek(handle, 1, &at, 0);
            byte* got = stackalloc byte[8];
            int readCount = 0;
            read(handle, got, 8, &readCount, IntPtr.Zero);
            byte* standard = stackalloc byte[24];
            info(handle, 1, standard, 24);
            h.Check("WriteFile, SetFilePointerEx, ReadFile and FileStandardInfo agree",
                n == 5 && at == 1 && readCount == 4 && got[0] == 2 && got[3] == 5 && *(long*)(standard + 8) == 5,
                $"wrote {n}, at {at}, read {readCount}, size {*(long*)(standard + 8)}");
            h.Check("CloseHandle closes it", close(handle) == 1);

            fixed (char* name = "Content\\missing.wav")
            {
                handle = createFile(name, 0x80000000, 1, 3, IntPtr.Zero);
            }

            h.Check("a missing file is INVALID_HANDLE_VALUE, as SharpDX's NativeFileStream expects", handle == new IntPtr(-1));
        }
        finally
        {
            WPR.WinRT.WinRtHost.InstalledPath = previous;
        }
    }
}

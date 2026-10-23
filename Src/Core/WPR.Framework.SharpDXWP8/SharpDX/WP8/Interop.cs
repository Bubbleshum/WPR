using WPR.HostDirect3D;

namespace SharpDX.WP8
{
    /// <summary>
    /// Shim for <c>SharpDX.WP8.Interop</c> (SharpDX.WP8.winmd): the address of each native export
    /// SharpDX calls, as the int the component returned. They are WPR.Engine.HostDirect3D's
    /// implementations, placed below 2 GB so the int holds them on a 64-bit host.
    /// </summary>
    public static class Interop
    {
        // Direct3D 11 / DXGI (SharpDX.Direct3D11, SharpDX.DXGI)
        public static int D3D11CreateDevice() => HostExports.Address(nameof(D3D11CreateDevice));
        public static int CreateDXGIFactory1() => HostExports.Address(nameof(CreateDXGIFactory1));

        // Win32 (SharpDX: NativeFile, ResultDescriptor, Utilities)
        public static int CreateFile2() => HostExports.Address(nameof(CreateFile2));
        public static int ReadFile() => HostExports.Address(nameof(ReadFile));
        public static int WriteFile() => HostExports.Address(nameof(WriteFile));
        public static int FlushFileBuffers() => HostExports.Address(nameof(FlushFileBuffers));
        public static int SetFilePointerEx() => HostExports.Address(nameof(SetFilePointerEx));
        public static int SetEndOfFile() => HostExports.Address(nameof(SetEndOfFile));
        public static int GetFileInformationByHandleEx() => HostExports.Address(nameof(GetFileInformationByHandleEx));
        public static int GetFileAttributesExW() => HostExports.Address(nameof(GetFileAttributesExW));
        public static int CloseHandle() => HostExports.Address(nameof(CloseHandle));
        public static int FormatMessageW() => HostExports.Address(nameof(FormatMessageW));
        public static int LoadPackagedLibrary() => HostExports.Address(nameof(LoadPackagedLibrary));
        public static int GetProcAddress() => HostExports.Address(nameof(GetProcAddress));
        public static int CoCreateInstanceFromApp() => HostExports.Address(nameof(CoCreateInstanceFromApp));

        // XAudio2 / X3DAudio (SharpDX.XAudio2)
        public static int XAudio2Create() => HostExports.Address(nameof(XAudio2Create));
        public static int CreateFX() => HostExports.Address(nameof(CreateFX));
        public static int CreateAudioReverb() => HostExports.Address(nameof(CreateAudioReverb));
        public static int CreateAudioVolumeMeter() => HostExports.Address(nameof(CreateAudioVolumeMeter));
        public static int X3DAudioInitialize() => HostExports.Address(nameof(X3DAudioInitialize));
        public static int X3DAudioCalculate() => HostExports.Address(nameof(X3DAudioCalculate));

        // Media Foundation (SharpDX.MediaFoundation): answered E_NOTIMPL, logged by name
        public static int MFCreate2DMediaBuffer() => HostExports.Address(nameof(MFCreate2DMediaBuffer));
        public static int MFCreateAlignedMemoryBuffer() => HostExports.Address(nameof(MFCreateAlignedMemoryBuffer));
        public static int MFCreateAsyncResult() => HostExports.Address(nameof(MFCreateAsyncResult));
        public static int MFCreateAttributes() => HostExports.Address(nameof(MFCreateAttributes));
        public static int MFCreateCollection() => HostExports.Address(nameof(MFCreateCollection));
        public static int MFCreateDXGIDeviceManager() => HostExports.Address(nameof(MFCreateDXGIDeviceManager));
        public static int MFCreateDXGISurfaceBuffer() => HostExports.Address(nameof(MFCreateDXGISurfaceBuffer));
        public static int MFCreateMediaBufferFromMediaType() => HostExports.Address(nameof(MFCreateMediaBufferFromMediaType));
        public static int MFCreateMediaBufferWrapper() => HostExports.Address(nameof(MFCreateMediaBufferWrapper));
        public static int MFCreateMediaTypeFromProperties() => HostExports.Address(nameof(MFCreateMediaTypeFromProperties));
        public static int MFCreateMemoryBuffer() => HostExports.Address(nameof(MFCreateMemoryBuffer));
        public static int MFCreateMFByteStreamOnStreamEx() => HostExports.Address(nameof(MFCreateMFByteStreamOnStreamEx));
        public static int MFCreatePropertiesFromMediaType() => HostExports.Address(nameof(MFCreatePropertiesFromMediaType));
        public static int MFCreateSample() => HostExports.Address(nameof(MFCreateSample));
        public static int MFCreateStreamOnMFByteStreamEx() => HostExports.Address(nameof(MFCreateStreamOnMFByteStreamEx));
        public static int MFCreateVideoSampleAllocatorEx() => HostExports.Address(nameof(MFCreateVideoSampleAllocatorEx));
        public static int MFDeserializeAttributesFromStream() => HostExports.Address(nameof(MFDeserializeAttributesFromStream));
        public static int MFGetAttributesAsBlob() => HostExports.Address(nameof(MFGetAttributesAsBlob));
        public static int MFGetAttributesAsBlobSize() => HostExports.Address(nameof(MFGetAttributesAsBlobSize));
        public static int MFInvokeCallback() => HostExports.Address(nameof(MFInvokeCallback));
        public static int MFllMulDiv() => HostExports.Address(nameof(MFllMulDiv));
        public static int MFLockDXGIDeviceManager() => HostExports.Address(nameof(MFLockDXGIDeviceManager));
        public static int MFLockPlatform() => HostExports.Address(nameof(MFLockPlatform));
        public static int MFSerializeAttributesToStream() => HostExports.Address(nameof(MFSerializeAttributesToStream));
        public static int MFShutdown() => HostExports.Address(nameof(MFShutdown));
        public static int MFStartup() => HostExports.Address(nameof(MFStartup));
        public static int MFUnlockDXGIDeviceManager() => HostExports.Address(nameof(MFUnlockDXGIDeviceManager));
        public static int MFUnlockPlatform() => HostExports.Address(nameof(MFUnlockPlatform));
    }
}

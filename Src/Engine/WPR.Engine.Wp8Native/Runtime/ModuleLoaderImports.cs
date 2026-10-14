namespace WPR.Wp8Native
{
    /// <summary>
    /// The library-loader imports, each to its documented contract and all backed by the module
    /// loader (ModuleLoader.cs): LoadPackagedLibrary (PhoneAppModelHost), LoadLibraryExW,
    /// GetProcAddress, GetModuleHandleW/A, GetModuleHandleExW, GetModuleFileNameW/A, FreeLibrary and
    /// DisableThreadLibraryCalls. Every one of these used to be answered 0 by default.
    /// </summary>
    public sealed partial class HostStubs
    {
        private const uint GetModuleHandleExFlagPin = 0x1;
        private const uint GetModuleHandleExFlagUnchangedRefcount = 0x2;
        private const uint GetModuleHandleExFlagFromAddress = 0x4;

        /// <summary>The image an unwound frame's RVAs are relative to (the main image for a frame that predates multi-module unwinding).</summary>
        private long FrameBase(UnwoundFrame frame) => frame.ImageBase != 0 ? frame.ImageBase : _emulator.ImageBase;

        private void SetLoaderError(int error) => Files.LoaderLastError = error;

        private void RegisterModuleLoader()
        {
            // HMODULE LoadPackagedLibrary(LPCWSTR lpwLibFileName, DWORD Reserved): a module from the
            // app's package, by a path relative to it. Reserved must be 0; an absolute path or one
            // leaving the package is ERROR_INVALID_PARAMETER; a name without an extension gets
            // ".dll"; NULL and ERROR_MOD_NOT_FOUND when the package has no such file; NULL and
            // ERROR_DLL_INIT_FAILED when its DllMain refuses. Loading an already-loaded module
            // returns the same handle and adds a reference. A path may go into subfolders, with
            // either separator, any case, and "." segments (Spark2's Lua modules); a bare name is
            // looked for among loaded modules, then in the package root only.
            _handlers["LoadPackagedLibrary"] = () =>
            {
                long namePointer = Arg(0);
                if (Arg(1) != 0 || namePointer == 0)
                {
                    SetLoaderError(ArmEmulator.ErrorInvalidParameter);
                    Return(0);
                    return;
                }

                string name = _emulator.ReadUtf16String(namePointer, 520);
                if (!IsPackageRelative(name))
                {
                    SetLoaderError(ArmEmulator.ErrorInvalidParameter);
                    Return(0);
                    return;
                }

                _emulator.LoadLibraryFromGuest(name, allowSystem: false, FinishLoad);
            };

            // HMODULE LoadLibraryExW(LPCWSTR, HANDLE hFile, DWORD flags): hFile must be NULL. A path
            // inside the install folder (or a bare name) loads from the package; a system DLL the
            // process already has answers with its handle.
            _handlers["LoadLibraryExW"] = () =>
            {
                long namePointer = Arg(0);
                if (namePointer == 0 || Arg(1) != 0)
                {
                    SetLoaderError(ArmEmulator.ErrorInvalidParameter);
                    Return(0);
                    return;
                }

                string name = _emulator.ReadUtf16String(namePointer, 520).Replace('/', '\\');
                int install = name.LastIndexOf(@"\Install\", StringComparison.OrdinalIgnoreCase);
                if (install >= 0)
                {
                    name = name[(install + @"\Install\".Length)..];
                }
                else if (!IsPackageRelative(name))
                {
                    name = name[(name.LastIndexOf('\\') + 1)..];
                }

                _emulator.LoadLibraryFromGuest(name, allowSystem: true, FinishLoad);
            };

            // FARPROC GetProcAddress(HMODULE, LPCSTR): a name, or an ordinal in the low word when
            // the high word is zero. NULL with ERROR_PROC_NOT_FOUND for a missing procedure and
            // ERROR_MOD_NOT_FOUND for a handle that is not a module.
            _handlers["GetProcAddress"] = () =>
            {
                long module = Arg(0);
                long procedure = Arg(1);
                string name = (procedure >> 16) == 0
                    ? $"#{procedure & 0xFFFF}"
                    : _frame.ReadNarrowString(procedure, 1024);

                // Asynchronous only when the export is forwarded to a DLL that has to be loaded
                // now: its DllMain runs before GetProcAddress returns.
                _emulator.ProcedureAddressFromGuest(module, name, (address, error) =>
                {
                    if (address is { } found)
                    {
                        Return(found);
                        return;
                    }

                    SetLoaderError(error);
                    Return(0);
                });
            };

            // HMODULE GetModuleHandleW(LPCWSTR): NULL is the process's own image; otherwise a
            // module already loaded, no reference added. NULL and ERROR_MOD_NOT_FOUND otherwise.
            _handlers["GetModuleHandleW"] = () => ReturnModuleHandle(Arg(0) == 0 ? null : _emulator.ReadUtf16String(Arg(0), 520));
            _handlers["GetModuleHandleA"] = () => ReturnModuleHandle(Arg(0) == 0 ? null : _frame.ReadNarrowString(Arg(0), 520));

            // BOOL GetModuleHandleExW(DWORD flags, LPCWSTR nameOrAddress, HMODULE* out): as
            // GetModuleHandle, adding a reference unless UNCHANGED_REFCOUNT, pinning with PIN, and
            // looking up by an address inside the module with FROM_ADDRESS. PIN together with
            // UNCHANGED_REFCOUNT, or a NULL out, is ERROR_INVALID_PARAMETER; *out is NULL on failure.
            _handlers["GetModuleHandleExW"] = () =>
            {
                uint flags = (uint)Arg(0);
                long name = Arg(1);
                long result = Arg(2);
                const uint known = GetModuleHandleExFlagPin | GetModuleHandleExFlagUnchangedRefcount | GetModuleHandleExFlagFromAddress;
                if (result == 0 || (flags & ~known) != 0 ||
                    (flags & (GetModuleHandleExFlagPin | GetModuleHandleExFlagUnchangedRefcount)) ==
                    (GetModuleHandleExFlagPin | GetModuleHandleExFlagUnchangedRefcount))
                {
                    if (result != 0)
                    {
                        _emulator.WriteUInt32(result, 0);
                    }

                    SetLoaderError(ArmEmulator.ErrorInvalidParameter);
                    Return(0);
                    return;
                }

                long handle = (flags & GetModuleHandleExFlagFromAddress) != 0
                    ? _emulator.ModuleHandleByAddress(name)
                    : _emulator.ModuleHandleByName(name == 0 ? null : _emulator.ReadUtf16String(name, 520));

                _emulator.WriteUInt32(result, (uint)handle);
                if (handle == 0)
                {
                    SetLoaderError(ArmEmulator.ErrorModNotFound);
                    Return(0);
                    return;
                }

                if ((flags & GetModuleHandleExFlagUnchangedRefcount) == 0)
                {
                    _emulator.ReferenceModule(handle, pin: (flags & GetModuleHandleExFlagPin) != 0);
                }

                Return(1);
            };

            // DWORD GetModuleFileNameW(HMODULE, LPWSTR, DWORD nSize): the module's full path, NULL
            // being the process image. Returns the length without the terminator; when it does not
            // fit, the string is truncated to nSize - 1 characters plus a terminator, nSize is
            // returned and the error is ERROR_INSUFFICIENT_BUFFER. 0 and ERROR_MOD_NOT_FOUND for a
            // handle that is not a module.
            _handlers["GetModuleFileNameW"] = () => ReturnModuleFileName(wide: true);
            _handlers["GetModuleFileNameA"] = () => ReturnModuleFileName(wide: false);

            // BOOL FreeLibrary(HMODULE): releases a reference from LoadPackagedLibrary or
            // GetModuleHandleEx; at zero the module gets DllMain(DLL_PROCESS_DETACH, NULL) and is
            // unloaded, its dependencies after it. FALSE and ERROR_MOD_NOT_FOUND for a non-module.
            _handlers["FreeLibrary"] = () => _emulator.FreeLibraryFromGuest(Arg(0), (ok, error) =>
            {
                if (!ok)
                {
                    SetLoaderError(error);
                }

                Return(ok ? 1 : 0);
            });

            // BOOL DisableThreadLibraryCalls(HMODULE): no DLL_THREAD_ATTACH/DETACH for that module
            // from now on. Nonzero on success.
            _handlers["DisableThreadLibraryCalls"] = () =>
            {
                if (_emulator.DisableThreadNotifications(Arg(0)))
                {
                    Return(1);
                    return;
                }

                SetLoaderError(ArmEmulator.ErrorModNotFound);
                Return(0);
            };
        }

        private void FinishLoad(long handle, int error)
        {
            if (handle == 0)
            {
                SetLoaderError(error);
            }

            Return(handle);
        }

        /// <summary>LoadPackagedLibrary takes only a path inside the package: no drive, no root, no "..".</summary>
        private static bool IsPackageRelative(string path) => ArmEmulator.IsInsidePackage(path);

        private void ReturnModuleHandle(string? name)
        {
            long handle = _emulator.ModuleHandleByName(name);
            if (handle == 0)
            {
                SetLoaderError(ArmEmulator.ErrorModNotFound);
            }

            Return(handle);
        }

        private void ReturnModuleFileName(bool wide)
        {
            long buffer = Arg(1);
            long size = Arg(2) & 0xFFFFFFFFL;
            string? path = _emulator.ModuleFileName(Arg(0));
            if (path is null)
            {
                SetLoaderError(ArmEmulator.ErrorModNotFound);
                Return(0);
                return;
            }

            if (size == 0 || buffer == 0)
            {
                SetLoaderError(ArmEmulator.ErrorInsufficientBuffer);
                Return(0);
                return;
            }

            bool fits = path.Length < size;
            string written = fits ? path : path[..(int)(size - 1)];
            byte[] bytes = wide
                ? System.Text.Encoding.Unicode.GetBytes(written + "\0")
                : System.Text.Encoding.ASCII.GetBytes(written + "\0");
            _emulator.WriteMemory(buffer, bytes);

            if (!fits)
            {
                SetLoaderError(ArmEmulator.ErrorInsufficientBuffer);
                Return(size);
                return;
            }

            Return(path.Length);
        }
    }

    public sealed partial class FileLibrary
    {
        /// <summary>The calling thread's last-error value, as GetLastError reports it, for other host libraries to set.</summary>
        internal int LoaderLastError
        {
            get => _lastError;
            set => _lastError = value;
        }
    }
}

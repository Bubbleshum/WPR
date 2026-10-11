namespace WPR.Wp8Native
{
    /// <summary>
    /// File mappings and <c>_stat</c>, to their documented contracts
    /// (Plans/RUNTIME-CONVERGENCE.md, 1c).
    /// </summary>
    public sealed partial class FileLibrary
    {
        /// <summary>Mapping handles live in their own range, so CloseHandle can tell them from files.</summary>
        private const long FirstMappingHandle = 0x00B00000;

        private const int PageReadWrite = 0x04, PageWriteCopy = 0x08, PageExecuteReadWrite = 0x40;
        private const int FileMapWrite = 0x02;
        private const int ErrorInvalidParameter = 87;
        private const int Ebadf = 9;

        private sealed record Mapping(OpenFile? File, long Size, bool Writable);

        private sealed record View(Mapping Mapping, long Block, long Offset, long Length, bool WriteBack);

        private readonly Dictionary<long, Mapping> _mappings = new();
        private readonly Dictionary<long, View> _views = new();
        private long _nextMappingHandle = FirstMappingHandle;

        private void RegisterMappingAndStat(Dictionary<string, Action> handlers)
        {
            handlers["CreateFileMappingFromApp"] = CreateFileMapping;
            handlers["MapViewOfFileFromApp"] = MapViewOfFile;
            handlers["UnmapViewOfFile"] = UnmapViewOfFile;

            // CloseHandle closes a mapping as well as a file; the view stays valid until unmapped,
            // as on Windows.
            Action closeFile = handlers["CloseHandle"];
            handlers["CloseHandle"] = () =>
            {
                if (_mappings.Remove(_frame.Arg(0)))
                {
                    _frame.Return(1);
                    return;
                }

                closeFile();
            };

            handlers["_stat64"] = () => Stat(ReadNarrow(0), _frame.Arg(1), wideSize: true);
            handlers["_wstat64"] = () => Stat(ReadWide(0), _frame.Arg(1), wideSize: true);
            handlers["_stat64i32"] = () => Stat(ReadNarrow(0), _frame.Arg(1), wideSize: false);
            handlers["_wstat64i32"] = () => Stat(ReadWide(0), _frame.Arg(1), wideSize: false);
            handlers["_fstat64"] = () => FStat(_frame.SignedArg(0), _frame.Arg(1), wideSize: true);
            handlers["_fstat64i32"] = () => FStat(_frame.SignedArg(0), _frame.Arg(1), wideSize: false);

            // FILE* std::_Fiopen(const char* path, ios_base::openmode, int prot): what basic_filebuf
            // opens through, so it is how every std::ifstream reaches a file. NULL on failure. The
            // mode string is the table MSVC's own _Fiopen uses; ate opens and then seeks to the end.
            handlers["?_Fiopen@std@@YAPAU_iobuf@@PBDHH@Z"] = () => OpenStream(ReadNarrow(0), (int)_frame.Arg(1));
            handlers["?_Fiopen@std@@YAPAU_iobuf@@PB_WHH@Z"] = () => OpenStream(ReadWide(0), (int)_frame.Arg(1));

            // intptr_t _get_osfhandle(int fd): the OS handle behind a descriptor. Here every open
            // file is one object reachable by its handle, so it is that handle; ReadFile and
            // friends accept it. stdin/stdout/stderr have no stream in an app (-2, as the CRT
            // answers for a GUI process); anything else is -1 with EBADF. 0 was neither.
            handlers["_get_osfhandle"] = () =>
            {
                int descriptor = _frame.SignedArg(0);
                if (descriptor is >= 0 and <= 2)
                {
                    _frame.Return(-2);
                    return;
                }

                if (_byDescriptor.TryGetValue(descriptor, out OpenFile? file) &&
                    _byPointer.FirstOrDefault(p => ReferenceEquals(p.Value, file)) is { Value: not null } entry)
                {
                    _frame.Return(entry.Key);
                    return;
                }

                _emulator.WriteUInt32(ErrnoAddress(), Ebadf);
                _frame.Return(-1);
            };

            // int remove(const char*) and its wide and _unlink spellings: 0 when deleted, -1 with
            // errno otherwise. Only the writable sandbox can lose a file, as with DeleteFileW; a
            // packaged file is EACCES, which is what a read-only install folder answers on a phone.
            handlers["remove"] = () => RemoveFile(ReadNarrow(0));
            handlers["_unlink"] = handlers["remove"];
            handlers["_wremove"] = () => RemoveFile(ReadWide(0));
            handlers["_wunlink"] = handlers["_wremove"];
        }

        private const int Eacces = 13;

        private void RemoveFile(string path)
        {
            string host = Resolve(path, out bool writable);
            if (!File.Exists(host))
            {
                _emulator.WriteUInt32(ErrnoAddress(), Enoent);
                _frame.Return(-1);
                return;
            }

            if (!writable)
            {
                _emulator.WriteUInt32(ErrnoAddress(), Eacces);
                _frame.Return(-1);
                return;
            }

            try
            {
                File.Delete(host);
                Log.Add($"remove(\"{path}\") -> deleted");
                _frame.Return(0);
            }
            catch (IOException)
            {
                _emulator.WriteUInt32(ErrnoAddress(), Eacces);
                _frame.Return(-1);
            }
        }

        private const int OpenIn = 0x01, OpenOut = 0x02, OpenAte = 0x04, OpenApp = 0x08, OpenTrunc = 0x10, OpenBinary = 0x20;

        private void OpenStream(string path, int openmode)
        {
            string? mode = (openmode & ~(OpenAte | OpenBinary | 0xC0)) switch
            {
                OpenIn => "r",
                OpenOut or OpenOut | OpenTrunc => "w",
                OpenOut | OpenApp or OpenApp => "a",
                OpenIn | OpenOut => "r+",
                OpenIn | OpenOut | OpenTrunc => "w+",
                OpenIn | OpenOut | OpenApp or OpenIn | OpenApp => "a+",
                _ => null,
            };

            if (mode is null)
            {
                Log.Add($"_Fiopen(\"{path}\", openmode 0x{openmode:X}) -> invalid mode");
                _frame.Return(0);
                return;
            }

            OpenStdio(path, (openmode & OpenBinary) != 0 ? mode + "b" : mode);
            long opened = _frame.Arg(0);   // OpenStdio returned the FILE* in r0
            if ((openmode & OpenAte) != 0 && opened != 0 && _byPointer.TryGetValue(opened, out OpenFile? file))
            {
                file.Stream.Seek(0, SeekOrigin.End);
                file.FilePointer = file.Stream.Position;
            }
        }

        /// <summary>
        /// HANDLE CreateFileMappingFromApp(HANDLE file, SECURITY_ATTRIBUTES*, ULONG protection,
        /// ULONG64 maximumSize, PCWSTR name). The 64-bit size is doubleword aligned, so it skips r3
        /// and arrives on the stack (words 4-5), with the name after it (word 6).
        /// </summary>
        private void CreateFileMapping()
        {
            long fileHandle = _frame.Arg(0);
            int protection = (int)_frame.Arg(2) & 0xFF;
            long maximum = _frame.Arg(4) | (_frame.Arg(5) << 32);
            bool writable = protection is PageReadWrite or PageWriteCopy or PageExecuteReadWrite;

            OpenFile? file = null;
            if (fileHandle != 0xFFFFFFFFL && fileHandle != -1)
            {
                if (!_byPointer.TryGetValue(fileHandle, out file))
                {
                    _lastError = ErrorInvalidHandle;
                    _frame.Return(0);
                    return;
                }
            }
            else if (maximum == 0)
            {
                // A pagefile-backed mapping must say how big it is.
                _lastError = ErrorInvalidParameter;
                _frame.Return(0);
                return;
            }

            long size = maximum != 0 ? maximum : file!.Stream.Length;
            if (file is not null && size > file.Stream.Length)
            {
                if (!writable || !file.Stream.CanWrite)
                {
                    // A read-only mapping cannot grow its file: ERROR_NOT_ENOUGH_MEMORY on Windows
                    // is too vague, and this is what the caller can act on.
                    _lastError = ErrorInvalidParameter;
                    _frame.Return(0);
                    return;
                }

                file.Stream.SetLength(size);
            }

            long handle = _nextMappingHandle;
            _nextMappingHandle += 4;
            _mappings[handle] = new Mapping(file, size, writable);
            Log.Add($"CreateFileMappingFromApp(\"{file?.Path ?? "<pagefile>"}\", {size} bytes{(writable ? ", writable" : "")})");
            _frame.Return(handle);
        }

        /// <summary>
        /// PVOID MapViewOfFileFromApp(HANDLE mapping, ULONG access, ULONG64 offset, SIZE_T bytes).
        /// The offset is the r2:r3 pair, the size on the stack. A view is a copy in guest memory:
        /// a writable one is written back at UnmapViewOfFile, which is enough for a title that maps
        /// a save and unmaps it, and wrong only for two live views of one file that expect to see
        /// each other's writes - nothing has needed that yet.
        /// </summary>
        private void MapViewOfFile()
        {
            if (!_mappings.TryGetValue(_frame.Arg(0), out Mapping? mapping))
            {
                _lastError = ErrorInvalidHandle;
                _frame.Return(0);
                return;
            }

            long access = _frame.Arg(1);
            long offset = _frame.Arg(2) | (_frame.Arg(3) << 32);
            long length = _frame.Arg(4);
            if (length == 0)
            {
                length = mapping.Size - offset;
            }

            if (offset < 0 || length <= 0 || offset + length > mapping.Size)
            {
                _lastError = ErrorInvalidParameter;
                _frame.Return(0);
                return;
            }

            // Views are page aligned on Windows, and code that maps a header and casts it relies on
            // at least natural alignment for everything inside.
            const long page = 0x1000;
            long block = _emulator.AllocateHeap(length + page);
            long address = (block + page - 1) & ~(page - 1);

            if (mapping.File is { } file)
            {
                long position = file.Stream.Position;
                try
                {
                    byte[] bytes = new byte[length];
                    file.Stream.Position = offset;
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int got = file.Stream.Read(bytes, read, bytes.Length - read);
                        if (got == 0)
                        {
                            break;
                        }

                        read += got;
                    }

                    _emulator.WriteMemory(address, bytes);
                }
                finally
                {
                    file.Stream.Position = position;
                }
            }
            else
            {
                _emulator.WriteMemory(address, new byte[length]);
            }

            bool writeBack = mapping.File is not null && mapping.Writable && (access & FileMapWrite) != 0;
            _views[address] = new View(mapping, block, offset, length, writeBack);
            Log.Add($"MapViewOfFileFromApp({mapping.File?.Path ?? "<pagefile>"} +{offset}, {length} bytes) -> 0x{address:X8}");
            _frame.Return(address);
        }

        /// <summary>BOOL UnmapViewOfFile(LPCVOID base): writes a writable view back, then frees it.</summary>
        private void UnmapViewOfFile()
        {
            if (!_views.Remove(_frame.Arg(0), out View? view))
            {
                _lastError = ErrorInvalidParameter;
                _frame.Return(0);
                return;
            }

            if (view.WriteBack && view.Mapping.File is { } file && file.Stream.CanWrite)
            {
                long position = file.Stream.Position;
                try
                {
                    file.Stream.Position = view.Offset;
                    file.Stream.Write(_emulator.ReadMemory(_frame.Arg(0), (int)view.Length));
                    file.Stream.Flush();
                }
                finally
                {
                    file.Stream.Position = position;
                }
            }

            _emulator.FreeHeap(view.Block);
            _frame.Return(1);
        }

        /// <summary>
        /// int _stat64(const char* path, struct _stat64* buffer), and the <c>i32</c> variant with
        /// a 32-bit size. 0 with the struct filled, or -1 with errno ENOENT and the struct untouched.
        /// Answering 0 without filling it - the old default - reported every file as existing, empty
        /// and from 1970.
        /// </summary>
        private void Stat(string path, long buffer, bool wideSize)
        {
            string? host = ExistingHostPath(path);
            if (host is null)
            {
                _emulator.WriteUInt32(ErrnoAddress(), Enoent);
                Log.Add($"_stat(\"{path}\") -> not found");
                _frame.Return(-1);
                return;
            }

            WriteStat(buffer, host, wideSize);
            _frame.Return(0);
        }

        /// <summary>int _fstat64(int fd, struct _stat64* buffer).</summary>
        private void FStat(int descriptor, long buffer, bool wideSize)
        {
            if (!_byDescriptor.TryGetValue(descriptor, out OpenFile? file))
            {
                _emulator.WriteUInt32(ErrnoAddress(), Ebadf);
                _frame.Return(-1);
                return;
            }

            file.Stream.Flush();
            WriteStat(buffer, file.Stream.Name, wideSize);
            _frame.Return(0);
        }

        /// <summary>
        /// The 32-bit MSVC layout: st_dev 0, st_ino 4, st_mode 6, st_nlink 8, st_uid 10, st_gid 12,
        /// st_rdev 16, then for _stat64 st_size (int64) 24 and the three time64s at 32/40/48 (56
        /// bytes); for _stat64i32 st_size (int32) 20 and the times at 24/32/40 (48 bytes).
        /// </summary>
        private void WriteStat(long buffer, string host, bool wideSize)
        {
            bool directory = Directory.Exists(host);
            FileSystemInfo info = directory ? new DirectoryInfo(host) : new FileInfo(host);

            // The CRT derives the mode from the attributes and copies the owner bits to group and
            // other: directories are rwx, files rw (r only when read-only), plus x for executables.
            int owner = directory ? 0x1C0 : (info.Attributes.HasFlag(FileAttributes.ReadOnly) ? 0x100 : 0x180);
            if (!directory && Path.GetExtension(host).ToLowerInvariant() is ".exe" or ".com" or ".bat" or ".cmd")
            {
                owner |= 0x40;
            }

            int mode = (directory ? 0x4000 : 0x8000) | owner | (owner >> 3) | (owner >> 6);
            long size = directory ? 0 : ((FileInfo)info).Length;
            const int driveC = 2;

            byte[] stat = new byte[wideSize ? 56 : 48];
            BitConverter.GetBytes(driveC).CopyTo(stat, 0);              // st_dev
            BitConverter.GetBytes((ushort)mode).CopyTo(stat, 6);        // st_mode
            BitConverter.GetBytes((short)1).CopyTo(stat, 8);            // st_nlink
            BitConverter.GetBytes(driveC).CopyTo(stat, 16);             // st_rdev
            int times;
            if (wideSize)
            {
                BitConverter.GetBytes(size).CopyTo(stat, 24);
                times = 32;
            }
            else
            {
                BitConverter.GetBytes((int)Math.Min(size, int.MaxValue)).CopyTo(stat, 20);
                times = 24;
            }

            BitConverter.GetBytes(new DateTimeOffset(info.LastAccessTimeUtc).ToUnixTimeSeconds()).CopyTo(stat, times);
            BitConverter.GetBytes(new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()).CopyTo(stat, times + 8);
            BitConverter.GetBytes(new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeSeconds()).CopyTo(stat, times + 16);
            _emulator.WriteMemory(buffer, stat);
        }

        /// <summary>
        /// The host file or folder an image path names, if it exists - through the same
        /// <see cref="Resolve"/> every open uses, plus a package folder, which the package index
        /// (files only) does not list.
        /// </summary>
        private string? ExistingHostPath(string path)
        {
            string host = Resolve(path, out _);
            if (host.Length == 0)
            {
                return null;
            }

            if (File.Exists(host) || Directory.Exists(host))
            {
                return host;
            }

            string relative = Path.GetRelativePath(_writeRoot, host);
            if (!relative.StartsWith("..", StringComparison.Ordinal))
            {
                string packaged = Path.Combine(_readRoot, relative);
                if (Directory.Exists(packaged))
                {
                    return packaged;
                }
            }

            return null;
        }
    }
}

using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The api-ms-win-core-file / file-l2 surface to its documented Win32 contract: creation
    /// dispositions, directory search, attributes, copy/move, byte-range locks, per-handle file
    /// information and the last-error each failure owes.
    /// </summary>
    /// <remarks>
    /// The two roots keep their meaning: the package (the install folder) is read-only, as it is
    /// on a phone, so anything that would change it answers ERROR_ACCESS_DENIED; the local folder
    /// is the writable sandbox. Narrow (A) entry points take the ANSI code page, 1252 on an en-US
    /// device.
    /// </remarks>
    public sealed partial class FileLibrary
    {
        private const int ErrorAccessDenied = 5;
        private const int ErrorNoMoreFiles = 18;
        private const int ErrorBadLength = 24;
        private const int ErrorLockViolation = 33;
        private const int ErrorHandleEof = 38;
        private const int ErrorFileExists = 80;
        private const int ErrorDirNotEmpty = 145;
        private const int ErrorNotLocked = 158;
        private const int ErrorAlreadyExists = 183;
        private const int ErrorMoreData = 234;

        private const long InvalidHandleValue = 0xFFFFFFFFL;
        private const uint InvalidFileAttributes = 0xFFFFFFFF;

        /// <summary>FindFirstFileEx search handles live in their own range.</summary>
        private const long FirstSearchHandle = 0x00C00000;

        private readonly Dictionary<long, Queue<(FileSystemInfo Entry, string Name)>> _win32Searches = new();
        private long _nextSearchHandle = FirstSearchHandle;

        /// <summary>
        /// Attributes a game set that the host filesystem cannot hold (hidden, system, archive on
        /// Android), by host path, so SetFileAttributes then GetFileAttributes round-trips.
        /// </summary>
        private readonly Dictionary<string, uint> _attributeOverlay = new(StringComparer.OrdinalIgnoreCase);

        private sealed record FileLock(OpenFile Owner, long Offset, long Length, bool Exclusive);

        /// <summary>Byte-range locks, by host path.</summary>
        private readonly Dictionary<string, List<FileLock>> _locks = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Encoding Ansi = CodePage1252();

        private static Encoding CodePage1252()
        {
            try
            {
                return Encoding.GetEncoding(1252);
            }
            catch (Exception)
            {
                // No code-page provider registered: Latin-1 agrees with 1252 everywhere except
                // 0x80-0x9F, which file names in a WP8 package do not use.
                return Encoding.Latin1;
            }
        }

        private void RegisterWin32Contracts(Dictionary<string, Action> handlers)
        {
            handlers["CreateFile2"] = CreateFile2Contract;
            handlers["CloseHandle"] = WrapCloseHandle(handlers["CloseHandle"]);

            handlers["CreateDirectoryW"] = () => CreateDirectoryContract(ReadWide(0));
            handlers["CreateDirectoryA"] = () => CreateDirectoryContract(ReadAnsi(0));
            handlers["RemoveDirectoryW"] = () => RemoveDirectoryContract(ReadWide(0));
            handlers["RemoveDirectoryA"] = () => RemoveDirectoryContract(ReadAnsi(0));
            handlers["DeleteFileW"] = () => DeleteFileContract(ReadWide(0));
            handlers["DeleteFileA"] = () => DeleteFileContract(ReadAnsi(0));

            handlers["GetFileAttributesExW"] = () => GetFileAttributesEx(ReadWide(0));
            handlers["GetFileAttributesExA"] = () => GetFileAttributesEx(ReadAnsi(0));
            handlers["GetFileAttributesW"] = () => _frame.Return(AttributesOf(ReadWide(0), out _, out _) ?? InvalidFileAttributes);
            handlers["GetFileAttributesA"] = () => _frame.Return(AttributesOf(ReadAnsi(0), out _, out _) ?? InvalidFileAttributes);
            handlers["SetFileAttributesW"] = () => SetFileAttributes(ReadWide(0), (uint)_frame.Arg(1));
            handlers["SetFileAttributesA"] = () => SetFileAttributes(ReadAnsi(0), (uint)_frame.Arg(1));

            handlers["MoveFileExW"] = () => MoveFileContract(ReadWide(0), ReadWide(1), _frame.Arg(2));
            handlers["CopyFile2"] = CopyFile2;

            handlers["FindFirstFileExW"] = () => FindFirstFileEx(ReadWide(0), wide: true);
            handlers["FindFirstFileExA"] = () => FindFirstFileEx(ReadAnsi(0), wide: false);
            handlers["FindNextFileW"] = () => FindNextFile(wide: true);
            handlers["FindNextFileA"] = () => FindNextFile(wide: false);
            handlers["FindClose"] = () =>
            {
                if (_win32Searches.Remove(_frame.Arg(0)))
                {
                    _frame.Return(1);
                    return;
                }

                _lastError = ErrorInvalidHandle;
                _frame.Return(0);
            };

            handlers["FlushViewOfFile"] = FlushViewOfFile;
            handlers["GetDiskFreeSpaceExW"] = GetDiskFreeSpaceEx;
            handlers["GetDiskFreeSpaceExA"] = GetDiskFreeSpaceEx;
            handlers["LockFileEx"] = LockFileEx;
            handlers["UnlockFileEx"] = UnlockFileEx;
            handlers["SetEndOfFile"] = SetEndOfFile;
            handlers["GetFileTime"] = GetFileTime;
            handlers["SetFileTime"] = SetFileTime;
            handlers["GetFileInformationByHandleEx"] = GetFileInformationByHandleExContract;
            handlers["SetFileInformationByHandle"] = SetFileInformationByHandle;
            handlers["GetFileSizeEx"] = () =>
            {
                if (!_byPointer.TryGetValue(_frame.Arg(0), out OpenFile? file))
                {
                    _lastError = ErrorInvalidHandle;
                    _frame.Return(0);
                    return;
                }

                if (_frame.Arg(1) == 0)
                {
                    _lastError = ErrorInvalidParameter;
                    _frame.Return(0);
                    return;
                }

                _emulator.WriteUInt64(_frame.Arg(1), (ulong)file.Stream.Length);
                _frame.Return(1);
            };

            // BOOL FlushFileBuffers(HANDLE): flushes, and fails on a handle that is not a file.
            handlers["FlushFileBuffers"] = () =>
            {
                if (!_byPointer.TryGetValue(_frame.Arg(0), out OpenFile? file))
                {
                    _lastError = ErrorInvalidHandle;
                    _frame.Return(0);
                    return;
                }

                if (file.Stream.CanWrite)
                {
                    file.Stream.Flush(flushToDisk: true);
                }

                _frame.Return(1);
            };
        }

        private string ReadAnsi(int argument)
        {
            long address = _frame.Arg(argument);
            if (address == 0)
            {
                return string.Empty;
            }

            var bytes = new List<byte>();
            while (bytes.Count < 1024)
            {
                byte b = _emulator.ReadMemory(address + bytes.Count, 1)[0];
                if (b == 0)
                {
                    break;
                }

                bytes.Add(b);
            }

            return Ansi.GetString(bytes.ToArray());
        }

        // ---------------------------------------------------------------------------
        // handles
        // ---------------------------------------------------------------------------

        /// <summary>Releases what a file object holds when its last handle closes.</summary>
        private void DisposeFile(OpenFile file)
        {
            string host = file.Stream.Name;
            foreach (List<FileLock> list in _locks.Values)
            {
                list.RemoveAll(l => ReferenceEquals(l.Owner, file));
            }

            file.Stream.Dispose();
            if (file.DeleteOnClose)
            {
                try
                {
                    File.Delete(host);
                }
                catch (Exception)
                {
                    // Gone already, or held elsewhere: either way not this handle's to report.
                }
            }
        }

        /// <summary>
        /// CloseHandle on a search handle is ERROR_INVALID_HANDLE on Windows (FindClose owns it);
        /// everything else falls through to whoever owns the handle.
        /// </summary>
        private Action WrapCloseHandle(Action inner) => () =>
        {
            if (_win32Searches.ContainsKey(_frame.Arg(0)))
            {
                _lastError = ErrorInvalidHandle;
                _frame.Return(0);
                return;
            }

            inner();
        };

        /// <summary>Whether this library owns <paramref name="handle"/> (a file, a FILE* or a mapping).</summary>
        public bool OwnsHandle(long handle) => _byPointer.ContainsKey(handle) || _mappings.ContainsKey(handle);

        /// <summary>
        /// DuplicateHandle for a file: a second handle on the same file object, sharing its
        /// position, as on Windows. The file closes when its last handle does.
        /// </summary>
        public long DuplicateFileHandle(long handle)
        {
            if (_byPointer.TryGetValue(handle, out OpenFile? file))
            {
                long copy = _nextHandle;
                _nextHandle += 4;
                _byPointer[copy] = file;
                return copy;
            }

            if (_mappings.TryGetValue(handle, out Mapping? mapping))
            {
                long copy = _nextMappingHandle++;
                _mappings[copy] = mapping;
                return copy;
            }

            return 0;
        }

        // ---------------------------------------------------------------------------
        // CreateFile2
        // ---------------------------------------------------------------------------

        private const long GenericRead = 0x80000000, GenericWrite = 0x40000000, GenericAll = 0x10000000;
        private const long FileWriteData = 0x2, FileAppendData = 0x4, FileReadData = 0x1;
        private const int CreateNew = 1, CreateAlways = 2, OpenExisting = 3, OpenAlways = 4, TruncateExisting = 5;
        private const uint FlagOverlapped = 0x40000000, FlagDeleteOnClose = 0x04000000;

        /// <summary>
        /// HANDLE CreateFile2(LPCWSTR name, DWORD access, DWORD share, DWORD disposition,
        /// CREATEFILE2_EXTENDED_PARAMETERS*): the five creation dispositions with their errors
        /// (CREATE_NEW on an existing file is ERROR_FILE_EXISTS, CREATE_ALWAYS / OPEN_ALWAYS over
        /// one succeed with ERROR_ALREADY_EXISTS, a missing folder is ERROR_PATH_NOT_FOUND), the
        /// install folder read-only, and a directory not openable as a file.
        /// </summary>
        private void CreateFile2Contract()
        {
            string path = ReadWide(0);
            long access = _frame.Arg(1);
            int disposition = (int)_frame.Arg(3);
            long extended = _frame.Arg(4);
            uint flags = extended == 0 ? 0 : _emulator.ReadUInt32(extended + 8);

            bool wantsWrite = (access & (GenericWrite | GenericAll | FileWriteData | FileAppendData)) != 0;
            bool wantsRead = (access & (GenericRead | GenericAll | FileReadData)) != 0;
            bool creates = disposition is CreateNew or CreateAlways or OpenAlways;

            if (disposition is < CreateNew or > TruncateExisting || path.Length == 0)
            {
                Fail(ErrorInvalidParameter, "invalid parameter");
                return;
            }

            string host = Resolve(path, out bool writable);
            bool exists = File.Exists(host);

            if (Directory.Exists(host))
            {
                Fail(ErrorAccessDenied, "is a directory");
                return;
            }

            if (!exists && !creates)
            {
                Fail(ParentExists(host) ? ErrorFileNotFound : ErrorPathNotFound, "not found");
                return;
            }

            if (!writable && (wantsWrite || !exists || disposition is CreateAlways or TruncateExisting))
            {
                Fail(ErrorAccessDenied, "the install folder is read-only");
                return;
            }

            if (disposition == CreateNew && exists)
            {
                Fail(ErrorFileExists, "exists");
                return;
            }

            if (disposition == TruncateExisting && !wantsWrite)
            {
                Fail(ErrorInvalidParameter, "TRUNCATE_EXISTING without write access");
                return;
            }

            if (!exists && !ParentExists(host))
            {
                Fail(ErrorPathNotFound, "folder not found");
                return;
            }

            try
            {
                FileMode mode = disposition switch
                {
                    CreateNew => FileMode.CreateNew,
                    CreateAlways => FileMode.Create,
                    OpenAlways => FileMode.OpenOrCreate,
                    TruncateExisting => FileMode.Truncate,
                    _ => FileMode.Open,
                };

                // Access 0 is a query-only open; the host still needs something to open with.
                FileAccess hostAccess = wantsWrite ? FileAccess.ReadWrite : FileAccess.Read;
                if (!wantsWrite && mode is FileMode.CreateNew or FileMode.OpenOrCreate or FileMode.Create)
                {
                    if (!exists)
                    {
                        File.WriteAllBytes(host, []);
                    }
                    else if (mode == FileMode.Create)
                    {
                        File.WriteAllBytes(host, []);
                    }

                    mode = FileMode.Open;
                }

                FileStream stream = new(host, mode, hostAccess, FileShare.ReadWrite | FileShare.Delete);
                long handle = _nextHandle;
                _nextHandle += 4;

                OpenFile file = new()
                {
                    Stream = stream,
                    Path = path,
                    Descriptor = _nextDescriptor++,
                    Overlapped = (flags & FlagOverlapped) != 0,
                    DeleteOnClose = (flags & FlagDeleteOnClose) != 0,
                };
                _byPointer[handle] = file;
                _byDescriptor[file.Descriptor] = file;

                // CREATE_ALWAYS and OPEN_ALWAYS report whether the file was already there.
                _lastError = exists && disposition is CreateAlways or OpenAlways ? ErrorAlreadyExists : 0;
                OpenedSuccessfully++;
                Log.Add($"CreateFile2(\"{path}\") -> {stream.Length} bytes");
                _frame.Return(handle);
            }
            catch (UnauthorizedAccessException)
            {
                Fail(ErrorAccessDenied, "access denied");
            }
            catch (Exception ex)
            {
                Fail(ErrorPathNotFound, ex.GetType().Name);
            }

            void Fail(int error, string why)
            {
                _lastError = error;
                OpenFailed++;
                Log.Add($"CreateFile2(\"{path}\") -> {why}");
                _frame.Return(InvalidHandleValue);
            }
        }

        private static bool ParentExists(string host)
        {
            // "Local/State/" names the folder State, whose parent is Local: Win32 accepts a
            // trailing separator on a directory name, and GetDirectoryName would otherwise
            // answer State itself. Amazing Alex creates every folder that way.
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(host));
            return parent is null || Directory.Exists(parent);
        }

        // ---------------------------------------------------------------------------
        // directories, deletion, attributes
        // ---------------------------------------------------------------------------

        /// <summary>BOOL CreateDirectory(name, SECURITY_ATTRIBUTES*): one level, never recursive.</summary>
        private void CreateDirectoryContract(string path)
        {
            string host = Resolve(path, out bool writable);
            if (Directory.Exists(host) || File.Exists(Path.TrimEndingDirectorySeparator(host)))
            {
                Log.Add($"CreateDirectory(\"{path}\") -> already exists");
                FailBool(ErrorAlreadyExists);
                return;
            }

            if (!writable)
            {
                Log.Add($"CreateDirectory(\"{path}\") -> access denied");
                FailBool(ErrorAccessDenied);
                return;
            }

            if (!ParentExists(host))
            {
                Log.Add($"CreateDirectory(\"{path}\") -> path not found");
                FailBool(ErrorPathNotFound);
                return;
            }

            try
            {
                Directory.CreateDirectory(host);
                Log.Add($"CreateDirectory(\"{path}\") -> created");
                _frame.Return(1);
            }
            catch (Exception)
            {
                FailBool(ErrorAccessDenied);
            }
        }

        /// <summary>BOOL RemoveDirectory(name): empty folders in the local folder only.</summary>
        private void RemoveDirectoryContract(string path)
        {
            string host = Resolve(path, out bool writable);
            if (!Directory.Exists(host))
            {
                FailBool(File.Exists(host) ? 267 /* ERROR_DIRECTORY */ : ParentExists(host) ? ErrorFileNotFound : ErrorPathNotFound);
                return;
            }

            if (!writable || string.Equals(Path.GetFullPath(host).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(_writeRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                FailBool(ErrorAccessDenied);
                return;
            }

            if (Directory.EnumerateFileSystemEntries(host).Any())
            {
                FailBool(ErrorDirNotEmpty);
                return;
            }

            try
            {
                Directory.Delete(host, recursive: false);
                _frame.Return(1);
            }
            catch (Exception)
            {
                FailBool(ErrorAccessDenied);
            }
        }

        /// <summary>BOOL DeleteFile(name): a missing file is ERROR_FILE_NOT_FOUND, a packaged or read-only one ERROR_ACCESS_DENIED.</summary>
        private void DeleteFileContract(string path)
        {
            string host = Resolve(path, out bool writable);
            if (Directory.Exists(host))
            {
                FailBool(ErrorAccessDenied);
                return;
            }

            if (!File.Exists(host))
            {
                FailBool(ParentExists(host) ? ErrorFileNotFound : ErrorPathNotFound);
                return;
            }

            if (!writable || (AttributesOf(host, isHostPath: true) & 0x1) != 0)
            {
                FailBool(ErrorAccessDenied);
                return;
            }

            try
            {
                File.Delete(host);
                _attributeOverlay.Remove(host);
                Log.Add($"DeleteFile(\"{path}\") -> deleted");
                _frame.Return(1);
            }
            catch (Exception)
            {
                FailBool(ErrorAccessDenied);
            }
        }

        private void FailBool(int error)
        {
            _lastError = error;
            _frame.Return(0);
        }

        /// <summary>The Win32 attribute word for a path, or null (with the last-error set) when it does not exist.</summary>
        private uint? AttributesOf(string path, out string host, out bool writable)
        {
            host = Resolve(path, out writable);
            if (!File.Exists(host) && !Directory.Exists(host))
            {
                _lastError = ParentExists(host) ? ErrorFileNotFound : ErrorPathNotFound;
                return null;
            }

            return AttributesOf(host, isHostPath: true);
        }

        /// <summary>
        /// The host's attributes in Win32 terms - .NET's FileAttributes values are the Win32 bits -
        /// kept to the ones a phone's file system has, with the overlay applied. NORMAL only alone.
        /// </summary>
        private uint AttributesOf(string host, bool isHostPath)
        {
            _ = isHostPath;
            bool directory = Directory.Exists(host);
            uint attributes;
            if (_attributeOverlay.TryGetValue(host, out uint overlay))
            {
                attributes = overlay;
            }
            else
            {
                try
                {
                    attributes = (uint)File.GetAttributes(host) & (0x1 | 0x2 | 0x4 | 0x20);
                }
                catch (Exception)
                {
                    attributes = 0;
                }
            }

            attributes &= ~0x10u;
            if (directory)
            {
                attributes |= 0x10;
            }

            return attributes == 0 ? 0x80u : attributes;
        }

        /// <summary>
        /// BOOL GetFileAttributesEx(name, GET_FILEEX_INFO_LEVELS, WIN32_FILE_ATTRIBUTE_DATA*):
        /// attributes, the three FILETIMEs and the size (high word first). Only level 0 exists.
        /// </summary>
        private void GetFileAttributesEx(string path)
        {
            long output = _frame.Arg(2);
            if (_frame.Arg(1) != 0 || output == 0)
            {
                FailBool(ErrorInvalidParameter);
                return;
            }

            uint? attributes = AttributesOf(path, out string host, out _);
            if (attributes is null)
            {
                Log.Add($"GetFileAttributesEx(\"{path}\") -> not found");
                _frame.Return(0);
                return;
            }

            FileSystemInfo info = (attributes.Value & 0x10) != 0 ? new DirectoryInfo(host) : new FileInfo(host);
            long size = info is FileInfo f ? f.Length : 0;
            byte[] block = new byte[36];
            BitConverter.GetBytes(attributes.Value).CopyTo(block, 0);
            BitConverter.GetBytes(FileTime(info.CreationTimeUtc)).CopyTo(block, 4);
            BitConverter.GetBytes(FileTime(info.LastAccessTimeUtc)).CopyTo(block, 12);
            BitConverter.GetBytes(FileTime(info.LastWriteTimeUtc)).CopyTo(block, 20);
            BitConverter.GetBytes((uint)(size >> 32)).CopyTo(block, 28);
            BitConverter.GetBytes((uint)size).CopyTo(block, 32);
            _emulator.WriteMemory(output, block);
            Log.Add($"GetFileAttributesEx(\"{path}\") -> {((attributes.Value & 0x10) != 0 ? "directory" : "file")}");
            _frame.Return(1);
        }

        private static long FileTime(DateTime utc)
        {
            try
            {
                return utc.ToFileTimeUtc();
            }
            catch (ArgumentOutOfRangeException)
            {
                return 0;
            }
        }

        /// <summary>
        /// BOOL SetFileAttributes(name, DWORD): read-only, hidden, system, archive and normal are
        /// settable; the directory bit is not. The install folder cannot be changed.
        /// </summary>
        private void SetFileAttributes(string path, uint attributes)
        {
            uint? current = AttributesOf(path, out string host, out bool writable);
            if (current is null)
            {
                _frame.Return(0);
                return;
            }

            if (!writable)
            {
                FailBool(ErrorAccessDenied);
                return;
            }

            uint settable = attributes & (0x1 | 0x2 | 0x4 | 0x20 | 0x80 | 0x100 | 0x2000);
            uint stored = settable & (0x1 | 0x2 | 0x4 | 0x20);
            try
            {
                FileAttributes onHost = File.GetAttributes(host);
                onHost = (stored & 0x1) != 0 ? onHost | FileAttributes.ReadOnly : onHost & ~FileAttributes.ReadOnly;
                File.SetAttributes(host, onHost);
            }
            catch (Exception)
            {
                // The host keeps what it can; the overlay below keeps the rest for this run.
            }

            _attributeOverlay[host] = stored;
            _frame.Return(1);
        }

        // ---------------------------------------------------------------------------
        // move / copy
        // ---------------------------------------------------------------------------

        private const long MoveFileReplaceExisting = 0x1;

        /// <summary>
        /// BOOL MoveFileEx(from, to, flags): an existing destination is ERROR_ALREADY_EXISTS unless
        /// MOVEFILE_REPLACE_EXISTING; nothing moves into or out of the install folder.
        /// </summary>
        private void MoveFileContract(string from, string to, long flags)
        {
            if (Environment.GetEnvironmentVariable("WPR_MOVETRACE") is not null)
            {
                var calls = _emulator.CallOrder;
                Console.Error.WriteLine($"[move] {from} -> {to}; preceding: " +
                    string.Join(" | ", calls.Skip(Math.Max(0, calls.Count - 3000)).Select(c => c.Split((char)33)[^1]).Where(c => c is not ("memcpy" or "memmove" or "memset" or "strlen" or "??2@YAPAXI@Z" or "??3@YAXPAX@Z" or "_lock" or "_unlock"))));
            }

            string source = Resolve(from, out bool sourceWritable);
            string destination = Resolve(to, out bool destinationWritable);
            bool sourceIsDirectory = Directory.Exists(source);

            if (!sourceIsDirectory && !File.Exists(source))
            {
                Report(ParentExists(source) ? ErrorFileNotFound : ErrorPathNotFound);
                return;
            }

            if (!sourceWritable || !destinationWritable)
            {
                Report(ErrorAccessDenied);
                return;
            }

            if (!ParentExists(destination))
            {
                Report(ErrorPathNotFound);
                return;
            }

            bool destinationExists = File.Exists(destination) || Directory.Exists(destination);
            bool sameFile = string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase);
            if (destinationExists && !sameFile && ((flags & MoveFileReplaceExisting) == 0 || sourceIsDirectory || Directory.Exists(destination)))
            {
                Report(ErrorAlreadyExists);
                return;
            }

            try
            {
                if (sourceIsDirectory)
                {
                    Directory.Move(source, destination);
                }
                else
                {
                    File.Move(source, destination, overwrite: true);
                }

                Log.Add($"MoveFileExW(\"{from}\" -> \"{to}\") -> ok");
                _frame.Return(1);
            }
            catch (Exception ex)
            {
                Log.Add($"MoveFileExW(\"{from}\" -> \"{to}\") -> {ex.GetType().Name}");
                Report(ErrorAccessDenied);
            }

            void Report(int error)
            {
                if (error != 0)
                {
                    Log.Add($"MoveFileExW(\"{from}\" -> \"{to}\") -> error {error}");
                }

                FailBool(error);
            }
        }

        /// <summary>
        /// HRESULT CopyFile2(src, dst, COPYFILE2_EXTENDED_PARAMETERS*): S_OK, or
        /// HRESULT_FROM_WIN32 of the error. COPY_FILE_FAIL_IF_EXISTS (1) refuses an existing
        /// destination. The copy is a single step, so the optional progress routine is never called -
        /// it is a notification, and a copy that finishes before its first chunk owes none.
        /// </summary>
        private void CopyFile2()
        {
            string from = ReadWide(0);
            string to = ReadWide(1);
            long parameters = _frame.Arg(2);
            uint flags = parameters == 0 ? 0 : _emulator.ReadUInt32(parameters + 4);

            string source = Resolve(from, out _);
            string destination = Resolve(to, out bool destinationWritable);

            int error = 0;
            if (Directory.Exists(source))
            {
                error = ErrorAccessDenied;
            }
            else if (!File.Exists(source))
            {
                error = ParentExists(source) ? ErrorFileNotFound : ErrorPathNotFound;
            }
            else if (!destinationWritable || Directory.Exists(destination))
            {
                error = ErrorAccessDenied;
            }
            else if (!ParentExists(destination))
            {
                error = ErrorPathNotFound;
            }
            else if ((flags & 0x1) != 0 && File.Exists(destination))
            {
                error = ErrorFileExists;
            }
            else if (File.Exists(destination) && (AttributesOf(destination, isHostPath: true) & 0x1) != 0)
            {
                error = ErrorAccessDenied;
            }

            if (error == 0)
            {
                try
                {
                    File.Copy(source, destination, overwrite: true);
                    Log.Add($"CopyFile2(\"{from}\" -> \"{to}\") -> ok");
                    _frame.Return(0);
                    return;
                }
                catch (Exception)
                {
                    error = ErrorAccessDenied;
                }
            }

            _lastError = error;
            Log.Add($"CopyFile2(\"{from}\" -> \"{to}\") -> error {error}");
            _frame.Return(0x80070000L | (uint)error);
        }

        // ---------------------------------------------------------------------------
        // FindFirstFileEx
        // ---------------------------------------------------------------------------

        /// <summary>
        /// HANDLE FindFirstFileEx(spec, FINDEX_INFO_LEVELS, WIN32_FIND_DATA*, FINDEX_SEARCH_OPS,
        /// void* filter, DWORD flags). Lists the real folder the spec resolves to, filtered by its
        /// wildcard, "." and ".." first as a non-root folder reports them. A missing folder is
        /// ERROR_PATH_NOT_FOUND; a folder with no match ERROR_FILE_NOT_FOUND.
        /// FindExSearchLimitToDirectories is advisory on Windows and is honoured here.
        /// </summary>
        private void FindFirstFileEx(string spec, bool wide)
        {
            long infoLevel = _frame.Arg(1);
            long data = _frame.Arg(2);
            long searchOp = _frame.Arg(3);
            if (infoLevel > 1 || searchOp > 2 || data == 0 || spec.Length == 0)
            {
                _lastError = ErrorInvalidParameter;
                _frame.Return(InvalidHandleValue);
                return;
            }

            string normalised = spec.Replace('\\', '/');
            int slash = normalised.LastIndexOf('/');
            string folder = slash >= 0 ? normalised[..slash] : string.Empty;
            string pattern = slash >= 0 ? normalised[(slash + 1)..] : normalised;
            string host = folder.Length == 0 ? _readRoot : Resolve(folder, out _);

            if (!Directory.Exists(host))
            {
                _lastError = ErrorPathNotFound;
                _frame.Return(InvalidHandleValue);
                return;
            }

            var found = new Queue<(FileSystemInfo, string)>();
            if (pattern.Length > 0)
            {
                var directory = new DirectoryInfo(host);
                if (Matches(".", pattern)) found.Enqueue((directory, "."));
                if (Matches("..", pattern)) found.Enqueue((directory.Parent ?? directory, ".."));
                foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos()
                             .Where(e => Matches(e.Name, pattern))
                             .Where(e => searchOp != 1 || e is DirectoryInfo)
                             .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    found.Enqueue((entry, entry.Name));
                }
            }

            if (found.Count == 0)
            {
                _lastError = ErrorFileNotFound;
                _frame.Return(InvalidHandleValue);
                return;
            }

            long handle = _nextSearchHandle;
            _nextSearchHandle += 4;
            (FileSystemInfo first, string name) = found.Dequeue();
            WriteWin32FindData(data, first, name, wide);
            _win32Searches[handle] = found;
            _frame.Return(handle);
        }

        /// <summary>BOOL FindNextFile(HANDLE, WIN32_FIND_DATA*): FALSE with ERROR_NO_MORE_FILES at the end.</summary>
        private void FindNextFile(bool wide)
        {
            if (!_win32Searches.TryGetValue(_frame.Arg(0), out var queue))
            {
                FailBool(ErrorInvalidHandle);
                return;
            }

            if (queue.Count == 0)
            {
                FailBool(ErrorNoMoreFiles);
                return;
            }

            (FileSystemInfo entry, string name) = queue.Dequeue();
            WriteWin32FindData(_frame.Arg(1), entry, name, wide);
            _frame.Return(1);
        }

        /// <summary>
        /// WIN32_FIND_DATAW: attributes 0, three FILETIMEs at 4/12/20, size high 28 and low 32,
        /// two reserved words, cFileName[260] at 44, cAlternateFileName[14] at 564 (592 bytes).
        /// The A form is the same up to cFileName, which is 260 chars at 44, then 14 at 304 (320).
        /// </summary>
        private void WriteWin32FindData(long data, FileSystemInfo entry, string name, bool wide)
        {
            uint attributes = AttributesOf(entry.FullName, isHostPath: true);
            long size = entry is FileInfo file ? file.Length : 0;
            byte[] block = new byte[wide ? 592 : 320];
            BitConverter.GetBytes(attributes).CopyTo(block, 0);
            BitConverter.GetBytes(FileTime(entry.CreationTimeUtc)).CopyTo(block, 4);
            BitConverter.GetBytes(FileTime(entry.LastAccessTimeUtc)).CopyTo(block, 12);
            BitConverter.GetBytes(FileTime(entry.LastWriteTimeUtc)).CopyTo(block, 20);
            BitConverter.GetBytes((uint)(size >> 32)).CopyTo(block, 28);
            BitConverter.GetBytes((uint)size).CopyTo(block, 32);

            byte[] encoded = wide ? Encoding.Unicode.GetBytes(name) : Ansi.GetBytes(name);
            int room = wide ? 518 : 259;
            Array.Copy(encoded, 0, block, 44, Math.Min(encoded.Length, room));
            _emulator.WriteMemory(data, block);
        }

        // ---------------------------------------------------------------------------
        // disk space
        // ---------------------------------------------------------------------------

        /// <summary>
        /// BOOL GetDiskFreeSpaceEx(folder, ULARGE_INTEGER* freeToCaller, total, totalFree): the
        /// volume the local folder lives on. A missing folder is ERROR_PATH_NOT_FOUND.
        /// </summary>
        private void GetDiskFreeSpaceEx()
        {
            if (_frame.Arg(0) != 0)
            {
                string folder = ReadWide(0);
                string host = Resolve(folder, out _);
                if (!Directory.Exists(host))
                {
                    FailBool(ErrorPathNotFound);
                    return;
                }
            }

            (long free, long total) = VolumeSpace();
            if (_frame.Arg(1) != 0) _emulator.WriteUInt64(_frame.Arg(1), (ulong)free);
            if (_frame.Arg(2) != 0) _emulator.WriteUInt64(_frame.Arg(2), (ulong)total);
            if (_frame.Arg(3) != 0) _emulator.WriteUInt64(_frame.Arg(3), (ulong)free);
            _frame.Return(1);
        }

        private (long Free, long Total) VolumeSpace()
        {
            try
            {
                string root = Path.GetFullPath(_writeRoot);
                DriveInfo? best = null;
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    string mount = drive.RootDirectory.FullName;
                    if (root.StartsWith(mount, StringComparison.OrdinalIgnoreCase) &&
                        (best is null || mount.Length > best.RootDirectory.FullName.Length))
                    {
                        best = drive;
                    }
                }

                if (best is { IsReady: true })
                {
                    return (best.AvailableFreeSpace, best.TotalSize);
                }
            }
            catch (Exception)
            {
                // Fall through to a figure that is at least a phone's.
            }

            // An 8 GB phone with 2 GB free, when the host will not say.
            return (2L << 30, 8L << 30);
        }

        // ---------------------------------------------------------------------------
        // byte-range locks
        // ---------------------------------------------------------------------------

        private const long LockFileFailImmediately = 0x1, LockFileExclusive = 0x2;

        /// <summary>
        /// BOOL LockFileEx(HANDLE, flags, reserved, lengthLow, lengthHigh, OVERLAPPED*): the
        /// offset is in the OVERLAPPED. Exclusive locks conflict with any overlapping lock, shared
        /// ones only with exclusive ones. A conflict is ERROR_LOCK_VIOLATION with
        /// LOCKFILE_FAIL_IMMEDIATELY, and otherwise blocks the calling thread until it clears.
        /// </summary>
        private void LockFileEx()
        {
            long flags = _frame.Arg(1);
            long length = _frame.Arg(3) | (_frame.Arg(4) << 32);
            long overlapped = _frame.Arg(5);
            if (!_byPointer.TryGetValue(_frame.Arg(0), out OpenFile? file))
            {
                FailBool(ErrorInvalidHandle);
                return;
            }

            if (overlapped == 0 || _frame.Arg(2) != 0)
            {
                FailBool(ErrorInvalidParameter);
                return;
            }

            long offset = _emulator.ReadUInt32(overlapped + 8) | ((long)_emulator.ReadUInt32(overlapped + 12) << 32);
            var request = new FileLock(file, offset, length, (flags & LockFileExclusive) != 0);
            List<FileLock> list = LocksFor(file);

            _frame.Return(1);
            if (!Conflicts(list, request))
            {
                list.Add(request);
                CompleteOverlapped(overlapped, 0);
                return;
            }

            if ((flags & LockFileFailImmediately) != 0)
            {
                FailBool(ErrorLockViolation);
                return;
            }

            bool switched = _emulator.BlockCurrentThread(
                () => !Conflicts(list, request),
                () =>
                {
                    list.Add(request);
                    CompleteOverlapped(overlapped, 0);
                },
                why: $"file lock on {Path.GetFileName(file.Path)}");
            if (!switched)
            {
                // Nothing else can run, so nothing will ever release it.
                FailBool(ErrorLockViolation);
            }
        }

        /// <summary>BOOL UnlockFileEx(HANDLE, reserved, lengthLow, lengthHigh, OVERLAPPED*): the exact range, or ERROR_NOT_LOCKED.</summary>
        private void UnlockFileEx()
        {
            long length = _frame.Arg(2) | (_frame.Arg(3) << 32);
            long overlapped = _frame.Arg(4);
            if (!_byPointer.TryGetValue(_frame.Arg(0), out OpenFile? file))
            {
                FailBool(ErrorInvalidHandle);
                return;
            }

            if (overlapped == 0 || _frame.Arg(1) != 0)
            {
                FailBool(ErrorInvalidParameter);
                return;
            }

            long offset = _emulator.ReadUInt32(overlapped + 8) | ((long)_emulator.ReadUInt32(overlapped + 12) << 32);
            List<FileLock> list = LocksFor(file);
            int index = list.FindIndex(l => ReferenceEquals(l.Owner, file) && l.Offset == offset && l.Length == length);
            if (index < 0)
            {
                FailBool(ErrorNotLocked);
                return;
            }

            list.RemoveAt(index);
            CompleteOverlapped(overlapped, 0);
            _frame.Return(1);
        }

        private List<FileLock> LocksFor(OpenFile file)
        {
            if (!_locks.TryGetValue(file.Stream.Name, out List<FileLock>? list))
            {
                list = new List<FileLock>();
                _locks[file.Stream.Name] = list;
            }

            return list;
        }

        private static bool Conflicts(List<FileLock> held, FileLock request) =>
            held.Any(l => (l.Exclusive || request.Exclusive) &&
                          request.Offset < l.Offset + Math.Max(1, l.Length) &&
                          l.Offset < request.Offset + Math.Max(1, request.Length));

        /// <summary>
        /// An operation on an OVERLAPPED that completed before returning: status, bytes, and the
        /// event (if any) set, as the I/O manager would on completion. The low bit of hEvent only
        /// suppresses completion-port queueing.
        /// </summary>
        private void CompleteOverlapped(long overlapped, long transferred)
        {
            if (overlapped == 0)
            {
                return;
            }

            _emulator.WriteUInt32(overlapped, 0);
            _emulator.WriteUInt32(overlapped + 4, (uint)transferred);
            long hEvent = _emulator.ReadUInt32(overlapped + 16) & ~1L;
            if (hEvent != 0)
            {
                _emulator.Stubs.Sync.SignalEvent(hEvent);
            }
        }

        // ---------------------------------------------------------------------------
        // per-handle information
        // ---------------------------------------------------------------------------

        /// <summary>BOOL SetEndOfFile(HANDLE): truncates or extends to the file pointer; needs write access.</summary>
        private void SetEndOfFile()
        {
            if (!_byPointer.TryGetValue(_frame.Arg(0), out OpenFile? file))
            {
                FailBool(ErrorInvalidHandle);
                return;
            }

            if (!file.Stream.CanWrite)
            {
                FailBool(ErrorAccessDenied);
                return;
            }

            file.Stream.SetLength(file.Stream.Position);
            _frame.Return(1);
        }

        /// <summary>BOOL GetFileTime(HANDLE, FILETIME* creation, FILETIME* access, FILETIME* write).</summary>
        private void GetFileTime()
        {
            if (!_byPointer.TryGetValue(_frame.Arg(0), out OpenFile? file))
            {
                FailBool(ErrorInvalidHandle);
                return;
            }

            var info = new FileInfo(file.Stream.Name);
            if (_frame.Arg(1) != 0) _emulator.WriteUInt64(_frame.Arg(1), (ulong)FileTime(info.CreationTimeUtc));
            if (_frame.Arg(2) != 0) _emulator.WriteUInt64(_frame.Arg(2), (ulong)FileTime(info.LastAccessTimeUtc));
            if (_frame.Arg(3) != 0) _emulator.WriteUInt64(_frame.Arg(3), (ulong)FileTime(info.LastWriteTimeUtc));
            _frame.Return(1);
        }

        /// <summary>BOOL SetFileTime(HANDLE, const FILETIME* x3): null or zero leaves a time alone; needs write access.</summary>
        private void SetFileTime()
        {
            if (!_byPointer.TryGetValue(_frame.Arg(0), out OpenFile? file))
            {
                FailBool(ErrorInvalidHandle);
                return;
            }

            if (!file.Stream.CanWrite)
            {
                FailBool(ErrorAccessDenied);
                return;
            }

            SetTimes(file.Stream.Name, ReadTime(_frame.Arg(1)), ReadTime(_frame.Arg(2)), ReadTime(_frame.Arg(3)));
            _frame.Return(1);
        }

        private long ReadTime(long pointer) => pointer == 0 ? 0 : (long)BitConverter.ToUInt64(_emulator.ReadMemory(pointer, 8));

        private static void SetTimes(string host, long creation, long access, long write)
        {
            try
            {
                if (creation > 0) File.SetCreationTimeUtc(host, DateTime.FromFileTimeUtc(creation));
                if (access > 0) File.SetLastAccessTimeUtc(host, DateTime.FromFileTimeUtc(access));
                if (write > 0) File.SetLastWriteTimeUtc(host, DateTime.FromFileTimeUtc(write));
            }
            catch (Exception)
            {
                // A host that cannot store a creation time keeps the others.
            }
        }

        private const int FileBasicInfo = 0, FileStandardInfo = 1, FileNameInfo = 2, FileRenameInfo = 3,
            FileDispositionInfo = 4, FileAllocationInfo = 5, FileEndOfFileInfo = 6, FileAttributeTagInfo = 9,
            FileIoPriorityHintInfo = 12;

        /// <summary>
        /// BOOL GetFileInformationByHandleEx(HANDLE, FILE_INFO_BY_HANDLE_CLASS, void*, DWORD):
        /// FileBasicInfo (times, attributes), FileStandardInfo (sizes, links, delete-pending,
        /// directory), FileNameInfo (the path below the volume root; ERROR_MORE_DATA when it does
        /// not fit) and FileAttributeTagInfo. A buffer smaller than the class is ERROR_BAD_LENGTH;
        /// a class with nothing to say about a file is ERROR_INVALID_PARAMETER.
        /// </summary>
        private void GetFileInformationByHandleExContract()
        {
            long buffer = _frame.Arg(2);
            int size = (int)_frame.Arg(3);
            if (!_byPointer.TryGetValue(_frame.Arg(0), out OpenFile? file))
            {
                FailBool(ErrorInvalidHandle);
                return;
            }

            var info = new FileInfo(file.Stream.Name);
            uint attributes = AttributesOf(file.Stream.Name, isHostPath: true);
            switch ((int)_frame.Arg(1))
            {
                case FileBasicInfo:
                {
                    if (!Fits(40)) return;
                    byte[] block = new byte[40];
                    BitConverter.GetBytes(FileTime(info.CreationTimeUtc)).CopyTo(block, 0);
                    BitConverter.GetBytes(FileTime(info.LastAccessTimeUtc)).CopyTo(block, 8);
                    BitConverter.GetBytes(FileTime(info.LastWriteTimeUtc)).CopyTo(block, 16);
                    BitConverter.GetBytes(FileTime(info.LastWriteTimeUtc)).CopyTo(block, 24);
                    BitConverter.GetBytes(attributes).CopyTo(block, 32);
                    _emulator.WriteMemory(buffer, block);
                    break;
                }

                case FileStandardInfo:
                {
                    if (!Fits(24)) return;
                    long length = file.Stream.Length;
                    byte[] block = new byte[24];
                    BitConverter.GetBytes((length + 4095) & ~4095L).CopyTo(block, 0);
                    BitConverter.GetBytes(length).CopyTo(block, 8);
                    BitConverter.GetBytes(1u).CopyTo(block, 16);
                    block[20] = file.DeleteOnClose ? (byte)1 : (byte)0;
                    block[21] = 0;
                    _emulator.WriteMemory(buffer, block);
                    break;
                }

                case FileNameInfo:
                {
                    if (!Fits(8)) return;
                    string name = VolumeRelativeName(file.Path);
                    byte[] text = Encoding.Unicode.GetBytes(name);
                    _emulator.WriteUInt32(buffer, (uint)text.Length);
                    int room = size - 4;
                    _emulator.WriteMemory(buffer + 4, text.Length <= room ? text : text[..(room & ~1)]);
                    if (text.Length > room)
                    {
                        FailBool(ErrorMoreData);
                        return;
                    }

                    break;
                }

                case FileAttributeTagInfo:
                    if (!Fits(8)) return;
                    _emulator.WriteUInt32(buffer, attributes);
                    _emulator.WriteUInt32(buffer + 4, 0);
                    break;

                default:
                    FailBool(ErrorInvalidParameter);
                    return;
            }

            _frame.Return(1);

            bool Fits(int needed)
            {
                if (buffer != 0 && size >= needed)
                {
                    return true;
                }

                FailBool(ErrorBadLength);
                return false;
            }
        }

        /// <summary>A path as FileNameInfo reports it: no drive, rooted at the volume.</summary>
        private static string VolumeRelativeName(string guestPath)
        {
            string name = guestPath.Replace('/', '\\');
            if (name.Length > 1 && name[1] == ':')
            {
                name = name[2..];
            }

            return name.StartsWith('\\') ? name : "\\" + name;
        }

        /// <summary>
        /// BOOL SetFileInformationByHandle(HANDLE, class, void*, DWORD): basic info (times and
        /// attributes), end of file, allocation (a hint), disposition (delete on close), rename and
        /// the I/O priority hint. Anything that changes the file needs a writable handle.
        /// </summary>
        private void SetFileInformationByHandle()
        {
            long buffer = _frame.Arg(2);
            int size = (int)_frame.Arg(3);
            if (!_byPointer.TryGetValue(_frame.Arg(0), out OpenFile? file))
            {
                FailBool(ErrorInvalidHandle);
                return;
            }

            int infoClass = (int)_frame.Arg(1);
            int needed = infoClass switch
            {
                FileBasicInfo => 40,
                FileRenameInfo => 12,
                FileDispositionInfo => 1,
                FileAllocationInfo or FileEndOfFileInfo => 8,
                FileIoPriorityHintInfo => 4,
                _ => -1,
            };

            if (needed < 0)
            {
                FailBool(ErrorInvalidParameter);
                return;
            }

            if (buffer == 0 || size < needed)
            {
                FailBool(ErrorBadLength);
                return;
            }

            if (infoClass != FileIoPriorityHintInfo && !file.Stream.CanWrite)
            {
                FailBool(ErrorAccessDenied);
                return;
            }

            switch (infoClass)
            {
                case FileBasicInfo:
                {
                    byte[] block = _emulator.ReadMemory(buffer, 40);
                    SetTimes(file.Stream.Name, BitConverter.ToInt64(block, 0), BitConverter.ToInt64(block, 8), BitConverter.ToInt64(block, 16));
                    uint attributes = BitConverter.ToUInt32(block, 32);
                    if (attributes != 0)
                    {
                        _attributeOverlay[file.Stream.Name] = attributes & (0x1 | 0x2 | 0x4 | 0x20);
                    }

                    break;
                }

                case FileEndOfFileInfo:
                    file.Stream.SetLength(BitConverter.ToInt64(_emulator.ReadMemory(buffer, 8)));
                    break;

                case FileDispositionInfo:
                    file.DeleteOnClose = _emulator.ReadMemory(buffer, 1)[0] != 0;
                    break;

                case FileRenameInfo:
                {
                    bool replace = _emulator.ReadMemory(buffer, 1)[0] != 0;
                    int length = (int)_emulator.ReadUInt32(buffer + 8);
                    string target = Encoding.Unicode.GetString(_emulator.ReadMemory(buffer + 12, Math.Clamp(length, 0, 2048)));
                    string destination = Resolve(target, out bool writable);
                    if (!writable)
                    {
                        FailBool(ErrorAccessDenied);
                        return;
                    }

                    if (File.Exists(destination) && !replace)
                    {
                        FailBool(ErrorAlreadyExists);
                        return;
                    }

                    if (!ParentExists(destination))
                    {
                        FailBool(ErrorPathNotFound);
                        return;
                    }

                    RenameOpenFile(file, destination, target);
                    break;
                }
            }

            _frame.Return(1);
        }

        /// <summary>Moves an open file and reopens it where it went, keeping its position.</summary>
        private void RenameOpenFile(OpenFile file, string destination, string guestPath)
        {
            long position = file.Stream.Position;
            string source = file.Stream.Name;
            FileAccess access = file.Stream.CanWrite ? FileAccess.ReadWrite : FileAccess.Read;
            file.Stream.Dispose();
            File.Move(source, destination, overwrite: true);
            var reopened = new FileStream(destination, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete) { Position = position };
            OpenFile moved = new()
            {
                Stream = reopened,
                Path = guestPath,
                Descriptor = file.Descriptor,
                FilePointer = file.FilePointer,
                Overlapped = file.Overlapped,
                DeleteOnClose = file.DeleteOnClose,
            };

            foreach (long key in _byPointer.Where(p => ReferenceEquals(p.Value, file)).Select(p => p.Key).ToList())
            {
                _byPointer[key] = moved;
            }

            _byDescriptor[file.Descriptor] = moved;
        }

        /// <summary>
        /// BOOL FlushViewOfFile(const void* base, SIZE_T bytes): writes that part of a writable
        /// view (to its end when bytes is 0) back to the file now, rather than at unmap. A read-only
        /// view has nothing to write. An address in no view is ERROR_INVALID_ADDRESS.
        /// </summary>
        private void FlushViewOfFile()
        {
            long address = _frame.Arg(0);
            long bytes = _frame.Arg(1);
            foreach ((long start, View view) in _views)
            {
                if (address < start || address >= start + view.Length)
                {
                    continue;
                }

                long length = bytes == 0 ? start + view.Length - address : Math.Min(bytes, start + view.Length - address);
                if (view.WriteBack && view.Mapping.File is { } file && file.Stream.CanWrite && length > 0)
                {
                    long position = file.Stream.Position;
                    try
                    {
                        file.Stream.Position = view.Offset + (address - start);
                        file.Stream.Write(_emulator.ReadMemory(address, (int)length));
                        file.Stream.Flush();
                    }
                    finally
                    {
                        file.Stream.Position = position;
                    }
                }

                _frame.Return(1);
                return;
            }

            FailBool(487); // ERROR_INVALID_ADDRESS
        }
    }
}

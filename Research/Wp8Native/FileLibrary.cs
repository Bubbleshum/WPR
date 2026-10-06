using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// File I/O, backed by real files on the host.
    /// </summary>
    /// <remarks>
    /// The image reads its assets through C stdio - <c>fopen</c>, <c>fread</c>,
    /// <c>fseek</c> - and manages its save data through the Win32 layer. Both are here,
    /// over two roots: the unpacked XAP directory, which is read-only and holds everything
    /// shipped with the game, and a writable sandbox standing in for the app's local
    /// folder. Nothing the image writes escapes the sandbox.
    ///
    /// Case matters more than it looks. WP8 paths are case-insensitive and the image is
    /// written accordingly, but this probe usually runs on a case-sensitive filesystem, so
    /// an asset opened as <c>Data/Scripts/x.lua</c> would not be found on disk as
    /// <c>data/scripts/x.lua</c>. Every lookup falls back to a case-insensitive walk.
    /// </remarks>
    public sealed class FileLibrary
    {
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;

        /// <summary>A handle value the image will not mistake for null or INVALID_HANDLE_VALUE.</summary>
        private const long FirstHandle = 0x00A00000;

        private sealed class OpenFile
        {
            public required FileStream Stream { get; init; }
            public required string Path { get; init; }
            public long FilePointer { get; set; }
            public int Descriptor { get; set; }
            public bool AtEof { get; set; }
        }

        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;
        private readonly string _readRoot;
        private readonly string _writeRoot;
        private readonly string _localFolderPrefix;

        private readonly Dictionary<long, OpenFile> _byPointer = new();
        private readonly Dictionary<int, OpenFile> _byDescriptor = new();
        private int _nextDescriptor = 3;   // 0-2 are stdin/stdout/stderr
        private long _nextHandle = FirstHandle;
        private int _lastError;

        public FileLibrary(ArmEmulator emulator, CallFrame frame, string readRoot, string writeRoot)
        {
            _emulator = emulator;
            _frame = frame;
            _readRoot = readRoot;
            _writeRoot = writeRoot;
            _localFolderPrefix = WinRtRuntime.LocalFolderPath;

            Directory.CreateDirectory(_writeRoot);
        }

        /// <summary>Every path the image asked for, and what happened - the discovery log.</summary>
        public List<string> Log { get; } = new();

        public int OpenedSuccessfully { get; private set; }

        public int OpenFailed { get; private set; }

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            // --- C stdio ---
            handlers["fopen"] = () => OpenStdio(ReadNarrow(0), ReadNarrow(1));
            handlers["_wfopen"] = () => OpenStdio(ReadWide(0), ReadWide(1));
            handlers["freopen"] = () =>
            {
                CloseStdio(_frame.Arg(2));
                OpenStdio(ReadNarrow(0), ReadNarrow(1));
            };

            handlers["fclose"] = () => _frame.Return(CloseStdio(_frame.Arg(0)) ? 0 : -1);
            handlers["fread"] = Read;
            handlers["fwrite"] = Write;
            handlers["fseek"] = Seek;
            handlers["ftell"] = () => _frame.Return(Find(_frame.Arg(0))?.FilePointer ?? -1);
            handlers["feof"] = () => _frame.Return(Find(_frame.Arg(0))?.AtEof == true ? 1 : 0);
            handlers["ferror"] = () => _frame.Return(0);
            handlers["fflush"] = () => _frame.Return(0);
            handlers["fputs"] = () => WriteText(_frame.Arg(1), ReadNarrow(0));
            handlers["fprintf"] = () => WriteText(
                _frame.Arg(0),
                new PrintfFormatter(_emulator, _frame)
                    .Format(ReadNarrow(1), new VarArgReader(_emulator, 2)));

            handlers["_fileno"] = () => _frame.Return(Find(_frame.Arg(0))?.Descriptor ?? -1);
            handlers["_close"] = () => _frame.Return(CloseDescriptor((int)_frame.Arg(0)) ? 0 : -1);
            handlers["_read"] = ReadDescriptor;
            handlers["_lseek"] = SeekDescriptor;

            // --- Win32 ---
            handlers["CreateFile2"] = CreateFile2;
            handlers["CloseHandle"] = () => _frame.Return(CloseStdio(_frame.Arg(0)) ? 1 : 1);
            handlers["GetFileAttributesExW"] = GetFileAttributes;
            handlers["CreateDirectoryW"] = CreateDirectory;
            handlers["MoveFileExW"] = MoveFile;
            handlers["FlushFileBuffers"] = () => _frame.Return(1);
            handlers["SetFileInformationByHandle"] = () => _frame.Return(1);
            handlers["GetFileInformationByHandleEx"] = () => _frame.Return(0);
            // Directory search. Unimplemented, these answered 0 - which for _wfindnext means
            // "found another" - so a game listing a folder looped for ever on a black screen
            // (Angry Birds Space, Classic, Seasons, Star Wars II and Stella all import them).
            handlers["_wfindfirst64i32"] = () => FindFirst(wide: true);
            handlers["_findfirst64i32"] = () => FindFirst(wide: false);
            handlers["_wfindnext64i32"] = () => FindNext(wide: true);
            handlers["_findnext64i32"] = () => FindNext(wide: false);
            handlers["_findclose"] = () =>
            {
                int handle = (int)_frame.Arg(0);
                _searchRoots.Remove(handle);
                _frame.Return(_searches.Remove(handle) ? 0 : -1);
            };
            handlers["RemoveDirectoryW"] = RemoveDirectory;

            // int _mkdir(const char*): 0, or -1 with errno.
            handlers["_mkdir"] = () =>
            {
                string host = Resolve(ReadNarrow(0), out _);
                try
                {
                    bool existed = Directory.Exists(host);
                    Directory.CreateDirectory(host);
                    _frame.Return(existed ? 0xFFFFFFFFL : 0);
                }
                catch (Exception)
                {
                    _frame.Return(0xFFFFFFFFL);
                }
            };

            // BOOL DeleteFileW(LPCWSTR): only inside the writable sandbox - never the package.
            handlers["DeleteFileW"] = () =>
            {
                string host = Resolve(ReadWide(0), out bool writable);
                if (writable && File.Exists(host))
                {
                    try
                    {
                        File.Delete(host);
                        _frame.Return(1);
                        return;
                    }
                    catch (IOException)
                    {
                    }
                }

                _lastError = ErrorFileNotFound;
                _frame.Return(0);
            };

            // int getc(FILE*) / int ungetc(int, FILE*): one byte, through the same file pointer
            // fread uses.
            handlers["getc"] = () =>
            {
                OpenFile? file = Find(_frame.Arg(0));
                if (file is null)
                {
                    _frame.Return(0xFFFFFFFFL);
                    return;
                }

                file.Stream.Position = file.FilePointer;
                int c = file.Stream.ReadByte();
                if (c < 0)
                {
                    file.AtEof = true;
                    _frame.Return(0xFFFFFFFFL);
                    return;
                }

                file.FilePointer++;
                _frame.Return(c);
            };
            handlers["ungetc"] = () =>
            {
                OpenFile? file = Find(_frame.Arg(1));
                int c = _frame.SignedArg(0);
                if (file is null || c < 0 || file.FilePointer == 0)
                {
                    _frame.Return(0xFFFFFFFFL);
                    return;
                }

                file.FilePointer--;
                file.AtEof = false;
                _frame.Return(c & 0xFF);
            };
            handlers["_errno"] = () => _frame.Return(ErrnoAddress());

            handlers["GetLastError"] = () => _frame.Return(_lastError);
            handlers["SetLastError"] = () => { _lastError = (int)_frame.Arg(0); _frame.Return(0); };
        }

        // ---------------------------------------------------------------------------
        // Path resolution
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Maps a path the image used onto a real one. Anything under the local folder is
        /// writable and lives in the sandbox; everything else is an asset read from the
        /// unpacked package.
        /// </summary>
        private string Resolve(string emulatedPath, out bool writable)
        {
            writable = false;
            if (string.IsNullOrWhiteSpace(emulatedPath))
            {
                return string.Empty;
            }

            string path = emulatedPath.Replace('\\', '/').Trim();

            if (path.StartsWith(_localFolderPrefix.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                writable = true;
                string relative = path[_localFolderPrefix.Length..].TrimStart('/');
                return Path.Combine(_writeRoot, relative);
            }

            // A WP8 package is installed at something like
            // C:\Applications\Install\{ProductId}\Install\..., so anything after the last
            // "Install" segment is the path within the package.
            int installAt = path.LastIndexOf("/Install/", StringComparison.OrdinalIgnoreCase);
            if (installAt >= 0)
            {
                path = path[(installAt + "/Install/".Length)..];
            }

            // Otherwise strip a drive letter or leading slash and treat it as relative.
            if (path.Length > 2 && path[1] == ':')
            {
                path = path[2..];
            }

            path = path.TrimStart('/');

            string candidate = ResolveCaseInsensitive(_readRoot, path);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }

            // Not shipped with the package, so it must be something the image creates.
            writable = true;
            return Path.Combine(_writeRoot, path);
        }

        /// <summary>
        /// Resolves a relative path one segment at a time, matching case-insensitively when
        /// an exact match does not exist.
        /// </summary>
        private static string ResolveCaseInsensitive(string root, string relative)
        {
            string current = root;

            foreach (string segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                string exact = Path.Combine(current, segment);
                if (File.Exists(exact) || Directory.Exists(exact))
                {
                    current = exact;
                    continue;
                }

                if (!Directory.Exists(current))
                {
                    return Path.Combine(current, segment);
                }

                string? match = Directory
                    .EnumerateFileSystemEntries(current)
                    .FirstOrDefault(e => string.Equals(
                        Path.GetFileName(e), segment, StringComparison.OrdinalIgnoreCase));

                current = match ?? Path.Combine(current, segment);
            }

            return current;
        }

        // ---------------------------------------------------------------------------
        // stdio
        // ---------------------------------------------------------------------------

        private void OpenStdio(string path, string mode)
        {
            string host = Resolve(path, out bool writable);
            bool wantsWrite = mode.Contains('w') || mode.Contains('a') || mode.Contains('+');

            try
            {
                if (wantsWrite && !writable)
                {
                    // Writing to something shipped in the package: redirect it into the
                    // sandbox rather than refusing, since the image expects it to work.
                    host = Path.Combine(_writeRoot, Path.GetFileName(host));
                }

                if (wantsWrite)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(host)!);
                }
                else if (!File.Exists(host))
                {
                    _lastError = ErrorFileNotFound;
                    OpenFailed++;
                    Log.Add($"fopen(\"{path}\", \"{mode}\") -> not found");
                    _frame.Return(0);
                    return;
                }

                FileStream stream = new(
                    host,
                    mode.Contains('a') ? FileMode.Append : wantsWrite ? FileMode.Create : FileMode.Open,
                    wantsWrite ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite);

                long pointer = _emulator.AllocateHeap(16);
                OpenFile file = new() { Stream = stream, Path = path, Descriptor = _nextDescriptor++ };

                _byPointer[pointer] = file;
                _byDescriptor[file.Descriptor] = file;

                OpenedSuccessfully++;
                Log.Add($"fopen(\"{path}\", \"{mode}\") -> {stream.Length} bytes");
                _frame.Return(pointer);
            }
            catch (Exception ex)
            {
                _lastError = ErrorPathNotFound;
                OpenFailed++;
                Log.Add($"fopen(\"{path}\", \"{mode}\") -> {ex.GetType().Name}");
                _frame.Return(0);
            }
        }

        private OpenFile? Find(long pointer) => _byPointer.GetValueOrDefault(pointer);

        private bool CloseStdio(long pointer)
        {
            if (!_byPointer.Remove(pointer, out OpenFile? file))
            {
                return false;
            }

            _byDescriptor.Remove(file.Descriptor);
            file.Stream.Dispose();
            return true;
        }

        private bool CloseDescriptor(int descriptor)
        {
            if (!_byDescriptor.Remove(descriptor, out OpenFile? file))
            {
                return false;
            }

            file.Stream.Dispose();
            return true;
        }

        /// <summary>size_t fread(void* buffer, size_t size, size_t count, FILE* stream)</summary>
        private void Read()
        {
            OpenFile? file = Find(_frame.Arg(3));
            long total = _frame.Arg(1) * _frame.Arg(2);

            if (file is null || total <= 0 || total > CallFrame.SaneLengthLimit)
            {
                _frame.Return(0);
                return;
            }

            byte[] buffer = new byte[total];
            file.Stream.Position = file.FilePointer;
            int read = file.Stream.Read(buffer, 0, (int)total);

            if (read > 0)
            {
                _emulator.WriteMemory(_frame.Arg(0), buffer[..read]);
            }

            file.FilePointer += read;
            file.AtEof = read < total;

            // A short read is how a game finds out its data is not what it expected, and it
            // usually reacts by throwing rather than by checking - so the file and the two
            // numbers are worth having when that happens.
            if (read < total)
            {
                Log.Add($"fread({System.IO.Path.GetFileName(file.Path)}) wanted {total} bytes at {file.FilePointer - read}, got {read}");
            }

            // fread returns whole items, not bytes.
            _frame.Return(_frame.Arg(1) == 0 ? 0 : read / _frame.Arg(1));
        }

        private void Write()
        {
            OpenFile? file = Find(_frame.Arg(3));
            long total = _frame.Arg(1) * _frame.Arg(2);

            if (file is null || total <= 0 || total > CallFrame.SaneLengthLimit)
            {
                _frame.Return(0);
                return;
            }

            file.Stream.Position = file.FilePointer;
            file.Stream.Write(_emulator.ReadMemory(_frame.Arg(0), (int)total));
            file.FilePointer += total;

            _frame.Return(_frame.Arg(1) == 0 ? 0 : total / _frame.Arg(1));
        }

        private void WriteText(long pointer, string text)
        {
            OpenFile? file = Find(pointer);
            if (file is null)
            {
                _frame.Return(-1);
                return;
            }

            byte[] bytes = Encoding.Latin1.GetBytes(text);
            file.Stream.Position = file.FilePointer;
            file.Stream.Write(bytes);
            file.FilePointer += bytes.Length;
            _frame.Return(bytes.Length);
        }

        private void Seek()
        {
            OpenFile? file = Find(_frame.Arg(0));
            if (file is null)
            {
                _frame.Return(-1);
                return;
            }

            file.FilePointer = Reposition(file, _frame.SignedArg(1), _frame.SignedArg(2));
            file.AtEof = false;
            _frame.Return(0);
        }

        private void SeekDescriptor()
        {
            OpenFile? file = _byDescriptor.GetValueOrDefault((int)_frame.Arg(0));
            if (file is null)
            {
                _frame.Return(-1);
                return;
            }

            file.FilePointer = Reposition(file, _frame.SignedArg(1), _frame.SignedArg(2));
            _frame.Return(file.FilePointer);
        }

        private void ReadDescriptor()
        {
            OpenFile? file = _byDescriptor.GetValueOrDefault((int)_frame.Arg(0));
            long count = _frame.Arg(2);

            if (file is null || count <= 0 || count > CallFrame.SaneLengthLimit)
            {
                _frame.Return(0);
                return;
            }

            byte[] buffer = new byte[count];
            file.Stream.Position = file.FilePointer;
            int read = file.Stream.Read(buffer, 0, (int)count);

            if (read > 0)
            {
                _emulator.WriteMemory(_frame.Arg(1), buffer[..read]);
            }

            file.FilePointer += read;
            _frame.Return(read);
        }

        /// <summary>SEEK_SET is 0, SEEK_CUR 1, SEEK_END 2.</summary>
        /// <summary>
        /// Applies an fseek, clamped to the file.
        /// </summary>
        /// <remarks>
        /// The clamp matters as much as the arithmetic. A position past the end is legal in
        /// C and simply reads nothing, but a negative one is not, and letting either through
        /// turns a bad seek into a bad read a long way downstream.
        /// </remarks>
        private static long Reposition(OpenFile file, int offset, int origin)
        {
            long position = origin switch
            {
                1 => file.FilePointer + offset,
                2 => file.Stream.Length + offset,
                _ => offset,
            };

            return Math.Max(0, position);
        }

        // ---------------------------------------------------------------------------
        // Win32
        // ---------------------------------------------------------------------------

        /// <summary>
        /// HANDLE CreateFile2(PCWSTR name, DWORD access, DWORD share, DWORD creation, ...)
        /// </summary>
        private void CreateFile2()
        {
            string path = ReadWide(0);
            long access = _frame.Arg(1);
            long creation = _frame.Arg(3);

            const long genericWrite = 0x40000000;
            bool wantsWrite = (access & genericWrite) != 0 || creation is 1 or 2; // CREATE_NEW / CREATE_ALWAYS

            string host = Resolve(path, out bool writable);
            if (wantsWrite && !writable)
            {
                host = Path.Combine(_writeRoot, Path.GetFileName(host));
            }

            try
            {
                if (wantsWrite)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(host)!);
                }
                else if (!File.Exists(host))
                {
                    _lastError = ErrorFileNotFound;
                    OpenFailed++;
                    Log.Add($"CreateFile2(\"{path}\") -> not found");
                    _frame.Return(-1);  // INVALID_HANDLE_VALUE
                    return;
                }

                FileStream stream = new(
                    host,
                    wantsWrite ? FileMode.OpenOrCreate : FileMode.Open,
                    wantsWrite ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite);

                long handle = _nextHandle;
                _nextHandle += 4;

                OpenFile file = new() { Stream = stream, Path = path, Descriptor = _nextDescriptor++ };
                _byPointer[handle] = file;
                _byDescriptor[file.Descriptor] = file;

                OpenedSuccessfully++;
                Log.Add($"CreateFile2(\"{path}\") -> {stream.Length} bytes");
                _frame.Return(handle);
            }
            catch (Exception ex)
            {
                _lastError = ErrorPathNotFound;
                OpenFailed++;
                Log.Add($"CreateFile2(\"{path}\") -> {ex.GetType().Name}");
                _frame.Return(-1);
            }
        }

        /// <summary>
        /// BOOL GetFileAttributesExW(PCWSTR name, level, WIN32_FILE_ATTRIBUTE_DATA* out)
        /// </summary>
        private void GetFileAttributes()
        {
            string path = ReadWide(0);
            string host = Resolve(path, out _);
            long output = _frame.Arg(2);

            const uint attributeDirectory = 0x10;
            const uint attributeNormal = 0x80;

            bool isDirectory = Directory.Exists(host);
            if (!isDirectory && !File.Exists(host))
            {
                _lastError = ErrorFileNotFound;
                Log.Add($"GetFileAttributesExW(\"{path}\") -> not found");
                _frame.Return(0);
                return;
            }

            if (output != 0)
            {
                long size = isDirectory ? 0 : new FileInfo(host).Length;

                _emulator.WriteUInt32(output, isDirectory ? attributeDirectory : attributeNormal);
                _emulator.WriteUInt64(output + 4, 0);      // creation time
                _emulator.WriteUInt64(output + 12, 0);     // last access
                _emulator.WriteUInt64(output + 20, 0);     // last write
                _emulator.WriteUInt32(output + 28, (uint)(size >> 32));
                _emulator.WriteUInt32(output + 32, (uint)size);
            }

            Log.Add($"GetFileAttributesExW(\"{path}\") -> {(isDirectory ? "directory" : "file")}");
            _frame.Return(1);
        }

        private void CreateDirectory()
        {
            string path = ReadWide(0);
            string host = Resolve(path, out _);

            try
            {
                Directory.CreateDirectory(host);
                Log.Add($"CreateDirectoryW(\"{path}\") -> created");
                _frame.Return(1);
            }
            catch (Exception)
            {
                _lastError = ErrorPathNotFound;
                _frame.Return(0);
            }
        }

        private void MoveFile()
        {
            string from = ReadWide(0);
            string to = ReadWide(1);
            if (Environment.GetEnvironmentVariable("WPR_MOVETRACE") is not null)
            {
                var calls = _emulator.CallOrder;
                Console.Error.WriteLine($"[move] {from} -> {to}; preceding: " +
                    string.Join(" | ", calls.Skip(Math.Max(0, calls.Count - 3000)).Select(c => c.Split((char)33)[^1]).Where(c => c is not ("memcpy" or "memmove" or "memset" or "strlen" or "??2@YAPAXI@Z" or "??3@YAXPAX@Z" or "_lock" or "_unlock"))));
            }

            try
            {
                string source = Resolve(from, out _);
                string destination = Resolve(to, out _);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination, overwrite: true);
                Log.Add($"MoveFileExW(\"{from}\" -> \"{to}\") -> ok");
                _frame.Return(1);
            }
            catch (Exception ex)
            {
                _lastError = ErrorFileNotFound;
                Log.Add($"MoveFileExW(\"{from}\" -> \"{to}\") -> {ex.GetType().Name}");
                _frame.Return(0);
            }
        }

        /// <summary>
        /// Checks path resolution against the real package, including the case-insensitive
        /// fallback: an asset asked for in the wrong case must still be found.
        /// </summary>
        public string CheckResolution(string emulatedPath)
        {
            string host = Resolve(emulatedPath, out bool writable);
            if (File.Exists(host))
            {
                return $"\"{emulatedPath}\" -> {new FileInfo(host).Length} bytes";
            }

            return Directory.Exists(host)
                ? $"\"{emulatedPath}\" -> directory"
                : $"\"{emulatedPath}\" -> NOT FOUND (writable={writable})";
        }

        private string ReadNarrow(int argument) => _frame.ReadNarrowString(_frame.Arg(argument));

        private readonly Dictionary<int, Queue<(FileSystemInfo Entry, string Name)>> _searches = new();
        private readonly Dictionary<int, string> _searchRoots = new();
        private int _nextSearch = 1;
        private long _errnoAddress;
        private const int Enoent = 2;

        /// <summary>errno lives in guest memory, since callers read it through _errno().</summary>
        private long ErrnoAddress()
        {
            if (_errnoAddress == 0)
            {
                _errnoAddress = _emulator.AllocateHeap(16);
            }

            return _errnoAddress;
        }

        /// <summary>
        /// intptr_t _wfindfirst64i32(const wchar_t* spec, _wfinddata64i32_t* data): lists the real
        /// folder the spec resolves to, filtered by its wildcard, "." and ".." first as the
        /// Windows CRT returns them. -1 with errno ENOENT when nothing matches.
        /// </summary>
        private void FindFirst(bool wide)
        {
            string spec = wide ? ReadWide(0) : ReadNarrow(0);
            long data = _frame.Arg(1);

            string normalised = spec.Replace('\\', '/');
            int slash = normalised.LastIndexOf('/');
            string folder = slash >= 0 ? normalised[..slash] : string.Empty;
            string pattern = slash >= 0 ? normalised[(slash + 1)..] : normalised;
            if (pattern.Length == 0)
            {
                pattern = "*";
            }

            string host = folder.Length == 0 ? _readRoot : Resolve(folder, out _);
            var found = new Queue<(FileSystemInfo, string)>();
            if (Directory.Exists(host))
            {
                var directory = new DirectoryInfo(host);
                if (Matches(".", pattern)) found.Enqueue((directory, "."));
                if (Matches("..", pattern)) found.Enqueue((directory.Parent ?? directory, ".."));
                foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos()
                             .Where(e => Matches(e.Name, pattern))
                             .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    found.Enqueue((entry, entry.Name));
                }
            }

            if (found.Count == 0)
            {
                _emulator.WriteUInt32(ErrnoAddress(), Enoent);
                _frame.Return(0xFFFFFFFFL);
                return;
            }

            int handle = _nextSearch++;
            (FileSystemInfo first, string name) = found.Dequeue();
            WriteFindData(data, first, name, wide);
            _searches[handle] = found;
            _searchRoots[handle] = host;
            _frame.Return(handle);
        }

        private void FindNext(bool wide)
        {
            int handle = (int)_frame.Arg(0);
            if (!_searches.TryGetValue(handle, out var queue) || queue.Count == 0)
            {
                _emulator.WriteUInt32(ErrnoAddress(), Enoent);
                _frame.Return(0xFFFFFFFFL);
                return;
            }

            (FileSystemInfo entry, string name) = queue.Dequeue();
            WriteFindData(_frame.Arg(1), entry, name, wide);
            _frame.Return(0);
        }

        /// <summary>
        /// _wfinddata64i32_t on ARM: attrib at 0, three __time64_t at 8/16/24 (8-aligned),
        /// _fsize_t size at 32, then name[260] at 36 - wchar_t or char.
        /// </summary>
        private void WriteFindData(long data, FileSystemInfo entry, string name, bool wide)
        {
            if (data == 0)
            {
                return;
            }

            bool isDirectory = entry is DirectoryInfo;
            uint attributes = isDirectory ? 0x10u : 0x20u;
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                attributes |= 0x01;
            }

            static long Unix(DateTime t) => new DateTimeOffset(t.ToUniversalTime()).ToUnixTimeSeconds();
            uint size = entry is FileInfo file ? (uint)Math.Min(file.Length, uint.MaxValue) : 0u;

            byte[] block = new byte[wide ? 36 + 520 : 36 + 260];
            BitConverter.GetBytes(attributes).CopyTo(block, 0);
            BitConverter.GetBytes(Unix(entry.CreationTimeUtc)).CopyTo(block, 8);
            BitConverter.GetBytes(Unix(entry.LastAccessTimeUtc)).CopyTo(block, 16);
            BitConverter.GetBytes(Unix(entry.LastWriteTimeUtc)).CopyTo(block, 24);
            BitConverter.GetBytes(size).CopyTo(block, 32);

            byte[] encoded = wide ? System.Text.Encoding.Unicode.GetBytes(name) : System.Text.Encoding.Latin1.GetBytes(name);
            Array.Copy(encoded, 0, block, 36, Math.Min(encoded.Length, block.Length - 36 - (wide ? 2 : 1)));
            _emulator.WriteMemory(data, block);
        }

        /// <summary>DOS wildcards: * and ?, case-insensitive; "*.*" matches everything.</summary>
        private static bool Matches(string name, string pattern)
        {
            if (pattern is "*" or "*.*")
            {
                return true;
            }

            string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(
                name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>BOOL RemoveDirectoryW(LPCWSTR): only ever inside the writable sandbox.</summary>
        private void RemoveDirectory()
        {
            string host = Resolve(ReadWide(0), out bool writable);
            try
            {
                if (writable && Directory.Exists(host))
                {
                    Directory.Delete(host, recursive: false);
                    _frame.Return(1);
                    return;
                }
            }
            catch (IOException)
            {
            }

            _lastError = ErrorFileNotFound;
            _frame.Return(0);
        }

        private string ReadWide(int argument) => _emulator.ReadUtf16String(_frame.Arg(argument), 1024);
    }
}

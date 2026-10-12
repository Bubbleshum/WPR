using System.Text;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The rest of MSVCR110's stdio and low-level I/O, to the VC11 CRT's documented contracts:
    /// stream modes (append, text, ccs=), the error indicator, pushback, 64-bit positions, the
    /// <c>_open</c>/<c>_write</c>/<c>_dup</c> descriptor layer, and the path functions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One file model.</b> Everything here works on the same <see cref="OpenFile"/> objects the
    /// original handlers use, reachable by FILE* (and Win32 handle) in <c>_byPointer</c> and by
    /// descriptor in <c>_byDescriptor</c>. What the CRT keeps per stream beyond that - mode, error
    /// indicator, pushback - lives in <see cref="CrtState"/>, keyed by the open file. A file opened
    /// by a path that records no state (CreateFile2) behaves as a binary stream open for both.
    /// </para>
    /// <para>
    /// <b>Text mode</b> is honoured on the character readers (fgetc, getc, fgets, fgetwc): CR LF
    /// reads as LF and Ctrl+Z is end of file, as the VC11 CRT does. fread and _read stay raw (they
    /// predate this and are what every asset loader uses), and nothing translates on write: the
    /// only reader of a file this runtime writes is this runtime, and a file without CRs reads the
    /// same either way.
    /// </para>
    /// <para>
    /// <b>Invalid parameters.</b> Where the CRT validates an argument with <c>_VALIDATE_RETURN</c>,
    /// the failure goes to <see cref="CrtInvalidParameter"/>: the guest's handler if it installed
    /// one, otherwise the default handler, which ends the process exactly as it would on a phone.
    /// </para>
    /// </remarks>
    public sealed partial class FileLibrary
    {
        private const int CrtEperm = 1, CrtEnoent = 2, CrtEbadf = 9, CrtEnomem = 12, CrtEacces = 13, CrtEexist = 17,
            CrtEinval = 22, CrtErange = 34, CrtEnotempty = 41, CrtEilseq = 42;

        private const long CrtEof = 0xFFFFFFFFL;
        private const long CrtWeof = 0xFFFF;

        // Low-level open flags (fcntl.h).
        private const int ORdOnly = 0, OWrOnly = 1, ORdWr = 2, OAppend = 0x8, OTemporary = 0x40, OCreat = 0x100,
            OTrunc = 0x200, OExcl = 0x400, OText = 0x4000, OBinary = 0x8000, OWText = 0x10000, OU16Text = 0x20000,
            OU8Text = 0x40000;

        private const int SIRead = 0x100, SIWrite = 0x80;

        private enum CrtEncoding
        {
            Ansi,
            Utf8,
            Utf16,
        }

        /// <summary>What the CRT keeps per stream or descriptor beyond the file itself.</summary>
        private sealed class CrtState
        {
            public bool Text;
            public CrtEncoding Encoding;
            public bool CanRead = true;
            public bool CanWrite = true;
            public bool Append;
            public bool Error;
            public bool DeleteOnClose;
            public string? HostPath;

            /// <summary>Bytes the last character read consumed (2 for a translated CR LF), so ungetc can undo it.</summary>
            public int LastReadWidth;

            /// <summary>A pushed-back character that differs from what is in the file, and how many bytes it stands for.</summary>
            public int? Pushback;
            public int PushbackWidth;
        }

        private sealed record CrtMode(
            bool Read,
            bool Write,
            bool Append,
            bool Truncate,
            bool Create,
            bool Exclusive,
            bool Text,
            CrtEncoding Encoding,
            bool DeleteOnClose,
            bool ExplicitTextOrBinary = false);

        private readonly Dictionary<OpenFile, CrtState> _crtStates = new(ReferenceEqualityComparer.Instance);

        /// <summary>stdin, stdout and stderr as descriptors: open, text, on a device with no handle (-2).</summary>
        private readonly bool[] _crtStdOpen = [true, true, true];
        private readonly int[] _crtStdMode = [OText, OText, OText];
        private int _crtUmask;
        private int _crtTempCounter;
        private long _crtTmpnamBuffer;

        /// <summary>
        /// A parameter the CRT would reject with <c>_VALIDATE_RETURN</c>: (function, return value
        /// already in r0, errno). Set by <see cref="HostStubs"/>; it runs the guest's handler or ends
        /// the process. Null only outside a HostStubs (a bare FileLibrary), where it just returns.
        /// </summary>
        internal Action<string, long, int>? CrtInvalidParameter { get; set; }

        /// <summary>The guest's errno, for the rest of the CRT runtime.</summary>
        internal void CrtSetErrno(int value) => _emulator.WriteUInt32(ErrnoAddress(), (uint)value);

        internal int CrtGetErrno() => (int)_emulator.ReadUInt32(ErrnoAddress());

        private void CrtReturnError(long result, int errno)
        {
            CrtSetErrno(errno);
            _frame.Return(result);
        }

        private void CrtInvalid(string function, long result, int errno)
        {
            CrtSetErrno(errno);
            _frame.Return(result);
            CrtInvalidParameter?.Invoke(function, result, errno);
        }

        /// <summary>A wide string argument, or empty for NULL (read lazily, so a NULL never reaches memory).</summary>
        private string CrtWide(int argument) => _frame.Arg(argument) == 0 ? string.Empty : ReadWide(argument);

        private CrtState State(OpenFile file)
        {
            if (!_crtStates.TryGetValue(file, out CrtState? state))
            {
                state = new CrtState();
                _crtStates[file] = state;
            }

            return state;
        }

        private void RegisterCrtStdio(Dictionary<string, Action> handlers)
        {
            // --- opening ---
            handlers["fopen"] = () => FopenHandler("fopen", ReadNarrow(0), ReadNarrow(1), _frame.Arg(0) == 0 || _frame.Arg(1) == 0);
            handlers["_wfopen"] = () => FopenHandler("_wfopen", CrtWide(0), CrtWide(1), _frame.Arg(0) == 0 || _frame.Arg(1) == 0);

            // errno_t fopen_s(FILE** pFile, const char* filename, const char* mode)
            handlers["fopen_s"] = () => FopenS("fopen_s", wide: false);
            handlers["_wfopen_s"] = () => FopenS("_wfopen_s", wide: true);

            // FILE* freopen(path, mode, stream): closes stream, reopens the SAME FILE* on path.
            handlers["freopen"] = () => Freopen("freopen", wide: false);
            handlers["_wfreopen"] = () => Freopen("_wfreopen", wide: true);
            handlers["_fdopen"] = Fdopen;
            handlers["tmpfile"] = Tmpfile;
            handlers["tmpnam"] = Tmpnam;
            handlers["_mktemp"] = Mktemp;

            // --- closing: a descriptor shared by _dup or _fdopen keeps the file open ---
            handlers["fclose"] = Fclose;
            handlers["_close"] = CloseFd;

            // --- the stream state ---
            handlers["ferror"] = () => _frame.Return(Find(_frame.Arg(0)) is { } f && State(f).Error ? 1 : 0);
            handlers["clearerr"] = () =>
            {
                if (_frame.Arg(0) == 0)
                {
                    CrtInvalid("clearerr", 0, CrtEinval);
                    return;
                }

                if (Find(_frame.Arg(0)) is { } f)
                {
                    State(f).Error = false;
                    f.AtEof = false;
                }

                _frame.Return(0);
            };
            handlers["setvbuf"] = Setvbuf;
            handlers["setbuf"] = () =>
            {
                if (_frame.Arg(0) == 0)
                {
                    CrtInvalid("setbuf", 0, CrtEinval);
                    return;
                }

                _frame.Return(0);
            };

            // void _lock_file(FILE*) / _unlock_file(FILE*): a handler runs atomically and guest
            // threads switch only inside a blocking import, so a stream is never shared mid-call.
            handlers["_lock_file"] = () => _frame.Return(0);
            handlers["_unlock_file"] = () => _frame.Return(0);

            // --- characters ---
            handlers["fgetc"] = () => Fgetc("fgetc");
            handlers["getc"] = () => Fgetc("getc");
            handlers["ungetc"] = Ungetc;
            handlers["fgets"] = Fgets;
            handlers["fputc"] = () => Fputc("fputc");
            handlers["putc"] = () => Fputc("putc");
            handlers["fgetwc"] = Fgetwc;
            handlers["fputwc"] = Fputwc;
            handlers["ungetwc"] = Ungetwc;

            // --- positions ---
            handlers["fseek"] = () => FseekCommon("fseek", _frame.SignedArg(1), _frame.SignedArg(2));

            // int _fseeki64(FILE*, __int64 offset, int origin): the offset takes r2:r3, origin is on the stack.
            handlers["_fseeki64"] = () => FseekCommon("_fseeki64", (long)((ulong)(uint)_frame.Arg(2) | ((ulong)(uint)_frame.Arg(3) << 32)), _frame.SignedArg(4));
            handlers["_ftelli64"] = () =>
            {
                if (_frame.Arg(0) == 0)
                {
                    _frame.Return64(-1);
                    CrtInvalid("_ftelli64", CrtEof, CrtEinval);
                    return;
                }

                OpenFile? f = Find(_frame.Arg(0));
                if (f is null)
                {
                    CrtSetErrno(CrtEbadf);
                    _frame.Return64(-1);
                    return;
                }

                _frame.Return64(f.FilePointer);
            };

            // int fgetpos(FILE*, fpos_t*) / fsetpos(FILE*, const fpos_t*): fpos_t is a 64-bit offset.
            handlers["fgetpos"] = () =>
            {
                if (_frame.Arg(0) == 0 || _frame.Arg(1) == 0)
                {
                    CrtInvalid("fgetpos", CrtEof, CrtEinval);
                    return;
                }

                OpenFile? f = Find(_frame.Arg(0));
                if (f is null)
                {
                    CrtReturnError(CrtEof, CrtEbadf);
                    return;
                }

                _emulator.WriteUInt64(_frame.Arg(1), (ulong)f.FilePointer);
                _frame.Return(0);
            };
            handlers["fsetpos"] = () =>
            {
                if (_frame.Arg(0) == 0 || _frame.Arg(1) == 0)
                {
                    CrtInvalid("fsetpos", CrtEof, CrtEinval);
                    return;
                }

                long position = BitConverter.ToInt64(_emulator.ReadMemory(_frame.Arg(1), 8));
                FseekTo("fsetpos", _frame.Arg(0), position, 0);
            };
            handlers["rewind"] = () =>
            {
                if (_frame.Arg(0) == 0)
                {
                    CrtInvalid("rewind", 0, CrtEinval);
                    return;
                }

                if (Find(_frame.Arg(0)) is { } f)
                {
                    ResetPosition(f, 0);
                    State(f).Error = false;
                }

                _frame.Return(0);
            };

            // --- the existing block transfers, made to honour the stream's mode ---
            WrapTransfer(handlers, "fwrite", stream: 3, write: true);
            WrapTransfer(handlers, "fread", stream: 3, write: false);
            WrapTransfer(handlers, "fputs", stream: 1, write: true);
            WrapTransfer(handlers, "fprintf", stream: 0, write: true);
            handlers["_fread_nolock"] = handlers["fread"];

            // std::_Fiopen opens text unless ios_base::binary is asked for.
            foreach (string fiopen in new[] { "?_Fiopen@std@@YAPAU_iobuf@@PBDHH@Z", "?_Fiopen@std@@YAPAU_iobuf@@PB_WHH@Z" })
            {
                if (handlers.TryGetValue(fiopen, out Action? open))
                {
                    handlers[fiopen] = () =>
                    {
                        int openmode = (int)_frame.Arg(1);
                        open();
                        if (Find(_frame.Arg(0)) is { } f)
                        {
                            CrtState s = State(f);
                            s.Text = (openmode & 0x20) == 0;
                            s.CanRead = (openmode & 0x01) != 0;
                            s.CanWrite = (openmode & (0x02 | 0x08)) != 0;
                            s.Append = (openmode & 0x08) != 0;
                        }
                    };
                }
            }

            // --- low-level I/O ---
            handlers["_open"] = () => OpenFd("_open", ReadNarrow(0), _frame.Arg(0) == 0, _frame.SignedArg(1), (int)_frame.Arg(2));
            handlers["?_open@@YAHPBDHH@Z"] = handlers["_open"];
            handlers["_wopen"] = () => OpenFd("_wopen", CrtWide(0), _frame.Arg(0) == 0, _frame.SignedArg(1), (int)_frame.Arg(2));

            // int _creat(const char*, int pmode): create or truncate, for writing.
            handlers["_creat"] = () => OpenFd("_creat", ReadNarrow(0), _frame.Arg(0) == 0, OCreat | OTrunc | ORdWr, (int)_frame.Arg(1));
            handlers["_write"] = WriteFd;

            // __int64 _lseeki64(int fd, __int64 offset, int origin): offset in r2:r3, origin on the stack.
            handlers["_lseeki64"] = () => SeekFd("_lseeki64", _frame.SignedArg(0),
                (long)((ulong)(uint)_frame.Arg(2) | ((ulong)(uint)_frame.Arg(3) << 32)), _frame.SignedArg(4), wide: true);
            handlers["_lseek"] = () => SeekFd("_lseek", _frame.SignedArg(0), _frame.SignedArg(1), _frame.SignedArg(2), wide: false);
            handlers["_telli64"] = () => SeekFd("_telli64", _frame.SignedArg(0), 0, 1, wide: true);
            handlers["_dup"] = Dup;
            handlers["_dup2"] = Dup2;
            handlers["_setmode"] = Setmode;
            handlers["_locking"] = Locking;

            // --- paths ---
            handlers["rename"] = () => Rename("rename", ReadNarrow(0), ReadNarrow(1), _frame.Arg(0) == 0 || _frame.Arg(1) == 0);
            handlers["_wrename"] = () => Rename("_wrename", CrtWide(0), CrtWide(1), _frame.Arg(0) == 0 || _frame.Arg(1) == 0);
            handlers["_access"] = () => Access("_access", ReadNarrow(0), _frame.Arg(0) == 0, _frame.SignedArg(1));
            handlers["_waccess"] = () => Access("_waccess", CrtWide(0), _frame.Arg(0) == 0, _frame.SignedArg(1));
            handlers["_chmod"] = Chmod;
            handlers["_umask"] = () =>
            {
                int mode = _frame.SignedArg(0);
                if ((mode & ~(SIRead | SIWrite)) != 0)
                {
                    CrtInvalid("_umask", CrtEinval, CrtEinval);
                    return;
                }

                int old = _crtUmask;
                _crtUmask = mode;
                _frame.Return(old);
            };
            handlers["_wmkdir"] = () => MakeDirectory(CrtWide(0), _frame.Arg(0) == 0);
            handlers["_mkdir"] = () => MakeDirectory(ReadNarrow(0), _frame.Arg(0) == 0);
            handlers["_rmdir"] = () => RemoveDirectoryCrt(ReadNarrow(0), _frame.Arg(0) == 0);
            handlers["_utime64"] = Utime64;
            handlers["_fullpath"] = FullPath;
            handlers["_splitpath"] = () => SplitPath(secure: false);
            handlers["_splitpath_s"] = () => SplitPath(secure: true);
            handlers["_makepath_s"] = MakePathS;
        }

        // =======================================================================================
        // Opening
        // =======================================================================================

        /// <summary>
        /// Parses an fopen mode: r/w/a, then any of + t b c n S R T D N and spaces, then optionally
        /// ",ccs=UTF-8|UTF-16LE|UNICODE". Null when the CRT would reject it.
        /// </summary>
        private static CrtMode? ParseStdioMode(string mode)
        {
            int i = 0;
            while (i < mode.Length && mode[i] == ' ')
            {
                i++;
            }

            if (i >= mode.Length)
            {
                return null;
            }

            bool read, write, append = false, truncate = false, create = false;
            switch (mode[i])
            {
                case 'r': read = true; write = false; break;
                case 'w': read = false; write = true; truncate = true; create = true; break;
                case 'a': read = false; write = true; append = true; create = true; break;
                default: return null;
            }

            bool plus = false, textOrBinary = false, text = true, commit = false, hint = false, shortLived = false, delete = false;
            CrtEncoding encoding = CrtEncoding.Ansi;
            bool ccs = false;
            for (i++; i < mode.Length; i++)
            {
                char c = mode[i];
                switch (c)
                {
                    case '+' when !plus: plus = true; read = true; write = true; break;
                    case 't' when !textOrBinary: textOrBinary = true; text = true; break;
                    case 'b' when !textOrBinary: textOrBinary = true; text = false; break;
                    case 'c' or 'n' when !commit: commit = true; break;
                    case 'S' or 'R' when !hint: hint = true; break;
                    case 'T' when !shortLived: shortLived = true; break;
                    case 'D' when !delete: delete = true; break;
                    case 'N': break;
                    case ' ': break;
                    case ',':
                    {
                        string rest = mode[(i + 1)..].Replace(" ", string.Empty);
                        if (!rest.StartsWith("ccs=", StringComparison.Ordinal) || (textOrBinary && !text))
                        {
                            return null;
                        }

                        string name = rest[4..];
                        encoding = name.ToUpperInvariant() switch
                        {
                            "UTF-8" => CrtEncoding.Utf8,
                            "UTF-16LE" or "UNICODE" => CrtEncoding.Utf16,
                            _ => (CrtEncoding)(-1),
                        };
                        if ((int)encoding < 0)
                        {
                            return null;
                        }

                        ccs = true;
                        i = mode.Length;
                        break;
                    }

                    default:
                        return null;
                }
            }

            _ = ccs;
            return new CrtMode(read, write, append, truncate, create, Exclusive: false, text, encoding, delete, textOrBinary);
        }

        /// <summary>
        /// Opens <paramref name="path"/> to <paramref name="mode"/>. The mapping is the one every
        /// other open uses: the local folder is the sandbox, the package is read-only, and a write to
        /// a packaged file goes to a sandbox copy of it (copied first unless the open truncates).
        /// </summary>
        private (OpenFile? File, int Errno) CrtOpen(string path, CrtMode mode, string caller)
        {
            string host = Resolve(path, out bool writable);
            if (host.Length == 0)
            {
                return (null, CrtEnoent);
            }

            try
            {
                if (Directory.Exists(host))
                {
                    return (null, CrtEacces);
                }

                bool exists = File.Exists(host);
                if (mode.Write && !writable)
                {
                    string redirect = Path.Combine(_writeRoot, Path.GetFileName(host));
                    if (exists && !mode.Truncate && !File.Exists(redirect))
                    {
                        File.Copy(host, redirect);
                    }

                    host = redirect;
                    exists = File.Exists(host);
                }

                if (!exists && !mode.Create)
                {
                    return (null, CrtEnoent);
                }

                if (exists && mode.Exclusive)
                {
                    return (null, CrtEexist);
                }

                if (exists && mode.Write && new FileInfo(host).IsReadOnly)
                {
                    return (null, CrtEacces);
                }

                if (!exists && !EnsureParent(host))
                {
                    return (null, CrtEnoent);
                }

                FileMode fileMode = !exists ? FileMode.CreateNew : mode.Truncate ? FileMode.Truncate : FileMode.Open;
                FileStream stream = new(
                    host,
                    fileMode,
                    mode.Write ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    mode.DeleteOnClose ? FileOptions.DeleteOnClose : FileOptions.None);

                OpenFile file = new() { Stream = stream, Path = path, Descriptor = _nextDescriptor++ };
                _byDescriptor[file.Descriptor] = file;

                CrtState state = State(file);
                state.Text = mode.Text;
                state.CanRead = mode.Read;
                state.CanWrite = mode.Write;
                state.Append = mode.Append;
                state.DeleteOnClose = mode.DeleteOnClose;
                state.HostPath = host;
                state.Encoding = SettleEncoding(file, mode);

                OpenedSuccessfully++;
                Log.Add($"{caller}(\"{path}\") -> {stream.Length} bytes");
                return (file, 0);
            }
            catch (UnauthorizedAccessException)
            {
                OpenFailed++;
                Log.Add($"{caller}(\"{path}\") -> access denied");
                return (null, CrtEacces);
            }
            catch (DirectoryNotFoundException)
            {
                OpenFailed++;
                return (null, CrtEnoent);
            }
            catch (FileNotFoundException)
            {
                OpenFailed++;
                return (null, CrtEnoent);
            }
            catch (IOException ex)
            {
                OpenFailed++;
                Log.Add($"{caller}(\"{path}\") -> {ex.GetType().Name}");
                return (null, CrtEacces);
            }
        }

        /// <summary>
        /// A file is only ever created in a folder that exists. A folder the package has but the
        /// sandbox does not is mirrored, since a write into the package lands in the sandbox; any
        /// other missing folder is ENOENT, as on a phone.
        /// </summary>
        private bool EnsureParent(string host)
        {
            string? parent = Path.GetDirectoryName(host);
            if (parent is null || Directory.Exists(parent))
            {
                return true;
            }

            string relative = Path.GetRelativePath(_writeRoot, parent);
            if (relative.StartsWith("..", StringComparison.Ordinal) || !Directory.Exists(Path.Combine(_readRoot, relative)))
            {
                return false;
            }

            Directory.CreateDirectory(parent);
            return true;
        }

        /// <summary>
        /// A ccs= stream's encoding: a byte-order mark on an existing file wins (and is skipped), a
        /// new or truncated file written as Unicode gets one, and UNICODE with no mark reads as ANSI.
        /// </summary>
        private CrtEncoding SettleEncoding(OpenFile file, CrtMode mode)
        {
            if (mode.Encoding == CrtEncoding.Ansi)
            {
                return CrtEncoding.Ansi;
            }

            FileStream stream = file.Stream;
            if (stream.Length == 0)
            {
                if (mode.Write)
                {
                    byte[] bom = mode.Encoding == CrtEncoding.Utf8 ? [0xEF, 0xBB, 0xBF] : [0xFF, 0xFE];
                    stream.Position = 0;
                    stream.Write(bom);
                    file.FilePointer = bom.Length;
                }

                return mode.Encoding;
            }

            byte[] head = new byte[3];
            stream.Position = 0;
            int got = stream.Read(head, 0, 3);
            CrtEncoding found;
            int skip;
            if (got >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                (found, skip) = (CrtEncoding.Utf8, 3);
            }
            else if (got >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            {
                (found, skip) = (CrtEncoding.Utf16, 2);
            }
            else
            {
                (found, skip) = (mode.Encoding == CrtEncoding.Utf16 && mode.Read ? CrtEncoding.Ansi : mode.Encoding, 0);
            }

            file.FilePointer = mode.Append ? stream.Length : skip;
            return found;
        }

        private long RegisterStream(OpenFile file)
        {
            long pointer = _emulator.AllocateHeap(32);
            _emulator.WriteMemory(pointer, new byte[32]);
            _byPointer[pointer] = file;
            return pointer;
        }

        private void FopenHandler(string caller, string path, string modeText, bool nullArgument)
        {
            if (nullArgument)
            {
                CrtInvalid(caller, 0, CrtEinval);
                return;
            }

            CrtMode? mode = ParseStdioMode(modeText);
            if (mode is null || path.Length == 0)
            {
                CrtInvalid(caller, 0, CrtEinval);
                return;
            }

            (OpenFile? file, int errno) = CrtOpen(path, mode, caller);
            if (file is null)
            {
                Log.Add($"{caller}(\"{path}\", \"{modeText}\") -> errno {errno}");
                CrtReturnError(0, errno);
                return;
            }

            _frame.Return(RegisterStream(file));
        }

        private void FopenS(string caller, bool wide)
        {
            long destination = _frame.Arg(0);
            if (destination == 0)
            {
                CrtInvalid(caller, CrtEinval, CrtEinval);
                return;
            }

            _emulator.WriteUInt32(destination, 0);
            if (_frame.Arg(1) == 0 || _frame.Arg(2) == 0)
            {
                CrtInvalid(caller, CrtEinval, CrtEinval);
                return;
            }

            string path = wide ? CrtWide(1) : ReadNarrow(1);
            string modeText = wide ? CrtWide(2) : ReadNarrow(2);
            CrtMode? mode = ParseStdioMode(modeText);
            if (mode is null || path.Length == 0)
            {
                CrtInvalid(caller, CrtEinval, CrtEinval);
                return;
            }

            (OpenFile? file, int errno) = CrtOpen(path, mode, caller);
            if (file is null)
            {
                CrtReturnError(errno, errno);
                return;
            }

            _emulator.WriteUInt32(destination, (uint)RegisterStream(file));
            _frame.Return(0);
        }

        private void Freopen(string caller, bool wide)
        {
            long stream = _frame.Arg(2);
            if (stream == 0 || _frame.Arg(0) == 0 || _frame.Arg(1) == 0)
            {
                CrtInvalid(caller, 0, CrtEinval);
                return;
            }

            string path = wide ? CrtWide(0) : ReadNarrow(0);
            string modeText = wide ? CrtWide(1) : ReadNarrow(1);
            CrtMode? mode = ParseStdioMode(modeText);
            if (mode is null)
            {
                CrtInvalid(caller, 0, CrtEinval);
                return;
            }

            // The old file is closed whether or not the new open succeeds.
            if (_byPointer.Remove(stream, out OpenFile? old))
            {
                Release(old, old.Descriptor);
            }

            (OpenFile? file, int errno) = CrtOpen(path, mode, caller);
            if (file is null)
            {
                CrtReturnError(0, errno);
                return;
            }

            _byPointer[stream] = file;
            _frame.Return(stream);
        }

        /// <summary>FILE* _fdopen(int fd, const char* mode): a stream over an open descriptor.</summary>
        private void Fdopen()
        {
            int fd = _frame.SignedArg(0);
            if (_frame.Arg(1) == 0)
            {
                CrtInvalid("_fdopen", 0, CrtEinval);
                return;
            }

            CrtMode? mode = ParseStdioMode(ReadNarrow(1));
            if (mode is null)
            {
                CrtInvalid("_fdopen", 0, CrtEinval);
                return;
            }

            if (!_byDescriptor.TryGetValue(fd, out OpenFile? file))
            {
                if (fd is >= 0 and <= 2 && _crtStdOpen[fd])
                {
                    // A stream over the console device, which an app does not have.
                    CrtReturnError(0, CrtEbadf);
                    return;
                }

                BadDescriptor("_fdopen", 0);
                return;
            }

            CrtState state = State(file);
            if (mode.ExplicitTextOrBinary)
            {
                state.Text = mode.Text;
            }

            _frame.Return(RegisterStream(file));
        }

        /// <summary>FILE* tmpfile(void): a new w+b file, removed when it is closed.</summary>
        private void Tmpfile()
        {
            string name;
            do
            {
                name = $"t{++_crtTempCounter:x}.tmp";
            }
            while (File.Exists(Path.Combine(_writeRoot, name)));

            CrtMode mode = new(true, true, false, true, true, true, false, CrtEncoding.Ansi, DeleteOnClose: true);
            (OpenFile? file, int errno) = CrtOpen(Path.Combine(_localFolderPrefix, name), mode, "tmpfile");
            if (file is null)
            {
                CrtReturnError(0, errno);
                return;
            }

            _frame.Return(RegisterStream(file));
        }

        /// <summary>
        /// char* tmpnam(char* buffer): a name not yet used, of the VC11 form "\sXXX.N" (relative to
        /// the current folder). With a null buffer the CRT's own static one is used.
        /// </summary>
        private void Tmpnam()
        {
            long buffer = _frame.Arg(0);
            string name;
            do
            {
                name = $"\\s{0x2000:x}.{Convert.ToString(++_crtTempCounter, 16)}";
            }
            while (ExistingHostPath(name) is not null);

            if (buffer == 0)
            {
                if (_crtTmpnamBuffer == 0)
                {
                    _crtTmpnamBuffer = _emulator.AllocateHeap(260);
                }

                buffer = _crtTmpnamBuffer;
            }

            _frame.WriteNarrowString(buffer, name);
            _frame.Return(buffer);
        }

        /// <summary>
        /// char* _mktemp(char* template): the trailing six X become a letter and five digits naming a
        /// file that does not exist. NULL (EINVAL) for a malformed template, or when all 26 letters
        /// are taken.
        /// </summary>
        private void Mktemp()
        {
            long template = _frame.Arg(0);
            if (template == 0)
            {
                CrtInvalid("_mktemp", 0, CrtEinval);
                return;
            }

            string text = ReadNarrow(0);
            if (text.Length < 6 || !text.EndsWith("XXXXXX", StringComparison.Ordinal))
            {
                CrtReturnError(0, CrtEinval);
                return;
            }

            string stem = text[..^6];
            string digits = ((0x1000 + _emulator.CurrentThreadId) % 100000).ToString("D5");
            for (char letter = 'a'; letter <= 'z'; letter++)
            {
                string candidate = stem + letter + digits;
                if (ExistingHostPath(candidate) is null)
                {
                    _frame.WriteNarrowString(template, candidate);
                    _frame.Return(template);
                    return;
                }
            }

            _emulator.WriteMemory(template, [0]);
            CrtReturnError(0, CrtEexist);
        }

        // =======================================================================================
        // Closing
        // =======================================================================================

        /// <summary>Whether anything still refers to <paramref name="file"/> by pointer or descriptor.</summary>
        private bool StillReferenced(OpenFile file) =>
            _byPointer.Values.Any(f => ReferenceEquals(f, file)) || _byDescriptor.Values.Any(f => ReferenceEquals(f, file));

        /// <summary>Drops one descriptor's hold on a file, closing it when nothing else holds it.</summary>
        private void Release(OpenFile file, int descriptor)
        {
            if (_byDescriptor.TryGetValue(descriptor, out OpenFile? held) && ReferenceEquals(held, file))
            {
                _byDescriptor.Remove(descriptor);
            }

            if (StillReferenced(file))
            {
                return;
            }

            try
            {
                file.Stream.Dispose();
            }
            catch (IOException)
            {
            }

            _crtStates.Remove(file);
        }

        /// <summary>int fclose(FILE*): 0, or EOF for a stream that is not open.</summary>
        private void Fclose()
        {
            long pointer = _frame.Arg(0);
            if (pointer == 0)
            {
                CrtInvalid("fclose", CrtEof, CrtEinval);
                return;
            }

            if (!_byPointer.Remove(pointer, out OpenFile? file))
            {
                CrtReturnError(CrtEof, CrtEbadf);
                return;
            }

            Release(file, file.Descriptor);
            _frame.Return(0);
        }

        private void BadDescriptor(string function, long result)
        {
            CrtInvalid(function, result, CrtEbadf);
        }

        /// <summary>int _close(int fd): 0, or -1 with EBADF.</summary>
        private void CloseFd()
        {
            int fd = _frame.SignedArg(0);
            if (fd == -2)
            {
                CrtReturnError(CrtEof, CrtEbadf);
                return;
            }

            if (fd is >= 0 and <= 2 && !_byDescriptor.ContainsKey(fd))
            {
                if (!_crtStdOpen[fd])
                {
                    BadDescriptor("_close", CrtEof);
                    return;
                }

                _crtStdOpen[fd] = false;
                _frame.Return(0);
                return;
            }

            if (!_byDescriptor.TryGetValue(fd, out OpenFile? file))
            {
                BadDescriptor("_close", CrtEof);
                return;
            }

            Release(file, fd);
            _frame.Return(0);
        }

        // =======================================================================================
        // Stream state and characters
        // =======================================================================================

        /// <summary>int setvbuf(FILE*, char* buffer, int mode, size_t size). There is no buffer to configure.</summary>
        private void Setvbuf()
        {
            const int ioFbf = 0x0000, ioLbf = 0x0040, ioNbf = 0x0004;
            int mode = _frame.SignedArg(2);
            long size = _frame.Arg(3);
            if (_frame.Arg(0) == 0 || mode is not (ioFbf or ioLbf or ioNbf) ||
                (mode != ioNbf && (size < 2 || size > int.MaxValue)))
            {
                CrtInvalid("setvbuf", CrtEof, CrtEinval);
                return;
            }

            _frame.Return(Find(_frame.Arg(0)) is null ? CrtEof : 0);
        }

        private int ReadRawByte(OpenFile file)
        {
            file.Stream.Position = file.FilePointer;
            int b = file.Stream.ReadByte();
            if (b >= 0)
            {
                file.FilePointer++;
            }

            return b;
        }

        private int PeekRawByte(OpenFile file)
        {
            file.Stream.Position = file.FilePointer;
            return file.Stream.ReadByte();
        }

        /// <summary>One character: pushback first, then the file, CR LF read as LF and Ctrl+Z as end in text mode.</summary>
        private int ReadChar(OpenFile file, CrtState state)
        {
            if (state.Pushback is int pushed)
            {
                state.Pushback = null;
                file.FilePointer += state.PushbackWidth;
                state.LastReadWidth = state.PushbackWidth;
                return pushed & 0xFF;
            }

            if (!state.CanRead)
            {
                state.Error = true;
                CrtSetErrno(CrtEbadf);
                return -1;
            }

            long start = file.FilePointer;
            int b = ReadRawByte(file);
            if (b < 0)
            {
                file.AtEof = true;
                state.LastReadWidth = 0;
                return -1;
            }

            if (state.Text)
            {
                if (b == 0x1A)
                {
                    file.FilePointer = start;
                    file.AtEof = true;
                    state.LastReadWidth = 0;
                    return -1;
                }

                if (b == '\r' && PeekRawByte(file) == '\n')
                {
                    file.FilePointer++;
                    b = '\n';
                }
            }

            state.LastReadWidth = (int)(file.FilePointer - start);
            return b;
        }

        private void Fgetc(string caller)
        {
            if (_frame.Arg(0) == 0)
            {
                CrtInvalid(caller, CrtEof, CrtEinval);
                return;
            }

            OpenFile? file = Find(_frame.Arg(0));
            if (file is null)
            {
                CrtReturnError(CrtEof, CrtEbadf);
                return;
            }

            int c = ReadChar(file, State(file));
            _frame.Return(c < 0 ? CrtEof : c);
        }

        /// <summary>
        /// int ungetc(int c, FILE*). When c is the character just read the file position simply
        /// steps back over it, so every reader (fread included) sees it again; a different c is held
        /// as pushback for the character readers. One character of pushback, as the standard promises.
        /// </summary>
        private void Ungetc()
        {
            int c = _frame.SignedArg(0);
            if (_frame.Arg(1) == 0)
            {
                CrtInvalid("ungetc", CrtEof, CrtEinval);
                return;
            }

            OpenFile? file = Find(_frame.Arg(1));
            if (c == -1 || file is null)
            {
                _frame.Return(CrtEof);
                return;
            }

            CrtState state = State(file);
            if (state.Pushback is not null)
            {
                _frame.Return(CrtEof);
                return;
            }

            byte value = (byte)c;
            int width = state.LastReadWidth > 0 ? state.LastReadWidth : file.FilePointer > 0 ? 1 : 0;
            long back = file.FilePointer - width;
            bool same = width switch
            {
                1 => ReadAt(file, back) == value,
                2 => value == '\n',
                _ => false,
            };

            file.FilePointer = back;
            state.LastReadWidth = 0;
            if (!same)
            {
                state.Pushback = value;
                state.PushbackWidth = width;
            }

            file.AtEof = false;
            _frame.Return(value);
        }

        private int ReadAt(OpenFile file, long position)
        {
            file.Stream.Position = position;
            return file.Stream.ReadByte();
        }

        /// <summary>char* fgets(char* buffer, int count, FILE*): up to count-1 characters, through the newline.</summary>
        private void Fgets()
        {
            long buffer = _frame.Arg(0);
            int count = _frame.SignedArg(1);
            if ((buffer == 0 && count != 0) || count < 0 || _frame.Arg(2) == 0)
            {
                CrtInvalid("fgets", 0, CrtEinval);
                return;
            }

            if (count == 0)
            {
                _frame.Return(0);
                return;
            }

            OpenFile? file = Find(_frame.Arg(2));
            if (file is null)
            {
                CrtReturnError(0, CrtEbadf);
                return;
            }

            CrtState state = State(file);
            List<byte> line = new();
            while (line.Count < count - 1)
            {
                int c = ReadChar(file, state);
                if (c < 0)
                {
                    if (line.Count == 0 || state.Error)
                    {
                        _frame.Return(0);
                        return;
                    }

                    break;
                }

                line.Add((byte)c);
                if (c == '\n')
                {
                    break;
                }
            }

            line.Add(0);
            _emulator.WriteMemory(buffer, line.ToArray());
            _frame.Return(buffer);
        }

        /// <summary>Writes through the stream's mode: append goes to the end, a read-only stream fails.</summary>
        private bool WriteBytes(OpenFile file, CrtState state, byte[] bytes)
        {
            if (!state.CanWrite)
            {
                state.Error = true;
                CrtSetErrno(CrtEbadf);
                return false;
            }

            try
            {
                if (state.Append)
                {
                    file.FilePointer = file.Stream.Length;
                }

                file.Stream.Position = file.FilePointer;
                file.Stream.Write(bytes);
                file.FilePointer += bytes.Length;
                state.LastReadWidth = 0;
                state.Pushback = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
            {
                state.Error = true;
                CrtSetErrno(CrtEacces);
                return false;
            }
        }

        private void Fputc(string caller)
        {
            int c = _frame.SignedArg(0);
            if (_frame.Arg(1) == 0)
            {
                CrtInvalid(caller, CrtEof, CrtEinval);
                return;
            }

            OpenFile? file = Find(_frame.Arg(1));
            if (file is null)
            {
                TraceIfStandardOutput(_frame.Arg(1), () => ((char)(byte)c).ToString());
                CrtReturnError(CrtEof, CrtEbadf);
                return;
            }

            _frame.Return(WriteBytes(file, State(file), [(byte)c]) ? (byte)c : CrtEof);
        }

        /// <summary>
        /// wint_t fgetwc(FILE*): two raw bytes in binary mode; in text mode one character of the
        /// stream's encoding - ANSI bytes map to themselves (the "C" locale), UTF-8 and UTF-16LE are
        /// decoded - with CR LF read as LF.
        /// </summary>
        private void Fgetwc()
        {
            if (_frame.Arg(0) == 0)
            {
                CrtInvalid("fgetwc", CrtWeof, CrtEinval);
                return;
            }

            OpenFile? file = Find(_frame.Arg(0));
            if (file is null)
            {
                CrtReturnError(CrtWeof, CrtEbadf);
                return;
            }

            CrtState state = State(file);
            int c = ReadWideChar(file, state);
            _frame.Return(c < 0 ? CrtWeof : c);
        }

        private int ReadWideChar(OpenFile file, CrtState state)
        {
            if (state.Pushback is int pushed)
            {
                state.Pushback = null;
                file.FilePointer += state.PushbackWidth;
                state.LastReadWidth = state.PushbackWidth;
                return pushed & 0xFFFF;
            }

            if (state.Text && state.Encoding == CrtEncoding.Ansi)
            {
                return ReadChar(file, state);
            }

            if (!state.CanRead)
            {
                state.Error = true;
                CrtSetErrno(CrtEbadf);
                return -1;
            }

            long start = file.FilePointer;
            int unit;
            if (!state.Text || state.Encoding == CrtEncoding.Utf16)
            {
                unit = ReadUtf16Unit(file);
                if (unit >= 0 && state.Text && unit == '\r')
                {
                    long afterCr = file.FilePointer;
                    if (ReadUtf16Unit(file) == '\n')
                    {
                        unit = '\n';
                    }
                    else
                    {
                        file.FilePointer = afterCr;
                    }
                }
            }
            else
            {
                unit = ReadUtf8Char(file);
                if (unit == '\r')
                {
                    long afterCr = file.FilePointer;
                    if (ReadRawByte(file) == '\n')
                    {
                        unit = '\n';
                    }
                    else
                    {
                        file.FilePointer = afterCr;
                    }
                }
            }

            if (unit < 0)
            {
                file.FilePointer = start;
                file.AtEof = true;
                state.LastReadWidth = 0;
                return -1;
            }

            state.LastReadWidth = (int)(file.FilePointer - start);
            return unit;
        }

        private int ReadUtf16Unit(OpenFile file)
        {
            int low = ReadRawByte(file);
            int high = low < 0 ? -1 : ReadRawByte(file);
            return high < 0 ? -1 : low | (high << 8);
        }

        private int ReadUtf8Char(OpenFile file)
        {
            int first = ReadRawByte(file);
            if (first < 0x80)
            {
                return first;
            }

            int extra = first >= 0xF0 ? 3 : first >= 0xE0 ? 2 : first >= 0xC0 ? 1 : 0;
            int value = first & (0x3F >> extra);
            for (int i = 0; i < extra; i++)
            {
                int next = ReadRawByte(file);
                if (next < 0)
                {
                    return -1;
                }

                value = (value << 6) | (next & 0x3F);
            }

            return value > 0xFFFF ? 0xFFFD : value;
        }

        /// <summary>wint_t fputwc(wchar_t, FILE*): the stream's encoding; a character with no ANSI byte is EILSEQ.</summary>
        private void Fputwc()
        {
            int c = (int)(_frame.Arg(0) & 0xFFFF);
            if (_frame.Arg(1) == 0)
            {
                CrtInvalid("fputwc", CrtWeof, CrtEinval);
                return;
            }

            OpenFile? file = Find(_frame.Arg(1));
            if (file is null)
            {
                CrtReturnError(CrtWeof, CrtEbadf);
                return;
            }

            CrtState state = State(file);
            byte[] bytes;
            if (!state.Text || state.Encoding == CrtEncoding.Utf16)
            {
                bytes = [(byte)c, (byte)(c >> 8)];
            }
            else if (state.Encoding == CrtEncoding.Utf8)
            {
                bytes = Encoding.UTF8.GetBytes(((char)c).ToString());
            }
            else if (c <= 0xFF)
            {
                bytes = [(byte)c];
            }
            else
            {
                CrtReturnError(CrtWeof, CrtEilseq);
                return;
            }

            _frame.Return(WriteBytes(file, state, bytes) ? c : CrtWeof);
        }

        private void Ungetwc()
        {
            int c = (int)(_frame.Arg(0) & 0xFFFF);
            if (_frame.Arg(1) == 0)
            {
                CrtInvalid("ungetwc", CrtWeof, CrtEinval);
                return;
            }

            OpenFile? file = Find(_frame.Arg(1));
            if (c == 0xFFFF || file is null)
            {
                _frame.Return(CrtWeof);
                return;
            }

            CrtState state = State(file);
            if (state.Pushback is not null)
            {
                _frame.Return(CrtWeof);
                return;
            }

            int width = state.LastReadWidth;
            long back = file.FilePointer - width;
            bool same = false;
            if (width > 0)
            {
                long saved = file.FilePointer;
                file.FilePointer = back;
                CrtState probe = new() { Text = state.Text, Encoding = state.Encoding };
                same = ReadWideChar(file, probe) == c;
                file.FilePointer = saved;
            }

            file.FilePointer = back;
            state.LastReadWidth = 0;
            if (!same)
            {
                state.Pushback = c;
                state.PushbackWidth = width;
            }

            file.AtEof = false;
            _frame.Return(c);
        }

        // =======================================================================================
        // Positions
        // =======================================================================================

        private void ResetPosition(OpenFile file, long position)
        {
            file.FilePointer = position;
            file.AtEof = false;
            CrtState state = State(file);
            state.Pushback = null;
            state.LastReadWidth = 0;
        }

        private void FseekCommon(string caller, long offset, int origin)
        {
            if (_frame.Arg(0) == 0 || origin is < 0 or > 2)
            {
                CrtInvalid(caller, CrtEof, CrtEinval);
                return;
            }

            FseekTo(caller, _frame.Arg(0), offset, origin);
        }

        /// <summary>A seek: SEEK_SET 0, SEEK_CUR 1, SEEK_END 2. Negative is EINVAL; past the end is legal.</summary>
        private void FseekTo(string caller, long stream, long offset, int origin)
        {
            OpenFile? file = Find(stream);
            if (file is null)
            {
                CrtReturnError(CrtEof, CrtEbadf);
                return;
            }

            long target = origin switch
            {
                1 => file.FilePointer + offset,
                2 => file.Stream.Length + offset,
                _ => offset,
            };

            if (target < 0)
            {
                CrtReturnError(CrtEof, CrtEinval);
                return;
            }

            ResetPosition(file, target);
            _frame.Return(0);
        }

        /// <summary>
        /// Wraps an existing block transfer so it honours the stream: a write to a read-only stream
        /// or a read from a write-only one fails and sets the error indicator, an append stream
        /// writes at the end, and a character held back by ungetc is not lost.
        /// </summary>
        private void WrapTransfer(Dictionary<string, Action> handlers, string name, int stream, bool write)
        {
            if (!handlers.TryGetValue(name, out Action? original))
            {
                return;
            }

            handlers[name] = () =>
            {
                OpenFile? file = Find(_frame.Arg(stream));
                if (file is null || !_crtStates.TryGetValue(file, out CrtState? state))
                {
                    original();
                    return;
                }

                if (write ? !state.CanWrite : !state.CanRead)
                {
                    state.Error = true;
                    CrtSetErrno(CrtEbadf);
                    _frame.Return(write && name != "fwrite" ? CrtEof : 0);
                    return;
                }

                if (write && state.Append)
                {
                    file.FilePointer = file.Stream.Length;
                }

                int? pushed = write ? null : state.Pushback;
                int pushedWidth = state.PushbackWidth;
                long buffer = _frame.Arg(0);
                state.Pushback = null;
                state.LastReadWidth = 0;
                original();

                // fread read the byte the pushback stands in for; the caller asked for the pushback.
                if (pushed is int c && pushedWidth == 1 && _frame.Arg(0) != 0)
                {
                    _emulator.WriteMemory(buffer, [(byte)c]);
                }
            };
        }

        // =======================================================================================
        // Low-level I/O
        // =======================================================================================

        private void OpenFd(string caller, string path, bool nullPath, int flags, int pmode)
        {
            if (nullPath)
            {
                CrtInvalid(caller, CrtEof, CrtEinval);
                return;
            }

            int access = flags & 3;
            if (access == 3)
            {
                CrtReturnError(CrtEof, CrtEinval);
                return;
            }

            int textFlags = flags & (OText | OBinary | OWText | OU16Text | OU8Text);
            bool text = (flags & OBinary) == 0;
            CrtEncoding encoding = (flags & OU8Text) != 0 ? CrtEncoding.Utf8
                : (flags & (OU16Text | OWText)) != 0 ? CrtEncoding.Utf16
                : CrtEncoding.Ansi;
            if (textFlags != 0 && (textFlags & (textFlags - 1)) != 0)
            {
                CrtReturnError(CrtEof, CrtEinval);
                return;
            }

            bool create = (flags & OCreat) != 0;
            CrtMode mode = new(
                Read: access is ORdOnly or ORdWr,
                Write: access is OWrOnly or ORdWr,
                Append: (flags & OAppend) != 0,
                Truncate: (flags & OTrunc) != 0,
                Create: create,
                Exclusive: create && (flags & OExcl) != 0,
                Text: text,
                Encoding: encoding,
                DeleteOnClose: (flags & OTemporary) != 0);

            if (mode.Truncate && !mode.Write)
            {
                CrtReturnError(CrtEof, CrtEacces);
                return;
            }

            string before = Resolve(path, out _);
            bool existed = before.Length > 0 && File.Exists(before);
            (OpenFile? file, int errno) = CrtOpen(path, mode, caller);
            if (file is null)
            {
                CrtReturnError(CrtEof, errno);
                return;
            }

            // A file this call created takes pmode, less the umask: no _S_IWRITE means read-only.
            // Bits outside _S_IREAD | _S_IWRITE are ignored (ported code passes Unix octal modes).
            if (!existed && create && (pmode & ~_crtUmask & SIWrite) == 0 && State(file).HostPath is { } host)
            {
                try
                {
                    File.SetAttributes(host, File.GetAttributes(host) | FileAttributes.ReadOnly);
                }
                catch (IOException)
                {
                }
            }

            _frame.Return(file.Descriptor);
        }

        /// <summary>int _write(int fd, const void* buffer, unsigned count): count, or -1 with errno.</summary>
        private void WriteFd()
        {
            int fd = _frame.SignedArg(0);
            long buffer = _frame.Arg(1);
            long count = _frame.Arg(2);
            if (fd == -2)
            {
                CrtReturnError(CrtEof, CrtEbadf);
                return;
            }

            if (!_byDescriptor.TryGetValue(fd, out OpenFile? file))
            {
                if (fd is >= 0 and <= 2 && _crtStdOpen[fd])
                {
                    // The console device has no handle in an app; WriteFile fails.
                    CrtReturnError(CrtEof, CrtEbadf);
                    return;
                }

                BadDescriptor("_write", CrtEof);
                return;
            }

            if (count == 0)
            {
                _frame.Return(0);
                return;
            }

            if (buffer == 0)
            {
                CrtInvalid("_write", CrtEof, CrtEinval);
                return;
            }

            CrtState state = State(file);
            if (state.Text && state.Encoding != CrtEncoding.Ansi && count % 2 != 0)
            {
                CrtInvalid("_write", CrtEof, CrtEinval);
                return;
            }

            if (count > CallFrame.SaneLengthLimit)
            {
                CrtReturnError(CrtEof, CrtEinval);
                return;
            }

            _frame.Return(WriteBytes(file, state, _emulator.ReadMemory(buffer, (int)count)) ? count : CrtEof);
        }

        private void SeekFd(string caller, int fd, long offset, int origin, bool wide)
        {
            void Answer(long value)
            {
                if (wide)
                {
                    _frame.Return64(value);
                }
                else
                {
                    _frame.Return(value & 0xFFFFFFFFL);
                }
            }

            // The answer goes in first: an invalid-parameter handler call is set up after it.
            if (!_byDescriptor.TryGetValue(fd, out OpenFile? file))
            {
                Answer(-1);
                if (fd == -2 || (fd is >= 0 and <= 2 && _crtStdOpen[fd]))
                {
                    CrtSetErrno(CrtEbadf);
                }
                else
                {
                    BadDescriptor(caller, CrtEof);
                }

                return;
            }

            if (origin is < 0 or > 2)
            {
                Answer(-1);
                CrtInvalid(caller, CrtEof, CrtEinval);
                return;
            }

            long target = origin switch
            {
                1 => file.FilePointer + offset,
                2 => file.Stream.Length + offset,
                _ => offset,
            };

            if (target < 0 || (!wide && target > int.MaxValue))
            {
                CrtSetErrno(CrtEinval);
                Answer(-1);
                return;
            }

            ResetPosition(file, target);
            Answer(target);
        }

        /// <summary>int _dup(int fd): another descriptor for the same open file, sharing its position.</summary>
        private void Dup()
        {
            int fd = _frame.SignedArg(0);
            if (!_byDescriptor.TryGetValue(fd, out OpenFile? file))
            {
                if (fd == -2 || (fd is >= 0 and <= 2 && _crtStdOpen[fd]))
                {
                    CrtReturnError(CrtEof, CrtEbadf);
                    return;
                }

                BadDescriptor("_dup", CrtEof);
                return;
            }

            int copy = _nextDescriptor++;
            _byDescriptor[copy] = file;
            _frame.Return(copy);
        }

        /// <summary>int _dup2(int fd1, int fd2): fd2 becomes another descriptor for fd1's file; 0 or -1.</summary>
        private void Dup2()
        {
            int from = _frame.SignedArg(0), to = _frame.SignedArg(1);
            if (!_byDescriptor.TryGetValue(from, out OpenFile? file))
            {
                if (from == -2 || (from is >= 0 and <= 2 && _crtStdOpen[from]))
                {
                    CrtReturnError(CrtEof, CrtEbadf);
                    return;
                }

                BadDescriptor("_dup2", CrtEof);
                return;
            }

            if (to is < 0 or >= 2048)
            {
                BadDescriptor("_dup2", CrtEof);
                return;
            }

            if (from == to)
            {
                _frame.Return(0);
                return;
            }

            if (_byDescriptor.TryGetValue(to, out OpenFile? displaced))
            {
                Release(displaced, to);
            }

            if (to <= 2)
            {
                _crtStdOpen[to] = false;
            }

            _byDescriptor[to] = file;
            _nextDescriptor = Math.Max(_nextDescriptor, to + 1);
            _frame.Return(0);
        }

        /// <summary>int _setmode(int fd, int mode): the previous translation mode, or -1.</summary>
        private void Setmode()
        {
            int fd = _frame.SignedArg(0);
            int mode = _frame.SignedArg(1);
            if (mode is not (OText or OBinary or OWText or OU16Text or OU8Text))
            {
                CrtInvalid("_setmode", CrtEof, CrtEinval);
                return;
            }

            if (fd is >= 0 and <= 2 && !_byDescriptor.ContainsKey(fd))
            {
                if (!_crtStdOpen[fd])
                {
                    BadDescriptor("_setmode", CrtEof);
                    return;
                }

                int previous = _crtStdMode[fd];
                _crtStdMode[fd] = mode == OWText ? OU16Text : mode;
                _frame.Return(previous);
                return;
            }

            if (!_byDescriptor.TryGetValue(fd, out OpenFile? file))
            {
                if (fd == -2)
                {
                    CrtReturnError(CrtEof, CrtEbadf);
                    return;
                }

                BadDescriptor("_setmode", CrtEof);
                return;
            }

            CrtState state = State(file);
            int old = !state.Text ? OBinary : state.Encoding switch
            {
                CrtEncoding.Utf8 => OU8Text,
                CrtEncoding.Utf16 => OU16Text,
                _ => OText,
            };

            state.Text = mode != OBinary;
            state.Encoding = mode switch
            {
                OU8Text => CrtEncoding.Utf8,
                OU16Text or OWText => CrtEncoding.Utf16,
                _ => CrtEncoding.Ansi,
            };
            _frame.Return(old);
        }

        /// <summary>
        /// int _locking(int fd, int mode, long nbytes). One process owns every file here, so a lock
        /// is always granted; the arguments are still checked as the CRT checks them.
        /// </summary>
        private void Locking()
        {
            int fd = _frame.SignedArg(0);
            int mode = _frame.SignedArg(1);
            int bytes = _frame.SignedArg(2);
            if (!_byDescriptor.ContainsKey(fd))
            {
                if (fd == -2)
                {
                    CrtReturnError(CrtEof, CrtEbadf);
                    return;
                }

                BadDescriptor("_locking", CrtEof);
                return;
            }

            if (mode is < 0 or > 4 || bytes < 0)
            {
                CrtInvalid("_locking", CrtEof, CrtEinval);
                return;
            }

            _frame.Return(0);
        }

        // =======================================================================================
        // Paths
        // =======================================================================================

        private void Rename(string caller, string from, string to, bool nullArgument)
        {
            if (nullArgument)
            {
                CrtInvalid(caller, CrtEof, CrtEinval);
                return;
            }

            string source = Resolve(from, out bool sourceWritable);
            string target = Resolve(to, out bool targetWritable);
            bool isFile = source.Length > 0 && File.Exists(source);
            bool isDirectory = source.Length > 0 && Directory.Exists(source);
            if (!isFile && !isDirectory)
            {
                CrtReturnError(CrtEof, CrtEnoent);
                return;
            }

            if (!sourceWritable || !targetWritable || target.Length == 0 || File.Exists(target) || Directory.Exists(target))
            {
                CrtReturnError(CrtEof, CrtEacces);
                return;
            }

            if (!EnsureParent(target))
            {
                CrtReturnError(CrtEof, CrtEnoent);
                return;
            }

            try
            {
                if (isFile)
                {
                    File.Move(source, target);
                }
                else
                {
                    Directory.Move(source, target);
                }

                Log.Add($"{caller}(\"{from}\" -> \"{to}\") -> ok");
                _frame.Return(0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrtReturnError(CrtEof, CrtEacces);
            }
        }

        /// <summary>
        /// int _access(path, mode): 00 exists, 02 write, 04 read, 06 both. The package is read-only,
        /// so asking to write a packaged file is EACCES, as is a read-only file in the sandbox.
        /// </summary>
        private void Access(string caller, string path, bool nullPath, int mode)
        {
            if (nullPath || (mode & ~6) != 0)
            {
                CrtInvalid(caller, CrtEof, CrtEinval);
                return;
            }

            string host = Resolve(path, out bool writable);
            bool isFile = host.Length > 0 && File.Exists(host);
            bool isDirectory = host.Length > 0 && Directory.Exists(host);
            if (!isFile && !isDirectory)
            {
                CrtReturnError(CrtEof, CrtEnoent);
                return;
            }

            if ((mode & 2) != 0 && (!writable || (isFile && new FileInfo(host).IsReadOnly)))
            {
                CrtReturnError(CrtEof, CrtEacces);
                return;
            }

            _frame.Return(0);
        }

        /// <summary>int _chmod(const char* path, int pmode): _S_IWRITE clear makes the file read-only.</summary>
        private void Chmod()
        {
            if (_frame.Arg(0) == 0)
            {
                CrtInvalid("_chmod", CrtEof, CrtEinval);
                return;
            }

            string host = Resolve(ReadNarrow(0), out bool writable);
            bool isFile = host.Length > 0 && File.Exists(host);
            if (!isFile && !(host.Length > 0 && Directory.Exists(host)))
            {
                CrtReturnError(CrtEof, CrtEnoent);
                return;
            }

            if (!writable)
            {
                CrtReturnError(CrtEof, CrtEacces);
                return;
            }

            try
            {
                FileAttributes attributes = File.GetAttributes(host);
                attributes = (_frame.SignedArg(1) & SIWrite) != 0 ? attributes & ~FileAttributes.ReadOnly : attributes | FileAttributes.ReadOnly;
                File.SetAttributes(host, attributes);
                _frame.Return(0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrtReturnError(CrtEof, CrtEacces);
            }
        }

        /// <summary>int _mkdir / _wmkdir(path): 0, or -1 with EEXIST or ENOENT.</summary>
        private void MakeDirectory(string path, bool nullPath)
        {
            if (nullPath)
            {
                CrtInvalid("_mkdir", CrtEof, CrtEinval);
                return;
            }

            string host = Resolve(path, out bool writable);
            if (host.Length == 0)
            {
                CrtReturnError(CrtEof, CrtEnoent);
                return;
            }

            if (Directory.Exists(host) || File.Exists(host))
            {
                CrtReturnError(CrtEof, CrtEexist);
                return;
            }

            if (!writable)
            {
                CrtReturnError(CrtEof, CrtEacces);
                return;
            }

            if (!EnsureParent(host))
            {
                CrtReturnError(CrtEof, CrtEnoent);
                return;
            }

            try
            {
                Directory.CreateDirectory(host);
                Log.Add($"_mkdir(\"{path}\") -> created");
                _frame.Return(0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrtReturnError(CrtEof, CrtEacces);
            }
        }

        /// <summary>int _rmdir(path): 0, or -1 with ENOENT, ENOTEMPTY or EACCES (the package).</summary>
        private void RemoveDirectoryCrt(string path, bool nullPath)
        {
            if (nullPath)
            {
                CrtInvalid("_rmdir", CrtEof, CrtEinval);
                return;
            }

            string host = Resolve(path, out bool writable);
            if (host.Length == 0 || !Directory.Exists(host))
            {
                CrtReturnError(CrtEof, CrtEnoent);
                return;
            }

            if (!writable)
            {
                CrtReturnError(CrtEof, CrtEacces);
                return;
            }

            if (Directory.EnumerateFileSystemEntries(host).Any())
            {
                CrtReturnError(CrtEof, CrtEnotempty);
                return;
            }

            try
            {
                Directory.Delete(host);
                _frame.Return(0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrtReturnError(CrtEof, CrtEacces);
            }
        }

        /// <summary>int _utime64(const char* path, struct __utimbuf64* times): {actime, modtime}; NULL is now.</summary>
        private void Utime64()
        {
            if (_frame.Arg(0) == 0)
            {
                CrtInvalid("_utime64", CrtEof, CrtEinval);
                return;
            }

            string host = Resolve(ReadNarrow(0), out bool writable);
            if (host.Length == 0 || !File.Exists(host))
            {
                CrtReturnError(CrtEof, CrtEnoent);
                return;
            }

            if (!writable)
            {
                CrtReturnError(CrtEof, CrtEacces);
                return;
            }

            DateTime accessed = DateTime.UtcNow, modified = DateTime.UtcNow;
            if (_frame.Arg(1) != 0)
            {
                byte[] times = _emulator.ReadMemory(_frame.Arg(1), 16);
                long a = BitConverter.ToInt64(times, 0), m = BitConverter.ToInt64(times, 8);
                if (a < 0 || m < 0)
                {
                    CrtReturnError(CrtEof, CrtEinval);
                    return;
                }

                accessed = DateTime.UnixEpoch.AddSeconds(a);
                modified = DateTime.UnixEpoch.AddSeconds(m);
            }

            try
            {
                File.SetLastAccessTimeUtc(host, accessed);
                File.SetLastWriteTimeUtc(host, modified);
                _frame.Return(0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrtReturnError(CrtEof, CrtEacces);
            }
        }

        /// <summary>
        /// The app's current folder: the package root, which is what a relative path resolves
        /// against everywhere else here, spelled as argv[0] spells it.
        /// </summary>
        internal const string CrtCurrentDirectory = @"C:\Data\Programs\Install";

        /// <summary>
        /// char* _fullpath(char* absPath, const char* relPath, size_t maxLength): relPath made
        /// absolute against the current folder, '.' and '..' folded, separators made '\'. A null
        /// absPath is allocated (and owned by the caller); ERANGE when the result does not fit.
        /// </summary>
        private void FullPath()
        {
            long destination = _frame.Arg(0);
            string relative = _frame.Arg(1) == 0 ? string.Empty : ReadNarrow(1);
            long max = _frame.Arg(2);

            string result = CrtFullPath(relative);
            if (destination == 0)
            {
                destination = _emulator.AllocateHeap(Math.Max(260, result.Length + 1));
                if (destination == 0)
                {
                    CrtReturnError(0, CrtEnomem);
                    return;
                }
            }
            else if (result.Length + 1 > max)
            {
                CrtReturnError(0, CrtErange);
                return;
            }

            _frame.WriteNarrowString(destination, result);
            _frame.Return(destination);
        }

        internal static string CrtFullPath(string relative)
        {
            string path = relative.Replace('/', '\\');
            string combined;
            if (path.Length == 0)
            {
                combined = CrtCurrentDirectory;
            }
            else if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                combined = path;
            }
            else if (path.Length >= 2 && path[1] == ':')
            {
                combined = path.Length > 2 && path[2] == '\\' ? path : path[..2] + "\\" + path[2..];
            }
            else if (path.StartsWith('\\'))
            {
                combined = CrtCurrentDirectory[..2] + path;
            }
            else
            {
                combined = CrtCurrentDirectory + "\\" + path;
            }

            bool unc = combined.StartsWith(@"\\", StringComparison.Ordinal);
            string root = unc ? @"\\" : combined[..2] + "\\";
            string rest = unc ? combined[2..] : combined.Length > 3 ? combined[3..] : string.Empty;
            bool trailing = rest.EndsWith('\\');
            List<string> parts = new();
            foreach (string part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }

                    continue;
                }

                parts.Add(part);
            }

            string joined = root + string.Join('\\', parts);
            return trailing && parts.Count > 0 ? joined + "\\" : joined;
        }

        private const int MaxDrive = 3, MaxDir = 256, MaxFname = 256, MaxExt = 256;

        internal static (string Drive, string Dir, string Fname, string Ext) CrtSplitPath(string path)
        {
            string drive = string.Empty;
            string rest = path;
            if (rest.Length >= 2 && rest[1] == ':')
            {
                drive = rest[..2];
                rest = rest[2..];
            }

            int lastSeparator = Math.Max(rest.LastIndexOf('\\'), rest.LastIndexOf('/'));
            string dir = lastSeparator >= 0 ? rest[..(lastSeparator + 1)] : string.Empty;
            string name = rest[(lastSeparator + 1)..];
            int dot = name.LastIndexOf('.');
            return dot >= 0 ? (drive, dir, name[..dot], name[dot..]) : (drive, dir, name, string.Empty);
        }

        /// <summary>
        /// void _splitpath(path, drive, dir, fname, ext) and errno_t _splitpath_s(path, drive,
        /// driveSize, dir, dirSize, fname, fnameSize, ext, extSize). Any component may be NULL (with
        /// a size of 0 for the _s form). A component that does not fit is ERANGE, and every buffer
        /// is emptied.
        /// </summary>
        private void SplitPath(bool secure)
        {
            if (_frame.Arg(0) == 0)
            {
                if (secure)
                {
                    CrtInvalid("_splitpath_s", CrtEinval, CrtEinval);
                }
                else
                {
                    CrtInvalid("_splitpath", 0, CrtEinval);
                }

                return;
            }

            (string drive, string dir, string fname, string ext) = CrtSplitPath(ReadNarrow(0));
            (long Buffer, long Size, string Value)[] parts = secure
                ? [(_frame.Arg(1), _frame.Arg(2), drive), (_frame.Arg(3), _frame.Arg(4), dir), (_frame.Arg(5), _frame.Arg(6), fname), (_frame.Arg(7), _frame.Arg(8), ext)]
                : [(_frame.Arg(1), MaxDrive, drive), (_frame.Arg(2), MaxDir, dir), (_frame.Arg(3), MaxFname, fname), (_frame.Arg(4), MaxExt, ext)];

            if (secure && parts.Any(p => (p.Buffer == 0) != (p.Size == 0)))
            {
                foreach (var p in parts.Where(p => p.Buffer != 0 && p.Size > 0))
                {
                    _emulator.WriteMemory(p.Buffer, [0]);
                }

                CrtInvalid("_splitpath_s", CrtEinval, CrtEinval);
                return;
            }

            if (parts.Any(p => p.Buffer != 0 && p.Value.Length + 1 > p.Size))
            {
                foreach (var p in parts.Where(p => p.Buffer != 0 && p.Size > 0))
                {
                    _emulator.WriteMemory(p.Buffer, [0]);
                }

                CrtInvalid(secure ? "_splitpath_s" : "_splitpath", secure ? CrtErange : 0, CrtErange);
                return;
            }

            foreach (var p in parts.Where(p => p.Buffer != 0))
            {
                _frame.WriteNarrowString(p.Buffer, p.Value);
            }

            _frame.Return(0);
        }

        /// <summary>
        /// errno_t _makepath_s(char* path, size_t size, drive, dir, fname, ext): "X:" from the drive's
        /// letter, the folder with a '\' added when it has no trailing separator, the name, and the
        /// extension with a '.' added when it has none.
        /// </summary>
        private void MakePathS()
        {
            long destination = _frame.Arg(0);
            long size = _frame.Arg(1);
            if (destination == 0 || size == 0)
            {
                CrtInvalid("_makepath_s", CrtEinval, CrtEinval);
                return;
            }

            string Part(int index) => _frame.Arg(index) == 0 ? string.Empty : ReadNarrow(index);
            string drive = Part(2), dir = Part(3), fname = Part(4), ext = Part(5);
            StringBuilder path = new();
            if (drive.Length > 0)
            {
                path.Append(drive[0]).Append(':');
            }

            if (dir.Length > 0)
            {
                path.Append(dir);
                if (!dir.EndsWith('\\') && !dir.EndsWith('/'))
                {
                    path.Append('\\');
                }
            }

            path.Append(fname);
            if (ext.Length > 0)
            {
                if (ext[0] != '.')
                {
                    path.Append('.');
                }

                path.Append(ext);
            }

            if (path.Length + 1 > size)
            {
                _emulator.WriteMemory(destination, [0]);
                CrtInvalid("_makepath_s", CrtErange, CrtErange);
                return;
            }

            _frame.WriteNarrowString(destination, path.ToString());
            _frame.Return(0);
        }
    }
}

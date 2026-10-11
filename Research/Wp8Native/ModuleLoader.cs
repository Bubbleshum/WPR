using System.Buffers.Binary;
using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    // Inside the namespace on purpose, as in ArmEmulator.cs: with WPR.Common referenced, a
    // file-level alias loses to the WPR.Common namespace.
    using Common = UnicornEngine.Const.Common;

    /// <summary>One image in the guest's address space: the main image or a DLL from the package.</summary>
    public sealed class LoadedModule
    {
        internal LoadedModule(string name, string guestPath, string? hostPath, PeImage image, long imageBase, bool isMain)
        {
            Name = name;
            GuestPath = guestPath;
            HostPath = hostPath;
            Image = image;
            ImageBase = imageBase;
            IsMain = isMain;
        }

        /// <summary>The file name, as it is spelled on disk.</summary>
        public string Name { get; }

        /// <summary>What GetModuleFileNameW reports: the file inside the install folder.</summary>
        public string GuestPath { get; }

        /// <summary>The file this was read from, when it came from a file.</summary>
        public string? HostPath { get; }

        public PeImage Image { get; }

        /// <summary>Where it is mapped, which is also its HMODULE.</summary>
        public long ImageBase { get; }

        /// <summary>Bytes reserved for it, page aligned.</summary>
        public long MappedSize => (Image.SizeOfImage + 0xFFFL) & ~0xFFFL;

        public bool IsMain { get; }

        /// <summary>
        /// Never unloaded: the main image, anything loaded because an image imports it (a static
        /// load's LoadCount is -1 on Windows), and anything pinned through GetModuleHandleEx.
        /// </summary>
        public bool Pinned { get; internal set; }

        /// <summary>LoadPackagedLibrary/GetModuleHandleEx references plus dynamic importers. Meaningless once pinned.</summary>
        public int ReferenceCount { get; internal set; }

        /// <summary>DllMain(DLL_PROCESS_ATTACH) has been called and returned TRUE.</summary>
        public bool ProcessAttached { get; internal set; }

        /// <summary>DisableThreadLibraryCalls was called on it.</summary>
        public bool ThreadCallsDisabled { get; internal set; }

        /// <summary>Base relocations applied when it was mapped away from its preferred base.</summary>
        public int RelocationsApplied { get; internal set; }

        /// <summary>The package modules its import table is bound to.</summary>
        public List<LoadedModule> Dependencies { get; } = new();

        /// <summary>Unloaded by FreeLibrary: its range is no longer accessible.</summary>
        public bool Unloaded { get; internal set; }

        /// <summary>DllMain, or 0 when the image has no entry point.</summary>
        public long EntryPoint => Image.EntryPointRva == 0 ? 0 : ImageBase + Image.EntryPointRva;

        /// <summary>Its static TLS (ModuleTls.cs), or null when it has no TLS directory.</summary>
        public ModuleTls? Tls { get; internal set; }

        /// <summary>TLS callbacks, in the order the directory lists them.</summary>
        public IReadOnlyList<long> TlsCallbacks => Tls?.Callbacks ?? [];

        /// <summary>A DllMain or a TLS callback to call when the loader notifies it.</summary>
        public bool HasNotifications => EntryPoint != 0 || TlsCallbacks.Count > 0;

        public bool Contains(long address) => address >= ImageBase && address < ImageBase + MappedSize;

        public override string ToString() =>
            $"{Name} at 0x{ImageBase:X8}-0x{ImageBase + MappedSize:X8}" +
            (ImageBase != Image.ImageBase ? $" (preferred 0x{Image.ImageBase:X8}, {RelocationsApplied} relocations)" : string.Empty) +
            (Pinned ? " pinned" : $" refs={ReferenceCount}");
    }

    /// <summary>
    /// The Windows loader for the DLLs a game ships in its own package: mapping, relocation,
    /// import binding, DllMain, and LoadPackagedLibrary / GetProcAddress / GetModuleHandle(Ex) /
    /// GetModuleFileName / FreeLibrary over the same modules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this the emulator mapped exactly one image, and every call into a DLL the game
    /// carries itself - FMOD, SQLite, IGPLib, PushNotificationWP8, an OpenGL ES port - reached the
    /// host stubs by name and was answered 0: the library silently did nothing. Now an import
    /// whose DLL is in the package root is bound to the real export, the DLL is mapped (relocated
    /// when its preferred base is taken - WP8 DLLs all prefer 0x10000000), its own imports are
    /// bound the same way recursively, and its DllMain runs before the image that needs it.
    /// </para>
    /// <para>
    /// System DLLs keep their host stubs, even when a package carries a copy (one cocos2d title
    /// ships msvcr120_app.dll): the CRT, vccorlib, the API sets and the platform DLLs are this
    /// host. Anything a module imports that cannot be bound - no such file, no such export, an
    /// image that cannot be mapped - falls back to the host stub exactly as before and is written
    /// to <see cref="UnresolvedModuleImports"/>. Nothing here ever stops the guest.
    /// </para>
    /// </remarks>
    public sealed partial class ArmEmulator
    {
        private const uint DllProcessDetach = 0;
        private const uint DllProcessAttach = 1;
        private const uint DllThreadAttach = 2;
        private const uint DllThreadDetach = 3;

        /// <summary>Where GetModuleFileNameW says the package is (the file library maps it back).</summary>
        public const string InstallFolder = @"C:\Data\Programs\Install\";

        private const string SystemFolder = @"C:\Windows\System32\";

        /// <summary>Lowest and highest address a DLL may be placed at when its preferred base is taken.</summary>
        private const long ModuleSearchStart = 0x10000000L;
        private const long ModuleAlignment = 0x10000L;

        private readonly List<LoadedModule> _modules = new();
        private readonly List<LoadedModule> _pendingInitialisers = new();

        /// <summary>Attached modules in the order their PROCESS_ATTACH completed: thread notifications walk it.</summary>
        private readonly List<LoadedModule> _initialisationOrder = new();

        /// <summary>Pages an unloaded module left mapped (no access) that a later load may take.</summary>
        private readonly HashSet<long> _retiredPages = new();
        private bool _mainImageInitialised;
        private readonly Dictionary<string, long> _systemModules = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _procedureTraps = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, long> _threadStarts = new();
        private Dictionary<string, string>? _packageDlls;
        private string _packageRoot = string.Empty;
        private LoadedModule? _bindingFor;
        private bool _loadingDynamically;
        private int _trappedImportCount;
        private long _threadAttachTrap;
        private long _threadDetachTrap;

        /// <summary>
        /// WPR_WP8_NO_MODULES=1: the single-image behaviour from before the loader - every import a
        /// host stub, no package DLL mapped, no thread notifications. A diagnostic for A/B runs only.
        /// </summary>
        public static readonly bool LoaderDisabled = Environment.GetEnvironmentVariable("WPR_WP8_NO_MODULES") == "1";

        /// <summary>Every module in the address space, the main image first.</summary>
        public IReadOnlyList<LoadedModule> Modules => _modules;

        /// <summary>What the loader did, in order: maps, relocations, DllMain results, unloads.</summary>
        public List<string> ModuleLog { get; } = new();

        /// <summary>
        /// DllMain(DLL_PROCESS_ATTACH) calls that returned FALSE during process start-up. On a
        /// device the process would not start (STATUS_DLL_INIT_FAILED); here the game carries on
        /// and this is what the report says instead.
        /// </summary>
        public List<string> InitialisationFailures { get; } = new();

        /// <summary>Imports of a package DLL that could not be bound and went to the host stubs.</summary>
        public List<string> UnresolvedModuleImports { get; } = new();

        /// <summary>GetProcAddress calls answered NULL, as <c>module!name</c>.</summary>
        public List<string> UnresolvedProcedures { get; } = new();

        /// <summary>The lowest mapped image, for "is this plausibly a code address" checks.</summary>
        public long LowestImageBase => _modules.Count == 0 ? _image.ImageBase : _modules.Min(m => m.ImageBase);

        /// <summary>Base of the module containing <paramref name="address"/>, or 0.</summary>
        public long ModuleBaseOf(long address) => ModuleAt(address)?.ImageBase ?? 0;

        /// <summary>The module containing <paramref name="address"/>, or null.</summary>
        public LoadedModule? ModuleAt(long address)
        {
            foreach (LoadedModule module in _modules)
            {
                if (module.Contains(address))
                {
                    return module;
                }
            }

            return null;
        }

        /// <summary>The module whose HMODULE is <paramref name="handle"/>, or null.</summary>
        public LoadedModule? ModuleByHandle(long handle)
            => _modules.FirstOrDefault(m => m.ImageBase == (handle & 0xFFFFFFFFL));

        /// <summary>
        /// DLLs this host implements. An import naming one is always a host stub, even if the
        /// package happens to carry a file of that name.
        /// </summary>
        public static bool IsSystemDll(string dll)
        {
            string name = Path.GetFileNameWithoutExtension(dll).ToLowerInvariant();
            return name.StartsWith("api-ms-win-", StringComparison.Ordinal) ||
                   name.StartsWith("ext-ms-win-", StringComparison.Ordinal) ||
                   name.StartsWith("msvcr", StringComparison.Ordinal) ||
                   name.StartsWith("msvcp", StringComparison.Ordinal) ||
                   name.StartsWith("vccorlib", StringComparison.Ordinal) ||
                   name.StartsWith("concrt", StringComparison.Ordinal) ||
                   name.StartsWith("xaudio2", StringComparison.Ordinal) ||
                   name.StartsWith("xinput", StringComparison.Ordinal) ||
                   name is "kernel32" or "kernelbase" or "ntdll" or "user32" or "ole32" or "oleaut32"
                       or "combase" or "rpcrt4" or "advapi32" or "ws2_32" or "d3d11" or "dxgi" or "d2d1"
                       or "dwrite" or "mfplat" or "mmdevapi" or "phoneappmodelhost" or "phoneaudioses"
                       or "windowscodecs" or "bcrypt" or "crypt32" or "winhttp" or "wininet" or "urlmon"
                       or "iphlpapi" or "shlwapi" or "shell32" or "gdi32" or "winmm" or "dsound" or "xboxlive";
        }

        // -----------------------------------------------------------------------------
        // Start-up
        // -----------------------------------------------------------------------------

        /// <summary>Registers the main image as a module; called once, before its imports are bound.</summary>
        private void RegisterMainModule(string imageDirectory)
        {
            _packageRoot = imageDirectory;
            string name = MainImageFileName(imageDirectory);
            LoadedModule main = new(name, InstallFolder + name, Path.Combine(imageDirectory, name), _image, _image.ImageBase, isMain: true)
            {
                Pinned = true,
                // An exe has no DllMain; a component DLL's is called by whoever activates it.
                ProcessAttached = !_image.IsDll,
            };

            _modules.Add(main);
            _bindingFor = main;

            // The main image's TLS gets index 0, ahead of every DLL, as in load order on Windows.
            AttachStaticTls(main);
        }

        /// <summary>
        /// The main image's file name. The emulator is handed the parsed image, not its path, so
        /// it is found again by content: the package file with the same length and headers.
        /// </summary>
        private string MainImageFileName(string imageDirectory)
        {
            string fallback = _image.IsDll ? "app.dll" : "app.exe";
            if (!Directory.Exists(imageDirectory))
            {
                return fallback;
            }

            int headerLength = (int)Math.Min(_image.SizeOfHeaders, (uint)_image.Raw.Length);
            foreach (string file in Directory.EnumerateFiles(imageDirectory))
            {
                if (!(file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    if (new FileInfo(file).Length != _image.Raw.Length)
                    {
                        continue;
                    }

                    using FileStream stream = File.OpenRead(file);
                    byte[] header = new byte[headerLength];
                    stream.ReadExactly(header);
                    if (_image.Raw[..headerLength].SequenceEqual(header))
                    {
                        return Path.GetFileName(file);
                    }
                }
                catch (IOException)
                {
                }
            }

            return fallback;
        }

        /// <summary>The package's DLLs by lower-case file name. The WP8 loader searches the package root.</summary>
        private Dictionary<string, string> PackageDlls()
        {
            if (_packageDlls is not null)
            {
                return _packageDlls;
            }

            _packageDlls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_packageRoot))
            {
                foreach (string file in Directory.EnumerateFiles(_packageRoot, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        _packageDlls[Path.GetFileName(file)] = file;
                    }
                }
            }

            return _packageDlls;
        }

        /// <summary>
        /// Binds one IAT slot to a package DLL's export, loading the DLL if needed. False means
        /// the import was not bound and the caller traps it to the host stubs, as it always did.
        /// </summary>
        private bool TryBindToPackageModule(ImportedFunction import, long slotAddress)
        {
            if (LoaderDisabled || IsSystemDll(import.Dll))
            {
                NoteSystemModule(import.Dll);
                return false;
            }

            LoadedModule? target = FindOrLoadModule(import.Dll, out _);
            if (target is null)
            {
                NoteSystemModule(import.Dll);
                return false;
            }

            AddDependency(_bindingFor, target);

            long? address = ResolveExport(target, import.Name, depth: 0);
            if (address is not { } bound)
            {
                UnresolvedModuleImports.Add($"{_bindingFor?.Name ?? "?"} imports {import.FullName}: no such export, left to the host stub");
                return false;
            }

            _cpu.MemWrite(slotAddress, BitConverter.GetBytes((uint)bound));
            if (!IsExecutableCode(bound))
            {
                // A data export: the importer reads and writes through the slot, so it must hold
                // the variable itself, which it now does.
                DataImportCells.Add($"{import.FullName} -> data export at 0x{bound:X8}");
            }

            return true;
        }

        /// <summary>Counts an import left to the host stubs.</summary>
        private void NoteTrappedImport() => _trappedImportCount++;

        private void AddDependency(LoadedModule? importer, LoadedModule target)
        {
            if (importer is null || importer == target || importer.Dependencies.Contains(target))
            {
                return;
            }

            importer.Dependencies.Add(target);

            // A dependency is as permanent as its least permanent importer: a static importer
            // pins it, a dynamically loaded one holds a reference FreeLibrary gives back.
            if (importer.Pinned)
            {
                target.Pinned = true;
            }
            else if (!target.Pinned)
            {
                target.ReferenceCount++;
            }
        }

        /// <summary>A system DLL is "loaded" once any module imports from it, as on Windows.</summary>
        private void NoteSystemModule(string dll)
        {
            string name = NormaliseModuleName(dll);
            if (!_systemModules.ContainsKey(name))
            {
                _systemModules[name] = 0;   // a handle is minted when one is asked for
            }
        }

        /// <summary>The pseudo-HMODULE for a system DLL: a page holding "MZ", distinct per DLL.</summary>
        private long SystemModuleHandle(string name)
        {
            long handle = _systemModules.GetValueOrDefault(name);
            if (handle == 0)
            {
                handle = AllocateHeap(PageSize);
                _cpu.MemWrite(handle, new byte[PageSize]);
                _cpu.MemWrite(handle, "MZ"u8.ToArray());
                _systemModules[name] = handle;
            }

            return handle;
        }

        private string? SystemModuleNameOf(long handle)
        {
            foreach ((string name, long value) in _systemModules)
            {
                if (value != 0 && value == (handle & 0xFFFFFFFFL))
                {
                    return name;
                }
            }

            return null;
        }

        /// <summary>A bare name gets ".dll"; a trailing '.' means "no extension". Paths are reduced to the file name.</summary>
        private static string NormaliseModuleName(string name)
        {
            string file = name.Replace('/', '\\');
            file = file[(file.LastIndexOf('\\') + 1)..];
            if (file.EndsWith('.'))
            {
                return file.TrimEnd('.');
            }

            return Path.HasExtension(file) ? file : file + ".dll";
        }

        // -----------------------------------------------------------------------------
        // Mapping
        // -----------------------------------------------------------------------------

        /// <summary>
        /// The loaded module of that name, or the package file of that name mapped and bound now.
        /// Null (with a Win32 error) when there is no such file or it cannot be loaded.
        /// </summary>
        private LoadedModule? FindOrLoadModule(string dll, out int error)
        {
            error = 0;
            string name = NormaliseModuleName(dll);
            if (_modules.FirstOrDefault(m => !m.Unloaded && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) is { } loaded)
            {
                return loaded;
            }

            if (!PackageDlls().TryGetValue(name, out string? path))
            {
                error = ErrorModNotFound;
                return null;
            }

            return LoadModuleFile(path, out error);
        }

        private LoadedModule? LoadModuleFile(string path, out int error)
        {
            error = 0;
            string name = Path.GetFileName(path);
            PeImage image;
            try
            {
                image = PeImage.Load(path);
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or ArgumentException or IndexOutOfRangeException)
            {
                ModuleLog.Add($"{name}: not a loadable image ({ex.Message}); its imports stay host stubs");
                error = ErrorBadExeFormat;
                return null;
            }

            if (image.Machine != PeImage.MachineArmNt || image.IsManaged)
            {
                ModuleLog.Add($"{name}: {(image.IsManaged ? "a managed assembly" : $"machine 0x{image.Machine:X4}")}, not an ARM native image; its imports stay host stubs");
                error = ErrorBadExeFormat;
                return null;
            }

            long size = Align(image.SizeOfImage, PageSize);
            long imageBase = ChooseModuleBase(image.ImageBase, size);
            if (imageBase == 0)
            {
                ModuleLog.Add($"{name}: no free range of 0x{size:X} bytes for it");
                error = ErrorNotEnoughMemory;
                return null;
            }

            byte[] memory = new byte[size];
            int headerBytes = (int)Math.Min(Math.Min(image.SizeOfHeaders, (uint)image.Raw.Length), (uint)size);
            image.Raw[..headerBytes].CopyTo(memory);
            foreach (PeSection section in image.Sections)
            {
                long available = image.Raw.Length - (long)section.RawPointer;
                long count = Math.Min(Math.Min(section.RawSize, available), size - section.VirtualAddress);
                if (count > 0)
                {
                    image.Raw.Slice((int)section.RawPointer, (int)count).CopyTo(memory.AsSpan((int)section.VirtualAddress));
                }
            }

            int relocations = 0;
            long delta = imageBase - image.ImageBase;
            if (delta != 0)
            {
                if (image.BaseRelocationSize == 0)
                {
                    // IMAGE_FILE_RELOCS_STRIPPED in effect: Windows refuses to load it anywhere else.
                    ModuleLog.Add($"{name}: its preferred base 0x{image.ImageBase:X8} is taken and it has no relocations");
                    error = ErrorBadExeFormat;
                    return null;
                }

                relocations = ApplyBaseRelocations(name, image, memory, (uint)delta);
            }

            // The loader writes the actual base into the mapped header, which is what code reading
            // its own PE header (and __ImageBase arithmetic) finds there.
            int peHeader = BinaryPrimitives.ReadInt32LittleEndian(memory.AsSpan(0x3C));
            if (peHeader > 0 && peHeader + 24 + 32 <= memory.Length)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(peHeader + 24 + 28), (uint)imageBase);
            }

            if (!MapModuleRange(imageBase, size))
            {
                // Not reached: ChooseModuleBase only returns free ranges. Kept as a guard.
                ModuleLog.Add($"{name}: 0x{imageBase:X8} is already mapped");
                error = ErrorNotEnoughMemory;
                return null;
            }

            // The whole range is written, so a range an unloaded module left behind holds none of it.
            _cpu.MemWrite(imageBase, memory);

            LoadedModule module = new(name, InstallFolder + Path.GetRelativePath(_packageRoot, path), path, image, imageBase, isMain: false)
            {
                Pinned = !_loadingDynamically,
                RelocationsApplied = relocations,
            };

            // Registered before its imports are bound, so an import cycle finds it mapped rather
            // than loading it twice - the same order the Windows loader uses.
            _modules.Add(module);
            Unwinder.AddImage(image, imageBase);
            ModuleLog.Add($"mapped {module}");

            // Its _tls_index and a block for every live thread, before anything can run in it.
            AttachStaticTls(module);

            LoadedModule? outer = _bindingFor;
            _bindingFor = module;
            try
            {
                InstallImportTraps(image, imageBase);
            }
            finally
            {
                _bindingFor = outer;
            }

            // Dependencies first: they were appended while the imports above were bound.
            _pendingInitialisers.Add(module);
            return module;
        }

        /// <summary>
        /// The preferred base if it is free, otherwise the lowest free 64 KB-aligned range from
        /// 0x10000000 up to the stack. 0 when there is none.
        /// </summary>
        private long ChooseModuleBase(long preferred, long size)
        {
            if (preferred % ModuleAlignment == 0 && IsRangeFree(preferred, size))
            {
                return preferred;
            }

            for (long candidate = ModuleSearchStart; candidate + size <= StackBase; candidate += ModuleAlignment)
            {
                if (IsRangeFree(candidate, size))
                {
                    return candidate;
                }
            }

            return 0;
        }

        private bool IsRangeFree(long start, long size)
        {
            if (start < NullPageLimit || start + size > StackBase)
            {
                return false;
            }

            foreach (LoadedModule module in _modules)
            {
                if (start < module.ImageBase + module.MappedSize && module.ImageBase < start + size)
                {
                    return false;
                }
            }

            // Anything the run touched was mapped on demand and shows up here. A range an
            // unloaded module gave back is mapped too, but reusable (see UnmapModule).
            for (long page = start; page < start + size; page += PageSize)
            {
                if (_cpu.IsMapped(page) && !_retiredPages.Contains(page))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Makes [start, start+size) accessible for a module: pages an unloaded module gave back
        /// are re-protected, the rest are mapped. False, touching nothing, if any page is in use.
        /// </summary>
        private bool MapModuleRange(long start, long size)
        {
            for (long page = start; page < start + size; page += PageSize)
            {
                if (_cpu.IsMapped(page) && !_retiredPages.Contains(page))
                {
                    return false;
                }
            }

            long run = start;
            while (run < start + size)
            {
                bool retired = _retiredPages.Contains(run);
                long end = run;
                while (end < start + size && _retiredPages.Contains(end) == retired)
                {
                    end += PageSize;
                }

                if (retired)
                {
                    _cpu.MemProtect(run, end - run, Common.UC_PROT_ALL);
                    for (long page = run; page < end; page += PageSize)
                    {
                        _retiredPages.Remove(page);
                    }
                }
                else
                {
                    _cpu.MemMap(run, end - run, Common.UC_PROT_ALL);
                }

                run = end;
            }

            return true;
        }

        /// <summary>
        /// Applies IMAGE_DIRECTORY_ENTRY_BASERELOC to the image as laid out in memory.
        /// HIGHLOW (3) adds the delta to a word; ARM_MOV32 (5) and THUMB_MOV32 (7) rewrite the
        /// 16-bit immediates of a Thumb-2 MOVW/MOVT pair, the way every WP8 image loads an address.
        /// </summary>
        internal static int ApplyBaseRelocations(string name, PeImage image, byte[] memory, uint delta)
        {
            int applied = 0;
            int unsupported = 0;
            long cursor = image.BaseRelocationRva;
            long end = Math.Min(cursor + image.BaseRelocationSize, memory.Length);

            while (cursor + 8 <= end)
            {
                uint pageRva = BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan((int)cursor));
                uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan((int)cursor + 4));
                if (blockSize < 8 || cursor + blockSize > end)
                {
                    break;
                }

                for (long entryAt = cursor + 8; entryAt + 2 <= cursor + blockSize; entryAt += 2)
                {
                    ushort entry = BinaryPrimitives.ReadUInt16LittleEndian(memory.AsSpan((int)entryAt));
                    int type = entry >> 12;
                    long at = pageRva + (entry & 0xFFF);

                    switch (type)
                    {
                        case 0: // IMAGE_REL_BASED_ABSOLUTE: padding
                            break;

                        case 3 when at + 4 <= memory.Length: // IMAGE_REL_BASED_HIGHLOW
                        {
                            Span<byte> word = memory.AsSpan((int)at, 4);
                            BinaryPrimitives.WriteUInt32LittleEndian(word, BinaryPrimitives.ReadUInt32LittleEndian(word) + delta);
                            applied++;
                            break;
                        }

                        case 5 or 7 when at + 8 <= memory.Length && RelocateThumbMov32(memory.AsSpan((int)at, 8), delta):
                            applied++;
                            break;

                        default:
                            unsupported++;
                            break;
                    }
                }

                cursor += blockSize;
            }

            if (unsupported > 0)
            {
                Console.Error.WriteLine($"[wp8-loader] {name}: {unsupported} base relocation(s) of an unsupported type or shape were skipped");
            }

            return applied;
        }

        /// <summary>
        /// Adds <paramref name="delta"/> to the 32-bit value a Thumb-2 MOVW (low half) / MOVT (high
        /// half) pair loads. False when the bytes are not that pair.
        /// </summary>
        internal static bool RelocateThumbMov32(Span<byte> pair, uint delta)
        {
            ushort movw1 = BinaryPrimitives.ReadUInt16LittleEndian(pair);
            ushort movw2 = BinaryPrimitives.ReadUInt16LittleEndian(pair[2..]);
            ushort movt1 = BinaryPrimitives.ReadUInt16LittleEndian(pair[4..]);
            ushort movt2 = BinaryPrimitives.ReadUInt16LittleEndian(pair[6..]);

            // T3 encodings: MOVW is 11110 i 100100 imm4, MOVT is 11110 i 101100 imm4.
            if ((movw1 & 0xFBF0) != 0xF240 || (movt1 & 0xFBF0) != 0xF2C0 || (movw2 & 0x8000) != 0 || (movt2 & 0x8000) != 0)
            {
                return false;
            }

            uint value = ThumbImmediate16(movw1, movw2) | ((uint)ThumbImmediate16(movt1, movt2) << 16);
            value += delta;

            EncodeThumbImmediate16(ref movw1, ref movw2, (ushort)value);
            EncodeThumbImmediate16(ref movt1, ref movt2, (ushort)(value >> 16));
            BinaryPrimitives.WriteUInt16LittleEndian(pair, movw1);
            BinaryPrimitives.WriteUInt16LittleEndian(pair[2..], movw2);
            BinaryPrimitives.WriteUInt16LittleEndian(pair[4..], movt1);
            BinaryPrimitives.WriteUInt16LittleEndian(pair[6..], movt2);
            return true;
        }

        /// <summary>imm16 = imm4:i:imm3:imm8 of a Thumb-2 MOVW/MOVT.</summary>
        internal static ushort ThumbImmediate16(ushort first, ushort second)
            => (ushort)(((first & 0xF) << 12) | (((first >> 10) & 1) << 11) | (((second >> 12) & 7) << 8) | (second & 0xFF));

        private static void EncodeThumbImmediate16(ref ushort first, ref ushort second, ushort value)
        {
            first = (ushort)((first & 0xFBF0) | ((value >> 12) & 0xF) | (((value >> 11) & 1) << 10));
            second = (ushort)((second & 0x8F00) | (((value >> 8) & 7) << 12) | (value & 0xFF));
        }

        // -----------------------------------------------------------------------------
        // Exports
        // -----------------------------------------------------------------------------

        /// <summary>
        /// An export of a module by name or <c>#ordinal</c>, as a guest address (Thumb bit
        /// included for code), following forwarders. Null when there is no such export.
        /// </summary>
        public long? ResolveExport(LoadedModule module, string name, int depth = 0)
        {
            uint rva;
            if (name.StartsWith('#'))
            {
                if (!uint.TryParse(name.AsSpan(1), out uint ordinal) || !module.Image.ExportsByOrdinal.TryGetValue(ordinal, out rva))
                {
                    return null;
                }
            }
            else if (!module.Image.Exports.TryGetValue(name, out rva))
            {
                return null;
            }

            if (module.Image.ForwarderOf(rva) is not { } forwarder)
            {
                return module.ImageBase + rva;
            }

            // "OTHERDLL.Function" or "OTHERDLL.#12". The module name may itself contain dots, the
            // function name never does, so the split is at the last one.
            int dot = forwarder.LastIndexOf('.');
            if (dot <= 0 || depth > 16)
            {
                return null;
            }

            string dll = forwarder[..dot] + ".dll";
            string function = forwarder[(dot + 1)..];
            if (IsSystemDll(dll) || FindOrLoadModule(dll, out _) is not { } target)
            {
                NoteSystemModule(dll);
                return ThumbEntry(ProcedureTrap(dll, function));
            }

            AddDependency(module, target);
            return ResolveExport(target, function, depth + 1);
        }

        /// <summary>A trap slot answering <c>dll!function</c> from the host stubs, one per name.</summary>
        private long ProcedureTrap(string dll, string function)
        {
            string fullName = $"{dll}!{function}";
            if (!_procedureTraps.TryGetValue(fullName, out long trap))
            {
                trap = AllocateTrapSlot(fullName, TrapKind.Import, handler: null);
                _procedureTraps[fullName] = trap;
            }

            return trap;
        }

        // -----------------------------------------------------------------------------
        // DllMain and TLS callbacks
        // -----------------------------------------------------------------------------

        /// <summary>One loader notification: a TLS callback or the DllMain of a module.</summary>
        private readonly record struct LoaderCall(LoadedModule Module, long Address, bool IsEntryPoint, bool LastForModule);

        /// <summary>
        /// For each module in turn: its TLS callbacks, then its DllMain when
        /// <paramref name="includeEntry"/> allows (always, by default). The order LdrpCallInitRoutine
        /// and LdrpCallTlsInitializers are used in, for every reason.
        /// </summary>
        private static List<LoaderCall> NotificationCalls(IEnumerable<LoadedModule> modules, Func<LoadedModule, bool>? includeEntry = null)
        {
            List<LoaderCall> calls = new();
            foreach (LoadedModule module in modules)
            {
                int first = calls.Count;
                foreach (long callback in module.TlsCallbacks)
                {
                    calls.Add(new LoaderCall(module, callback, IsEntryPoint: false, LastForModule: false));
                }

                if (module.EntryPoint != 0 && (includeEntry?.Invoke(module) ?? true))
                {
                    calls.Add(new LoaderCall(module, module.EntryPoint, IsEntryPoint: true, LastForModule: false));
                }

                if (calls.Count > first)
                {
                    calls[^1] = calls[^1] with { LastForModule = true };
                }
            }

            return calls;
        }

        /// <summary>
        /// Arranges for the DLLs loaded at start-up to get DllMain(DLL_PROCESS_ATTACH, static) -
        /// dependencies before dependants, each one's TLS callbacks first - then the main image's
        /// own TLS callbacks, on the main thread; then for the CPU to continue at
        /// <paramref name="start"/> with the registers exactly as the caller had set them.
        /// </summary>
        /// <returns>The address to start the CPU at: <paramref name="start"/> when there is nothing to run.</returns>
        public long StartWithModuleInitialisers(long start)
        {
            List<LoadedModule> modules = TakePendingInitialisers();
            List<LoaderCall> calls = NotificationCalls(modules);

            // The main image's TLS callbacks run after every start-up DLL and before its entry point
            // (for a component, before the DllMain its host is about to call). Once.
            LoadedModule? main = _modules.FirstOrDefault(m => m.IsMain);
            bool mainNow = main is not null && !_mainImageInitialised && !LoaderDisabled;
            if (mainNow)
            {
                _mainImageInitialised = true;
                calls.AddRange(main!.TlsCallbacks.Select(c => new LoaderCall(main, c, IsEntryPoint: false, LastForModule: false)));
            }

            if (calls.Count == 0)
            {
                if (mainNow)
                {
                    AddInitialised(main!);
                }

                return start;
            }

            // The registers the caller set up for `start`. Not CPSR: it was captured before the CPU
            // ever ran (ARM state), and writing it from inside a Thumb trap slot would switch the
            // slot's own `bx r12` to ARM decoding.
            int[] ids = CoreRegisters.Where(r => r != Arm.UC_ARM_REG_CPSR).ToArray();
            long[] saved = ids.Select(_cpu.RegRead).ToArray();
            CallLoaderChain(calls, 0, DllProcessAttach, staticLoad: true, after: (module, ok) =>
            {
                if (module.IsMain)
                {
                    return;   // its TLS callbacks: nothing to record
                }

                module.ProcessAttached = ok;
                ModuleLog.Add($"DllMain({module.Name}, DLL_PROCESS_ATTACH) returned {(ok ? "TRUE" : "FALSE")}");
                if (ok)
                {
                    AddInitialised(module);
                }
                else
                {
                    InitialisationFailures.Add(
                        $"{module.Name}: DllMain(DLL_PROCESS_ATTACH) returned FALSE; on a device the process would not start (STATUS_DLL_INIT_FAILED)");
                }
            }, done: () =>
            {
                if (mainNow)
                {
                    AddInitialised(main!);
                }

                for (int i = 0; i < ids.Length; i++)
                {
                    _cpu.RegWrite(ids[i], saved[i]);
                }

                _cpu.RegWrite(Arm.UC_ARM_REG_R12, start);
            });

            return calls[0].Address;
        }

        private void AddInitialised(LoadedModule module)
        {
            if (!_initialisationOrder.Contains(module))
            {
                _initialisationOrder.Add(module);
            }
        }

        /// <summary>
        /// The modules mapped since the last call that have something to notify; the rest are
        /// marked attached (and initialised) now.
        /// </summary>
        private List<LoadedModule> TakePendingInitialisers()
        {
            List<LoadedModule> modules = new();
            foreach (LoadedModule module in _pendingInitialisers)
            {
                if (module.HasNotifications)
                {
                    modules.Add(module);
                }
                else
                {
                    module.ProcessAttached = true;
                    AddInitialised(module);
                }
            }

            _pendingInitialisers.Clear();
            return modules;
        }

        /// <summary>
        /// Calls each loader notification (hinst, reason, reserved) in turn as guest calls, then
        /// <paramref name="done"/> - which, like any return-trap continuation, must set r12.
        /// <paramref name="after"/> gets each module's DllMain result, or TRUE after the last TLS
        /// callback of a module without one. Only legal before the CPU starts or inside a trap handler.
        /// </summary>
        private void CallLoaderChain(
            IReadOnlyList<LoaderCall> calls, int index, uint reason, bool staticLoad, Action<LoadedModule, bool>? after, Action done)
        {
            if (index >= calls.Count)
            {
                done();
                return;
            }

            LoaderCall call = calls[index];
            LoadedModule module = call.Module;

            // lpReserved: non-NULL for a static load's PROCESS_ATTACH, NULL for everything else
            // (FreeLibrary's PROCESS_DETACH included). A TLS callback gets the same.
            long reserved = reason == DllProcessAttach && staticLoad ? 1 : 0;
            string label = call.IsEntryPoint ? $"{module.Name}!DllMain({reason})" : $"{module.Name}!TlsCallback({reason})";
            CallEmulated(label, call.Address, [module.ImageBase, reason, reserved], onReturn: () =>
            {
                if (call.IsEntryPoint)
                {
                    after?.Invoke(module, (_cpu.RegRead(Arm.UC_ARM_REG_R0) & 0xFFFFFFFFL) != 0);
                }
                else if (call.LastForModule)
                {
                    after?.Invoke(module, true);
                }

                CallLoaderChain(calls, index + 1, reason, staticLoad, after, done);
            }, recycle: true);
        }

        /// <summary>
        /// Runs PROCESS_ATTACH (TLS callbacks, then DllMain) for what a runtime load just mapped -
        /// everything in the pending list beyond <paramref name="earlier"/>, which is put back.
        /// <paramref name="finished"/> gets TRUE, or FALSE once a refusing DllMain has been undone:
        /// it and everything attached before it get PROCESS_DETACH and every module the load mapped
        /// is unloaded. When nothing has to run, <paramref name="finished"/> is called before this
        /// returns and r12 is untouched; otherwise the caller's return address is put back after it.
        /// </summary>
        private void InitialiseDynamicLoad(List<LoadedModule> earlier, Action<bool> finished)
        {
            List<LoadedModule> fresh = _pendingInitialisers.ToList();
            List<LoadedModule> initialise = TakePendingInitialisers();
            _pendingInitialisers.AddRange(earlier);
            List<LoaderCall> calls = NotificationCalls(initialise);
            if (calls.Count == 0)
            {
                finished(true);
                return;
            }

            long returnTo = _cpu.RegRead(Arm.UC_ARM_REG_LR);
            List<LoadedModule> attached = new();
            LoadedModule? failed = null;
            CallLoaderChain(calls, 0, DllProcessAttach, staticLoad: false, after: (m, ok) =>
            {
                ModuleLog.Add($"DllMain({m.Name}, DLL_PROCESS_ATTACH) returned {(ok ? "TRUE" : "FALSE")} (dynamic load)");
                m.ProcessAttached = ok;
                if (ok)
                {
                    attached.Add(m);
                    AddInitialised(m);
                }
                else
                {
                    failed ??= m;
                }
            }, done: () =>
            {
                if (failed is null)
                {
                    finished(true);
                    _cpu.RegWrite(Arm.UC_ARM_REG_R12, returnTo);
                    return;
                }

                // A DllMain that refused: the loader calls it again with DLL_PROCESS_DETACH, undoes
                // the load, and LoadLibrary returns NULL with ERROR_DLL_INIT_FAILED.
                List<LoadedModule> detach = [failed, .. Enumerable.Reverse(attached)];
                CallLoaderChain(NotificationCalls(detach), 0, DllProcessDetach, staticLoad: false, after: null, done: () =>
                {
                    foreach (LoadedModule m in fresh)
                    {
                        UnmapModule(m);
                    }

                    finished(false);
                    _cpu.RegWrite(Arm.UC_ARM_REG_R12, returnTo);
                });
            });
        }

        // -----------------------------------------------------------------------------
        // Thread notifications
        // -----------------------------------------------------------------------------

        /// <summary>
        /// DLL_THREAD_ATTACH (or, <paramref name="detach"/>, DETACH) calls owed, in initialisation
        /// order (reversed for detach): each attached module's TLS callbacks, then its DllMain unless
        /// it opted out. The main image's DllMain is a DLL's only (a component); an exe's entry point
        /// is not one, so an exe contributes its TLS callbacks alone.
        /// </summary>
        private List<LoaderCall> ThreadNotificationCalls(bool detach)
        {
            List<LoadedModule> modules = new();
            foreach (LoadedModule module in _initialisationOrder)
            {
                // A component main image's DllMain is called by its host (XamlInterop) and has
                // run by the time anything can start a thread.
                if (!module.Unloaded && (module.ProcessAttached || module.IsMain))
                {
                    modules.Add(module);
                }
            }

            if (detach)
            {
                modules.Reverse();
            }

            return NotificationCalls(modules, includeEntry: m => m.Image.IsDll && !m.ThreadCallsDisabled);
        }

        /// <summary>
        /// Where a new guest thread should start: <paramref name="entry"/> itself, or - when modules
        /// want thread notifications - a trampoline that makes their DLL_THREAD_ATTACH calls on the
        /// new thread first, and the DLL_THREAD_DETACH ones when its routine returns.
        /// </summary>
        private long ThreadStartThroughLoader(int threadId, long entry)
        {
            if (LoaderDisabled || ThreadNotificationCalls(detach: false).Count == 0)
            {
                return entry;
            }

            _threadStarts[threadId] = entry;
            if (_threadAttachTrap == 0)
            {
                _threadAttachTrap = AllocateTrapSlot("loader: DLL_THREAD_ATTACH", TrapKind.Return, OnThreadAttach);
                _threadDetachTrap = AllocateTrapSlot("loader: DLL_THREAD_DETACH", TrapKind.Return, OnThreadDetach);
            }

            return ThumbEntry(_threadAttachTrap);
        }

        private void OnThreadAttach()
        {
            if (!_threadStarts.Remove(CurrentThreadId, out long entry))
            {
                return;
            }

            long argument = _cpu.RegRead(Arm.UC_ARM_REG_R0);
            CallLoaderChain(ThreadNotificationCalls(detach: false), 0, DllThreadAttach, staticLoad: false, after: null, done: () =>
            {
                _cpu.RegWrite(Arm.UC_ARM_REG_R0, argument);
                _cpu.RegWrite(Arm.UC_ARM_REG_LR, ThumbEntry(_threadDetachTrap));
                _cpu.RegWrite(Arm.UC_ARM_REG_R12, entry);
            });
        }

        private void OnThreadDetach()
        {
            long exitCode = _cpu.RegRead(Arm.UC_ARM_REG_R0);
            CallLoaderChain(ThreadNotificationCalls(detach: true), 0, DllThreadDetach, staticLoad: false, after: null, done: () =>
            {
                _cpu.RegWrite(Arm.UC_ARM_REG_R0, exitCode);
                _cpu.RegWrite(Arm.UC_ARM_REG_R12, ThumbEntry(ThreadExitTrap()));
            });
        }

        // -----------------------------------------------------------------------------
        // The library loader API (called from the host stubs, inside a trap)
        // -----------------------------------------------------------------------------

        public const int ErrorModNotFound = 126;
        public const int ErrorProcNotFound = 127;
        public const int ErrorBadExeFormat = 193;
        public const int ErrorInvalidParameter = 87;
        public const int ErrorNotEnoughMemory = 8;
        public const int ErrorDllInitFailed = 1114;
        public const int ErrorInsufficientBuffer = 122;

        /// <summary>
        /// LoadPackagedLibrary / LoadLibraryEx: the module's handle, loading it (and running the
        /// DllMain of everything newly mapped) when it is not loaded yet. <paramref name="finish"/>
        /// gets the handle (0 on failure) and a Win32 error, and must only set r0 and the last
        /// error: the loader puts the caller's return address back itself.
        /// </summary>
        /// <param name="relativePath">A path inside the package (checked by the caller).</param>
        /// <param name="allowSystem">Answer a system DLL that is already loaded with its handle (LoadLibraryEx).</param>
        public void LoadLibraryFromGuest(string relativePath, bool allowSystem, Action<long, int> finish)
        {
            string name = NormaliseModuleName(relativePath);

            if (_modules.FirstOrDefault(m => !m.Unloaded && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) is { } loaded)
            {
                if (!loaded.Pinned)
                {
                    loaded.ReferenceCount++;
                }

                finish(loaded.ImageBase, 0);
                return;
            }

            string? path = ResolvePackagePath(relativePath, name);
            if (path is null || IsSystemDll(name))
            {
                if (allowSystem && _systemModules.ContainsKey(name))
                {
                    finish(SystemModuleHandle(name), 0);
                    return;
                }

                finish(0, ErrorModNotFound);
                return;
            }

            // Only what this load maps is initialised by it; anything still pending from start-up
            // (a host that never called StartWithModuleInitialisers) stays pending.
            List<LoadedModule> earlier = _pendingInitialisers.ToList();
            _pendingInitialisers.Clear();

            LoadedModule? module;
            _loadingDynamically = true;
            LoadedModule? outer = _bindingFor;
            _bindingFor = null;
            try
            {
                module = LoadModuleFile(path, out int error);
                if (module is null)
                {
                    _pendingInitialisers.AddRange(earlier);
                    finish(0, error);
                    return;
                }
            }
            finally
            {
                _loadingDynamically = false;
                _bindingFor = outer;
            }

            if (!module.Pinned)
            {
                module.ReferenceCount++;
            }

            InitialiseDynamicLoad(earlier, ok => finish(ok ? module.ImageBase : 0, ok ? 0 : ErrorDllInitFailed));
        }

        /// <summary>
        /// A path the guest named, inside the package root: case-insensitive per segment, ".dll"
        /// appended to a bare name. Null when there is no such file.
        /// </summary>
        private string? ResolvePackagePath(string relativePath, string normalisedName)
        {
            string[] segments = relativePath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length <= 1)
            {
                return PackageDlls().GetValueOrDefault(normalisedName);
            }

            string current = _packageRoot;
            for (int i = 0; i < segments.Length; i++)
            {
                string wanted = i == segments.Length - 1 ? normalisedName : segments[i];
                if (!Directory.Exists(current))
                {
                    return null;
                }

                string? match = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(e => string.Equals(Path.GetFileName(e), wanted, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    return null;
                }

                current = match;
            }

            return File.Exists(current) ? current : null;
        }

        /// <summary>
        /// FreeLibrary: drops a reference, and once none remain calls DllMain(DLL_PROCESS_DETACH)
        /// and unmaps the module - then its dependencies, which may follow it out. A pinned module,
        /// the main image and system DLLs are never unloaded. <paramref name="finish"/> gets the
        /// BOOL result and a Win32 error.
        /// </summary>
        public void FreeLibraryFromGuest(long handle, Action<bool, int> finish)
        {
            LoadedModule? module = ModuleByHandle(handle);
            if (module is null || module.Unloaded)
            {
                if (SystemModuleNameOf(handle) is not null)
                {
                    finish(true, 0);
                    return;
                }

                finish(false, ErrorModNotFound);
                return;
            }

            if (module.Pinned)
            {
                finish(true, 0);
                return;
            }

            List<LoadedModule> unload = new();
            Release(module, unload);
            if (unload.Count == 0)
            {
                finish(true, 0);
                return;
            }

            long returnTo = _cpu.RegRead(Arm.UC_ARM_REG_LR);
            List<LoadedModule> detach = unload.Where(m => m.ProcessAttached).ToList();
            CallLoaderChain(NotificationCalls(detach), 0, DllProcessDetach, staticLoad: false, after: (m, _) => m.ProcessAttached = false, done: () =>
            {
                foreach (LoadedModule m in unload)
                {
                    UnmapModule(m);
                }

                finish(true, 0);
                _cpu.RegWrite(Arm.UC_ARM_REG_R12, returnTo);
            });
        }

        /// <summary>Drops one reference; collects, dependants first, every module that reaches zero.</summary>
        private static void Release(LoadedModule module, List<LoadedModule> unload)
        {
            if (module.Pinned || module.Unloaded || unload.Contains(module))
            {
                return;
            }

            module.ReferenceCount--;
            if (module.ReferenceCount > 0)
            {
                return;
            }

            unload.Add(module);
            foreach (LoadedModule dependency in module.Dependencies)
            {
                Release(dependency, unload);
            }
        }

        /// <summary>
        /// Takes a module out of the address space. The CPU interface cannot unmap (and the
        /// native shim is not rebuilt for it), so the range stays mapped with no access - a stale
        /// pointer into it faults rather than reading a module that is gone - and is recorded as
        /// reusable: the next module that wants that range gets it, re-protected and overwritten
        /// whole, exactly as if it had been unmapped and mapped again.
        /// </summary>
        private void UnmapModule(LoadedModule module)
        {
            if (module.Unloaded)
            {
                return;
            }

            DetachStaticTls(module);
            module.Unloaded = true;
            _modules.Remove(module);
            _pendingInitialisers.Remove(module);
            _initialisationOrder.Remove(module);
            foreach (LoadedModule other in _modules)
            {
                other.Dependencies.Remove(module);
            }

            Unwinder.RemoveImage(module.ImageBase);
            try
            {
                _cpu.MemProtect(module.ImageBase, module.MappedSize, 0);
                for (long page = module.ImageBase; page < module.ImageBase + module.MappedSize; page += PageSize)
                {
                    _retiredPages.Add(page);
                }
            }
            catch (CpuMemoryException)
            {
                // Not mapped as a whole after all: leave it out of the reusable set.
            }

            ModuleLog.Add($"unloaded {module.Name} from 0x{module.ImageBase:X8}; its range is free for reuse");
        }

        /// <summary>
        /// GetModuleHandle: NULL is the main image; otherwise a loaded module by file name, or a
        /// system DLL the process has loaded (imported by some module). 0 when it is not loaded.
        /// </summary>
        public long ModuleHandleByName(string? name)
        {
            if (name is null)
            {
                return _image.ImageBase;
            }

            string file = NormaliseModuleName(name);
            if (_modules.FirstOrDefault(m => !m.Unloaded && string.Equals(m.Name, file, StringComparison.OrdinalIgnoreCase)) is { } module)
            {
                return module.ImageBase;
            }

            return _systemModules.ContainsKey(file) || file is "ntdll.dll" or "kernelbase.dll"
                ? SystemModuleHandle(file)
                : 0;
        }

        /// <summary>GetModuleHandleEx(FROM_ADDRESS): the module containing an address, or 0.</summary>
        public long ModuleHandleByAddress(long address)
        {
            if (ModuleAt(address) is { Unloaded: false } module)
            {
                return module.ImageBase;
            }

            foreach (long handle in _systemModules.Values)
            {
                if (handle != 0 && address >= handle && address < handle + PageSize)
                {
                    return handle;
                }
            }

            return 0;
        }

        /// <summary>Adds a reference (or pins) for GetModuleHandleEx without UNCHANGED_REFCOUNT.</summary>
        public void ReferenceModule(long handle, bool pin)
        {
            if (ModuleByHandle(handle) is { Unloaded: false } module)
            {
                if (pin)
                {
                    module.Pinned = true;
                }
                else if (!module.Pinned)
                {
                    module.ReferenceCount++;
                }
            }
        }

        /// <summary>The path GetModuleFileName reports for a handle (0 = main image), or null.</summary>
        public string? ModuleFileName(long handle)
        {
            if (handle == 0)
            {
                return _modules.FirstOrDefault(m => m.IsMain)?.GuestPath;
            }

            if (ModuleByHandle(handle) is { Unloaded: false } module)
            {
                return module.GuestPath;
            }

            return SystemModuleNameOf(handle) is { } system ? SystemFolder + system : null;
        }

        /// <summary>DisableThreadLibraryCalls: no more DLL_THREAD_ATTACH/DETACH for that module.</summary>
        public bool DisableThreadNotifications(long handle)
        {
            if (ModuleByHandle(handle) is { } module)
            {
                // A module with static TLS keeps its notifications: the loader still owes it its
                // TLS callbacks per thread. Windows (LdrDisableThreadCalloutsForDll) succeeds and
                // changes nothing for such a module.
                if (module.Tls is null)
                {
                    module.ThreadCallsDisabled = true;
                }

                return true;
            }

            return SystemModuleNameOf(handle) is not null;
        }

        /// <summary>
        /// GetProcAddress from guest code: <see cref="ProcedureAddress"/>, plus what Windows does
        /// when the export is forwarded to a package DLL that is not loaded yet - the DLL is loaded
        /// (holding a reference for the forwarding module) and its TLS callbacks and DllMain run
        /// before the address is returned. A refusing DllMain undoes that load and the call fails
        /// with ERROR_DLL_INIT_FAILED. <paramref name="finish"/> must only set r0 and the last error.
        /// </summary>
        public void ProcedureAddressFromGuest(long handle, string procedure, Action<long?, int> finish)
        {
            List<LoadedModule> earlier = _pendingInitialisers.ToList();
            _pendingInitialisers.Clear();
            bool outer = _loadingDynamically;
            _loadingDynamically = true;
            long? address;
            int error;
            try
            {
                address = ProcedureAddress(handle, procedure, out error);
            }
            finally
            {
                _loadingDynamically = outer;
            }

            if (_pendingInitialisers.Count == 0)
            {
                _pendingInitialisers.AddRange(earlier);
                finish(address, error);
                return;
            }

            InitialiseDynamicLoad(earlier, ok =>
            {
                if (ok)
                {
                    finish(address, error);
                }
                else
                {
                    UnresolvedProcedures.Add($"{ModuleByHandle(handle)?.Name ?? "?"}!{procedure}: the DLL it forwards to failed DllMain");
                    finish(null, ErrorDllInitFailed);
                }
            });
        }

        /// <summary>
        /// GetProcAddress: a package module's export (name or ordinal, forwarders followed), or for
        /// a system DLL a trap slot into the host stubs when the host implements that function.
        /// Null when there is no such procedure, with the Win32 error in <paramref name="error"/>.
        /// </summary>
        public long? ProcedureAddress(long handle, string procedure, out int error)
        {
            error = 0;
            LoadedModule? module = handle == 0 ? _modules.FirstOrDefault(m => m.IsMain) : ModuleByHandle(handle);
            if (module is { Unloaded: false })
            {
                if (ResolveExport(module, procedure) is { } address)
                {
                    return address;
                }

                UnresolvedProcedures.Add($"{module.Name}!{procedure}");
                error = ErrorProcNotFound;
                return null;
            }

            if (SystemModuleNameOf(handle) is { } system)
            {
                if (_stubs.IsImplemented(procedure))
                {
                    return ThumbEntry(ProcedureTrap(system, procedure));
                }

                UnresolvedProcedures.Add($"{system}!{procedure}");
                error = ErrorProcNotFound;
                return null;
            }

            error = ErrorModNotFound;
            return null;
        }

        /// <summary>True when the address is in an executable section of any loaded image.</summary>
        private bool IsInModuleCode(long target)
        {
            if (_modules.Count == 0)
            {
                return IsInImageCode(_image, _image.ImageBase, target, mainRule: true);
            }

            foreach (LoadedModule module in _modules)
            {
                if (module.Contains(target) && IsInImageCode(module.Image, module.ImageBase, target, module.IsMain))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsInImageCode(PeImage image, long imageBase, long target, bool mainRule)
        {
            foreach (PeSection section in image.Sections)
            {
                // The main image keeps the rule it always had (sections named .text*); a DLL's
                // code is also any section flagged executable.
                bool code = section.Name.StartsWith(".text", StringComparison.Ordinal) || (!mainRule && section.IsExecutable);
                if (!code)
                {
                    continue;
                }

                long start = imageBase + section.VirtualAddress;
                if (target >= start && target < start + section.VirtualSize)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

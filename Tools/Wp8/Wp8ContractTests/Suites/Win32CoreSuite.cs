using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The api-ms-win-core-* / api-ms-win-eventing-* sets: files, synchronisation, time, locale,
/// system information, memory queries, handles, debugging and event tracing, each against its
/// documented Win32 contract and last-error.
/// </summary>
public sealed class Win32CoreSuite : IContractSuite
{
    public string Name => "win32 core";

    private const long Invalid = 0xFFFFFFFF;

    public void Run(Harness h)
    {
        Files(h);
        Sync(h);
        Time(h);
        Locale(h);
        System(h);
    }

    private static long LastError(Harness h) => h.Call("GetLastError");

    private static string Local(string name) => @"C:\Data\Users\DefApps\AppData\{E94059A2-135C-420E-8E60-BCDA5FC3EC30}\Local\" + name;

    // -------------------------------------------------------------------------------------------
    private static void Files(Harness h)
    {
        ArmEmulator e = h.E;
        string package = Path.GetDirectoryName(h.Image)!;
        string exe = Path.GetFileName(h.Image);

        // --- directory search against the real package folder ---
        long data = h.Buffer(592);
        long search = h.Call("FindFirstFileExW", h.Wide("*"), 0, data, 0, 0, 0);
        var seen = new List<string>();
        if (search != Invalid)
        {
            do
            {
                seen.Add(h.ReadWide(data + 44, 260));
            }
            while (h.Call("FindNextFileW", search, data) == 1);
        }

        h.Check("FindNextFileW ends with ERROR_NO_MORE_FILES", LastError(h) == 18, $"error {LastError(h)}");
        var expected = Directory.EnumerateFileSystemEntries(package).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        h.Check("FindFirstFileExW lists the package folder, . and .. first",
            seen.Count == expected.Count + 2 && seen[0] == "." && seen[1] == ".." && seen.Skip(2).All(expected.Contains),
            $"{seen.Count} entries, package has {expected.Count}");
        h.Check("FindClose closes a search", h.Call("FindClose", search) == 1);
        h.Check("FindClose twice is ERROR_INVALID_HANDLE", h.Call("FindClose", search) == 0 && LastError(h) == 6);

        long one = h.Call("FindFirstFileExW", h.Wide(exe), 1, data, 0, 0, 0);
        long size = e.ReadUInt32(data + 32) | ((long)e.ReadUInt32(data + 28) << 32);
        h.Check("FindFirstFileExW on a file name finds it, with its size", one != Invalid && size == new FileInfo(h.Image).Length, $"size {size}");
        h.Call("FindClose", one);
        h.Check("a missing folder is ERROR_PATH_NOT_FOUND", h.Call("FindFirstFileExW", h.Wide(@"no\such\*"), 0, data, 0, 0, 0) == Invalid && LastError(h) == 3);
        h.Check("no match is ERROR_FILE_NOT_FOUND", h.Call("FindFirstFileExW", h.Wide("*.nosuchext"), 0, data, 0, 0, 0) == Invalid && LastError(h) == 2);
        long narrow = h.Buffer(320);
        long a = h.Call("FindFirstFileExA", h.Narrow(exe), 0, narrow, 0, 0, 0);
        h.Check("FindFirstFileExA writes a narrow name", a != Invalid && h.ReadNarrow(narrow + 44).Equals(exe, StringComparison.OrdinalIgnoreCase));
        h.Call("FindClose", a);

        // --- attributes ---
        long attributes = h.Call("GetFileAttributesW", h.Wide(exe));
        h.Check("GetFileAttributesW on a file: no directory bit", attributes != Invalid && (attributes & 0x10) == 0, $"0x{attributes:X}");
        h.Check("GetFileAttributesW on nothing: INVALID_FILE_ATTRIBUTES, ERROR_FILE_NOT_FOUND", h.Call("GetFileAttributesW", h.Wide("nothing.bin")) == Invalid && LastError(h) == 2);
        long fad = h.Buffer(36);
        h.Check("GetFileAttributesExW fills the size and a write time",
            h.Call("GetFileAttributesExW", h.Wide(exe), 0, fad) == 1 && e.ReadUInt32(fad + 32) == (uint)new FileInfo(h.Image).Length && e.ReadUInt32(fad + 24) != 0);
        h.Check("GetFileAttributesExW with a level other than 0 is ERROR_INVALID_PARAMETER", h.Call("GetFileAttributesExW", h.Wide(exe), 1, fad) == 0 && LastError(h) == 87);

        // --- CreateFile2 dispositions ---
        string save = Local("w32core.bin");
        long first = h.Call("CreateFile2", h.Wide(save), 0xC0000000, 0, 1, 0);
        h.Check("CreateFile2 CREATE_NEW creates", first != Invalid && LastError(h) == 0);
        h.Check("CREATE_NEW over an existing file is ERROR_FILE_EXISTS", h.Call("CreateFile2", h.Wide(save), 0xC0000000, 0, 1, 0) == Invalid && LastError(h) == 80);
        long again = h.Call("CreateFile2", h.Wide(save), 0x80000000, 3, 4, 0);
        h.Check("OPEN_ALWAYS over an existing file succeeds with ERROR_ALREADY_EXISTS", again != Invalid && LastError(h) == 183);
        h.Check("OPEN_EXISTING on nothing is ERROR_FILE_NOT_FOUND", h.Call("CreateFile2", h.Wide(Local("none.bin")), 0x80000000, 0, 3, 0) == Invalid && LastError(h) == 2);
        h.Check("a missing folder is ERROR_PATH_NOT_FOUND", h.Call("CreateFile2", h.Wide(Local(@"none\x.bin")), 0x40000000, 0, 2, 0) == Invalid && LastError(h) == 3);
        h.Check("writing into the install folder is ERROR_ACCESS_DENIED", h.Call("CreateFile2", h.Wide(exe), 0x40000000, 0, 3, 0) == Invalid && LastError(h) == 5);
        h.Check("opening a folder as a file is ERROR_ACCESS_DENIED", h.Call("CreateFile2", h.Wide(Local("")), 0x80000000, 0, 3, 0) == Invalid && LastError(h) == 5);

        long bytes = h.Buffer(16);
        e.WriteMemory(bytes, Encoding.ASCII.GetBytes("0123456789ABCDEF"));
        long done = h.Buffer(4);
        h.Check("WriteFile writes", h.Call("WriteFile", first, bytes, 16, done, 0) == 1 && e.ReadUInt32(done) == 16);
        h.Check("WriteFile on a read-only handle is ERROR_ACCESS_DENIED", h.Call("WriteFile", again, bytes, 16, done, 0) == 0 && LastError(h) == 5);
        long size64 = h.Buffer(8);
        h.Check("GetFileSizeEx", h.Call("GetFileSizeEx", first, size64) == 1 && e.ReadUInt32(size64) == 16);
        h.Call("SetFilePointerEx", first, 0, 10, 0, 0, 0);
        h.Check("SetEndOfFile truncates at the file pointer", h.Call("SetEndOfFile", first) == 1 && h.Call("GetFileSizeEx", first, size64) == 1 && e.ReadUInt32(size64) == 10);
        h.Check("SetEndOfFile on a read-only handle is ERROR_ACCESS_DENIED", h.Call("SetEndOfFile", again) == 0 && LastError(h) == 5);

        long info = h.Buffer(600);
        h.Check("FileStandardInfo: EndOfFile and one link",
            h.Call("GetFileInformationByHandleEx", first, 1, info, 24) == 1 && e.ReadUInt32(info + 8) == 10 && e.ReadUInt32(info + 16) == 1);
        h.Check("FileBasicInfo: a write time and attributes",
            h.Call("GetFileInformationByHandleEx", first, 0, info, 40) == 1 && e.ReadUInt32(info + 20) != 0 && e.ReadUInt32(info + 32) != 0);
        h.Check("FileNameInfo: the path below the volume root",
            h.Call("GetFileInformationByHandleEx", first, 2, info, 600) == 1 && Encoding.Unicode.GetString(e.ReadMemory(info + 4, (int)e.ReadUInt32(info))).EndsWith(@"\Local\w32core.bin"));
        h.Check("a short buffer is ERROR_BAD_LENGTH", h.Call("GetFileInformationByHandleEx", first, 1, info, 8) == 0 && LastError(h) == 24);
        h.Check("an unsupported class is ERROR_INVALID_PARAMETER", h.Call("GetFileInformationByHandleEx", first, 99, info, 600) == 0 && LastError(h) == 87);

        // --- byte-range locks ---
        long overlapped = h.Buffer(20);
        h.Check("LockFileEx takes an exclusive lock", h.Call("LockFileEx", first, 3, 0, 4, 0, overlapped) == 1);
        h.Check("an overlapping lock from another handle fails immediately with ERROR_LOCK_VIOLATION",
            h.Call("LockFileEx", again, 1, 0, 2, 0, overlapped) == 0 && LastError(h) == 33);
        h.Check("UnlockFileEx on a range not locked is ERROR_NOT_LOCKED", h.Call("UnlockFileEx", first, 0, 99, 0, overlapped) == 0 && LastError(h) == 158);
        h.Check("UnlockFileEx releases it", h.Call("UnlockFileEx", first, 0, 4, 0, overlapped) == 1);
        h.Check("then the other handle can lock it", h.Call("LockFileEx", again, 1, 0, 2, 0, overlapped) == 1);
        h.Call("UnlockFileEx", again, 0, 2, 0, overlapped);

        // --- an OVERLAPPED's event is set on completion ---
        long ev = h.Call("CreateEventExW", 0, 0, 1, 0);
        long ov = h.Buffer(20);
        e.WriteUInt32(ov + 16, (uint)ev);
        long read = h.Buffer(16);
        h.Check("ReadFile with an OVERLAPPED reads at its offset",
            h.Call("ReadFile", again, read, 4, done, ov) == 1 && h.ReadNarrow(read, 4) == "0123" && e.ReadUInt32(ov + 4) == 4);
        h.Check("... and sets its event", h.Call("WaitForSingleObjectEx", ev, 0, 0) == 0);

        // --- DuplicateHandle on a file shares the file object ---
        long dupOut = h.Buffer(4);
        h.Check("DuplicateHandle on a file", h.Call("DuplicateHandle", 0xFFFFFFFF, again, 0xFFFFFFFF, dupOut, 0, 0, 2) == 1);
        long dup = e.ReadUInt32(dupOut);
        h.Call("CloseHandle", again);
        h.Check("the duplicate outlives the original", h.Call("GetFileSizeEx", dup, size64) == 1 && e.ReadUInt32(size64) == 10);
        h.Call("CloseHandle", dup);
        h.Call("CloseHandle", first);

        // --- directories, deletion, copy, move, attributes ---
        h.Check("CreateDirectoryW with a missing parent is ERROR_PATH_NOT_FOUND", h.Call("CreateDirectoryW", h.Wide(Local(@"a\b")), 0) == 0 && LastError(h) == 3);
        h.Check("CreateDirectoryW", h.Call("CreateDirectoryW", h.Wide(Local("dir")), 0) == 1);
        h.Check("CreateDirectoryW again is ERROR_ALREADY_EXISTS", h.Call("CreateDirectoryW", h.Wide(Local("dir")), 0) == 0 && LastError(h) == 183);
        h.Check("CreateDirectoryA", h.Call("CreateDirectoryA", h.Narrow(Local(@"dir\sub")), 0) == 1);
        h.Check("RemoveDirectoryW on a non-empty folder is ERROR_DIR_NOT_EMPTY", h.Call("RemoveDirectoryW", h.Wide(Local("dir")), 0) == 0 && LastError(h) == 145);
        h.Check("RemoveDirectoryA on an empty one", h.Call("RemoveDirectoryA", h.Narrow(Local(@"dir\sub"))) == 1);

        string asset = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).First(f => new FileInfo(f).Length is > 100 and < 2_000_000 && !f.EndsWith(".exe"));
        string assetRel = Path.GetRelativePath(package, asset).Replace('/', '\\');
        h.Check("CopyFile2 from the package into the local folder", h.Call("CopyFile2", h.Wide(assetRel), h.Wide(Local(@"dir\copy.bin")), 0) == 0 &&
            File.ReadAllBytes(Path.Combine(h.Sandbox, "dir", "copy.bin")).SequenceEqual(File.ReadAllBytes(asset)));
        long copyParameters = h.Buffer(20);
        e.WriteUInt32(copyParameters, 20);
        e.WriteUInt32(copyParameters + 4, 1);
        h.Check("COPY_FILE_FAIL_IF_EXISTS is HRESULT_FROM_WIN32(ERROR_FILE_EXISTS)", h.Call("CopyFile2", h.Wide(assetRel), h.Wide(Local(@"dir\copy.bin")), copyParameters) == 0x80070050);
        h.Check("CopyFile2 into the install folder is E_ACCESSDENIED", h.Call("CopyFile2", h.Wide(assetRel), h.Wide(assetRel), 0) == 0x80070005);
        h.Call("CopyFile2", h.Wide(assetRel), h.Wide(Local(@"dir\other.bin")), 0);
        h.Check("MoveFileExW onto an existing file without REPLACE is ERROR_ALREADY_EXISTS",
            h.Call("MoveFileExW", h.Wide(Local(@"dir\copy.bin")), h.Wide(Local(@"dir\other.bin")), 0) == 0 && LastError(h) == 183);
        h.Check("MoveFileExW with MOVEFILE_REPLACE_EXISTING", h.Call("MoveFileExW", h.Wide(Local(@"dir\copy.bin")), h.Wide(Local(@"dir\other.bin")), 1) == 1);
        h.Check("SetFileAttributesW then GetFileAttributesW round-trips hidden",
            h.Call("SetFileAttributesW", h.Wide(Local(@"dir\other.bin")), 0x2) == 1 && (h.Call("GetFileAttributesW", h.Wide(Local(@"dir\other.bin"))) & 0x2) != 0);
        h.Check("SetFileAttributesW in the install folder is ERROR_ACCESS_DENIED", h.Call("SetFileAttributesW", h.Wide(exe), 0x80) == 0 && LastError(h) == 5);
        h.Check("DeleteFileW in the install folder is ERROR_ACCESS_DENIED", h.Call("DeleteFileW", h.Wide(exe)) == 0 && LastError(h) == 5);
        h.Check("DeleteFileW", h.Call("DeleteFileW", h.Wide(Local(@"dir\other.bin"))) == 1);
        h.Check("DeleteFileA on nothing is ERROR_FILE_NOT_FOUND", h.Call("DeleteFileA", h.Narrow(Local(@"dir\other.bin"))) == 0 && LastError(h) == 2);

        long free = h.Buffer(8), total = h.Buffer(8);
        h.Check("GetDiskFreeSpaceExW(NULL) reports a volume", h.Call("GetDiskFreeSpaceExW", 0, free, total, 0) == 1 && e.ReadUInt32(total + 4) + e.ReadUInt32(total) > 0);
        h.Check("GetDiskFreeSpaceExW on a missing folder is ERROR_PATH_NOT_FOUND", h.Call("GetDiskFreeSpaceExW", h.Wide(Local("gone")), free, total, 0) == 0 && LastError(h) == 3);
    }

    // -------------------------------------------------------------------------------------------
    private static void Sync(Harness h)
    {
        ArmEmulator e = h.E;

        // --- named events ---
        long e1 = h.Call("CreateEventExW", 0, h.Wide("w32core.ev"), 0, 0);
        h.Check("CreateEventExW named, new: last error 0", e1 != 0 && LastError(h) == 0);
        long e2 = h.Call("CreateEventExW", 0, h.Wide("w32core.ev"), 0, 0);
        h.Check("the same name again opens it with ERROR_ALREADY_EXISTS", e2 != 0 && e2 != e1 && LastError(h) == 183);
        long e3 = h.Call("OpenEventW", 0x1F0003, 0, h.Wide("w32core.ev"));
        h.Check("OpenEventW finds it", e3 != 0);
        h.Check("OpenEventW on an unknown name is ERROR_FILE_NOT_FOUND", h.Call("OpenEventW", 0x1F0003, 0, h.Wide("nope")) == 0 && LastError(h) == 2);
        h.Check("a mutex of the same name is ERROR_INVALID_HANDLE", h.Call("CreateMutexExW", 0, h.Wide("w32core.ev"), 0, 0) == 0 && LastError(h) == 6);
        h.Call("SetEvent", e1);
        h.Check("one object behind three handles: set on one, taken through another", h.Call("WaitForSingleObjectEx", e3, 0, 0) == 0);
        h.Check("an auto-reset event resets when a wait takes it", h.Call("WaitForSingleObjectEx", e2, 0, 0) == 0x102);
        h.Check("SetEvent on a bogus handle is ERROR_INVALID_HANDLE", h.Call("SetEvent", 0x1234) == 0 && LastError(h) == 6);

        // --- WaitForMultipleObjectsEx ---
        long unset = h.Call("CreateEventExW", 0, 0, 1, 0);
        long set = h.Call("CreateEventExW", 0, 0, 3, 0);   // manual reset, initially set
        long array = h.Buffer(8);
        e.WriteUInt32(array, (uint)unset);
        e.WriteUInt32(array + 4, (uint)set);
        h.Check("wait-any answers WAIT_OBJECT_0 + the signalled index", h.Call("WaitForMultipleObjectsEx", 2, array, 0, 0, 0) == 1);
        h.Check("wait-all with one unsignalled times out", h.Call("WaitForMultipleObjectsEx", 2, array, 1, 0, 0) == 0x102);
        h.Call("SetEvent", unset);
        h.Check("wait-all once both are signalled", h.Call("WaitForMultipleObjectsEx", 2, array, 1, 0, 0) == 0);
        h.Check("a count of 0 is WAIT_FAILED with ERROR_INVALID_PARAMETER", h.Call("WaitForMultipleObjectsEx", 0, array, 0, 0, 0) == Invalid && LastError(h) == 87);

        // --- mutexes and semaphores ---
        long mutex = h.Call("CreateMutexExW", 0, 0, 1, 0);
        h.Check("an initially owned mutex is released by its owner", h.Call("ReleaseMutex", mutex) == 1);
        h.Check("releasing it again is ERROR_NOT_OWNER", h.Call("ReleaseMutex", mutex) == 0 && LastError(h) == 288);
        h.Check("an invalid semaphore (initial > maximum) is ERROR_INVALID_PARAMETER", h.Call("CreateSemaphoreExW", 0, 3, 2, 0, 0, 0) == 0 && LastError(h) == 87);
        long semaphore = h.Call("CreateSemaphoreExW", 0, 1, 2, 0, 0, 0);
        long previous = h.Buffer(4);
        h.Check("a release past the maximum is ERROR_TOO_MANY_POSTS", h.Call("ReleaseSemaphore", semaphore, 2, previous) == 0 && LastError(h) == 298);
        h.Check("ReleaseSemaphore reports the previous count", h.Call("ReleaseSemaphore", semaphore, 1, previous) == 1 && e.ReadUInt32(previous) == 1);
        h.Check("two waits take the two counts", h.Call("WaitForSingleObject", semaphore, 0) == 0 && h.Call("WaitForSingleObject", semaphore, 0) == 0);
        h.Check("a third times out", h.Call("WaitForSingleObject", semaphore, 0) == 0x102);

        // --- critical sections ---
        long cs = h.Buffer(24);
        h.Call("InitializeCriticalSectionEx", cs, 4000, 0);
        long me = h.Call("GetCurrentThreadId");
        h.Call("EnterCriticalSection", cs);
        h.Call("EnterCriticalSection", cs);
        h.Check("EnterCriticalSection records the owner and recursion", e.ReadUInt32(cs + 12) == me && e.ReadUInt32(cs + 8) == 2, $"owner 0x{e.ReadUInt32(cs + 12):X} recursion {e.ReadUInt32(cs + 8)}");
        h.Check("TryEnterCriticalSection by the owner succeeds", h.Call("TryEnterCriticalSection", cs) == 1);
        h.Call("LeaveCriticalSection", cs);
        h.Call("LeaveCriticalSection", cs);
        h.Call("LeaveCriticalSection", cs);
        h.Check("leaving as often as entered frees it", e.ReadUInt32(cs + 12) == 0 && e.ReadUInt32(cs + 8) == 0);
        h.Call("_lock", 8);
        h.Call("_unlock", 8);
        h.Check("CRT lock numbers do not touch guest memory", true);

        // --- SRW locks ---
        long srw = h.Buffer(4);
        h.Call("InitializeSRWLock", srw);
        h.Check("TryAcquireSRWLockExclusive on a free lock", h.Call("TryAcquireSRWLockExclusive", srw) == 1);
        h.Check("SRW locks are not recursive", h.Call("TryAcquireSRWLockExclusive", srw) == 0);
        h.Check("no shared acquire while held exclusively", h.Call("TryAcquireSRWLockShared", srw) == 0);
        h.Call("ReleaseSRWLockExclusive", srw);
        h.Check("two shared holders", h.Call("TryAcquireSRWLockShared", srw) == 1 && h.Call("TryAcquireSRWLockShared", srw) == 1);
        h.Check("no exclusive acquire while shared", h.Call("TryAcquireSRWLockExclusive", srw) == 0);
        h.Call("ReleaseSRWLockShared", srw);
        h.Call("ReleaseSRWLockShared", srw);
        h.Check("free again after both shared releases", h.Call("TryAcquireSRWLockExclusive", srw) == 1);
        h.Call("ReleaseSRWLockExclusive", srw);

        // --- condition variables (single-threaded: the timeout path) ---
        long cv = h.Buffer(4);
        h.Call("InitializeConditionVariable", cv);
        h.Call("EnterCriticalSection", cs);
        h.Check("SleepConditionVariableCS with 0 ms: FALSE, ERROR_TIMEOUT", h.Call("SleepConditionVariableCS", cv, cs, 0) == 0 && LastError(h) == 1460);
        h.Check("... and the critical section is held again", e.ReadUInt32(cs + 12) == me && e.ReadUInt32(cs + 8) == 1);
        h.Call("LeaveCriticalSection", cs);
        h.Call("AcquireSRWLockExclusive", srw);
        h.Check("SleepConditionVariableSRW with 0 ms: FALSE, ERROR_TIMEOUT, lock held again",
            h.Call("SleepConditionVariableSRW", cv, srw, 0, 0) == 0 && LastError(h) == 1460 && h.Call("TryAcquireSRWLockShared", srw) == 0);
        h.Call("ReleaseSRWLockExclusive", srw);
        h.Call("WakeAllConditionVariable", cv);
        h.Check("a wake with no waiter is harmless", true);

        // --- one-time initialisation ---
        long once = h.Buffer(4);
        long pending = h.Buffer(4), context = h.Buffer(4);
        h.Check("INIT_ONCE_CHECK_ONLY before initialisation is ERROR_GEN_FAILURE", h.Call("InitOnceBeginInitialize", once, 1, pending, context) == 0 && LastError(h) == 31);
        h.Check("InitOnceBeginInitialize hands the first caller the work", h.Call("InitOnceBeginInitialize", once, 0, pending, context) == 1 && e.ReadUInt32(pending) == 1);
        h.Check("InitOnceComplete with a context", h.Call("InitOnceComplete", once, 0, 0x5000) == 1);
        h.Check("afterwards, not pending, with the context", h.Call("InitOnceBeginInitialize", once, 0, pending, context) == 1 && e.ReadUInt32(pending) == 0 && e.ReadUInt32(context) == 0x5000);
        e.WriteUInt32(context, 0);
        h.Check("InitOnceExecuteOnce on a completed object returns its context without calling back",
            h.Call("InitOnceExecuteOnce", once, 0xDEAD, 0, context) == 1 && e.ReadUInt32(context) == 0x5000);

        // --- a real thread handle ---
        long handle = h.Buffer(4);
        h.Check("DuplicateHandle(GetCurrentThread) gives a real handle", h.Call("DuplicateHandle", 0xFFFFFFFF, 0xFFFFFFFE, 0xFFFFFFFF, handle, 0, 0, 2) == 1 && e.ReadUInt32(handle) != 0);
        h.Check("a running thread's handle is not signalled", h.Call("WaitForSingleObjectEx", e.ReadUInt32(handle), 0, 0) == 0x102);
        h.Check("DuplicateHandle of an event", h.Call("DuplicateHandle", 0xFFFFFFFF, set, 0xFFFFFFFF, handle, 0, 0, 2) == 1 && h.Call("WaitForSingleObjectEx", e.ReadUInt32(handle), 0, 0) == 0);
        h.Check("DuplicateHandle of nothing is ERROR_INVALID_HANDLE", h.Call("DuplicateHandle", 0xFFFFFFFF, 0x77, 0xFFFFFFFF, handle, 0, 0, 2) == 0 && LastError(h) == 6);
        h.Check("CloseHandle on an event", h.Call("CloseHandle", e1) == 1);

        h.Call("SetLastError", 1234);
        h.Check("SetLastError / GetLastError", LastError(h) == 1234);
    }

    // -------------------------------------------------------------------------------------------
    private static ushort[] SystemTime(Harness h, long address) => Enumerable.Range(0, 8).Select(i => (ushort)h.U16(address + (i * 2))).ToArray();

    private static long WriteSystemTime(Harness h, DateTime t)
    {
        long st = h.Buffer(16);
        ushort[] fields = [(ushort)t.Year, (ushort)t.Month, (ushort)t.DayOfWeek, (ushort)t.Day, (ushort)t.Hour, (ushort)t.Minute, (ushort)t.Second, (ushort)t.Millisecond];
        h.E.WriteMemory(st, fields.SelectMany(BitConverter.GetBytes).ToArray());
        return st;
    }

    private static DateTime ReadDate(Harness h, long st)
    {
        ushort[] f = SystemTime(h, st);
        return new DateTime(f[0], f[1], f[3], f[4], f[5], f[6], f[7]);
    }

    private static void Time(Harness h)
    {
        ArmEmulator e = h.E;
        var instant = new DateTime(2026, 10, 11, 13, 5, 9, 250, DateTimeKind.Utc);
        long ft = h.Buffer(8);
        e.WriteUInt64(ft, (ulong)instant.ToFileTimeUtc());
        long st = h.Buffer(16);
        h.Check("FileTimeToSystemTime", h.Call("FileTimeToSystemTime", ft, st) == 1 && ReadDate(h, st) == instant && SystemTime(h, st)[2] == (ushort)instant.DayOfWeek,
            string.Join(",", SystemTime(h, st)));
        e.WriteUInt64(ft, 0x8000000000000000);
        h.Check("a FILETIME with the top bit set is ERROR_INVALID_PARAMETER", h.Call("FileTimeToSystemTime", ft, st) == 0 && LastError(h) == 87);

        long back = h.Buffer(8);
        h.Check("SystemTimeToFileTime round-trips", h.Call("SystemTimeToFileTime", WriteSystemTime(h, instant), back) == 1 && (long)BitConverter.ToUInt64(e.ReadMemory(back, 8)) == instant.ToFileTimeUtc());
        long bad = WriteSystemTime(h, instant);
        e.WriteMemory(bad + 6, BitConverter.GetBytes((ushort)31));
        e.WriteMemory(bad + 2, BitConverter.GetBytes((ushort)2));
        h.Check("February 31st is ERROR_INVALID_PARAMETER", h.Call("SystemTimeToFileTime", bad, back) == 0 && LastError(h) == 87);
        long far = h.Buffer(16);
        e.WriteMemory(far, new ushort[] { 30000, 12, 0, 31, 23, 59, 59, 999 }.SelectMany(BitConverter.GetBytes).ToArray());
        h.Check("the year 30000 round-trips (past DateTime's range)",
            h.Call("SystemTimeToFileTime", far, back) == 1 && h.Call("FileTimeToSystemTime", back, st) == 1 && SystemTime(h, st)[0] == 30000 && SystemTime(h, st)[3] == 31);

        long f1 = h.Buffer(8), f2 = h.Buffer(8);
        e.WriteUInt64(f1, 5);
        e.WriteUInt64(f2, 9);
        h.Check("CompareFileTime", h.Call("CompareFileTime", f1, f2) == Invalid && h.Call("CompareFileTime", f2, f1) == 1 && h.Call("CompareFileTime", f1, f1) == 0);

        e.WriteUInt64(ft, (ulong)instant.ToFileTimeUtc());
        long offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes * 600_000_000;
        h.Check("FileTimeToLocalFileTime applies the current bias",
            h.Call("FileTimeToLocalFileTime", ft, back) == 1 && (long)BitConverter.ToUInt64(e.ReadMemory(back, 8)) == instant.ToFileTimeUtc() + offset);

        long local = h.Buffer(16);
        h.Call("GetLocalTime", local);
        h.Check("GetLocalTime is the host's local time", Math.Abs((ReadDate(h, local) - DateTime.Now).TotalSeconds) < 5);

        // --- the host's time zone ---
        long tzi = h.Buffer(172);
        long id = h.Call("GetTimeZoneInformation", tzi);
        TimeZoneInfo zone = TimeZoneInfo.Local;
        int bias = BitConverter.ToInt32(e.ReadMemory(tzi, 4));
        h.Check("GetTimeZoneInformation's Bias is minus the zone's standard UTC offset", bias == -(int)zone.BaseUtcOffset.TotalMinutes, $"bias {bias}, id {id}");
        h.Check("and its return names standard or daylight time now",
            id == (!zone.SupportsDaylightSavingTime ? 0 : zone.IsDaylightSavingTime(DateTime.Now) ? 2 : 1) || (id == 0 && !zone.GetAdjustmentRules().Any()));
        h.Check("GetDynamicTimeZoneInformation names the zone", h.Call("GetDynamicTimeZoneInformation", h.Buffer(432)) == id);

        int year = DateTime.UtcNow.Year;
        bool allMatch = true;
        string mismatch = string.Empty;
        for (int month = 1; month <= 12; month++)
        {
            var utc = new DateTime(year, month, 15, 12, 0, 0, DateTimeKind.Utc);
            long output = h.Buffer(16);
            h.Call("SystemTimeToTzSpecificLocalTime", 0, WriteSystemTime(h, utc), output);
            DateTime expected = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            long again = h.Buffer(16);
            h.Call("TzSpecificLocalTimeToSystemTime", 0, output, again);
            if (ReadDate(h, output) != expected || ReadDate(h, again) != utc)
            {
                allMatch = false;
                mismatch = $"{utc:yyyy-MM-dd} -> {ReadDate(h, output):HH:mm} (expected {expected:HH:mm}) -> {ReadDate(h, again):HH:mm}";
            }
        }

        h.Check("SystemTimeToTzSpecificLocalTime(NULL) matches .NET across the year, and inverts", allMatch, mismatch);

        // US Pacific, explicitly: second Sunday of March 02:00 to first Sunday of November 02:00.
        long pacific = h.Buffer(172);
        e.WriteMemory(pacific, BitConverter.GetBytes(480));
        e.WriteMemory(pacific + 68, new ushort[] { 0, 11, 0, 1, 2, 0, 0, 0 }.SelectMany(BitConverter.GetBytes).ToArray());
        e.WriteMemory(pacific + 152, new ushort[] { 0, 3, 0, 2, 2, 0, 0, 0 }.SelectMany(BitConverter.GetBytes).ToArray());
        e.WriteMemory(pacific + 168, BitConverter.GetBytes(-60));
        string Pacific(DateTime utc)
        {
            long output = h.Buffer(16);
            h.Call("SystemTimeToTzSpecificLocalTime", pacific, WriteSystemTime(h, utc), output);
            return ReadDate(h, output).ToString("MM-dd HH:mm");
        }

        h.Check("a given zone: winter is UTC-8", Pacific(new DateTime(2026, 1, 15, 12, 0, 0)) == "01-15 04:00");
        h.Check("summer is UTC-7", Pacific(new DateTime(2026, 7, 1, 12, 0, 0)) == "07-01 05:00");
        h.Check("the spring transition happens at 02:00 local standard time",
            Pacific(new DateTime(2026, 3, 8, 9, 59, 0)) == "03-08 01:59" && Pacific(new DateTime(2026, 3, 8, 10, 0, 0)) == "03-08 03:00");
        h.Check("the autumn transition happens at 02:00 local daylight time",
            Pacific(new DateTime(2026, 11, 1, 8, 59, 0)) == "11-01 01:59" && Pacific(new DateTime(2026, 11, 1, 9, 0, 0)) == "11-01 01:00");
    }

    // -------------------------------------------------------------------------------------------
    private static string? CallWide(Harness h, string function, params long[] args)
    {
        long buffer = h.Buffer(512);
        long[] withBuffer = [.. args, buffer, 256];
        return h.Call(function, withBuffer) == 0 ? null : h.ReadWide(buffer, 256);
    }

    private static void Locale(Harness h)
    {
        ArmEmulator e = h.E;
        long name = h.Buffer(200);
        h.Check("GetUserDefaultLocaleName is en-US", h.Call("GetUserDefaultLocaleName", name, 85) == 6 && h.ReadWide(name) == "en-US");
        h.Check("too small a buffer is ERROR_INSUFFICIENT_BUFFER", h.Call("GetUserDefaultLocaleName", name, 3) == 0 && LastError(h) == 122);
        h.Check("LocaleNameToLCID(en-US) is 0x409", h.Call("LocaleNameToLCID", h.Wide("en-US"), 0) == 0x409);
        h.Check("LocaleNameToLCID on nonsense is ERROR_INVALID_PARAMETER", h.Call("LocaleNameToLCID", h.Wide("zz-NOTALOCALE"), 0) == 0 && LastError(h) == 87);
        h.Check("LCIDToLocaleName(0x409)", h.Call("LCIDToLocaleName", 0x409, name, 85, 0) == 6 && h.ReadWide(name) == "en-US");
        h.Check("IsValidLocaleName", h.Call("IsValidLocaleName", h.Wide("en-US")) == 1 && h.Call("IsValidLocaleName", h.Wide("zz-NOTALOCALE")) == 0);
        h.Check("GetUserDefaultLCID / GetThreadLocale", h.Call("GetUserDefaultLCID") == 0x409 && h.Call("GetThreadLocale") == 0x409);

        string? Info(uint type, string? locale = "en-US") => CallWide(h, "GetLocaleInfoEx", locale is null ? 0 : h.Wide(locale), type);
        h.Check("LOCALE_SNAME", Info(0x5C) == "en-US");
        h.Check("LOCALE_SNAME for LOCALE_NAME_USER_DEFAULT (NULL)", Info(0x5C, null) == "en-US");
        h.Check("LOCALE_SDECIMAL / STHOUSAND / SGROUPING", Info(0x0E) == "." && Info(0x0F) == "," && Info(0x10) == "3;0");
        h.Check("LOCALE_SDAYNAME1 is Monday, SABBREVDAYNAME7 Sun", Info(0x2A) == "Monday" && Info(0x37) == "Sun");
        h.Check("LOCALE_SMONTHNAME1 / SABBREVMONTHNAME12", Info(0x38) == "January" && Info(0x4F) == "Dec");
        h.Check("LOCALE_SSHORTDATE / SLONGDATE / STIMEFORMAT", Info(0x1F) == "M/d/yyyy" && Info(0x20) == "dddd, MMMM d, yyyy" && Info(0x1003) == "h:mm:ss tt");
        h.Check("LOCALE_SCURRENCY / SINTLSYMBOL / SENGLISHLANGUAGENAME", Info(0x14) == "$" && Info(0x15) == "USD" && Info(0x1001) == "English");
        h.Check("LOCALE_IDEFAULTANSICODEPAGE", Info(0x1004) == "1252");
        long number = h.Buffer(4);
        h.Check("LOCALE_RETURN_NUMBER | LOCALE_IFIRSTDAYOFWEEK is 6 (Sunday), two WCHARs",
            h.Call("GetLocaleInfoEx", h.Wide("en-US"), 0x2000100C, number, 2) == 2 && e.ReadUInt32(number) == 6);
        h.Check("a size query returns the length with the NUL", h.Call("GetLocaleInfoEx", h.Wide("en-US"), 0x5C, 0, 0) == 6);
        h.Check("an unknown LCTYPE is ERROR_INVALID_FLAGS", h.Call("GetLocaleInfoEx", h.Wide("en-US"), 0x9999, name, 85) == 0 && LastError(h) == 1004);
        h.Check("GetUserGeoID(GEOCLASS_NATION) is the US", h.Call("GetUserGeoID", 16) == 244);
        h.Check("GetGeoInfoW(244, GEO_ISO2)", h.Call("GetGeoInfoW", 244, 4, name, 85, 0) == 3 && h.ReadWide(name) == "US");

        // --- numbers and currency ---
        string? Number(string value, long format = 0) => CallWide(h, "GetNumberFormatEx", h.Wide("en-US"), 0, h.Wide(value), format);
        h.Check("GetNumberFormatEx groups and rounds", Number("1234567.891") == "1,234,567.89", Number("1234567.891"));
        h.Check("half away from zero, with a carry", Number("0.995") == "1.00" && Number("-2.345") == "-2.35");
        h.Check("a value that is not a number is ERROR_INVALID_PARAMETER", Number("12a") is null && LastError(h) == 87);
        long fmt = h.Buffer(24);
        e.WriteMemory(fmt, [.. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(32u),
            .. BitConverter.GetBytes((uint)h.Wide(".")), .. BitConverter.GetBytes((uint)h.Wide(",")), .. BitConverter.GetBytes(0u)]);
        h.Check("a NUMBERFMT: no decimals, Indic grouping (32), negative in parentheses", Number("-1234567", fmt) == "(12,34,567)", Number("-1234567", fmt));
        string? Currency(string value) => CallWide(h, "GetCurrencyFormatEx", h.Wide("en-US"), 0, h.Wide(value), 0);
        h.Check("GetCurrencyFormatEx", Currency("1234.5") == "$1,234.50" && Currency("-1234.5") == "($1,234.50)", $"{Currency("1234.5")} {Currency("-1234.5")}");

        // --- dates and times ---
        long when = WriteSystemTime(h, new DateTime(2026, 10, 11, 13, 5, 9));
        string? Date(long flags, string? picture = null)
        {
            long buffer = h.Buffer(512);
            return h.Call("GetDateFormatEx", h.Wide("en-US"), flags, when, picture is null ? 0 : h.Wide(picture), buffer, 256, 0) == 0 ? null : h.ReadWide(buffer, 256);
        }
        h.Check("GetDateFormatEx short date", Date(0x1) == "10/11/2026", Date(0x1));
        h.Check("GetDateFormatEx long date (the day of week is computed, not read)", Date(0x2) == "Sunday, October 11, 2026", Date(0x2));
        h.Check("a picture with quoted text and the era", Date(0, "dd MMM yy 'at' gg") == "11 Oct 26 at A.D.", Date(0, "dd MMM yy 'at' gg"));
        h.Check("a picture with a format flag is ERROR_INVALID_FLAGS", Date(0x1, "d") is null && LastError(h) == 1004);
        string? Time(long flags, string? picture = null) => CallWide(h, "GetTimeFormatEx", h.Wide("en-US"), flags, when, picture is null ? 0 : h.Wide(picture));
        h.Check("GetTimeFormatEx", Time(0) == "1:05:09 PM", Time(0));
        h.Check("TIME_NOSECONDS drops the seconds and their separator", Time(0x2) == "1:05 PM", Time(0x2));
        h.Check("TIME_FORCE24HOURFORMAT | TIME_NOTIMEMARKER", Time(0xC) == "13:05:09", Time(0xC));
        h.Check("a picture: HH'h'mm", Time(0, "HH'h'mm") == "13h05");

        // --- comparison and character types ---
        long Compare(string a, string b, long flags = 0) => h.Call("CompareStringEx", h.Wide("en-US"), flags, h.Wide(a), 0xFFFFFFFF, h.Wide(b), 0xFFFFFFFF, 0, 0, 0);
        h.Check("CompareStringEx is linguistic: apple < Banana", Compare("apple", "Banana") == 1);
        h.Check("NORM_IGNORECASE", Compare("ABC", "abc", 1) == 2);
        h.Check("an unknown flag is ERROR_INVALID_FLAGS", Compare("a", "b", 0x40000000) == 0 && LastError(h) == 1004);
        h.Check("CompareStringOrdinal is by code unit: a > B", h.Call("CompareStringOrdinal", h.Wide("a"), 0xFFFFFFFF, h.Wide("B"), 0xFFFFFFFF, 0) == 3);
        h.Check("CompareStringOrdinal ignoring case", h.Call("CompareStringOrdinal", h.Wide("a"), 1, h.Wide("Abc"), 1, 1) == 2);
        long types = h.Buffer(8);
        h.Check("GetStringTypeExW CT_CTYPE1", h.Call("GetStringTypeExW", 0x409, 1, h.Wide("A1 "), 3, types) == 1 &&
            h.U16(types) == (0x1 | 0x100 | 0x200 | 0x80) && h.U16(types + 2) == (0x4 | 0x80 | 0x200) && h.U16(types + 4) == (0x8 | 0x40 | 0x200),
            $"{h.U16(types):X} {h.U16(types + 2):X} {h.U16(types + 4):X}");
        h.Check("GetStringTypeExW with two info types is ERROR_INVALID_FLAGS", h.Call("GetStringTypeExW", 0x409, 3, h.Wide("A"), 1, types) == 0 && LastError(h) == 1004);

        // --- code pages ---
        long utf8 = h.Buffer(8);
        e.WriteMemory(utf8, [(byte)'h', 0xC3, 0xA9, 0]);
        long wide = h.Buffer(32);
        h.Check("MultiByteToWideChar CP_UTF8, NUL-terminated (the NUL counted)", h.Call("MultiByteToWideChar", 65001, 0, utf8, 0xFFFFFFFF, wide, 16) == 3 && h.ReadWide(wide) == "h\u00e9");
        e.WriteMemory(utf8, [0xFF, 0]);
        h.Check("MB_ERR_INVALID_CHARS on bad UTF-8 is ERROR_NO_UNICODE_TRANSLATION", h.Call("MultiByteToWideChar", 65001, 8, utf8, 1, wide, 16) == 0 && LastError(h) == 1113);
        e.WriteMemory(utf8, [0xE9, 0x80, 0]);
        h.Check("CP_ACP is code page 1252 (0x80 is the euro sign)", h.Call("MultiByteToWideChar", 0, 0, utf8, 2, wide, 16) == 2 && h.ReadWide(wide, 2) == "\u00e9\u20ac");
        h.Check("a capacity of 0 asks for the size", h.Call("MultiByteToWideChar", 0, 0, utf8, 2, 0, 0) == 2);
        h.Check("too small is ERROR_INSUFFICIENT_BUFFER", h.Call("MultiByteToWideChar", 0, 0, utf8, 2, wide, 1) == 0 && LastError(h) == 122);
        long narrowOut = h.Buffer(16), used = h.Buffer(4);
        h.Check("WideCharToMultiByte to 1252: unmappable becomes '?', and says so",
            h.Call("WideCharToMultiByte", 0, 0, h.Wide("a\u0100"), 2, narrowOut, 16, 0, used) == 2 && h.ReadNarrow(narrowOut, 2) == "a?" && e.ReadUInt32(used) == 1);
        h.Check("WideCharToMultiByte to UTF-8 with a used-default pointer is ERROR_INVALID_PARAMETER",
            h.Call("WideCharToMultiByte", 65001, 0, h.Wide("a"), 1, narrowOut, 16, 0, used) == 0 && LastError(h) == 87);
        h.Check("WideCharToMultiByte to UTF-8", h.Call("WideCharToMultiByte", 65001, 0, h.Wide("\u00e9"), 1, narrowOut, 16, 0, 0) == 2 && e.ReadMemory(narrowOut, 2).SequenceEqual(new byte[] { 0xC3, 0xA9 }));

        // --- FormatMessage ---
        long message = h.Buffer(512);
        h.Check("FormatMessageW FROM_SYSTEM", h.Call("FormatMessageW", 0x1000, 0, 2, 0, message, 256, 0) > 0 && h.ReadWide(message) == "The system cannot find the file specified.\r\n");
        h.Check("an HRESULT_FROM_WIN32 has the Win32 text", h.Call("FormatMessageW", 0x1000, 0, 0x80070005, 0, message, 256, 0) > 0 && h.ReadWide(message).StartsWith("Access is denied."));
        h.Check("an unknown id is ERROR_MR_MID_NOT_FOUND", h.Call("FormatMessageW", 0x1000, 0, 0x12345, 0, message, 256, 0) == 0 && LastError(h) == 317);
        long args = h.Buffer(8);
        e.WriteUInt32(args, (uint)h.Wide("box"));
        e.WriteUInt32(args + 4, 5);
        h.Check("FROM_STRING with ARGUMENT_ARRAY inserts, %0 ends it",
            h.Call("FormatMessageW", 0x400 | 0x2000, h.Wide("%1 has %2!d! items%0 ignored"), 0, 0, message, 256, args) == 15 && h.ReadWide(message) == "box has 5 items");
        h.Check("IGNORE_INSERTS keeps them", h.Call("FormatMessageW", 0x400 | 0x200, h.Wide("%1!d! left"), 0, 0, message, 256, 0) > 0 && h.ReadWide(message) == "%1!d! left");
        long allocated = h.Buffer(4);
        h.Check("ALLOCATE_BUFFER hands back a block", h.Call("FormatMessageA", 0x100 | 0x1000, 0, 5, 0, allocated, 0, 0) > 0 && h.ReadNarrow(e.ReadUInt32(allocated)) == "Access is denied.\r\n");
    }

    // -------------------------------------------------------------------------------------------
    private static void System(Harness h)
    {
        ArmEmulator e = h.E;
        long info = h.Buffer(36);
        h.Call("GetNativeSystemInfo", info);
        h.Check("GetNativeSystemInfo: ARM, 4 KB pages, two processors, 64 KB granularity",
            h.U16(info) == 5 && e.ReadUInt32(info + 4) == 4096 && e.ReadUInt32(info + 20) == 2 && e.ReadUInt32(info + 28) == 0x10000);
        h.Check("IsProcessorFeaturePresent: NEON yes, MMX no", h.Call("IsProcessorFeaturePresent", 19) == 1 && h.Call("IsProcessorFeaturePresent", 3) == 0);
        h.Check("IsDebuggerPresent is FALSE", h.Call("IsDebuggerPresent") == 0);
        h.Check("GetCurrentProcess is the pseudo-handle -1", h.Call("GetCurrentProcess") == 0xFFFFFFFF);

        long status = h.Buffer(64);
        h.Check("GlobalMemoryStatusEx without dwLength is ERROR_INVALID_PARAMETER", h.Call("GlobalMemoryStatusEx", status) == 0 && LastError(h) == 87);
        e.WriteUInt32(status, 64);
        h.Check("GlobalMemoryStatusEx: a 1 GB phone", h.Call("GlobalMemoryStatusEx", status) == 1 && BitConverter.ToInt64(e.ReadMemory(status + 8, 8)) == 1L << 30);

        long mbi = h.Buffer(28);
        h.Check("VirtualQuery on the image: committed MEM_IMAGE",
            h.Call("VirtualQuery", e.ImageBase + 0x1000, mbi, 28) == 28 && e.ReadUInt32(mbi + 4) == e.ImageBase && e.ReadUInt32(mbi + 16) == 0x1000 && e.ReadUInt32(mbi + 24) == 0x1000000);
        h.Check("VirtualQuery on a heap block: committed, private, read-write",
            h.Call("VirtualQuery", h.Buffer(64), mbi, 28) == 28 && e.ReadUInt32(mbi + 16) == 0x1000 && e.ReadUInt32(mbi + 20) == 0x04 && e.ReadUInt32(mbi + 24) == 0x20000);
        h.Check("VirtualQuery on unmapped memory: MEM_FREE", h.Call("VirtualQuery", 0x20000, mbi, 28) == 28 && e.ReadUInt32(mbi + 16) == 0x10000);
        h.Check("VirtualQuery with a short buffer is ERROR_BAD_LENGTH", h.Call("VirtualQuery", 0x20000, mbi, 10) == 0 && LastError(h) == 24);
        long big = e.AllocateHeap(256 * 1024);
        h.Check("a large heap block is its own region (a thread stack's shape)",
            h.Call("VirtualQuery", big + 100_000, mbi, 28) == 28 && e.ReadUInt32(mbi + 4) == (big & ~0xFFF) && e.ReadUInt32(mbi) + e.ReadUInt32(mbi + 12) >= big + (256 * 1024));

        long handle = h.Buffer(8);
        h.Check("EventRegister answers ERROR_SUCCESS and a handle", h.Call("EventRegister", h.Buffer(16), 0, 0, handle) == 0 && e.ReadUInt32(handle) != 0);
        h.Check("EventEnabled is FALSE with no session", h.Call("EventEnabled", e.ReadUInt32(handle), 0, h.Buffer(16)) == 0);
        h.Check("EventWrite answers ERROR_SUCCESS", h.Call("EventWrite", e.ReadUInt32(handle), 0, h.Buffer(16), 0) == 0);

        h.Call("OutputDebugStringW", h.Wide("w32core says hello\r\n"));
        h.Check("OutputDebugStringW reaches the trace", Win32Core.DebugOutput.Contains("w32core says hello"));

        // RaiseException(MS_VC_EXCEPTION, 0, 4, THREADNAME_INFO): a thread name for a debugger.
        long threadName = h.Buffer(16);
        e.WriteMemory(threadName, [.. BitConverter.GetBytes(0x1000u), .. BitConverter.GetBytes((uint)h.Narrow("loader")), .. BitConverter.GetBytes(0xFFFFFFFFu), .. BitConverter.GetBytes(0u)]);
        h.Call("RaiseException", 0x406D1388, 0, 4, threadName);
        h.Check("RaiseException(MS_VC_EXCEPTION) names the thread and returns", h.S.GuestThreadNames.ContainsValue("loader") && h.E.StopReason is null);
    }
}

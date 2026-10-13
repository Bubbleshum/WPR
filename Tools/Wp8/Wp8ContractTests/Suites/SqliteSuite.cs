using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// A real SQLite (the <c>sqlite3.dll</c> Assassin's Creed Pirates ships for its mobile SDK) run in
/// the guest over our Win32 file API: CreateFile2, ReadFile/WriteFile, SetFilePointerEx,
/// LockFileEx/UnlockFileEx, FlushFileBuffers, SetEndOfFile, GetFileInformationByHandleEx,
/// GetFileAttributesExW, DeleteFileW. A database is created in the local folder, a table is
/// created, rows are written in a transaction (journal created and deleted), and everything is read
/// back from a second connection after the first has closed - the contract that SQLite's own
/// os_win.c relies on, end to end. Needs the installed title (or <c>WPR_SQLITE_DLL</c>); skipped
/// otherwise.
/// </summary>
public sealed class SqliteSuite : IContractSuite
{
    private const string ProductId = "40ae56ef-6238-40e1-a2ad-62cac8b13d93";

    public string Name => "sqlite over the Win32 file API";

    public void Run(Harness h)
    {
        string? dll = Environment.GetEnvironmentVariable("WPR_SQLITE_DLL");
        dll ??= Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "WPR", "AppData", ProductId, "sqlite3.dll");
        if (!File.Exists(dll))
        {
            Console.WriteLine("      (no sqlite3.dll: install Assassin's Creed Pirates or set WPR_SQLITE_DLL; skipped)");
            return;
        }

        string package = Path.Combine(Path.GetTempPath(), $"wp8-sqlite-suite-{Environment.ProcessId}");
        string sandbox = Path.Combine(Path.GetTempPath(), $"wp8-sqlite-sandbox-{Environment.ProcessId}");
        string? previousSandbox = HostStubs.SandboxRoot;
        try
        {
            Directory.CreateDirectory(package);
            Directory.CreateDirectory(sandbox);
            File.Copy(dll, Path.Combine(package, "sqlite3.dll"), overwrite: true);
            SyntheticPe exe = new(0x400000, dll: false);
            exe.EntryRva = exe.Return();
            string exePath = Path.Combine(package, "Host.exe");
            File.WriteAllBytes(exePath, exe.Build());
            HostStubs.SandboxRoot = sandbox;
            Exercise(h, exePath, package, sandbox);
        }
        finally
        {
            HostStubs.SandboxRoot = previousSandbox;
            foreach (string dir in new[] { package, sandbox })
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static void Exercise(Harness h, string exePath, string package, string sandbox)
    {
        using ArmEmulator e = new(PeImage.Load(exePath), package, collectBlockStats: false);
        ModuleLoaderSuite.Guest g = new(e, stackSize: 0x40000);
        e.Run(e.StartWithModuleInitialisers(g.Sentinel), 1_000_000);

        long lib = g.CallGuest("LoadPackagedLibrary", g.Wide("sqlite3.dll"), 0);
        h.Check("sqlite3.dll loads", lib != 0);
        if (lib == 0)
        {
            return;
        }

        long Proc(string name) => g.Call("GetProcAddress", lib, g.Narrow(name));
        long open = Proc("sqlite3_open_v2"), exec = Proc("sqlite3_exec"), close = Proc("sqlite3_close"), errmsg = Proc("sqlite3_errmsg");
        long getTable = Proc("sqlite3_get_table");
        h.Check("its exports resolve", open != 0 && exec != 0 && close != 0 && errmsg != 0 && getTable != 0);

        string Error(long db) => db == 0 ? "no db" : ReadUtf8(e, g.Invoke(errmsg, db));

        // As the game names it: the device local folder, then a forward slash.
        string guestPath = e.WinRt.LocalFolderPath + "/msdk";
        long dbOut = g.Buffer(4);
        const long ReadWriteCreate = 0x2 | 0x4;
        long rc = g.Invoke(open, Utf8(e, guestPath), dbOut, ReadWriteCreate, 0);
        long db = e.ReadUInt32(dbOut);
        h.Check("sqlite3_open_v2(LocalFolder/msdk, READWRITE|CREATE) = SQLITE_OK", rc == 0 && db != 0, $"rc {rc}: {Error(db)}");
        if (db == 0)
        {
            return;
        }

        long Exec(long connection, string sql) => g.Invoke(exec, connection, Utf8(e, sql), 0, 0, 0);

        rc = Exec(db, "CREATE TABLE IF NOT EXISTS UserPreferences (key TEXT PRIMARY KEY, value TEXT)");
        h.Check("CREATE TABLE = SQLITE_OK", rc == 0, $"rc {rc}: {Error(db)}");
        string host = Path.Combine(sandbox, "msdk");
        h.Check("...the database file is in the local folder on the host", File.Exists(host) && new FileInfo(host).Length > 0,
            File.Exists(host) ? $"{new FileInfo(host).Length} bytes" : "missing");

        rc = Exec(db, "BEGIN; INSERT OR REPLACE INTO UserPreferences VALUES ('a', 'one'); INSERT OR REPLACE INTO UserPreferences VALUES ('b', 'two'); COMMIT;");
        h.Check("a transaction (journal) commits", rc == 0, $"rc {rc}: {Error(db)}");
        h.Check("...and the rollback journal is gone afterwards", !File.Exists(host + "-journal"));

        rc = Exec(db, "UPDATE UserPreferences SET value = 'uno' WHERE key = 'a'");
        h.Check("UPDATE outside a transaction commits", rc == 0, $"rc {rc}: {Error(db)}");

        h.Check("sqlite3_close = SQLITE_OK", g.Invoke(close, db) == 0);

        // A second connection, after the first is gone: what a second run of the game sees.
        e.WriteUInt32(dbOut, 0);
        rc = g.Invoke(open, Utf8(e, guestPath), dbOut, ReadWriteCreate, 0);
        long db2 = e.ReadUInt32(dbOut);
        h.Check("reopening = SQLITE_OK", rc == 0 && db2 != 0, $"rc {rc}");
        if (db2 == 0)
        {
            return;
        }

        long result = g.Buffer(4), rows = g.Buffer(4), cols = g.Buffer(4);
        rc = g.Invoke(getTable, db2, Utf8(e, "SELECT key, value FROM UserPreferences ORDER BY key"), result, rows, cols, 0);
        long table = e.ReadUInt32(result);
        int rowCount = (int)e.ReadUInt32(rows), colCount = (int)e.ReadUInt32(cols);
        List<string> cells = [];
        for (int i = 0; table != 0 && i < (rowCount + 1) * colCount; i++)
        {
            cells.Add(ReadUtf8(e, e.ReadUInt32(table + (i * 4))));
        }

        h.Check("...the rows written by the first connection read back",
            rc == 0 && string.Join(",", cells) == "key,value,a,uno,b,two", $"rc {rc}: {string.Join(",", cells)} {Error(db2)}");

        // The same file through a second handle while the first holds it open: SQLite's locking
        // (LockFileEx on the pending/reserved/shared bytes) must let both read and one write.
        long dbOut3 = g.Buffer(4);
        rc = g.Invoke(open, Utf8(e, guestPath), dbOut3, ReadWriteCreate, 0);
        long db3 = e.ReadUInt32(dbOut3);
        h.Check("a second concurrent connection opens", rc == 0 && db3 != 0, $"rc {rc}");
        rc = Exec(db3, "INSERT INTO UserPreferences VALUES ('c', 'three')");
        h.Check("...and writes while the other is idle", rc == 0, $"rc {rc}: {Error(db3)}");
        rc = Exec(db2, "BEGIN IMMEDIATE");
        h.Check("BEGIN IMMEDIATE takes the RESERVED lock", rc == 0, $"rc {rc}: {Error(db2)}");
        rc = Exec(db3, "INSERT INTO UserPreferences VALUES ('d', 'four')");
        h.Check("...so the other connection's write is SQLITE_BUSY (5)", rc == 5, $"rc {rc}: {Error(db3)}");
        rc = Exec(db2, "COMMIT");
        h.Check("...and after COMMIT it succeeds", rc == 0 && Exec(db3, "INSERT INTO UserPreferences VALUES ('d', 'four')") == 0, $"rc {rc}: {Error(db3)}");

        h.Check("both close", g.Invoke(close, db3) == 0 && g.Invoke(close, db2) == 0);
    }

    private static long Utf8(ArmEmulator e, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
        long p = e.AllocateHeap(bytes.Length);
        e.WriteMemory(p, bytes);
        return p;
    }

    private static string ReadUtf8(ArmEmulator e, long address)
    {
        if (address == 0)
        {
            return "(null)";
        }

        List<byte> bytes = [];
        for (int i = 0; i < 4096; i++)
        {
            byte b = e.ReadMemory(address + i, 1)[0];
            if (b == 0)
            {
                break;
            }

            bytes.Add(b);
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }
}

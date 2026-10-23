using System;
using System.Threading;
using WPR.WindowsCompability;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// The process-local fallback NamedMutex uses where the OS has no named mutexes (Android): every
/// Mutex made for one name must exclude the others, as a named mutex does.
/// </summary>
public class NamedMutexTests
{
    [Fact]
    public void MutexesOfOneNameExcludeEachOther()
    {
        string name = "wpr-test-" + Guid.NewGuid().ToString("N");
        Mutex first = NamedMutex.CreateProcessLocal(false, name, out bool firstNew);
        Mutex second = NamedMutex.CreateProcessLocal(false, name, out bool secondNew);
        Assert.True(firstNew);
        Assert.False(secondNew);

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() => { first.WaitOne(); held.Set(); release.Wait(); first.ReleaseMutex(); });
        owner.Start();
        held.Wait();

        Assert.False(second.WaitOne(0));
        release.Set();
        owner.Join();
        Assert.True(second.WaitOne(0));
        second.ReleaseMutex();

        // Disposing one Mutex leaves the shared object to the others.
        first.Dispose();
        Mutex third = NamedMutex.CreateProcessLocal(false, name, out _);
        Assert.True(third.WaitOne(0));
        Assert.True(second.WaitOne(0));   // re-entrant for the owning thread, like any mutex
        second.ReleaseMutex();
        third.ReleaseMutex();
    }

    [Fact]
    public void OtherNamesAreIndependent()
    {
        Mutex a = NamedMutex.CreateProcessLocal(true, "wpr-test-a-" + Guid.NewGuid().ToString("N"), out _);
        Mutex b = NamedMutex.CreateProcessLocal(false, "wpr-test-b-" + Guid.NewGuid().ToString("N"), out _);
        bool otherThreadGotB = false;
        var t = new Thread(() => { otherThreadGotB = b.WaitOne(0); if (otherThreadGotB) b.ReleaseMutex(); });
        t.Start();
        t.Join();
        Assert.True(otherThreadGotB);
        a.ReleaseMutex();
    }

    [Fact]
    public void WhereTheOsHasNamedMutexesTheyAreUsed()
    {
        // On Windows the real named mutex is created; it is visible to OpenExisting.
        if (!OperatingSystem.IsWindows()) return;
        string name = "wpr-test-os-" + Guid.NewGuid().ToString("N");
        using Mutex m = NamedMutex.Create(false, name);
        using Mutex opened = Mutex.OpenExisting(name);
        Assert.NotNull(opened);
    }
}

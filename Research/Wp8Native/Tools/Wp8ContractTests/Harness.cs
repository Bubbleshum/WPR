using System.Reflection;
using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests;

/// <summary>One family's checks. Drop a class implementing this into Suites/; it is found by reflection.</summary>
public interface IContractSuite
{
    string Name { get; }

    void Run(Harness h);
}

/// <summary>
/// A real WP8 image loaded into the emulator, and helpers to call import handlers the way a trap
/// does: arguments in r0-r3 and on the stack, <c>HostStubs.Dispatch</c>, the result read from r0.
/// </summary>
public sealed class Harness
{
    public const int R0 = 66, R1 = 67, R2 = 68, R3 = 69, SP = 12;   // unicorn ids; the Arm const class is internal

    public Harness(string image, string sandbox)
    {
        Image = image;
        Sandbox = sandbox;
        HostStubs.SandboxRoot = sandbox;
        E = new ArmEmulator(PeImage.Load(image), Path.GetDirectoryName(image)!, collectBlockStats: false);
        Stack = E.AllocateHeap(0x1000) + 0x800;
    }

    public ArmEmulator E { get; }

    public HostStubs S => E.Stubs;

    /// <summary>The ARM image loaded; its folder is the package root files resolve against.</summary>
    public string Image { get; }

    /// <summary>The writable local folder; deleted at the start of every run.</summary>
    public string Sandbox { get; }

    /// <summary>A guest stack top for stack-passed arguments; sp is set to it on every <see cref="Call"/>.</summary>
    public long Stack { get; }

    public int Passed { get; private set; }

    public int Failed { get; private set; }

    public void Check(string what, bool ok, string detail = "")
    {
        if (ok) Passed++; else Failed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
    }

    /// <summary>
    /// Calls the import <paramref name="function"/> (dispatch is by name, so the DLL does not
    /// matter) with word arguments; the fifth onwards go on the stack. A 64-bit argument is two
    /// words, already placed on an even register or stack slot by the caller.
    /// </summary>
    public long Call(string function, params long[] args)
    {
        int[] registers = [R0, R1, R2, R3];
        for (int i = 0; i < args.Length && i < 4; i++) E.WriteRegister(registers[i], args[i]);
        for (int i = 4; i < args.Length; i++) E.WriteUInt32(Stack + ((i - 4) * 4), (uint)args[i]);
        E.WriteRegister(SP, Stack);
        S.Dispatch("kernel32.dll!" + function);
        return E.ReadRegister(R0) & 0xFFFFFFFFL;
    }

    /// <summary>r1 after a call: the high word of a 64-bit result.</summary>
    public long R1Value => E.ReadRegister(R1) & 0xFFFFFFFFL;

    /// <summary>Single-precision VFP register s<paramref name="index"/>, for hard-float arguments and results.</summary>
    public float S32(int index) => BitConverter.UInt32BitsToSingle(E.Cpu.VfpRead(index));

    public void SetS32(int index, float value) => E.Cpu.VfpWrite(index, BitConverter.SingleToUInt32Bits(value));

    public double D64(int index) => BitConverter.UInt64BitsToDouble(E.Cpu.VfpRead(index * 2) | ((ulong)E.Cpu.VfpRead((index * 2) + 1) << 32));

    public void SetD64(int index, double value)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        E.Cpu.VfpWrite(index * 2, (uint)bits);
        E.Cpu.VfpWrite((index * 2) + 1, (uint)(bits >> 32));
    }

    public int U16(long address)
    {
        byte[] b = E.ReadMemory(address, 2);
        return b[0] | (b[1] << 8);
    }

    public long Wide(string text)
    {
        long p = E.AllocateHeap((text.Length * 2) + 2);
        E.WriteMemory(p, Encoding.Unicode.GetBytes(text + "\0"));
        return p;
    }

    public long Narrow(string text)
    {
        long p = E.AllocateHeap(text.Length + 1);
        E.WriteMemory(p, Encoding.ASCII.GetBytes(text + "\0"));
        return p;
    }

    /// <summary>A zeroed guest buffer.</summary>
    public long Buffer(int size)
    {
        long p = E.AllocateHeap(size);
        E.WriteMemory(p, new byte[size]);
        return p;
    }

    public string ReadNarrow(long address, int max = 256) => Encoding.ASCII.GetString(E.ReadMemory(address, max)).Split('\0')[0];

    public string ReadWide(long address, int max = 256) => E.ReadUtf16String(address, max);

    /// <summary>The guest's errno, as _errno() would report it.</summary>
    public long Errno() => E.ReadUInt32((long)typeof(FileLibrary).GetMethod("ErrnoAddress", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(S.Files, null)!);
}

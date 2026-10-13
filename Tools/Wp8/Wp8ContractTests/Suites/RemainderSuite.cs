using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// What the census listed after the families (HostStubs.Remainder.cs): the "C" locale object, the
/// FILE* format/scan functions, MSVCP110's C-locale collation helpers, the last vccorlib exports, and
/// the census itself counting data imports and DLL-qualified ordinals as implemented.
/// </summary>
public sealed class RemainderSuite : IContractSuite
{
    public string Name => "remainder imports";

    private const int SP = Harness.SP, LR = 10, R12 = 78;

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        HostStubs s = h.S;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        long Call(string fn, params long[] a) => h.Call(fn, a);
        long U32(long a) => e.ReadUInt32(a);

        bool returned = false;
        long sentinel = ArmEmulator.ThumbEntry(e.RegisterVtableMethod("remainder test: returned", () =>
        {
            returned = true;
            e.Stop("returned to the contract test");
        }));
        string? lastFault = null;
        long RunGuest(string fn, params long[] args)
        {
            int[] registers = [Harness.R0, Harness.R1, Harness.R2, Harness.R3];
            for (int i = 0; i < args.Length && i < 4; i++) e.WriteRegister(registers[i], args[i]);
            e.WriteRegister(SP, h.Stack);
            e.WriteRegister(LR, sentinel);
            e.WriteRegister(R12, sentinel);
            s.Dispatch("vccorlib110.dll!" + fn);
            long next = e.ReadRegister(R12) & 0xFFFFFFFFL;
            lastFault = null;
            if ((next & ~1L) != (sentinel & ~1L))
            {
                returned = false;
                string? fault = e.Run(next, 10_000_000);
                lastFault = returned ? null : fault ?? e.StopReason ?? "did not return";
            }

            return e.ReadRegister(Harness.R0) & 0xFFFFFFFFL;
        }

        // ---- the census ----
        foreach (string data in new[] { "_fmode", "_commode", "_HUGE", "_acmdln", "_FInf", "?_BADOFF@std@@3_JB", "_Nan", "?_1@placeholders@std@@3V?$_Ph@$00@2@A" })
        {
            Check($"data import {data} counts as implemented (backed by a variable)", s.IsImplemented(data));
        }

        Check("a DLL-qualified ordinal handler counts (XAudio2_8 #2, CreateAudioReverb)", s.IsImplemented("XAudio2_8.dll", "#2"));
        Check("SafeToDefault is empty: nothing is answered 0 by allowance any more", HostStubs.SafeToDefault.Count == 0);

        // ---- MSVCR110 ----
        Check("__crtSetUnhandledExceptionFilter answers the previous filter",
            Call("__crtSetUnhandledExceptionFilter", 0x1234) == 0 && Call("__crtSetUnhandledExceptionFilter", 0x5678) == 0x1234);

        long locale = Call("_create_locale", 0, h.Narrow("C"));
        Check("_create_locale(LC_ALL, \"C\") makes a locale", locale != 0);
        long locinfo = U32(locale), mbcinfo = U32(locale + 4);
        Check("... locinfo: refcount 1, lc_clike, mb_cur_max 1, an lconv", locinfo != 0 && U32(locinfo) == 1 && U32(locinfo + 112) == 1 && U32(locinfo + 116) == 1 && U32(locinfo + 132) != 0);
        long pctype = U32(locinfo + 144);
        Check("... pctype classifies as the C locale ('A' upper|hex|alpha, '5' digit|hex, ' ' space|blank)",
            pctype != 0 && h.U16(pctype + ('A' * 2)) == 0x181 && h.U16(pctype + ('5' * 2)) == 0x84 && h.U16(pctype + (' ' * 2)) == 0x48,
            $"A=0x{h.U16(pctype + ('A' * 2)):X} 5=0x{h.U16(pctype + ('5' * 2)):X}");
        Check("... the C category names", h.ReadNarrow(U32(locinfo + 16)) == "C");
        Check("... mbcinfo refcount 1", mbcinfo != 0 && U32(mbcinfo) == 1);
        Check("_create_locale(LC_ALL, \"\") is the native locale, \"C\"", Call("_create_locale", 0, h.Narrow("")) != 0);
        Check("_create_locale of a locale that does not exist here is NULL", Call("_create_locale", 0, h.Narrow("fr-FR")) == 0);
        Check("_create_locale of an invalid category is NULL", Call("_create_locale", 6, h.Narrow("C")) == 0);
        Check("_wcreate_locale(LC_ALL, L\"C\")", Call("_wcreate_locale", 0, h.Wide("C")) != 0);
        Check("_get_current_locale is a C locale", Call("_get_current_locale") is var current && current != 0 && U32(U32(current) + 116) == 1);
        Call("_free_locale", locale);
        Call("_free_locale", 0);
        Check("_free_locale (and of NULL) returns", true);

        // vfprintf / fscanf over a real stream
        long path = h.Narrow("remainder.txt");
        long file = Call("fopen", path, h.Narrow("w"));
        long list = h.Buffer(8);
        e.WriteUInt32(list, 42);
        e.WriteUInt32(list + 4, (uint)h.Narrow("abc"));
        long written = Call("vfprintf", file, h.Narrow("%d %s 7\n"), list);
        Call("fclose", file);
        string onDisk = File.ReadAllText(Path.Combine(h.Sandbox, "remainder.txt"));
        Check("vfprintf formats through a va_list into the stream", written == 9 && onDisk == "42 abc 7\n", $"{written} \"{onDisk}\"");
        file = Call("fopen", path, h.Narrow("r"));
        long number = h.Buffer(4), word = h.Buffer(16), second = h.Buffer(4);
        Check("fscanf(\"%d %s\") assigns two fields", Call("fscanf", file, h.Narrow("%d %s"), number, word) == 2 && U32(number) == 42 && h.ReadNarrow(word) == "abc");
        Check("... and the next fscanf continues where it stopped", Call("fscanf", file, h.Narrow("%d"), second) == 1 && U32(second) == 7);
        Check("... and at the end of the input it is EOF", Call("fscanf", file, h.Narrow("%d"), second) == 0xFFFFFFFF);
        Call("fclose", file);
        Check("printf_s returns the characters written", Call("printf_s", h.Narrow("x=%d\n"), 5) == 4);
        Check("wprintf returns the characters written", Call("wprintf", h.Wide("w=%d"), 12) == 4);

        // ---- MSVCP110 C-locale helpers ----
        long abc = h.Narrow("abc"), abd = h.Narrow("abd"), ab = h.Narrow("ab");
        Check("_Strcoll: \"abc\" < \"abd\"", Call("_Strcoll", abc, abc + 3, abd, abd + 3, 0) == 0xFFFFFFFF);
        Check("_Strcoll: a prefix sorts first", Call("_Strcoll", abc, abc + 3, ab, ab + 2, 0) == 1);
        Check("_Strcoll: equal ranges", Call("_Strcoll", abc, abc + 3, abc, abc + 3, 0) == 0);
        long wabc = h.Wide("abc"), wabd = h.Wide("abd");
        Check("_Wcscoll compares code units", Call("_Wcscoll", wabd, wabd + 6, wabc, wabc + 6, 0) == 1);
        long key = h.Buffer(8);
        Check("_Strxfrm copies the key when it fits and answers its length", Call("_Strxfrm", key, key + 8, abc, abc + 3, 0) == 3 && h.ReadNarrow(key) == "abc");
        long small = h.Buffer(4);
        Check("_Strxfrm answers the length it needs when it does not fit, writing nothing", Call("_Strxfrm", small, small + 2, abc, abc + 3, 0) == 3 && U32(small) == 0);
        long wkey = h.Buffer(16);
        Check("_Wcsxfrm", Call("_Wcsxfrm", wkey, wkey + 16, wabc, wabc + 6, 0) == 3 && h.ReadWide(wkey) == "abc");
        long wc = h.Buffer(4);
        Check("_Mbrtowc: one byte to the same code unit", Call("_Mbrtowc", wc, h.Narrow("A"), 1, 0, 0) == 1 && h.U16(wc) == 'A');
        Check("_Mbrtowc: the NUL character is 0", Call("_Mbrtowc", wc, h.Narrow(""), 1, 0, 0) == 0 && h.U16(wc) == 0);

        // ---- vccorlib110 ----
        Check("InitializeData is S_OK", Call("?InitializeData@Details@Platform@@YAJH@Z", 1) == 0);
        Check("TerminateModule(NULL) is true", RunGuest("?TerminateModule@Details@Platform@@YA_NPAVModuleBase@1WRL@Microsoft@@@Z", 0) == 1);
        long objects = 0;
        long moduleVtable = e.AllocateHeap(16);
        for (int i = 0; i < 4; i++)
        {
            int slot = i;
            e.WriteUInt32(moduleVtable + (i * 4), (uint)ArmEmulator.ThumbEntry(e.RegisterVtableMethod($"test module::{slot}", () => e.WriteRegister(Harness.R0, slot == 3 ? objects : 0))));
        }

        long module = h.Buffer(8);
        e.WriteUInt32(module, (uint)moduleVtable);
        Check("TerminateModule: true when GetObjectCount is 0", RunGuest("?TerminateModule@Details@Platform@@YA_NPAVModuleBase@1WRL@Microsoft@@@Z", module) == 1, lastFault ?? "");
        objects = 3;
        Check("TerminateModule: false while objects live", RunGuest("?TerminateModule@Details@Platform@@YA_NPAVModuleBase@1WRL@Microsoft@@@Z", module) == 0, lastFault ?? "");

        long rect = h.Buffer(16);
        h.SetS32(0, 1); h.SetS32(1, 2); h.SetS32(2, 10); h.SetS32(3, 20);
        Check("Rect(Point, Size): this, { X, Y, Width, Height } from s0-s3",
            Call("??0Rect@Foundation@Windows@@QAA@VPoint@12@VSize@12@@Z", rect) == rect &&
            h.F32(rect) == 1 && h.F32(rect + 4) == 2 && h.F32(rect + 8) == 10 && h.F32(rect + 12) == 20);
        long empty = h.Buffer(16);
        h.SetS32(0, 1); h.SetS32(1, 2); h.SetS32(2, float.NegativeInfinity); h.SetS32(3, float.NegativeInfinity);
        Call("??0Rect@Foundation@Windows@@QAA@VPoint@12@VSize@12@@Z", empty);
        Check("Rect(Point, Size::Empty) is Rect::Empty", h.F32(empty) == float.PositiveInfinity && h.F32(empty + 8) == float.NegativeInfinity);
        bool Intersects(long self, float x, float y, float w, float hgt)
        {
            h.SetS32(0, x); h.SetS32(1, y); h.SetS32(2, w); h.SetS32(3, hgt);
            return Call("?IntersectsWith@Rect@Foundation@Windows@@QAA_NV123@@Z", self) == 1;
        }

        Check("IntersectsWith: overlapping", Intersects(rect, 5, 5, 10, 10));
        Check("IntersectsWith: touching edges count", Intersects(rect, 11, 22, 3, 3));
        Check("IntersectsWith: apart", !Intersects(rect, 12, 0, 3, 3));
        Check("IntersectsWith: an empty rect intersects nothing", !Intersects(empty, 0, 0, 100, 100) && !Intersects(rect, 0, 0, float.NegativeInfinity, float.NegativeInfinity));

        long String(string text)
        {
            long handle = h.Buffer(4);
            Call("WindowsCreateString", h.Wide(text), text.Length, handle);
            return U32(handle);
        }

        const string Box = "?__abi_cast_String_to_Object@__abi_details@@YAP$AAVObject@Platform@@P$AAVString@3@@Z";
        long x1 = Call(Box, String("same")), x2 = Call(Box, String("same")), y = Call(Box, String("other"));
        const string Equals = "?Equals@ValueType@Platform@@Q$AAA_NP$AAVObject@2@@Z";
        Check("ValueType::Equals: two boxes of the same value", Call(Equals, x1, x2) == 1);
        Check("ValueType::Equals: different values", Call(Equals, x1, y) == 0);
        Check("ValueType::Equals: null is never equal", Call(Equals, x1, 0) == 0);
        long hash = RunGuest("?GetHashCode@Object@Platform@@Q$AAAHXZ", x1);
        Check("Object::GetHashCode is the object's IUnknown identity", lastFault is null && hash != 0 && RunGuest("?GetHashCode@Object@Platform@@Q$AAAHXZ", x1) == hash, lastFault ?? $"0x{hash:X}");
        Check("... different objects hash apart", RunGuest("?GetHashCode@Object@Platform@@Q$AAAHXZ", y) != hash);

        foreach (string name in new[] { "vfprintf", "fscanf", "printf_s", "wprintf", "_create_locale", "_free_locale", "_Strcoll", "_Strxfrm", "_Wcscoll", "_Wcsxfrm", "_Mbrtowc",
                     "?TerminateModule@Details@Platform@@YA_NPAVModuleBase@1WRL@Microsoft@@@Z", "?Equals@ValueType@Platform@@Q$AAA_NP$AAVObject@2@@Z",
                     "?GetHashCode@Object@Platform@@Q$AAAHXZ", "??0Rect@Foundation@Windows@@QAA@VPoint@12@VSize@12@@Z", "?IntersectsWith@Rect@Foundation@Windows@@QAA_NV123@@Z",
                     "?_Swap_all@_Container_base0@std@@QAAXAAU12@@Z" })
        {
            Check($"{name} is implemented", s.IsImplemented(name));
        }

        _ = Encoding.ASCII;
    }
}

using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// What a catch clause receives in its object slot (vcruntime's __BuildCatchObject): the address
/// of the thrown value for a catch by reference, the pointer itself for a by-value catch of a
/// pointer type. Halo: Spartan Assault catches <c>Platform::Exception^</c> by value while making
/// its first save, and was handed the address of the handle.
/// </summary>
public sealed class CatchObjectSuite : IContractSuite
{
    public string Name => "C++ catch objects";

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);

        UnwoundFrame frame = new(0x00401000, 0x1000, 0x40, true, "test");
        CatchCandidate ByValue(string type) => new(frame, 0, 0, type, 0x2000) { CatchObjectOffset = -0x20, Adjectives = 0x20 };
        CatchCandidate ByReference(string type) => new(frame, 0, 0, type, 0x2000) { CatchObjectOffset = -0x20, Adjectives = 0x08 };

        // A guest `throw ref new COMException(hr)`: _CxxThrowException gets the address of the handle.
        long exception = h.Buffer(0x40);
        long handleSlot = h.Buffer(4);
        e.WriteUInt32(handleSlot, (uint)exception);
        string[] hat = [".P$AAVCOMException@Platform@@", ".P$AAVException@Platform@@", ".P$AAVObject@Platform@@", ".PAX"];
        ThrownException guestThrow = new(handleSlot, hat) { ThrowInfo = 0x01234560 };

        Check("a by-value catch(Exception^) of a guest throw gets the handle, not its address",
            h.S.CatchObjectValue(ByValue(".P$AAVException@Platform@@"), guestThrow) == exception);
        Check("a by-reference catch(Exception^&) gets the address of the handle",
            h.S.CatchObjectValue(ByReference(".P$AAVException@Platform@@"), guestThrow) == handleSlot);

        // A host-raised Platform exception carries the handle itself.
        ThrownException raised = new(exception, hat) { ObjectIsHandle = true };
        Check("a by-value catch of a host raise gets the handle",
            h.S.CatchObjectValue(ByValue(".P$AAVException@Platform@@"), raised) == exception);
        long holder = h.S.CatchObjectValue(ByReference(".P$AAVException@Platform@@"), raised);
        Check("a by-reference catch of a host raise gets a word holding the handle",
            holder != exception && h.U32(holder) == exception);

        // A class thrown by value (std::exception and friends) is still caught at its address.
        long stdObject = h.Buffer(12);
        ThrownException stdThrow = new(stdObject, [".?AVruntime_error@std@@", ".?AVexception@std@@"]);
        Check("a catch(const std::exception&) gets the object's address",
            h.S.CatchObjectValue(ByReference(".?AVexception@std@@"), stdThrow) == stdObject);
        Check("a by-value class catch keeps getting the address (no copy constructor run)",
            h.S.CatchObjectValue(ByValue(".?AVexception@std@@") with { Adjectives = 0 }, stdThrow) == stdObject);
    }
}

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The GC object header stores the payload size in a single byte, so no
/// allocation can exceed 255 bytes. `list.append`'s grow path used to double
/// the capacity unconditionally (`cap * 2`) and never checked the pointer
/// GcAlloc handed back: a list[uint16] reaching 64 elements requested a
/// 258-byte object, gc_alloc returned null, and the new header was written
/// through SRAM[0] -- the program hung with no diagnostic. In the emulator the
/// failure surfaced as adafruit_irremote's read_pulses() freezing on the 65th
/// pulse of an NEC frame (cp-bisect test03).
///
/// The grow path now clamps the request to the largest capacity the object
/// format can represent ((255 - 2) / elemSize), raises MemoryError when the
/// list truly outgrows that ceiling, and raises MemoryError on a null return
/// instead of dereferencing it.
/// </summary>
public class ListGrowCeilingTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void AppendGrowClampsNewCapacityToTheObjectCeiling()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n\n" +
            "def grow(xs: list[uint16], v: uint16) -> None:\n" +
            "    xs.append(v)\n\n" +
            "xs: list[uint16] = list()\n" +
            "grow(xs, 1)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        // (255 - 2) / 2 = 126 for uint16 elements: the clamp compare must be
        // emitted against that constant.
        Assert.Contains(main.Body, i => i is Binary gt
            && gt.Op == PyMCU.IR.BinaryOp.GreaterThan
            && gt.Src2 is Constant c && c.Value == 126);
    }

    [Fact]
    public void AppendGrowRefusesAtTheObjectCeiling()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n\n" +
            "def grow(xs: list[uint16], v: uint16) -> None:\n" +
            "    xs.append(v)\n\n" +
            "xs: list[uint16] = list()\n" +
            "grow(xs, 1)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        // len == cap on the slow path: when len is already the maximum
        // representable capacity there is no larger object to move into --
        // the grew-check fails and the path signals MemoryError.
        int clampIdx = main.Body.FindIndex(i => i is Binary gt
            && gt.Op == PyMCU.IR.BinaryOp.GreaterThan
            && gt.Src2 is Constant c && c.Value == 126);
        Assert.True(clampIdx >= 0);
        Assert.Contains(main.Body.Skip(clampIdx), i => i is SignalError);
    }

    [Fact]
    public void AppendGrowChecksTheAllocationResult()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n\n" +
            "def grow(xs: list[uint16], v: uint16) -> None:\n" +
            "    xs.append(v)\n\n" +
            "xs: list[uint16] = list()\n" +
            "grow(xs, 1)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        int allocIdx = main.Body.FindIndex(i => i is GcAlloc);
        Assert.True(allocIdx >= 0);
        Assert.Contains(main.Body.Skip(allocIdx + 1), i => i is JumpIfZero);
    }

    [Fact]
    public void Uint8ListsClampToTheirOwnCeiling()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n\n" +
            "def grow(xs: list[uint8], v: uint8) -> None:\n" +
            "    xs.append(v)\n\n" +
            "xs: list[uint8] = list()\n" +
            "grow(xs, 1)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        // (255 - 2) / 1 = 253 for uint8 elements.
        Assert.Contains(main.Body, i => i is Binary gt
            && gt.Op == PyMCU.IR.BinaryOp.GreaterThan
            && gt.Src2 is Constant c && c.Value == 253);
    }

    [Fact]
    public void AppendGrowUsesAFiftyPercentStepNotDoubling()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n\n" +
            "def grow(xs: list[uint16], v: uint16) -> None:\n" +
            "    xs.append(v)\n\n" +
            "xs: list[uint16] = list()\n" +
            "grow(xs, 1)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        // Doubling overshoots the 255-byte object ceiling long before the heap
        // is full: a list[uint16] reaching 64 elements jumped straight to the
        // 126-element clamp, parking ~120 dead bytes inside the object. On a
        // 733-byte heap that waste alone decides whether decode_bits fits.
        // The grow path must request cap + cap/2 (still amortised O(1)): an
        // RShift-by-1 result added back to the same operand.
        Assert.Contains(main.Body, i => i is Binary half
            && half.Op == PyMCU.IR.BinaryOp.RShift
            && half.Src2 is Constant h && h.Value == 1
            && main.Body.Any(j => j is Binary add
                && add.Op == PyMCU.IR.BinaryOp.Add
                && add.Src1 == half.Src1
                && add.Src2 == half.Dst));
    }
}

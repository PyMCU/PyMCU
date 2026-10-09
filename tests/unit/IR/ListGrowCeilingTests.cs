using PyMCU.Common;
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
    public void AppendGrowDoublesLikeMain()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n\n" +
            "def grow(xs: list[uint16], v: uint16) -> None:\n" +
            "    xs.append(v)\n\n" +
            "xs: list[uint16] = list()\n" +
            "grow(xs, 1)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        // The growth policy is main's doubling: cap << 1, a single LShift. A
        // 1.5x step was tried and reverted -- the extra shift/add/compare it
        // emits at every append site grew every GC program that appends.
        Assert.Contains(main.Body, i => i is Binary shl
            && shl.Op == PyMCU.IR.BinaryOp.LShift
            && shl.Src2 is Constant s && s.Value == 1);
    }

    // ------------------------------------------------------------------
    // The other two GcAlloc call sites that landed a header through a null pointer
    // unchecked: `x = []` later appended (Assign.cs, promotedEmptyLists) and an
    // annotated `x: list[T] = [...]` declaration with a literal initializer
    // (Assign.cs, EmitListAnnAssign -- also list[list[T]]'s own materialization).
    // Every OTHER GcAlloc call site already raises MemoryError on a null result;
    // these two did not, so a doubly-failed allocation (OOM, then OOM again after
    // the collection attempt gc_alloc makes internally) wrote the list header
    // through SRAM[0] -- the register file on AVR, R0/R1 included.
    // ------------------------------------------------------------------

    [Fact]
    public void EmptyListPromotionChecksTheAllocationResult()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n\n" +
            "x = []\n" +
            "x.append(1)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        int allocIdx = main.Body.FindIndex(i => i is GcAlloc { Size: Constant { Value: 2 } });
        Assert.True(allocIdx >= 0);
        var tail = main.Body.Skip(allocIdx + 1).ToList();
        Assert.Contains(tail, i => i is Binary b && b.Op == PyMCU.IR.BinaryOp.NotEqual);
        Assert.Contains(tail, i => i is JumpIfNotZero);
        Assert.Contains(tail, i => i is SignalError);
    }

    [Fact]
    public void AnnotatedListLiteralChecksTheAllocationResult()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n\n" +
            "x: list[uint8] = [1, 2, 3]\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        int allocIdx = main.Body.FindIndex(i => i is GcAlloc);
        Assert.True(allocIdx >= 0);
        var tail = main.Body.Skip(allocIdx + 1).ToList();
        Assert.Contains(tail, i => i is Binary b && b.Op == PyMCU.IR.BinaryOp.NotEqual);
        Assert.Contains(tail, i => i is JumpIfNotZero);
        Assert.Contains(tail, i => i is SignalError);
        // The check must run BEFORE any element is stored through the pointer.
        int signalIdx = tail.FindIndex(i => i is SignalError);
        int firstElemStoreIdx = tail.FindIndex(i => i is ArrayStore);
        Assert.True(firstElemStoreIdx < 0 || signalIdx < firstElemStoreIdx);
    }

    [Fact]
    public void AnnotatedListOfListsLiteralChecksTheAllocationResult()
    {
        // list[list[T]]'s own materialization goes through the SAME EmitListAnnAssign
        // path, with elemDt == DataType.GC_REF -- the ref-bearing flag set on the SAME
        // GcAlloc call this fix checks.
        var ir = Gen(
            "from pymcu.types import uint8\n\n" +
            "bins: list[list[uint8]] = [[1, 2]]\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        int allocIdx = main.Body.FindIndex(i => i is GcAlloc { Refs: true });
        Assert.True(allocIdx >= 0);
        Assert.Contains(main.Body.Skip(allocIdx + 1), i => i is SignalError);
    }

    // ------------------------------------------------------------------
    // Target architecture gate (EmitListAnnAssign, Assign.cs): a growable list[T] needs
    // the GC heap's collector. ARM (rp2040/rp2350, arch="arm") implements it the same way
    // AVR does; any other target still refuses, naming a fixed array as the portable
    // alternative.
    // ------------------------------------------------------------------

    private static ProgramIR GenFor(string arch, string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = arch });

    [Fact]
    public void AnnotatedGrowableListIsAcceptedOnArm()
    {
        var ir = GenFor("arm",
            "from pymcu.types import uint8\n\n" +
            "xs: list[uint8] = list()\n" +
            "xs.append(1)\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is GcAlloc);
    }

    [Fact]
    public void AnnotatedGrowableListStillRefusedOnAnUnsupportedTarget()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() => GenFor("riscv",
            "from pymcu.types import uint8\n\n" +
            "xs: list[uint8] = list()\n"));
        Assert.Contains("only implemented on AVR and ARM", ex.Message);
        Assert.Contains("riscv", ex.Message);
    }
}

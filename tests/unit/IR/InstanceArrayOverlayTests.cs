using PyMCU.Backend.Analysis;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#275. Two module-level objects, each holding a fixed-size array field, written through
/// two sibling calls, were given the SAME SRAM address. Every read of one answered with the
/// other's value. It compiled, it ran, nothing was refused and nothing was warned.
///
///     lo = Cell(); hi = Cell()
///     def put_lo(i, v): lo.store(i, v)
///     def put_hi(i, v): hi.store(i, v)
///     put_lo(0, 0x11); put_hi(0, 0x22)   ->  lo.load(0) answered 0x22
///
/// Reached DIRECTLY from main the same two objects were correct, which is what kept it hidden,
/// and one-through-a-call with one direct was correct too. Both through calls aliased.
///
/// The overlay is not at fault and is not changed here. Module-level arrays are registered as
/// global arrays for exactly this reason -- Core.cs says it on the line, "so the overlay
/// algorithm never aliases them with function-local arrays across sibling calls" -- and an
/// array reached as `obj.buf` never reached that registration. It had a name and a size only
/// because the allocator infers both from the ArrayStore it sees, inside whichever function did
/// the store, so two of them written from two siblings looked like two disjoint locals.
///
/// These assertions are on ADDRESSES, from the allocator that produces them, not on values. A
/// value test can pass by luck: write `lo` then `hi` and read them back in that order and an
/// aliased pair still answers correctly for the second one.
/// </summary>
public class InstanceArrayOverlayTests
{
    private const string Cell =
        "from pymcu.types import uint8\n" +
        "class Cell:\n" +
        "    def __init__(self):\n" +
        "        self.buf: uint8[2] = [0, 0]\n" +
        "    def store(self, i: uint8, v: uint8):\n" +
        "        self.buf[i] = v\n" +
        "    def load(self, i: uint8) -> uint8:\n" +
        "        return self.buf[i]\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    /// <summary>The SRAM offsets the allocator hands the backend, which become its .equ lines.</summary>
    private static Dictionary<string, int> Offsets(string src) =>
        new StackAllocator().Allocate(Gen(src)).Offsets;

    private static int OffsetOf(Dictionary<string, int> offsets, string name)
    {
        Assert.True(offsets.ContainsKey(name),
            $"'{name}' has no address at all; the allocator placed [{string.Join(", ", offsets.Keys)}]");
        return offsets[name];
    }

    private const string TwoThroughCalls =
        Cell +
        "lo = Cell()\n" +
        "hi = Cell()\n" +
        "def put_lo(i: uint8, v: uint8):\n" +
        "    lo.store(i, v)\n" +
        "def put_hi(i: uint8, v: uint8):\n" +
        "    hi.store(i, v)\n" +
        "def main() -> uint8:\n" +
        "    put_lo(0, 0x11)\n" +
        "    put_hi(0, 0x22)\n" +
        "    return lo.load(0)\n";

    [Fact]
    public void TwoModuleLevelInstances_DoNotShareOneArrayAddress()
    {
        var off = Offsets(TwoThroughCalls);

        Assert.NotEqual(OffsetOf(off, "lo_buf"), OffsetOf(off, "hi_buf"));
    }

    [Fact]
    public void TheirArraysAreRegisteredAsGlobalArrays_WithTheirDeclaredSize()
    {
        var ir = Gen(TwoThroughCalls);

        Assert.Equal(2, ir.GlobalArrays["lo_buf"]);
        Assert.Equal(2, ir.GlobalArrays["hi_buf"]);
    }

    // The same objects held by a CLASS attribute rather than by a module-level name. They are
    // constructed by the module init under the flattened name, and they aliased the same way.
    [Fact]
    public void TwoClassAttributeInstances_DoNotShareOneArrayAddress()
    {
        var off = Offsets(
            Cell +
            "class Reg:\n" +
            "    lo = Cell()\n" +
            "    hi = Cell()\n" +
            "class Driver:\n" +
            "    def __init__(self, tag: uint8):\n" +
            "        self.tag = tag\n" +
            "    def put_lo(self, i: uint8, v: uint8):\n" +
            "        Reg.lo.store(i, v)\n" +
            "    def put_hi(self, i: uint8, v: uint8):\n" +
            "        Reg.hi.store(i, v)\n" +
            "d = Driver(1)\n" +
            "def main() -> uint8:\n" +
            "    d.put_lo(0, 0x11)\n" +
            "    d.put_hi(0, 0x22)\n" +
            "    return Reg.lo.load(0)\n");

        Assert.NotEqual(OffsetOf(off, "Reg_lo_buf"), OffsetOf(off, "Reg_hi_buf"));
    }

    // THE CONTROL THAT KEEPS THE OVERLAY DOING ITS JOB.
    //
    // The allocator names a function-local `use_a.c_buf`; the backend flattens the dot when it
    // writes the .equ. Same slot, same question.
    //
    // Two arrays belonging to FUNCTION-LOCAL instances have disjoint lifetimes and must keep
    // SHARING one slot. Registering every instance field as a global would make this test's two
    // offsets differ, pass every test above, and quietly cost SRAM in every program that builds
    // an object inside a function. It is the only assertion here that fails in that direction.
    [Fact]
    public void TwoFunctionLocalInstances_STILLShareOneArrayAddress()
    {
        var off = Offsets(
            Cell +
            "def use_a(v: uint8) -> uint8:\n" +
            "    c = Cell()\n" +
            "    c.store(0, v)\n" +
            "    return c.load(0)\n" +
            "def use_b(v: uint8) -> uint8:\n" +
            "    e = Cell()\n" +
            "    e.store(1, v)\n" +
            "    return e.load(1)\n" +
            "def main() -> uint8:\n" +
            "    return use_a(0x11) + use_b(0x22)\n");

        Assert.Equal(OffsetOf(off, "use_a.c_buf"), OffsetOf(off, "use_b.e_buf"));
    }

    // A function-local instance's array is not a global array either: the registration must not
    // reach past the module's own top level.
    [Fact]
    public void AFunctionLocalInstanceArray_IsNotRegisteredAsAGlobalArray()
    {
        var ir = Gen(
            Cell +
            "def use_a(v: uint8) -> uint8:\n" +
            "    c = Cell()\n" +
            "    c.store(0, v)\n" +
            "    return c.load(0)\n" +
            "def main() -> uint8:\n" +
            "    return use_a(0x11)\n");

        Assert.DoesNotContain(ir.GlobalArrays.Keys, k => k.EndsWith("c_buf", StringComparison.Ordinal));
    }

    // One instance, one array: registered once. Registering the bare name as well as the
    // prefixed one would give it two homes and spend the SRAM twice.
    [Fact]
    public void OneInstanceArray_IsRegisteredExactlyOnce()
    {
        var ir = Gen(
            Cell +
            "lo = Cell()\n" +
            "def put(i: uint8, v: uint8):\n" +
            "    lo.store(i, v)\n" +
            "def main() -> uint8:\n" +
            "    put(0, 0x11)\n" +
            "    return lo.load(0)\n");

        Assert.Single(ir.GlobalArrays.Keys, k => k.EndsWith("lo_buf", StringComparison.Ordinal));
    }
}

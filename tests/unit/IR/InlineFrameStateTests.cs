using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An @inline body's locals are spelled `inline{depth}.{callee}.{name}`, so every expansion
/// of one callee at one depth reuses the same keys. Two things follow, and both were silent
/// wrong code before these tests:
///
/// - What a previous expansion bound under those keys must not answer for the next one.
///   `m[0x0601] = v` bound `p: ptr[uint8] = ptr(k)` to a constant address; the following
///   `m[a] = v` kept it and stored through the byte at 0x0601. The other order left a
///   run-time pointer mark behind, and `ptr(0x0610)` stored through the byte AT 0x0610.
/// - The spelling must not be shared between two functions. Every backend gives a name one
///   storage program-wide, so main's `inline1.__init__.base` and the same spelling inside a
///   function main calls were one slot, and the value main held across the call came back
///   as the callee's.
/// </summary>
public class InlineFrameStateTests
{
    private const string Regs =
        "from pymcu.types import uint8, uint16, inline, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Regs + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig());

    private static Function Fn(ProgramIR ir, string name) =>
        ir.Functions.Single(f => f.Name == name);

    private static List<StoreIndirect> IndirectStores(Function f) =>
        f.Body.OfType<StoreIndirect>().ToList();

    private static bool StoresDirectlyTo(Function f, int address) =>
        f.Body.Any(i => i is Copy { Dst: MemoryAddress m } && m.Address == address);

    [Fact]
    public void ADunderExpansion_DoesNotKeepThePreviousConstantPointer()
    {
        var ir = Gen(
            "class M8:\n" +
            "    @inline\n" +
            "    def __setitem__(self, addr: uint16, value: uint8):\n" +
            "        p: ptr[uint8] = ptr(addr)\n" +
            "        p.value = value\n" +
            "m8 = M8()\n" +
            "m8[0x0601] = 0x5A\n" +
            "a: uint16 = G.value + 0x0602\n" +
            "m8[a] = G.value + 0x34\n");

        var main = Fn(ir, "main");
        Assert.True(StoresDirectlyTo(main, 0x0601));
        // The run-time store goes through the pointer the second expansion computed, not
        // through the byte the first one named.
        var st = Assert.Single(IndirectStores(main));
        Assert.IsType<Variable>(st.DstPtr);
    }

    [Theory]
    [InlineData("@inline\ndef put(k: uint16, v: uint8):\n    p: ptr[uint8] = ptr(k)\n    p.value = v\n",
                "put({0}, {1})\n")]
    [InlineData("class M:\n    @inline\n    def __setitem__(self, k: uint16, v: uint8):\n" +
                "        p: ptr[uint8] = ptr(k)\n        p.value = v\nm = M()\n",
                "m[{0}] = {1}\n")]
    public void AConstantPointerAfterARunTimeOne_StoresIntoTheAddress(string def, string call)
    {
        var ir = Gen(def +
            "a: uint16 = G.value + 0x0610\n" +
            string.Format(call, "a", "G.value") +
            string.Format(call, "0x0620", "0x22"));

        var main = Fn(ir, "main");
        Assert.True(StoresDirectlyTo(main, 0x0620));
        // Only the run-time expansion stores indirectly, and never through a fixed cell.
        var st = Assert.Single(IndirectStores(main));
        Assert.IsNotType<MemoryAddress>(st.DstPtr);
    }

    [Fact]
    public void RebindingARunTimePointerToAConstant_LoadsTheAddress()
    {
        var ir = Gen(
            "def f():\n" +
            "    a: uint16 = G.value + 0x0610\n" +
            "    p: ptr[uint8] = ptr(a)\n" +
            "    p.value = 0x11\n" +
            "    p = ptr(0x0620)\n" +
            "    p.value = 0x22\n" +
            "f()\n");

        var f = Fn(ir, "f");
        Assert.Contains(f.Body, i => i is Copy { Src: Constant { Value: 0x0620 }, Dst: Variable { Name: "f.p" } });
        Assert.DoesNotContain(f.Body, i => i is Copy { Src: MemoryAddress, Dst: Variable { Name: "f.p" } });
    }

    [Fact]
    public void TwoFunctionsExpandingTheSameInline_DoNotShareItsLocals()
    {
        // w's `t` is live across the call to f, and f expands w too.
        var ir = Gen(
            "@inline\n" +
            "def w(k: uint8, deep: uint8) -> uint8:\n" +
            "    t: uint8 = k + 1\n" +
            "    u: uint8 = 0\n" +
            "    if deep == 1:\n" +
            "        u = f()\n" +
            "    return t + u\n" +
            "def f() -> uint8:\n" +
            "    return w(G.value + 40, 0)\n" +
            "r: uint8 = w(G.value + 7, 1)\n");

        static HashSet<string> Written(Function fn) => fn.Body
            .Select(i => i switch
            {
                Copy { Dst: Variable v } => v.Name,
                Binary { Dst: Variable v } => v.Name,
                _ => null,
            })
            .Where(n => n != null && n.StartsWith("inline", StringComparison.Ordinal))
            .Select(n => n!)
            .ToHashSet();

        var inMain = Written(Fn(ir, "main"));
        var inF = Written(Fn(ir, "f"));
        Assert.NotEmpty(inMain);
        Assert.NotEmpty(inF);
        Assert.Empty(inMain.Intersect(inF));
    }

    [Fact]
    public void AConstructorExpandedInTwoFunctions_KeepsTheCallersArgument()
    {
        // A's __init__ holds `base` across mk(), which builds a B: both are
        // `__init__` at depth 1, the spelling the two frames used to share.
        var ir = Gen(
            "class B:\n" +
            "    def __init__(self, base: uint8):\n" +
            "        self.v: uint8 = base\n" +
            "def mk() -> uint8:\n" +
            "    o = B(G.value + 50)\n" +
            "    return o.v\n" +
            "class A:\n" +
            "    def __init__(self, base: uint8):\n" +
            "        k: uint8 = mk()\n" +
            "        self.base: uint8 = base\n" +
            "        self.k: uint8 = k\n" +
            "a = A(G.value + 7)\n");

        static IEnumerable<string> Names(Function fn) => fn.Body
            .OfType<Copy>().Select(c => c.Dst).OfType<Variable>().Select(v => v.Name);

        var shared = Names(Fn(ir, "main")).Intersect(Names(Fn(ir, "mk")))
            .Where(n => n.StartsWith("inline", StringComparison.Ordinal));
        Assert.Empty(shared);
    }
}

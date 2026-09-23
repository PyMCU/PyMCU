using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Loop constant-invalidation asks what a called method mutates: it resolves the callee
/// through the receiver's MRO, then walks the DEFINING class's sibling map for the
/// self-calls inside the body. That map walked the base chain only while the current
/// entry still carried its trailing '_' separator, so it stopped after the first hop --
/// a self-call to a method two or more bases up resolved to nothing and the caller was
/// reported as mutating every field. A runtime loop over `o.m()` then invalidated
/// constants `m` never touched: `o.f` lost its 17 and `x` read the field at run time.
/// </summary>
public class InheritedMethodMutationTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig());
    }

    private static List<Instruction> MainBody(ProgramIR ir) =>
        ir.Functions.First(f => f.Name == "main").Body;

    /// <summary>
    /// `Leaf.m` calls `self.deep()`, which `Root` defines -- two bases above `Leaf`.
    /// `deep` writes `self.buf[0]`, an index store `MethodMutatesField` does not count
    /// as a field write, so `m` mutates nothing and `o.f`'s constant must survive a
    /// non-unrolled loop over `o.m()`. `m` carries a `for` only so it stays out of the
    /// outline pool (a construct IsOutlineSafe has no case for); outlining it would
    /// mark `f` write-back through a different map and hide the join under test.
    /// </summary>
    [Fact]
    public void LoopCallToDeepInheritedMethodKeepsUnrelatedConstant()
    {
        var body = MainBody(Gen(
            "from pymcu.types import uint8\n" +
            "class Root:\n" +
            "    def __init__(self):\n" +
            "        self.f = 17\n" +
            "        self.buf = bytearray(4)\n" +
            "    def deep(self):\n" +
            "        self.buf[0] = 1\n" +
            "class Mid(Root):\n" +
            "    pass\n" +
            "class Leaf(Mid):\n" +
            "    def m(self):\n" +
            "        for j in range(2):\n" +
            "            self.deep()\n" +
            "o = Leaf()\n" +
            "for i in range(100):\n" +
            "    o.m()\n" +
            "x = o.f\n"));

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 17 }, Dst: Variable { Name: "x" } });
    }

    /// <summary>
    /// The receiver half of loop invalidation removed the field's constant but left the
    /// name re-trackable: `measure` then stored `f = 1` on its error arms and `f = 0`
    /// unconditionally at the end, so the last write's value folded into every later
    /// read -- `G.value = o.f` emitted `const 0` on paths where `f` was 1. A name a
    /// called method writes is runtime-mutable for the rest of the compile; it must go
    /// to killedConstants, not just lose its current entry. (The `for` keeps `measure`
    /// out of the outline pool so its writes expand inside the loop.)
    /// </summary>
    [Fact]
    public void MethodWrittenFieldStaysRuntimeAfterLoopInvalidation()
    {
        var body = MainBody(Gen(
            "from pymcu.types import uint8, ptr\n" +
            "G: ptr[uint8] = ptr(0x3E)\n" +
            "class DHTBase:\n" +
            "    def __init__(self, pin: uint8):\n" +
            "        self.f = 0\n" +
            "        self.pin = pin\n" +
            "    def measure(self):\n" +
            "        for j in range(2):\n" +
            "            pass\n" +
            "        if self.pin > 0:\n" +
            "            self.f = 1\n" +
            "            return\n" +
            "        self.f = 0\n" +
            "class DHT(DHTBase):\n" +
            "    pass\n" +
            "o = DHT(G.value)\n" +
            "for i in range(100):\n" +
            "    o.measure()\n" +
            "    G.value = o.f\n"));

        Assert.Contains(body, i => i is Copy { Src: Variable { Name: "o_f" }, Dst: MemoryAddress });
        Assert.DoesNotContain(body, i => i is Copy { Src: Constant { Value: 0 }, Dst: MemoryAddress });
    }
}

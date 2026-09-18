using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `f()[k]` on an inline multi-return call: the expansion writes each returned element to
/// its own slot and the subscript reads slot k -- the tuple CPython would build is never
/// materialised. This is the shape adafruit_tcs34725's `_temperature_and_lux_dn40()[0]`
/// writes.
///
/// The arity comes from the `-> (T1, T2)` annotation when the callee declares one and from
/// the tuple returns in its body when it does not. What the subscript CANNOT be is dynamic:
/// each element is a separate slot, so a run-time index has no object to index.
/// </summary>
public class TupleIndexCallTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(),
                                          new DeviceConfig { Arch = "avr" });
    }

    private static CompilerError Fails(string src) =>
        Assert.ThrowsAny<CompilerError>(() => Gen(src));

    private const string Pair =
        "@inline\n" +
        "def pair(a: uint8) -> (uint8, uint8):\n" +
        "    return (a + 1, a + 2)\n";

    private const string Tri =
        "@inline\n" +
        "def tri(x: uint8):\n" +
        "    if x > 10:\n" +
        "        return (1, 2, 3)\n" +
        "    return (x, x + 1, x + 2)\n";

    /// <summary>The slot names a `f()[k]` site reads, in order.</summary>
    private static List<string> SlotsRead(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Select(c => c.Src).OfType<Variable>()
            .Select(v => v.Name).Where(n => n.Contains("iret_")).ToList();

    [Fact]
    public void AnnotatedTuple_Index0_ReadsTheFirstSlot()
    {
        var ir = Gen(Pair + "def main():\n    x: uint8 = pair(3)[0]\n");

        Assert.Contains(SlotsRead(ir), s => s.EndsWith("_0"));
    }

    [Fact]
    public void AnnotatedTuple_Index1_ReadsTheSecondSlot()
    {
        var ir = Gen(Pair + "def main():\n    x: uint8 = pair(3)[1]\n");

        Assert.Contains(SlotsRead(ir), s => s.EndsWith("_1"));
    }

    [Fact]
    public void UnannotatedTuple_ArityComesFromTheBody()
    {
        var ir = Gen(Tri + "def main():\n    x: uint8 = tri(20)[2]\n");

        Assert.Contains(SlotsRead(ir), s => s.EndsWith("_2"));
    }

    [Fact]
    public void UnpackAssignment_StillWorks()
    {
        var ir = Gen(Pair + "def main():\n    a: uint8 = 0\n    b: uint8 = 0\n    a, b = pair(3)\n");

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void OutOfRangeIndex_IsAnIndexError()
    {
        var err = Fails(Pair + "def main():\n    x: uint8 = pair(3)[2]\n");

        Assert.Contains("out of range", err.Message);
    }

    [Fact]
    public void NegativeIndex_IsAnIndexError()
    {
        var err = Fails(Pair + "def main():\n    x: uint8 = pair(3)[-1]\n");

        Assert.Contains("out of range", err.Message);
    }

    [Fact]
    public void RuntimeIndex_IsRefused_AndSaysWhy()
    {
        var err = Fails(Pair + "def main():\n    i: uint8 = 1\n    x: uint8 = pair(3)[i]\n");

        Assert.Contains("compile-time", err.Message);
    }

    [Fact]
    public void ConstructorGetItem_StillDispatches()
    {
        var ir = Gen(
            "class Vec:\n" +
            "    x: uint8\n" +
            "    y: uint8\n" +
            "    @inline\n" +
            "    def __init__(self, x: uint8, y: uint8):\n" +
            "        self.x = x\n" +
            "        self.y = y\n" +
            "    @inline\n" +
            "    def __getitem__(self, i: uint8) -> uint8:\n" +
            "        if i == 0:\n" +
            "            return self.x\n" +
            "        return self.y\n" +
            "def main():\n    v: uint8 = Vec(3, 4)[1]\n");

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void MethodTuple_IndexedOnTheSpot()
    {
        var ir = Gen(
            "class Sensor:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def read(self):\n" +
            "        return (42, 43)\n" +
            "def main():\n" +
            "    s = Sensor()\n" +
            "    x: uint8 = s.read()[0]\n");

        Assert.Contains(SlotsRead(ir), s => s.EndsWith("_0"));
    }
}

using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `if b is None: b = bytearray(8)` on an `Optional[bytearray]` parameter, then `b[k]`.
/// The fresh buffer was laid out and the read still answered from the record the call site
/// left behind -- None, so an inlined method raised `TypeError: 'NoneType' object is not
/// subscriptable` for a program CPython runs and prints 0 for.
///
/// The three places the name can live are pinned apart, because they are three different
/// answers: an @inline expansion decides the None-ness per call site, so the rebinding holds
/// outright; a real subroutine receives a pointer and a tag, so the rebinding points both at
/// the new storage; and a rebinding under a condition only the run time decides, in an
/// expansion, has no handle to choose between the two buffers and is refused.
/// </summary>
public class FreshBufferRebindTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    private static List<Instruction> Body(ProgramIR ir, string fn) =>
        ir.Functions.Single(f => f.Name == fn).Body;

    private const string Method =
        "from typing import Optional\n" +
        "from pymcu.types import uint8\n" +
        "class Bus:\n" +
        "    def __init__(self):\n" +
        "        self.n = 0\n" +
        "    def search(self, b: Optional[bytearray], k: uint8) -> uint8:\n" +
        "        if b is None:\n" +
        "            b = bytearray(8)\n" +
        "        return b[k]\n" +
        "bus = Bus()\n";

    [Fact]
    public void AnInlinedMethodReadsTheBufferItJustBoundInsteadOfRaising()
    {
        var ir = Gen(Method +
            "def go(k: uint8) -> uint8:\n" +
            "    return bus.search(None, k)\n" +
            "x = go(3)\n" + "y = go(4)\n");

        var go = Body(ir, "go");
        go.OfType<SignalError>().Should().BeEmpty(
            because: "the None arm rebinds b to a real buffer, so b[k] cannot be a TypeError");
        go.OfType<ArrayLoad>().Should().Contain(l => l.ArrayName.EndsWith(".b"),
            because: "the subscript reads the buffer the arm laid out");
    }

    [Fact]
    public void ARunTimeRebindingInsideAnExpansionIsRefused()
    {
        var act = () => Gen(
            "from pymcu.types import uint8, inline\n" +
            "class Bus:\n" +
            "    def __init__(self):\n" +
            "        self.n = 0\n" +
            "    @inline\n" +
            "    def pick(self, b, k: uint8) -> uint8:\n" +
            "        if k > 3:\n" +
            "            b = bytearray(8)\n" +
            "        return b[k]\n" +
            "bus = Bus()\n" +
            "buf = bytearray(8)\n" +
            "def go(k: uint8) -> uint8:\n" +
            "    return bus.pick(buf, k)\n" +
            "x = go(3)\n" + "y = go(4)\n");

        act.Should().Throw<CompilerError>().WithMessage(
            "*'b' is rebound to a new buffer under a condition decided at run time*",
            because: "after the branch b is the caller's buffer on one path and the fresh one "
                     + "on the other, and the merge used to keep the fresh one on both");
    }

    [Fact]
    public void ARealSubroutinePointsItsPointerAndTagAtTheFreshBuffer()
    {
        var ir = Gen(
            "from typing import Optional\n" +
            "from pymcu.types import uint8\n" +
            "def take(b: Optional[bytearray], k: uint8) -> uint8:\n" +
            "    if b is None:\n" +
            "        b = bytearray(8)\n" +
            "    return b[k]\n" +
            "buf = bytearray(8)\n" +
            "def go(k: uint8) -> uint8:\n" +
            "    return take(buf, k) + take(None, k)\n" +
            "x = go(3)\n" + "y = go(4)\n");

        var take = Body(ir, "take");
        take.Any(i => i is Copy { Src: ArrayBase, Dst: Variable { Name: "take.b" } }).Should().BeTrue(because: "the load after the merge goes through the pointer, so the rebinding has "
                     + "to reach it -- and must survive the optimizer, which saw no reader of it");
        take.Any(i => i is BytearrayLoad { PtrName: "take.b" }).Should().BeTrue();
    }
}

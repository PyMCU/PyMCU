using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `match data: case [head, mid, last]:` over an array whose elements are only known at run time
/// (#401). The elements live under the key the enclosing function files the array under
/// (<c>main.data__0</c>); the lowering read the bare <c>data__0</c>, a slot nothing wrote, so every
/// capture was 0 and every literal in the pattern compared against 0.
/// </summary>
public class MatchSequenceRuntimeArrayTests
{
    private const string Prelude =
        "from pymcu.types import uint8, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n" +
        "O: ptr[uint8] = ptr(0x3F)\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static IEnumerable<string> ReadNames(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).SelectMany(i => i switch
        {
            Copy c => new[] { c.Src },
            Binary b => new[] { b.Src1, b.Src2 },
            _ => Array.Empty<Val>(),
        }).OfType<Variable>().Select(v => v.Name);

    [Fact]
    public void ACaptureReadsTheArraysOwnSlot()
    {
        var ir = Gen(
            "seed: uint8 = G.value\n" +
            "data = [seed + 7, seed + 8, seed + 9]\n" +
            "match data:\n" +
            "    case [head, mid, last]:\n" +
            "        O.value = head + mid + last\n" +
            "    case _:\n" +
            "        O.value = 0\n");
        ReadNames(ir).Should().NotContain(n => n.StartsWith("data__"),
            "the slots are filed under the function, a bare data__N was never written");
    }

    [Fact]
    public void ALiteralInThePatternIsComparedWithTheArraysOwnSlot()
    {
        var ir = Gen(
            "seed: uint8 = G.value\n" +
            "data = [seed + 7, seed + 8]\n" +
            "match data:\n" +
            "    case [7, y]:\n" +
            "        O.value = y\n" +
            "    case _:\n" +
            "        O.value = 0\n");
        ReadNames(ir).Should().NotContain(n => n.StartsWith("data__"));
    }
}

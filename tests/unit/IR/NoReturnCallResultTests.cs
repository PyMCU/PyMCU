using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#520. Reading the result of a function that returns nothing: CPython
/// binds the name to None and <c>print(x)</c> writes "None". The call emitted
/// its result as a discarded destination and the binding still materialized a
/// numeric slot, so the reader printed whatever byte the slot held: 0.
/// The fix binds the name to the compile-time None record (the same one
/// <c>x = None</c> writes), and print streams the literal text. A constructor
/// call also yields NoneVal -- its <c>__init__</c> returns void -- but the
/// binding must keep the built instance, so the two are told apart by marker
/// identity, not by value kind.
/// </summary>
public class NoReturnCallResultTests
{
    private const string Prelude =
        "from pymcu.types import uint8, uint16, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src, bool optimize = true)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return optimize ? Optimizer.Optimize(ir) : ir;
    }

    private static IEnumerable<Instruction> Body(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body);

    /// The name of the flash byte-table whose bytes are the literal "None\0".
    private static string? NoneStringName(ProgramIR ir) =>
        Body(ir).OfType<FlashData>()
            .FirstOrDefault(d => d.Bytes.SequenceEqual(new[] { 78, 111, 110, 101, 0 }))
            ?.Name;

    private static bool WritesNone(ProgramIR ir)
    {
        string? noneName = NoneStringName(ir);
        return noneName != null && Body(ir).Any(i =>
            i is Call c && c.FunctionName == "uart_write_str"
            && c.Args.Count > 0 && c.Args[0] is FlashStrAddr fs && fs.Name == noneName);
    }

    private static bool WritesDecimal(ProgramIR ir) =>
        Body(ir).Any(i => i is Call c && c.FunctionName.StartsWith("uart_write_decimal"));

    [Fact]
    public void ABoundNoReturnResult_PrintsNone()
    {
        var ir = Gen("def f():\n    pass\n\nx = f()\nprint(x)\n");
        WritesNone(ir).Should().BeTrue(
            because: "x is the compile-time None the call returned, and CPython writes \"None\"");
        WritesDecimal(ir).Should().BeFalse(
            because: "the scalar writer used to stream x's slot byte: 0");
    }

    [Fact]
    public void ABoundInlineNoReturnResult_PrintsNone()
    {
        var ir = Gen("@inline\ndef f():\n    pass\n\nx = f()\nprint(x)\n");
        WritesNone(ir).Should().BeTrue();
        WritesDecimal(ir).Should().BeFalse();
    }

    [Fact]
    public void ABoundMethodNoReturnResult_PrintsNone()
    {
        var ir = Gen(
            "class M:\n" +
            "    def m(self):\n" +
            "        pass\n\n" +
            "o = M()\nx = o.m()\nprint(x)\n");
        WritesNone(ir).Should().BeTrue();
        WritesDecimal(ir).Should().BeFalse();
    }

    [Fact]
    public void ABareReturnCountsAsNoReturnResult()
    {
        var ir = Gen("def f():\n    return\n\nx = f()\nprint(x)\n");
        WritesNone(ir).Should().BeTrue();
    }

    [Fact]
    public void AConstructorCall_IsNotMarkedNone()
    {
        var ir = Gen(
            "class P:\n" +
            "    def __init__(self, v: uint16):\n" +
            "        self.v: uint16 = v\n\n" +
            "x = P(GPIOR0.value + 321)\n" +
            "print(x.v)\n");
        WritesDecimal(ir).Should().BeTrue(
            because: "x is the built instance: its field streams a number, not \"None\"");
        WritesNone(ir).Should().BeFalse();
    }
}

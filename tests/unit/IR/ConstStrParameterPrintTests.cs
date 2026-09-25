using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `print(s)` inside a real subroutine whose parameter is declared `const[str]` wrote the
/// text's flash ADDRESS as a decimal number: `take("passed")` printed 538.
///
/// The value was right the whole time. `const[str]` lowers to a 16-bit slot and the call
/// passes the address whole; what was wrong is that print's ladder had no branch for a
/// parameter, so the address fell through to the numeric writer. The shared write_str
/// subroutine already walks flash from a pointer in registers -- the same call print makes
/// for an exception's message.
///
/// This is the 23rd branch of a 22-branch ladder that dispatches on the SYNTACTIC SHAPE of
/// the argument. The fix that scales is the one #393 took: ask the VALUE whether it stands
/// for text, not the tree.
///
/// A bare `str` parameter is a different defect and is NOT covered here: it lowers to a
/// one-byte slot, so the address is truncated before print ever sees it. That one is the
/// storage width, and the control test below pins it as still-numeric so this fix cannot be
/// mistaken for covering it.
/// </summary>
public class ConstStrParameterPrintTests
{
    private const string Prelude =
        "from pymcu.types import const\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static Function Fn(ProgramIR ir, string name) =>
        ir.Functions.Single(f => f.Name == name);

    // The body streams the parameter through write_str with the parameter itself as the
    // operand, rather than a flash literal the compiler folded.
    private static bool StreamsTheParameter(Function f, string param) =>
        f.Body.Any(i => i is Call c && c.FunctionName.EndsWith("uart_write_str")
                        && c.Args.Any(a => a is Variable v && v.Name == param));

    private static bool WritesADecimal(Function f) =>
        f.Body.Any(i => i is Call c && c.FunctionName.Contains("uart_write_decimal"));

    [Fact]
    public void PrintingAConstStrParameter_StreamsItAsText()
    {
        var ir = Gen(
            "def take(s: const[str]) -> None:\n" +
            "    print(s)\n" +
            "take(\"passed\")\n");

        var take = Fn(ir, "take");
        Assert.True(StreamsTheParameter(take, "take.s"));
        Assert.False(WritesADecimal(take));
    }

    [Fact]
    public void PrintingTwoConstStrParameters_StreamsBoth()
    {
        var ir = Gen(
            "def take(a: const[str], b: const[str]) -> None:\n" +
            "    print(a)\n" +
            "    print(b)\n" +
            "take(\"one\", \"two\")\n");

        var take = Fn(ir, "take");
        Assert.True(StreamsTheParameter(take, "take.a"));
        Assert.True(StreamsTheParameter(take, "take.b"));
        Assert.False(WritesADecimal(take));
    }

    // The control that keeps the two defects apart: a bare `str` parameter is a one-byte
    // slot, so the address reaching print is already truncated and streaming from it would
    // read flash from a pointer that lost its high byte. It keeps the numeric writer until
    // the storage width is fixed.
    [Fact]
    public void PrintingABareStrParameter_IsUntouched()
    {
        var ir = Gen(
            "def take(s: str) -> None:\n" +
            "    print(s)\n" +
            "take(\"plain\")\n");

        var take = Fn(ir, "take");
        Assert.True(WritesADecimal(take));
        Assert.False(StreamsTheParameter(take, "take.s"));
    }

    // A numeric parameter is not text and must not be streamed.
    [Fact]
    public void PrintingAUint8Parameter_StillWritesADecimal()
    {
        var ir = Gen(
            "def take(v: uint8) -> None:\n" +
            "    print(v)\n" +
            "take(7)\n");

        Assert.True(WritesADecimal(Fn(ir, "take")));
    }
}

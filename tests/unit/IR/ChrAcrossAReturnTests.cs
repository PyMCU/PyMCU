using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#436. `chr(n)` prints its character only where print's ladder recognises the
/// `chr(...)` CALL by its syntax. A char IS its byte on this target, so nothing in the value
/// says it is one, and every shape that hides the call behind something -- a name, a
/// `return` -- handed the caller a bare byte that went to the decimal writer: `print(make(66))`
/// wrote 66 and `c = chr(69); print(c)` wrote 69, both with no diagnostic.
///
/// Two halves. A constant `chr(...)` now carries its one-character text on the value, which
/// is what a name binds and what the writer reads. A run-time one cannot, so the scan records
/// which functions hand back a character -- every `return` a `chr(...)`, no path falling off
/// the end -- and a call to one goes to the raw byte writer.
/// </summary>
public class ChrAcrossAReturnTests
{
    private const string Prelude =
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write(c: uint8):\n" +
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

    private static string FlashText(ProgramIR ir, string name) =>
        new string(ir.Functions.SelectMany(f => f.Body).OfType<FlashData>()
            .Single(fd => fd.Name == name).Bytes.TakeWhile(b => b != 0)
            .Select(b => (char)b).ToArray());

    private static List<string> StrWrites(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body)
            .OfType<Copy>()
            .Where(c => c.Dst is Variable { Name: "uart_write_str.s" } && c.Src is FlashStrAddr)
            .Select(c => FlashText(ir, ((FlashStrAddr)c.Src).Name))
            .ToList();

    // main's own calls only: the callee's body calls the byte writer for its own reasons in
    // no test here, and looking at main is what says the CALLER wrote the character.
    private static bool MainWritesABareByte(ProgramIR ir) =>
        ir.Functions.Single(f => f.Name == "main").Body
            .Any(i => i is Call c && c.FunctionName == "uart_write");

    private static bool MainWritesADecimal(ProgramIR ir) =>
        ir.Functions.Single(f => f.Name == "main").Body
            .Any(i => i is Call c && c.FunctionName.Contains("uart_write_decimal"));

    [Fact]
    public void AConstantChrBoundToAName_PrintsItsCharacter()
    {
        var ir = Gen("c = chr(69)\nprint(c)\n");
        Assert.Contains("E", StrWrites(ir));
        Assert.False(MainWritesADecimal(ir));
    }

    [Fact]
    public void AChrReturnedFromAFunction_PrintsItsCharacter()
    {
        var ir = Gen(
            "def make(n: int) -> int:\n" +
            "    return chr(n)\n" +
            "print(make(66))\n");

        Assert.True(MainWritesABareByte(ir));
        Assert.False(MainWritesADecimal(ir));
    }

    [Fact]
    public void AChrReturnedFromAFunctionWithARuntimeArgument_PrintsItsCharacter()
    {
        var ir = Gen(
            "def make(n: int) -> int:\n" +
            "    return chr(n)\n" +
            "k: uint8 = GPIOR0.value\n" +
            "print(make(k))\n");

        Assert.True(MainWritesABareByte(ir));
        Assert.False(MainWritesADecimal(ir));
    }

    // The control: a function whose returns are not all chr() hands back a number on at
    // least one path, so it keeps the decimal writer.
    [Fact]
    public void AFunctionThatOnlySometimesReturnsAChr_StillPrintsANumber()
    {
        var ir = Gen(
            "def mixed(n: int) -> int:\n" +
            "    if n == 0:\n" +
            "        return chr(65)\n" +
            "    return n\n" +
            "k: uint8 = GPIOR0.value\n" +
            "print(mixed(k))\n");

        Assert.True(MainWritesADecimal(ir));
    }

    // The other control: ord() is the inverse and stays a number.
    [Fact]
    public void AnOrdReturnedFromAFunction_StillPrintsANumber()
    {
        var ir = Gen(
            "def code(c: uint8) -> int:\n" +
            "    return ord(\"A\")\n" +
            "print(code(0))\n");

        Assert.True(MainWritesADecimal(ir));
    }
}

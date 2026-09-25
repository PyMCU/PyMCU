using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#393. `hex()`, `bin()` and `str()` fold their constant argument to a string, and
/// print() wrote the interned id of that string as a decimal number instead of its text:
/// `print(hex(255))` sent "257". `str()` came out right for an unrelated reason -- it is one
/// of the shapes print's ladder recognises by SYNTAX (StaticStringOf answers for a `str(...)`
/// call and for nothing else in that family), so the hole was exactly the two intrinsics
/// nobody had added to the ladder.
///
/// The fix is at the writer instead of the ladder: a Constant carrying its compile-time text
/// IS that text, whichever expression shape produced it. Text is set exactly where a Constant
/// stands for text, so `pow()` and `**`, which need no folding to a string, keep printing
/// their numbers.
/// </summary>
public class PrintFoldedStringTests
{
    private const string Prelude =
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

    private static bool WritesADecimal(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body)
            .Any(i => i is Call c && c.FunctionName.Contains("uart_write_decimal"));

    [Fact]
    public void PrintingHexOfAConstant_WritesItsText()
    {
        var ir = Gen("print(hex(255))\n");
        Assert.Contains("0xff", StrWrites(ir));
        Assert.False(WritesADecimal(ir));
    }

    [Fact]
    public void PrintingBinOfAConstant_WritesItsText()
    {
        var ir = Gen("print(bin(10))\n");
        Assert.Contains("0b1010", StrWrites(ir));
        Assert.False(WritesADecimal(ir));
    }

    [Fact]
    public void PrintingStrOfAConstant_WritesItsText()
    {
        var ir = Gen("print(str(42))\n");
        Assert.Contains("42", StrWrites(ir));
    }

    // The control: pow() and ** fold to numbers, not to strings, and a number still goes to
    // the decimal writer. A Constant with no Text is untouched by the string branch.
    [Fact]
    public void PrintingPowOfConstants_StillWritesADecimal()
    {
        var ir = Gen("print(pow(2, 5))\n");
        Assert.True(WritesADecimal(ir));
        Assert.DoesNotContain("32", StrWrites(ir));
    }

    // The one-character seam: hex() of a value whose text is one character long is still text.
    [Fact]
    public void PrintingAOneCharacterFoldedString_WritesItsText()
    {
        var ir = Gen("print(str(7))\n");
        Assert.Contains("7", StrWrites(ir));
        Assert.False(WritesADecimal(ir));
    }
}

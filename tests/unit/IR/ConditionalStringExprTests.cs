using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#378. A conditional EXPRESSION that yields a str printed its interned id:
/// `print("mono" if k == 0 else "none")` wrote 260 and 261 on a real Uno, and binding it to a
/// name first did not help. The same two texts chosen by an if/else STATEMENT printed
/// correctly, because that one goes through the merge in ControlFlow which records the
/// alternatives so a read can dispatch on the stored id. A TernaryExpr reaches no merge: it
/// was lowered as a value, and the value of a string literal on this target is its interned
/// id, so a number arrived at the writer.
///
/// Two halves, one per spelling. The expression written straight into a write is lowered as
/// the condition plus a write_str of a literal on each side, in the shared streaming helper,
/// so uart.write_str and println get it too. Bound to a NAME, the name holds one of two texts
/// at run time, which is exactly what the statement's merge records, so the scan counts both
/// arms as bindings and the store puts the id in the multi-str slot a read dispatches on.
/// </summary>
public class ConditionalStringExprTests
{
    private const string Prelude =
        "from pymcu.chips.atmega328p import GPIOR0\n" +
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
    public void PrintingAConditionalExpression_WritesBothTexts()
    {
        var ir = Gen(
            "k: uint8 = GPIOR0.value\n" +
            "print(\"mono\" if k == 0 else \"none\")\n");

        Assert.Contains("mono", StrWrites(ir));
        Assert.Contains("none", StrWrites(ir));
        Assert.False(WritesADecimal(ir));
    }

    [Fact]
    public void PrintingANameBoundToAConditionalExpression_WritesBothTexts()
    {
        var ir = Gen(
            "k: uint8 = GPIOR0.value\n" +
            "label = \"mono\" if k == 0 else \"none\"\n" +
            "print(label)\n");

        Assert.Contains("mono", StrWrites(ir));
        Assert.Contains("none", StrWrites(ir));
        Assert.False(WritesADecimal(ir));
    }

    // A condition the compiler decides is not a run-time choice: the name holds ONE text and
    // the arm that cannot run is not lowered. This is the fold StaticStringOf already did and
    // must keep doing, or every `"GRB" if bpp == 3 else "GRBW"` in a driver grows a branch.
    [Fact]
    public void AConditionalExpressionWithACompileTimeCondition_WritesOnlyTheChosenText()
    {
        var ir = Gen("po = \"GRB\" if 3 == 3 else \"GRBW\"\nprint(po)\n");

        Assert.Contains("GRB", StrWrites(ir));
        Assert.DoesNotContain("GRBW", StrWrites(ir));
    }

    // A conditional expression over NUMBERS is untouched: it is a value, and printing it is
    // printing a number.
    [Fact]
    public void AConditionalExpressionOverNumbers_StillWritesADecimal()
    {
        var ir = Gen(
            "k: uint8 = GPIOR0.value\n" +
            "print(7 if k == 0 else 9)\n");

        Assert.True(WritesADecimal(ir));
    }
}

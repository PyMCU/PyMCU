using System.Linq;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `text = f"{'literal string'}"` (a fully compile-time f-string bound to a name) printed the
/// interned string id as a bare decimal number -- "257" instead of "literal string" -- a
/// silent wrong answer, not a refusal, in both front ends.
///
/// Root cause: `StaticStringOf`, which VisitAssign's preamble asks "does this name's
/// right-hand side hold text the compiler already knows", had no case for `FStringExpr`. For
/// an f-string whose every part is already constant (TryExpandFStringValue's own IsConstPart
/// says so, which is why it declines the runtime-buffer path), StaticStringOf answered null,
/// so the preamble took the "not a known string" branch and actively CLEARED the name's text
/// record. The generic scalar Copy that followed then stored the f-string's own folded
/// Constant -- which correctly carries the text in .Text, the same as any interned string --
/// but with no strConstantVariables entry, print() had no way to tell it apart from an
/// ordinary integer and streamed the raw id.
///
/// The fix: StaticStringOf gets an FStringExpr case (StaticFStringText) that folds the same
/// text VisitFStringExpr computes, purely (no IR emitted), so the preamble records it like
/// any other compile-time string right-hand side.
/// </summary>
public class FStringConstantLiteralTests
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
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

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
    public void AssigningAFullyConstantFString_PrintsItsTextNotTheInternedId()
    {
        var ir = Gen(
            "text = f\"{'literal string'}\"\n" +
            "print(text)\n");

        Assert.Contains("literal string", StrWrites(ir));
        Assert.False(WritesADecimal(ir));
    }

    [Fact]
    public void AssigningAFullyConstantFString_WithSurroundingText_FoldsTheWholeText()
    {
        var ir = Gen(
            "text = f\"prefix {'literal string'} suffix\"\n" +
            "print(text)\n");

        Assert.Contains("prefix literal string suffix", StrWrites(ir));
        Assert.False(WritesADecimal(ir));
    }

    [Fact]
    public void AssigningAFullyConstantFString_WithAnIntegerLiteralPart_FoldsItsDecimalText()
    {
        var ir = Gen(
            "text = f\"n={42}\"\n" +
            "print(text)\n");

        Assert.Contains("n=42", StrWrites(ir));
        Assert.False(WritesADecimal(ir));
    }

    [Fact]
    public void AssigningAFullyConstantFString_InsideAFunction_AlsoFoldsItsText()
    {
        // The module-level pre-scan (ScanGlobals) only ever registered StringLiteral
        // initializers; this must work from the ordinary per-statement preamble too, not
        // rely on that scan, so a function-local assignment is covered the same way.
        var ir = Gen(
            "def show():\n" +
            "    text = f\"{'literal string'}\"\n" +
            "    print(text)\n" +
            "show()\n");

        Assert.Contains("literal string", StrWrites(ir));
        Assert.False(WritesADecimal(ir));
    }

    [Fact]
    public void PrintingTheFStringDirectly_StillWorks()
    {
        // Not through a name at all -- the regression guard: this path never broke
        // (VisitFStringExpr's own fold already carried .Text correctly), so it must stay
        // working exactly as before.
        var ir = Gen("print(f\"{'literal string'}\")\n");

        Assert.Contains("literal string", StrWrites(ir));
    }
}

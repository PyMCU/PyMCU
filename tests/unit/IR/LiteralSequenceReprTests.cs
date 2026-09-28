using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A list or tuple literal bound to a name lives in fixed per-element slots, and print()
/// used to take it for a scalar (a `0` where CPython writes `[1, 2, 300]`) or for a
/// bytearray (`bytearray(b'\t\n')`). The lowering now spells the literal's repr:
/// brackets or parens, ", " separators, the one-element tuple's trailing comma, and
/// each element's own repr -- True/False for a bool, quotes around a string, the
/// runtime value for a number. `str(seq)` and `f"{seq}"` write the same text, the
/// value forms through the pymcu.strfmt buffer pair an f-string value uses.
/// </summary>
public class LiteralSequenceReprTests
{
    private static ProgramIR Gen(string src, Dictionary<string, ProgramNode>? modules = null) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            modules ?? new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string PrintPrelude =
        "from pymcu.types import uint8, uint16, ptr\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write(c: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n" +
        "GPIOR0: ptr[uint8] = ptr(0x3E)\n";

    // Every text handed to the string writer, in order -- single characters may go
    // through the byte writer instead, so the bracket checks look at both.
    private static List<string> StrWrites(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body)
            .OfType<Copy>()
            .Where(c => c.Dst is Variable { Name: "uart_write_str.s" } && c.Src is FlashStrAddr)
            .Select(c => new string(ir.Functions.SelectMany(f => f.Body).OfType<FlashData>()
                .Single(fd => fd.Name == ((FlashStrAddr)c.Src).Name).Bytes
                .TakeWhile(b => b != 0).Select(b => (char)b).ToArray()))
            .ToList();

    private static List<char> ByteWrites(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body)
            .OfType<Copy>()
            .Where(c => c.Dst is Variable { Name: "uart_write.c" } && c.Src is Constant)
            .Select(c => (char)((Constant)c.Src).Value)
            .ToList();

    private static void AssertWritten(ProgramIR ir, params string[] pieces)
    {
        var texts = StrWrites(ir);
        var bytes = ByteWrites(ir);
        foreach (var piece in pieces)
            Assert.True(
                texts.Any(t => t.Contains(piece))
                || (piece.Length == 1 && bytes.Contains(piece[0])),
                $"no write of {piece} in [{string.Join("|", texts)}] + bytes [{string.Join("", bytes)}]");
    }

    [Fact]
    public void ARuntimeListLiteral_PrintsAsAList()
    {
        var ir = Gen(PrintPrelude +
            "v = [GPIOR0.value + 1, GPIOR0.value + 2, GPIOR0.value + 300]\n" +
            "print(v)\n");
        AssertWritten(ir, "[", "]", ", ");
        // The 300 element is wider than a byte: it gets a 16-bit decimal write, and
        // the value is never the list's base address.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Call>(),
            c => c.FunctionName == "uart_write_decimal_i16" || c.FunctionName == "uart_write_decimal_u16");
    }

    [Fact]
    public void ARuntimeTupleLiteral_PrintsAsATuple()
    {
        var ir = Gen(PrintPrelude +
            "t = (GPIOR0.value + 3, GPIOR0.value + 4)\n" +
            "print(t)\n");
        AssertWritten(ir, "(", ")", ", ");
    }

    [Fact]
    public void AOneElementTuple_PrintsTheTrailingComma()
    {
        var ir = Gen(PrintPrelude +
            "one = (GPIOR0.value + 5,)\n" +
            "print(one)\n");
        AssertWritten(ir, "(", ",", ")");
    }

    [Fact]
    public void AConstantListLiteral_PrintsWithListBrackets()
    {
        // An all-constant literal resolves as a module constant sequence too; that path
        // spelled every sequence with parens, so `xs = [1, 2]` printed `(1, 2)`.
        var ir = Gen(PrintPrelude +
            "xs = [1, 2]\n" +
            "print(xs)\n");
        AssertWritten(ir, "[", "]");
        var texts = StrWrites(ir);
        Assert.DoesNotContain(texts, t => t == "(");
    }

    [Fact]
    public void BoolElements_PrintAsWords()
    {
        var ir = Gen(PrintPrelude +
            "b = [GPIOR0.value == 0, GPIOR0.value != 0]\n" +
            "print(b)\n");
        AssertWritten(ir, "True", "False");
    }

    [Fact]
    public void StringElements_PrintQuoted()
    {
        var ir = Gen(PrintPrelude +
            "s = [\"a\", \"b\"]\n" +
            "print(s)\n");
        AssertWritten(ir, "'a'", "'b'");
    }

    [Fact]
    public void AnFStringInterpolation_PrintsTheRepr()
    {
        var ir = Gen(PrintPrelude +
            "v = [GPIOR0.value + 1, GPIOR0.value + 2]\n" +
            "print(f\"{v}\")\n");
        AssertWritten(ir, "[", "]");
    }

    [Fact]
    public void StrOfASequence_PrintsTheRepr()
    {
        // print(str(v)) streams the repr straight to the wire -- no buffer involved.
        var ir = Gen(PrintPrelude +
            "v = [GPIOR0.value + 1, GPIOR0.value + 2]\n" +
            "print(str(v))\n");
        AssertWritten(ir, "[", "]");
    }

    [Fact]
    public void StrOfASequenceBoundToAName_MaterializesABuffer()
    {
        // `x = str(v)` builds the repr text in a fixed buffer through pymcu.strfmt,
        // the same runtime-string pair an f-string-as-value gets.
        var strfmt = new Parser(new Lexer(
            "from pymcu.types import uint8, uint16\n" +
            "def _fs_text(buf: bytearray, pos: uint16, s: const[str]) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_u32(buf: bytearray, pos: uint16, v: uint8) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_i32(buf: bytearray, pos: uint16, v: uint8) -> uint16:\n" +
            "    return pos\n").Tokenize()).ParseProgram();
        var ir = Gen(PrintPrelude +
            "import pymcu.strfmt as _pymcu_strfmt\n" +
            "v = [GPIOR0.value + 1, GPIOR0.value + 2]\n" +
            "x = str(v)\n" +
            "print(x)\n",
            new Dictionary<string, ProgramNode> { ["pymcu.strfmt"] = strfmt });
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.Contains(body.OfType<Call>(), c => c.FunctionName.Contains("_fs_text"));
        Assert.Contains(body.OfType<Call>(),
            c => c.FunctionName.Contains("_fs_i32") || c.FunctionName.Contains("_fs_u32"));
    }

    [Fact]
    public void AnInlineReturnOfALiteralSequence_PrintsAsAList()
    {
        var ir = Gen(PrintPrelude +
            "from pymcu.types import inline\n" +
            "@inline\n" +
            "def fi(k):\n" +
            "    w = [k, k + 1]\n" +
            "    return w\n" +
            "print(fi(GPIOR0.value + 3))\n");
        AssertWritten(ir, "[", "]");
    }
}

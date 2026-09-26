using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A list handed back by a function or method compiled as a real subroutine, with no
/// `-> list[T]` on its def, reaches the caller as a list: TypeInference reads the local
/// every `return` names -- its `list[T]` declaration, or the elements of the literal (or of
/// the appends to `[]`) it was bound to -- and gives the def that return type, exactly as if
/// it were written. Before, a caller compiled ahead of the body (every module-level caller)
/// lowered the call as a void one and `print(g())` printed a stale register.
/// </summary>
public class InferredListReturnTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Prelude =
        "from pymcu.types import uint8, uint16, ptr, inline\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write(c: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n" +
        "GPIOR0: ptr[uint8] = ptr(0x3E)\n";

    // The bracket goes out through the string writer or, one character long, through the
    // byte writer; the decimal writer alone is the pointer being printed.
    private static bool PrintsABracket(ProgramIR ir)
    {
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        var flash = body.OfType<FlashData>().ToDictionary(fd => fd.Name,
            fd => new string(fd.Bytes.TakeWhile(b => b != 0).Select(b => (char)b).ToArray()));
        return body.OfType<Copy>().Any(c =>
            (c.Dst is Variable { Name: "uart_write_str.s" } && c.Src is FlashStrAddr fs
                && flash.TryGetValue(fs.Name, out var t) && t.Contains('['))
            || (c.Dst is Variable { Name: "uart_write.c" } && c.Src is Constant { Value: '[' }));
    }

    private const string Func =
        "def g(k: uint8, w: uint8):\n" +
        "    v = [k, k + 1, k + 2]\n" +
        "    if w:\n" +
        "        v.append(k + 3)\n" +
        "    return v\n";

    [Fact]
    public void AFunctionsLiteralReturn_PrintsAsAList()
    {
        Assert.True(PrintsABracket(Gen(Prelude + Func + "print(g(GPIOR0.value, 1))\n")));
    }

    [Fact]
    public void AFunctionsAppendedEmptyListReturn_PrintsAsAList()
    {
        Assert.True(PrintsABracket(Gen(Prelude +
            "def g(k: uint8):\n" +
            "    v = []\n" +
            "    v.append(k)\n" +
            "    v.append(k + 1)\n" +
            "    return v\n" +
            "print(g(GPIOR0.value))\n")));
    }

    [Fact]
    public void AListReturnBoundToAName_HasALength()
    {
        var ir = Gen(Prelude + Func +
            "v = g(GPIOR0.value, 1)\n" +
            "n: uint8 = len(v)\n");
        Assert.Contains(ir.Functions.Single(f => f.Name == "main").Body,
            i => i is LoadIndirect { SrcPtr: Variable { Name: "v", Type: DataType.GC_REF } });
    }

    private const string Cls =
        "class P:\n" +
        "    def __init__(self):\n" +
        "        self.w = GPIOR0.value\n" +
        "    def get(self, k: uint8):\n" +
        "        v = [k, k + 1, k + 2]\n" +
        "        if self.w:\n" +
        "            v.append(k + 3)\n" +
        "        return v\n" +
        "    def __getitem__(self, k: uint8):\n" +
        "        return self.get(k)\n" +
        "p = P()\n";

    [Fact]
    public void AMethodsLiteralReturn_PrintsAsAList()
    {
        Assert.True(PrintsABracket(Gen(Prelude + Cls + "print(p.get(GPIOR0.value))\n")));
    }

    [Fact]
    public void AMethodsLiteralReturnThroughGetitem_PrintsAsAList()
    {
        Assert.True(PrintsABracket(Gen(Prelude + Cls + "print(p[GPIOR0.value])\n")));
    }

    [Fact]
    public void AReturnedListWiderThanTheDeclaration_IsRefused()
    {
        // A caller compiled before the body reads the elements at the declared width.
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(Prelude +
            "def g(k: uint8) -> list[uint16]:\n" +
            "    v: list[uint8] = [k]\n" +
            "    return v\n" +
            "print(g(GPIOR0.value))\n"));
        Assert.Contains("list[uint16]", ex.Message);
    }
}

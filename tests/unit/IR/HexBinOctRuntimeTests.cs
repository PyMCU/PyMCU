using System.Linq;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// P2 AVR gaps bundle, item 1: `hex(x)` / `bin(x)` / `oct(x)` used to refuse any argument
/// that was not a compile-time constant ("hex() argument must be a compile-time constant
/// integer"), and `oct()` was not implemented at all. Two bugs found while fixing that:
///
///  - A compile-time NEGATIVE constant silently printed the wrong answer, not a refusal:
///    `hex(-1)` spelled "0xffffffff" (C#'s `(-1).ToString("x")` on the raw 32-bit two's
///    complement pattern) instead of CPython's "-0x1". Every compile-time negative argument
///    to hex()/bin() was affected.
///  - `s = hex(x)` on a RUN-TIME x (after the fix below made it compile at all) fell to the
///    generic scalar Copy path and took the digit buffer's first byte as a plain number:
///    `s = hex(x + 200); print(s)` printed "255" instead of "0xc8".
///
/// The fix: a run-time argument builds its base spelling into a buffer through
/// pymcu.strfmt (the same machinery an f-string value or `str(float)` uses), with the sign
/// (if any) written explicitly BEFORE the base prefix so `hex(-1)` reads "-0x1", never
/// "0x-1". A compile-time constant keeps interning its spelling as a flash string (zero
/// run-time cost), now computed with CPython's actual sign placement.
/// </summary>
public class HexBinOctRuntimeTests
{
    private const string Prelude =
        "from pymcu.types import uint8, uint16, int16, ptr\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write(c: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n" +
        "def uart_write_decimal_i16(v: int16):\n" +
        "    pass\n" +
        "GPIOR0: ptr[uint8] = ptr(0x3E)\n";

    private static readonly Dictionary<string, ProgramNode> StrfmtModule = new()
    {
        ["pymcu.strfmt"] = new Parser(new Lexer(
            "from pymcu.types import uint8, uint16, uint32, int32\n" +
            "def _fs_text(buf: bytearray, pos: uint16, s: const[str]) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_u32(buf: bytearray, pos: uint16, v: uint32) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_i32(buf: bytearray, pos: uint16, v: int32) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_fmt(buf: bytearray, pos: uint16, value: int32, base: uint8, width: uint8, flags: uint8) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_frepr(buf: bytearray, pos: uint16, value: float) -> uint16:\n" +
            "    return pos\n").Tokenize()).ParseProgram(),
    };

    private static ProgramIR Gen(string src, Dictionary<string, ProgramNode>? modules = null) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            modules ?? new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

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

    private static List<Call> Calls(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Call>().ToList();

    // --- Compile-time constant: negative-value sign placement (the silent miscompile) ---

    [Fact]
    public void HexOfANegativeConstant_SpellsSignBeforePrefix()
    {
        var ir = Gen("print(hex(-1))\n");
        Assert.Contains("-0x1", StrWrites(ir));
        Assert.DoesNotContain("0xffffffff", StrWrites(ir));
    }

    [Fact]
    public void BinOfANegativeConstant_SpellsSignBeforePrefix()
    {
        var ir = Gen("print(bin(-2))\n");
        Assert.Contains("-0b10", StrWrites(ir));
    }

    [Fact]
    public void OctOfANegativeConstant_SpellsSignBeforePrefix()
    {
        var ir = Gen("print(oct(-8))\n");
        Assert.Contains("-0o10", StrWrites(ir));
    }

    [Fact]
    public void HexOfAPositiveConstant_IsUnchanged()
    {
        var ir = Gen("print(hex(255))\n");
        Assert.Contains("0xff", StrWrites(ir));
    }

    // --- oct(): previously unimplemented ---

    [Fact]
    public void OctOfAConstant_WritesItsText()
    {
        var ir = Gen("print(oct(8))\n");
        Assert.Contains("0o10", StrWrites(ir));
    }

    // --- Run-time argument: used to refuse outright ---

    [Fact]
    public void PrintingHexOfARuntimeValue_CallsFsFmt_NotTheDecimalWriter()
    {
        var ir = Gen(
            "import pymcu.strfmt as _pymcu_strfmt\n" +
            "def show(x: uint8):\n" +
            "    print(hex(x))\n" +
            "show(GPIOR0.value)\n",
            StrfmtModule);
        var calls = Calls(ir);
        Assert.Contains(calls, c => c.FunctionName.Contains("_fs_fmt"));
        Assert.DoesNotContain(calls, c => c.FunctionName.Contains("uart_write_decimal"));
    }

    [Fact]
    public void PrintingOctOfARuntimeValue_CallsFsFmt()
    {
        var ir = Gen(
            "import pymcu.strfmt as _pymcu_strfmt\n" +
            "def show(x: uint8):\n" +
            "    print(oct(x))\n" +
            "show(GPIOR0.value)\n",
            StrfmtModule);
        Assert.Contains(Calls(ir), c => c.FunctionName.Contains("_fs_fmt"));
    }

    [Fact]
    public void PrintingBinOfARuntimeValue_CallsFsFmt()
    {
        var ir = Gen(
            "import pymcu.strfmt as _pymcu_strfmt\n" +
            "def show(x: uint8):\n" +
            "    print(bin(x))\n" +
            "show(GPIOR0.value)\n",
            StrfmtModule);
        Assert.Contains(Calls(ir), c => c.FunctionName.Contains("_fs_fmt"));
    }

    // --- The assignment silent-miscompile: `s = hex(x)` on a run-time x ---

    [Fact]
    public void AssigningHexOfARuntimeValue_BuildsABuffer_NotAScalarCopy()
    {
        var ir = Gen(
            "import pymcu.strfmt as _pymcu_strfmt\n" +
            "def show(x: uint8):\n" +
            "    s = hex(x)\n" +
            "    print(s)\n" +
            "show(GPIOR0.value)\n",
            StrfmtModule);
        var calls = Calls(ir);
        // The fix: the assignment routes through the same _fs_text/_fs_fmt buffer
        // builder print(hex(x)) uses, and print(s) streams that buffer rather than
        // reading it as a one-byte scalar (uart_write_decimal_u8 on the buffer's
        // first byte, which is what silently printed "255" for hex(200)).
        Assert.Contains(calls, c => c.FunctionName.Contains("_fs_fmt"));
        Assert.DoesNotContain(calls, c => c.FunctionName.Contains("uart_write_decimal"));
    }

    [Fact]
    public void AnnotatedAssigningHexOfARuntimeValue_BuildsABuffer_NotAScalarCopy()
    {
        // The unannotated form above (`s = hex(x)`) was fixed for #p2avr-1; the ANNOTATED
        // form (`s: str = hex(x)`) is the same call in a VarDecl instead of a plain Assign
        // and was not wired to the same buffer builder, so it still fell to the generic
        // scalar Copy path and took the digit buffer's first byte as a number.
        var ir = Gen(
            "import pymcu.strfmt as _pymcu_strfmt\n" +
            "def show(x: uint8):\n" +
            "    s: str = hex(x)\n" +
            "    print(s)\n" +
            "show(GPIOR0.value)\n",
            StrfmtModule);
        var calls = Calls(ir);
        Assert.Contains(calls, c => c.FunctionName.Contains("_fs_fmt"));
        Assert.DoesNotContain(calls, c => c.FunctionName.Contains("uart_write_decimal"));
    }

    // --- RFC 0014 decision 4: a user's own def hex/bin/oct/round shadows the builtin ---

    [Fact]
    public void OwnHexDefinition_ShadowsTheBuiltin_UnannotatedAssign()
    {
        // Without the shadow check, `s = hex(v)` matched the call shape directly (ahead of
        // the ordinary callee dispatch) and bound the BUILTIN's digit buffer to s -- silently
        // ignoring the user's own function, no refusal, not even a diagnostic.
        var ir = Gen(
            "def hex(x: uint8) -> uint8:\n" +
            "    return x\n" +
            "def show(x: uint8):\n" +
            "    s = hex(x)\n" +
            "    print(s)\n" +
            "show(GPIOR0.value)\n");
        var calls = Calls(ir);
        Assert.DoesNotContain(calls, c => c.FunctionName.Contains("_fs_fmt"));
        Assert.Contains(calls, c => c.FunctionName.Contains("hex"));
    }

    [Fact]
    public void OwnHexDefinition_ShadowsTheBuiltin_AnnotatedAssign()
    {
        var ir = Gen(
            "def hex(x: uint8) -> uint8:\n" +
            "    return x\n" +
            "def show(x: uint8):\n" +
            "    s: str = hex(x)\n" +
            "    print(s)\n" +
            "show(GPIOR0.value)\n");
        var calls = Calls(ir);
        Assert.DoesNotContain(calls, c => c.FunctionName.Contains("_fs_fmt"));
        Assert.Contains(calls, c => c.FunctionName.Contains("hex"));
    }

    [Fact]
    public void OwnHexDefinition_ShadowsTheBuiltin_DirectCall()
    {
        var ir = Gen(
            "def hex(x: uint8) -> uint8:\n" +
            "    return x\n" +
            "print(hex(GPIOR0.value))\n");
        Assert.DoesNotContain(Calls(ir), c => c.FunctionName.Contains("_fs_fmt"));
    }

    // --- A signed argument's magnitude, not its two's-complement bit pattern ---

    [Fact]
    public void HexOfAnInt16ReturningCall_TakesTheSignBranch()
    {
        // LooksSigned is a syntax check with no CallExpr case, so a signed-typed
        // call result took the unsigned lane and hex(minus_one()) spelled
        // "0xffffffff". The Val's own type now decides too: the sign branch is
        // the `v < 0` conditional (jumped past when v >= 0) plus the `0 - v`
        // magnitude write, neither of which exists on the unsigned path.
        var ir = Gen(
            "import pymcu.strfmt as _pymcu_strfmt\n" +
            "def minus_one() -> int16:\n" +
            "    return -1\n" +
            "print(hex(minus_one()))\n",
            StrfmtModule);
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.Contains(body, i => i is JumpIfGreaterOrEqual { Src2: Constant { Value: 0 } });
        Assert.Contains(body, i => i is Binary { Op: PyMCU.IR.BinaryOp.Sub, Src1: Constant { Value: 0 } });
        Assert.Contains(body.OfType<FlashData>(), fd =>
            new string(fd.Bytes.TakeWhile(b => b != 0).Select(b => (char)b).ToArray()) == "-");
    }

    [Fact]
    public void HexOfARuntimeNegativeInt16_WritesSignThenMagnitude()
    {
        // int16 x = -1 - GPIOR0.value (GPIOR0 poisons to 0 in the runner, but the value is
        // not compile-time foldable either way): hex(x) must branch on sign at run time
        // rather than ever intern a single folded spelling, so this only has to compile and
        // route through the runtime buffer builder -- the actual byte-for-byte sign/prefix
        // ordering is covered end-to-end in the pymcu-avr oracle suite.
        var ir = Gen(
            "import pymcu.strfmt as _pymcu_strfmt\n" +
            "def show(x: int16):\n" +
            "    print(hex(x))\n" +
            "show(0 - 1 - int16(GPIOR0.value))\n",
            StrfmtModule);
        Assert.Contains(Calls(ir), c => c.FunctionName.Contains("_fs_fmt"));
    }
}

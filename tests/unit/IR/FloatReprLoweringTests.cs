using System.Linq;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An unformatted float in an f-string value (`s = f"{x}"`), `str(x)` and `repr(x)` on a
/// float all used to fall through to the integer decimal writer, which marshalled the
/// IEEE-754 bit pattern as if it were an integer -- `f"{0.1}"` spelled "1036831949". They
/// now route through strfmt's `_fs_frepr`, the buffer-side twin of `uart_write_float`
/// (MicroPython's 7-significant-digit float32 policy; see `_f32_repr` in
/// lib/src/pymcu/hal/uart_text.py). `repr()` on anything else stays refused: PyMCU has no
/// run-time object model to describe it.
/// </summary>
public class FloatReprLoweringTests
{
    private static ProgramIR Gen(string src, Dictionary<string, ProgramNode>? modules = null) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            modules ?? new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Prelude =
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

    private static readonly Dictionary<string, ProgramNode> StrfmtModule = new()
    {
        ["pymcu.strfmt"] = new Parser(new Lexer(
            "from pymcu.types import uint8, uint16\n" +
            "def _fs_text(buf: bytearray, pos: uint16, s: const[str]) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_u32(buf: bytearray, pos: uint16, v: uint8) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_i32(buf: bytearray, pos: uint16, v: uint8) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_frepr(buf: bytearray, pos: uint16, value: float) -> uint16:\n" +
            "    return pos\n").Tokenize()).ParseProgram(),
    };

    private static List<Call> Calls(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Call>().ToList();

    [Fact]
    public void AnFStringValueOnAFloat_CallsFrepr_NotTheIntegerWriter()
    {
        var ir = Gen(Prelude +
            "import pymcu.strfmt as _pymcu_strfmt\n" +
            "def show(x: float):\n" +
            "    s = f\"{x}\"\n" +
            "    print(s)\n" +
            "show(GPIOR0.value / 7.0)\n",
            StrfmtModule);
        var calls = Calls(ir);
        Assert.Contains(calls, c => c.FunctionName.Contains("_fs_frepr"));
        Assert.DoesNotContain(calls, c => c.FunctionName.Contains("_fs_u32") || c.FunctionName.Contains("_fs_i32"));
    }

    [Fact]
    public void StrOfAFloat_CallsFrepr()
    {
        var ir = Gen(Prelude +
            "import pymcu.strfmt as _pymcu_strfmt\n" +
            "def show(x: float):\n" +
            "    s = str(x)\n" +
            "    print(s)\n" +
            "show(GPIOR0.value / 7.0)\n",
            StrfmtModule);
        Assert.Contains(Calls(ir), c => c.FunctionName.Contains("_fs_frepr"));
    }

    [Fact]
    public void ReprOfAFloat_CallsFrepr_LikeStr()
    {
        var ir = Gen(Prelude +
            "import pymcu.strfmt as _pymcu_strfmt\n" +
            "def show(x: float):\n" +
            "    s = repr(x)\n" +
            "    print(s)\n" +
            "show(GPIOR0.value / 7.0)\n",
            StrfmtModule);
        Assert.Contains(Calls(ir), c => c.FunctionName.Contains("_fs_frepr"));
    }

    [Fact]
    public void ReprOfANonFloat_StaysRefused()
    {
        // repr() has no run-time object model behind it except the float shortcut;
        // an int (or anything else) must still refuse at compile time, not silently
        // pick a wrong lowering.
        Assert.ThrowsAny<Exception>(() => Gen(Prelude +
            "def show(x: uint8):\n" +
            "    s = repr(x)\n" +
            "    uart_write_str(s)\n" +
            "show(GPIOR0.value)\n"));
    }
}

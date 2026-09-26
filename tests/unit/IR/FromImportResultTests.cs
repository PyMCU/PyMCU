using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The result of a from-imported function, bound to a name.
///
/// The module-level scan tags `x = C()` with C's class, and read any callee that
/// resolves to a mangled name as a class re-exported through a facade. A from-imported
/// FUNCTION resolves the same way (mylib_plain), so `w = plain(...)` was tagged an
/// instance of `plain`: `print(w)` was refused as "cannot interpolate 'w', an instance
/// of 'plain'" -- MicroPython's own idiom, `from utime import ticks_diff` then
/// `elapsed = ticks_diff(...)` and `print(elapsed)`.
///
/// Behind the refusal sat a silent fault: a comparison folded on the tag, so
/// `restore_interrupts(s1)` took `state != 0` for true and a nested critical section
/// written with `from pymcu.hal.irq import ...` re-enabled interrupts with the outer
/// section still open.
/// </summary>
public class FromImportResultTests
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

    private const string MyLib =
        "def plain(a: uint8, b: uint8) -> uint8:\n" +
        "    return a + b\n";

    private static ProgramIR Gen(string src, bool withModule = false) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            withModule
                ? new Dictionary<string, ProgramNode>
                {
                    ["mylib"] = new Parser(new Lexer(MyLib).Tokenize()).ParseProgram(),
                }
                : new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static Call CallTo(ProgramIR ir, string fn) =>
        ir.Functions.Single(f => f.Name == "main").Body.OfType<Call>()
            .Single(c => c.FunctionName == fn);

    // DISCRIMINATING. The reported program, refused before the fix.
    [Fact]
    public void AFromImportedFunctionResultPrints()
    {
        var ir = Gen(
            "from mylib import plain\n" +
            "w = plain(GPIOR0.value + 100, 100)\n" +
            "print(w)\n", withModule: true);

        Assert.IsNotType<NoneVal>(CallTo(ir, "mylib_plain").Dst);
    }

    private const string IrqLib =
        "from pymcu.types import uint8, ptr, inline, asm\n" +
        "@inline\n" +
        "def save() -> uint8:\n" +
        "    sreg: ptr[uint8] = ptr(0x5F)\n" +
        "    state: uint8 = sreg.value & 0x80\n" +
        "    asm(\"CLI\")\n" +
        "    return state\n" +
        "@inline\n" +
        "def restore(state: uint8):\n" +
        "    if state != 0:\n" +
        "        asm(\"SEI\")\n" +
        "    else:\n" +
        "        asm(\"CLI\")\n";

    // DISCRIMINATING, and the SILENT face of the same tag. The HAL's nested critical section
    // written with `from pymcu.hal.irq import ...`: the saved state was tagged as an instance,
    // `state != 0` in restore folded to true, and the IR came out CLI, CLI, SEI, SEI -- the
    // inner restore turned interrupts back on with the outer section still open. Kept, the
    // test reads both arms of each restore: two SEI and four CLI (two from the saves).
    [Fact]
    public void AFromImportedSavedStateKeepsTheRestoreTest()
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(
                "from myirq import save, restore\n" +
                "s1 = save()\n" +
                "s2 = save()\n" +
                "restore(s2)\n" +
                "restore(s1)\n").Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>
            {
                ["myirq"] = new Parser(new Lexer(IrqLib).Tokenize()).ParseProgram(),
            },
            new DeviceConfig { Arch = "avr" });

        var asm = ir.Functions.Single(f => f.Name == "main").Body.OfType<InlineAsm>()
            .Select(a => a.Code.Trim()).ToList();
        Assert.Equal(2, asm.Count(c => c == "SEI"));
        Assert.Equal(4, asm.Count(c => c == "CLI"));
    }
}

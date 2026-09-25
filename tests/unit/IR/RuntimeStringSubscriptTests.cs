using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#399. `s[i]` on a RUNTIME string printed the character's code instead of the
/// character: `s = f"t={x:04d}"` then `print(s[2])` wrote 48 where CPython writes 0.
///
/// Python has no char type -- `"abcd"[0]` is the one-character string "a" -- and print
/// already knew that for a subscript of a COMPILE-TIME string. That lookup is the only one
/// that ever ran, and a runtime string has no text to look it up in, so the subscript fell
/// through to the numeric writer. On this target the one-character string IS the byte, so
/// the raw byte writer is the whole answer, for a constant index and a run-time one alike.
/// </summary>
public class RuntimeStringSubscriptTests
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

    private static bool WritesABareByte(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body)
            .Any(i => i is Call c && c.FunctionName == "uart_write");

    private static bool WritesADecimal(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body)
            .Any(i => i is Call c && c.FunctionName.Contains("uart_write_decimal"));

    // A runtime string this single-source harness can build: the canonical bytes-to-string
    // idiom, which registers the same NUL-capped buffer an f-string value would. An f-string
    // needs the strfmt helpers the driver injects, and the oracle probe covers that spelling.
    private const string RuntimeStr =
        "buf = bytearray(4)\n" +
        "buf[0] = GPIOR0.value\n" +
        "s = \"\".join([chr(b) for b in buf])\n";

    [Fact]
    public void AConstantIndexOfARuntimeString_WritesTheCharacter()
    {
        var ir = Gen(RuntimeStr + "print(s[2])\n");
        Assert.True(WritesABareByte(ir));
        Assert.False(WritesADecimal(ir));
    }

    [Fact]
    public void ARuntimeIndexOfARuntimeString_WritesTheCharacter()
    {
        var ir = Gen(RuntimeStr + "i: uint8 = GPIOR0.value\nprint(s[i])\n");
        Assert.True(WritesABareByte(ir));
        Assert.False(WritesADecimal(ir));
    }

    // The control: a bytearray element is a NUMBER in Python too, so it keeps the decimal
    // writer. Only a string's subscript is a one-character string.
    [Fact]
    public void AnIndexOfABytearray_StillWritesTheNumber()
    {
        var ir = Gen(
            "b = bytearray(2)\n" +
            "b[0] = 65\n" +
            "print(b[0])\n");

        Assert.True(WritesADecimal(ir));
    }
}

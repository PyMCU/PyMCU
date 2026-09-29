using System.Linq;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// TryEmitStreamMethodFString (uart.println(f"...")/write_str(f"...")) and
/// TryEmitLcdMethodFString (lcd.print_str(f"...")) used to decide "is this instance the pymcu
/// stdlib class" with `cls.EndsWith("UART")` / `cls.EndsWith("LCD")`. A user class whose name
/// merely ends the same way -- `class BleUART`, `class SoftUART`, `class MyLCD` -- matched too,
/// so `ble.println(f"hi {x}")` silently skipped the user's own println() and streamed straight
/// to the console UART instead: no compile error, no runtime effect from the method body, a
/// wire nothing asked for.
///
/// A user-defined method has no support for an f-string argument in general (only the
/// assignment-to-a-variable and streaming positions do) -- so once the shortcut no longer
/// claims a user class by name alone, the call correctly falls through to normal dispatch,
/// which refuses the same f-string-in-an-unsupported-position with a diagnostic naming the
/// real cause, instead of silently doing something else.
/// </summary>
public class StreamMethodShortcutClassIdentityTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new System.Collections.Generic.Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Preamble =
        "from pymcu.types import uint8\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "seed: uint8 = GPIOR0.value\n";

    [Theory]
    [InlineData("UART")]
    [InlineData("BleUART")]
    [InlineData("SoftUART")]
    public void AUserClassEndingInUART_IsNotTreatedAsTheStdlibUART(string className)
    {
        var src = Preamble +
            $"class {className}:\n" +
            "    def __init__(self):\n" +
            "        self.pos: uint8 = 0\n" +
            "    def println(self, s):\n" +
            "        self.pos = self.pos + 1\n" +
            "u = " + className + "()\n" +
            "u.println(f\"hi {seed}\")\n";

        // The shortcut used to accept this silently. With the class-identity fix it falls
        // through to normal method dispatch, which refuses an f-string argument here --
        // proof the shortcut did NOT fire and swallow the call.
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(src));
        Assert.Contains("f-string", ex.Message);
    }

    [Theory]
    [InlineData("LCD")]
    [InlineData("MyLCD")]
    public void AUserClassEndingInLCD_IsNotTreatedAsTheStdlibLCD(string className)
    {
        var src = Preamble +
            $"class {className}:\n" +
            "    def __init__(self):\n" +
            "        self.pos: uint8 = 0\n" +
            "    def print_str(self, s):\n" +
            "        self.pos = self.pos + 1\n" +
            "u = " + className + "()\n" +
            "u.print_str(f\"hi {seed}\")\n";

        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(src));
        Assert.Contains("f-string", ex.Message);
    }
}

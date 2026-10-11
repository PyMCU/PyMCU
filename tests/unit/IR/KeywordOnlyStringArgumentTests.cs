using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A string argument passed by KEYWORD to a callee that rebinds that parameter stayed a
/// compile-time string only when it was a literal. Adafruit_CircuitPython_NeoPixel's
/// `NeoPixel(pin, n, pixel_order=ORDER)`, with `ORDER = neopixel.GRB` in the user's file
/// (here `ORDER = "GR" + "B"`, a string that is a constant without being a literal),
/// died in adafruit_pixelbuf with "len() argument must be a fixed-size array or list
/// literal" at `len(byteorder)`.
///
/// The positional binder asks a variable for its text. The keyword binder did not: a name
/// bound to a string folded from an expression reached it as the interned id with no text,
/// and a name bound to an imported module's string as a variable holding that id. When the
/// callee writes the parameter (`if not pixel_order: pixel_order = GRB`) the parameter became
/// an integer, and `len()` of it was refused. A literal kept its text in a side table, which
/// is why `pixel_order="GRB"` always worked. A keyword-only parameter (`*, pixel_order=None`)
/// always takes the keyword path.
/// </summary>
public class KeywordOnlyStringArgumentTests
{
    private static ProgramIR Generate(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Classes =
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n" +
        "GRB = \"GRB\"\n" +
        "\n" +
        "class P:\n" +
        "    def __init__(self, size, *, byteorder: str = \"BGR\"):\n" +
        "        bpp, t = self.parse(byteorder)\n" +
        "        self.bpp = bpp\n" +
        "        self.t = t\n" +
        "\n" +
        "    @staticmethod\n" +
        "    def parse(byteorder: str):\n" +
        "        bpp = len(byteorder)\n" +
        "        r = byteorder.index(\"R\")\n" +
        "        return bpp, r\n" +
        "\n" +
        "class N(P):\n" +
        "    def __init__(self, n, *, pixel_order: str = None):\n" +
        "        if not pixel_order:\n" +
        "            pixel_order = GRB\n" +
        "        super().__init__(n, byteorder=pixel_order)\n" +
        "\n" +
        "ORDER = \"GR\" + \"B\"\n" +
        "\n" +
        "def main():\n";

    [Theory]
    [InlineData("    p = N(8, pixel_order=ORDER)\n    GPIOR0.value = p.bpp\n")]
    [InlineData("    p = N(8, pixel_order=GRB)\n    GPIOR0.value = p.bpp\n")]
    [InlineData("    p = N(8, pixel_order=\"GRB\")\n    GPIOR0.value = p.bpp\n")]
    public void AKeywordStringArgumentTheCalleeRebindsStaysAString(string body)
    {
        Action gen = () => Generate(Classes + body);

        gen.Should().NotThrow(
            because: "the argument is a known string, and len() of it is 3 at compile time");
    }
}

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#322. An unannotated field laid out as a byte truncated whatever was stored in it,
/// with nothing said: the width came from the field, not from the value, so
/// `self._period = uint16(1000000 // uint32(hz))` read back as 20000 &amp; 0xFF. A servo driven
/// from such a field was asked for 1000 us and put 689 us on the pin.
///
/// The width now comes from the widest value the constructor assigns. An explicit
/// `self.x: T = ...` still wins, because that is the reader's own declaration.
/// </summary>
public class FieldWidthFromValueTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Prelude =
        "from pymcu.types import uint8, uint16, uint32, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n";

    /// <summary>The type the store to `<c>s._&lt;field&gt;</c>` is emitted with.</summary>
    private static DataType FieldStoreType(ProgramIR ir, string field)
    {
        var main = ir.Functions.Last(f => f.Name == "main");
        var copy = main.Body.OfType<Copy>()
            .First(c => c.Dst is Variable v && v.Name.EndsWith("_" + field, StringComparison.Ordinal));
        return ((Variable)copy.Dst).Type;
    }

    [Fact]
    public void AConversionCall_GivesTheFieldItsWidth()
    {
        var ir = Gen(Prelude +
            "class Servo:\n" +
            "    @inline\n" +
            "    def __init__(self, hz: uint8):\n" +
            "        self._period = uint16(1000000 // uint32(hz))\n" +
            "\n" +
            "def main():\n" +
            "    s = Servo(50)\n" +
            "    GPIOR0.value = s._period\n");
        Assert.Equal(DataType.UINT16, FieldStoreType(ir, "period"));
    }

    [Fact]
    public void ALiteralTooWideForAByte_GivesTheFieldItsWidth()
    {
        var ir = Gen(Prelude +
            "class Box:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        self._big = 70000\n" +
            "\n" +
            "def main():\n" +
            "    b = Box()\n" +
            "    GPIOR0.value = b._big\n");
        Assert.Equal(DataType.UINT32, FieldStoreType(ir, "big"));
    }

    [Fact]
    public void AnExplicitAnnotationStillWins()
    {
        var ir = Gen(Prelude +
            "class Servo:\n" +
            "    @inline\n" +
            "    def __init__(self, hz: uint8):\n" +
            "        self._period: uint32 = uint16(1000000 // uint32(hz))\n" +
            "\n" +
            "def main():\n" +
            "    s = Servo(50)\n" +
            "    GPIOR0.value = s._period\n");
        Assert.Equal(DataType.UINT32, FieldStoreType(ir, "period"));
    }

    [Fact]
    public void AByteWideValueLeavesTheFieldAByte()
    {
        var ir = Gen(Prelude +
            "class Box:\n" +
            "    @inline\n" +
            "    def __init__(self, n: uint8):\n" +
            "        self._n = n + 1\n" +
            "\n" +
            "def main():\n" +
            "    b = Box(3)\n" +
            "    GPIOR0.value = b._n\n");
        Assert.Equal(DataType.UINT8, FieldStoreType(ir, "n"));
    }
}

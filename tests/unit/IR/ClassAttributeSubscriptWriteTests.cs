using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `Class.attr[k] = v` used to be claimed unconditionally by the compile-time class-dict
/// accumulator, which exists for the Adafruit CV pattern (`cls.string = {}` followed by
/// `cls.string[code] = text`). The receiver naming a class was the whole test, so a class
/// attribute that is real storage was swallowed too:
///
///     class Store:
///         buf = bytearray(4)
///     Store.buf[0] = 5          # no store emitted
///     Store.buf[0]              # folded to 5 out of the phantom dict
///
/// The program agreed with itself at that one subscript and disagreed everywhere else: a
/// runtime index, a loop, or a read from another function saw the zeros the array was
/// allocated with. The accumulator now runs only when the attribute IS a dict or set
/// binding; everything else falls through to the path that owns it.
///
/// Every assertion is on the instructions generated, never on the build result.
/// </summary>
public class ClassAttributeSubscriptWriteTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<ArrayStore> StoresInto(ProgramIR ir, string array) =>
        ir.Functions
            .SelectMany(f => f.Body)
            .OfType<ArrayStore>()
            .Where(s => s.ArrayName.EndsWith(array, StringComparison.Ordinal))
            .ToList();

    [Fact]
    public void AClassLevelBufferSubscriptWrite_ReachesTheArray()
    {
        var ir = Gen(
            "class Store:\n" +
            "    buf = bytearray(4)\n" +
            "\n" +
            "def main():\n" +
            "    Store.buf[0] = 5\n" +
            "    Store.buf[3] = 9\n");

        // The allocation zeroes all four elements; the two writes are the ones that
        // carry a value of their own.
        var written = StoresInto(ir, "Store_buf")
            .Where(s => s.Src is Constant c && c.Value != 0)
            .Select(s => ((Constant)s.Index).Value + ":" + ((Constant)s.Src).Value)
            .ToList();

        Assert.Equal(new List<string> { "0:5", "3:9" }, written);
    }

    [Fact]
    public void AClassLevelBufferReadBack_ComesFromTheArrayAndNotFromAPhantomDict()
    {
        var ir = Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "\n" +
            "class Store:\n" +
            "    buf = bytearray(4)\n" +
            "\n" +
            "def main():\n" +
            "    Store.buf[0] = 5\n" +
            "    GPIOR0.value = Store.buf[0]\n");

        var loads = ir.Functions
            .SelectMany(f => f.Body)
            .OfType<ArrayLoad>()
            .Where(l => l.ArrayName.EndsWith("Store_buf", StringComparison.Ordinal))
            .ToList();

        Assert.Single(loads);

        // And the value never folded into the sink: a phantom dict entry would have
        // answered the read with the 5 just "written" instead of loading the array.
        var folded = ir.Functions
            .SelectMany(f => f.Body)
            .OfType<Copy>()
            .Any(c => c.Src is Constant { Value: 5 });
        Assert.False(folded);
    }

    [Fact]
    public void TheAdafruitCvPattern_StillAccumulatesAtCompileTime()
    {
        // `cls.string = {}` then `cls.string[k] = v`: a compile-time lookup table, so the
        // subscript writes emit nothing and the read folds. This is the shape the
        // accumulator exists for and it must keep working.
        var ir = Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "\n" +
            "class Mode:\n" +
            "    TABLE = {}\n" +
            "\n" +
            "def main():\n" +
            "    Mode.TABLE[1] = 7\n" +
            "    GPIOR0.value = Mode.TABLE[1]\n");

        Assert.Empty(StoresInto(ir, "Mode_TABLE"));
        Assert.Contains(
            ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant { Value: 7 });
    }
}

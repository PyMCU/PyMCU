using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// sum() and reversed() over a fixed-size array or a bytearray read the array through
/// flattened `a__0`, `a__1` slots. Nothing writes those: the array lives in SRAM and its
/// stores are ArrayStore instructions. So sum(a) printed 0 -- or whatever the previous
/// expression left in the register -- and `for v in reversed(a)` produced only zeros, with
/// literal and run-time elements alike (the shape of #401). Both now read what `a[k]` reads.
/// sum() also added at the element width, so a uint8 total wrapped past 255.
/// </summary>
public class BuiltinOverFixedArrayTests
{
    private const string Prelude =
        "from pymcu.types import uint8\n" +
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

    private static List<Instruction> Main(ProgramIR ir) =>
        ir.Functions.Single(f => f.Name == "main").Body;

    private static IEnumerable<Val> Reads(Instruction i) => i switch
    {
        Binary b => new[] { b.Src1, b.Src2 },
        Copy c => new[] { c.Src },
        Call c => c.Args,
        _ => Array.Empty<Val>(),
    };

    private static bool ReadsAFlattenedSlot(ProgramIR ir, string array) =>
        Main(ir).SelectMany(Reads).Any(v => v is Variable vv && vv.Name.StartsWith(array + "__"));

    [Fact]
    public void SumOfAFixedArray_ReadsTheArray()
    {
        var ir = Gen(
            "s = GPIOR0.value\n" +
            "a: uint8[3] = [s + 20, s + 10, s + 5]\n" +
            "print(sum(a))\n");

        Assert.False(ReadsAFlattenedSlot(ir, "a"));
        Assert.Equal(3, Main(ir).OfType<ArrayLoad>().Count(l => l.ArrayName == "a"));
    }

    [Fact]
    public void SumOfABytearray_ReadsTheArray()
    {
        var ir = Gen(
            "s = GPIOR0.value\n" +
            "bb = bytearray(3)\n" +
            "bb[0] = s + 200\n" +
            "bb[1] = s + 100\n" +
            "bb[2] = s + 50\n" +
            "print(sum(bb))\n");

        Assert.False(ReadsAFlattenedSlot(ir, "bb"));
    }

    // 200 + 100 + 50 is 350: the total needs more than the element's byte.
    [Fact]
    public void SumOfAByteArray_PromotesLikePlus()
    {
        var ir = Gen(
            "s = GPIOR0.value\n" +
            "a: uint8[3] = [s + 200, s + 100, s + 50]\n" +
            "t = sum(a)\n" +
            "print(t)\n");

        var last = Main(ir).OfType<Binary>().Last(b => b.Op == PyMCU.IR.BinaryOp.Add);
        Assert.True(((Temporary)last.Dst).Type.SizeOf() >= 2);
    }

    [Fact]
    public void ReversedOverAFixedArray_ReadsTheArray()
    {
        var ir = Gen(
            "s = GPIOR0.value\n" +
            "a: uint8[3] = [s + 30, s + 7, s + 70]\n" +
            "for v in reversed(a):\n" +
            "    print(v)\n");

        Assert.False(ReadsAFlattenedSlot(ir, "a"));
        var loads = Main(ir).OfType<ArrayLoad>().Where(l => l.ArrayName == "a")
            .Select(l => ((Constant)l.Index).Value).ToList();
        Assert.Equal(new[] { 2, 1, 0 }, loads);
    }
}

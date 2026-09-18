using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>[None] * N</c> is a fixed SRAM array of N slots. Adafruit DPS310 writes
/// <c>coeffs = [None] * 18</c> then fills it in a real loop; PCA9685 writes
/// <c>self._channels = [None] * len(self)</c>. The repeat used to reach the
/// expression visitor as a list literal in a value position.
/// </summary>
public class RepeatedNoneListTests
{
    private static ProgramIR Gen(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return Optimizer.Optimize(ir);
    }

    private static ProgramIR GenImported(string sensor, string main)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["sensor"] = new Parser(new Lexer(sensor).Tokenize()).ParseProgram(),
        };
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string>(imported.Keys));
        return Optimizer.Optimize(ir);
    }

    private static IEnumerable<Instruction> Body(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body);

    private static bool StoredAt(ArrayStore s, int index, int value) =>
        s.Index is Constant k && k.Value == index && s.Src.Equals(new Constant(value));

    private static bool StoredAtRuntime(ArrayStore s) =>
        s.Count == 18 && s.Index is not Constant;

    private static bool LoadedAt(ArrayLoad l, int index) =>
        l.Index is Constant k && k.Value == index;

    private static int ArrayCount(ProgramIR ir, string name) =>
        Body(ir).Select(i => i switch
        {
            ArrayStore s when s.ArrayName.EndsWith(name) => s.Count,
            ArrayLoad l when l.ArrayName.EndsWith(name) => l.Count,
            _ => 0,
        }).DefaultIfEmpty(0).Max();

    [Fact]
    public void ARepeatedNoneList_IsAFixedArrayIndexedByConstant()
    {
        var ir = Gen(
            "buf = bytearray([0])\n" +
            "xs = [None] * 4\n" +
            "xs[0] = 10\n" +
            "xs[1] = 20\n" +
            "xs[2] = 30\n" +
            "xs[3] = 40\n" +
            "buf[0] = xs[2]\n");

        Body(ir).OfType<ArrayStore>().Should().Contain(s => StoredAt(s, 2, 30),
            because: "[None]*4 is a 4-slot array, so xs[2] = 30 stores 30 at index 2");
        Body(ir).OfType<ArrayLoad>().Should().Contain(l => LoadedAt(l, 2),
            because: "xs[2] is an array load from the repeated list, not a scalar");
        ArrayCount(ir, "xs").Should().Be(4,
            because: "the repeat count is the array size");
    }

    [Fact]
    public void EighteenNones_AreAnEighteenSlotArray()
    {
        var ir = Gen(
            "buf = bytearray([0])\n" +
            "coeffs = [None] * 18\n" +
            "coeffs[6] = 70\n" +
            "buf[0] = coeffs[6]\n");

        ArrayCount(ir, "coeffs").Should().Be(18,
            because: "[None]*18 is 18 slots, the dps310 calibration length");
        Body(ir).OfType<ArrayStore>().Should().Contain(s => StoredAt(s, 6, 70),
            because: "coeffs[6] = 70 stores 70 at index 6 of the 18-slot array");
        Body(ir).OfType<ArrayLoad>().Should().Contain(l => LoadedAt(l, 6),
            because: "reading coeffs[6] loads the slot that was stored");
    }

    [Fact]
    public void AFieldRepeatedByLenSelf_IsIndexable()
    {
        var ir = Gen(
            "buf = bytearray([0])\n" +
            "class Dev:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.ch = [None] * len(self)\n" +
            "    def __len__(self) -> int:\n" +
            "        return 4\n" +
            "d = Dev()\n" +
            "d.ch[2] = 7\n" +
            "buf[0] = d.ch[2]\n");

        Body(ir).OfType<ArrayStore>().Should().Contain(s => StoredAt(s, 2, 7),
            because: "self.ch = [None]*len(self) is 4 slots, so ch[2] = 7 stores 7");
        Body(ir).OfType<ArrayLoad>().Should().Contain(l => LoadedAt(l, 2),
            because: "ch[2] loads the field array, not a uint8 bit");
        ArrayCount(ir, "ch").Should().Be(4,
            because: "len(self) is 4, so the field array has 4 slots");
    }

    [Fact]
    public void AnImportedMethodFillsARepeatedNoneList()
    {
        const string sensor =
            "class Dev:\n" +
            "    def fill(self) -> uint8:\n" +
            "        coeffs = [None] * 18\n" +
            "        coeffs[6] = 70\n" +
            "        return coeffs[6]\n";
        var ir = GenImported(sensor,
            "from sensor import Dev\n" +
            "buf = bytearray([0])\n" +
            "d = Dev()\n" +
            "buf[0] = d.fill()\n");

        Body(ir).OfType<ArrayStore>().Should().Contain(s => StoredAt(s, 6, 70),
            because: "an imported fill() still lays out [None]*18 and stores 70 at index 6");
        Body(ir).OfType<ArrayLoad>().Should().Contain(l => LoadedAt(l, 6),
            because: "return coeffs[6] is a load of that slot, not a missing list value");
    }

    [Fact]
    public void ARuntimeIndexFillsTheRepeatedList()
    {
        var ir = Gen(
            "buf = bytearray([0])\n" +
            "coeffs = [None] * 18\n" +
            "for i in range(18):\n" +
            "    coeffs[i] = i\n" +
            "buf[0] = coeffs[6]\n");

        Body(ir).OfType<ArrayStore>().Should().Contain(s => StoredAtRuntime(s),
            because: "range(18) is a real loop, so the store index is runtime, not a constant fold");
        Body(ir).OfType<ArrayLoad>().Should().Contain(l => LoadedAt(l, 6),
            because: "coeffs[6] after the loop loads slot 6 of the 18-slot array");
        ArrayCount(ir, "coeffs").Should().Be(18,
            because: "the repeat count is still the array size when later stores are runtime");
    }
}

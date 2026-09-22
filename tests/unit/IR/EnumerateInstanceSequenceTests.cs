using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>for i, dev in enumerate(self.i2c_device)</c>: the field is a compile-time sequence
/// of ZCA instances (<c>base__k</c>) -- the same shape <c>for x in self.seq</c> already
/// unrolls -- so the index binds as a constant and the value var binds to the element's
/// instance slot, letting a method call on the element inline per iteration. HT16K33
/// walks its wrapped I2CDevice list exactly this way; enumerate() previously recognised
/// literal/name sequences and fixed arrays but never a member-access field of instances.
/// </summary>
public class EnumerateInstanceSequenceTests
{
    // A write to a module-level ptr register is a hardware side effect the optimizer
    // cannot remove -- the observable sink that proves each element's body ran.
    private const string Hdr =
        "from pymcu.types import uint8, ptr\n\n" +
        "G: ptr[uint8] = ptr(0x3E)\n\n" +
        "class Dev:\n" +
        "    @inline\n    def __init__(self, a: uint8):\n        self._a = a\n" +
        "    @inline\n    def write(self, b: uint8):\n        G.value = b + self._a\n\n";

    private static ProgramIR Gen(string src, bool pyParser, bool optimize = true)
    {
        var ast = pyParser
            ? PythonAstReader.ParseSource(src, "main.py")
            : new Parser(new Lexer(src).Tokenize()).ParseProgram();
        var ir = new IRGenerator().Generate(
            ast,
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return optimize ? Optimizer.Optimize(ir) : ir;
    }

    public static TheoryData<bool> BothFrontEnds => new() { false, true };

    private static List<Instruction> Body(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).ToList();

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void EnumerateOverInstanceSequenceField_UnrollsWithIndexAndInstance(bool pyParser)
    {
        var ir = Gen(Hdr +
            "class Disp:\n" +
            "    @inline\n    def __init__(self, a: uint8):\n" +
            "        self.i2c_device = [Dev(a), Dev(a + 1)]\n" +
            "        for i, dev in enumerate(self.i2c_device):\n" +
            "            self._cmd(i)\n" +
            "            dev.write(i)\n" +
            "    @inline\n    def _cmd(self, i: uint8):\n        G.value = i + 100\n\n" +
            "d = Disp(0x70)\n", pyParser);

        var body = Body(ir);
        // No degraded CALL: each element's write() inlined with its own slot's _a.
        Assert.DoesNotContain(body, i => i is Call);
        // _cmd(0)->100, dev0.write(0)->0+0x70=112, _cmd(1)->101, dev1.write(1)->1+0x71=114.
        foreach (var v in new[] { 100, 112, 101, 114 })
            Assert.Contains(body, i => i is Copy
                { Src: Constant { Value: var cv }, Dst: MemoryAddress } && cv == v);
    }

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void EnumerateOverASingleInstance_UnrollsOnce(bool pyParser)
    {
        // The single-device HT16K33 arm: a one-element instance sequence.
        var ir = Gen(Hdr +
            "class Disp:\n" +
            "    @inline\n    def __init__(self, a: uint8):\n" +
            "        self.i2c_device = [Dev(a)]\n" +
            "        for i, dev in enumerate(self.i2c_device):\n" +
            "            dev.write(i)\n\n" +
            "d = Disp(0x70)\n", pyParser);

        var body = Body(ir);
        Assert.DoesNotContain(body, i => i is Call);
        Assert.Contains(body, i => i is Copy
            { Src: Constant { Value: var cv }, Dst: MemoryAddress } && cv == 0x70);
    }

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void ForIn_OverInstanceSequenceField_StillUnrolls(bool pyParser)
    {
        // The pre-existing path must not regress: plain `for x in self.seq` over the field.
        var ir = Gen(Hdr +
            "class Disp:\n" +
            "    @inline\n    def __init__(self, a: uint8):\n" +
            "        self.i2c_device = [Dev(a), Dev(a + 1)]\n" +
            "        for dev in self.i2c_device:\n" +
            "            dev.write(7)\n\n" +
            "d = Disp(0x70)\n", pyParser);

        var body = Body(ir);
        Assert.DoesNotContain(body, i => i is Call);
        foreach (var v in new[] { 7 + 0x70, 7 + 0x71 })
            Assert.Contains(body, i => i is Copy
                { Src: Constant { Value: var cv }, Dst: MemoryAddress } && cv == v);
    }
}

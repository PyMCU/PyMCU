using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A field bound to a list of constructed instances (<c>self.i2c_device = [Dev(a)]</c>) is a
/// compile-time sequence of ZCA elements (<c>base__k</c>) -- the shape the plain
/// <c>for x in self.seq</c> unroll already walks. HT16K33 also sizes its buffer with
/// <c>bytearray(self._buffer_size * len(self.i2c_device))</c>: a field constant times
/// <c>len()</c> of the instance sequence, both fixed at compile time. The constant
/// evaluator had no <c>len()</c> case at all, so the product never folded and the buffer
/// fell into the runtime-sized arena path. <c>len()</c> of a compile-time sequence now
/// folds -- and when it cannot, the refusal names the operand that did not fold.
/// </summary>
public class InstanceSequenceFieldTests
{
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

    // bytearray(self._n * len(self.devs)): the field constant and the sequence length are
    // both known at compile time, so the product is the fixed buffer size -- not an arena
    // allocation.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void LenOfInstanceSequenceField_SizesABytearrayField(bool pyParser)
    {
        var ir = Gen(Hdr +
            "class Disp:\n" +
            "    @inline\n    def __init__(self, a: uint8):\n" +
            "        self.i2c_device = [Dev(a)]\n" +
            "        self._buffer_size = 17\n" +
            "        self._buffer = bytearray((self._buffer_size) * len(self.i2c_device))\n\n" +
            "d = Disp(0x70)\n", pyParser);

        var body = Body(ir);
        // 17 * 1 = 17: a fixed SRAM buffer, zero-filled by one ArrayStore per slot.
        Assert.Contains(body, i => i is ArrayStore s && s.ArrayName.EndsWith("_buffer")
            && s.Count == 17);
        // a size that folds is a fixed buffer, not a runtime-sized arena region
        Assert.DoesNotContain(body, i => i is ArrayStore s && s.ArrayName == "_arena");
    }

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void LenOfInstanceSequenceField_CountsEveryElement(bool pyParser)
    {
        var ir = Gen(Hdr +
            "class Disp:\n" +
            "    @inline\n    def __init__(self, a: uint8):\n" +
            "        self.i2c_device = [Dev(a), Dev(a + 1)]\n" +
            "        self._buffer_size = 17\n" +
            "        self._buffer = bytearray((self._buffer_size) * len(self.i2c_device))\n\n" +
            "d = Disp(0x70)\n", pyParser);

        // 17 * 2 = 34: len() answers the element count, so the product is exact.
        Assert.Contains(Body(ir), i => i is ArrayStore s && s.ArrayName.EndsWith("_buffer")
            && s.Count == 34);
    }

    // bytearray(n * len(self.devs)) where devs is a scalar field: len() has no sequence
    // to count, and the refusal must say WHICH operand -- not the whole initializer.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void BytearraySize_LenOfANonSequence_NamesTheOperand(bool pyParser)
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Hdr +
            "class Disp:\n" +
            "    @inline\n    def __init__(self, a: uint8):\n" +
            "        self.devs = a\n" +
            "        self._buffer_size = 17\n" +
            "        self._buffer = bytearray((self._buffer_size) * len(self.devs))\n\n" +
            "d = Disp(0x70)\n", pyParser));

        Assert.Contains("self.devs", ex.Message);
    }
}

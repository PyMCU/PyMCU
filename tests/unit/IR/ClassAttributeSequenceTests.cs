using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A class-level tuple/list (<c>analog_pins = (0, 1, 2, 3)</c> inside a class body) is a
/// compile-time sequence: it has no storage and the binding is the whole meaning, same as
/// a module-level one. Before this registration <c>Cls.pins[0]</c> folded to a bit check
/// on a zero-initialised scalar -- a wrong answer said cleanly. adafruit_seesaw's pinmap
/// classes (whose <c>pwm_pins += (...)</c> class-body augmentation concatenates tuples at
/// class creation) are the demandant.
/// </summary>
public class ClassAttributeSequenceTests
{
    private const string Hdr =
        "from pymcu.types import uint8, ptr\n\n" +
        "G: ptr[uint8] = ptr(0x3E)\n\n" +
        "class MapA:\n" +
        "    analog_pins = (0, 1, 2, 3)\n" +
        "    pwm_pins = (0, 1)\n" +
        "    pwm_pins += (2, 3)\n\n";

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

    // `MapA.analog_pins[2]` folds to the element -- not a bit check on a scalar slot.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void ConstSubscript_FoldsToTheElement(bool pyParser)
    {
        var ir = Gen(Hdr +
            "G.value = MapA.analog_pins[2]\n", pyParser);

        Assert.Contains(Body(ir), i => i is Copy c && c.Src is Constant { Value: 2 }
            && c.Dst is MemoryAddress);
    }

    // `pin in MapA.analog_pins` answers membership against the class's tuple.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void Membership_AnswersFromTheClassTuple(bool pyParser)
    {
        var ir = Gen(Hdr +
            "if 40 not in MapA.analog_pins:\n" +
            "    G.value = 1\n", pyParser);

        // 40 is in no element, so the not-in guard folds true and the write is emitted
        // unconditionally (nothing compares at run time).
        var body = Body(ir);
        Assert.Contains(body, i => i is Copy c && c.Src is Constant { Value: 1 }
            && c.Dst is MemoryAddress);
        Assert.DoesNotContain(body, i => i is Binary { Op: PyMCU.IR.BinaryOp.Equal });
    }

    // `for p in MapA.analog_pins` unrolls over the class's elements.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void ForLoop_UnrollsOverClassElements(bool pyParser)
    {
        var ir = Gen(Hdr +
            "for p in MapA.analog_pins:\n" +
            "    G.value = p\n", pyParser);

        var body = Body(ir);
        foreach (int el in new[] { 0, 1, 2, 3 })
            Assert.Contains(body, i => i is Copy c && c.Src is Constant cv && cv.Value == el
                && c.Dst is MemoryAddress);
    }

    // `pwm_pins += (2, 3)` in the class body concatenates at class creation: the binding
    // holds all four elements, so index 3 folds to the appended tuple's last element.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void AugAssign_ExtendsTheClassTuple(bool pyParser)
    {
        var ir = Gen(Hdr +
            "G.value = MapA.pwm_pins[3]\n", pyParser);

        Assert.Contains(Body(ir), i => i is Copy c && c.Src is Constant { Value: 3 }
            && c.Dst is MemoryAddress);
    }

    // `len(MapA.analog_pins)` answers the element count.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void Len_AnswersTheElementCount(bool pyParser)
    {
        var ir = Gen(Hdr +
            "G.value = len(MapA.analog_pins)\n", pyParser);

        Assert.Contains(Body(ir), i => i is Copy c && c.Src is Constant { Value: 4 }
            && c.Dst is MemoryAddress);
    }
}

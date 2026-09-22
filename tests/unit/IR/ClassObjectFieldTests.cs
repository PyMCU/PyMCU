using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A field assigned a class OBJECT (<c>self.pin_mapping = SAMD09_Pinmap</c>) is a
/// compile-time class binding: the layout byte carries a tag -- the candidate's index in
/// the ordered list of classes the field can hold -- and a read like
/// <c>self.pin_mapping.analog_pins</c> resolves the attribute on every candidate and
/// selects on the tag. This is how adafruit_seesaw picks a pinmap by chip id, off wire
/// data, inside __init__; every arm is live at compile time.
/// </summary>
public class ClassObjectFieldTests
{
    private const string Hdr =
        "from pymcu.types import uint8, ptr\n\n" +
        "G: ptr[uint8] = ptr(0x3E)\n\n" +
        "class MapA:\n" +
        "    analog_pins = (0, 1, 2, 3)\n" +
        "    pwm_width = 8\n\n" +
        "class MapB:\n" +
        "    analog_pins = (2, 3, 40, 41)\n" +
        "    pwm_width = 16\n\n";

    private const string Dev =
        "class Dev:\n" +
        "    @inline\n    def __init__(self, chip):\n" +
        "        self.chip_id = chip\n" +
        "        if self.chip_id == 0x55:\n" +
        "            self.pin_mapping = MapA\n" +
        "        else:\n" +
        "            self.pin_mapping = MapB\n\n";

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

    // self.pin_mapping = MapA / = MapB on different arms: each writes the tag byte of the
    // field's layout slot, so a read knows which class object the field holds.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void ClassObjectField_StoresATagPerCandidate(bool pyParser)
    {
        var ir = Gen(Hdr + Dev +
            "d = Dev(G[0])\n", pyParser, optimize: false);

        var body = Body(ir);
        Assert.Contains(body, i => i is Copy c && c.Src is Constant { Value: 0 }
            && c.Dst is Variable v && v.Name.EndsWith("_pin_mapping"));
        Assert.Contains(body, i => i is Copy c && c.Src is Constant { Value: 1 }
            && c.Dst is Variable v && v.Name.EndsWith("_pin_mapping"));
    }

    // `pin not in self.pin_mapping.analog_pins` where every candidate's tuple answers the
    // same way folds with no tag compare -- 2 is in (0,1,2,3) and in (2,3,40,41), so the
    // not-in guard is always false and no raise path is emitted.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void In_AllCandidatesAgree_Folds(bool pyParser)
    {
        var ir = Gen(Hdr + Dev +
            "    @inline\n    def probe(self, pin):\n" +
            "        if pin not in self.pin_mapping.analog_pins:\n" +
            "            G.value = 99\n" +
            "d = Dev(G[0])\n" +
            "d.probe(2)\n", pyParser, optimize: false);

        var body = Body(ir);
        // 2 is in BOTH tuples, so `not in` folds to false on every arm -- nothing ever
        // compares the field's tag byte for this read.
        Assert.DoesNotContain(body, i => i is Binary b
            && b.Src1 is Variable v && v.Name.EndsWith("_pin_mapping"));
        Assert.DoesNotContain(body, i => i is JumpIfNotEqual j
            && j.Src1 is Variable v && v.Name.EndsWith("_pin_mapping"));
    }

    // pin = 40 is in MapB's tuple but not MapA's: membership cannot fold to one answer,
    // so the tag byte selects which class's tuple answers.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void In_CandidatesDisagree_DispatchesOnTag(bool pyParser)
    {
        var ir = Gen(Hdr + Dev +
            "    @inline\n    def probe(self, pin):\n" +
            "        if pin not in self.pin_mapping.analog_pins:\n" +
            "            G.value = 99\n" +
            "d = Dev(G[0])\n" +
            "d.probe(40)\n", pyParser, optimize: false);

        var body = Body(ir);
        // `40 not in analog_pins` is true only when the field holds MapA (tag 0): the
        // membership result is a compare of the field's tag byte.
        Assert.Contains(body, i => i is Binary b && b.Op == PyMCU.IR.BinaryOp.Equal
            && b.Src1 is Variable v && v.Name.EndsWith("_pin_mapping")
            && b.Src2 is Constant { Value: 0 });
    }

    // `self.pin_mapping.analog_pins.index(pin)`: the index differs per candidate class,
    // so each arm computes its own tuple's index and the tag selects.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void Index_DispatchesPerCandidateTuple(bool pyParser)
    {
        var ir = Gen(Hdr + Dev +
            "    @inline\n    def probe(self, pin):\n" +
            "        G.value = self.pin_mapping.analog_pins.index(pin)\n" +
            "d = Dev(G[0])\n" +
            "d.probe(2)\n", pyParser);

        var body = Body(ir);
        // pin=2: MapA (0,1,2,3) -> 2, MapB (2,3,40,41) -> 0. Tag arms carry both.
        Assert.Contains(body, i => i is JumpIfNotEqual j
            && j.Src1 is Variable v && v.Name.EndsWith("_pin_mapping")
            && j.Src2 is Constant { Value: 0 });
        Assert.Contains(body, i => i is Copy c && c.Src is Constant { Value: 2 });
        Assert.Contains(body, i => i is Copy c && c.Src is Constant { Value: 0 }
            && c.Dst is Temporary);
    }

    // `self.pin_mapping.pwm_width`: a scalar class attribute -- per-candidate values
    // (8 on MapA, 16 on MapB) selected on the tag.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void ScalarAttribute_SelectsOnTag(bool pyParser)
    {
        var ir = Gen(Hdr + Dev +
            "    @inline\n    def probe(self):\n" +
            "        G.value = self.pin_mapping.pwm_width\n" +
            "d = Dev(G[0])\n" +
            "d.probe()\n", pyParser);

        var body = Body(ir);
        Assert.Contains(body, i => i is JumpIfNotEqual j
            && j.Src1 is Variable v && v.Name.EndsWith("_pin_mapping"));
        Assert.Contains(body, i => i is Copy c
            && c.Src is Variable sv && sv.Name.EndsWith("MapA_pwm_width"));
        Assert.Contains(body, i => i is Copy c
            && c.Src is Variable sv2 && sv2.Name.EndsWith("MapB_pwm_width"));
    }

    // One candidate needs no tag: `self.pm = MapA` unconditionally means reads fold to
    // MapA's attributes directly.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void SingleCandidate_FoldsWithoutDispatch(bool pyParser)
    {
        var ir = Gen(Hdr +
            "class Dev:\n" +
            "    @inline\n    def __init__(self):\n" +
            "        self.pin_mapping = MapA\n" +
            "    @inline\n    def probe(self, pin):\n" +
            "        if pin not in self.pin_mapping.analog_pins:\n" +
            "            G.value = 99\n" +
            "        else:\n" +
            "            G.value = self.pin_mapping.analog_pins.index(pin)\n\n" +
            "d = Dev()\n" +
            "d.probe(2)\n", pyParser);

        var body = Body(ir);
        // 2 IS in MapA's tuple, index 2 -- both fold, no tag compares anywhere.
        Assert.DoesNotContain(body, i => i is Binary b && b.Src1 is Variable v
            && v.Name.EndsWith("_pin_mapping"));
        Assert.DoesNotContain(body, i => i is JumpIfNotEqual j
            && j.Src1 is Variable v && v.Name.EndsWith("_pin_mapping"));
        Assert.Contains(body, i => i is Copy c && c.Src is Constant { Value: 2 }
            && c.Dst is MemoryAddress);
    }

    // A subscript on a sequence attribute: `self.pm.analog_pins[1]` -- per-candidate
    // element values, selected on the tag.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void ConstSubscript_SelectsPerCandidateElement(bool pyParser)
    {
        var ir = Gen(Hdr + Dev +
            "    @inline\n    def probe(self):\n" +
            "        G.value = self.pin_mapping.analog_pins[1]\n" +
            "d = Dev(G[0])\n" +
            "d.probe()\n", pyParser);

        var body = Body(ir);
        // MapA[1]=1, MapB[1]=3 -- the two constants land behind a tag select.
        Assert.Contains(body, i => i is JumpIfNotEqual j
            && j.Src1 is Variable v && v.Name.EndsWith("_pin_mapping"));
        Assert.Contains(body, i => i is Copy c && c.Src is Constant { Value: 1 }
            && c.Dst is Temporary);
        Assert.Contains(body, i => i is Copy c && c.Src is Constant { Value: 3 }
            && c.Dst is Temporary);
    }

    // len(self.pm.analog_pins): counts differ -> select on tag.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void Len_SelectsPerCandidateCount(bool pyParser)
    {
        var ir = Gen(
            "from pymcu.types import uint8, ptr\n\n" +
            "G: ptr[uint8] = ptr(0x3E)\n\n" +
            "class MapA:\n    pins = (0, 1)\n\n" +
            "class MapB:\n    pins = (0, 1, 2, 3)\n\n" +
            "class Dev:\n" +
            "    @inline\n    def __init__(self, chip):\n" +
            "        if chip == 0x55:\n" +
            "            self.pm = MapA\n" +
            "        else:\n" +
            "            self.pm = MapB\n" +
            "    @inline\n    def probe(self):\n" +
            "        G.value = len(self.pm.pins)\n\n" +
            "d = Dev(G[0])\n" +
            "d.probe()\n", pyParser);

        var body = Body(ir);
        Assert.Contains(body, i => i is JumpIfNotEqual j
            && j.Src1 is Variable v && v.Name.EndsWith("_pm"));
        Assert.Contains(body, i => i is Copy c && c.Src is Constant { Value: 2 }
            && c.Dst is Temporary);
        Assert.Contains(body, i => i is Copy c && c.Src is Constant { Value: 4 }
            && c.Dst is Temporary);
    }

    // Every assignment to a class-object field must name a class known at compile time:
    // `self.pm = chip` after `self.pm = MapA` is a located refusal naming the field.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void NonClassAssignment_NamesTheField(bool pyParser)
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Hdr +
            "class Dev:\n" +
            "    @inline\n    def __init__(self, chip):\n" +
            "        self.pin_mapping = MapA\n" +
            "        self.pin_mapping = chip\n\n" +
            "d = Dev(G[0])\n", pyParser));

        Assert.Contains("pin_mapping", ex.Message);
    }

    // `for p in self.pm.analog_pins` over two candidates has no tag dispatch to unroll
    // through -- the refusal names the field and the classes.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void ForOverClassObjectAttr_NamesFieldAndClasses(bool pyParser)
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Hdr + Dev +
            "    @inline\n    def probe(self):\n" +
            "        for p in self.pin_mapping.analog_pins:\n" +
            "            G.value = p\n" +
            "d = Dev(G[0])\n" +
            "d.probe()\n", pyParser));

        Assert.Contains("pin_mapping", ex.Message);
        Assert.Contains("MapA", ex.Message);
        Assert.Contains("MapB", ex.Message);
    }

    // `x = self.pm.analog_pins` asks a scalar slot to carry a tuple -- refused with the
    // field name and the attribute's kind.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void SequenceAttributeAsValue_NamesFieldAndAttribute(bool pyParser)
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Hdr + Dev +
            "    @inline\n    def probe(self):\n" +
            "        x = self.pin_mapping.analog_pins\n" +
            "        G.value = x[0]\n" +
            "d = Dev(G[0])\n" +
            "d.probe()\n", pyParser));

        Assert.Contains("pin_mapping", ex.Message);
        Assert.Contains("analog_pins", ex.Message);
    }

    // `self.pm.attr[i]` with a run-time index cannot fold -- refused naming the field.

    [Theory]
    [MemberData(nameof(BothFrontEnds))]
    public void RuntimeSubscript_NamesTheField(bool pyParser)
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(Hdr + Dev +
            "    @inline\n    def probe(self, i):\n" +
            "        G.value = self.pin_mapping.analog_pins[i]\n" +
            "d = Dev(G[0])\n" +
            "d.probe(G[1])\n", pyParser));

        Assert.Contains("pin_mapping", ex.Message);
    }
}

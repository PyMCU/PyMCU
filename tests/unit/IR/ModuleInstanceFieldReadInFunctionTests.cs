using System.Collections.Generic;
using System.Linq;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A field of a module-level instance read inside a function must see the same value the
/// constructor stored: `pwmio.PWMOut(board.D5, ..., frequency=50)` computes the bucket the
/// pin actually runs at (`self._real_frequency = self._pwm.frequency()`, an @inline call
/// whose result temp aliases the constant it folded to), and `print(pwm.frequency)` inside
/// a function read 0.
///
/// Two gaps met there. The store elided the field into constant tracking only when the RHS
/// temp held the constant itself -- a result temp that merely ALIASED the folded temp fell
/// through to a runtime Copy the optimizer then dead-stored once main's own reads folded,
/// because the flattened name was nobody's global. And when the value could not fold at all
/// the read still produced that same un-homed Variable. The store now chases the alias and
/// folds like every sibling field; the read gives a module-level instance's surviving field
/// a global, so the store in main is kept for the function that loads it.
/// </summary>
public class ModuleInstanceFieldReadInFunctionTests
{
    private static ProgramIR Gen(ProgramNode prog) =>
        new IRGenerator().Generate(
            prog,
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static ProgramIR Parse(string src) => Gen(
        new Parser(new Lexer(src).Tokenize()).ParseProgram());

    private static ProgramIR Translate(string src) => Gen(
        PythonAstReader.ParseSource(src, "main.py"));

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "from pymcu.types import uint8, uint16, inline\n\n";

    // The pwmio.PWMOut shape in miniature: the field a property returns is stored from an
    // @inline call's result, which is itself an @inline call's result -- the value arrives
    // under an alias of the temp that holds the constant.
    private const string NestedInlineField =
        "@inline\n" +
        "def bucket_frequency(pin: uint8, f: uint16) -> uint16:\n" +
        "    return f * 61 // 50\n\n" +
        "class Hal:\n" +
        "    @inline\n" +
        "    def __init__(self, pin: uint8, f: uint16):\n" +
        "        self._pin = pin\n" +
        "        self._freq = f\n\n" +
        "    @inline\n" +
        "    def frequency(self) -> uint16:\n" +
        "        return bucket_frequency(self._pin, self._freq)\n\n" +
        "class Out:\n" +
        "    @inline\n" +
        "    def __init__(self, pin: uint8, f: uint16):\n" +
        "        self._pwm = Hal(pin, f)\n" +
        "        self._real_frequency = self._pwm.frequency()\n\n" +
        "    @property\n" +
        "    def frequency(self) -> uint16:\n" +
        "        return self._real_frequency\n\n" +
        "o = Out(5, 50)\n\n" +
        "def show():\n" +
        "    GPIOR0.value = o.frequency\n\n" +
        "show()\n";

    private static void AssertShowWrites61(ProgramIR ir)
    {
        var show = Assert.Single(ir.Functions, f => f.Name == "show");
        Assert.Contains(show.Body.OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 61);
        // And the flattened field is gone entirely: a read of it in show would have loaded
        // a slot the constructor never wrote.
        Assert.DoesNotContain(show.Body.SelectMany(AllVals).OfType<Variable>(),
            v => v.Name == "o__real_frequency");
    }

    private static IEnumerable<Val> AllVals(Instruction i)
    {
        foreach (var p in i.GetType().GetProperties())
            if (p.GetValue(i) is Val v) yield return v;
    }

    [Fact]
    public void PropertyFieldFromAliasedTemp_ReadInFunction_Folds() =>
        AssertShowWrites61(Parse(Preamble + NestedInlineField));

    [Fact]
    public void PropertyFieldFromAliasedTemp_ReadInFunction_Folds_CPythonFrontEnd() =>
        AssertShowWrites61(Translate(Preamble + NestedInlineField));

    // The non-foldable twin: the field holds a runtime value (a mutable global read at
    // construction), so there is no constant to see -- the reader in another function needs
    // the constructor's store to survive, which only a real global does.
    private const string RuntimeField =
        "counter: uint8 = 0\n\n" +
        "def bump():\n" +
        "    global counter\n" +
        "    counter = counter + 1\n\n" +
        "class C:\n" +
        "    @inline\n" +
        "    def __init__(self):\n" +
        "        self._x = counter\n\n" +
        "    @property\n" +
        "    def x(self) -> uint8:\n" +
        "        return self._x\n\n" +
        "bump()\n" +
        "bump()\n" +
        "c = C()\n\n" +
        "def show():\n" +
        "    GPIOR0.value = c.x\n\n" +
        "show()\n";

    private static void AssertFieldIsGlobal(ProgramIR ir)
    {
        Assert.Contains(ir.Globals, g => g.Name == "c__x");
        var show = Assert.Single(ir.Functions, f => f.Name == "show");
        Assert.Contains(show.Body.SelectMany(AllVals).OfType<Variable>(),
            v => v.Name == "c__x");
    }

    [Fact]
    public void RuntimeField_ReadInFunction_GetsAGlobal() =>
        AssertFieldIsGlobal(Parse(Preamble + RuntimeField));

    [Fact]
    public void RuntimeField_ReadInFunction_GetsAGlobal_CPythonFrontEnd() =>
        AssertFieldIsGlobal(Translate(Preamble + RuntimeField));

    // The PWM.frequency() shape in miniature: `if self._exact:` folds at expansion, so the
    // first return ends the body unconditionally and the trailing `return` is dead code.
    // A dead return still walks its expression, and its alias write used to land anyway --
    // chasing it folded the field to the DEAD arm's value (61 for a pin running at 50).
    private const string TwoReturnGetter =
        "@inline\n" +
        "def exact_frequency(f: uint16) -> uint16:\n" +
        "    return f\n\n" +
        "@inline\n" +
        "def bucket_frequency(f: uint16) -> uint16:\n" +
        "    return f * 61 // 50\n\n" +
        "class Hal:\n" +
        "    @inline\n" +
        "    def __init__(self, pin: uint8, f: uint16, exact: uint8):\n" +
        "        self._pin = pin\n" +
        "        self._freq = f\n" +
        "        self._exact = exact\n\n" +
        "    @inline\n" +
        "    def frequency(self) -> uint16:\n" +
        "        if self._exact == 1:\n" +
        "            return exact_frequency(self._freq)\n" +
        "        return bucket_frequency(self._freq)\n\n" +
        "class Out:\n" +
        "    @inline\n" +
        "    def __init__(self, pin: uint8, f: uint16, exact: uint8):\n" +
        "        self._pwm = Hal(pin, f, exact)\n" +
        "        self._real_frequency = self._pwm.frequency()\n\n" +
        "    @property\n" +
        "    def frequency(self) -> uint16:\n" +
        "        return self._real_frequency\n\n";

    private const string ExactTail =
        "o = Out(9, 50, 1)\n\n" +
        "def show():\n" +
        "    GPIOR0.value = o.frequency\n\n" +
        "show()\n";

    private const string BucketTail =
        "o = Out(5, 50, 0)\n\n" +
        "def show():\n" +
        "    GPIOR0.value = o.frequency\n\n" +
        "show()\n";

    private static void AssertShowWrites(ProgramIR ir, int value)
    {
        var show = Assert.Single(ir.Functions, f => f.Name == "show");
        Assert.Contains(show.Body.OfType<Copy>(),
            c => c.Src is Constant k && k.Value == value);
        Assert.DoesNotContain(show.Body.SelectMany(AllVals).OfType<Variable>(),
            v => v.Name == "o__real_frequency");
    }

    [Fact]
    public void DeadReturnDoesNotOverwriteTheTakenArmsAlias() =>
        AssertShowWrites(Parse(Preamble + TwoReturnGetter + ExactTail), 50);

    [Fact]
    public void DeadReturnDoesNotOverwriteTheTakenArmsAlias_CPythonFrontEnd() =>
        AssertShowWrites(Translate(Preamble + TwoReturnGetter + ExactTail), 50);

    [Fact]
    public void DeadArmReturnDoesNotMaskTheFallthroughAlias() =>
        AssertShowWrites(Parse(Preamble + TwoReturnGetter + BucketTail), 61);

    [Fact]
    public void DeadArmReturnDoesNotMaskTheFallthroughAlias_CPythonFrontEnd() =>
        AssertShowWrites(Translate(Preamble + TwoReturnGetter + BucketTail), 61);

    // The servo.fraction setter shape: `s.fraction = 0.5` hands the setter a FloatConstant,
    // which had no binding arm -- `value` read a never-written slot, so the duty came out
    // as _min_duty alone. And once the float binds, `0.0 <= value <= 1.0` must fold TRUE:
    // the const-float compare used to answer FloatConstant(0.0) for every comparison, so
    // the range check raised on a legal 0.5. duty = 29 + int(0.5 * 59) = 58.
    private const string FloatSetterChain =
        "class S:\n" +
        "    @inline\n" +
        "    def __init__(self):\n" +
        "        self._min = 29\n" +
        "        self._range = 59\n" +
        "        self._duty = 0\n\n" +
        "    @property\n" +
        "    def duty(self) -> uint8:\n" +
        "        return self._duty\n\n" +
        "    @property\n" +
        "    def fraction(self) -> float:\n" +
        "        return 0.0\n\n" +
        "    @fraction.setter\n" +
        "    def fraction(self, value: float):\n" +
        "        if not 0.0 <= value <= 1.0:\n" +
        "            raise ValueError(\"Must be 0.0 to 1.0\")\n" +
        "        self._duty = self._min + int(value * self._range)\n\n" +
        "s = S()\n" +
        "s.fraction = 0.5\n\n" +
        "def show():\n" +
        "    GPIOR0.value = s.duty\n\n" +
        "show()\n";

    private static void AssertShowWritesConst(ProgramIR ir, int value)
    {
        var show = Assert.Single(ir.Functions, f => f.Name == "show");
        Assert.Contains(show.Body.OfType<Copy>(),
            c => c.Src is Constant k && k.Value == value);
    }

    private static void AssertDutyStored58(ProgramIR ir)
    {
        // _duty is mutable (init 0, setter writes 58), so it lives in a real global the
        // constructor's computed store reaches -- and show() reads that same global.
        var main = Assert.Single(ir.Functions, f => f.Name == "main");
        Assert.Contains(main.Body.OfType<Copy>(),
            c => c.Src is Constant { Value: 58 }
                 && c.Dst is Variable { Name: "s__duty" });
        Assert.Contains(ir.Globals, g => g.Name == "s__duty");
        var show = Assert.Single(ir.Functions, f => f.Name == "show");
        Assert.Contains(show.Body.SelectMany(AllVals).OfType<Variable>(),
            v => v.Name == "s__duty");
    }

    [Fact]
    public void FloatSetterParam_BindsAndRangeCheckFolds() =>
        AssertDutyStored58(Parse(Preamble + FloatSetterChain));

    [Fact]
    public void FloatSetterParam_BindsAndRangeCheckFolds_CPythonFrontEnd() =>
        AssertDutyStored58(Translate(Preamble + FloatSetterChain));

    // The comparison fold on its own: a float-typed @inline parameter binds into
    // floatConstantVariables, so `0.0 <= v <= 1.0` sees FloatConstant operands -- and used
    // to fold each arm to FloatConstant(0.0), so `if not <chain>` read the range check as
    // failed on a legal value. (`if <chain>` alone jump-threads to jgt on the operands;
    // `not` forces the value evaluation this fold serves.)
    private const string FloatCompare =
        "@inline\n" +
        "def check(v: float) -> int:\n" +
        "    if not 0.0 <= v <= 1.0:\n" +
        "        return 99\n" +
        "    return 7\n\n" +
        "def show():\n" +
        "    GPIOR0.value = check(0.5)\n\n" +
        "show()\n";

    private static void AssertShowFoldsTo(ProgramIR ir, int value)
    {
        var show = Assert.Single(ir.Functions, f => f.Name == "show");
        Assert.Contains(show.Body.OfType<Copy>(),
            c => c.Src is Constant k && k.Value == value);
        Assert.DoesNotContain(show.Body, i => i is JumpIfZero or JumpIfNotZero
            or JumpIfEqual or JumpIfNotEqual or JumpIfLessThan or JumpIfLessOrEqual
            or JumpIfGreaterThan or JumpIfGreaterOrEqual);
    }

    [Fact]
    public void ConstFloatComparison_FoldsToBool() =>
        AssertShowFoldsTo(Parse(Preamble + FloatCompare), 7);

    [Fact]
    public void ConstFloatComparison_FoldsToBool_CPythonFrontEnd() =>
        AssertShowFoldsTo(Translate(Preamble + FloatCompare), 7);
}

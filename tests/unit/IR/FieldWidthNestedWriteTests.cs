using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#488. DeriveFieldLayout decided a field's width from the TOP-LEVEL statements of
/// each method body only, so a write inside a for/while/if/try/with was invisible. A field
/// bound to an unannotated __init__ parameter and re-written with a 31-bit LCG state inside
/// a nested loop kept the uint8 default and every store truncated. CPython answers the same
/// program correctly; the firmware diverged in silence.
///
/// The layout is the join of ALL writes at any depth, so these are all the same field:
/// <c>self.x = 0</c> plus <c>self.x = 70000</c> inside a loop is a uint32 field wherever
/// the second write sits.
/// </summary>
public class FieldWidthNestedWriteTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Variable> Field(ProgramIR ir, string suffix)
    {
        var found = new List<Variable>();
        void Note(Val? v) { if (v is Variable var && var.Name.EndsWith(suffix)) found.Add(var); }
        foreach (var f in ir.Functions)
            foreach (var ins in f.Body)
                switch (ins)
                {
                    case Copy c: Note(c.Src); Note(c.Dst); break;
                    case Binary b: Note(b.Src1); Note(b.Src2); Note(b.Dst); break;
                }
        return found;
    }

    [Fact]
    public void ParamBoundField_WrittenWideInsideNestedLoops_IsUint32()
    {
        // The reproducer from the issue: `self._rng = seed` on an unannotated parameter,
        // then the 31-bit update inside two `for` loops in another method.
        var ir = Gen(
            "class Box:\n" +
            "    def __init__(self, seed):\n" +
            "        self._rng = seed\n" +
            "    def step(self):\n" +
            "        for y in range(4):\n" +
            "            for x in range(4):\n" +
            "                self._rng = (self._rng * 1103515245 + 12345) & 0x7FFFFFFF\n" +
            "b = Box(9)\n" +
            "b.step()\n" +
            "out = b._rng\n");
        Optimizer.UnifyVariableWidths(ir);
        var vars = Field(ir, "_rng");
        Assert.NotEmpty(vars);
        Assert.All(vars, v => Assert.Equal(DataType.UINT32, v.Type));
    }

    [Fact]
    public void NestedWriteInsideInit_WidensTheField()
    {
        var ir = Gen(
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self._x = 0\n" +
            "        for i in range(3):\n" +
            "            self._x = 70000\n" +
            "b = Box()\n" +
            "out = b._x\n");
        Optimizer.UnifyVariableWidths(ir);
        var vars = Field(ir, "_x");
        Assert.NotEmpty(vars);
        Assert.All(vars, v => Assert.Equal(DataType.UINT32, v.Type));
    }

    [Fact]
    public void NestedWriteInsideTryAndWhile_WidensTheField()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self._x = 0\n" +
            "    def bump(self):\n" +
            "        try:\n" +
            "            while True:\n" +
            "                self._x = 1000000\n" +
            "                break\n" +
            "        except:\n" +
            "            pass\n" +
            "b = Box()\n" +
            "b.bump()\n" +
            "out = b._x\n");
        Optimizer.UnifyVariableWidths(ir);
        var vars = Field(ir, "_x");
        Assert.NotEmpty(vars);
        Assert.All(vars, v => Assert.Equal(DataType.UINT32, v.Type));
    }

    [Fact]
    public void NestedKindConflict_StillDiagnosed()
    {
        // The incompatible-kind diagnostic must fire for a nested write exactly as it does
        // for a top-level one: an int field cannot later take a str, wherever the write sits.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "from pymcu.types import uint8\n" +
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self._x = 1\n" +
            "    def set(self):\n" +
            "        if True:\n" +
            "            self._x = \"no\"\n" +
            "def main():\n" +
            "    b = Box()\n" +
            "    b.set()\n"));
        Assert.Contains("first typed as numeric", ex.Message);
    }

    [Fact]
    public void KindConflictInsideInit_IsDiagnosed()
    {
        // The cross-method check refuses a field whose kind changes between write sites;
        // two writes inside __init__ itself must answer the same way. Without the check the
        // second write silently retyped the field to str.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self._x = 1\n" +
            "        self._x = \"no\"\n" +
            "def main():\n" +
            "    b = Box()\n"));
        Assert.Contains("first typed as numeric", ex.Message);
        Assert.Contains("__init__", ex.Message);
    }

    [Fact]
    public void KindConflictNestedInsideInit_IsDiagnosed()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self._x = 1\n" +
            "        for i in range(2):\n" +
            "            self._x = \"no\"\n" +
            "def main():\n" +
            "    b = Box()\n"));
        Assert.Contains("first typed as numeric", ex.Message);
    }

    [Fact]
    public void KindConflictInsideInit_StrThenNumeric_IsDiagnosed()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self._x = \"a\"\n" +
            "        self._x = 70000\n" +
            "def main():\n" +
            "    b = Box()\n"));
        Assert.Contains("first typed as str", ex.Message);
    }

    [Fact]
    public void SameKindWritesInsideInit_StillWiden()
    {
        // numeric -> numeric is the widening the layout always allowed: no diagnostic.
        var ir = Gen(
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self._x = 1\n" +
            "        if True:\n" +
            "            self._x = 70000\n" +
            "b = Box()\n" +
            "out = b._x\n");
        Optimizer.UnifyVariableWidths(ir);
        var vars = Field(ir, "_x");
        Assert.NotEmpty(vars);
        Assert.All(vars, v => Assert.Equal(DataType.UINT32, v.Type));
    }

    [Fact]
    public void AugAssignInsideNestedLoop_WidensTheField()
    {
        // `self._x += 70000` is the same write for the width join as `=`.
        var ir = Gen(
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self._x = 0\n" +
            "    def bump(self):\n" +
            "        for i in range(2):\n" +
            "            self._x += 70000\n" +
            "b = Box()\n" +
            "b.bump()\n" +
            "out = b._x\n");
        Optimizer.UnifyVariableWidths(ir);
        var vars = Field(ir, "_x");
        Assert.NotEmpty(vars);
        Assert.All(vars, v => Assert.Equal(DataType.UINT32, v.Type));
    }

    [Fact]
    public void AugAssignAlone_DoesNotIntroduceAField()
    {
        // `+=` reads the member it augments: `self._y += 1` on a name no
        // assignment ever declared is CPython's AttributeError, not a field
        // declaration. bump() IS reachable from __init__ here, so an `=` write
        // would legitimately introduce the field -- the pin is that `+=` alone
        // does not.
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self._x = 0\n" +
            "        self.bump()\n" +
            "    def bump(self):\n" +
            "        for i in range(2):\n" +
            "            self._y += 1\n" +
            "b = Box()\n"));
    }

    [Fact]
    public void HelperCalledThroughAnotherHelper_IntroducesItsField()
    {
        // __init__ -> _setup -> _reset are all inside the constructor's own call graph,
        // so the field only `_reset` writes is still a construction-time field.
        var ir = Gen(
            "class Dev:\n" +
            "    def __init__(self):\n" +
            "        self._setup()\n" +
            "    def _setup(self):\n" +
            "        self._reset()\n" +
            "    def _reset(self):\n" +
            "        self.state = 0x12345678\n" +
            "def main():\n" +
            "    d = Dev()\n" +
            "    out = d.state\n" +
            "main()\n");
        Optimizer.UnifyVariableWidths(ir);
        var vars = Field(ir, "state");
        Assert.NotEmpty(vars);
        Assert.All(vars, v => Assert.Equal(DataType.UINT32, v.Type));
    }

    [Fact]
    public void HelperUnreachableFromInit_StillCannotIntroduceAField()
    {
        // The typo-safety net: a method no `self.<m>()` path from __init__ reaches is
        // post-construction code, and a novel `self.<name>` write there stays the same
        // refusal it always was.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self._x = 0\n" +
            "    def update(self):\n" +
            "        self._y = 1\n" +
            "def main():\n" +
            "    b = Box()\n" +
            "    b.update()\n" +
            "main()\n"));
        Assert.Contains("_y", ex.Message);
    }

    [Fact]
    public void FieldFromAnnotatedSelfCall_TakesTheDeclaredReturnWidth()
    {
        // `self.v = self._read()` with `-> uint16` declares the same width an explicit
        // `self.v: uint16 = ...` would: the callee's contract is the evidence.
        var ir = Gen(
            "from pymcu.types import uint16\n" +
            "class Dev:\n" +
            "    def __init__(self):\n" +
            "        self.vread = self._read()\n" +
            "    def _read(self) -> uint16:\n" +
            "        return 300\n" +
            "def main():\n" +
            "    d = Dev()\n" +
            "    out = d.vread\n" +
            "main()\n");
        Optimizer.UnifyVariableWidths(ir);
        var vars = Field(ir, "vread");
        Assert.NotEmpty(vars);
        Assert.All(vars, v => Assert.Equal(DataType.UINT16, v.Type));
    }

    [Fact]
    public void HelperCalledFromInit_IntroducesAFieldInsideALoop()
    {
        // set_pulse_width_range's shape (adafruit_motor.servo): the constructor factors
        // its setup into a helper, and the field only exists inside the helper's loop.
        var ir = Gen(
            "from pymcu.types import uint16\n" +
            "class Servo:\n" +
            "    def __init__(self):\n" +
            "        self._init_ranges()\n" +
            "    def _init_ranges(self):\n" +
            "        for i in range(2):\n" +
            "            self._min_duty = i * 70000\n" +
            "def main():\n" +
            "    s = Servo()\n" +
            "    y: uint16 = s._min_duty\n" +
            "main()\n");
        Optimizer.UnifyVariableWidths(ir);
        var vars = Field(ir, "_min_duty");
        Assert.NotEmpty(vars);
        Assert.All(vars, v => Assert.Equal(DataType.UINT32, v.Type));
    }
}

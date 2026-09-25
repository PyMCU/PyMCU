using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Class patterns in match/case (issue #173).
///
/// A call cannot appear in a pattern in Python, so `case Point(...)` is a class pattern and
/// nothing else. It used to be lowered as an expression: the keyword form became a
/// constructor CALL and asked for the argument it was missing, and the positional form
/// resolved its capture names as reads and reported the very names the pattern binds as
/// undefined.
///
/// There is no runtime type tag on this target, so the isinstance half is decided at compile
/// time and only the sub-patterns cost anything.
/// </summary>
public class ClassPatternTests
{
    private static ProgramIR Gen(string src)
    {
        var ast = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(),
                                          new DeviceConfig { Arch = "avr" });
    }

    private const string PointNoArgs =
        "class Point:\n" +
        "    def __init__(self, x: uint8, y: uint8):\n" +
        "        self.x: uint8 = x\n" +
        "        self.y: uint8 = y\n";

    private const string PointWithArgs =
        "class Point:\n" +
        "    __match_args__ = (\"x\", \"y\")\n" +
        "\n" +
        "    def __init__(self, x: uint8, y: uint8):\n" +
        "        self.x: uint8 = x\n" +
        "        self.y: uint8 = y\n";

    private static List<Instruction> MainBody(ProgramIR ir)
        => Assert.Single(ir.Functions, f => f.Name == "main").Body;

    // ---- the two valid forms -------------------------------------------------------------

    [Fact]
    public void AKeywordSubPattern_ComparesTheFieldInsteadOfCallingTheConstructor()
    {
        var ir = Gen(PointNoArgs +
            "\ndef main():\n" +
            "    p = Point(3, 5)\n" +
            "    q: uint8 = 0\n" +
            "    match p:\n" +
            "        case Point(x=3):\n" +
            "            q = 1\n" +
            "        case _:\n" +
            "            q = 2\n");

        // 3 == 3 folds, so the taken branch is the one that assigns 1.
        Assert.Contains(MainBody(ir), i =>
            i is Copy { Src: Constant { Value: 1 }, Dst: Variable { Name: "main.q" } });
    }

    [Fact]
    public void APositionalSubPattern_BindsTheFieldNamedByMatchArgs()
    {
        var ir = Gen(PointWithArgs +
            "\ndef main():\n" +
            "    p = Point(3, 5)\n" +
            "    total: uint8 = 0\n" +
            "    match p:\n" +
            "        case Point(a, b):\n" +
            "            total = a + b\n");

        // The first positional sub-pattern binds Point.x and the second Point.y, which is
        // the order __match_args__ gives. Reversed, this program would add 5 to itself.
        Assert.Contains(MainBody(ir), i =>
            i is Copy { Src: Constant { Value: 3 }, Dst: Variable { Name: "main.a" } });
        Assert.Contains(MainBody(ir), i =>
            i is Copy { Src: Constant { Value: 5 }, Dst: Variable { Name: "main.b" } });
    }

    [Fact]
    public void AMixedPattern_TestsTheValueAndBindsTheName()
    {
        var ir = Gen(PointWithArgs +
            "\ndef main():\n" +
            "    p = Point(3, 5)\n" +
            "    got: uint8 = 0\n" +
            "    match p:\n" +
            "        case Point(3, b):\n" +
            "            got = b\n");

        // The value sub-pattern becomes a comparison on Point.x, the capture a bind of Point.y.
        Assert.Contains(MainBody(ir), i => i is Binary { Op: PyMCU.IR.BinaryOp.Equal });
        Assert.Contains(MainBody(ir), i =>
            i is Copy { Src: Constant { Value: 5 }, Dst: Variable { Name: "main.b" } });
        Assert.DoesNotContain(MainBody(ir), i =>
            i is Copy { Dst: Variable { Name: "main.3" } });
    }

    // ---- what must be refused, and how --------------------------------------------------

    // CPython raises "Point() accepts 0 positional sub-patterns (2 given)" for this. Reading
    // the field layout instead would accept a program CPython rejects.
    [Fact]
    public void APositionalPatternWithoutMatchArgs_NamesMatchArgs()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(PointNoArgs +
            "\ndef main():\n" +
            "    p = Point(3, 5)\n" +
            "    match p:\n" +
            "        case Point(a, b):\n" +
            "            p.x = a\n"));

        Assert.Contains("positional sub-pattern", ex.Message);
        Assert.Contains("__match_args__", ex.Message);
        Assert.DoesNotContain("is not defined", ex.Message);
    }

    [Fact]
    public void TheOldMessagesAreGone()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(PointNoArgs +
            "\ndef main():\n" +
            "    p = Point(3, 5)\n" +
            "    match p:\n" +
            "        case Point(a, b):\n" +
            "            p.x = a\n"));

        // The two the issue reports: a capture name blamed as an undefined read, and the
        // pattern read as a constructor call.
        Assert.DoesNotContain("name 'a' is not defined", ex.Message);
        Assert.DoesNotContain("in call to constructor", ex.Message);
    }

    [Fact]
    public void AClassPatternOverASubjectOfAnotherClass_IsADeadCase()
    {
        var ir = Gen(PointNoArgs +
            "\nclass Other:\n" +
            "    def __init__(self, z: uint8):\n" +
            "        self.z: uint8 = z\n" +
            "\ndef main():\n" +
            "    p = Point(3, 5)\n" +
            "    q: uint8 = 0\n" +
            "    match p:\n" +
            "        case Other(z=1):\n" +
            "            q = 1\n" +
            "        case _:\n" +
            "            q = 2\n");

        Assert.Contains(MainBody(ir), i =>
            i is Copy { Src: Constant { Value: 2 }, Dst: Variable { Name: "main.q" } });
        Assert.DoesNotContain(MainBody(ir), i =>
            i is Copy { Src: Constant { Value: 1 }, Dst: Variable { Name: "main.q" } });
    }

    // ---- the control the issue used ------------------------------------------------------

    [Fact]
    public void AValuePattern_StillCompiles()
    {
        var ir = Gen(
            "def main():\n" +
            "    seed: uint8 = 0\n" +
            "    q: uint8 = 0\n" +
            "    match seed:\n" +
            "        case 0:\n" +
            "            q = 1\n" +
            "        case _:\n" +
            "            q = 2\n");

        Assert.Contains(MainBody(ir), i =>
            i is Copy { Src: Constant { Value: 1 }, Dst: Variable { Name: "main.q" } });
    }

    // ---- a capture is a binding, so it takes the key every read of the name resolves to ----
    //
    // VisitClassPattern qualified the bind target with currentFunction alone. At module level
    // that minted `main.px` while every read of `px` answered the module global: the arm bound
    // nothing, printed the global's old value, and the firmware looked right. CPython binds the
    // global there, so the bare spelling is the correct key at module level and the qualified
    // one inside a function -- the distinction QualifyBoundName now makes for both loop
    // variables and captures.

    [Fact]
    public void AModuleLevelCaptureThatCollidesWithAGlobal_WritesTheGlobalsKey()
    {
        var ir = Gen(PointWithArgs +
            "\npx: uint8 = 99\n" +
            "p = Point(3, 5)\n" +
            "match p:\n" +
            "    case Point(x=px):\n" +
            "        p.y = px\n");

        Assert.Contains(MainBody(ir), i =>
            i is Copy { Src: Constant { Value: 3 }, Dst: Variable { Name: "px" } });
        Assert.DoesNotContain(MainBody(ir), i =>
            i is Copy { Dst: Variable { Name: "main.px" } });
    }

    // A capture WRITES the name, so the folded value the shadowed global carried is stale for
    // the rest of the arm. It outlived the bind, so `out = px` stored 99 where the capture
    // holds 3.
    [Fact]
    public void AModuleLevelCapture_DropsTheFoldedValueOfTheNameItShadows()
    {
        var ir = Gen(PointWithArgs +
            "\npx: uint8 = 99\n" +
            "out: uint8 = 0\n" +
            "p = Point(3, 5)\n" +
            "match p:\n" +
            "    case Point(x=px):\n" +
            "        out = px\n");

        // The store reads the capture instead of folding the shadowed global's 99.
        Assert.Contains(MainBody(ir), i =>
            i is Copy { Src: Variable { Name: "px" }, Dst: Variable { Name: "out" } });
        Assert.DoesNotContain(MainBody(ir), i =>
            i is Copy { Src: Constant { Value: 99 }, Dst: Variable { Name: "out" } });
    }

    // The control for the rule above: the module-level case must not leak into function scope.
    // No global claims `px` there, so the reads resolve `f.px` and the bind writes that key.
    [Fact]
    public void ACaptureInsideAFunction_KeepsTheQualifiedKey()
    {
        var ir = Gen(PointWithArgs +
            "\nq = Point(3, 5)\n" +
            "\ndef f() -> uint8:\n" +
            "    px: uint8 = 4\n" +
            "    match q:\n" +
            "        case Point(x=px):\n" +
            "            return px\n" +
            "    return px\n" +
            "\nr: uint8 = f()\n");

        var body = Assert.Single(ir.Functions, fn => fn.Name == "f").Body;
        Assert.Contains(body, i => i is Copy { Dst: Variable { Name: "f.px" } });
        Assert.DoesNotContain(body, i => i is Return { Value: Constant { Value: 4 } });
    }

    // The only one of the seven scope qualifiers that never consulted currentInlinePrefix, so
    // two expansions of one @inline wrote a single capture slot and the second overwrote the
    // first.
    [Fact]
    public void ACaptureInsideAnInline_IsKeyedByItsExpansion()
    {
        var ir = Gen(PointWithArgs +
            "\n@inline\n" +
            "def show(p: Point) -> None:\n" +
            "    match p:\n" +
            "        case Point(x=px):\n" +
            "            p.y = px\n" +
            "\na = Point(3, 5)\n" +
            "b = Point(4, 6)\n" +
            "show(a)\n" +
            "show(b)\n");

        var keys = MainBody(ir)
            .OfType<Copy>()
            .Select(c => c.Dst)
            .OfType<Variable>()
            .Select(v => v.Name)
            .Where(n => n.EndsWith(".px", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        Assert.DoesNotContain("show.px", keys);
        Assert.DoesNotContain("main.px", keys);
    }

    // A sub-pattern capture holds the FIELD's value, a scalar. Inheriting the instanceClasses
    // entry of the global it shadows refused `f"{px}"` inside the arm as "an instance of
    // 'Point'", a spurious rejection of a program CPython runs.
    [Fact]
    public void ACaptureThatShadowsAnInstanceGlobal_IsNotItselfAnInstance()
    {
        var ir = Gen(PointWithArgs +
            "\npx = Point(1, 2)\n" +
            "p = Point(3, 5)\n" +
            "match p:\n" +
            "    case Point(x=px):\n" +
            "        p.y = px\n");

        Assert.Contains(MainBody(ir), i =>
            i is Copy { Src: Constant { Value: 3 }, Dst: Variable { Name: "px" } });
    }

    // `case Point() as p` on the subject `p` itself resolves the capture to the key the
    // subject already holds. Filing `p -> p` there is a SELF-alias, and the alias chases that
    // carry no depth guard spin on it: the compiler hung instead of emitting.
    [Fact]
    public void AnAsCaptureOfTheSubjectItself_FilesNoSelfAlias()
    {
        var ir = Gen(PointWithArgs +
            "\np = Point(3, 5)\n" +
            "match p:\n" +
            "    case Point() as p:\n" +
            "        p.y = p.x\n");

        Assert.NotEmpty(MainBody(ir));
    }
}

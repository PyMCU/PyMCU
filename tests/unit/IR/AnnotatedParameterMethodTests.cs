using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A method call on a parameter annotated with an imported class:
/// <c>def use(b: busio.I2C): b.try_lock()</c>. The receiver's class comes from the
/// annotation, and the instance comes from <c>board.I2C()</c> -- an unannotated factory
/// function (<c>def I2C(): return _board_i2c(SCL, SDA)</c>) in a third module. The declared
/// return type never named the class, so nothing force-inlined the factory and the
/// assignment target never learned it; the call then flattened the receiver's own NAME and
/// asked for the undefined <c>i2c_try_lock</c>.
///
/// Main's fix (4da95deb) scans a function body for a returned ZCA construction and
/// force-inlines the factory; these tests pin the annotated-parameter call sites that
/// the bug was found on.
/// </summary>
public class AnnotatedParameterMethodTests
{
    private static ProgramIR Gen(string mainSrc, params (string Name, string Source)[] modules)
    {
        var imported = new Dictionary<string, ProgramNode>();
        foreach (var (name, source) in modules)
            imported[name] = new Parser(new Lexer(source).Tokenize()).ParseProgram();
        return new IRGenerator().Generate(
            new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram(),
            imported, new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string>(modules.Select(m => m.Name)));
    }

    // A write of the lock flag proves try_lock's body expanded against the caller's
    // instance -- the dispatch worked, not just that nothing threw.
    private static bool LocksTheInstance(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Any(c => c.Src is Constant { Value: 1 }
                      && c.Dst is Variable v && v.Name.EndsWith("_locked"));

    // inner.Inner: a nested ZCA field, so bus.I2C is a multi-field instance like the
    // real busio.I2C (self._bus + self._locked).
    private const string Inner =
        "from pymcu.types import uint8, inline\n" +
        "class Inner:\n" +
        "    @inline\n" +
        "    def __init__(self):\n" +
        "        self.v: uint8 = 0\n";

    private const string Bus =
        "from pymcu.types import uint8, inline\n" +
        "from inner import Inner\n" +
        "class I2C:\n" +
        "    @inline\n" +
        "    def __init__(self):\n" +
        "        self._bus = Inner()\n" +
        "        self._locked: uint8 = 0\n" +
        "    @inline\n" +
        "    def try_lock(self) -> uint8:\n" +
        "        if self._locked == 0:\n" +
        "            self._locked = 1\n" +
        "            return 1\n" +
        "        return 0\n";

    // board.I2C(): the generated board.py spelling -- a plain function with no return
    // annotation whose body constructs the imported class under an alias.
    private const string Board =
        "from bus import I2C as _board_i2c\n" +
        "def I2C():\n" +
        "    return _board_i2c()\n";

    [Fact]
    public void MethodCall_OnDottedAnnotatedParam_AfterFactoryConstruction_Resolves()
    {
        var act = () => Gen(
            "from pymcu.types import uint8, inline\n" +
            "import board\n" +
            "import bus\n" +
            "def use(b: bus.I2C):\n" +
            "    b.try_lock()\n" +
            "i2c = board.I2C()\n" +
            "use(i2c)\n",
            ("inner", Inner), ("bus", Bus), ("board", Board));

        var ir = act.Should().NotThrow(
            because: "b: bus.I2C names the class; the method must not flatten to i2c_try_lock").Subject;
        LocksTheInstance(ir).Should().BeTrue(
            because: "try_lock writes self._locked on the instance board.I2C() built");
    }

    [Fact]
    public void MethodCall_OnFromImportAnnotatedParam_AfterFactoryConstruction_Resolves()
    {
        var act = () => Gen(
            "from pymcu.types import uint8, inline\n" +
            "import board\n" +
            "from bus import I2C\n" +
            "def use(b: I2C):\n" +
            "    b.try_lock()\n" +
            "i2c = board.I2C()\n" +
            "use(i2c)\n",
            ("inner", Inner), ("bus", Bus), ("board", Board));

        var ir = act.Should().NotThrow(
            because: "from bus import I2C + b: I2C is the same annotation without the dot").Subject;
        LocksTheInstance(ir).Should().BeTrue();
    }

    [Fact]
    public void MethodCall_OnTheFactoryResultItself_Resolves()
    {
        // No function parameter at all: the assignment alone must carry the class.
        var act = () => Gen(
            "from pymcu.types import uint8, inline\n" +
            "import board\n" +
            "i2c = board.I2C()\n" +
            "i2c.try_lock()\n",
            ("inner", Inner), ("bus", Bus), ("board", Board));

        var ir = act.Should().NotThrow(
            because: "i2c = board.I2C() tags the target even without a parameter to bind").Subject;
        LocksTheInstance(ir).Should().BeTrue();
    }

    [Fact]
    public void MethodCall_OnDottedAnnotatedParam_AfterSingleFieldFactoryConstruction_Resolves()
    {
        // Single-field ZCA: a declared `-> I2C` factory stays outlined and returns the
        // packed field as a handle (RFC 0001 Model B), but this factory declares
        // nothing, so the body scan force-inlines it like the multi-field case.
        // Either way the call must resolve to the class, not the receiver's name.
        var act = () => Gen(
            "from pymcu.types import uint8, inline\n" +
            "import board\n" +
            "import bus\n" +
            "def use(b: bus.I2C):\n" +
            "    b.try_lock()\n" +
            "i2c = board.I2C()\n" +
            "use(i2c)\n",
            ("bus",
                "from pymcu.types import uint8, inline\n" +
                "class I2C:\n" +
                "    @inline\n" +
                "    def __init__(self):\n" +
                "        self._locked: uint8 = 0\n" +
                "    @inline\n" +
                "    def try_lock(self) -> uint8:\n" +
                "        if self._locked == 0:\n" +
                "            self._locked = 1\n" +
                "            return 1\n" +
                "        return 0\n"),
            ("board", Board));

        var ir = act.Should().NotThrow(
            because: "an unannotated single-field factory inlines too; the class tag is still owed").Subject;
        LocksTheInstance(ir).Should().BeTrue();
    }
}

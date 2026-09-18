using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A <c>for</c> over a tuple of already-constructed instances unrolls the same way
/// <c>for p in self._pins</c> does. adafruit_character_lcd writes
/// <c>for pin in (reset_dio, enable_dio, d4_dio, d5_dio, d6_dio, d7_dio)</c>.
/// </summary>
public class ForOverInstanceTupleTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static ProgramIR GenWithModules(string mainSrc, params (string Name, string Source)[] modules)
    {
        var imported = new Dictionary<string, ProgramNode>();
        foreach (var (name, source) in modules)
            imported[name] = new Parser(new Lexer(source).Tokenize()).ParseProgram();

        var mainAst = new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram();
        var ctx = new PyMCU.Common.CompilationContext(new CompilerOptions(
            FilePath: "main.py", OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        foreach (var (name, _) in modules) ctx.ProjectModules.Add(name);

        return new IRGenerator().Generate(mainAst, imported, new DeviceConfig { Arch = "avr" },
                                          projectModules: ctx.ProjectModules);
    }

    private const string Pin =
        "from pymcu.types import uint8\n" +
        "class Pin:\n" +
        "    def __init__(self, n: uint8):\n" +
        "        self.n = n\n" +
        "    def bump(self):\n" +
        "        self.n = self.n + 1\n" +
        "\n";

    [Fact]
    public void ATupleOfNamedInstances_UnrollsInsideInit()
    {
        var ir = Gen(Pin +
            "class Lcd:\n" +
            "    def __init__(self, a: Pin, b: Pin):\n" +
            "        for p in (a, b):\n" +
            "            p.bump()\n" +
            "        self.a = a\n" +
            "        self.b = b\n" +
            "def main():\n" +
            "    x = Pin(0)\n" +
            "    y = Pin(0)\n" +
            "    l = Lcd(x, y)\n");

        ir.Functions.Should().NotBeEmpty(
            because: "for pin in (reset_dio, enable_dio, ...) unrolls over the named instances");
    }

    [Fact]
    public void AListOfNamedInstances_UnrollsTheSameWay()
    {
        var ir = Gen(Pin +
            "def main():\n" +
            "    x = Pin(0)\n" +
            "    y = Pin(0)\n" +
            "    for p in [x, y]:\n" +
            "        p.bump()\n");

        ir.Functions.Should().NotBeEmpty(
            because: "a list of named instances is the same hoist a tuple of them is");
    }

    [Fact]
    public void APropertySetterThroughTheLoopVar_Compiles()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "class Pin:\n" +
            "    def __init__(self):\n" +
            "        self._d = 0\n" +
            "    @property\n" +
            "    def direction(self):\n" +
            "        return self._d\n" +
            "    @direction.setter\n" +
            "    def direction(self, d: uint8):\n" +
            "        self._d = d\n" +
            "class Lcd:\n" +
            "    def __init__(self, a: Pin, b: Pin):\n" +
            "        for p in (a, b):\n" +
            "            p.direction = 1\n" +
            "def main():\n" +
            "    Lcd(Pin(), Pin())\n");

        ir.Functions.Should().NotBeEmpty(
            because: "pin.direction = OUTPUT through a for-unrolled instance is the setter, not a method");

        var dirStores = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("_d", StringComparison.Ordinal)
                        && c.Src is Constant { Value: 1 })
            .ToList();
        dirStores.Should().NotBeEmpty(
            because: "the setter must write 1 into each pin's _d, not a discarded loop-var copy");
    }

    [Fact]
    public void AMultiFieldImportedPropertySetterThroughTheLoopVar_Compiles()
    {
        // adafruit_character_lcd: DigitalInOut lives in another module, has several fields,
        // and the constructor writes pin.direction = OUTPUT through the unrolled tuple.
        const string dio =
            "from pymcu.types import uint8\n" +
            "class Direction:\n" +
            "    OUTPUT = 1\n" +
            "class Inner:\n" +
            "    def __init__(self, n: uint8):\n" +
            "        self.n = n\n" +
            "    def mode(self, m: uint8):\n" +
            "        self.n = m\n" +
            "class DigitalInOut:\n" +
            "    def __init__(self, n: uint8):\n" +
            "        self._pin = Inner(n)\n" +
            "        self._direction = 0\n" +
            "        self._pull_mode = 0\n" +
            "        self._drive_mode = 0\n" +
            "    @property\n" +
            "    def direction(self):\n" +
            "        return self._direction\n" +
            "    @direction.setter\n" +
            "    def direction(self, d: uint8):\n" +
            "        self._direction = d\n" +
            "        self._pin.mode(d)\n";

        var ir = GenWithModules(
            "from dio import DigitalInOut, Direction\n" +
            "class Lcd:\n" +
            "    def __init__(self, a: DigitalInOut, b: DigitalInOut):\n" +
            "        for pin in (a, b):\n" +
            "            pin.direction = Direction.OUTPUT\n" +
            "def main():\n" +
            "    Lcd(DigitalInOut(1), DigitalInOut(2))\n",
            ("dio", dio));

        ir.Functions.Should().NotBeEmpty(
            because: "an imported DigitalInOut's direction setter is the same property through a for-unrolled pin");
    }

    [Fact]
    public void AnOutlinedConstructorWithClassTypedPinParams_Unrolls()
    {
        // adafruit_character_lcd.Character_LCD.__init__ stores several fields and then
        // writes `for pin in (reset_dio, ...)`: enough layout that the constructor can
        // compile as a shared subroutine, so the pins have to be instances from their
        // annotation, not only from an @inline call-site binding.
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "class Pin:\n" +
            "    def __init__(self):\n" +
            "        self._d = 0\n" +
            "        self._a = 0\n" +
            "        self._b = 0\n" +
            "        self._c = 0\n" +
            "    @property\n" +
            "    def direction(self):\n" +
            "        return self._d\n" +
            "    @direction.setter\n" +
            "    def direction(self, d: uint8):\n" +
            "        self._d = d\n" +
            "class Lcd:\n" +
            "    def __init__(self, a: Pin, b: Pin, columns: uint8, lines: uint8):\n" +
            "        self.columns = columns\n" +
            "        self.lines = lines\n" +
            "        self.reset = a\n" +
            "        self.enable = b\n" +
            "        self._n = 0\n" +
            "        self._m = 0\n" +
            "        for pin in (a, b):\n" +
            "            pin.direction = 1\n" +
            "def main():\n" +
            "    Lcd(Pin(), Pin(), 16, 2)\n");

        ir.Functions.Should().NotBeEmpty(
            because: "a class-typed pin parameter is an instance even inside an outlined constructor");
    }
}

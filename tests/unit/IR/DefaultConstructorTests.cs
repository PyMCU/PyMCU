using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#391. A class that declares no `__init__` method is constructible, matching CPython's
/// synthesized no-op default constructor.
///
/// The refusal this replaces fired at every FIRST construction of such a class rather than at
/// its definition, because `Foo()` resolving to a class with nothing registered under
/// `Foo___init__` was indistinguishable, at that call site, from a typo or a missing import --
/// so the message read `class 'Foo' cannot be constructed`, correct about what was missing and
/// wrong about the program: nothing is missing from a class Python itself accepts unmodified.
///
/// The fix is scanned once per class, after every base's methods (an inherited real `__init__`
/// included) have already been copied in: only a class that inherits none of its own gets the
/// synthesized one, so a subclass that relies on its base's constructor is untouched.
/// </summary>
public class DefaultConstructorTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8\n\n\n";

    [Fact]
    public void AClassWithNoInitBuildsAndConstructsCleanly()
    {
        // The minimal shape from #391: only methods, no state, no explicit __init__.
        var ir = Gen(Preamble +
            "class Sensor:\n" +
            "    def read(self, raw: uint8) -> uint8:\n" +
            "        return raw\n\n" +
            "def main():\n" +
            "    s = Sensor()\n" +
            "    GPIOR1.value = s.read(GPIOR0.value)\n");

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void AClassAttributeReadThroughAnInstanceStillWorksWithNoInit()
    {
        // #391's second reproducer: a class-level constant read via an instance, still with
        // no __init__ anywhere in the class.
        var ir = Gen(Preamble +
            "class Device:\n" +
            "    SCALE: uint8 = 5\n" +
            "    def read(self) -> uint8:\n" +
            "        return Device.SCALE + 1\n\n" +
            "def main():\n" +
            "    d = Device()\n" +
            "    GPIOR1.value = d.read()\n");

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void ASubclassWithNoInitStillUsesTheBaseConstructor()
    {
        // The synthesis must not shadow real inheritance: a subclass that declares no
        // __init__ of its own, but whose base does, keeps using the base's -- it must not
        // get a fresh no-op instead, which would silently drop the base's field.
        var ir = Gen(Preamble +
            "class Base:\n" +
            "    def __init__(self, v: uint8) -> None:\n" +
            "        self._v: uint8 = v\n" +
            "    def get(self) -> uint8:\n" +
            "        return self._v\n\n" +
            "class Derived(Base):\n" +
            "    def double(self) -> uint8:\n" +
            "        return self.get() + self.get()\n\n" +
            "def main():\n" +
            "    d = Derived(GPIOR0.value)\n" +
            "    GPIOR1.value = d.double()\n");

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }
}

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#454. A with-block's context manager can hand back an instance of a DIFFERENT class than its
/// own: adafruit_bus_device's `SPIDevice.__enter__(self) -> SPI: ... return self.spi` returns
/// a `busio.SPI`, not the `SPIDevice` itself. `with self._spi_device as spi: spi
/// .write_readinto(...)` -- adafruit_mcp3xxx's own shape -- bound `spi` to a nameless scalar
/// instead of an `SPI` instance, so the first method called on it mangled to an undefined
/// free function. The sibling of #390, which fixed the with-bound name losing its OWN
/// manager's class; this is the same loss one level further, when `__enter__` reads a field.
/// </summary>
public class WithEnterReturnsAnotherClassTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Main(string src) => Gen(src).Functions.Single(f => f.Name == "main").Body;

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8\n\n\n";

    private const string Classes =
        "class Inner:\n" +
        "    def __init__(self, v: uint8) -> None:\n" +
        "        self._v: uint8 = v\n\n" +
        "    def value(self) -> uint8:\n" +
        "        return self._v\n\n" +
        "class Manager:\n" +
        "    def __init__(self, v: uint8) -> None:\n" +
        "        self.inner: Inner = Inner(v)\n\n" +
        "    def __enter__(self) -> Inner:\n" +
        "        return self.inner\n\n" +
        "    def __exit__(self) -> None:\n" +
        "        pass\n\n";

    [Fact]
    public void AtModuleLevel_TheBoundNameCallsAMethodOnTheReturnedFieldsClass()
    {
        var body = Main(Preamble + Classes +
            "mgr = Manager(11)\n" +
            "with mgr as inner:\n" +
            "    GPIOR1.value = inner.value()\n");

        // A real CALL to Inner's own value() -- not a mangled "mgr_inner_value" free function,
        // and not a scalar copy of whatever the with-header's own temporary held.
        Assert.Contains(body, i => i is Call { FunctionName: "Inner_value" });
    }

    [Fact]
    public void InsideAMethod_TheBoundNameCallsAMethodOnTheReturnedFieldsClass()
    {
        var body = Main(Preamble + Classes +
            "class Owner:\n" +
            "    def __init__(self, v: uint8) -> None:\n" +
            "        self._mgr: Manager = Manager(v)\n\n" +
            "    def read(self) -> uint8:\n" +
            "        with self._mgr as inner:\n" +
            "            return inner.value()\n\n" +
            "o = Owner(13)\n" +
            "GPIOR1.value = o.read()\n");

        Assert.Contains(body, i => i is Call { FunctionName: "Inner_value" });
    }

    [Fact]
    public void TheValueConstructedWithReachesTheCall()
    {
        // Not just "a call happens" -- the actual constructor argument (13) has to be what
        // reaches Inner.value(), the same way ComparisonRangeTests pins a fold to its value
        // rather than only its shape. A wrong-but-building reading (the with-header's own
        // discarded temporary, a stray 0) would still produce SOME call instruction.
        var body = Main(Preamble + Classes +
            "mgr = Manager(13)\n" +
            "with mgr as inner:\n" +
            "    GPIOR1.value = inner.value()\n");

        var call = body.OfType<Call>().Single(c => c.FunctionName == "Inner_value");
        Assert.Contains(call.Args, a => a is Constant { Value: 13 });
    }
}

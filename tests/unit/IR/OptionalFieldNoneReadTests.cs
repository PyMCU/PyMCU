using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `obj.field is None` on a module-level instance read the None mark under the
/// function-qualified name (`main.d1_chip_select`) while the write had flattened it
/// under the bare global (`d1_chip_select`), so the fold answered False and a
/// `chip_select: Optional[DigitalInOut] = None` device looked wired when it was not
/// (adafruit_bus_device.SPIDevice). The member read now falls back to the bare
/// object name, mirroring the fallback the plain-name arm of IsNoneValued has.
/// </summary>
public class OptionalFieldNoneReadTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Program =
        "class Pin:\n" +
        "    def __init__(self, n: int):\n" +
        "        self.n = n\n\n" +
        "class Dev:\n" +
        "    def __init__(self, cs=None):\n" +
        "        self.chip_select = cs\n\n" +
        "d1 = Dev()\n" +
        "x = 1 if d1.chip_select is None else 2\n" +
        "d2 = Dev(Pin(10))\n" +
        "y = 1 if d2.chip_select is None else 2\n";

    private static bool IsConstCopy(Instruction i, int value, string dst) =>
        i is Copy c && c.Src is Constant k && k.Value == value
                    && c.Dst is Variable v && v.Name == dst;

    [Fact]
    public void AFieldThatReceivedANoneParam_ReadsNone()
    {
        var main = Gen(Program).Functions.Single(f => f.Name == "main");
        main.Body.Any(i => IsConstCopy(i, 1, "x")).Should().BeTrue(
            "d1.chip_select is None folds True");
    }

    [Fact]
    public void AFieldThatReceivedAnInstance_ReadsNotNone()
    {
        var main = Gen(Program).Functions.Single(f => f.Name == "main");
        main.Body.Any(i => IsConstCopy(i, 2, "y")).Should().BeTrue(
            "d2.chip_select is None folds False — the field holds a real Pin");
    }
}

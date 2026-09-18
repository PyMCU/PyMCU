using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>Name = namedtuple("Name", ("a", "b"))</c> is a compile-time class factory:
/// the assignment becomes a ZCA class with those fields. adafruit_irremote writes
/// <c>IRMessage = namedtuple("IRMessage", ("pulses", "code"))</c> and then
/// constructs with a keyword (<c>reason="Too short"</c>) and asks
/// <c>isinstance(message, IRMessage)</c>.
///
/// IR generation does not load <c>collections.py</c>; a callee named
/// <c>namedtuple</c> is the factory. Driver and AVR fixtures import the real
/// module.
/// </summary>
public class NamedtupleTests
{
    private static ProgramIR Gen(string src)
    {
        var config = new DeviceConfig { Arch = "avr", Chip = "atmega328p" };
        var program = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        new ConditionalCompilator(config).Process(program);
        var ir = new IRGenerator().Generate(
            program, new Dictionary<string, ProgramNode>(), config);
        return Optimizer.Optimize(ir);
    }

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    private static Val LastStored(ProgramIR ir, int slot) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    private const string Point =
        "from pymcu.types import uint8\n" +
        "Point = namedtuple(\"Point\", (\"x\", \"y\"))\n" +
        "buf = bytearray(4)\n";

    [Fact]
    public void PositionalFieldsStoreOnTheInstance()
    {
        var ir = Gen(Point +
            "p = Point(3, 5)\n" +
            "buf[0] = p.x\n" +
            "buf[1] = p.y\n");

        LastStored(ir, 0).Should().Be(new Constant(3),
            because: "namedtuple fields are the constructor arguments, accessed by name");
        LastStored(ir, 1).Should().Be(new Constant(5),
            because: "the second field is y, matching the field_names order");
    }

    [Fact]
    public void KeywordConstructionBindsTheNamedField()
    {
        var ir = Gen(Point +
            "p = Point(3, y=5)\n" +
            "buf[0] = p.y\n");

        LastStored(ir, 0).Should().Be(new Constant(5),
            because: "adafruit_irremote writes UnparseableIRMessage(..., reason='Too short')");
    }

    [Fact]
    public void IsInstanceOfTheBoundNameIsTrue()
    {
        var ir = Gen(Point +
            "p = Point(3, 5)\n" +
            "buf[0] = 1 if isinstance(p, Point) else 0\n");

        LastStored(ir, 0).Should().Be(new Constant(1),
            because: "isinstance(message, IRMessage) folds: the instance's class is the bound name");
    }

    [Fact]
    public void StringFieldNamesSplitTheSameWay()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "Point = namedtuple(\"Point\", \"x y\")\n" +
            "buf = bytearray(1)\n" +
            "p = Point(3, 5)\n" +
            "buf[0] = p.x\n");

        LastStored(ir, 0).Should().Be(new Constant(3),
            because: "CPython accepts a space-separated field-name string");
    }

    [Fact]
    public void SingleFieldTupleIsAOneFieldClass()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "NEC = namedtuple(\"NECRepeatIRMessage\", (\"pulses\",))\n" +
            "buf = bytearray(1)\n" +
            "m = NEC(11)\n" +
            "buf[0] = m.pulses\n");

        LastStored(ir, 0).Should().Be(new Constant(11),
            because: "adafruit_irremote's NECRepeatIRMessage has one field, spelled ('pulses',)");
    }

    [Fact]
    public void TheTypenameStringIsNotTheClass()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "Unparseable = namedtuple(\"IRMessage\", (\"pulses\", \"reason\"))\n" +
            "buf = bytearray(1)\n" +
            "m = Unparseable(1, 2)\n" +
            "buf[0] = 1 if isinstance(m, Unparseable) else 0\n");

        LastStored(ir, 0).Should().Be(new Constant(1),
            because: "the assignment target is the class, not the typename string");
    }

    [Fact]
    public void ADuplicateFieldNameIsRefused()
    {
        Refusal(
            "P = namedtuple(\"P\", (\"x\", \"x\"))\n" +
            "def main():\n" +
            "    pass\n")
            .Should().Contain("duplicated",
                because: "two fields with the same name would collide on the instance");
    }
}

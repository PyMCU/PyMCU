using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A bound METHOD stored on an instance field -- `self._readbit =
/// self._ow.read_bit` -- is a compile-time pair (receiver, method). The call
/// `self._readbit(args)` re-emits `<recv>.read_bit(args)` spelled with the
/// receiver's terminal instance key, so the generic member-call path resolves
/// it exactly as the direct spelling: an outlined callee gets a real call with
/// the receiver as first argument. adafruit_onewire's OneWireBus caches both
/// pin methods this way.
/// </summary>
public class BoundMethodFieldTests
{
    private const string Prelude =
        "from pymcu.types import uint8\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n";

    private static ProgramIR Gen(string src, bool optimize = true)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
        return optimize ? Optimizer.Optimize(ir) : ir;
    }

    private const string Fixture =
        "class Pin:\n" +
        "    def __init__(self, x: uint8) -> None:\n" +
        "        self.x: uint8 = x\n" +
        "    def write_bit(self, b: uint8) -> None:\n" +
        "        self.x = b\n" +
        "    def read_bit(self) -> uint8:\n" +
        "        return self.x\n\n" +
        "class Bus:\n" +
        "    def __init__(self, p: Pin) -> None:\n" +
        "        self._ow = p\n" +
        "        self._rb = self._ow.read_bit\n" +
        "        self._wb = self._ow.write_bit\n" +
        "    def probe(self, v: uint8) -> uint8:\n" +
        "        self._wb(v)\n" +
        "        return self._rb()\n\n";

    [Fact]
    public void AFieldBoundToAMethod_CallsItWithTheRecordedReceiver()
    {
        var ir = Gen(Fixture +
            "b = Bus(Pin(GPIOR0.value))\n" +
            "r = b.probe(3)\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        main.Body.Any(i => i is Call c && c.FunctionName == "Pin_write_bit").Should().BeTrue(
            "self._wb(v) dispatches to Pin.write_bit, not a mangled field name");
        main.Body.Any(i => i is Call c && c.FunctionName == "Pin_read_bit").Should().BeTrue(
            "self._rb() dispatches to Pin.read_bit");
        ir.Functions.Any(f => f.Name == "Pin_read_bit").Should().BeTrue(
            "the outlined callee is emitted");
        ir.Functions.Any(f => f.Name == "Pin_write_bit").Should().BeTrue();
    }

    [Fact]
    public void AParamReceiver_ResolvesToTheArgumentPassedIn()
    {
        var ir = Gen(
            "class Pin:\n" +
            "    def __init__(self, x: uint8) -> None:\n" +
            "        self.x: uint8 = x\n" +
            "    def write_bit(self, b: uint8) -> None:\n" +
            "        self.x = b\n" +
            "    def read_bit(self) -> uint8:\n" +
            "        return self.x\n\n" +
            "class Bus:\n" +
            "    def __init__(self, p: Pin) -> None:\n" +
            "        self._rb = p.read_bit\n" +
            "    def probe(self) -> uint8:\n" +
            "        return self._rb()\n\n" +
            "b = Bus(Pin(GPIOR0.value))\n" +
            "r = b.probe()\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        main.Body.Any(i => i is Call c && c.FunctionName == "Pin_read_bit").Should().BeTrue(
            "the receiver was recorded as its terminal key, so it resolves " +
            "inside a different method's expansion");
    }

    [Fact]
    public void APropertyReadOnTheRight_IsNotABoundMethodStore()
    {
        // `GPIOR0.value = o.frequency` is target MemberAccessExpr, value
        // MemberAccessExpr -- the same shape as `self._rb = self._ow.read_bit`
        // -- but `frequency` is a @property, so the read invokes the getter and
        // the store lands on the register. Mistaking it for a bound-method
        // binding swallowed the store entirely.
        var ir = Gen(
            "class Out:\n" +
            "    def __init__(self, x: uint8) -> None:\n" +
            "        self._x: uint8 = x\n" +
            "    @property\n" +
            "    def frequency(self) -> uint8:\n" +
            "        return self._x\n\n" +
            "o = Out(GPIOR0.value)\n" +
            "GPIOR0.value = o.frequency\n",
            optimize: false);

        var main = ir.Functions.Single(f => f.Name == "main");
        main.Body.OfType<Copy>().Count().Should().BeGreaterThanOrEqualTo(2,
            "the property read still produces a store after the constructor's " +
            "own copy -- the bound-method branch must not swallow the statement");
    }

    [Fact]
    public void RebindingAFieldToAnotherInstancesMethod_Refuses()
    {
        var act = () => Gen(
            "class Pin:\n" +
            "    def read_bit(self) -> int:\n" +
            "        return 1\n\n" +
            "class Bus:\n" +
            "    def __init__(self, p, q) -> None:\n" +
            "        self._rb = p.read_bit\n" +
            "        self._rb = q.read_bit\n\n" +
            "b = Bus(Pin(), Pin())\n");
        act.Should().Throw<Exception>().WithMessage("*dispatch table*");
    }
}

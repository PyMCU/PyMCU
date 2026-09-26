using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// adafruit_onewire's `OneWireAddress.rom` is a @property returning `self._rom`, a field
/// that was handed a bytearray. The getter's `return self._rom` lowered as a scalar read,
/// so `len(addr.rom)` and `bus.write(addr.rom)` -- whose body takes `len(buf)` -- were
/// refused as "len() argument must be a fixed-size array".
/// </summary>
public class BufferReturnedThroughFieldTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    private const string Addr =
        "from pymcu.types import uint8\n" +
        "class Addr:\n" +
        "    def __init__(self, rom: bytearray) -> None:\n" +
        "        self._rom = rom\n" +
        "    @property\n" +
        "    def rom(self) -> bytearray:\n" +
        "        return self._rom\n" +
        "class Bus:\n" +
        "    def __init__(self):\n" +
        "        self.acc = 0\n" +
        "    def write(self, buf, end=None) -> None:\n" +
        "        if end is None:\n" +
        "            end = len(buf)\n" +
        "        for i in range(0, end):\n" +
        "            self.acc += buf[i]\n" +
        "r = bytearray(3)\n" +
        "a = Addr(r)\n" +
        "bus = Bus()\n";

    [Fact]
    public void LenOfAPropertyReturningABufferFieldIsItsLength()
    {
        var main = Gen(Addr + "n: uint8 = len(a.rom)\n").Functions.Single(f => f.Name == "main").Body;

        main.Any(i => i is Copy { Src: Constant { Value: 3 }, Dst: Variable { Name: "n" } }).Should().BeTrue();
    }

    [Fact]
    public void APropertyReturningABufferFieldPassesTheBuffer()
    {
        var main = Gen(Addr + "bus.write(a.rom)\n").Functions.Single(f => f.Name == "main").Body;

        main.OfType<ArrayLoad>().Should().Contain(l => l.ArrayName == "r",
            because: "the callee's buf[i] reads the array the property handed back");
    }
}

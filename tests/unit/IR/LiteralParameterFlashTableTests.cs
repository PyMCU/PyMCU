using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `bus.write(b"\xcc\xbe")` into a method that walks `buf[i]` with a run-time index: the
/// literal goes to a flash table, unless something can store through it. The veto counted
/// every store to a NAME `buf` in the program, so the sibling `readinto(self, buf)` filling
/// its own argument -- another object -- refused the literal (adafruit_onewire's bus.py).
/// </summary>
public class LiteralParameterFlashTableTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    private const string Bus =
        "from pymcu.types import uint8\n" +
        "class Bus:\n" +
        "    def __init__(self):\n" +
        "        self.acc = 0\n" +
        "    def write(self, buf, start: uint8 = 0) -> None:\n" +
        "        for i in range(start, len(buf)):\n" +
        "            self.acc += buf[i]\n" +
        "    def readinto(self, buf) -> None:\n" +
        "        for i in range(0, len(buf)):\n" +
        "            buf[i] = self.acc\n" +
        "bus = Bus()\n" +
        "rx = bytearray(2)\n" +
        "bus.readinto(rx)\n";

    [Fact]
    public void AStoreToTheSameNameInAnotherMethodDoesNotVetoTheTable()
    {
        var act = () => Gen(Bus +
            "def go(s: uint8) -> None:\n" +
            "    bus.write(b\"\\xcc\\xbe\", s)\n" +
            "go(1)\n" + "go(2)\n");

        act.Should().NotThrow();
    }

    [Fact]
    public void AStoreThroughTheParameterItselfStillKeepsTheLiteralOutOfFlash()
    {
        var act = () => Gen(
            "from pymcu.types import uint8, inline\n" +
            "class Bus:\n" +
            "    def __init__(self):\n" +
            "        self.acc = 0\n" +
            "    @inline\n" +
            "    def scribble(self, buf, s: uint8) -> None:\n" +
            "        buf[0] = 1\n" +
            "        self.acc += buf[s]\n" +
            "bus = Bus()\n" +
            "def go(s: uint8) -> None:\n" +
            "    bus.scribble(b\"\\xcc\\xbe\", s)\n" +
            "go(1)\n" + "go(2)\n");

        act.Should().Throw<Exception>().WithMessage("*compile-time values with no storage*");
    }
}

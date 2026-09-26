using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Optional state is filed by name, and an @inline body shares names with its caller. The
/// caller's `n = uart.readinto(buf)` (an Optional result) made the body's own `n: uint16 = 0`
/// optional-capable, so `if n == 0:` inside readinto was refused as "'n' may be None here"
/// -- the spelling every CircuitPython program uses. The inverse was silent: a caller's
/// `n = None` answered `n is None` for the body's local `n` holding a number.
/// </summary>
public class InlineScopeOptionalTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    [Fact]
    public void TheCallersOptionalNameDoesNotReachTheBodysLocal()
    {
        var act = () => Gen(
            "from typing import Optional\n" +
            "from pymcu.types import uint8, uint16, inline\n" +
            "src: uint8 = 0\n" +
            "class U:\n" +
            "    def __init__(self):\n" +
            "        self.k = 0\n" +
            "    @inline\n" +
            "    def readinto(self, buf) -> Optional[uint16]:\n" +
            "        n: uint16 = 0\n" +
            "        for i in range(len(buf)):\n" +
            "            if src == 0:\n" +
            "                break\n" +
            "            buf[i] = src\n" +
            "            n = n + 1\n" +
            "        if n == 0:\n" +
            "            return None\n" +
            "        return n\n" +
            "u = U()\n" +
            "buf = bytearray(4)\n" +
            "n = u.readinto(buf)\n" +
            "r: uint8 = 1 if n is None else 0\n");

        act.Should().NotThrow(because: "the body's n is its own uint16 local");
    }

    [Fact]
    public void ACallersNoneDoesNotFoldTheBodysIsNone()
    {
        var main = Gen(
            "from pymcu.types import uint8, inline\n" +
            "@inline\n" +
            "def probe(k: uint8) -> uint8:\n" +
            "    n: uint8 = k\n" +
            "    if n is None:\n" +
            "        return 1\n" +
            "    return 2\n" +
            "n = None\n" +
            "def go(k: uint8) -> uint8:\n" +
            "    return probe(k)\n" +
            "x = go(3)\n" + "y = go(4)\n").Functions.Single(f => f.Name == "go").Body;

        main.Any(i => i is Return { Value: Constant { Value: 1 } }
                                      || i is Copy { Src: Constant { Value: 1 } }).Should().BeFalse(because: "the body's n holds k, so the `is None` arm is dead");
    }
}

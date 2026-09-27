using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `out = bytearray(2 * len(data))` in an @inline that returns `out`: len() of a buffer
/// parameter did not fold in a size, so the buffer took the run-time arena path, and the
/// `return out` handed back its arena offset as a number. `a = dbl(src)` then read the bits
/// of that number -- a buffer of zeros, and `len(a)` refused (binascii.hexlify in the
/// MicroPython layer is this shape).
/// </summary>
public class ReturnedBufferSizedFromLenTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    [Fact]
    public void LenOfABufferParameterSizesTheReturnedBuffer()
    {
        var main = Gen(
            "from pymcu.types import uint8, inline\n" +
            "@inline\n" +
            "def dbl(data: bytearray) -> bytearray:\n" +
            "    out = bytearray(2 * len(data))\n" +
            "    out[0] = data[0] + 1\n" +
            "    return out\n" +
            "src = bytearray(3)\n" +
            "a = dbl(src)\n" +
            "n: uint8 = len(a)\n").Functions.Single(f => f.Name == "main").Body;

        main.Any(i => i is Copy { Src: Constant { Value: 6 }, Dst: Variable { Name: "n" } })
            .Should().BeTrue(because: "the returned buffer is 2 * len(src) = 6 bytes");
        main.OfType<BitCheck>().Should().BeEmpty();
    }
}

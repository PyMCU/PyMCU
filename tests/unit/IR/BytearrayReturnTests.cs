using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#464. A function that allocates a <c>bytearray</c>, fills it, and returns it
/// is how adafruit_bmp280's <c>_read_register</c> is written. The buffer is element
/// storage under a name; there is no handle to put in a register.
///
/// Force-inlining the callee lays the buffer out in the caller's frame, and the
/// assignment aliases it -- the same shape <c>bytes([...])</c> already uses as a
/// call argument (#431).
/// </summary>
public class BytearrayReturnTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    [Fact]
    public void AReturnedBytearrayIsIndexedAtTheCallSite()
    {
        var ir = Gen(
            "from pymcu.types import used, uint8\n\n" +
            "def read_reg(n: uint8) -> bytearray:\n" +
            "    buf = bytearray(2)\n" +
            "    buf[0] = n\n" +
            "    buf[1] = n + 1\n" +
            "    return buf\n\n" +
            "@used\n" +
            "def main() -> uint8:\n" +
            "    b = read_reg(0xD0)\n" +
            "    return b[0]\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is ArrayStore s && s.Src is Constant { Value: 0xD0 });
        Assert.Contains(main.Body, i => i is ArrayLoad);
    }

    [Fact]
    public void TheSecondElementOfAReturnedBytearrayIsTheFilledValue()
    {
        var ir = Gen(
            "from pymcu.types import used, uint8\n\n" +
            "def read_reg(n: uint8) -> bytearray:\n" +
            "    buf = bytearray(2)\n" +
            "    buf[0] = n\n" +
            "    buf[1] = n + 1\n" +
            "    return buf\n\n" +
            "@used\n" +
            "def main() -> uint8:\n" +
            "    b = read_reg(0xD0)\n" +
            "    return b[1]\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is ArrayStore s && s.Src is Constant { Value: 0xD1 });
        Assert.Contains(main.Body, i => i is ArrayLoad);
    }

    [Fact]
    public void ABufferReturnFromARealSubroutineThatCannotInlineIsStillNamed()
    {
        // A recursive @inline is refused; a buffer return that is not expanded keeps
        // the existing diagnostic rather than compiling to a scalar.
        string msg = Refusal(
            "from pymcu.types import uint8\n\n" +
            "def main() -> uint8:\n" +
            "    buf = bytearray(2)\n" +
            "    buf[0] = 1\n" +
            "    return buf\n");
        Assert.Contains("cannot be returned", msg);
    }
}

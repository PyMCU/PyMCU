using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A function that takes the length of a buffer parameter, or walks it, was refused ("len() argument
/// must be a fixed-size array"): as a subroutine the parameter is an address with no length. The
/// call sites know the buffer, so such a function is expanded at them. adafruit_onewire's
/// `crc8(data)`, a static method that does `for byte in data`, is the shape that asked for it.
/// </summary>
public class BufferParamMeasuredTests
{
    private static ProgramIR Generate(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr", Chip = "atmega328p" });

    private const string Buf = "buf = bytearray(3)\nbuf[0] = 1\n";

    [Theory]
    [InlineData("def total(data: bytearray) -> int:\n    s = 0\n    for b in data:\n        s += b\n    return s\n\n")]
    [InlineData("def total(data: bytearray) -> int:\n    s = 0\n    for i in range(len(data)):\n        s += data[i]\n    return s\n\n")]
    [InlineData("def total(data: bytearray) -> int:\n    return data[len(data) - 1]\n\n")]
    public void AFunctionThatMeasuresItsBufferParameterIsExpandedAtItsCallSite(string fn)
    {
        var ir = Generate(fn + Buf + "x: int = total(buf)\n");
        Assert.DoesNotContain(ir.Functions, f => f.Name == "total");
    }

    [Fact]
    public void AStaticMethodThatWalksItsBufferParameterIsExpandedToo()
    {
        var ir = Generate("class C:\n    @staticmethod\n    def crc(data: bytearray) -> int:\n"
                          + "        c = 0\n        for b in data:\n            c ^= b\n        return c\n\n"
                          + Buf + "x: int = C.crc(buf)\n");
        Assert.DoesNotContain(ir.Functions, f => f.Name.EndsWith("crc"));
    }

    [Fact]
    public void AFunctionThatOnlyIndexesItsBufferParameterStaysASubroutine()
    {
        var ir = Generate("def first(data: bytearray) -> int:\n    return data[0]\n\n" + Buf + "x: int = first(buf)\n");
        Assert.Contains(ir.Functions, f => f.Name == "first");
    }
}

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#365. A function that is neither compiled as a subroutine nor expanded at a call site
/// is compiled NOWHERE, and the compiler used to say `[BUILD_OK]` and exit 0 about it.
///
/// `bytes` is how it was found. No part of the compiler had a width for the name, so a
/// parameter annotated with it was taken for a CLASS and the function was registered for
/// call-site expansion. With `@used` there was no call site at all and the function was never
/// emitted; the absence reached a CircuitPython native module as `undefined symbol` out of
/// `tools/mpy_ld.py`, which reads as a linker problem and is not one. `bytes` is the storage
/// a `bytearray` parameter already gets -- a pointer to bytes, subscripted in the body -- so
/// it is read as one.
///
/// The values a `bytes` parameter reads are measured on the simulator: pymcu-avr fixture
/// `bytes-param`.
/// </summary>
public class FunctionCompiledNowhereTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string[] Names(ProgramIR ir) => ir.Functions.Select(f => f.Name).ToArray();

    private const string Used = "from pymcu.types import used\n\n";

    [Fact]
    public void ABytesParameterIsReadAsTheByteBufferType()
    {
        var prog = new Parser(new Lexer("def f(buf: bytes) -> int:\n    return buf[0]\n")
            .Tokenize()).ParseProgram();
        Assert.Equal("bytearray", prog.Functions[0].Params[0].Type);
    }

    [Fact]
    public void AFunctionWithABytesParameterIsEmitted()
    {
        // Red before the fix: the function list came back empty, with BUILD_OK and exit 0.
        var ir = Gen(Used + "@used\ndef first(buf: bytes) -> int:\n    return buf[0]\n");
        Assert.Contains("first", Names(ir));
    }

    [Fact]
    public void ABytesParameterSubscriptLoadsAByte()
    {
        var ir = Gen(Used + "@used\ndef first(buf: bytes) -> int:\n    return buf[0]\n");
        var body = ir.Functions.Single(f => f.Name == "first").Body;
        Assert.Contains(body, i => i is BytearrayLoad);
        Assert.DoesNotContain(body, i => i is BitCheck);
    }

    [Fact]
    public void ACallToAFunctionWithABytesParameterKeepsTheCallee()
    {
        // The wrong-code face of the same loss: `f` was expanded to nothing and `x` was never
        // written, with nothing said about either.
        var ir = Gen("def f(b: bytes) -> int:\n    return b[0]\n\n" +
                     "def main() -> None:\n    buf = bytearray(4)\n    x: int = f(buf)\n");
        Assert.Contains("f", Names(ir));
    }

    [Fact]
    public void ABytearrayParameterIsUnchanged()
    {
        // The control that made the loss easy to walk past: one word apart, and it behaved.
        var ir = Gen(Used + "@used\ndef first(buf: bytearray) -> int:\n    return buf[0]\n");
        Assert.Contains("first", Names(ir));
    }
}

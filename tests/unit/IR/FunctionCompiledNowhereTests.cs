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
/// `bytes` was one reason among several, though, and the loss was in the REGISTRATION and not
/// in any one of the reasons. `@used` means "emit this with external linkage even though no
/// Python code calls it", and on a function registered for expansion that request cannot be
/// met. It was dropped in silence for every reason a function gets registered, `@inline`
/// included. It is refused at the definition now, naming which of the two facts has to give.
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

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    private static string[] Names(ProgramIR ir) => ir.Functions.Select(f => f.Name).ToArray();

    private const string Used = "from pymcu.types import used\n\n";

    // ── `bytes` is the byte buffer it names ─────────────────────────────────────────────

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

    // ── `@used` on a function that has no subroutine to export ──────────────────────────

    [Fact]
    public void UsedOnAnInlineFunctionIsRefused()
    {
        string msg = Refusal("from pymcu.types import used, inline\n\n" +
                             "@used\n@inline\ndef f(n: int) -> int:\n    return n\n");
        Assert.Contains("@used", msg);
        Assert.Contains("@inline", msg);
    }

    [Fact]
    public void UsedOnAFunctionTakingAClassInstanceIsRefused()
    {
        string msg = Refusal(Used +
                             "class P:\n    def __init__(self, n: int) -> None:\n" +
                             "        self._n: int = n\n\n" +
                             "@used\ndef f(p: P) -> int:\n    return 1\n");
        Assert.Contains("'p: P'", msg);
        Assert.Contains("caller's frame", msg);
    }

    [Fact]
    public void UsedOnAVariadicFunctionIsRefused()
    {
        string msg = Refusal(Used + "@used\ndef f(*args) -> int:\n    return 1\n");
        Assert.Contains("*args", msg);
    }

    [Fact]
    public void UsedOnAnOrdinaryFunctionStillCompiles()
    {
        // The control. The refusal must not reach a function that has a subroutine to export.
        var ir = Gen(Used + "@used\ndef f(n: int) -> int:\n    return n\n");
        Assert.Contains("f", Names(ir));
    }

    [Fact]
    public void UsedOnAFunctionTakingABytesBufferStillCompiles()
    {
        // And the two answers compose: `bytes` is a buffer, so this is an ordinary subroutine
        // and `@used` has a symbol to name.
        var ir = Gen(Used + "@used\ndef first(buf: bytes) -> int:\n    return buf[0]\n");
        Assert.Contains("first", Names(ir));
    }
}

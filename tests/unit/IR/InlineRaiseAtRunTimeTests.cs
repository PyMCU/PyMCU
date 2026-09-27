using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A `raise` inside an @inline expansion is a compile error only where the compiler decides
/// it is reached. Two shapes were refused that Python raises at run time:
///   * an unconditional `raise` at the end of a body whose earlier run-time `if`s return --
///     reached only when none of them returned (`hexval`, "ValueError: non-hex digit");
///   * a body whose only path is a `raise`, called inside a `try` that catches it, refused
///     as "reaches the end without returning".
/// </summary>
public class InlineRaiseAtRunTimeTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Head =
        "from pymcu.types import uint8, uint32, ptr, inline\n" +
        "GPIOR0: ptr[uint8] = ptr(0x3E)\n";

    [Fact]
    public void ARaiseAfterRunTimeReturns_IsARunTimeRaise()
    {
        var ir = Gen(Head +
            "@inline\n" +
            "def hexval(c: uint8) -> uint8:\n" +
            "    if c >= 48 and c <= 57:\n" +
            "        return c - 48\n" +
            "    raise ValueError(\"non-hex digit\")\n" +
            "n: uint8 = hexval(GPIOR0.value)\n");
        Assert.Contains(ir.Functions.Single(f => f.Name == "main").Body, i => i is SignalError);
    }

    [Fact]
    public void ABodyThatOnlyRaises_InsideATry_Compiles()
    {
        var ir = Gen(Head +
            "@inline\n" +
            "def f() -> uint32:\n" +
            "    raise ValueError(\"x\")\n" +
            "t: uint32 = 0\n" +
            "try:\n" +
            "    t = f()\n" +
            "except ValueError:\n" +
            "    t = 1\n");
        Assert.Contains(ir.Functions.Single(f => f.Name == "main").Body,
            i => i is SignalError { CatchLabel: not null });
    }

    [Fact]
    public void AnUnconditionalRaiseWithNoHandler_IsStillACompileError()
    {
        Assert.ThrowsAny<CompilerError>(() => Gen(Head +
            "@inline\n" +
            "def h(c: uint8) -> uint8:\n" +
            "    raise ValueError(\"unconditional\")\n" +
            "n: uint8 = h(3)\n"));
    }
}

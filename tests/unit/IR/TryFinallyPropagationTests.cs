using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An exception leaving a function through a try/finally reached the caller's `except` as
/// something else, or not at all. Two faults, both measured on
/// `try: r = 60000 // k` / `finally: q = 60000 // (k + 7)` in a function called from inside
/// the caller's `try ... except ZeroDivisionError`, which printed `E:ZeroDivisionError`:
///
///   * CanFail compared the count of propagating raises with the count of BranchOnError
///     guards, which follow CALLS, not raises. The finally's own zero check and the re-raise
///     after it were outnumbered, the function was not CanFail, and the caller never tested
///     the T flag after the call.
///   * the re-raise after the finally left R22 as it was ("code 0"), and the finally is
///     ordinary code that may use R22: the code the caller compared was not the exception.
/// </summary>
public class TryFinallyPropagationTests
{
    private const string Src =
        "from pymcu.types import uint8, uint16, ptr\n" +
        "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
        "sink: uint16 = 0\n" +
        "def h(x: uint16) -> uint16:\n" +
        "    while x > 60000:\n" +
        "        x = x - 1\n" +
        "    return x + 1\n" +
        "def run(k: uint16) -> uint16:\n" +
        "    global sink\n" +
        "    try:\n" +
        "        r = 60000 // k\n" +
        "        sink = h(r)\n" +
        "        sink = h(sink)\n" +
        "        sink = h(sink)\n" +
        "    finally:\n" +
        "        sink = 60000 // (k + 7)\n" +
        "    return r\n" +
        "try:\n" +
        "    n: uint16 = run(GPIOR0.value)\n" +
        "except ZeroDivisionError:\n" +
        "    sink = 1\n";

    private static ProgramIR Gen(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
        CanFailAnalyzer.Analyze(ir);
        return ir;
    }

    [Fact]
    public void AFunctionWhoseFinallyCanRaise_CanFail()
    {
        var ir = Gen(Src);
        Assert.True(ir.Functions.Single(f => f.Name == "run").CanFail);
    }
}

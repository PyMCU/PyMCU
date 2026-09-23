using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `x = []` crashed TryVisitCtListAssign: the element-type inference read
/// `visited[0]` on a zero-length array when the literal had no elements
/// (IndexOutOfRangeException, surfaced as a bare CompileError). Found on
/// adafruit_onewire's `scan()`, which opens with `devices = []` and grows it
/// with runtime `append`.
///
/// The program is still refused -- an untyped empty literal has no storage
/// shape -- but with a diagnostic naming the declaration PyMCU needs, not a
/// compiler crash.
/// </summary>
public class EmptyListLiteralAssignTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void EmptyListLiteral_DoesNotCrash()
    {
        // Nothing ever reads the name: the assignment lowers to a zero-size
        // compile-time array and IR generation completes.
        var ir = Gen("xs = []\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void EmptyListLiteral_ThenRuntimeAppend_IsRefused()
    {
        // `xs.append(1)` needs a typed list; the refusal must be the named
        // diagnostic, not the IndexOutOfRangeException it used to throw.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(
            () => Gen("xs = []\nxs.append(1)\n"));
        Assert.Contains("typed list", ex.Message);
    }

    [Fact]
    public void EmptyListLiteral_ThenIndex_IsRefused()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(
            () => Gen("xs = []\nprint(xs[0])\n"));
        Assert.NotNull(ex);
    }
}

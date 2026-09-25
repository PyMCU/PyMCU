using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Regression tests for a parameter of an @inline expansion losing to a module global of the
/// same name (PyMCU#512).
///
/// Every @inline shim in the compatibility layers takes parameters with ordinary names --
/// `buf`, `data`, `addr`, `value`, `n`, `pin`. A user program that binds a module-level name
/// matching one of them silently changed what the shim computed, and the collision is between
/// the user's own global and a parameter inside a library they did not write and cannot see
/// from the call site.
///
/// `len(param)` asked the bare name before the frame's own binding, so the global's length
/// was emitted as a constant.
///
/// Found through I2C: `machine.I2C.writeto` is `def writeto(self, addr, buf: bytearray)` and
/// calls `write_bytes(addr, buf, len(buf))`, so a program with its own module-level `buf` put
/// that buffer's length on the wire instead of the one it passed. The bytes were right and
/// there was the wrong number of them, which on a bus is indistinguishable from a wiring
/// fault.
///
/// The discriminating experiment in every case is a RENAME: the layout does not move and the
/// answer changes, which a shared storage slot could not do.
/// </summary>
public class InlineParamNameCollisionTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig());
    }

    // The expression folds, so the answer arrives as the constant that is stored. A
    // multiplying marker keeps the two candidate lengths far apart: 2 -> 23, 9 -> 93.
    private static bool Stores(ProgramIR ir, int value) =>
        ir.Functions.SelectMany(f => f.Body)
          .Any(i => i is Copy { Src: Constant c } && c.Value == value);

    private static IEnumerable<string> ArraysRead(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body)
          .OfType<ArrayLoad>()
          .Select(a => a.ArrayName);

    private const string Head =
        "from pymcu.types import inline, uint8, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n";

    // DISCRIMINATING. The reported program: len() of a parameter answered with the length of
    // a module global that happens to share the parameter's name.
    [Theory]
    [InlineData("buf")]      // colliding name: stored 93 before the fix
    [InlineData("otro")]     // control: the same program, one word changed
    public void LenOfAParameterMeasuresTheArgument(string globalName)
    {
        var ir = Gen(Head +
            "@inline\n" +
            "def takes(buf: bytearray) -> uint8:\n" +
            "    return uint8(len(buf)) * 10 + 3\n" +
            "small = bytearray(2)\n" +
            $"{globalName} = bytearray(9)\n" +
            "G.value = takes(small)\n");

        Assert.True(Stores(ir, 23), "len(buf) must be 2, the argument's length");
        Assert.False(Stores(ir, 93), "the colliding global's length must not be measured");
    }

    // INVARIANT: a module global read from inside an expansion that does NOT bind that name
    // must still resolve to the global. The fix makes the frame's binding win; a frame with no
    // such binding has to fall through to the module as it always did.
    [Fact]
    public void AGlobalWithNoCollidingParameterIsStillReached()
    {
        var ir = Gen(Head +
            "table = bytearray(4)\n" +
            "@inline\n" +
            "def reads(k: uint8) -> uint8:\n" +
            "    return table[k]\n" +
            "table[0] = 7\n" +
            "G.value = reads(0)\n");

        Assert.Contains("table", ArraysRead(ir));
    }
}

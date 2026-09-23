using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `[e] * n` with a RUNTIME count materializes a heap list of n copies of e --
/// the fixed-array repeat path needs the count at compile time, but a heap
/// list does not. Unmodified adafruit_irremote writes
/// `output = [0] * ((len(pulses) + 7) // 8)` in decode_bits.
/// </summary>
public class RuntimeListRepeatTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void RuntimeCount_EmitsHeapList()
    {
        var ir = Gen(
            "def f(n: uint16) -> uint16:\n" +
            "    out = [0] * n\n" +
            "    return out[0]\n" +
            "def main():\n" +
            "    f(4)\n");
        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.Contains(bodies, i => i is GcAlloc);
    }

    [Fact]
    public void RuntimeCount_ResultIsAList_LenAndIndexResolve()
    {
        var ir = Gen(
            "def f(n: uint16) -> uint16:\n" +
            "    out = [0] * n\n" +
            "    m = len(out)\n" +
            "    return out[m - 1]\n" +
            "def main():\n" +
            "    f(4)\n");
        Assert.NotEmpty(ir.Functions);
    }

    [Fact]
    public void RuntimeCount_ComputedFromLen_Works()
    {
        // The vendored shape: `(len(pulses) + 7) // 8`.
        var ir = Gen(
            "def f(p: list[uint16]) -> uint16:\n" +
            "    out = [0] * ((len(p) + 7) // 8)\n" +
            "    return len(out)\n" +
            "def main():\n" +
            "    a: list[uint16] = [1, 2, 3]\n" +
            "    f(a)\n");
        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.True(bodies.OfType<GcAlloc>().Count() >= 2);
    }

    [Fact]
    public void CompileTimeCount_StillUsesFixedArray()
    {
        // `x = [0] * 8` is the established fixed-array scratch buffer -- the
        // runtime path must not claim it.
        var ir = Gen(
            "def main():\n" +
            "    out = [0] * 8\n" +
            "    out[0] = 1\n");
        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.DoesNotContain(bodies, i => i is GcAlloc);
    }
}

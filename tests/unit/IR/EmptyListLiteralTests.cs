using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `x = []` -- an empty list literal. The compile-time-sequence lowering indexed
/// element 0 to learn the element width before it counted the elements, so an
/// empty literal raised IndexOutOfRangeException and surfaced as an
/// InternalCompilerError at module level, or as a bare "Index was outside the
/// bounds of the array" CompileError inside an inlined call.
///
/// An empty literal is a compile-time sequence of zero elements: `len(x)` folds
/// to 0, `for v in x` runs no iterations, `v in x` is false. Growing it is a
/// different question -- `x.append(v)` still needs the heap list a `list[T]`
/// declaration names.
/// </summary>
public class EmptyListLiteralTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void EmptyListAtModuleLevel_DoesNotCrash()
        => Assert.NotNull(Gen(
            "x = []\n" +
            "y = len(x)\n"));

    [Fact]
    public void EmptyListInsideACall_DoesNotCrash()
        => Assert.NotNull(Gen(
            "def f(flag: uint8) -> uint8:\n" +
            "    x = []\n" +
            "    if flag:\n" +
            "        x = [1, 2]\n" +
            "    return len(x)\n" +
            "def main():\n" +
            "    r = f(1)\n"));

    [Fact]
    public void EmptyList_IsAnEmptyConstSequence()
    {
        var ir = Gen(
            "x = []\n" +
            "y = len(x)\n");
        // len(x) must fold to 0 -- an element type of uint8 by default, and no
        // elements: anything else means the empty literal built real storage.
        var fn = Assert.Single(ir.Functions, f => f.Name == "main");
        Assert.DoesNotContain(fn.Body, i => i is Copy { Dst: Variable v }
            && v.Name == "y" && i is Copy { Src: Constant c } && c.Value != 0);
    }
}

using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A PyMCU instance is flattened to compile-time storage (`self.x` lives at `&lt;name&gt;_x`),
/// never a value with its own address. `EmitListAppend`'s generic value path did not know
/// this: `xs.append(Counter(i))` (a fresh instance) and `xs.append(c)` (an existing named one)
/// both read the instance's bare handle name -- storage NOTHING ever writes, since only the
/// flattened fields are assigned. Every element after the first then read back as 0
/// (uninitialized SRAM), indistinguishable from a real value: `xs[1]` silently answered
/// `xs[0]`'s field, in BOTH a single-field class (the scalar/ZCA shape) and a multi-field one.
/// Measured against CPython (which prints "0 1") and against the AVR8Sharp emulator (which
/// printed "0 0" for both repro shapes, before this fix).
///
/// Refused now, at the append, naming the class and the fixed-size-array alternative (RFC
/// 0001 Model B, `tests/integration/fixtures/zca-array`) that gives each instance real,
/// run-time-indexed storage.
/// </summary>
public class AppendClassInstanceRefusalTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void AppendingAFreshSingleFieldInstance_IsRefused()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(
            "from pymcu.types import uint8\n" +
            "class Counter:\n" +
            "    def __init__(self, n: uint8) -> None:\n" +
            "        self._n = n\n" +
            "xs = []\n" +
            "xs.append(Counter(0))\n" +
            "xs.append(Counter(1))\n"));
        Assert.Contains("'.append()' cannot take an instance of 'Counter'", ex.Message);
    }

    [Fact]
    public void AppendingAFreshMultiFieldInstance_IsRefused()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(
            "from pymcu.types import uint8\n" +
            "class Pair:\n" +
            "    def __init__(self, n: uint8, m: uint8) -> None:\n" +
            "        self._n = n\n" +
            "        self._m = m\n" +
            "xs = []\n" +
            "xs.append(Pair(0, 10))\n" +
            "xs.append(Pair(1, 11))\n"));
        Assert.Contains("'.append()' cannot take an instance of 'Pair'", ex.Message);
    }

    [Fact]
    public void AppendingAnExistingNamedInstance_IsRefused()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(
            "from pymcu.types import uint8\n" +
            "class Counter:\n" +
            "    def __init__(self, n: uint8) -> None:\n" +
            "        self._n = n\n" +
            "xs = []\n" +
            "c0 = Counter(0)\n" +
            "xs.append(c0)\n"));
        Assert.Contains("'.append()' cannot take an instance of 'Counter'", ex.Message);
    }

    [Fact]
    public void AppendingAScalar_StillCompiles()
    {
        // The refusal is narrow: a list of plain numbers, built the same way
        // (append in an unrolled loop), is untouched.
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "xs = []\n" +
            "for i in range(2):\n" +
            "    xs.append(i)\n" +
            "a0 = xs[0]\n" +
            "a1 = xs[1]\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }
}

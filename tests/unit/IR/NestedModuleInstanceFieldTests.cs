using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// A field of an instance HELD by a module-level instance, read or written inside a plain
// function, issue #520.
//
//     class Holder:
//         def __init__(self, n):
//             self.inner: Src = Src(n + GPIOR0.value)
//     h2 = Holder(2)
//     def f():
//         return h2.inner.base
//
// printed 2 at module level and 0 from f, the same expression in one file. A field of a
// module-level instance touched inside a function is given a real global at that touch,
// because the store that wrote it ran in main. The test was `topLevelInstanceTargets
// .Contains(baseName)`, and the receiver here flattens to `h2_inner`, a path UNDER the
// top-level `h2` rather than `h2` itself. So `h2_inner_base` stayed a local of f that nothing
// wrote, and main's own read only looked right because the optimizer propagated the
// constructor's copy into it. A write from a function went nowhere for the same reason.
//
// WHAT DISCRIMINATES: the storage assertions. Against the unfixed compiler `h2_inner_base` is
// absent from globals in both directions.
//
// WHAT IS INVARIANT: the one-level field (`h2.n`) that always had its global.
//
// The values are checked in the emulator by pymcu-avr's oracle probe 430.
public class NestedModuleInstanceFieldTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Classes =
        "from pymcu.types import uint8, uint16\n" +
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n\n\n" +
        "class Src:\n" +
        "    def __init__(self, base: uint16):\n" +
        "        self.base: uint16 = base\n\n\n" +
        "class Holder:\n" +
        "    def __init__(self, n: uint16):\n" +
        "        self.n: uint16 = n + GPIOR0.value\n" +
        "        self.inner: Src = Src(n + GPIOR0.value)\n\n\n" +
        "h2 = Holder(300)\n\n\n";

    private static bool HasStorage(ProgramIR ir, string name) =>
        ir.Globals.Any(g => g.Name == name);

    private static IEnumerable<Copy> CopiesInto(ProgramIR ir, string fn, string name) =>
        ir.Functions.Where(f => f.Name == fn).SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name == name);

    [Fact]
    public void ANestedFieldReadInAFunctionHasStorage()
    {
        var ir = Gen(Classes +
            "def f() -> uint16:\n" +
            "    return h2.inner.base\n\n\n" +
            "GPIOR1.value = f()\n");
        Assert.True(HasStorage(ir, "h2_inner_base"),
            "the reader in f loads a name main's constructor must be able to write");
        Assert.NotEmpty(CopiesInto(ir, "main", "h2_inner_base"));
    }

    [Fact]
    public void ANestedFieldReadThroughAModuleAliasHasStorage()
    {
        var ir = Gen(Classes +
            "inner_ref = h2.inner\n\n\n" +
            "def f() -> uint16:\n" +
            "    return inner_ref.base\n\n\n" +
            "GPIOR1.value = f()\n");
        Assert.True(HasStorage(ir, "h2_inner_base"));
    }

    [Fact]
    public void ANestedFieldWrittenInAFunctionHasStorage()
    {
        var ir = Gen(Classes +
            "def f():\n" +
            "    h2.inner.base = 700 + GPIOR0.value\n\n\n" +
            "f()\n" +
            "GPIOR1.value = h2.inner.base\n");
        Assert.True(HasStorage(ir, "h2_inner_base"),
            "a write from f must land where main reads");
        Assert.NotEmpty(CopiesInto(ir, "f", "h2_inner_base"));
    }

    [Fact]
    public void AOneLevelFieldKeepsItsStorage()
    {
        var ir = Gen(Classes +
            "def f() -> uint16:\n" +
            "    return h2.n\n\n\n" +
            "GPIOR1.value = f()\n");
        Assert.True(HasStorage(ir, "h2_n"));
    }
}

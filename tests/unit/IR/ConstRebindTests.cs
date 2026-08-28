using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// A `const` rebound through `global`, issue #217. The program states the name cannot change,
// and it changed, with no diagnostic.
//
// It depended on how the declaration was SPELLED, which is the part worth pinning:
//
//     LIMIT: const[uint8] = 10     refused a rebind
//     LIMIT: const = 10            accepted one, in silence
//
// The parser chooses between two nodes on one character: an annotation containing '[' becomes
// an AnnAssign, anything else a VarDecl. ScanGlobals, which is the only place a module-level
// declaration is seen, recorded the constant in its AnnAssign branch and not in its VarDecl
// one, so the bare spelling was never recorded as const at all. VisitAnnAssign has its own
// registration, through IsConstType, which accepts both spellings and is never reached for a
// module-level declaration.
//
// WHAT DISCRIMINATES: the three bare-spelling refusals. Against the unfixed compiler all three
// build in silence.
//
// WHAT IS INVARIANT: the typed spelling, which was already refused and must not change; an
// ordinary mutable global, which `global` exists to rebind; and a local that shadows the
// constant without `global`, which is what Python means and is correctly silent.
//
// Not fixed here, and measured: `global helper` then `helper = 5` on a function name is still
// accepted. That is the third of the three checks the issue lists, it has nothing to do with
// how a const is spelled, and it is left open rather than folded in.
public class ConstRebindTests
{
    private static void Build(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static PyMCU.Common.CompilerError Reject(string src)
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Build(src));

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR1\n" +
        "from pymcu.types import uint8, const\n\n\n";

    // --- the bare spelling, which was silent -----------------------------------

    [Fact]
    public void ABareConstCannotBeReboundThroughGlobal()
    {
        var ex = Reject(Preamble +
            "LIMIT: const = 10\n\n\n" +
            "def main() -> None:\n" +
            "    global LIMIT\n" +
            "    LIMIT = 20\n" +
            "    GPIOR1.value = LIMIT\n");

        Assert.Contains("cannot assign to constant 'LIMIT'", ex.Message);
    }

    [Fact]
    public void NorThroughAnAugmentedAssignment()
    {
        var ex = Reject(Preamble +
            "LIMIT: const = 10\n\n\n" +
            "def main() -> None:\n" +
            "    global LIMIT\n" +
            "    LIMIT += 5\n" +
            "    GPIOR1.value = LIMIT\n");

        Assert.Contains("cannot assign to constant 'LIMIT'", ex.Message);
    }

    [Fact]
    public void NorAtModuleLevelAfterTheDeclaration()
    {
        var ex = Reject(Preamble +
            "LIMIT: const = 10\n" +
            "LIMIT = 20\n\n\n" +
            "def main() -> None:\n" +
            "    GPIOR1.value = LIMIT\n");

        Assert.Contains("cannot assign to constant 'LIMIT'", ex.Message);
    }

    // --- invariants --------------------------------------------------------------

    [Fact]
    public void TheSubscriptedSpellingIsRefusedAsItAlreadyWas()
    {
        // The control that made the bug visible: one of the two spellings was enforced.
        var ex = Reject(Preamble +
            "LIMIT: const[uint8] = 10\n\n\n" +
            "def main() -> None:\n" +
            "    global LIMIT\n" +
            "    LIMIT = 20\n" +
            "    GPIOR1.value = LIMIT\n");

        Assert.Contains("cannot assign to constant 'LIMIT'", ex.Message);
    }

    [Fact]
    public void AnOrdinaryGlobalIsStillRebindable()
    {
        // What `global` is for. A refusal that reached this would take the statement away.
        Build(Preamble +
            "COUNT: uint8 = 10\n\n\n" +
            "def main() -> None:\n" +
            "    global COUNT\n" +
            "    COUNT = 20\n" +
            "    GPIOR1.value = COUNT\n");
    }

    [Fact]
    public void ALocalThatShadowsTheConstantIsStillSilent()
    {
        // Without `global` this binds a NEW LOCAL, which is what Python means and is not a
        // rebinding of the constant at all.
        Build(Preamble +
            "LIMIT: const = 10\n\n\n" +
            "def main() -> None:\n" +
            "    LIMIT: uint8 = 20\n" +
            "    GPIOR1.value = LIMIT\n");
    }

    [Fact]
    public void ABareConstIsStillReadableWhereItIsUsed()
    {
        // Recording the name as a constant must not stop it BEING one: it still folds at its
        // use sites, which is the whole reason `const` exists here.
        Build(Preamble +
            "LIMIT: const = 10\n\n\n" +
            "def main() -> None:\n" +
            "    GPIOR1.value = LIMIT + 1\n");
    }
}

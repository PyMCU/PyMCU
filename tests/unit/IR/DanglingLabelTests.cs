using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Every label a jump names has to exist. EmitOptimizedConditionalJump lowers each operand of
/// an `and` / `or` as it walks it, so it can emit a jump to the label it was given and only
/// afterwards decide the whole condition folds; the caller then keeps one branch and never
/// defines that label. The jump left behind is unreachable, but an undefined label is not a
/// dead instruction, it is `ld: undefined reference to L_2`.
///
/// It was invisible on a normal build because the optimizer deletes the unreachable jump
/// before anyone counts labels, which is why the check here reads the IR the GENERATOR
/// produced rather than the optimized image.
/// </summary>
public class DanglingLabelTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(),
                                          new DeviceConfig { Arch = "avr" });
    }

    private static string? JumpTargetOf(Instruction i) => i switch
    {
        Jump j => j.Target,
        JumpIfZero j => j.Target,
        JumpIfNotZero j => j.Target,
        JumpIfEqual j => j.Target,
        JumpIfNotEqual j => j.Target,
        JumpIfLessThan j => j.Target,
        JumpIfLessOrEqual j => j.Target,
        JumpIfGreaterThan j => j.Target,
        JumpIfGreaterOrEqual j => j.Target,
        JumpIfBitSet j => j.Target,
        JumpIfBitClear j => j.Target,
        _ => null,
    };

    /// <summary>Jump targets no Label in the program defines. Empty is the only good answer.</summary>
    private static List<string> UndefinedTargets(ProgramIR ir)
    {
        var defined = ir.Functions
            .SelectMany(f => f.Body)
            .OfType<Label>()
            .Select(l => l.Name)
            .ToHashSet();

        return ir.Functions
            .SelectMany(f => f.Body)
            .Select(JumpTargetOf)
            .Where(t => t != null && !defined.Contains(t))
            .Select(t => t!)
            .Distinct()
            .ToList();
    }

    private const string Preamble = "from pymcu.types import uint8, const, inline\n\n";

    [Fact]
    public void TheReportedProgram_LeavesNoUndefinedLabel()
    {
        // PyMCU#153, reduced: an `or` between two comparisons both decided at compile time.
        var ir = Gen(Preamble +
                     "@inline\n" +
                     "def arm(name: const):\n" +
                     "    if name == \"PD2\" or name == 2:\n" +
                     "        x: uint8 = 1\n" +
                     "    elif name == \"PD3\" or name == 3:\n" +
                     "        x2: uint8 = 2\n" +
                     "def main():\n" +
                     "    arm(\"PD2\")\n");

        Assert.Empty(UndefinedTargets(ir));
    }

    [Theory]
    // The four static combinations of an `or`, and the four of an `and`. Only one of the eight
    // reached the bug, but the label contract has to hold for all of them.
    [InlineData("1 == 1 or 2 == 2")]
    [InlineData("1 == 1 or 2 == 3")]
    [InlineData("1 == 2 or 2 == 2")]
    [InlineData("1 == 2 or 2 == 3")]
    [InlineData("1 == 1 and 2 == 2")]
    [InlineData("1 == 1 and 2 == 3")]
    [InlineData("1 == 2 and 2 == 2")]
    [InlineData("1 == 2 and 2 == 3")]
    public void EveryStaticCombination_LeavesNoUndefinedLabel(string cond)
    {
        var ir = Gen(Preamble +
                     "def main():\n" +
                     $"    if {cond}:\n" +
                     "        a: uint8 = 1\n" +
                     "    else:\n" +
                     "        a2: uint8 = 2\n");

        Assert.Empty(UndefinedTargets(ir));
    }

    [Theory]
    [InlineData("1 == 1 or 2 == 3")]
    [InlineData("1 == 2 or 2 == 3")]
    [InlineData("1 == 1 and 2 == 3")]
    public void TheSameCombinationsInAnElif_LeaveNoUndefinedLabel(string cond)
    {
        // The elif arm has its own early return for a statically true condition, and its own
        // freshly made label to abandon.
        var ir = Gen(Preamble +
                     "def main():\n" +
                     "    if 1 == 2:\n" +
                     "        a: uint8 = 1\n" +
                     $"    elif {cond}:\n" +
                     "        b: uint8 = 2\n" +
                     "    else:\n" +
                     "        c: uint8 = 3\n");

        Assert.Empty(UndefinedTargets(ir));
    }

    [Fact]
    public void NestedOrs_LeaveNoUndefinedLabel()
    {
        var ir = Gen(Preamble +
                     "def main():\n" +
                     "    if (1 == 1 or 2 == 3) or (4 == 5 or 6 == 6):\n" +
                     "        a: uint8 = 1\n" +
                     "    else:\n" +
                     "        b: uint8 = 2\n");

        Assert.Empty(UndefinedTargets(ir));
    }

    [Fact]
    public void AnOrOfTwoRuntimeComparisons_StillBranchesAtRuntime()
    {
        // The fix only defines a label; it must not turn a run-time condition into a folded
        // one, so the comparison instructions have to survive.
        var ir = Gen(Preamble +
                     "def main():\n" +
                     "    n: uint8 = 3\n" +
                     "    if n == 1 or n == 2:\n" +
                     "        a: uint8 = 1\n" +
                     "    else:\n" +
                     "        b: uint8 = 2\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is JumpIfEqual or JumpIfNotEqual);
        Assert.Empty(UndefinedTargets(ir));
    }

    [Fact]
    public void AStaticallyTrueCondition_StillKeepsOnlyTheThenBranch()
    {
        // The label is defined, but the else branch must stay gone: emitting it would undo the
        // compile-time dispatch the HAL depends on.
        var ir = Gen(Preamble +
                     "def main():\n" +
                     "    if 1 == 1 or 2 == 3:\n" +
                     "        a: uint8 = 7\n" +
                     "    else:\n" +
                     "        a2: uint8 = 9\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is Copy { Src: Constant { Value: 7 } });
        Assert.DoesNotContain(main.Body, i => i is Copy { Src: Constant { Value: 9 } });
    }

    [Fact]
    public void ALabelIsNotEmittedWhenNothingJumpsToIt()
    {
        // The whole point of asking first: a condition that folded without emitting anything
        // must not leave a label behind for the backend to carry.
        var ir = Gen(Preamble +
                     "def main():\n" +
                     "    if 1 == 1:\n" +
                     "        a: uint8 = 1\n" +
                     "    else:\n" +
                     "        b: uint8 = 2\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Single(main.Body.OfType<Label>());
    }
}

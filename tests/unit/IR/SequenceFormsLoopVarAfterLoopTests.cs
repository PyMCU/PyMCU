using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The loop-variable-after-loop doctrine (TupleLoopVarAfterLoopTests, RangeLoopVarAfterLoopTests)
/// generalized to every other compile-time-unrolled iteration form: a fixed array, a dict, a
/// string split, zip(), reversed(), a slice, and enumerate() over an array. Each of these used
/// the SAME materialize-on-early-exit / settle-on-completion fix as the tuple/list/range forms,
/// and two of them (reversed() over a list literal or a named sequence) also needed a loop-stack
/// entry that never existed at all: `break` inside either reported "Break statement outside of
/// loop" because nothing was ever pushed for a break to find.
/// </summary>
public class SequenceFormsLoopVarAfterLoopTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Main(string src) => Gen(src).Functions.Single(f => f.Name == "main").Body;

    private const string Prelude = "from pymcu.types import uint8\n\n";

    [Fact]
    public void AnEnumerateOverAnArray_IndexMaterializesOnBreak()
    {
        // The index is bound under a bare key throughout the whole enumerate()
        // construct; a read after the loop resolves through the qualified name
        // instead, so the real write has to land THERE, not on the bare key.
        var body = Main(Prelude +
            "def main():\n" +
            "    buf = bytearray([10, 20, 30])\n" +
            "    for i, b in enumerate(buf):\n" +
            "        break\n" +
            "    y: uint8 = i\n\n" +
            "main()\n");
        Assert.Contains(body, instr => instr is Copy { Src: Constant { Value: 0 }, Dst: Variable { Name: "main.i" } });
    }

    [Fact]
    public void AReversedListLiteral_BreakDoesNotCrash()
    {
        // `break` inside `reversed([..])` used to report "Break statement outside of
        // loop": nothing was ever pushed onto loopStack for it to find.
        var act = () => Main(Prelude +
            "def main():\n" +
            "    v: uint8 = 9\n" +
            "    for v in reversed([1, 2, 3]):\n" +
            "        break\n" +
            "    y: uint8 = v\n\n" +
            "main()\n");
        var ir = act();
        // valKey is bare; the real write has to land on the qualified "main.v".
        Assert.Contains(ir, instr => instr is Copy { Src: Constant { Value: 3 }, Dst: Variable { Name: "main.v" } });
    }

    [Fact]
    public void AReversedNamedSequence_BreakDoesNotCrash()
    {
        var ir = Main(Prelude +
            "def main():\n" +
            "    seq = [1, 2, 3]\n" +
            "    v: uint8 = 9\n" +
            "    for v in reversed(seq):\n" +
            "        break\n" +
            "    y: uint8 = v\n\n" +
            "main()\n");
        Assert.Contains(ir, instr => instr is Copy { Src: Constant { Value: 3 }, Dst: Variable { Name: "main.v" } });
    }

    [Fact]
    public void AZipOverTwoNamedConstantSequences_BothNamesMaterializeOnBreak()
    {
        // A bytearray's elements are real SRAM loads -- zip already copies those for
        // real on every iteration, early exit or not, so that shape never exercised
        // this bug. A name bound to a constant tuple/list DOES fold (ResolveConstSequenceExpr's
        // own Bind closure), with no backing store before this fix.
        var body = Main(Prelude +
            "def main():\n" +
            "    a = (1, 2, 3)\n" +
            "    b = (4, 5, 6)\n" +
            "    for x, y in zip(a, b):\n" +
            "        break\n" +
            "    p: uint8 = x\n" +
            "    q: uint8 = y\n\n" +
            "main()\n");
        Assert.Contains(body, instr => instr is Copy { Src: Constant { Value: 1 }, Dst: Variable { Name: "main.x" } });
        Assert.Contains(body, instr => instr is Copy { Src: Constant { Value: 4 }, Dst: Variable { Name: "main.y" } });
    }

    [Fact]
    public void ADictKey_MaterializesOnBreak()
    {
        var body = Main(Prelude +
            "def main():\n" +
            "    d = {1: 10, 2: 20, 3: 30}\n" +
            "    k: uint8 = 9\n" +
            "    for k in d:\n" +
            "        break\n" +
            "    y: uint8 = k\n\n" +
            "main()\n");
        Assert.Contains(body, instr => instr is Copy { Src: Constant { Value: 1 }, Dst: Variable { Name: "main.k" } });
    }

    // A slice's fold-only branch (constantVariables.TryGetValue(elemKey, ...)) needs
    // elements proven constant AND an array the compiler does not already track for
    // SRAM/variable-indexed access -- in practice every fixed array this language can
    // declare ends up SRAM-tracked, so the real-Copy branch always wins and there is
    // no reachable repro for its materialize/settle. Fixed defensively, for the same
    // reason EmitSequenceUnroll's own fold branch (member array, forward iteration)
    // was: consistent with every other sibling form, not independently observed.
}

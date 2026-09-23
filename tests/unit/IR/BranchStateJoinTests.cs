using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The join rule BranchState owns: a binding survives a control-flow merge only where
/// every reachable path agrees on it -- and a binding established on the ONLY possible
/// path survives even when that path leaves (an inline frame's result temp flows out
/// through it). Before the centralized owner, only three of the binding maps were
/// reconciled at all, and an arm that could not reach the merge was either joined as a
/// path carrying pre-branch state (vetoing everything the live arms established) or
/// leaked its writes wholesale into the merge.
/// </summary>
public class BranchStateJoinTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig());
    }

    private static List<Instruction> MainBody(ProgramIR ir) =>
        ir.Functions.First(f => f.Name == "main").Body;

    private const string Regs =
        "from pymcu.types import uint8, const, inline, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n";

    /// <summary>
    /// pwmio.PWMOut asks a helper for the timer prescaler exactly this way: every arm of
    /// the chain decides at compile time, so the else arm is the chain's unconditional
    /// answer and the constant its `return` bound must reach the caller -- even though
    /// that arm never falls through to the merge. Joining the else as an unreachable
    /// arm dropped the result temp's binding and `self._real_frequency` became a
    /// run-time field.
    /// </summary>
    [Fact]
    public void UnconditionalElseReturn_BindsTheResultConstant()
    {
        var body = MainBody(Gen(Regs +
            "@inline\n" +
            "def bucket(n: const[uint8]) -> uint8:\n" +
            "    if n > 200:\n" +
            "        return 1\n" +
            "    elif n > 100:\n" +
            "        return 2\n" +
            "    else:\n" +
            "        return 61\n" +
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self._a: uint8 = 0\n" +
            "def main():\n" +
            "    box = Box()\n" +
            "    box._a = bucket(50)\n" +
            "    G.value = box._a\n"));

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 61 }, Dst: MemoryAddress });
        Assert.DoesNotContain(body, i => i is Copy { Src: Constant { Value: 1 }, Dst: MemoryAddress });
        Assert.DoesNotContain(body, i => i is Copy { Src: Constant { Value: 2 }, Dst: MemoryAddress });
    }

    /// <summary>
    /// neopixel.py binds r, g, b in the try body and the only handler re-raises. The
    /// path where NO handler matches propagates the error away -- it never reaches the
    /// merge after the try, so it must not contribute pre-try state that vetoes the
    /// bindings the body established. When it did, the byte-order tuple stopped folding
    /// and the fixture grew 1022 bytes of packed-table bit reads.
    /// </summary>
    [Fact]
    public void TryBodyBinding_SurvivesWhenTheOnlyHandlerLeaves()
    {
        var body = MainBody(Gen(Regs +
            "def main():\n" +
            "    try:\n" +
            "        r: uint8 = 5\n" +
            "        G.value = r\n" +
            "    except OSError:\n" +
            "        return\n" +
            "    if r == 5:\n" +
            "        G.value = 1\n" +
            "    else:\n" +
            "        G.value = 2\n"));

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 1 }, Dst: MemoryAddress });
        Assert.DoesNotContain(body, i => i is Copy { Src: Constant { Value: 2 }, Dst: MemoryAddress });
    }

    /// <summary>
    /// The other side of the same rule: a name bound on only SOME reaching paths has no
    /// single value at the merge. A rebind inside one arm used to answer reads after
    /// the chain because the other arm never wrote the name.
    /// </summary>
    [Fact]
    public void BindingInOneArmOnly_DoesNotFoldAfterTheIf()
    {
        var body = MainBody(Gen(Regs +
            "def main():\n" +
            "    seed: uint8 = G.value\n" +
            "    if seed > 100:\n" +
            "        x: uint8 = 7\n" +
            "    if x == 7:\n" +
            "        G.value = 1\n" +
            "    else:\n" +
            "        G.value = 2\n"));

        // `x` is 7 on the then-path and unbound on the fall-through: `x == 7` cannot
        // fold, so both stores must still be emitted.
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 1 }, Dst: MemoryAddress });
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 2 }, Dst: MemoryAddress });
    }

    /// <summary>
    /// A walrus inside one ternary arm is a write that arm alone made: it cannot answer
    /// a read after the expression. The ternary had no reconcile at all before, so the
    /// bound name leaked.
    /// </summary>
    [Fact]
    public void WalrusInOneTernaryArm_DoesNotFoldAfterTheExpression()
    {
        var body = MainBody(Gen(Regs +
            "def main():\n" +
            "    seed: uint8 = G.value\n" +
            "    y: uint8 = (x := 9) if seed > 100 else 0\n" +
            "    if x == 9:\n" +
            "        G.value = 1\n" +
            "    else:\n" +
            "        G.value = 2\n"));

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 1 }, Dst: MemoryAddress });
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 2 }, Dst: MemoryAddress });
    }

    /// <summary>
    /// A sequence the body of a run-time-bound loop binds is not bound on the zero-trip
    /// path, so a read after the loop cannot fold it. The old 3-map reconcile did not
    /// cover literal bindings at all: `s[0]` folded to an element of a list the loop may
    /// never have stored.
    /// </summary>
    [Fact]
    public void SequenceBoundInsideARuntimeLoop_DoesNotFoldAfterIt()
    {
        var body = MainBody(Gen(Regs +
            "def main():\n" +
            "    n: uint8 = G.value\n" +
            "    for i in range(n):\n" +
            "        s = [1, 2, 3]\n" +
            "        G.value = i\n" +
            "    G.value = s[0]\n"));

        Assert.DoesNotContain(body, i => i is Copy { Src: Constant { Value: 1 }, Dst: MemoryAddress }
            && i == body.Last());
    }

    /// <summary>
    /// And the positive half of the loop rule: a binding every loop exit agrees on
    /// survives. `x` is 5 before the loop and the body leaves it alone, so the
    /// zero-trip join keeps it.
    /// </summary>
    [Fact]
    public void BindingUntouchedByTheLoopBody_SurvivesTheZeroTripJoin()
    {
        var body = MainBody(Gen(Regs +
            "def main():\n" +
            "    n: uint8 = G.value\n" +
            "    x: uint8 = 5\n" +
            "    for i in range(n):\n" +
            "        G.value = i\n" +
            "    if x == 5:\n" +
            "        G.value = 1\n" +
            "    else:\n" +
            "        G.value = 2\n"));

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 1 }, Dst: MemoryAddress });
        Assert.DoesNotContain(body, i => i is Copy { Src: Constant { Value: 2 }, Dst: MemoryAddress });
    }
}

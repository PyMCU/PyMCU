using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// Python evaluates every expression of a call exactly once, left to right, before the body
// runs. An @inline expansion substitutes its arguments into the body, and a substitution that
// re-reads an argument's EXPRESSION instead of its value replays the argument's effects:
//
//     @inline
//     def pick(seq):
//         return seq[bump()]
//     print(pick([10, 20, 30, 40]))     # CPython 20, one bump
//
// printed 30 and left the counter at 2.
//
// WHAT DISCRIMINATES: the number of `bump` calls (or register reads) in the IR, and the order
// of the calls. Each program below does what CPython does, and against the unfixed compiler
// it calls or reads a different number of times, or in the other order.
//
// WHAT IS INVARIANT: an argument with no effect is still substituted, so a constant index
// into the same literal folds to the element and emits nothing.
public class InlineArgumentEvaluatedOnceTests
{
    private static ProgramIR Gen(string src, bool optimize = false)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return optimize ? Optimizer.Optimize(ir) : ir;
    }

    private const string Bump =
        "from pymcu.types import uint8, inline, ptr\n\n" +
        "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
        "GPIOR1: ptr[uint8] = ptr(0x4A)\n\n" +
        "_c: uint8 = 0\n\n" +
        "def bump() -> uint8:\n" +
        "    global _c\n" +
        "    _c = _c + 1\n" +
        "    return _c\n\n" +
        "def other() -> uint8:\n" +
        "    GPIOR0.value = 5\n" +
        "    return 1\n\n";

    private static IEnumerable<Instruction> Main(ProgramIR ir) =>
        ir.Functions.Single(f => f.Name == "main").Body;

    private static int BumpCalls(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Call>().Count(c => c.FunctionName == "bump");

    private static List<string> CallOrder(ProgramIR ir) =>
        Main(ir).OfType<Call>().Select(c => c.FunctionName)
            .Where(n => n is "bump" or "other").ToList();

    // GPIOR0 is data address 0x3E. Every mention of it in main, a read or a write.
    private static int Gpior0Mentions(ProgramIR ir) =>
        Main(ir).Sum(i => CountOf(i.ToString(), "Address = 62"));

    private static int CountOf(string s, string what)
    {
        int n = 0;
        for (int at = s.IndexOf(what, StringComparison.Ordinal); at >= 0;
             at = s.IndexOf(what, at + what.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    [Fact]
    public void ARunTimeIndexIntoALiteralArgumentIsEvaluatedOnce()
    {
        var ir = Gen(Bump +
            "@inline\n" +
            "def pick(seq):\n" +
            "    return seq[bump()]\n\n" +
            "GPIOR1.value = pick([10, 20, 30, 40])\n");
        Assert.Equal(1, BumpCalls(ir));
    }

    [Fact]
    public void AConstantIndexIntoALiteralArgumentStillFolds()
    {
        var ir = Gen(Bump +
            "@inline\n" +
            "def pick(seq):\n" +
            "    return seq[2]\n\n" +
            "GPIOR1.value = pick([10, 20, 30, 40])\n");
        Assert.Contains(Main(ir).OfType<Copy>(), c => c.Src is Constant { Value: 30 });
        Assert.Empty(Main(ir).OfType<ArrayLoadFlash>());
    }

    [Fact]
    public void AnElementReadTwiceIsEvaluatedOnce()
    {
        var ir = Gen(Bump +
            "@inline\n" +
            "def s2(seq):\n" +
            "    return seq[0] + seq[0]\n\n" +
            "GPIOR1.value = s2([bump(), 5])\n");
        Assert.Equal(1, BumpCalls(ir));
    }

    [Fact]
    public void AnElementNeverReadIsStillEvaluated()
    {
        var ir = Gen(Bump +
            "@inline\n" +
            "def s0(seq):\n" +
            "    return 7\n\n" +
            "GPIOR1.value = s0([bump(), 5])\n");
        Assert.Equal(1, BumpCalls(ir));
    }

    [Fact]
    public void ElementsRunInTheOrderTheyAreWritten()
    {
        var ir = Gen(Bump +
            "@inline\n" +
            "def so(seq):\n" +
            "    return seq[1] * 10 + seq[0]\n\n" +
            "GPIOR1.value = so([bump(), other()])\n");
        Assert.Equal(new[] { "bump", "other" }, CallOrder(ir));
    }

    [Fact]
    public void ARegisterArgumentReadTwiceIsReadOnce()
    {
        var ir = Gen(Bump +
            "@inline\n" +
            "def twice(v):\n" +
            "    return v + v\n\n" +
            "GPIOR1.value = twice(GPIOR0.value)\n");
        Assert.Equal(1, Gpior0Mentions(ir));
    }

    [Fact]
    public void ARegisterArgumentIsReadBeforeALaterArgumentRuns()
    {
        var ir = Gen(Bump +
            "@inline\n" +
            "def f(a, b):\n" +
            "    return a * 10 + b\n\n" +
            "GPIOR1.value = f(GPIOR0.value, other())\n", optimize: true);
        var body = Main(ir).ToList();
        int read = body.FindIndex(i => i is Copy { Src: MemoryAddress { Address: 62 } });
        int call = body.FindIndex(i => i is Call { FunctionName: "other" });
        Assert.InRange(read, 0, call - 1);
    }

    [Fact]
    public void AnItemAssignmentEvaluatesItsValueFirst()
    {
        var ir = Gen(Bump +
            "arr: uint8[4] = [0, 0, 0, 0]\n" +
            "arr[bump()] = other()\n");
        Assert.Equal(new[] { "other", "bump" }, CallOrder(ir));
    }

    [Fact]
    public void AnAugmentedItemAssignmentEvaluatesItsSubscriptOnceAndFirst()
    {
        var ir = Gen(Bump +
            "arr: uint8[4] = [0, 0, 0, 0]\n" +
            "arr[bump()] += other()\n");
        Assert.Equal(new[] { "bump", "other" }, CallOrder(ir));
    }

    [Fact]
    public void ASetitemKeyPairRunsAfterTheValue()
    {
        var ir = Gen(Bump +
            "class M:\n" +
            "    def __init__(self):\n" +
            "        self.k = 0\n\n" +
            "    @inline\n" +
            "    def __setitem__(self, key, v):\n" +
            "        x, y = key\n" +
            "        self.k = x + y + v + x\n\n" +
            "m = M()\n" +
            "m[bump(), bump()] = other()\n" +
            "GPIOR1.value = m.k\n");
        Assert.Equal(new[] { "other", "bump", "bump" }, CallOrder(ir));
    }

    private static bool ReadsCAfterBump(ProgramIR ir)
    {
        var body = Main(ir).ToList();
        int bump = body.FindIndex(i => i is Call { FunctionName: "bump" });
        Assert.True(bump >= 0);
        // A record prints a List argument as its type name, so a call's arguments are asked
        // one by one.
        static bool ReadsC(Instruction i) => i is Call c
            ? c.Args.Any(a => a is Variable { Name: "_c" })
            : i.ToString()!.Contains("Name = _c,");
        return body.Skip(bump + 1).Any(ReadsC);
    }

    // `0 + _c` folds to a temporary holding `_c`, read before the call. Forwarding the
    // temporary into the call's argument reads `_c` where the call runs -- after bump()
    // wrote it through `global`.
    [Fact]
    public void ATempHoldingAGlobalIsNotForwardedPastACall()
    {
        var ir = Gen(Bump +
            "def f(a: uint8, b: uint8) -> uint8:\n" +
            "    return a * 10 + b\n\n" +
            "_c = GPIOR0.value\n" +
            "GPIOR1.value = f(0 + _c, bump())\n", optimize: true);
        Assert.False(ReadsCAfterBump(ir));
    }
}

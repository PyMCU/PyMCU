using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU golperf (perf/narrow-loop-arith): `(x + dx) % w` in a Game of Life step() called
/// __mod32/__mods32 roughly 1500 times per generation even though x is 0..31, dx is -1..1
/// and w is always 32 -- a class field written once, from a literal, in __init__. The
/// existing strength reduction in the optimizer only fires for a LITERAL power-of-two
/// divisor (see <c>IRGeneratorTests.FloorDivMod_PowerOfTwo_StrengthReduces</c>); these
/// tests cover the new case: a VARIABLE divisor whose value the EXISTING constant-folding
/// tables (constantVariables, localConstantValues) already prove, lowered at IR-generation
/// time, before the optimizer ever runs.
///
/// `a % P` with P a proven positive power of two is `a & (P - 1)`: the low bits of a
/// two's-complement value ARE its floor remainder at every width, so the mask answers
/// identically even when `a` is negative (`-1 % 32` is `-1 & 31` = 31, exactly CPython's
/// floor modulo).
///
/// A first version of this rewrite gave itself a bespoke range-tracking table
/// (variableRanges/ProvenRange/ProvenSourceFact) to prove the divisor, and that table went
/// through three rounds of silent regressions in code review before landing here: a shared
/// non-inline function's parameter proven from one call site (rem(5, 32) then rem(5, 4)
/// answering 1, 1), a loop-carried name surviving the loop's own invalidation, and a field
/// write through a method not voiding an earlier proof. All three traced back to the same
/// mistake -- inventing a new fact-tracking table instead of reading the ones the rest of
/// the compiler already trusts for "is this name provably one value here", each already
/// guarded (ForeignFlowRead) against exactly the cross-frame leaks that broke the bespoke
/// one. ProvenConstantDivisor (Expr.cs) reads only constantVariables and
/// localConstantValues; there is no variableRanges, no ProvenRange, no per-frame ownership
/// check to get wrong, because a parameter's argument never enters either table in the
/// first place. See the pymcu-avr oracle probes (620+) for the AVR-executed value checks
/// against CPython, including the three review-found cases (628-630 cover the alias/
/// @outline, break-in-loop and try/except shapes the type selection and shared-parameter
/// fixes did not already exercise).
///
/// A THIRD round found three more, this time in Expr.cs's own reading of
/// localConstantValues (no bespoke table this time): an exception handler inheriting the
/// pre-try value, a walrus on a short-circuited branch leaking into the fallthrough, and a
/// loop-carried walrus answering for every iteration with the loop's entry value (w1/w2/w3
/// below). All three read as "this name's value here" something that was only ever "the
/// last fact recorded for this name" -- correct everywhere else the compiler reads it this
/// way, wrong for a rewrite that treats the answer as true for the rest of the function.
/// The fix drops flow reasoning entirely: DivisorNameIsWholeProgramInvariant
/// (ProvenDivisors.cs) gates every lookup on a pre-scan that counts every TEXTUAL write to
/// a name, anywhere in the program, before lowering starts. A name with two or more writes
/// is refused outright -- which is why w1/w2/w3 (each writes its divisor twice) are refused
/// now regardless of where the read sits relative to either write, and why self.width in
/// the real Game of Life (written once, in __init__) still rewrites through the local
/// alias `w = self.width`. See oracle probes 670+ for the AVR-executed values.
/// </summary>
public class ProvenPowerOfTwoModTests
{
    private static ProgramIR Gen(string src) => new IRGenerator().Generate(
        new Parser(new Lexer(src).Tokenize()).ParseProgram(),
        new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static Function F(ProgramIR ir, string name) => ir.Functions.Single(fn => fn.Name == name);

    [Fact]
    public void AProvenPowerOfTwoDivisor_LowersToAMaskEvenWithANegativeDividend()
    {
        // x: uint8 in [0, 255], so `x - 1` can be -1 -- the floor-mod case a plain
        // "both operands non-negative" check would refuse to narrow.
        const string src =
            "def f(x: uint8) -> int16:\n" +
            "    w: uint8 = 32\n" +
            "    return (x - 1) % w\n" +
            "def main():\n" +
            "    f(0)\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 31 } });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.Mod or IrBinaryOp.FloorDiv });
    }

    [Fact]
    public void ANonPowerOfTwoProvenDivisor_IsNotRewritten()
    {
        // w is just as provably constant as the power-of-two case (10, a local written
        // once from a literal) -- the rewrite must still refuse it: `a & 9` is not `a % 10`.
        const string src =
            "def f(x: int16) -> int16:\n" +
            "    w: int16 = 10\n" +
            "    return x % w\n" +
            "def main():\n" +
            "    f(23)\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 9 } });
    }

    [Fact]
    public void ARuntimeDivisor_RangeDoesNotProveAPowerOfTwo_StaysAGenuineModulo()
    {
        // w is a plain parameter -- constantVariables and localConstantValues never see a
        // parameter's argument at all, so the rewrite must not fire. This is the "range
        // does NOT allow narrowing" case: the program must still compile to a correct (if
        // unoptimized) runtime modulo, not a silently wrong AND on a divisor that might
        // not even be a power of two.
        const string src =
            "def f(x: int16, w: int16) -> int16:\n" +
            "    return x % w\n" +
            "def main():\n" +
            "    f(23, 7)\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
    }

    [Fact]
    public void AProvenPowerOfTwoDivisor_FromAnInstanceFieldWrittenOnceInInit_StillRewrites()
    {
        // The actual golperf shape: `self.width` is a field written once, from a literal,
        // in __init__ -- read through a local (`w = self.width`) inside a different
        // method. This already folds to `copy 32 -> w` through localConstantValues (the
        // field write is inside an inline expansion, not true module-level code, so it is
        // localConstantValues rather than constantVariables that carries it; Expr.cs's
        // ProvenConstantDivisor checks both).
        const string src =
            "from pymcu.chips.atmega328p import GPIOR0\n\n" +
            "class Life:\n" +
            "    def __init__(self, width):\n" +
            "        self.width = width\n" +
            "    def step(self, x) -> int16:\n" +
            "        w = self.width\n" +
            "        return (x - 1) % w\n" +
            "def main():\n" +
            "    life = Life(32)\n" +
            // x must be genuinely runtime-decided (GPIOR0.value), or `life.step(0)`
            // would constant-fold the whole expression to 31 before the rewrite ever
            // runs, same as (-1) % 32 does -- which proves the semantics, not the lowering.
            "    life.step(GPIOR0.value)\n";
        // step() has one call site, so it is inlined into main rather than kept as its
        // own Function -- look at main's body, where the expansion lands, the same way
        // the GoL measurement found it in step()'s inlined asm.
        var mainBody = F(Gen(src), "main").Body;
        Assert.Contains(mainBody, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 31 } });
        Assert.DoesNotContain(mainBody, i => i is Binary { Op: IrBinaryOp.Mod or IrBinaryOp.FloorDiv });
    }

    [Fact]
    public void TheMaskDoesNotNarrowTheResultTypeBelowTheOriginalDivisors()
    {
        // Codex review, case 1: `x % n` with x: int8 and n: uint16 proven 256. The
        // rewrite replaces n with the literal mask 255 BEFORE the result type is
        // chosen; picking that type from the mask's own narrow look (255 reads as a
        // uint8) instead of from the original divisor's uint16 sign-extended the
        // result through int8. rem(int8(-1), 256) is 255 in CPython, not -1.
        const string src =
            "def rem(x: int8) -> int16:\n" +
            "    n: uint16 = 256\n" +
            "    return x % n\n" +
            "def main():\n" +
            "    rem(-1)\n";
        var body = F(Gen(src), "rem").Body;
        var and = body.OfType<Binary>().Single(b => b.Op == IrBinaryOp.BitAnd);
        Assert.Equal(new Constant(255), and.Src2);
        Assert.NotEqual(DataType.INT8, and.Dst is Temporary dt ? dt.Type : DataType.INT8);
    }

    [Fact]
    public void ASharedNonInlineFunctionsParameter_IsNeverProvenFromAnyOneCallSite()
    {
        // Codex review, case 2: rem(x, n) is a real (non-@inline) function, so its body
        // is generated exactly once and must be correct for every call site that shares
        // its parameter slots. A parameter's argument never enters constantVariables or
        // localConstantValues (RecordLocalConstant and the module-level assignment path
        // both key off a declared LOCAL or GLOBAL being written, not a call's argument
        // binding), so rem(5, 32) then rem(5, 4) can never specialize the one shared
        // `x % n` from either call's divisor.
        const string src =
            "def rem(x: int16, n: uint16) -> int16:\n" +
            "    return x % n\n" +
            "def main():\n" +
            "    rem(5, 32)\n" +
            "    rem(5, 4)\n";
        var body = F(Gen(src), "rem").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd });
    }

    [Fact]
    public void ALoopThatWritesTheFieldThroughAMethod_NeverProvesTheDivisorAcrossTheWrite()
    {
        // Codex review, case 3: the loop prepass already invalidates localConstantValues
        // for a field a called method writes (ControlFlow.cs, pre-existing, unrelated to
        // this rewrite) -- `set4()` setting self.w = 4 drops the fact `5 % o.w` would
        // otherwise read on the second iteration. This pins that the rewrite correctly
        // STAYS a genuine modulo once that fact is gone; it is not testing new
        // invalidation code, since ProvenConstantDivisor added none.
        const string src =
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self.w: uint16 = 32\n" +
            "    def set4(self):\n" +
            "        self.w = 4\n" +
            "def main():\n" +
            "    o = Box()\n" +
            "    i: uint8 = 0\n" +
            "    while i < 2:\n" +
            "        y: int16 = 5 % o.w\n" +
            "        o.set4()\n" +
            "        i = i + 1\n";
        var body = F(Gen(src), "main").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 31 } });
    }

    [Fact]
    public void AFieldWrittenThroughAnAliasAndAnOutlineMethod_NeverMasksByTheWrongDivisor()
    {
        // Codex review (second round), case 4: b.w is written through `alias.set4()`
        // where alias is the same instance as b, and set4 is @outline (a real,
        // non-inlined method). CPython's answer is 1, 1 (w ends at 4 before either
        // print). The alias indirection means the field write is not provable through
        // constantVariables/localConstantValues here, so the rewrite correctly declines
        // and leaves a genuine modulo -- optimizing this shape is a possible future
        // improvement, not a correctness requirement; what matters is that it never
        // masks by the STALE divisor 32 (`& 31`), which the AVR-executed probe 628
        // checks against CPython directly.
        const string src =
            "from pymcu.types import outline\n\n" +
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self.w: uint16 = 32\n" +
            "        self.a: uint16 = 0\n" +
            "        self.b: uint16 = 0\n" +
            "    @outline\n" +
            "    def set4(self):\n" +
            "        self.w = 4\n" +
            "def f(k: uint16) -> uint16:\n" +
            "    b = Box()\n" +
            "    alias = b\n" +
            "    alias.set4()\n" +
            "    return 5 % b.w\n" +
            "def main():\n" +
            "    f(0)\n" +
            "    f(1)\n";
        var body = F(Gen(src), "f").Body;
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 31 } });
    }

    [Fact]
    public void AnAssignmentInAnIfBreakInsideAFor_NeverProvesTheDivisorAtAll()
    {
        // Codex review (second round), case 5: x is reassigned on a conditional path
        // that breaks out of a for loop. CPython: x ends at 4, `5 % x` is 1. Under the
        // whole-program write-count criterion (third round) x is written TWICE in its
        // own source -- `(n & 0) + 32` and `x = 4` -- which refuses the rewrite on sight,
        // with no need to reason about which arm of the `if`/`break` actually ran.
        const string src =
            "def f(n: uint16) -> uint16:\n" +
            "    x: uint16 = (n & 0) + 32\n" +
            "    for i in range(n):\n" +
            "        if i == 0:\n" +
            "            x = 4\n" +
            "            break\n" +
            "    return 5 % x\n" +
            "def main():\n" +
            "    f(1)\n" +
            "    f(2)\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 31 } });
    }

    [Fact]
    public void AnAssignmentBeforeARaise_NeverProvesTheDivisorInTheExceptHandler()
    {
        // Codex review (second round), case 6: x is set right before a raise, and read
        // back in the except handler that catches it. CPython: x is 4, `5 % x` is 1.
        // Two textual writes to x (the declaration and the pre-raise reassignment)
        // refuse the rewrite outright -- this is the shape w1.py (third round) pins at
        // the IR-generation boundary; see w1/w1b in oracle probes 670+ for the
        // AVR-executed value this stays correct for.
        const string src =
            "def f(n: uint16) -> uint16:\n" +
            "    x: uint16 = (n & 0) + 32\n" +
            "    try:\n" +
            "        x = 4\n" +
            "        raise ValueError(\"x\")\n" +
            "    except ValueError:\n" +
            "        return 5 % x\n" +
            "    return 0\n" +
            "def main():\n" +
            "    f(1)\n" +
            "    f(2)\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 31 } });
    }

    [Fact]
    public void AWalrusOnAShortCircuitedBranch_NeverProvesTheDivisor()
    {
        // Codex review (third round), case w2: `flag and (n := 4)` only runs the walrus
        // when flag is truthy. CPython: f(0) is 5, f(1) is 1 -- two different answers
        // from one compiled body, which no single proven constant could ever produce.
        // n has two textual writes (the declaration and the walrus), so the whole-
        // program pre-scan refuses it before lowering ever reaches either path.
        const string src =
            "def f(flag: uint8) -> uint16:\n" +
            "    n: uint16 = 32\n" +
            "    flag and (n := 4)\n" +
            "    return 5 % n\n" +
            "def main():\n" +
            "    f(0)\n" +
            "    f(1)\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd });
    }

    [Fact]
    public void AWalrusInsideAWhileLoop_NeverProvesTheDivisorForAnyIteration()
    {
        // Codex review (third round), case w3: a loop-carried walrus rewrites n on the
        // first iteration and holds for the rest. CPython: 51 (5 % 32, then 5 % 4 twice
        // more folded in); a rewrite that answered from the loop's ENTRY value for
        // every iteration gave 55 instead. n has two textual writes, so this is refused
        // regardless of which iteration is being lowered.
        const string src =
            "def f() -> uint16:\n" +
            "    n: uint16 = 32\n" +
            "    i: uint8 = 0\n" +
            "    total: uint16 = 0\n" +
            "    while i < 2:\n" +
            "        total = total * 10 + 5 % n\n" +
            "        (n := 4)\n" +
            "        i = i + 1\n" +
            "    return total\n" +
            "def main():\n" +
            "    f()\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd });
    }

    [Fact]
    public void ALocalWrittenOnceFromALiteral_StillRewrites()
    {
        // A local whose one write is a bare literal is its own proof -- no module name
        // or field to chain through, but exactly as immutable as a literal divisor
        // written in place would be. Without this, the golperf measurement's own
        // `w: uint8 = 32` shape (before self.width even enters it) would stop
        // optimizing under the strict criterion, since "local" is not itself one of
        // the two named cases (module name, instance field) -- only a chain TO one of
        // those two, or to a literal, is.
        const string src =
            "def f(x: uint8) -> int16:\n" +
            "    w: uint8 = 32\n" +
            "    return (x - 1) % w\n" +
            "def main():\n" +
            "    f(0)\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 31 } });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.Mod or IrBinaryOp.FloorDiv });
    }

    // Codex review of the round-3 fix itself: WalkFunctionLocals counted plain
    // assignment, VarDecl/AnnAssign and aug-assign, but missed every OTHER binding
    // form PyMCU's grammar has -- a divisor local written once by a plain assign and
    // once by one of these still read as "one write" and kept its (wrong) proof. Each
    // test below gives w a non-literal first write (`(k & 0) + 32`, or a write behind
    // a runtime `if`) so the ordinary straight-line "last literal write, no read
    // between, wins" fold -- a different, flow-safe optimization -- cannot collapse
    // the whole expression to a constant before this rewrite's own gate ever runs;
    // what is being pinned is that the GATE refuses w, not that nothing else does.

    [Fact]
    public void ATupleUnpackTarget_CountsAsAWrite()
    {
        const string src =
            "def f(k: uint16) -> uint16:\n" +
            "    w: uint16 = (k & 0) + 32\n" +
            "    if k == 0:\n" +
            "        w, dummy = (4, 0)\n" +
            "    return 5 % w\n" +
            "def main():\n" +
            "    f(0)\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 31 } });
    }

    [Fact]
    public void AStarredUnpackTarget_CountsAsAWrite()
    {
        const string src =
            "def f(k: uint16) -> uint16:\n" +
            "    w: uint16 = (k & 0) + 32\n" +
            "    if k == 0:\n" +
            "        w, *rest = (4, 1, 2)\n" +
            "    return 5 % w\n" +
            "def main():\n" +
            "    f(0)\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 31 } });
    }

    [Fact]
    public void AForLoopTarget_CountsAsAWrite()
    {
        const string src =
            "def f() -> uint16:\n" +
            "    w: uint16 = 32\n" +
            "    for w in range(3):\n" +
            "        pass\n" +
            "    return 5 % w\n" +
            "def main():\n" +
            "    f()\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 31 } });
    }

    [Fact]
    public void AWithAsTarget_CountsAsAWrite()
    {
        const string src =
            "from pymcu.types import inline\n\n" +
            "class Ctx:\n" +
            "    @inline\n" +
            "    def __enter__(self):\n" +
            "        return 4\n" +
            "    @inline\n" +
            "    def __exit__(self, a=None, b=None, c=None):\n" +
            "        pass\n" +
            "def f() -> uint16:\n" +
            "    w: uint16 = 32\n" +
            "    with Ctx() as w:\n" +
            "        pass\n" +
            "    return 5 % w\n" +
            "def main():\n" +
            "    f()\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 31 } });
    }

    [Fact]
    public void AnExceptAsTarget_CountsAsAWrite()
    {
        const string src =
            "def f() -> uint16:\n" +
            "    w: uint16 = 32\n" +
            "    try:\n" +
            "        raise ValueError(\"x\")\n" +
            "    except ValueError as w:\n" +
            "        pass\n" +
            "    return 5 % w\n" +
            "def main():\n" +
            "    f()\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 31 } });
    }

    [Fact]
    public void AFunctionLocalImportAsTarget_CountsAsAWrite()
    {
        const string src =
            "def f(k: uint16) -> uint16:\n" +
            "    w: uint16 = (k & 0) + 32\n" +
            "    import pymcu.types as w\n" +
            "    return 5 % w\n" +
            "def main():\n" +
            "    f(0)\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 31 } });
    }

    [Fact]
    public void ANestedFunctionsNonlocalWrite_CountsTowardTheOuterLocal()
    {
        // A nested function (necessarily @inline) can rebind an enclosing local via
        // `nonlocal`. Which enclosing scope exactly is real scope resolution the scan
        // does not attempt -- crediting the write to every function it transitively
        // encloses is conservative, matching the rest of the scan's "unrecognized
        // shape disqualifies" stance rather than trying to get it exactly right.
        const string src =
            "from pymcu.types import inline\n\n" +
            "def f() -> uint16:\n" +
            "    w: uint16 = 32\n" +
            "    @inline\n" +
            "    def bump():\n" +
            "        nonlocal w\n" +
            "        w = 4\n" +
            "    bump()\n" +
            "    return 5 % w\n" +
            "def main():\n" +
            "    f()\n";
        var body = F(Gen(src), "f").Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Mod });
        Assert.DoesNotContain(body, i => i is Binary { Op: IrBinaryOp.BitAnd, Src2: Constant { Value: 31 } });
    }
}

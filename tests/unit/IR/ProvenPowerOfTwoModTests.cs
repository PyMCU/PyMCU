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
/// tests cover the new case: a VARIABLE divisor whose value the generator has PROVEN
/// constant (a local last written from a literal, in the same function, with no other
/// writer), lowered at IR-generation time, before the optimizer ever runs.
///
/// `a % P` with P a proven positive power of two is `a & (P - 1)`: the low bits of a
/// two's-complement value ARE its floor remainder at every width, so the mask answers
/// identically even when `a` is negative (`-1 % 32` is `-1 & 31` = 31, exactly CPython's
/// floor modulo). This is the division-routine elimination only: minting the
/// destination local narrower than the AND's own promoted width was tried and reverted
/// (it minted "inline1.text.char_x" UINT8 at one adafruit_framebuf.text() call site and
/// left a second, wider-argument call site reading the same homed slot inconsistently --
/// tools/verify_ir.py caught it as a storage-width violation), so `xx`'s own declared
/// type is unaffected here; see the pymcu-avr oracle probes (620+) for the AVR-executed
/// value check against CPython.
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
        // w is a plain parameter -- its range never narrows to a single proven value, so
        // the rewrite must not fire. This is the "range does NOT allow narrowing" case:
        // the program must still compile to a correct (if unoptimized) runtime modulo,
        // not a silently wrong AND on a divisor that might not even be a power of two.
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
        // method. Devin's diagnosis: this already folds to `copy 32 -> w`, so the
        // proven-range mechanism sees the same Constant source a plain local would.
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
        // its parameter slots. Proving n from ONE call's argument and baking that into
        // the shared body would be right for that call and wrong for the next one --
        // rem(5, 32) then rem(5, 4) must answer 5 and 1, not 1 and 1 (the last call's
        // divisor specializing the one shared `x % n`).
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
        // Codex review, case 3: the loop prepass invalidates constantVariables,
        // strConstantVariables and localConstantValues for a field a called method
        // writes, but used to leave variableRanges untouched -- the first iteration's
        // `self.w == 32` proof survived the write `set4()` makes on every later pass.
        // `5 % o.w` must stay a genuine modulo: the field's value changes inside the
        // very loop that reads it.
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
}

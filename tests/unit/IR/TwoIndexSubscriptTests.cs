using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#352, both halves.
///
/// The MESSAGE: `m[x, y]` was `Expected "]"` from the C# front end and, from the CPython bridge,
/// the generic tuple refusal, which names the right model limit and then advises building a
/// fixed list for indexable storage, which is not what a reader indexing a matrix is doing.
///
/// The CAPABILITY: a class whose `__getitem__(self, key)` or `__setitem__(self, key, value)`
/// takes the pair receives it as a COMPILE-TIME sequence, so `x, y = key` unpacks the way
/// `a, b = f()` already does and no tuple exists at run time. That is how upstream
/// `adafruit_ht16k33/matrix.py` is written, and it is now accepted as written.
///
/// Both answers depend on the class, which no reader knows, so the refusal moved out of the two
/// readers and into the one site that has the class tables. That is also what stopped the same
/// sentence from being maintained in two files.
/// </summary>
public class TwoIndexSubscriptTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static PyMCU.Common.CompilerError Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src));

    /// The value the port write ends up holding, with no arithmetic left in the program.
    ///
    /// The store reads a temporary that a Copy fills, which is the shape the IR generator emits
    /// before copy propagation runs; what matters is that the Copy carries a CONSTANT and that
    /// no Binary survives, because either index staying run-time would leave an add behind.
    private static void AssertFoldsTo(int expected, ProgramIR ir)
    {
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.Empty(body.OfType<Binary>());
        Assert.Contains(body.OfType<Copy>(), c => c.Src is Constant k && k.Value == expected);
    }

    /// A class with no dunder at all: the pair has nowhere to go, and the refusal is the answer.
    private const string Plain =
        "class Grid:\n" +
        "    @inline\n" +
        "    def __init__(self):\n" +
        "        pass\n" +
        "    @inline\n" +
        "    def pixel(self, x: uint8, y: uint8, v: uint8):\n" +
        "        PORTB: ptr[uint8] = ptr(0x25)\n" +
        "        PORTB.value = v\n";

    /// The upstream matrix shape: one key, unpacked in the body.
    private const string Pair =
        "class Grid:\n" +
        "    @inline\n" +
        "    def __init__(self):\n" +
        "        pass\n" +
        "    @inline\n" +
        "    def pixel(self, x: uint8, y: uint8, v: uint8):\n" +
        "        PORTB: ptr[uint8] = ptr(0x25)\n" +
        "        PORTB.value = x + y + v\n" +
        "    @inline\n" +
        "    def __setitem__(self, key, value: uint8):\n" +
        "        x, y = key\n" +
        "        self.pixel(x, y, value)\n" +
        "    @inline\n" +
        "    def __getitem__(self, key) -> uint8:\n" +
        "        x, y = key\n" +
        "        return x + y\n";

    // ── The refusal, for a class that cannot take the pair ───────────────────────────────

    [Fact]
    public void ATwoIndexStore_OnAClassWithoutTheDunder_IsNamed()
    {
        var ex = Refusal(Plain + "g = Grid()\ndef main():\n    g[1, 2] = 3\n");
        Assert.Contains("a subscript with more than one index", ex.Message);
        Assert.DoesNotContain("Expected ']'", ex.Message);
        Assert.DoesNotContain("fixed list", ex.Message);
    }

    [Fact]
    public void TheRefusalSaysWhatIsMissingAndOffersSomethingThatWorks()
    {
        var ex = Refusal(Plain + "g = Grid()\ndef main():\n    g[1, 2] = 3\n");
        Assert.Contains("takes the pair as one key", ex.Message);
        Assert.Contains("passing the indices separately", ex.Message);
    }

    [Fact]
    public void TheCaretIsOnTheFirstIndexAndNotOnTheComma()
    {
        // CPython stamps the Tuple at its first element. Pointing at the comma would have the
        // two front ends naming different characters for the same program.
        var ex = Refusal(Plain + "g = Grid()\ndef main():\n    g[1, 2] = 3\n");
        Assert.Equal(7, ex.Column);
    }

    [Fact]
    public void ATwoIndexRead_OnAClassWithoutTheDunder_GetsTheSameAnswer()
    {
        Assert.Contains("a subscript with more than one index", Refusal(
            Plain + "g = Grid()\ndef main():\n    y = g[1, 2]\n").Message);
    }

    // ── The capability, for a class that takes the pair ──────────────────────────────────

    [Fact]
    public void ATwoIndexStore_ReachesTheDunderAndUnpacksThePair()
    {
        // One port write, from the pixel() the dunder delegates to: the store compiled, the
        // pair was unpacked, and nothing was refused.
        var ir = Gen(Pair + "g = Grid()\ndef main():\n    g[1, 2] = 3\n");
        Assert.Equal(1, ir.Functions.SelectMany(f => f.Body).OfType<StoreIndirect>().Count());
    }

    [Fact]
    public void ThePairIsCompileTimeSoTheWholeStoreFoldsToItsValue()
    {
        // x + y + v with x=1, y=2, v=3 is the constant 6 before the optimizer runs, and there
        // is no arithmetic instruction left at all. If either index had survived as storage
        // this would be an add of two run-time values, which is the whole difference between
        // binding the pair and materialising it.
        AssertFoldsTo(6, Gen(Pair + "g = Grid()\ndef main():\n    g[1, 2] = 3\n"));
    }

    [Fact]
    public void ATwoIndexRead_ReachesTheDunder()
    {
        Assert.NotNull(Gen(
            Pair + "g = Grid()\ndef main():\n    y = g[4, 5]\n    GPIOR0: ptr[uint8] = ptr(0x3E)\n    GPIOR0.value = y\n"));
    }

    [Fact]
    public void TheKeyCanAlsoBeReadByIndexInsteadOfUnpacked()
    {
        string byIndex =
            "class Grid:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def __setitem__(self, key, value: uint8):\n" +
            "        PORTB: ptr[uint8] = ptr(0x25)\n" +
            "        PORTB.value = key[0] + key[1] + value\n";
        AssertFoldsTo(6, Gen(byIndex + "g = Grid()\ndef main():\n    g[1, 2] = 3\n"));
    }

    [Fact]
    public void AKeyAndAValueCanBothBeSequences()
    {
        // `m[x, y] = (r, g, b)`: the value binding already existed, and the key one is the
        // same mechanism, so the two have to work at once or only one of them really does.
        string rgb =
            "class Grid:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def __setitem__(self, key, value):\n" +
            "        x, y = key\n" +
            "        PORTB: ptr[uint8] = ptr(0x25)\n" +
            "        PORTB.value = x + y + value[0] + value[2]\n";
        AssertFoldsTo(13, Gen(rgb + "g = Grid()\ndef main():\n    g[1, 2] = (4, 5, 6)\n"));
    }

    [Fact]
    public void TheSubscriptFormsThatWork_AreUntouched()
    {
        Assert.NotNull(Gen("def main():\n    b = bytearray(4)\n    b[0] = 1\n    c = b[1:3]\n"));
        Assert.NotNull(Gen("def main():\n    xs = [1, 2, 3]\n    y = xs[1]\n"));
    }

    // ── PyMCU#397: a write and a read of a DIFFERENT key both answered 0 ─────────────────

    /// The upstream shape exactly (no `-> T` on either dunder, matching adafruit_ht16k33's
    /// own style): `__setitem__`/`__getitem__` both take the pair as one key, and neither
    /// declares a return type. `def` with no `-> T` is "void" to the parser, and return-type
    /// inference does not run for class methods, so `EmitDunderCall`'s own `result` local
    /// stayed null -- the fallback that allocates a result slot on the fly lives in the
    /// `return` statement's own handler (Statements.cs), which stores the slot it creates back
    /// onto the (shared) InlineContext. EmitDunderCall never read that back: it kept
    /// returning its OWN stale `result`, still null from before the body ran, so it fell
    /// through to a hardcoded `Constant(0)` regardless of what `x * 10 + y + self.value`
    /// actually computed.
    private const string Matrix =
        "class Matrix:\n" +
        "    def __init__(self):\n" +
        "        self.value = 0\n" +
        "    def __setitem__(self, key, value):\n" +
        "        x, y = key\n" +
        "        self.value = x * 10 + y + value\n" +
        "    def __getitem__(self, key):\n" +
        "        x, y = key\n" +
        "        return x * 10 + y + self.value\n";

    [Fact]
    public void ATwoIndexRoundTrip_WithNoReturnAnnotation_CarriesTheWrittenFieldIntoTheRead()
    {
        // CPython: `m[2, 3] = 4` sets self.value to 2*10+3+4 = 27; `m[1, 2]` then reads
        // 1*10+2+27 = 39. Before the fix, `EmitDunderCall` discarded whatever
        // `__getitem__`'s body actually computed and answered the hardcoded Constant(0) it
        // falls back to when the callee's (unannotated) return type gave it no result slot
        // up front -- so `y` was assigned that sentinel zero regardless of the class's own
        // arithmetic. Checked on the RAW (unoptimized) IR, because it is EmitDunderCall's
        // own return value -- not a later constant-folding pass -- that this asks about.
        var ir = Gen(Matrix +
            "m = Matrix()\n" +
            "def main():\n" +
            "    m[2, 3] = 4\n" +
            "    y: uint8 = m[1, 2]\n" +
            "    while True:\n        pass\n");

        var yCopies = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith(".y")).ToList();
        Assert.NotEmpty(yCopies);
        Assert.DoesNotContain(yCopies, c => c.Src is Constant k && k.Value == 0);
    }
}

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#308. A `for` over a short constant list unrolls, and the loop variable is a
/// compile-time constant in each iteration -- which is exactly what a `const` pin parameter
/// needs. The unroller accepted integers only, so the CircuitPython idiom for a row of pins,
/// `for pin in (board.D2, board.D3, board.D4)`, was refused with "elements must be
/// compile-time integer constants" for elements that ARE compile-time constants, and every
/// guide with more than one pin had to be written out one call per pin.
/// </summary>
public class ForOverStringConstantsTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Prelude =
        "from pymcu.types import uint8, const, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n" +
        "@inline\n" +
        "def bit_of(name: const):\n" +
        "    match name:\n" +
        "        case \"PD2\":\n" +
        "            GPIOR0.value = 2\n" +
        "        case \"PD3\":\n" +
        "            GPIOR0.value = 3\n" +
        "        case \"PD4\":\n" +
        "            GPIOR0.value = 4\n" +
        "        case _:\n" +
        "            GPIOR0.value = 0\n" +
        "\n" +
        "D2 = \"PD2\"\n" +
        "D3 = \"PD3\"\n" +
        "D4 = \"PD4\"\n" +
        "\n";

    private static List<int> RegisterWrites(ProgramIR ir)
    {
        var main = ir.Functions.Last(f => f.Name == "main");
        return main.Body.OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR0", StringComparison.Ordinal))
            .Select(c => c.Src is Constant k ? k.Value : -1)
            .ToList();
    }

    // print() lowers through the HAL writers; the stubs are all it needs to resolve here.
    private const string PrintPrelude =
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write(c: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n";

    // Every text the program hands to a writer, in the order the writes are emitted.
    // A loop variable bound to the interned id instead of the text reaches the decimal
    // writer and leaves nothing here -- which is the regression the oracle caught: the
    // probe printed 256 where "PD2" was meant (probes 009/010, folding regression).
    private static List<string> WrittenTexts(ProgramIR ir)
    {
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        var texts = new Dictionary<string, string>();
        foreach (var fd in body.OfType<FlashData>())
            texts[fd.Name] = new string(fd.Bytes.TakeWhile(b => b != 0).Select(b => (char)b).ToArray());

        return body.OfType<Call>()
            .SelectMany(c => c.Args)
            .OfType<FlashStrAddr>()
            .Select(a => texts.TryGetValue(a.Name, out var t) ? t : "")
            .ToList();
    }

    private static ProgramIR GenWithPrint(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(PrintPrelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void AListOfStringLiterals_PrintsTheTextNotTheId()
    {
        var ir = GenWithPrint(
            "def main():\n" +
            "    for name in [\"PD2\", \"PD3\"]:\n" +
            "        print(name)\n");
        Assert.Equal(new List<string> { "PD2", "\n", "PD3", "\n" }, WrittenTexts(ir));
    }

    [Fact]
    public void PairsOfANumberAndAStringLiteral_PrintTheTextNotTheId()
    {
        var ir = GenWithPrint(
            "def main():\n" +
            "    for pin, name in [(2, \"D2\"), (3, \"D3\")]:\n" +
            "        print(name)\n");
        Assert.Equal(new List<string> { "D2", "\n", "D3", "\n" }, WrittenTexts(ir));
    }

    [Fact]
    public void ATupleOfOneCharacterStrings_PrintsCharacters()
    {
        // Length one binds the character code as the numeric value too -- the name has to
        // answer 'A' as a char and as a string, the two spellings the same literal has.
        var ir = GenWithPrint(
            "def main():\n" +
            "    for c in [\"A\", \"B\"]:\n" +
            "        print(c)\n");
        Assert.Equal(new List<string> { "A", "\n", "B", "\n" }, WrittenTexts(ir));
    }

    [Fact]
    public void AListOfStringLiterals_Unrolls()
    {
        var ir = Gen(Prelude +
            "def main():\n" +
            "    for p in [\"PD2\", \"PD3\", \"PD4\"]:\n" +
            "        bit_of(p)\n");
        Assert.Equal(new List<int> { 2, 3, 4 }, RegisterWrites(ir));
    }

    [Fact]
    public void ATupleOfNamedStringConstants_Unrolls()
    {
        // The board-pin spelling: each element is a module constant, not a literal.
        var ir = Gen(Prelude +
            "def main():\n" +
            "    for p in (D2, D3, D4):\n" +
            "        bit_of(p)\n");
        Assert.Equal(new List<int> { 2, 3, 4 }, RegisterWrites(ir));
    }

    [Fact]
    public void PairsOfAStringAndANumber_Unroll()
    {
        var ir = Gen(Prelude +
            "def main():\n" +
            "    for p, n in [(D2, 7), (D3, 8)]:\n" +
            "        bit_of(p)\n" +
            "        GPIOR0.value = n\n");
        Assert.Equal(new List<int> { 2, 7, 3, 8 }, RegisterWrites(ir));
    }

    [Fact]
    public void AnElementThatIsNotAConstant_CompilesAsARuntimeValue()
    {
        // enumbuf case 3: `for xx in (x - 1, x, x + 1)` with a run-time `x` is valid
        // Python -- CPython builds the tuple once, evaluating each element, then iterates
        // it. Before, ANY non-constant element refused the whole loop, even next to
        // elements that already unroll as constants. Now each element unrolls on its own
        // terms: the constant keeps folding (RegisterWrites reads back 1), and the
        // run-time one copies `v`'s value into the loop variable's own slot (RegisterWrites
        // has no constant to read back for it, so it answers -1, not the wrong number).
        var ir = Gen(Prelude +
            "def main():\n" +
            "    v: uint8 = GPIOR0.value\n" +
            "    for p in [1, v]:\n" +
            "        GPIOR0.value = p\n");
        var writes = RegisterWrites(ir);
        Assert.Equal(2, writes.Count);
        Assert.Equal(1, writes[0]);
        Assert.Equal(-1, writes[1]);
    }

    [Fact]
    public void RunTimeElements_AreAllEvaluatedBeforeTheLoopStarts()
    {
        // CPython builds (bump(), bump(), bump()) EAGERLY: all three calls happen, in
        // order, before the loop -- or its body's `break` -- ever runs. Evaluating each
        // element lazily, right before its own unrolled iteration, let an earlier
        // iteration's `break` skip a later element's call entirely: the third bump()
        // was never emitted, so a counter it bumps read one short.
        var ir = Gen(Prelude +
            "counter = bytearray([0])\n" +
            "def bump() -> uint8:\n" +
            "    counter[0] = counter[0] + 1\n" +
            "    return counter[0]\n" +
            "def main():\n" +
            "    for v in (bump(), bump(), bump()):\n" +
            "        GPIOR0.value = v\n" +
            "        if v == 1:\n" +
            "            continue\n" +
            "        break\n");

        var calls = ir.Functions.Last(f => f.Name == "main").Body
            .OfType<Call>().Count(c => c.FunctionName == "bump");
        Assert.Equal(3, calls);
    }

    [Fact]
    public void TheLoopVariable_KeepsTheLastElementsValueAfterTheLoop()
    {
        // CPython leaves the loop variable bound to the LAST element once a for-loop
        // over a literal tuple/list ends -- `for v in (state[0], 7): pass; print(v)`
        // prints 7, the tuple's own last value, not whatever the FIRST (run-time)
        // element's leftover storage happens to hold. The all-constant case has the
        // exact same shape: `for v in (3, 7): pass; print(v)` must answer 7 too.
        var ir = Gen(Prelude +
            "def main():\n" +
            "    for v in (3, 7):\n" +
            "        pass\n" +
            "    GPIOR0.value = v\n");

        var writes = RegisterWrites(ir);
        Assert.Contains(7, writes);
    }

    [Fact]
    public void ABreak_KeepsTheElementTheBrokenIterationWasProcessing()
    {
        // A `break` can end the loop at an EARLIER iteration than the last -- CPython
        // leaves the loop variable bound to whichever element the break's OWN iteration
        // was processing, a run-time fact, not the tuple's textual last element
        // (PyMCU-review round 3). `for v in (1, 2): break; print(v)` answered 2 (the
        // tuple's last element, same unconditional fold as the no-break case above)
        // instead of 1 (what was actually in scope when the break fired).
        //
        // A break makes the post-loop value flow-dependent, so it is no longer a
        // compile-time constant at all (RegisterWrites' plain "is it a literal" check
        // does not apply here, unlike the no-break test above): the fix instead
        // MATERIALIZES each iteration's element into v's own real storage, so the read
        // after the loop goes through that -- never through the unconditional "last
        // element" fold.
        var ir = Gen(Prelude +
            "def main():\n" +
            "    for v in (1, 2):\n" +
            "        break\n" +
            "    GPIOR0.value = v\n");

        // Iteration 1's own materialize Copy (Constant 2 -> main.v) is still emitted --
        // it is iteration 0's break that makes it unreachable at RUN time, a jump this
        // check does not need to follow. What matters is that iteration 0 materializes
        // its own element, and that the read after the loop goes through the real
        // variable rather than a "last element" fold.
        var body = ir.Functions.Last(f => f.Name == "main").Body;
        Assert.Contains(body,
            i => i is Copy { Src: Constant { Value: 1 }, Dst: Variable { Name: "main.v" } });
        Assert.Contains(body,
            i => i is Copy { Src: Variable { Name: "main.v" }, Dst: Variable { Name: var n } }
                 && n.EndsWith("GPIOR0", StringComparison.Ordinal));
    }

    [Fact]
    public void ALoopVariable_DoesNotLeakIntoALaterLoopThatReusesItsBareName()
    {
        // The fix above (TheLoopVariable_KeepsTheLastElementsValueAfterTheLoop) leaves the
        // tuple's last element bound to "x" in constantVariables ON PURPOSE once ITS loop
        // ends -- CPython does the same. A LATER, unrelated for-loop that rebinds the same
        // bare name over a genuinely run-time sequence (enumerate() over a fixed array,
        // never unrolled as a constant) must not inherit that leftover: it read back 173
        // every iteration instead of the array's own elements (PyMCU "bytes-ops" fixture,
        // Codex-review regression).
        var ir = Gen(Prelude +
            "data: uint8[3] = [10, 20, 30]\n" +
            "def main():\n" +
            "    for x in (1, 173):\n" +
            "        pass\n" +
            "    for i, x in enumerate(data):\n" +
            "        GPIOR0.value = x\n");

        var writes = RegisterWrites(ir);
        Assert.Equal(3, writes.Count);
        Assert.DoesNotContain(173, writes);
    }

    [Fact]
    public void ABareFloatLiteralElement_IsStillRefused()
    {
        // The float-literal gap (#? -- a separate, pre-existing hole in the constant
        // evaluator itself, not case 3's "run-time expression" shape) must keep its own
        // diagnostic: the run-time fallback only takes an element BindUnrolledElement
        // could not fold for lack of a VALUE, never a bare literal it never tries to fold
        // as anything but a number.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Prelude +
            "def main():\n" +
            "    for p in [1, 2.5]:\n" +
            "        pass\n"));
        Assert.Contains("compile-time constants", ex.Message);
    }
}

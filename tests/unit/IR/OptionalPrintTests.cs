using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// RFC 0009 section 8: an unnarrowed read of a runtime-tagged Optional is legal
/// where the use site can represent BOTH outcomes -- print() and f-string
/// interpolation are the first such sites. The tag picks the text CPython
/// writes: each real member prints by its own repr and the None member prints
/// "None". Arithmetic, indexing, comparisons other than `is None`, and
/// non-Optional parameters keep the located refusal.
/// </summary>
public class OptionalPrintTests
{
    private const string Prelude =
        "from pymcu.types import uint8, int16, Optional, Union\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_i16(v: int16):\n" +
        "    pass\n" +
        "def uart_write_float(v: float):\n" +
        "    pass\n";

    // A runtime Optional: the seed comes off a memory register, so neither the
    // None arm nor the value arm can fold at compile time.
    private const string ReadFn =
        "def read(k: uint8) -> Optional[uint8]:\n" +
        "    if k == 0:\n" +
        "        return None\n" +
        "    return k + 7\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static Function Main(string src) =>
        Gen(src).Functions.Single(f => f.Name == "main");

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    private static bool CallsWriter(Instruction i, string writer) =>
        i is Call c && c.FunctionName == writer;

    /// The name of the flash byte-table whose bytes are the literal "None\0".
    private static string? NoneStringName(Function f) =>
        f.Body.OfType<FlashData>()
            .FirstOrDefault(d => d.Bytes.SequenceEqual(new[] { 78, 111, 110, 101, 0 }))
            ?.Name;

    private static bool WritesNone(Function f)
    {
        string? noneName = NoneStringName(f);
        return noneName != null && f.Body.Any(i =>
            i is Call c && c.FunctionName == "uart_write_str"
            && c.Args.Count > 0 && c.Args[0] is FlashStrAddr fs && fs.Name == noneName);
    }

    /// Every jump that reads the tag byte named <paramref name="tag"/>.
    private static IEnumerable<Instruction> TagReads(Function f, string tag) =>
        f.Body.Where(i => i is JumpIfNotEqual j && ValName(j.Src1) == tag
                       || i is JumpIfEqual j2 && ValName(j2.Src1) == tag);

    private static string? ValName(Val v) => v switch
    {
        Variable vv => vv.Name,
        Temporary tt => tt.Name,
        _ => null,
    };

    // ── the print site dispatches on the tag ─────────────────────────────────

    [Fact]
    public void PrintOfAnUnnarrowedOptional_DispatchesOnTheTag()
    {
        var main = Main(ReadFn +
            "def main():\n" +
            "    r = read(GPIOR0.value)\n" +
            "    print(r)\n");

        TagReads(main, "main.r$tag").Should().NotBeEmpty(
            "print must consult the tag byte instead of refusing the read");
        main.Body.Any(i => CallsWriter(i, "uart_write_decimal_u8")).Should().BeTrue(
            "the has-value arm prints the payload through the decimal writer");
        WritesNone(main).Should().BeTrue(
            "the None arm writes the literal text CPython writes");
    }

    [Fact]
    public void PrintOfAnOptionalCallResult_DispatchesOnTheReturnedTag()
    {
        var main = Main(ReadFn +
            "def main():\n" +
            "    print(read(GPIOR0.value))\n");

        var call = main.Body.OfType<Call>().Single(c => c.FunctionName == "read");
        call.TagDst.Should().NotBeNull("the call returns payload + tag");
        main.Body.Any(i => i is JumpIfNotEqual j && ValName(j.Src1) == ValName(call.TagDst!))
            .Should().BeTrue("the dispatch reads the tag the call returned");
        WritesNone(main).Should().BeTrue();
    }

    [Fact]
    public void FStringInterpolation_DispatchesOnTheTag()
    {
        var main = Main(ReadFn +
            "def main():\n" +
            "    r = read(GPIOR0.value)\n" +
            "    print(f\"r={r}\")\n");

        TagReads(main, "main.r$tag").Should().NotBeEmpty();
        main.Body.Any(i => CallsWriter(i, "uart_write_decimal_u8")).Should().BeTrue();
        WritesNone(main).Should().BeTrue();
    }

    [Fact]
    public void OptionalBool_PrintsTrueFalseOrNone()
    {
        var main = Main(
            "def readb(k: uint8) -> Optional[bool]:\n" +
            "    if k == 0:\n" +
            "        return None\n" +
            "    return k > 3\n\n" +
            "def main():\n" +
            "    b = readb(GPIOR0.value)\n" +
            "    print(b)\n");

        TagReads(main, "main.b$tag").Should().NotBeEmpty();
        var texts = main.Body.OfType<FlashData>().Select(d => d.Bytes).ToList();
        texts.Should().Contain(b => b.SequenceEqual(new[] { 84, 114, 117, 101, 0 }),
            "the bool member prints True");
        texts.Should().Contain(b => b.SequenceEqual(new[] { 70, 97, 108, 115, 101, 0 }),
            "the bool member prints False");
        WritesNone(main).Should().BeTrue();
        main.Body.Any(i => CallsWriter(i, "uart_write_decimal_u8")).Should().BeFalse(
            "a bool member is True/False, not 1/0");
    }

    [Fact]
    public void ThreeMemberUnion_DispatchesPerMember()
    {
        var main = Main(
            "def readu(k: uint8) -> Union[int, float, None]:\n" +
            "    if k == 0:\n" +
            "        return None\n" +
            "    if k == 1:\n" +
            "        return 23\n" +
            "    return k + 0.5\n\n" +
            "def main():\n" +
            "    v = readu(GPIOR0.value)\n" +
            "    print(v)\n");

        main.Body.Count(i => i is JumpIfNotEqual j && ValName(j.Src1) == "main.v$tag")
            .Should().Be(2, "int and float each get a tag compare; None is the remainder");
        main.Body.Any(i => CallsWriter(i, "uart_write_decimal_i16")).Should().BeTrue(
            "the int member prints through the signed decimal writer");
        main.Body.Any(i => CallsWriter(i, "uart_write_float")).Should().BeTrue(
            "the float member prints through the float writer");
        WritesNone(main).Should().BeTrue();
    }

    [Fact]
    public void PrintOfANameProvenNone_WritesNoneWithoutATagRead()
    {
        var main = Main(ReadFn +
            "def main():\n" +
            "    r = read(GPIOR0.value)\n" +
            "    r = None\n" +
            "    print(r)\n");

        WritesNone(main).Should().BeTrue(
            "a provably-None optional still prints \"None\"");
        TagReads(main, "main.r$tag").Should().BeEmpty(
            "a compile-time-proven None pays no tag dispatch -- the fold is free");
        main.Body.Any(i => CallsWriter(i, "uart_write_decimal_u8")).Should().BeFalse(
            "the stale payload must not leak into the output");
    }

    [Fact]
    public void ADecidableOptional_KeepsTheUntaggedRead()
    {
        var main = Main(
            "def main():\n" +
            "    x: Optional[uint8] = 5\n" +
            "    print(x)\n");

        main.Body.Any(i => CallsWriter(i, "uart_write_decimal_u8")).Should().BeTrue();
        main.Body.Any(i => i is JumpIfNotEqual j && ValName(j.Src1) is { } n && n.EndsWith("$tag"))
            .Should().BeFalse("an Optional the compiler decides pays no dispatch");
    }

    // ── what stays refused ────────────────────────────────────────────────────

    [Fact]
    public void ArithmeticOnAnUnnarrowedOptional_DispatchesOnTheTag()
    {
        // RFC 0009 decision 7 (runtime form): `r + 1` lowers to a member dispatch --
        // the payload member adds, the None leaf raises TypeError where CPython faults.
        var main = Main(ReadFn +
            "def main():\n" +
            "    r = read(GPIOR0.value)\n" +
            "    print(r + 1)\n");
        main.Body.OfType<Binary>().Any(b => b.Op == PyMCU.IR.BinaryOp.Add)
            .Should().BeTrue("the payload member's leaf is the add");
        main.Body.Any(i => i is SignalError
                or Call { FunctionName: "__pymcu_unhandled_exn" or "__pymcu_raise" })
            .Should().BeTrue("the None leaf raises TypeError");
    }

    [Fact]
    public void AComparisonOtherThanIsNone_KeepsTheRefusal()
    {
        Refusal(ReadFn +
            "def main():\n" +
            "    r = read(GPIOR0.value)\n" +
            "    print(r == 5)\n").Should().Contain("may be None here");
    }

    [Fact]
    public void ANonOptionalParameter_DispatchesOnTheTag()
    {
        // RFC 0009 decision 7 (runtime form): `takes(r)` marshals per member --
        // the payload leaf is the call itself, the None leaf raises TypeError.
        var main = Main(ReadFn +
            "def takes(v: uint8) -> uint8:\n" +
            "    return v\n\n" +
            "def main():\n" +
            "    r = read(GPIOR0.value)\n" +
            "    print(takes(r))\n");
        TagReads(main, "main.r$tag").Should().NotBeEmpty(
            "the call boundary must consult the tag byte instead of refusing");
        main.Body.OfType<Call>().Any(c => c.FunctionName == "takes")
            .Should().BeTrue("the payload leaf is the call with the member marshalled");
        main.Body.Any(i => i is SignalError
                or Call { FunctionName: "__pymcu_unhandled_exn" or "__pymcu_raise" })
            .Should().BeTrue("the None leaf raises TypeError");
    }

    [Fact]
    public void AnIndex_DispatchesOnTheTag()
    {
        // `xs[r]` dispatches on r's tag: the int member indexes, None raises
        // the TypeError CPython raises for a None subscript.
        var main = Main(ReadFn +
            "def main():\n" +
            "    r = read(GPIOR0.value)\n" +
            "    xs: list[uint8] = [1, 2, 3]\n" +
            "    print(xs[r])\n");
        TagReads(main, "main.r$tag").Should().NotBeEmpty(
            "the subscript must consult the tag byte instead of refusing");
        main.Body.Any(i => i is LoadIndirect or ArrayLoad)
            .Should().BeTrue("the int-member leaf performs the element load");
        main.Body.Any(i => i is SignalError
                or Call { FunctionName: "__pymcu_unhandled_exn" or "__pymcu_raise" })
            .Should().BeTrue("the None leaf raises TypeError");
    }
}

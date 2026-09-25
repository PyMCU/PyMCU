using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An argument bound to a parameter of a declared integer width arrives narrowed to that
/// width. A real subroutine has always done this -- the call marshals the value into the
/// parameter's slot -- but an @inline expansion substituted the literal as written, so the
/// same callee saw a different value depending on how it was called.
///
/// The literals here are written through an explicit `uint8(...)` / `int8(...)` because
/// CheckConstantArgFitsParam now REFUSES the implicit form: a caller asking for 500 and a
/// callee seeing 244 is the silence that cost the blink below, and it is a diagnostic now
/// rather than a compiled program. The cast is not a way around that check, it is the way to
/// say the narrowing is meant, and it goes through the same narrowing: `wait(uint8(500))`
/// and `wait(244)` produce identical IR once the debug text is stripped. So these still
/// measure exactly what they measured -- that the narrowing happens, and that the inline
/// expansion agrees with the real subroutine -- while the refusal of the implicit form is
/// pinned by tests/stdlib/test_a_literal_that_does_not_fit_its_parameter_is_refused.py.
///
/// What it cost: `delay_ms(500)` reaches `_delay_ms_pic14e(ms: uint8)` in the stdlib, whose
/// body is `while i < ms` with i: uint8. Bound to 500 instead of 244, the range fold reads
/// the test as one a uint8 counter can never fail, deletes it, and everything after the loop
/// with it -- the Curiosity Nano blink wrote its LED once and then span forever, and the
/// second half of the blink was not in the hex at all.
/// </summary>
public class InlineArgumentWidthTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Body(string src, string fn) =>
        Gen(src).Functions.Single(f => f.Name == fn).Body;

    private const string Prelude =
        "from pymcu.types import uint8, uint16, int8, inline, ptr\n\n" +
        "PORTB: ptr[uint8] = ptr(0x0006)\n\n";

    private const string InlineCounter =
        "@inline\n" +
        "def wait(n: uint8):\n" +
        "    i: uint8 = 0\n" +
        "    while i < n:\n" +
        "        PORTB.value = i\n" +
        "        i = i + 1\n\n";

    [Fact]
    public void AnInlineLoopBoundedByAnOversizedLiteral_KeepsItsExitTest()
    {
        var body = Body(Prelude + InlineCounter + "wait(uint8(500))\n", "main");
        Assert.Contains(body, i => i is JumpIfGreaterOrEqual);
    }

    [Fact]
    public void AnOversizedLiteral_IsNarrowedToTheParametersWidth()
    {
        var body = Body(Prelude + InlineCounter + "wait(uint8(500))\n", "main");
        var jump = body.OfType<JumpIfGreaterOrEqual>().Single();
        Assert.Equal(244, ((Constant)jump.Src2).Value);
    }

    [Fact]
    public void TheStatementAfterTheInlineCall_SurvivesTheExpansion()
    {
        var body = Body(Prelude + InlineCounter + "wait(uint8(500))\nPORTB.value = 0xAA\n", "main");
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 0xAA } });
    }

    [Fact]
    public void TheInlineExpansionAgreesWithTheRealSubroutine()
    {
        const string outlined =
            "def wait(n: uint8):\n" +
            "    i: uint8 = 0\n" +
            "    while i < n:\n" +
            "        PORTB.value = i\n" +
            "        i = i + 1\n\n";
        var sub = Body(Prelude + outlined + "wait(uint8(500))\n", "wait");
        var expansion = Body(Prelude + InlineCounter + "wait(uint8(500))\n", "main");
        // The subroutine compares against its parameter slot, the expansion against the
        // constant the same argument narrows to. Both must still compare.
        Assert.Contains(sub, i => i is JumpIfGreaterOrEqual);
        Assert.Contains(expansion, i => i is JumpIfGreaterOrEqual);
    }

    [Fact]
    public void ALiteralThatFits_IsUntouched()
    {
        var body = Body(Prelude + InlineCounter + "wait(100)\n", "main");
        var jump = body.OfType<JumpIfGreaterOrEqual>().Single();
        Assert.Equal(100, ((Constant)jump.Src2).Value);
    }

    [Fact]
    public void ASignedParameter_TakesTheSignedNarrowing()
    {
        // 200 stored in an int8 slot is -56, so the body's sign test answers true. Reading
        // the parameter back as a uint8 gives 200 again, which is why the round trip cannot
        // show the narrowing and the sign test is what the assertion looks at.
        var body = Body(Prelude +
            "@inline\n" +
            "def sink(n: int8):\n" +
            "    if n < 0:\n" +
            "        PORTB.value = 1\n\n" +
            "sink(int8(200))\n", "main");
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 1 }, Dst: MemoryAddress { Address: 6 } });
    }
}

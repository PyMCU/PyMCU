using PyMCU.Frontend;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A rejection must not name a feature the program does not use. Every dotted decorator used to
/// be reported as an "Unknown property modifier", because the branch that catches `@X.Y` was
/// written for `@name.setter` and `@name.getter` and nothing narrowed it afterwards. So
/// `@micropython.native` sent the reader hunting for a `@property` they never wrote, and
/// `@property` is a real PyMCU feature, which makes it a plausible place to go and lose time.
///
/// Same defect class as the string methods reported as a nested-ZCA member access, fixed
/// separately in StringMethodDiagnosticTests: a fallthrough whose message describes the shape
/// its author had in mind rather than the shape in front of it. Both are fixed the same way,
/// by asking what the expression IS before choosing the sentence.
/// </summary>
public class DecoratorDiagnosticTests
{
    private static void Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static string Refusal(string src) =>
        Assert.ThrowsAny<Exception>(() => Parse(src)).Message;

    // ── the two that are no-ops here, so they are accepted ───────────────────

    [Theory]
    [InlineData("native")]
    [InlineData("viper")]
    public void TheMicroPythonCodegenDecoratorsAreAcceptedAndIgnored(string which)
    {
        // Both exist to ask MicroPython's interpreter to emit machine code for a function.
        // PyMCU compiles every function to machine code already, so they are no-ops BY
        // CONSTRUCTION rather than by omission, and an unmodified MicroPython program building
        // is the point of the compat layer.
        Parse($"@micropython.{which}\n" +
              "def f(x: uint8) -> uint8:\n" +
              "    return x + 77\n");
    }

    [Fact]
    public void AnAcceptedCodegenDecoratorDoesNotTurnTheFunctionIntoAProperty()
    {
        // Ignoring it must mean ignoring it, not falling into the setter/getter branch next door.
        var prog = new Parser(new Lexer(
            "@micropython.native\ndef f(x: uint8) -> uint8:\n    return x\n").Tokenize()).ParseProgram();

        var fn = Assert.Single(prog.Functions);
        Assert.False(fn.IsPropertyGetter);
        Assert.False(fn.IsPropertySetter);
    }

    // ── the rest are unknown DECORATORS, which is what they are ──────────────

    [Fact]
    public void AnUnknownDottedDecoratorIsNotCalledAPropertyModifier()
    {
        var msg = Refusal("@micropython.nonsense\ndef f():\n    pass\n");

        Assert.DoesNotContain("property modifier '", msg);
        Assert.Contains("Unknown decorator '@micropython.nonsense'", msg);
    }

    [Fact]
    public void TheRefusalSaysWhatWOULDMakeItAPropertyModifier()
    {
        // Naming the boundary is what stops the reader guessing at it.
        var msg = Refusal("@foo.bar\ndef f():\n    pass\n");

        Assert.Contains("'.setter'", msg);
        Assert.Contains("'.getter'", msg);
    }

    [Fact]
    public void TheDecoratorAsWrittenIsQuotedBack()
    {
        var msg = Refusal("@some_module.some_name\ndef f():\n    pass\n");

        Assert.Contains("@some_module.some_name", msg);
    }

    // ── and the feature the old message was about still works ────────────────

    [Fact]
    public void APropertySetterStillParses()
    {
        var prog = new Parser(new Lexer(
            "class C:\n" +
            "    @property\n" +
            "    def v(self) -> uint8:\n" +
            "        return self._v\n" +
            "    @v.setter\n" +
            "    def v(self, n: uint8):\n" +
            "        self._v = n\n").Tokenize()).ParseProgram();

        Assert.Contains(prog.GlobalStatements, st => st is ClassDef);
    }

    [Fact]
    public void ADottedPioDecoratorKeepsItsOwnMessage()
    {
        // `@rp2.asm_pio` had this right before the rest of the branch did, and it stays right.
        var msg = Refusal("@rp2.not_asm_pio\ndef f():\n    pass\n");

        Assert.Contains("@rp2.not_asm_pio", msg);
        Assert.DoesNotContain("property modifier '", msg);
    }
}

using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>raise CompileError(...)</c> is a compile-time diagnostic. RFC 0005 deferred
/// print applies to runtime exceptions only; a CompileError message must still
/// be a string constant (#435).
/// </summary>
[Trait("Issue", "435")]
public class CompileErrorMessageTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
    }

    private static CompilerError Fails(string src)
    {
        var act = () => Gen(src);
        return act.Should().Throw<CompilerError>(
            because: "raise CompileError is a diagnostic, so Generate must refuse").Which;
    }

    [Fact]
    public void MessageFromAModuleStringConstant_IsResolved()
    {
        Fails(
            "UNKNOWN: str = \"PIC10F200 has GP0, GP1, GP2 and GP3 only\"\n" +
            "def main():\n" +
            "    raise CompileError(UNKNOWN)\n")
            .Message.Should().Contain("PIC10F200 has GP0, GP1, GP2 and GP3 only",
                because: "the module-level str constant is the diagnostic text");
    }

    [Fact]
    public void MessageFromAnUnknownName_SaysWhyInsteadOfAParserError()
    {
        var msg = Fails(
            "def main():\n" +
            "    raise CompileError(NOPE)\n").Message;

        msg.Should().Contain("NOPE",
            because: "the unknown name has to appear so the author can find it");
        msg.Should().Contain("string constant known at compile time",
            because: "CompileError is not a deferred print; the message must exist now");
        msg.Should().Contain("NOPE: str =",
            because: "the diagnostic names the declaration the author should write");
        msg.Should().NotContain("Expected ')'",
            because: "a missing constant is not a parse error at the closing paren");
    }

    [Fact]
    public void AdjacentStringLiterals_AreConcatenated()
    {
        Fails(
            "def main():\n" +
            "    raise CompileError(\"Timer0 divides by a power of two from 2 to 256; \"\n" +
            "                       \"any other prescaler would leave it at reset\")\n")
            .Message.Should().Contain("from 2 to 256; any other prescaler",
                because: "adjacent literals are one message, the way CPython concatenates them");
    }

    [Fact]
    public void PlainStringLiteral_StillWorks()
    {
        Fails(
            "def main():\n" +
            "    raise CompileError(\"one literal\")\n")
            .Message.Should().Contain("one literal",
                because: "a string literal is the original CompileError path");
    }

    [Fact]
    public void RuntimeExceptionWithANamedMessage_AlsoResolves()
    {
        Fails(
            "REASON: str = \"no pull-up on this pin\"\n" +
            "@inline\ndef guard(name: str):\n" +
            "    if name == \"RC0\":\n" +
            "        raise NotImplementedError(REASON)\n" +
            "def main():\n" +
            "    guard(\"RC0\")\n")
            .Message.Should().Contain("no pull-up on this pin",
                because: "an uncaught raise in main still inlines the named constant into the halt text");
    }

    [Fact]
    public void ModuleConstant_BuiltFromAdjacentLiterals_ResolvesAsOneMessage()
    {
        Fails(
            "REASON: str = (\"the PIC10F200 gates its pull-ups with NOT_GPPU, one bit for \"\n" +
            "               \"the whole port, and OPTION is write-only\")\n" +
            "def main():\n" +
            "    raise CompileError(REASON)\n")
            .Message.Should().Contain("one bit for the whole port",
                because: "a parenthesized pair of literals is still one compile-time string");
    }
}

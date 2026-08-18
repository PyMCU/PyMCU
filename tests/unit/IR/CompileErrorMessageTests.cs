using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

public class CompileErrorMessageTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
    }

    [Fact]
    public void MessageFromAModuleStringConstant_IsResolved()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "UNKNOWN: str = \"PIC10F200 has GP0, GP1, GP2 and GP3 only\"\n" +
            "def main():\n" +
            "    raise CompileError(UNKNOWN)\n"));

        Assert.Contains("PIC10F200 has GP0, GP1, GP2 and GP3 only", ex.Message);
    }

    [Fact]
    public void MessageFromAnUnknownName_SaysWhyInsteadOfAParserError()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    raise CompileError(NOPE)\n"));

        Assert.Contains("NOPE", ex.Message);
        Assert.Contains("string constant known at compile time", ex.Message);
        Assert.Contains("NOPE: str =", ex.Message);
        Assert.DoesNotContain("Expected ')'", ex.Message);
    }

    [Fact]
    public void AdjacentStringLiterals_AreConcatenated()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    raise CompileError(\"Timer0 divides by a power of two from 2 to 256; \"\n" +
            "                       \"any other prescaler would leave it at reset\")\n"));

        Assert.Contains("from 2 to 256; any other prescaler", ex.Message);
    }

    [Fact]
    public void PlainStringLiteral_StillWorks()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    raise CompileError(\"one literal\")\n"));

        Assert.Contains("one literal", ex.Message);
    }

    [Fact]
    public void RuntimeExceptionWithANamedMessage_AlsoResolves()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "REASON: str = \"no pull-up on this pin\"\n" +
            "@inline\ndef guard(name: str):\n" +
            "    if name == \"RC0\":\n" +
            "        raise NotImplementedError(REASON)\n" +
            "def main():\n" +
            "    guard(\"RC0\")\n"));

        Assert.Contains("no pull-up on this pin", ex.Message);
    }

    [Fact]
    public void ModuleConstant_BuiltFromAdjacentLiterals_ResolvesAsOneMessage()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "REASON: str = (\"the PIC10F200 gates its pull-ups with NOT_GPPU, one bit for \"\n" +
            "               \"the whole port, and OPTION is write-only\")\n" +
            "def main():\n" +
            "    raise CompileError(REASON)\n"));

        Assert.Contains("one bit for the whole port", ex.Message);
    }
}

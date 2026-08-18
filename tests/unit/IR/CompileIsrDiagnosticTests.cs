using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

public class CompileIsrDiagnosticTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
    }

    private const string HandlerParamWithoutInline =
        "from pymcu.types import uint8, const, compile_isr\n" +
        "def irq_setup(handler: const = 0):\n" +
        "    compile_isr(handler, 0x0002)\n";

    [Fact]
    public void HandlerThatIsARuntimeParameter_IsAUserErrorNamingTheFunction()
    {
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => Gen(
            HandlerParamWithoutInline +
            "def main():\n" +
            "    x: uint8 = 1\n"));

        Assert.Contains("irq_setup", ex.Message);
        Assert.Contains("@inline", ex.Message);
        Assert.DoesNotContain("top-level function defined in the same translation unit.", ex.Message);
    }

    [Fact]
    public void HandlerThatIsARuntimeParameter_PointsAtTheCompileIsrLine()
    {
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => Gen(
            HandlerParamWithoutInline +
            "def main():\n" +
            "    x: uint8 = 1\n"));

        Assert.Equal(3, ex.Line);
    }

    [Fact]
    public void InlineHandlerFromACallSite_StillRegisters()
    {
        var ir = Gen(
            "from pymcu.types import uint8, const, inline, compile_isr\n" +
            "@inline\ndef irq_setup(handler: const = 0):\n" +
            "    compile_isr(handler, 0x0002)\n" +
            "def on_edge():\n" +
            "    x: uint8 = 1\n" +
            "def main():\n" +
            "    irq_setup(on_edge)\n");

        Assert.Contains(ir.Functions, f => f.Name == "on_edge" && f.IsInterrupt);
    }

    [Fact]
    public void HandlerInsideAnImportedModule_SaysTheLineIsNotFromTheCompiledFile()
    {
        var modAst = new Parser(new Lexer(HandlerParamWithoutInline).Tokenize()).ParseProgram();
        var mainAst = new Parser(new Lexer(
            "from pymcu.hal.fake import irq_setup\n" +
            "def main():\n" +
            "    x: uint8 = 1\n").Tokenize()).ParseProgram();
        var modules = new Dictionary<string, ProgramNode> { ["pymcu.hal.fake"] = modAst };

        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => new IRGenerator().Generate(mainAst, modules, new DeviceConfig { Arch = "avr" }));

        Assert.Contains("of the module that defines it", ex.Message);
        Assert.Equal(1, ex.Line);
    }
}

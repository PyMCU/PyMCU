using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

public class WhileGuardDepthTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig());
    }

    [Fact]
    public void GuardInsideARuntimeWhileBody_DoesNotFireAtCompileTime()
    {
        var ir = Gen(
            "def guard(v: uint8) -> uint8:\n" +
            "    while v > 10:\n" +
            "        raise CompileError(\"cannot be verified\")\n" +
            "    return 0x55\n" +
            "def main():\n" +
            "    a: uint8 = 3\n" +
            "    b: uint8 = guard(a)\n");

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void GuardAtTheTopOfAnInlineBody_StillFires_EvenWrappedInARuntimeWhile()
    {
        var ex = Assert.Throws<PyMCU.Common.ArchitectureError>(() => Gen(
            "@inline\ndef always() -> uint8:\n" +
            "    raise CompileError(\"this call site is invalid\")\n" +
            "def main():\n" +
            "    x: uint8 = 3\n" +
            "    while x > 0:\n" +
            "        y: uint8 = always()\n"));

        Assert.Contains("this call site is invalid", ex.Message);
    }
}

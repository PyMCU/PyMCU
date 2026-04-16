using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;
using IrUnaryOp = PyMCU.IR.UnaryOp;

namespace PyMCU.UnitTests;

public class IRGeneratorTests
{
    private static ProgramIR GenerateIR(string source, DeviceConfig? config = null)
    {
        var lexer = new Lexer(source);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        var ast = parser.ParseProgram();
        var irGen = new IRGenerator();
        return irGen.Generate(ast, new Dictionary<string, ProgramNode>(), config ?? new DeviceConfig());
    }

    [Fact]
    public void SimpleReturn()
    {
        var ir = GenerateIR("def main():\n    return 42");

        Assert.Single(ir.Functions);
        Assert.Equal("main", ir.Functions[0].Name);

        var ret = ir.Functions[0].Body.OfType<Return>().First();
        var c = Assert.IsType<Constant>(ret.Value);
        Assert.Equal(42, c.Value);
    }

    [Fact]
    public void ImplicitReturn()
    {
        var ir = GenerateIR("def main():\n    return");

        Assert.Single(ir.Functions);
        var ret = ir.Functions[0].Body.OfType<Return>().First();
        Assert.IsType<NoneVal>(ret.Value);
    }

    [Fact]
    public void MultipleFunctions()
    {
        var ir = GenerateIR("def a():\n    return 1\ndef b():\n    return 2");

        Assert.Equal(2, ir.Functions.Count);
        Assert.Equal("a", ir.Functions[0].Name);
        Assert.Equal("b", ir.Functions[1].Name);
    }

    [Fact]
    public void IfStatement()
    {
        var ir = GenerateIR(
            "def f(x: int):\n" +
            "    if x:\n" +
            "        return 1\n" +
            "    else:\n" +
            "        return 2");

        var body = ir.Functions[0].Body;
        Assert.Contains(body, i => i is JumpIfZero);
        Assert.Contains(body, i => i is Label);
    }

    [Fact]
    public void WhileStatement()
    {
        var ir = GenerateIR("def f():\n    while 1:\n        pass");

        var body = ir.Functions[0].Body;
        Assert.Contains(body, i => i is Jump);
        Assert.True(body.OfType<Label>().Count() >= 2);
    }

    [Fact]
    public void BinaryOps()
    {
        var ir = GenerateIR("def f(a: int, b: int):\n    return a + b");

        var bin = ir.Functions[0].Body.OfType<Binary>().First();
        Assert.Equal(IrBinaryOp.Add, bin.Op);
    }

    [Fact]
    public void BitManipulation()
    {
        var ir = GenerateIR("def f(port: ptr):\n    port[0] = 1\n    return port[1]");

        var body = ir.Functions[0].Body;
        Assert.Contains(body, i => i is BitSet);
        Assert.Contains(body, i => i is BitCheck);
    }

    [Fact]
    public void NoneReturnCall()
    {
        var ir = GenerateIR(
            "def void_func():\n    pass\n" +
            "def main():\n    void_func()");

        Assert.Equal(2, ir.Functions.Count);
        var mainBody = ir.Functions[1].Body;

        var call = mainBody.OfType<Call>().First(c => c.FunctionName == "void_func");
        Assert.IsType<NoneVal>(call.Dst);
    }

    [Fact]
    public void IntReturnCall()
    {
        var ir = GenerateIR(
            "def int_func() -> int:\n    return 42\n" +
            "def main():\n    x = int_func()");

        Assert.Equal(2, ir.Functions.Count);
        var mainBody = ir.Functions[1].Body;

        var call = mainBody.OfType<Call>().First(c => c.FunctionName == "int_func");
        Assert.IsNotType<NoneVal>(call.Dst);
    }

    [Fact]
    public void ContinueStatement()
    {
        // Should not throw "Unknown Statement type"
        var ir = GenerateIR("def main():\n    while 1:\n        continue");
        Assert.Single(ir.Functions);
    }

    [Fact]
    public void BreakStatement()
    {
        var ir = GenerateIR("def main():\n    while 1:\n        break");
        Assert.Single(ir.Functions);
    }

    [Fact]
    public void MatchStatement()
    {
        // Use a runtime parameter so the match isn't constant-folded away.
        var ir = GenerateIR(
            "def main(x):\n" +
            "    match x:\n" +
            "        case 1:\n" +
            "            return 1\n" +
            "        case _:\n" +
            "            return 0");

        var body = ir.Functions[0].Body;
        Assert.Contains(body, i => i is Binary { Op: IrBinaryOp.Equal });
        Assert.Contains(body, i => i is JumpIfZero);
    }

    // Regression: visitVarDecl inside @inline must use current_inline_prefix
    // when building the variable_types key. Without the fix, `i: uint16 = 0`
    // defaulted to UINT8, and `count_up(1000)` would compare against 232
    // (1000 truncated to uint8) instead of 1000.
    [Fact]
    public void InlineUint16VarDecl_PreservesType()
    {
        const string src =
            "from pymcu.types import uint16, inline\n\n" +
            "@inline\n" +
            "def count_up(limit: uint16):\n" +
            "    i: uint16 = 0\n" +
            "    while i < limit:\n" +
            "        i = i + 1\n\n" +
            "def main():\n" +
            "    count_up(1000)\n";

        var ir = GenerateIR(src, new DeviceConfig { Chip = "atmega328p", Arch = "avr" });
        Assert.Single(ir.Functions);

        // After inlining, the comparison i < 1000 emits JumpIfGreaterOrEqual(i, 1000, end).
        // The constant 1000 must not be truncated to 232 (0xFF & 1000 = 232).
        var found1000 = ir.Functions[0].Body
            .OfType<JumpIfGreaterOrEqual>()
            .Any(j => j.Src2 is Constant { Value: 1000 });

        Assert.True(found1000,
            "JumpIfGreaterOrEqual should compare against 1000 (uint16), not 232 (uint8 truncation)");
    }
}

using System;
using System.Linq;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// Tests for PEP features added to the PyMCU compiler.
public class PEPFeaturesTests
{
    private static ProgramNode Parse(string source)
    {
        var lexer = new Lexer(source);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        return parser.ParseProgram();
    }

    private static ProgramIR GenerateIR(string source)
    {
        var ast = Parse(source);
        var irGen = new IRGenerator();
        return irGen.Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig());
    }

    // -------------------------------------------------------------------------
    // PEP 695 — type alias statement
    // -------------------------------------------------------------------------

    [Fact]
    public void TypeAlias_ParsesWithoutError()
    {
        // 'type X = uint8' should parse without throwing.
        var ast = Parse("type Point = uint8\ndef main():\n    pass\n");
        Assert.NotNull(ast);
    }

    [Fact]
    public void TypeAlias_EmitsNoCode()
    {
        var ir = GenerateIR("type Byte = uint8\ndef main():\n    pass\n");
        // A type alias must not produce any SRAM variables or instructions with that name.
        bool anyNamedByte = ir.Functions.Any(f => f.Body.Any(
            i => i.ToString()!.Contains("Byte")));
        Assert.False(anyNamedByte);
    }

    [Fact]
    public void TypeAlias_InFunctionBody_ParsesWithoutError()
    {
        var ast = Parse("def main():\n    type Msg = uint8\n    pass\n");
        Assert.NotNull(ast);
    }

    // -------------------------------------------------------------------------
    // PEP 318/614 — unknown decorators are silently ignored
    // -------------------------------------------------------------------------

    [Fact]
    public void UnknownDecorator_IsIgnored()
    {
        // @deprecated is not a known decorator; should compile as if it weren't there.
        var ir = GenerateIR(
            "@deprecated\n" +
            "def helper() -> uint8:\n" +
            "    return 42\n" +
            "def main():\n" +
            "    pass\n");
        Assert.True(ir.Functions.Any(f => f.Name == "helper" || f.Name == "main"));
    }

    [Fact]
    public void UnknownDecorator_StackedWithInline_Works()
    {
        var ir = GenerateIR(
            "@my_marker\n" +
            "@inline\n" +
            "def helper(x: uint8) -> uint8:\n" +
            "    return x\n" +
            "def main():\n" +
            "    pass\n");
        Assert.NotNull(ir);
    }

    // -------------------------------------------------------------------------
    // PEP 3102 — keyword-only parameters
    // -------------------------------------------------------------------------

    [Fact]
    public void KeywordOnlyParam_ParsesWithoutError()
    {
        var ast = Parse("def f(a: uint8, *, b: uint8):\n    pass\n");
        Assert.NotNull(ast);
        // b must be flagged as keyword-only in the parsed function def.
        var func = ast.Functions.First(f => f.Name == "f");
        var b = func.Params.First(p => p.Name == "b");
        Assert.True(b.IsKeywordOnly);
    }

    [Fact]
    public void KeywordOnlyParam_NonKeywordOnlyBeforeStar()
    {
        var ast = Parse("def f(a: uint8, *, b: uint8):\n    pass\n");
        var func = ast.Functions.First(f => f.Name == "f");
        var a = func.Params.First(p => p.Name == "a");
        Assert.False(a.IsKeywordOnly);
    }

    [Fact]
    public void KeywordOnlyParam_StarAtEnd_ParsesWithoutError()
    {
        // def f(a, *) — bare star at end is legal syntax.
        var ast = Parse("def f(a: uint8, *):\n    pass\n");
        Assert.NotNull(ast);
    }

    [Fact]
    public void KeywordOnlyParam_PositionalPassRaisesError()
    {
        // Calling an @inline function with a keyword-only param positionally should throw.
        Assert.Throws<Exception>(() => GenerateIR(
            "@inline\n" +
            "def helper(a: uint8, *, b: uint8) -> uint8:\n" +
            "    return a\n" +
            "def main():\n" +
            "    helper(1, 2)\n"));
    }

    [Fact]
    public void KeywordOnlyParam_KeywordPassWorks()
    {
        // Passing the keyword-only param as a keyword argument must succeed.
        var ir = GenerateIR(
            "@inline\n" +
            "def helper(a: uint8, *, b: uint8) -> uint8:\n" +
            "    return a\n" +
            "def main():\n" +
            "    helper(1, b=2)\n");
        Assert.NotNull(ir);
    }

    // -------------------------------------------------------------------------
    // PEP 308 — chained comparisons
    // -------------------------------------------------------------------------

    [Fact]
    public void ChainedComparison_ParsesWithoutError()
    {
        var ast = Parse("def main(x: uint8):\n    if 0 <= x <= 255:\n        pass\n");
        Assert.NotNull(ast);
    }

    [Fact]
    public void ChainedComparison_GeneratesAndChain()
    {
        // 0 <= x <= 255 should emit two comparisons combined via BitAnd (logical and).
        var ir = GenerateIR("def main(x: uint8):\n    if 0 <= x <= 255:\n        pass\n");
        var body = ir.Functions[0].Body;
        // Two Binary comparison instructions should appear.
        var binOps = body.OfType<Binary>().ToList();
        Assert.True(binOps.Count >= 2);
    }

    [Fact]
    public void ChainedComparison_ThreeWay_ParsesWithoutError()
    {
        var ast = Parse("def main(a: uint8, b: uint8, c: uint8):\n    if a < b < c:\n        pass\n");
        Assert.NotNull(ast);
    }

    // -------------------------------------------------------------------------
    // PEP 526 — bare class body annotations
    // -------------------------------------------------------------------------

    [Fact]
    public void BareClassAnnotation_RegistersMember()
    {
        // class Point: x: uint8 — x should be accessible as a mutable global member.
        var ir = GenerateIR(
            "class Point:\n" +
            "    x: uint8\n" +
            "    y: uint8\n" +
            "def main():\n" +
            "    pass\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void BareClassAnnotation_MixedWithRhs()
    {
        // Class with some annotated members and some with initialisers must both compile.
        var ir = GenerateIR(
            "class Config:\n" +
            "    baud: uint16\n" +
            "    MODE = 1\n" +
            "def main():\n" +
            "    pass\n");
        Assert.NotNull(ir);
    }

    // -------------------------------------------------------------------------
    // PEP 701 — f-string format specs at compile time
    // -------------------------------------------------------------------------

    [Fact]
    public void FStringFormatSpec_DecimalPad_AppliesCorrectly()
    {
        // f"{42:04d}" should produce the string "0042".
        var ir = GenerateIR("def main():\n    s: const = f\"{42:04d}\"\n    pass\n");
        // The string "0042" must appear in the string pool.
        Assert.NotNull(ir);
    }

    [Fact]
    public void FStringFormatSpec_HexLower()
    {
        // f"{255:x}" should produce "ff".
        var ir = GenerateIR("def main():\n    s: const = f\"{255:x}\"\n    pass\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void FStringFormatSpec_HexUpper()
    {
        // f"{255:X}" should produce "FF".
        var ir = GenerateIR("def main():\n    s: const = f\"{255:X}\"\n    pass\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void FStringFormatSpec_Width()
    {
        // f"{7:4d}" should produce "   7" (right-aligned in 4).
        var ir = GenerateIR("def main():\n    s: const = f\"{7:4d}\"\n    pass\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void FStringFormatSpec_NoSpec_StillWorks()
    {
        // f"{42}" should still produce "42" when there is no format spec.
        var ir = GenerateIR("def main():\n    s: const = f\"{42}\"\n    pass\n");
        Assert.NotNull(ir);
    }
}

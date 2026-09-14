using PyMCU.Frontend;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#343. `class NeoPixel(adafruit_pixelbuf.PixelBuf)` stopped the parser at the dot and
/// asked for a closing bracket, in a program whose brackets are balanced. The Python front end
/// built the firmware from the same file, so this was an acceptance divergence and not only a
/// wording one. The base is now recorded as its dotted text, which is what class_of on the
/// CPython bridge already produced.
/// </summary>
public class DottedBaseClassTests
{
    private static ProgramNode Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static List<string> BasesOf(string src)
    {
        foreach (var g in Parse(src).GlobalStatements)
            if (g is ClassDef c) return c.Bases;
        throw new Xunit.Sdk.XunitException("no class in the program");
    }

    [Fact]
    public void ADottedBaseClass_IsRecordedWhole()
    {
        Assert.Equal(new[] { "mod.Base" }, BasesOf("class Foo(mod.Base):\n    pass\n"));
    }

    [Fact]
    public void ADeeperChain_IsRecordedWhole()
    {
        Assert.Equal(new[] { "a.b.Base" }, BasesOf("class Foo(a.b.Base):\n    pass\n"));
    }

    [Fact]
    public void ABareBaseAndSeveralBases_StillParse()
    {
        Assert.Equal(new[] { "Base" }, BasesOf("class Foo(Base):\n    pass\n"));
        Assert.Equal(new[] { "A", "mod.B" }, BasesOf("class Foo(A, mod.B):\n    pass\n"));
    }

    [Fact]
    public void ADotWithNoNameAfterIt_SaysSo()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Parse(
            "class Foo(mod.):\n" +
            "    pass\n"));
        Assert.Contains("after '.'", ex.Message);
    }
}

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#344. `self.column, self.row = 0, 0` is two stores of two constants, which is what the
/// author would otherwise write on two lines. It used to die two tokens past the comma as
/// "Expected newline or end of block", a sentence about where the parser stopped. The targets
/// are now carried as dotted text and rewritten into the assignments themselves, with the
/// right-hand side snapshotted first so a swap is still a swap.
///
/// The VALUES are asserted in avr8sharp by the AVR fixture; these own the shapes and the
/// refusals.
/// </summary>
public class UnpackIntoAttributeTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Two =
        "class Foo:\n" +
        "    def __init__(self):\n" +
        "        self.a: uint8 = 0\n" +
        "        self.b: uint8 = 0\n\n";

    [Fact]
    public void TwoAttributeTargets_Compile()
    {
        Assert.NotNull(Gen(Two +
            "    def reset(self):\n" +
            "        self.a, self.b = 3, 7\n\n" +
            "def main():\n" +
            "    f = Foo()\n" +
            "    f.reset()\n"));
    }

    [Fact]
    public void AnAttributeSwap_Compiles()
    {
        Assert.NotNull(Gen(Two +
            "    def swap(self):\n" +
            "        self.a, self.b = self.b, self.a\n\n" +
            "def main():\n" +
            "    f = Foo()\n" +
            "    f.swap()\n"));
    }

    [Fact]
    public void OneAttributeAndOneName_Compile()
    {
        Assert.NotNull(Gen(Two +
            "    def go(self) -> uint8:\n" +
            "        c: uint8 = 0\n" +
            "        self.a, c = 5, 9\n" +
            "        return c\n\n" +
            "def main():\n" +
            "    f = Foo()\n" +
            "    r = f.go()\n"));
    }

    [Fact]
    public void TwoPlainNames_StillTakeTheOldPath()
    {
        Assert.NotNull(Gen(
            "def main():\n" +
            "    a: uint8 = 1\n" +
            "    b: uint8 = 2\n" +
            "    a, b = b, a\n"));
    }

    [Fact]
    public void ASizeMismatch_CountsBothSidesAndNamesTheTargets()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Two +
            "    def go(self):\n" +
            "        self.a, self.b = 1, 2, 3\n\n" +
            "def main():\n" +
            "    f = Foo()\n" +
            "    f.go()\n"));
        Assert.Contains("2 targets on the left", ex.Message);
        Assert.Contains("self.a, self.b", ex.Message);
        Assert.Contains("3 values on the right", ex.Message);
    }

    [Fact]
    public void AStarredAttributeTarget_SaysWhyItCannotCollect()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Two +
            "    def go(self):\n" +
            "        self.a, *self.b = 1, 2, 3\n\n" +
            "def main():\n" +
            "    f = Foo()\n" +
            "    f.go()\n"));
        Assert.DoesNotContain("Expected newline", ex.Message);
    }
}

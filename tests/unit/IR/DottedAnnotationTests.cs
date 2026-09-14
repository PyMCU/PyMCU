using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#342. `p: module.Class` is the spelling every CircuitPython library uses, because a
/// module-level import is what it has. The parser used to end the parameter list at the '.'
/// and ask for a closing bracket, so the reader was told about a bracket for writing a name.
/// The dotted name is now read whole and resolved with the other annotation names.
///
/// These tests own the PARSE and the SENTENCE. Acceptance of a dotted name that really does
/// reach a class needs a second module, which this single-source harness has no way to build;
/// the AVR fixture adafruit-ssd1306-upstream owns that half.
/// </summary>
public class DottedAnnotationTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    [Fact]
    public void ADottedAnnotation_IsRefusedByNameAndNotByBracket()
    {
        string msg = Refusal(
            "def take(v: mod.Vec) -> uint8:\n" +
            "    return 1\n\n" +
            "def main():\n" +
            "    y = take(1)\n");
        Assert.Contains("mod.Vec", msg);
        Assert.DoesNotContain("Expected ')'", msg);
        Assert.DoesNotContain("Expected ']'", msg);
    }

    [Fact]
    public void ADottedReturnAndLocalAnnotation_AreAlsoReadWhole()
    {
        Assert.Contains("mod.Vec", Refusal(
            "def take(v: uint8) -> mod.Vec:\n" +
            "    return 1\n\n" +
            "def main():\n" +
            "    y = take(1)\n"));
        Assert.Contains("mod.Vec", Refusal(
            "def main():\n" +
            "    w: mod.Vec = 1\n"));
    }

    [Fact]
    public void ADeeperChain_IsReadWhole()
    {
        Assert.Contains("a.b.Vec", Refusal(
            "def take(v: a.b.Vec) -> uint8:\n" +
            "    return 1\n\n" +
            "def main():\n" +
            "    y = take(1)\n"));
    }

    [Fact]
    public void TheBareSpellingOfAClassInScope_StillCompiles()
    {
        var ir = Gen(
            "class Vec:\n" +
            "    def __init__(self, x: uint8):\n" +
            "        self.x = x\n\n" +
            "def take(v: Vec) -> uint8:\n" +
            "    return v.x\n\n" +
            "def main():\n" +
            "    y = take(Vec(3))\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void ADotWithNoNameAfterIt_SaysSo()
    {
        Assert.Contains("after '.'", Refusal(
            "def take(v: mod.) -> uint8:\n" +
            "    return 1\n\n" +
            "def main():\n" +
            "    y = take(1)\n"));
    }

    [Fact]
    public void ADottedNameOrNone_IsTheDottedName()
    {
        // `X | None` means X, and a dotted X is no different: None-ness is a compile-time
        // property, so there is no second width to reconcile. This used to be refused as a
        // union, which is the shape five Adafruit libraries stop on.
        Assert.Contains("unknown type", Refusal(
            "def take(v: mod.Vec | None) -> uint8:\n" +
            "    return 1\n\n" +
            "def main():\n" +
            "    y = take(1)\n"));
    }

    [Fact]
    public void ADottedUnionOfTwoRealTypes_IsStillAUnion()
    {
        // The refusal that remains, and the reason the dotted loop still has to be walked
        // before the judgement: two real types have no width they share.
        Assert.Contains("union type annotation", Refusal(
            "def take(v: mod.Vec | uint8) -> uint8:\n" +
            "    return 1\n\n" +
            "def main():\n" +
            "    y = take(1)\n"));
    }
}

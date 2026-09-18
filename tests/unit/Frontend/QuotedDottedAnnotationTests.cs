using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A quoted annotation is the same text without quotes. <c>"Vec"</c> already
/// was; <c>"mod.Cls"</c> is the dotted class an unquoted <c>busio.I2C</c>
/// already is. Adafruit si7021 writes
/// <c>obj: "adafruit_si7021.SI7021"</c>.
/// </summary>
public class QuotedDottedAnnotationTests
{
    private static ProgramNode Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static string FirstParamType(string src) =>
        Parse(src).Functions[0].Params[0].Type ?? "";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            Parse(src),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    [Fact]
    public void AQuotedDottedName_IsTheDottedAnnotation()
    {
        FirstParamType("def f(obj: \"adafruit_si7021.SI7021\"):\n    pass\n")
            .Should().Be("adafruit_si7021.SI7021",
                because: "\"adafruit_si7021.SI7021\" is the same annotation as unquoted adafruit_si7021.SI7021");
    }

    [Fact]
    public void AQuotedBareName_IsStillTheBareName()
    {
        FirstParamType("def f(obj: \"SI7021\"):\n    pass\n")
            .Should().Be("SI7021",
                because: "\"SI7021\" stays the forward-reference spelling #261 already accepted");
    }

    [Fact]
    public void OptionalTypeOfAQuotedDottedName_Compiles()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "class Pack:\n" +
            "    class Dev:\n" +
            "        def __init__(self):\n" +
            "            self.a: uint8 = 1\n" +
            "def read(obj: \"Pack.Dev\", objtype: Optional[Type[\"Pack.Dev\"]] = None) -> uint8:\n" +
            "    return obj.a\n" +
            "def main() -> None:\n" +
            "    n = read(Pack.Dev())\n");
        ir.Should().NotBeNull(
            because: "Optional[Type[\"Pack.Dev\"]] is typing-only around the same quoted dotted class");
    }

    [Fact]
    public void AQuotedDottedNestedClass_Compiles()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "class Pack:\n" +
            "    class Dev:\n" +
            "        def __init__(self):\n" +
            "            self.a: uint8 = 1\n" +
            "def read(obj: \"Pack.Dev\") -> uint8:\n" +
            "    return obj.a\n" +
            "def main() -> None:\n" +
            "    n = read(Pack.Dev())\n");
        ir.Should().NotBeNull(
            because: "a quoted nested class is the same type as unquoted Pack.Dev");
    }
}

using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A PEP 484 FORWARD REFERENCE: the type named as a string literal, because the name is not
/// bound yet where the annotation is written. `def __enter__(self) -> "BH1750":` on a class's
/// own methods is how an Adafruit driver spells its context manager, and CPython, MicroPython
/// and CircuitPython all take it -- at run time the annotation is a string nobody evaluates.
///
/// Before the fix the compiler answered `unknown type '"BH1750"' in the annotation (did you
/// mean 'BH1750'?)`, naming the answer and refusing it in the same sentence. Measured on the
/// upstream libraries: adafruit_bh1750's simpletest went from that refusal to a 6488-byte
/// build with no edit to the library.
///
/// Normalisation lives in AnnotationText, the one place BOTH front ends reach, so the C#
/// parser and the CPython bridge cannot disagree about it.
/// </summary>
public class ForwardReferenceAnnotationTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Theory]
    [InlineData("\"uint16\"")]
    [InlineData("'uint16'")]
    [InlineData("Optional[\"uint16\"]")]
    public void AQuotedTypeNameIsTheTypeItNames(string annotation)
    {
        Assert.Equal(AnnotationText.Normalize("uint16"), AnnotationText.Normalize(annotation));
    }

    [Fact]
    public void AQuotedClassNameOnAReturnAnnotationCompiles()
    {
        // Red before the fix: unknown type '"Sensor"' in the annotation.
        var src = """
            class Sensor:
                def __init__(self, base: int) -> None:
                    self.base = base

                def bump(self) -> "Sensor":
                    self.base = self.base + 1
                    return self

            def main() -> None:
                s = Sensor(3)
                s.bump()
            """;
        var ir = Gen(src);
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void AQuotedNameIsStillCheckedAsAName()
    {
        // The quotes say nothing about the type, so a quoted name that names nothing is
        // refused exactly as the bare one is -- the fix takes the quotes off, it does not
        // wave the annotation through.
        var src = """
            def read(x: "Nonexistent") -> int:
                return 1
            """;
        var e = Assert.ThrowsAny<CompilerError>(() => Gen(src));
        Assert.Contains("Nonexistent", e.Message);
        Assert.DoesNotContain("\"Nonexistent\"", e.Message);
    }
}

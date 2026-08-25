using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#106. A list field could not be declared in either spelling, and neither message
/// described the program.
///
/// Annotated, the T[N] reading split "list[uint8]" into elem "list" and size "uint8", so the
/// size resolver reported the ELEMENT TYPE as not being a compile-time constant, for a
/// program containing no size expression. Unannotated, the list literal reached the generic
/// expression visitor and the reader was shown "Unknown Expression type: ListExpr", the name
/// of a compiler class.
///
/// A growable list is heap-allocated and a field is flattened into the instance, so there is
/// nothing to route it to. Both spellings are now refused by name and point at the fixed-size
/// form, which already works.
/// </summary>
public class ListFieldDiagnosticTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
    }

    private const string Annotated =
        "class Box:\n" +
        "    def __init__(self, k: uint8):\n" +
        "        self.buf: list[uint8] = [0, 0, 0]\n" +
        "def main():\n" +
        "    b = Box(1)\n";

    private const string Unannotated =
        "class Box:\n" +
        "    def __init__(self, k: uint8):\n" +
        "        self.buf = [0, 0, 0]\n" +
        "def main():\n" +
        "    b = Box(1)\n";

    [Fact]
    public void AnnotatedListField_NamesTheConstructInsteadOfTheElementType()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Annotated));

        Assert.Contains("growable list cannot be a field", ex.Message);
        Assert.DoesNotContain("Array size 'uint8'", ex.Message);
    }

    [Fact]
    public void AnnotatedListField_OffersTheFixedSpellingWithTheSizeFromTheInitialiser()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Annotated));

        Assert.Contains("self.buf: uint8[3] = [...]", ex.Message);
    }

    [Fact]
    public void UnannotatedListField_DoesNotLeakAnAstClassName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Unannotated));

        Assert.DoesNotContain("ListExpr", ex.Message);
        Assert.Contains("needs a declared size", ex.Message);
        Assert.Contains("self.buf: uint8[3] = [...]", ex.Message);
    }

    [Fact]
    public void TheFixedSizeFieldTheMessageAdvises_Compiles()
    {
        // The advice has to work, or it is the same gap in a friendlier voice.
        var ir = Gen(
            "class Box:\n" +
            "    def __init__(self, k: uint8):\n" +
            "        self.buf: uint8[3] = [0, 0, 0]\n" +
            "        self.buf[0] = k\n" +
            "def main():\n" +
            "    b = Box(1)\n");

        Assert.NotNull(ir);
    }
}

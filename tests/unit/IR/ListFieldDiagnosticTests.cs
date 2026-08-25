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
/// Both spellings are now the fixed array the literal describes, which is what the field can
/// hold: as many elements as the literal has, of the annotated element type, or of the type
/// the widest literal needs. What is still refused is a declaration with no length to take.
/// </summary>
public class ListFieldDiagnosticTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
    }

    private static string Box(string declaration) =>
        "class Box:\n" +
        "    def __init__(self, k: uint8):\n" +
        $"        {declaration}\n" +
        "        self.buf[0] = k\n" +
        "def main():\n" +
        "    b = Box(1)\n";

    [Fact]
    public void AnnotatedListField_Compiles()
        => Assert.NotNull(Gen(Box("self.buf: list[uint8] = [0, 0, 0]")));

    [Fact]
    public void UnannotatedListField_Compiles()
        => Assert.NotNull(Gen(Box("self.buf = [0, 0, 0]")));

    // The spelling that always worked keeps working, and is the same lowering.
    [Fact]
    public void TheFixedSizeField_StillCompiles()
        => Assert.NotNull(Gen(Box("self.buf: uint8[3] = [0, 0, 0]")));

    // list[T] has no room for a length, so without a literal there is nothing to size the
    // field from. Refused by name, quoting what was written.
    [Fact]
    public void AListFieldWithNoLiteral_SaysWhereItsLengthWouldComeFrom()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "class Box:\n" +
            "    def __init__(self, k: uint8):\n" +
            "        self.buf: list[uint8]\n" +
            "def main():\n" +
            "    b = Box(1)\n"));

        Assert.Contains("self.buf: list[uint8]", ex.Message);
        Assert.Contains("[0, 0, 0]", ex.Message);
        Assert.DoesNotContain("Array size 'uint8'", ex.Message);
    }

    // A list of instances is a different construct and keeps its own lowering; it must not
    // be swallowed by the constant-literal path, and it must not leak an AST class name.
    [Fact]
    public void AFieldHoldingSomethingOtherThanConstants_StillAsksForASize()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "class Part:\n" +
            "    def __init__(self, n: uint8):\n" +
            "        self.n = n\n" +
            "class Box:\n" +
            "    def __init__(self, k: uint8):\n" +
            "        self.parts = [Part(1), Part(2)]\n" +
            "def main():\n" +
            "    b = Box(1)\n"));

        Assert.DoesNotContain("ListExpr", ex.Message);
        Assert.Contains("needs a declared size", ex.Message);
    }
}

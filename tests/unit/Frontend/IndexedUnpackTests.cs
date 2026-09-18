using System.Linq;
using FluentAssertions;
using PyMCU.Frontend;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>word[i], crc[i] = rhs</c> is a named unpack plus indexed stores.
/// Adafruit sht31d writes
/// <c>word[i*2], crc[i*2], word[(i*2)+1], crc[(i*2)+1] = struct.unpack(...)</c>.
/// TupleUnpackStmt only carries names, so the parser used to stop at the comma
/// as "Expected newline or end of block".
/// </summary>
public class IndexedUnpackTests
{
    private static ProgramNode Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static Block UnpackBlock(string src)
    {
        var fn = Parse(src).Functions[0];
        return fn.Body.Statements.OfType<Block>().Single();
    }

    private const string Four =
        "from pymcu.types import uint8, uint16\n" +
        "def main() -> uint8:\n" +
        "    word: uint16[4] = [0, 0, 0, 0]\n" +
        "    crc: uint8[4] = [0, 0, 0, 0]\n" +
        "    word[0], crc[0], word[1], crc[1] = 1, 2, 3, 4\n" +
        "    return crc[0]\n";

    [Fact]
    public void FourSubscriptTargets_DesugarToNamedUnpackThenStores()
    {
        var block = UnpackBlock(Four);
        var bind = block.Statements[0].Should().BeOfType<AssignStmt>().Subject;
        bind.Target.Should().BeOfType<VariableExpr>().Which.Name.Should().Be("__isub0",
            because: "the RHS is bound to one name so t = struct.unpack(...) can lower");
        bind.Value.Should().BeOfType<TupleExpr>().Which.Elements.Should().HaveCount(4,
            because: "the comma RHS is the four-value tuple a name already accepts");
        block.Statements.Should().HaveCount(5,
            because: "one bind plus one indexed store per target");
        var firstStore = block.Statements[1].Should().BeOfType<AssignStmt>().Subject;
        firstStore.Target.Should().BeOfType<IndexExpr>();
        firstStore.Value.Should().BeOfType<IndexExpr>().Which.Index.Should().BeOfType<IntegerLiteral>()
            .Which.Value.Should().Be(0, because: "the first target is t[0]");
        block.Statements[4].Should().BeOfType<AssignStmt>().Which.Target.Should().BeOfType<IndexExpr>();
    }

    [Fact]
    public void BothFrontEnds_AcceptASubscriptUnpack()
    {
        var cs = Record.Exception(() => Parse(Four));
        var py = Record.Exception(() => PythonAstReader.ParseSource(Four, "main.py"));
        cs.Should().BeNull(because: "the C# parser desugars word[i], crc[i] = ... into t = rhs; t[k] stores");
        py.Should().BeNull(because: "the CPython bridge desugars the same subscript unpack");
    }

    [Fact]
    public void BothFrontEnds_EmitTheSameTempNames()
    {
        var cs = UnpackBlock(Four);
        var pyFn = PythonAstReader.ParseSource(Four, "main.py").Functions[0];
        var py = pyFn.Body.Statements.OfType<Block>().Single();
        var csNames = ((AssignStmt)cs.Statements[0]).Target.Should().BeOfType<VariableExpr>().Subject.Name;
        var pyNames = ((AssignStmt)py.Statements[0]).Target.Should().BeOfType<VariableExpr>().Subject.Name;
        pyNames.Should().Be(csNames,
            because: "the AST is the contract; temp names must not drift between front ends");
    }
}

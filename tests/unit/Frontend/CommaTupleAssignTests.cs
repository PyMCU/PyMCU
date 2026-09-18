using System.Linq;
using FluentAssertions;
using PyMCU.Frontend;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>x = 1, 2, 3</c> is <c>x = (1, 2, 3)</c>. Adafruit framebuf writes
/// <c>fill = (color >> 16) &amp; 255, (color >> 8) &amp; 255, color &amp; 255</c>
/// without parentheses around the whole RHS. The parser stopped at the first
/// comma as "Expected newline or end of block". <c>return a, b</c> and
/// <c>a, b = 1, 2</c> already wrapped the same comma.
/// </summary>
public class CommaTupleAssignTests
{
    private static ProgramNode Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static AssignStmt FirstAssign(string src)
    {
        var stmt = Parse(src).GlobalStatements.OfType<AssignStmt>().First();
        return stmt;
    }

    [Fact]
    public void AnUnparenthesizedTupleRhs_IsATupleExpr()
    {
        var value = FirstAssign("x = 1, 2, 3\n").Value;
        var tup = value.Should().BeOfType<TupleExpr>().Subject;
        tup.Elements.Should().HaveCount(3,
            because: "x = 1, 2, 3 is the three-element tuple (1, 2, 3), not a truncated x = 1");
        tup.Elements[0].Should().BeOfType<IntegerLiteral>().Which.Value.Should().Be(1);
        tup.Elements[1].Should().BeOfType<IntegerLiteral>().Which.Value.Should().Be(2);
        tup.Elements[2].Should().BeOfType<IntegerLiteral>().Which.Value.Should().Be(3);
    }

    [Fact]
    public void AParenthesizedTupleRhs_IsUnchanged()
    {
        var value = FirstAssign("x = (1, 2, 3)\n").Value;
        value.Should().BeOfType<TupleExpr>().Which.Elements.Should().HaveCount(3,
            because: "parentheses already built the tuple; the comma wrap must not nest another");
    }

    [Fact]
    public void TheFramebufFillSpelling_Parses()
    {
        var src =
            "color = 1\n" +
            "fill = (color >> 16) & 255, (color >> 8) & 255, color & 255\n";
        var fill = Parse(src).GlobalStatements.OfType<AssignStmt>()
            .Single(a => a.Target is VariableExpr ve && ve.Name == "fill");
        fill.Value.Should().BeOfType<TupleExpr>().Which.Elements.Should().HaveCount(3,
            because: "framebuf's fill = shifted, shifted, masked is three tuple elements");
    }

    [Fact]
    public void BothFrontEnds_AcceptTheUnparenthesizedTupleRhs()
    {
        const string src = "x = 1, 2, 3\n";
        var cs = Record.Exception(() => Parse(src));
        var py = Record.Exception(() => PythonAstReader.ParseSource(src, "main.py"));
        cs.Should().BeNull(because: "the C# parser wraps a comma RHS the way return already does");
        py.Should().BeNull(because: "CPython already parses x = 1, 2, 3 as a Tuple");
    }
}

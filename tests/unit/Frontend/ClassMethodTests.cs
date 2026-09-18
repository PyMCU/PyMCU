using System.Linq;
using FluentAssertions;
using PyMCU.Frontend;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>@classmethod</c> parses as an inline class method. Adafruit sht4x/tmp117
/// write <c>Mode.add_values((...))</c> to populate class attributes at compile
/// time; there is no runtime class object, so the body expands at each call.
/// </summary>
public class ClassMethodTests
{
    private static ProgramNode Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    [Fact]
    public void Classmethod_ParsesAsInlineClassMethod()
    {
        var ast = Parse(
            "class C:\n" +
            "    @classmethod\n" +
            "    def f(cls):\n" +
            "        pass\n");
        var cls = ast.GlobalStatements.OfType<ClassDef>().Single();
        var fn = ((Block)cls.Body).Statements.OfType<FunctionDef>().Single();
        fn.IsClassMethod.Should().BeTrue(
            because: "@classmethod is compile-time class-namespace population, not a refusal");
        fn.IsInline.Should().BeTrue(
            because: "there is no runtime class object, so the body expands at each call");
        fn.Params[0].Name.Should().Be("cls",
            because: "the first parameter is the receiver class, spelled cls");
    }
}

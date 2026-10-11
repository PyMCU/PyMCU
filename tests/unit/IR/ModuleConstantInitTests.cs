using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `K: int = 7` at module level is a compile-time constant: every read of K folds to 7 and
/// the name has no storage. Lowered inside the synthesized `main` (an entry file with no
/// explicit `def main()`, which is every `pymcu natmod` module), the declaration still emitted
/// a Copy, and since K resolves to its own value the destination of that Copy was the
/// Constant 7. A write TO a constant is not an instruction any backend can encode: the ARM
/// backend stopped with "operand 'Constant' cannot be written yet", an internal message about
/// a construct that is plain Python and the commonest thing a library has.
///
/// The declaration is a no-op, so it must emit nothing.
/// </summary>
public class ModuleConstantInitTests
{
    private static ProgramIR Gen(string src, bool library) =>
        new IRGenerator { LibraryMode = library }.Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "arm" });

    private const string Source =
        "K: int = 7\n" +
        "\n" +
        "def F(a: int) -> int:\n" +
        "    return a + K\n";

    private static bool WritesAConstant(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>().Any(c => c.Dst is Constant);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AModuleConstantDeclarationWritesNothing(bool library)
    {
        var ir = Gen(Source, library);

        WritesAConstant(ir).Should().BeFalse(
            because: "K has no storage, so there is nothing to copy 7 into");
    }

    [Fact]
    public void TheReadsStillFoldToTheConstant()
    {
        var f = Gen(Source, library: true).Functions.Single(x => x.Name == "F");

        f.Body.OfType<Binary>().Any(b => b.Src2 is Constant { Value: 7 }).Should().BeTrue();
    }
}

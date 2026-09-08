using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#273. Assigning to an enum member had three outcomes, chosen by how the enum reached
/// the file that writes it:
///
///     class Color(IntEnum) here      "name 'Color' is not defined", eight lines below the
///                                    line that declares it
///     from cfg import Color          built, the write dropped, the member read its old value
///     import cfg; cfg.Color.RED = 9  the write LANDED and the member read 9
///
/// Refusing is right -- CPython raises `AttributeError: cannot reassign member 'RED'` for the
/// same program, measured against 3.14 rather than assumed -- so the first was the correct
/// decision reached by the wrong route and reported with a false sentence, the second was a
/// silent wrong value, and the third was the one answer Python never gives.
///
/// An enum class is deliberately never registered as a class, so nothing was keyed by the name
/// and the write fell through to the ordinary member store, which evaluated `Color` as a value
/// and failed to find one. That is why the message talked about name resolution.
///
/// Every assertion below is on the MESSAGE, since a test that only checked for an error would
/// have passed against the false sentence.
/// </summary>
public class EnumMemberAssignTests
{
    private const string Enum =
        "from pymcu.types import IntEnum\n" +
        "class Color(IntEnum):\n" +
        "    RED = 7\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static ProgramIR GenWithModule(string mainSrc, string moduleName, string moduleSrc) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>
                { [moduleName] = new Parser(new Lexer(moduleSrc).Tokenize()).ParseProgram() },
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { moduleName });

    private static CompilerError Fails(string src) =>
        Assert.Throws<CompilerError>(() => Gen(src));

    private static CompilerError FailsWithModule(string mainSrc, string moduleName, string moduleSrc) =>
        Assert.Throws<CompilerError>(() => GenWithModule(mainSrc, moduleName, moduleSrc));

    [Fact]
    public void AssigningAnEnumMemberSaysSo()
    {
        var ex = Fails(Enum +
            "def main() -> None:\n" +
            "    Color.RED = 9\n");

        Assert.Contains("cannot reassign enum member 'Color.RED'", ex.Message);
        Assert.DoesNotContain("is not defined", ex.Message);
    }

    [Fact]
    public void AugmentingAnEnumMemberSaysTheSameThing()
    {
        var ex = Fails(Enum +
            "def main() -> None:\n" +
            "    Color.RED += 1\n");

        Assert.Contains("cannot reassign enum member 'Color.RED'", ex.Message);
    }

    // Imported by name, this was not refused at all: it compiled, the write was dropped and
    // the member read its old value.
    [Fact]
    public void AssigningAMemberOfAnIMPORTEDEnum_IsRefusedRatherThanDropped()
    {
        var ex = FailsWithModule(
            "from cfg import Color\n" +
            "def main() -> None:\n" +
            "    Color.RED = 9\n",
            "cfg", Enum);

        Assert.Contains("cannot reassign enum member 'Color.RED'", ex.Message);
    }

    // Reached through the module, the write LANDED. Same statement, opposite outcome, and the
    // only one of the three that Python never produces.
    [Fact]
    public void AssigningAMemberThroughTheModuleName_IsRefusedRatherThanApplied()
    {
        var ex = FailsWithModule(
            "import cfg\n" +
            "def main() -> None:\n" +
            "    cfg.Color.RED = 9\n",
            "cfg", Enum);

        Assert.Contains("cannot reassign enum member 'Color.RED'", ex.Message);
    }

    // The caret goes on the first token of the target, as the const-reassignment guard beside
    // it does. `    Color.RED = 9` puts `Color` at column 5.
    [Fact]
    public void TheCaretPointsAtTheStartOfTheTarget()
    {
        var ex = Fails(Enum +
            "def main() -> None:\n" +
            "    Color.RED = 9\n");

        Assert.Equal(5, ex.Line);
        Assert.Equal(5, ex.Column);
    }

    // The control. A name the program really never defines must still get the real diagnostic,
    // word for word, so the enum message cannot be delivered by weakening this one.
    [Fact]
    public void AGenuinelyUndefinedName_StillGetsTheNameResolutionDiagnostic()
    {
        var ex = Fails(
            "def main() -> uint8:\n" +
            "    return nosuchname\n");

        Assert.Contains(
            "name 'nosuchname' is not defined -- it is read here but never assigned, " +
            "imported, or received as a parameter",
            ex.Message);
        Assert.DoesNotContain("enum member", ex.Message);
    }

    // The other control: an enum that is only READ is untouched. Its members still fold, so
    // the class costs no storage and the refusal cannot be bought by giving enums slots.
    [Fact]
    public void AnEnumThatIsOnlyRead_StillFoldsAndCompiles()
    {
        var ir = Gen(Enum +
            "def main() -> uint8:\n" +
            "    return Color.RED\n");

        Assert.DoesNotContain(ir.Globals, g => g.Name == "Color_RED");
    }

    // CPython protects MEMBERS only: `Color.NOPE = 9` on the same enum is allowed there, so a
    // name that is not a member must not collect this refusal. It keeps whatever the compiler
    // already did with it, which is a separate question from this one.
    [Fact]
    public void AnAttributeThatIsNotAMember_DoesNotGetTheEnumRefusal()
    {
        var ex = Fails(Enum +
            "def main() -> None:\n" +
            "    Color.NOPE = 9\n");

        Assert.DoesNotContain("enum member", ex.Message);
    }
}

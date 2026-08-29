using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#221. Rebinding the name of a module-level `def` was accepted with no diagnostic.
///
/// It is not only a missing refusal. A call through the name lowers to a direct CALL and never
/// reads the assignment, so the program compiled with the name meaning two things at once.
/// Measured on atmega328p before the fix, in one program:
///
///     global helper
///     helper = 5
///     GPIOR1.value = helper()   ->  CALL helper  ->  1
///     GPIOR2.value = helper     ->  LDI R24, 5   ->  5
///
/// CPython raises TypeError on that call, so neither reading agrees with Python. The
/// function-valued form was worse: `helper = other` through `global` emitted CALL other,
/// silently redirecting the call.
///
/// The boundary is the MODULE-LEVEL binding. A local of the same name with no `global` is
/// ordinary Python shadowing and still compiles.
/// </summary>
public class FunctionNameRebindTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(),
                                          new DeviceConfig { Arch = "avr" });
    }

    private const string Defs =
        "def helper() -> uint8:\n" +
        "    return 1\n" +
        "def other() -> uint8:\n" +
        "    return 2\n";

    private static string Refusal(string src)
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    // ─── refused ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("helper = 5")]      // to a value
    [InlineData("helper = other")]  // to another function: this one redirected the call
    public void RebindingThroughGlobal_IsRefused(string assignment)
    {
        var msg = Refusal(Defs + "def main() -> None:\n    global helper\n    " + assignment + "\n");

        Assert.Contains("'helper' is bound to a function at compile time", msg);
    }

    // No `global` needed at module level: the statement already targets that binding. It is
    // lowered into main's body, so the check recognises it by the binding it writes rather
    // than by the scope it appears in.
    [Fact]
    public void RebindingAtModuleLevel_IsRefused()
        => Assert.Contains("'helper' is bound to a function at compile time",
                           Refusal(Defs + "helper = 5\ndef main() -> None:\n    v: uint8 = 0\n"));

    // The message has to say why the assignment cannot mean anything, or the reader is left
    // thinking the compiler simply dislikes the spelling.
    [Fact]
    public void TheRefusal_SaysWhyTheAssignmentCannotWork()
    {
        var msg = Refusal(Defs + "def main() -> None:\n    global helper\n    helper = 5\n");

        Assert.Contains("direct call", msg);
        Assert.Contains("never reads the assignment", msg);
    }

    // ─── still legal, and this is the half that constrains the fix ────────

    // Ordinary Python shadowing. A check that could not tell this from the module binding
    // would break every function with a local named after some function elsewhere in the file.
    [Fact]
    public void ALocalOfTheSameName_StillCompiles()
        => Assert.NotNull(Gen(Defs + "def main() -> None:\n    helper = 5\n    v: uint8 = helper\n"));

    // The refusal keys on the name being a module global AND a function. An ordinary global
    // is a module global and must keep rebinding.
    [Fact]
    public void AnOrdinaryModuleGlobal_StillRebinds()
        => Assert.NotNull(Gen(
            "counter = 0\n" + Defs +
            "def main() -> None:\n    global counter\n    counter = 5\n"));

    // Binding an ALIAS to a function is the supported shape and must not be caught: the
    // target is not itself a function name.
    [Fact]
    public void BindingAnAliasToAFunction_StillCompiles()
        => Assert.NotNull(Gen(Defs + "def main() -> None:\n    f = helper\n    v: uint8 = f()\n"));

    // The sibling refusal this one complements, kept so the new check cannot displace it:
    // it must still fire, and still name the alias rather than the function.
    [Fact]
    public void RebindingAnAliasToADifferentFunction_KeepsItsOwnRefusal()
    {
        var msg = Refusal(Defs + "def main() -> None:\n    f = helper\n    f = other\n    v: uint8 = f()\n");

        Assert.Contains("'f' is bound to a function at compile time", msg);
    }
}

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A Python builtin is in scope in every module and needs no import, so
/// "(typo, or a missing import?)" answers a question the reader cannot act on: the spelling is
/// right and there is no import to add. These pin the messages that name the builtin instead,
/// and the ones that are now implemented rather than reported.
/// </summary>
public class BuiltinDiagnosticTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(),
                                          new DeviceConfig { Arch = "avr" });
    }

    private static string ErrorFor(string body)
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen("def main():\n" + body)).Message;

    [Fact]
    public void Round_NamesTheBuiltin_AndDoesNotBlameATypoOrAnImport()
    {
        var msg = ErrorFor("    x: float = 1.5\n    y = round(x)\n");

        Assert.Contains("round()", msg);
        Assert.Contains("builtin", msg);
        Assert.DoesNotContain("typo", msg);
        Assert.DoesNotContain("missing import", msg);
    }

    [Fact]
    public void Round_SaysWhatToWriteInstead_AndFlagsTheTieRule()
    {
        var msg = ErrorFor("    x: float = 1.5\n    y = round(x)\n");

        Assert.Contains("int(x + 0.5)", msg);
        // The suggested rewrite is NOT round(): CPython breaks a tie to the even neighbour.
        // Saying so is the difference between advice and a silent behaviour change.
        Assert.Contains("even", msg);
    }

    [Fact]
    public void Isinstance_SaysTypesAreFixedAtCompileTime()
    {
        var msg = ErrorFor("    a: uint8 = 1\n    if isinstance(a, int):\n        a = 2\n");

        Assert.Contains("isinstance()", msg);
        Assert.Contains("compile", msg);
        Assert.DoesNotContain("typo", msg);
        Assert.DoesNotContain("missing import", msg);
    }

    [Fact]
    public void Sorted_ReachesTheBuiltinDiagnostic_NotTheUndefinedNameOne()
    {
        // `sorted` used to fall through the undefined-NAME check first, which told the reader
        // it was "never assigned, imported, or received as a parameter" -- true of every
        // builtin, and useless. The builtins set is the whole namespace, not a kept subset.
        var msg = ErrorFor("    a: uint8[3] = [3, 1, 2]\n    b = sorted(a)\n");

        Assert.Contains("sorted()", msg);
        Assert.Contains("builtin", msg);
        Assert.DoesNotContain("never assigned", msg);
    }

    [Fact]
    public void ABuiltinWithNoSpecificAdvice_StillNamesItselfAndListsWhatExists()
    {
        var msg = ErrorFor("    a: uint8 = 8\n    b = oct(a)\n");

        Assert.Contains("oct()", msg);
        Assert.Contains("PyMCU does not provide", msg);
        Assert.Contains("hex", msg);   // the supported neighbours are named
        Assert.DoesNotContain("typo", msg);
    }

    [Fact]
    public void AnOrdinaryUndefinedName_IsStillReportedAsUndefined_NotAsABuiltin()
    {
        // Only names in the builtins namespace are exempted from the undefined-name path; a
        // user-defined name that really was never declared must keep its own diagnostic.
        //
        // That diagnostic is now the CALL one rather than the bare-name one. `my_helper(1)` is
        // a call, and it used to be reported as a name read because InstanceClassOfName probed
        // the name speculatively and let ResolveBinding's throw escape, so the call path never
        // reached its own message. This is the same complaint the sorted() test above makes:
        // the undefined-NAME wording is the wrong one when the thing is being called.
        var msg = ErrorFor("    a = my_helper(1)\n");

        Assert.Contains("my_helper", msg);
        Assert.Contains("undefined function", msg);
        Assert.DoesNotContain("builtin", msg);
    }

    [Fact]
    public void Bool_Compiles_AndLowersToANotEqualZeroTest()
    {
        var ir = Gen("def main():\n    a: uint8 = 7\n    b: uint8 = bool(a)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        Assert.Contains(main.Body, i => i is Binary { Op: PyMCU.IR.BinaryOp.NotEqual });
    }

    [Fact]
    public void BoolOfAStringLiteral_FoldsToItsEmptiness()
    {
        // A string lowers to a flash address, so comparing it against zero would answer a
        // different question. Non-empty is True, empty is False, both at compile time.
        var ir = Gen("def main():\n    a: uint8 = bool(\"hi\")\n    b: uint8 = bool(\"\")\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        Assert.Contains(main.Body, i => i is Copy { Src: Constant { Value: 1 } });
        Assert.Contains(main.Body, i => i is Copy { Src: Constant { Value: 0 } });
    }
}

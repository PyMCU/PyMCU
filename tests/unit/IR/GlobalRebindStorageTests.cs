using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// A module global written through `global`, issue #220.
//
//     N = 10
//     def bump():
//         global N
//         N = 20
//
// produced `globals: []` and, as the whole body of bump, a copy whose DESTINATION was a
// literal: `copy const 20 -> const 10`. The write went nowhere and every read folded 10.
//
// The condition is not the write being in another function, which is how it was filed and how
// it looks: it is the NAME. ALL CAPS means "constant" by convention here, and the convention is
// what gives the name no storage. The same program with a lowercase name has always worked, in
// every arrangement, which is what says this was the convention overriding a written statement
// rather than module globals being unsupported.
//
// `CollectModuleReassignedNames` already knew, and its own comment says so: a second assignment
// or a `global` declaration makes the initializer merely happen to be constant. It was
// consulted for an alias initializer and not for the ALL CAPS decision.
//
// WHAT DISCRIMINATES: the storage assertions. Against the unfixed compiler the name is absent
// from globals and the copy's destination is a Constant.
//
// WHAT IS INVARIANT: an ALL CAPS name that is never written, which must keep folding, since
// that is what the convention is for and what every HAL dispatch relies on.
//
// The values are checked on hardware: tests/integration/Tests/AVR/GlobalRebindUppercaseTests
// runs both spellings and compares the transcript with the one python3 prints.
public class GlobalRebindStorageTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Preamble = "from pymcu.chips.atmega328p import GPIOR1\n\n\n";

    private static string Written(string name) =>
        Preamble +
        $"{name} = 10\n\n\n" +
        "def bump():\n" +
        $"    global {name}\n" +
        $"    {name} = 20\n\n\n" +
        "def main():\n" +
        "    bump()\n" +
        $"    GPIOR1.value = {name}\n";

    private static bool HasStorage(ProgramIR ir, string name) =>
        ir.Globals.Any(g => g.Name == name);

    private static IEnumerable<Copy> Copies(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>();

    // --- the defect ---------------------------------------------------------------

    [Fact]
    public void AnUppercaseGlobalThatIsWrittenGetsStorage()
    {
        Assert.True(HasStorage(Gen(Written("N")), "N"),
            "a name the module writes needs somewhere to write it");
    }

    [Fact]
    public void NoCopyLandsInALiteral()
    {
        // The signature of the defect, and the reason it needs no argument about what the
        // program should print: a copy whose destination is a Constant cannot store anything.
        Assert.DoesNotContain(Copies(Gen(Written("N"))), c => c.Dst is Constant);
    }

    [Fact]
    public void TheReadAfterTheWriteIsNotFolded()
    {
        // main must READ the name rather than carry the initializer, or the write is invisible
        // however well it was stored.
        var main = Gen(Written("N")).Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body.OfType<Copy>(), c => c.Src is Variable v && v.Name == "N");
    }

    // --- the same program, lowercase, which always worked -------------------------

    [Fact]
    public void TheLowercaseSpellingIsUnchanged()
    {
        var ir = Gen(Written("n"));
        Assert.True(HasStorage(ir, "n"));
        Assert.DoesNotContain(Copies(ir), c => c.Dst is Constant);
    }

    // --- invariant: the convention still folds ------------------------------------

    [Fact]
    public void AnUppercaseNameThatIsNeverWrittenStillFolds()
    {
        // What ALL CAPS is for. `LIMIT + 1` folds to 11 and the name gets no storage, which is
        // what every HAL dispatch on a named constant depends on.
        var ir = Gen(Preamble + "LIMIT = 10\n\n\ndef main():\n    GPIOR1.value = LIMIT + 1\n");

        Assert.False(HasStorage(ir, "LIMIT"));
        Assert.Contains(Copies(ir), c => c.Src is Constant k && k.Value == 11);
    }

    [Fact]
    public void AnUppercaseNameWrittenOnlyAtModuleLevelStillFolds()
    {
        // Two top-level assignments and no `global` anywhere: the last value is knowable at
        // compile time, and this is the shape the reassigned set already handled.
        var ir = Gen(Preamble + "LIMIT = 10\n\n\ndef main():\n    GPIOR1.value = LIMIT\n");

        Assert.False(HasStorage(ir, "LIMIT"));
    }
}

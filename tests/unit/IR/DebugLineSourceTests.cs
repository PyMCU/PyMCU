using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Which file's TEXT a DebugLine carries (issue #179).
///
/// The lookup rebuilt a module name from the mangled prefix (`drivers.led` was queried as
/// `drivers_led`), so it missed for every dotted name, fell back to the entry file's lines
/// without a word, and produced a listing that was well formed, had real line numbers, and
/// showed another file's source next to them.
///
/// These assert on TEXT, not on presence. Every one of them passes on the broken compiler if
/// it only asks whether a listing was produced: the wrong listing is still a listing.
/// </summary>
public class DebugLineSourceTests
{
    private const string ModuleSrc =
        "from pymcu.types import uint8\n" +     // 1
        "\n" +                                  // 2
        "\n" +                                  // 3
        "def helper(v: uint8) -> uint8:\n" +    // 4
        "    w: uint8 = v + 7\n" +              // 5  <- only in the module
        "    return w\n";                       // 6  <- only in the module

    private static string EntryFor(string moduleName) =>
        "from pymcu.types import uint8\n" +           // 1
        $"from {moduleName} import helper\n" +        // 2
        "\n" +                                        // 3
        "\n" +                                        // 4
        "def main() -> None:\n" +                     // 5  <- only in the entry file
        "    aaa: uint8 = 3\n" +                      // 6  <- only in the entry file
        "    bbb: uint8 = helper(aaa)\n";             // 7

    private static ProgramIR Gen(string moduleName, string modulePath)
    {
        var moduleAst = new Parser(new Lexer(ModuleSrc).Tokenize()).ParseProgram();
        var entrySrc = EntryFor(moduleName);
        var mainAst = new Parser(new Lexer(entrySrc).Tokenize()).ParseProgram();

        return new IRGenerator().Generate(
            mainAst,
            new Dictionary<string, ProgramNode> { [moduleName] = moduleAst },
            new DeviceConfig { Arch = "avr" },
            sourceLines: entrySrc.Split('\n').ToList(),
            moduleSourceLines: new Dictionary<string, List<string>>
            {
                [moduleName] = ModuleSrc.Split('\n').ToList(),
            },
            projectModules: [moduleName],
            modulePaths: new Dictionary<string, string> { [moduleName] = modulePath });
    }

    private static List<string> TextOf(ProgramIR ir, string functionName)
        => Assert.Single(ir.Functions, f => f.Name == functionName)
                 .Body.OfType<DebugLine>().Select(d => d.Text).ToList();

    [Fact]
    public void ADottedModulesListing_CarriesThatModulesOwnText()
    {
        var texts = TextOf(Gen("drivers.led", "/proj/src/drivers/led.py"), "drivers_led_helper");

        Assert.Contains(texts, t => t.Contains("w: uint8 = v + 7"));
        Assert.Contains(texts, t => t.Contains("return w"));
    }

    // The half that catches the bug: the old listing was full, well formed, and made of the
    // entry file's lines.
    [Fact]
    public void ADottedModulesListing_CarriesNoneOfTheEntryFilesText()
    {
        var texts = TextOf(Gen("drivers.led", "/proj/src/drivers/led.py"), "drivers_led_helper");

        Assert.DoesNotContain(texts, t => t.Contains("def main()"));
        Assert.DoesNotContain(texts, t => t.Contains("aaa: uint8 = 3"));
    }

    // A single-segment name was the one shape whose rebuilt key happened to match, so it has
    // to keep working: the fix must not trade one direction of the bug for the other.
    [Fact]
    public void AnUndottedModuleKeepsItsOwnText()
    {
        var texts = TextOf(Gen("led", "/proj/src/led.py"), "led_helper");

        Assert.Contains(texts, t => t.Contains("w: uint8 = v + 7"));
        Assert.DoesNotContain(texts, t => t.Contains("def main()"));
    }

    [Fact]
    public void TheEntryFileStillGetsItsOwnText()
    {
        var texts = TextOf(Gen("drivers.led", "/proj/src/drivers/led.py"), "main");

        Assert.Contains(texts, t => t.Contains("aaa: uint8 = 3"));
        Assert.DoesNotContain(texts, t => t.Contains("w: uint8 = v + 7"));
    }

    // A module with no recorded path cannot be resolved, and answering with the entry file's
    // text would be the very fiction this fixes. Emitting nothing is the honest outcome.
    [Fact]
    public void AModuleWithNoRecordedPath_BorrowsNobodysText()
    {
        var moduleAst = new Parser(new Lexer(ModuleSrc).Tokenize()).ParseProgram();
        var entrySrc = EntryFor("drivers.led");
        var mainAst = new Parser(new Lexer(entrySrc).Tokenize()).ParseProgram();

        var ir = new IRGenerator().Generate(
            mainAst,
            new Dictionary<string, ProgramNode> { ["drivers.led"] = moduleAst },
            new DeviceConfig { Arch = "avr" },
            sourceLines: entrySrc.Split('\n').ToList(),
            moduleSourceLines: new Dictionary<string, List<string>>
            {
                ["drivers.led"] = ModuleSrc.Split('\n').ToList(),
            },
            projectModules: ["drivers.led"],
            modulePaths: new Dictionary<string, string>());

        var texts = TextOf(ir, "drivers_led_helper");
        Assert.DoesNotContain(texts, t => t.Contains("def main()"));
        Assert.DoesNotContain(texts, t => t.Contains("aaa: uint8 = 3"));
    }
}

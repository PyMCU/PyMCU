using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#348. `CheckAnnotationNames` is the one sentence for a name no annotation position may
/// hold, and its sweep reached a local, a parameter, a return type and an instance field. A
/// MODULE-LEVEL GLOBAL was the position it never reached, so `X: Bogus = 1` at the top level
/// took the silent uint8 fallback -- the exact truncation #278 was filed to stop -- while the
/// same name one line lower, inside a function, was refused.
///
/// The sweep runs where the signature sweep does, after every module is scanned, so an
/// imported module's globals are covered by the same pass and the same sentence.
/// </summary>
public class GlobalAnnotationNameTests
{
    private static ProgramIR Gen(string src, Dictionary<string, ProgramNode>? mods = null) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            mods ?? new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src, Dictionary<string, ProgramNode>? mods = null) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src, mods)).Message;

    /// The same program with one of its modules written in a second FILE, which is the shape a
    /// library global has: the entry module never mentions the annotation at all.
    private static ProgramIR GenWithModule(string mainSrc, string modName, string modSrc)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            [modName] = new Parser(new Lexer(modSrc).Tokenize()).ParseProgram(),
        };
        var ctx = new PyMCU.Common.CompilationContext(new CompilerOptions(
            FilePath: "main.py", OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        ctx.ProjectModules.Add(modName);
        return new IRGenerator().Generate(
            new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram(), imported,
            new DeviceConfig { Arch = "avr" }, projectModules: ctx.ProjectModules);
    }

    [Fact]
    public void AModuleLevelAnnotatedGlobal_IsRefusedLikeTheSameNameInAFunction()
    {
        string msg = Refusal(
            "# 1\n" +
            "# 2\n" +
            "X: Bogus = 1\n\n" +
            "def main() -> None:\n" +
            "    y = X\n");
        Assert.Contains("unknown type 'Bogus' in the annotation", msg);
    }

    [Fact]
    public void TheRefusalPointsAtTheLineTheGlobalIsWrittenOn()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "# 1\n" +
            "# 2\n" +
            "X: Bogus = 1\n\n" +
            "def main() -> None:\n" +
            "    y = X\n"));
        Assert.Equal(3, ex.Line);
    }

    [Fact]
    public void ABracketedGlobalAnnotationIsCheckedByItsHead()
    {
        Assert.Contains("unknown type 'Bogus'", Refusal(
            "X: Bogus[uint8] = 1\n\n" +
            "def main() -> None:\n" +
            "    y = X\n"));
    }

    [Fact]
    public void AGlobalWhoseAnnotationNamesAClass_IsAccepted()
    {
        Assert.NotNull(Gen(
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self.v = 1\n\n" +
            "B: Box = Box()\n\n" +
            "def main() -> None:\n" +
            "    y = B.v\n"));
    }

    [Fact]
    public void AnImportedModulesGlobalIsSweptToo()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => GenWithModule(
            "from lib1 import read\n\n" +
            "def main() -> None:\n" +
            "    y = read()\n",
            "lib1",
            "# a\n" +
            "GAIN: Bogus = 3\n\n" +
            "def read() -> uint8:\n" +
            "    return GAIN\n"));
        Assert.Contains("unknown type 'Bogus' in the annotation", ex.Message);
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public void TheSpellingsThatAlreadyCompiled_AreUnchanged()
    {
        Assert.NotNull(Gen("X: uint8 = 1\n\ndef main() -> None:\n    y = X\n"));
        Assert.NotNull(Gen(
            "from pymcu.types import const\n\n" +
            "LIMIT: const = 10\n\n" +
            "def main() -> None:\n" +
            "    y = LIMIT\n"));
        Assert.NotNull(Gen("T: uint8[3] = [1, 2, 3]\n\ndef main() -> None:\n    y = T[0]\n"));
        Assert.NotNull(Gen("S: str = \"hi\"\n\ndef main() -> None:\n    y = S[0]\n"));
    }
}

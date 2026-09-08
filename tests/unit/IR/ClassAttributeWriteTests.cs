using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#272, the mirror of #270 in the other arm of the same branch. An ALL-CAPS class
/// attribute folds at its reads and is given no storage, so
///
///     class Dev:
///         LIMIT = 7
///     Dev.LIMIT = 9        # dropped, with no diagnostic
///     Dev.LIMIT            # 7
///
/// the write had nowhere to land. It emitted no instruction and the program read the old
/// value. Module level has had the answer since #220: its own isAllUpper is gated on
/// `reassigned`, because a name the program writes is not a constant whatever it is called.
///
/// The set of written attributes is gathered across every module BEFORE the first scan, since
/// the entry file is scanned last: a write in main.py to a class declared in cfg.py was not
/// yet visible when cfg's class body was scanned. The cross-module test below is the one that
/// fails if the collection is moved to the natural-looking place.
///
/// Every assertion is on the constant the generated code stores, never on the build result.
/// </summary>
public class ClassAttributeWriteTests
{
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

    private static List<int> ConstantsStoredInto(ProgramIR ir, string slot) =>
        ir.Functions
            .SelectMany(f => f.Body)
            .OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name == slot)
            .Select(c => c.Src)
            .OfType<Constant>()
            .Select(c => c.Value)
            .ToList();

    // Every slot the program stores into at all, so a test can say a name has NO storage
    // without depending on which instruction shape a write happens to take.
    private static IEnumerable<string> SlotsWritten(ProgramIR ir) =>
        ir.Functions
            .SelectMany(f => f.Body)
            .OfType<Copy>()
            .Select(c => c.Dst)
            .OfType<Variable>()
            .Select(v => v.Name);

    [Fact]
    public void AWrittenAllCapsClassAttribute_KeepsTheValueItIsGiven()
    {
        var ir = Gen(
            "class Dev:\n" +
            "    LIMIT = 7\n" +
            "def main() -> uint8:\n" +
            "    Dev.LIMIT = 9\n" +
            "    return Dev.LIMIT\n");

        var stored = ConstantsStoredInto(ir, "Dev_LIMIT");
        Assert.Contains(7, stored);   // the declared value still reaches the slot (#270)
        Assert.Contains(9, stored);   // and the write is no longer dropped (#272)
    }

    // The control, currently correct and kept so the written case cannot be made to work by
    // giving every class attribute storage: an attribute nobody writes still folds, costs no
    // global and emits no store.
    [Fact]
    public void AnUnwrittenAllCapsClassAttribute_StaysAFoldedConstant()
    {
        var ir = Gen(
            "class Dev:\n" +
            "    LIMIT = 7\n" +
            "def main() -> uint8:\n" +
            "    return Dev.LIMIT\n");

        Assert.DoesNotContain(ir.Globals, g => g.Name == "Dev_LIMIT");
        Assert.DoesNotContain("Dev_LIMIT", SlotsWritten(ir));
    }

    [Fact]
    public void AWriteFromAPlainFunction_CountsAsAWrite()
    {
        var ir = Gen(
            "class Dev:\n" +
            "    LIMIT = 7\n" +
            "def bump():\n" +
            "    Dev.LIMIT = 9\n" +
            "def main() -> uint8:\n" +
            "    bump()\n" +
            "    return Dev.LIMIT\n");

        Assert.Contains(9, ConstantsStoredInto(ir, "Dev_LIMIT"));
    }

    [Fact]
    public void AnAugmentedWrite_CountsAsAWrite()
    {
        var ir = Gen(
            "class Dev:\n" +
            "    LIMIT = 7\n" +
            "def main() -> uint8:\n" +
            "    Dev.LIMIT += 2\n" +
            "    return Dev.LIMIT\n");

        Assert.Contains(7, ConstantsStoredInto(ir, "Dev_LIMIT"));
        Assert.Contains("Dev_LIMIT", SlotsWritten(ir));
    }

    // The write and the class live in different files, which is the ordinary shape: the class
    // is configuration and the entry file adjusts it. The entry file is scanned LAST, so a
    // per-module collection has not seen this write when cfg's class body is scanned and the
    // attribute folds anyway. This test is the one that catches that.
    [Fact]
    public void AWriteInTheEntryFile_DefoldsAClassDeclaredInAnImportedModule()
    {
        var ir = GenWithModule(
            "from cfg import Dev\n" +
            "def main() -> uint8:\n" +
            "    Dev.LIMIT = 9\n" +
            "    return Dev.LIMIT\n",
            "cfg",
            "class Dev:\n" +
            "    LIMIT = 7\n");

        var stored = ConstantsStoredInto(ir, "cfg_Dev_LIMIT");
        Assert.Contains(7, stored);
        Assert.Contains(9, stored);
    }

    // An imported module's own attribute that NOBODY writes still folds. The pre-pass runs
    // over every module, so this is the control that says it does not de-fold everything it
    // walks past.
    [Fact]
    public void AnUnwrittenAttributeInAnImportedModule_StaysAFoldedConstant()
    {
        var ir = GenWithModule(
            "from cfg import Dev\n" +
            "def main() -> uint8:\n" +
            "    return Dev.LIMIT\n",
            "cfg",
            "class Dev:\n" +
            "    LIMIT = 7\n");

        Assert.DoesNotContain(ir.Globals, g => g.Name == "cfg_Dev_LIMIT");
        Assert.DoesNotContain("cfg_Dev_LIMIT", SlotsWritten(ir));
    }

    // Two attributes of one class, one written and one not. The gate is per attribute, not
    // per class: de-folding the whole class would cost storage nobody asked for.
    [Fact]
    public void OnlyTheWrittenAttributeOfAClass_LosesItsFold()
    {
        var ir = Gen(
            "class Dev:\n" +
            "    LIMIT = 7\n" +
            "    OTHER = 3\n" +
            "def main() -> uint8:\n" +
            "    Dev.LIMIT = 9\n" +
            "    return Dev.LIMIT + Dev.OTHER\n");

        Assert.Contains(9, ConstantsStoredInto(ir, "Dev_LIMIT"));
        Assert.DoesNotContain(ir.Globals, g => g.Name == "Dev_OTHER");
        Assert.DoesNotContain("Dev_OTHER", SlotsWritten(ir));
    }
}

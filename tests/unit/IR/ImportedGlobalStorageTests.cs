using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `from m import g` on a module-level variable of m (issue #162). The name has ONE home, the
/// one m gave it: importing it used to register a second mutable global under the bare name,
/// so m's initializer wrote the declared value into m's slot while m's own functions and the
/// importing file wrote and read the other one. The declared value shipped in the firmware
/// with nothing able to reach it.
/// </summary>
public class ImportedGlobalStorageTests
{
    private const string Prelude =
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n";

    private static ProgramIR Generate(string mainSrc, string moduleName, string moduleSrc)
    {
        var mod = new Parser(new Lexer(moduleSrc).Tokenize()).ParseProgram();
        var main = new Parser(new Lexer(Prelude + mainSrc).Tokenize()).ParseProgram();
        return new IRGenerator().Generate(main,
            new Dictionary<string, ProgramNode> { [moduleName] = mod },
            new DeviceConfig { Arch = "avr" });
    }

    private static IEnumerable<string> SlotsWrittenBy(ProgramIR ir, string function) =>
        ir.Functions.Where(f => f.Name == function)
            .SelectMany(f => f.Body)
            .OfType<Copy>()
            .Select(c => c.Dst)
            .OfType<Variable>()
            .Select(v => v.Name);

    // Every global slot the function's body mentions, whatever the instruction reads it into.
    private static IEnumerable<string> SlotsTouchedBy(ProgramIR ir, string function) =>
        ir.Functions.Where(f => f.Name == function)
            .SelectMany(f => f.Body)
            .SelectMany(i => i switch
            {
                Copy c => new[] { c.Src, c.Dst },
                Call c => c.Args.ToArray(),
                Binary b => new[] { b.Src1, b.Src2 },
                JumpIfNotEqual j => new[] { j.Src1, j.Src2 },
                _ => Array.Empty<Val>(),
            })
            .OfType<Variable>()
            .Select(v => v.Name);

    private const string CountModule =
        "counter: uint8 = 7\n" +
        "def bump():\n" +
        "    global counter\n" +
        "    counter = 42\n";

    [Fact]
    public void ImportedGlobal_HasOneSlot_TheDefiningModulesOwn()
    {
        var ir = Generate(
            "from countmod import counter, bump\n" +
            "def main(seed: uint8):\n" +
            "    if seed > 10:\n" +
            "        bump()\n" +
            "    print(counter)\n",
            "countmod", CountModule);

        Assert.DoesNotContain(ir.Globals, g => g.Name == "counter");
        Assert.Contains(ir.Globals, g => g.Name == "countmod_counter");
    }

    [Fact]
    public void ImportedGlobal_IsReadFromTheSlotTheModuleWrites()
    {
        var ir = Generate(
            "from countmod import counter, bump\n" +
            "def main(seed: uint8):\n" +
            "    if seed > 10:\n" +
            "        bump()\n" +
            "    print(counter)\n",
            "countmod", CountModule);

        Assert.Contains("countmod_counter", SlotsWrittenBy(ir, "countmod_bump"));
        Assert.Contains("countmod_counter", SlotsTouchedBy(ir, "main"));
    }

    [Fact]
    public void TheImportSpellingDoesNotDecideWhereTheModuleWrites()
    {
        // The importing file used to change the callee module's own code: with `import m`,
        // m.bump() wrote m_counter; adding `from m import counter` made the same bump() write
        // the bare name instead.
        var viaModule = Generate(
            "import countmod\n" +
            "def main(seed: uint8):\n" +
            "    countmod.bump()\n" +
            "    print(countmod.counter)\n",
            "countmod", CountModule);
        var viaFrom = Generate(
            "from countmod import counter, bump\n" +
            "def main(seed: uint8):\n" +
            "    bump()\n" +
            "    print(counter)\n",
            "countmod", CountModule);

        Assert.Equal(SlotsWrittenBy(viaModule, "countmod_bump"), SlotsWrittenBy(viaFrom, "countmod_bump"));
    }

    [Fact]
    public void ImportedStrGlobal_KeepsItsText()
    {
        // The text belongs to the defining module's key. Read under the bare name it was not a
        // string at all, and print wrote an empty line.
        var ir = Generate(
            "from banners import BANNER\n" +
            "def main():\n" +
            "    print(BANNER)\n",
            "banners", "BANNER: str = \"ready\"\n");

        var texts = ir.Functions.SelectMany(f => f.Body).OfType<FlashData>()
            .Select(d => new string(d.Bytes.TakeWhile(b => b != 0).Select(b => (char)b).ToArray()))
            .ToList();
        Assert.Contains("ready", texts);
    }

    [Fact]
    public void ImportedStrGlobal_ReboundByItsModule_DispatchesOverBothTexts()
    {
        // The #145 shape reached through an import: the id lives in the module's global, and
        // both texts are candidates at the read.
        var ir = Generate(
            "from statemod import state, bump\n" +
            "def main(seed: uint8):\n" +
            "    if seed > 10:\n" +
            "        bump()\n" +
            "    print(state)\n",
            "statemod",
            "state: str = \"idle\"\n" +
            "def bump():\n" +
            "    global state\n" +
            "    state = \"running\"\n");

        var texts = ir.Functions.SelectMany(f => f.Body).OfType<FlashData>()
            .Select(d => new string(d.Bytes.TakeWhile(b => b != 0).Select(b => (char)b).ToArray()))
            .ToList();
        Assert.Contains("idle", texts);
        Assert.Contains("running", texts);
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<JumpIfNotEqual>(),
            j => j.Src1 is Variable { Name: "statemod_state" });
    }
}

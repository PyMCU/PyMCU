using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// A module global is its module's, and a function local is its function's.
//
// The entry file's globals are filed under the bare name, and the name resolver asked the bare
// tables first, so code in an imported module read the ENTRY file's global of the same name:
// cfg.py's `def get(): return LIM` returned main.py's `LIM`, and a `global LIM` write in cfg
// landed there too. The two modules shared one slot, and CPython's 311 printed as 602.
//
// A local went the other way: a bare name the frame had bound was answered with any imported
// module's global of that spelling, so `lim = x + 300; return lim` in a function of main.py
// returned cfg.py's `lim` (a module main never imported the name from) and the store was
// dropped as dead. 311 printed as 20.
//
// WHAT DISCRIMINATES: every assertion below names the storage a read or write reaches; against
// the unfixed compiler each names the other module's slot.
public class ModuleGlobalScopeTests
{
    private static ProgramIR Gen(string mainSrc, params (string Name, string Source)[] modules)
    {
        var imported = new Dictionary<string, ProgramNode>();
        foreach (var (name, source) in modules)
            imported[name] = new Parser(new Lexer(source).Tokenize()).ParseProgram();
        var project = new HashSet<string>(modules.Select(m => m.Name));
        return new IRGenerator().Generate(
            new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram(), imported,
            new DeviceConfig { Arch = "avr" }, projectModules: project);
    }

    private static IEnumerable<string> Names(Function f) =>
        f.Body.SelectMany(i => i switch
        {
            Return { Value: Variable v } => [v.Name],
            Binary b => new[] { b.Src1, b.Src2, b.Dst }.OfType<Variable>().Select(v => v.Name),
            Copy c => new[] { c.Src, c.Dst }.OfType<Variable>().Select(v => v.Name),
            _ => []
        });

    private const string Cfg =
        "from pymcu.types import uint32\n" +
        "LIM: uint32 = 10\n" +
        "def bump(d: uint32):\n" +
        "    global LIM\n" +
        "    LIM = LIM + d\n" +
        "def get() -> uint32:\n" +
        "    return LIM\n";

    [Fact]
    public void AnImportedModuleReadsAndWritesItsOwnGlobal()
    {
        var ir = Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "from pymcu.types import uint32\n" +
            "import cfg\n" +
            "LIM: uint32 = GPIOR0.value + 1\n" +
            "cfg.bump(GPIOR0.value)\n" +
            "LIM = LIM + 300\n" +
            "GPIOR0.value = LIM + cfg.get()\n",
            ("cfg", Cfg));

        Assert.Contains("cfg_LIM", Names(ir.Functions.Single(f => f.Name == "cfg_get")));
        Assert.DoesNotContain("LIM", Names(ir.Functions.Single(f => f.Name == "cfg_get")));
        Assert.All(Names(ir.Functions.Single(f => f.Name == "cfg_bump")).Where(n => n.EndsWith("LIM")),
            n => Assert.Equal("cfg_LIM", n));
    }

    [Fact]
    public void AFunctionLocalIsNotAnotherModulesGlobal()
    {
        var ir = Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "from pymcu.types import uint32\n" +
            "from cfg import get\n" +
            "def f(x: uint32) -> uint32:\n" +
            "    lim = x + 300\n" +
            "    return lim\n" +
            "GPIOR0.value = f(GPIOR0.value) + get()\n",
            ("cfg", "from pymcu.types import uint32\n" +
                    "lim: uint32 = 10\n" +
                    "def get() -> uint32:\n" +
                    "    return lim\n"));

        var f = ir.Functions.Single(fn => fn.Name == "f");
        Assert.DoesNotContain("cfg_lim", Names(f));
        Assert.Contains(f.Body, i => i is Return { Value: Variable { Name: "f.lim" } });
    }
}

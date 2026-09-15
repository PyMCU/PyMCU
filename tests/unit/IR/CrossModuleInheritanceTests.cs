using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#420. A class inherited across a module boundary -- reduced from adafruit_mcp3xxx,
/// where `MCP3008(MCP3xxx)` declares no `__init__` of its own and inherits one from `MCP3xxx`,
/// defined in a different file -- was treated as though it had no constructor of any kind.
/// #391's synthesized no-op then gave it a real but wrong signature (zero parameters), so
/// `MCP3008(spi, cs)` was refused for passing "too many arguments" to a constructor the
/// library never wrote as taking none.
///
/// Two independent causes, both required to reproduce and both needed to fix it:
///
/// 1. Every place the scanner resolves an inherited base (the method-inheritance copy loop
///    and the two field-layout inheritance passes) tried the base name under the SUBCLASS's
///    own module prefix and under the bare name -- neither is where a base reached through
///    `from other_module import Base` is registered.
/// 2. Module scanning order follows import-DISCOVERY (BFS) order, not dependency order: an
///    entry file importing `sub_mod`, whose own body imports `base_mod`, scans `sub_mod`
///    before `base_mod` -- so even with the prefix fixed, nothing is registered yet for the
///    subclass to find.
/// </summary>
public class CrossModuleInheritanceTests
{
    private static ProgramIR GenWithModules(string mainSrc, params (string Name, string Source)[] modules)
    {
        var imported = new Dictionary<string, ProgramNode>();
        foreach (var (name, source) in modules)
            imported[name] = new Parser(new Lexer(source).Tokenize()).ParseProgram();

        var mainAst = new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram();
        var ctx = new PyMCU.Common.CompilationContext(new CompilerOptions(
            FilePath: "main.py", OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        foreach (var (name, _) in modules) ctx.ProjectModules.Add(name);

        return new IRGenerator().Generate(mainAst, imported, new DeviceConfig { Arch = "avr" },
                                          projectModules: ctx.ProjectModules);
    }

    private const string BaseMod =
        "from pymcu.types import uint8\n\n" +
        "class Base:\n" +
        "    def __init__(self, a: uint8, b: uint8):\n" +
        "        self._a: uint8 = a\n" +
        "        self._b: uint8 = b\n\n" +
        "    def sum(self) -> uint8:\n" +
        "        return self._a + self._b\n";

    // `sub_mod` imports `base_mod` -- the entry file below imports `sub_mod` FIRST, so
    // `base_mod` is discovered only once sub_mod's own imports are walked, landing it SECOND
    // in insertion order despite being the dependency. This is the shape that exercises cause
    // 2 (scan order): a topologically-naive scan reaches `Sub` before `Base`.
    private const string SubMod =
        "from base_mod import Base\n\n" +
        "class Sub(Base):\n" +
        "    pass\n";

    [Fact]
    public void ASubclassWithNoInitInheritsTheCrossModuleBaseConstructor()
    {
        // The whole defect in one assertion: it used to raise UserError("too many arguments
        // in call to constructor of 'Sub': it expects 0 argument(s), but 2 were provided").
        var ir = GenWithModules(
            "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
            "from pymcu.types import uint8\n" +
            "import sub_mod\n\n" +
            "seed: uint8 = GPIOR0.value\n" +
            "s = sub_mod.Sub(seed, 1)\n" +
            "GPIOR1.value = s.sum()\n",
            ("sub_mod", SubMod), ("base_mod", BaseMod));

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void BothConstructorArgumentsReachTheirFields()
    {
        // Not just "it builds" -- both fields the base constructor sets have to carry the
        // values THIS call passed, not the earlier synthesized-no-op reading (both zero).
        var ir = GenWithModules(
            "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
            "from pymcu.types import uint8\n" +
            "import sub_mod\n\n" +
            "seed: uint8 = GPIOR0.value\n" +
            "s = sub_mod.Sub(seed, 9)\n" +
            "GPIOR1.value = s.sum()\n",
            ("sub_mod", SubMod), ("base_mod", BaseMod));

        var stores = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR1", StringComparison.Ordinal))
            .ToList();
        // seed (a runtime value, not folded) + the literal 9 -- a Binary add reaching GPIOR1,
        // not a folded constant, since seed's own value is unknown until the device runs.
        Assert.Empty(stores.Where(s => s.Src is Constant));
        var adds = ir.Functions.SelectMany(f => f.Body).OfType<Binary>()
            .Where(b => b.Src2 is Constant { Value: 9 }).ToList();
        Assert.NotEmpty(adds);
    }
}

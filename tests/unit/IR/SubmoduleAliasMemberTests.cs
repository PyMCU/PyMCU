using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#422. `import pkg.submodule as alias` resolves the alias to the full dotted module
/// path, and reading a member through it (`alias.NAME`) mangled the member's symbol as
/// `"pkg.submodule" + "_" + "NAME"` -- keeping the literal dot -- instead of
/// `"pkg_submodule_NAME"`, the underscored form every OTHER module-mangling site in the
/// compiler already produces and the module's own scan prefix is registered under.
///
/// Reduced from adafruit_mcp3xxx: `import adafruit_mcp3xxx.mcp3008 as MCP` then `MCP.P0`, a
/// plain module-level int constant defined in the submodule.
/// </summary>
public class SubmoduleAliasMemberTests
{
    private static ProgramIR GenWithModule(string mainSrc, string moduleName, string moduleSrc)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            [moduleName] = new Parser(new Lexer(moduleSrc).Tokenize()).ParseProgram(),
        };
        var mainAst = new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram();
        var ctx = new PyMCU.Common.CompilationContext(new CompilerOptions(
            FilePath: "main.py", OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        ctx.ProjectModules.Add(moduleName);
        return new IRGenerator().Generate(mainAst, imported, new DeviceConfig { Arch = "avr" },
                                          projectModules: ctx.ProjectModules);
    }

    [Fact]
    public void AConstantReadThroughADottedSubmoduleAliasResolves()
    {
        // It used to raise UserError("Unknown module member: pkg.sub_P0") -- note the
        // literal dot still in the reported name.
        var ir = GenWithModule(
            "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
            "import pkg.sub as MCP\n\n" +
            "GPIOR1.value = MCP.P0\n",
            "pkg.sub", "P0 = 0\nP1 = 1\n");

        // Not just "it builds" -- the read has to carry P0's actual value (0), which a bug
        // that fell back to a fabricated symbol would not.
        var stores = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR1", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(stores, s => s.Src is Constant { Value: 0 });
    }
}

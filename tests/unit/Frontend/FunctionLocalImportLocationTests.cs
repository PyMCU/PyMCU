using PyMCU.Common;
using PyMCU.Common.Abstractions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.Infrastructure;
using PyMCU.Pipeline.Phases;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A missing module imported INSIDE a function of an imported module is reported at that
/// import. Conditional compilation hoists a function-local import into the module's import
/// list after the dependency graph is built, and the loop that loads those had no location
/// to give: the failure came out at line 1 of the entry file. adafruit_seesaw's
/// `from adafruit_seesaw.crickit import Crickit_Pinmap` inside Seesaw.__init__ was reported
/// as main.py:1:1.
/// </summary>
public class FunctionLocalImportLocationTests : IDisposable
{
    private readonly string _root;

    public FunctionLocalImportLocationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pymcu-local-import-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Phase(IModuleLoader loader)
        : FrontendResolutionPhase(loader, new DependencyGraphBuilder(loader))
    {
        public void RunDirect(CompilationContext ctx) => Run(ctx);
    }

    [Fact]
    public void AMissingModuleImportedInsideAFunction_IsReportedAtThatImport()
    {
        File.WriteAllText(Path.Combine(_root, "drv.py"),
            "class Dev:\n" +
            "    def __init__(self):\n" +
            "        self.n = 0\n" +
            "\n" +
            "    def load(self):\n" +
            "        from missing_pinmap import PINS\n" +
            "        self.n = PINS\n");
        string entry = Path.Combine(_root, "main.py");
        string entrySrc = "from drv import Dev\n\nd = Dev()\nd.load()\n";
        File.WriteAllText(entry, entrySrc);

        var ctx = new CompilationContext(new CompilerOptions(
            FilePath: entry, OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        ctx.IncludePaths.Clear();
        ctx.IncludePaths.Add(_root);
        ctx.RootAst = new Parser(new Lexer(entrySrc).Tokenize()).ParseProgram();

        var ex = Assert.Throws<CompilerError>(() => new Phase(new FileSystemModuleLoader()).RunDirect(ctx));

        Assert.Contains("missing_pinmap", ex.Message);
        Assert.Equal(Path.Combine(_root, "drv.py"), ex.File);
        Assert.Equal(6, ex.Line);
    }
}

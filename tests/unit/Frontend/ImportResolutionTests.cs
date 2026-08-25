using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.Infrastructure;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// How an import is RESOLVED, exercised through the loader and the dependency graph rather
/// than through the helpers that do the work, so each test states what a program gets.
///
/// Covers three failures that all looked like something else at the use site:
///   * a relative import was looked up with its leading dot stripped, so `.util` became a
///     missing top-level `util` (issue #148);
///   * `from m import *` bound the literal name "*" and therefore nothing, and the failure
///     surfaced as an undefined name (issue #149);
///   * a function defined twice in an IMPORTED module built clean and the first definition
///     won, the opposite of Python (issue #140).
/// </summary>
public class ImportResolutionTests : IDisposable
{
    private readonly string _root;

    public ImportResolutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pymcu-import-res-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path.Combine(_root, "drivers"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string relPath, string source)
        => File.WriteAllText(Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar)), source);

    private CompilationContext Context()
    {
        var ctx = new CompilationContext(new CompilerOptions(
            FilePath: Path.Combine(_root, "main.py"), OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        ctx.IncludePaths.Clear();
        ctx.IncludePaths.Add(_root);
        return ctx;
    }

    private static ProgramNode Parse(string source)
        => new Parser(new Lexer(source).Tokenize()).ParseProgram();

    // ---- #148: relative imports -------------------------------------------------------

    [Fact]
    public void ADottedRelativeImport_ResolvesToTheAbsoluteNameOfTheSamePackage()
    {
        Write("drivers/__init__.py", "");
        Write("drivers/util.py", "def half(v):\n    return v\n");
        Write("drivers/led.py", "from .util import half\n\ndef duty(v):\n    return half(v)\n");

        var ctx = Context();
        var led = new FileSystemModuleLoader().LoadModule("drivers.led", ctx.Options.FilePath, ctx);

        var imp = Assert.Single(led.Imports);
        Assert.Equal("drivers.util", imp.ModuleName);
        Assert.Equal(0, imp.RelativeLevel);
        Assert.Equal(["half"], imp.Symbols);
    }

    [Fact]
    public void ABareRelativeImport_BindsTheSubmoduleUnderItsOwnName()
    {
        Write("drivers/__init__.py", "");
        Write("drivers/util.py", "def half(v):\n    return v\n");
        Write("drivers/led.py", "from . import util\n\ndef duty(v):\n    return util.half(v)\n");

        var ctx = Context();
        var led = new FileSystemModuleLoader().LoadModule("drivers.led", ctx.Options.FilePath, ctx);

        var imp = Assert.Single(led.Imports);
        Assert.Equal("drivers.util", imp.ModuleName);
        Assert.Equal(0, imp.RelativeLevel);
        Assert.Empty(imp.Symbols);
        Assert.Equal("util", imp.ModuleAlias);
    }

    [Fact]
    public void ARelativeImportInASubpackage_IsLoadableAndItsModuleIsRegistered()
    {
        Write("drivers/__init__.py", "");
        Write("drivers/util.py", "def half(v):\n    return v\n");
        Write("drivers/led.py", "from .util import half\n\ndef duty(v):\n    return half(v)\n");
        Write("main.py", "from drivers.led import duty\n\ndef main():\n    duty(4)\n");

        var ctx = Context();
        ctx.RootAst = Parse(File.ReadAllText(Path.Combine(_root, "main.py")));

        var loader = new FileSystemModuleLoader();
        new DependencyGraphBuilder(loader).Build(ctx.RootAst, ctx.Options.FilePath, ctx);

        Assert.True(ctx.NamedModules.ContainsKey("drivers.util"),
            "the relative import should have loaded drivers.util, not a top-level util");
    }

    [Fact]
    public void ARelativeImportAboveTheSourcesRoot_IsReportedAtTheFileThatWroteIt()
    {
        Write("drivers/__init__.py", "");
        Write("drivers/led.py", "from ....util import half\n\ndef duty(v):\n    return half(v)\n");

        var ctx = Context();
        var ex = Assert.ThrowsAny<CompilerError>(
            () => new FileSystemModuleLoader().LoadModule("drivers.led", ctx.Options.FilePath, ctx));

        Assert.Contains("above the sources root", ex.Message);
        Assert.EndsWith(Path.Combine("drivers", "led.py"), ex.File);
        Assert.Equal(1, ex.Line);
    }

    // ---- #149: star imports -----------------------------------------------------------

    [Fact]
    public void AStarImport_BindsTheNamesTheModuleDefines()
    {
        Write("helpers.py", "SPEED = 3\n\nclass Motor:\n    def __init__(self):\n        self.n = 0\n\n"
                          + "def scale(v):\n    return v * 2\n\ndef _private(v):\n    return v\n");
        Write("main.py", "from helpers import *\n\ndef main():\n    scale(1)\n");

        var ctx = Context();
        ctx.RootAst = Parse(File.ReadAllText(Path.Combine(_root, "main.py")));
        new DependencyGraphBuilder(new FileSystemModuleLoader()).Build(ctx.RootAst, ctx.Options.FilePath, ctx);

        var imp = Assert.Single(ctx.RootAst.Imports);
        Assert.Contains("scale", imp.Symbols);
        Assert.Contains("Motor", imp.Symbols);
        Assert.Contains("SPEED", imp.Symbols);
        Assert.DoesNotContain("_private", imp.Symbols);
        Assert.DoesNotContain("*", imp.Symbols);
    }

    [Fact]
    public void AStarImport_HonoursAnExplicitAllList()
    {
        Write("helpers.py", "__all__ = [\"scale\"]\n\ndef scale(v):\n    return v * 2\n\n"
                          + "def other(v):\n    return v\n");
        Write("main.py", "from helpers import *\n\ndef main():\n    scale(1)\n");

        var ctx = Context();
        ctx.RootAst = Parse(File.ReadAllText(Path.Combine(_root, "main.py")));
        new DependencyGraphBuilder(new FileSystemModuleLoader()).Build(ctx.RootAst, ctx.Options.FilePath, ctx);

        var imp = Assert.Single(ctx.RootAst.Imports);
        Assert.Equal(["scale"], imp.Symbols);
    }

    // ---- #140: a function defined twice in an imported module -------------------------

    [Fact]
    public void AFunctionDefinedTwiceInAnImportedModule_IsRejected()
    {
        Write("helper.py", "@inline\ndef kind():\n    return 1\n\ndef kind():\n    return 2\n");

        var ctx = Context();
        var ex = Assert.ThrowsAny<CompilerError>(
            () => new FileSystemModuleLoader().LoadModule("helper", ctx.Options.FilePath, ctx));

        Assert.Contains("duplicate function definition", ex.Message);
        Assert.Contains("'kind'", ex.Message);
        Assert.EndsWith("helper.py", ex.File);
        Assert.Equal(5, ex.Line);
    }

    [Fact]
    public void InlineOverloadsWithDifferentParameterTypes_AreStillAccepted()
    {
        Write("helper.py", "@inline\ndef put(v: uint8):\n    return v\n\n"
                         + "@inline\ndef put(v: str):\n    return 0\n");

        var ctx = Context();
        var module = new FileSystemModuleLoader().LoadModule("helper", ctx.Options.FilePath, ctx);

        Assert.Equal(2, module.Functions.Count);
    }
}

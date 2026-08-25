using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Infrastructure;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `from pymcu import inline` used to report that the module `pymcu` was NOT FOUND and suggest
/// `pymcu install pymcu`. The package is right there -- the two imports above it in the same
/// file resolve from it -- and `inline` is exported from `pymcu.types`. These pin a message
/// that says where the name lives instead of denying the package exists.
/// </summary>
public class ModuleImportDiagnosticTests : IDisposable
{
    private readonly string _root;

    public ModuleImportDiagnosticTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pymcu-import-diag-" + Guid.NewGuid().ToString("N")[..12]);

        // A namespace package: a directory with submodules and no __init__.py, which is what
        // the shipped `pymcu` stdlib is.
        Directory.CreateDirectory(Path.Combine(_root, "pkg", "chips"));
        File.WriteAllText(Path.Combine(_root, "pkg", "types.py"), "def inline(f):\n    return f\n");
        File.WriteAllText(Path.Combine(_root, "pkg", "time.py"), "def delay_ms(n):\n    pass\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Resolve(string moduleName, params string[] symbols)
    {
        var main = Path.Combine(_root, "main.py");
        var ctx = new CompilationContext(new CompilerOptions(
            FilePath: main, OutputPath: "", Arch: "avr", Target: "atmega328p", Frequency: 16000000,
            Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0, Verbose: false));
        ctx.IncludePaths.Add(_root);

        var ex = Assert.ThrowsAny<Exception>(
            () => new FileSystemModuleLoader().ResolveModulePath(moduleName, ctx.Options.FilePath, ctx, symbols));
        return ex.Message;
    }

    [Fact]
    public void ImportingANameFromANamespacePackage_SaysWhichSubmoduleExportsIt()
    {
        var msg = Resolve("pkg", "inline");

        Assert.Contains("'inline'", msg);
        Assert.Contains("pkg.types", msg);
        Assert.Contains("from pkg.types import inline", msg);
    }

    [Fact]
    public void ItNoLongerClaimsThePackageIsMissing_NorSuggestsInstallingIt()
    {
        var msg = Resolve("pkg", "inline");

        Assert.DoesNotContain("Module not found", msg);
        Assert.DoesNotContain("pymcu install", msg);
    }

    [Fact]
    public void AnUnknownName_SaysNoSubmoduleDefinesIt_AndListsTheSubmodules()
    {
        var msg = Resolve("pkg", "nonesuch");

        Assert.Contains("'nonesuch'", msg);
        Assert.Contains("no submodule", msg);
        Assert.Contains("pkg.types", msg);
        Assert.Contains("pkg.time", msg);
    }

    [Fact]
    public void ImportingThePackageItself_SaysToNameASubmodule()
    {
        var msg = Resolve("pkg");

        Assert.Contains("package", msg);
        Assert.Contains("submodule", msg);
        Assert.DoesNotContain("pymcu install", msg);
    }

    [Fact]
    public void AModuleThatReallyIsAbsent_StillGetsTheLibraryAdvice()
    {
        var msg = Resolve("some_library", "thing");

        Assert.Contains("Module not found: some_library", msg);
        Assert.Contains("pymcu install some_library", msg);
    }
}

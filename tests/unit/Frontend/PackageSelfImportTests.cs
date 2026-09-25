using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.Infrastructure;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A package that writes its OWN absolute name to re-export a submodule: `from pkg import sub`
/// inside pkg/__init__.py. It is an ordinary layout -- CPython runs it, because by then `pkg` is
/// already in sys.modules and the statement binds what is there -- and it stopped the build twice
/// over.
///
/// The self-edge in the dependency graph never let the package's in-degree fall to zero, so the
/// whole compilation ended in "Cyclic dependency detected" with no line to look at. And the
/// submodule rewrite read the package's own bindings to decide whether `sub` was a name the
/// package binds, where THIS statement was one of those bindings: the name justified itself, the
/// file beside it was never loaded, and `pkg.sub.f()` came out as an unknown member of pkg.
/// </summary>
public class PackageSelfImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "pymcu-selfimp-" + Guid.NewGuid().ToString("N")[..12]);

    public PackageSelfImportTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "pkg"));
        File.WriteAllText(Path.Combine(_root, "pkg", "sub.py"), "def f(v):\n    return v\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private CompilationContext Build(string initSrc, string mainSrc)
    {
        File.WriteAllText(Path.Combine(_root, "pkg", "__init__.py"), initSrc);

        var ctx = new CompilationContext(new CompilerOptions(
            FilePath: Path.Combine(_root, "main.py"), OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        ctx.IncludePaths.Clear();
        ctx.IncludePaths.Add(_root);
        ctx.RootAst = new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram();
        RelativeImportResolver.Rewrite(ctx.RootAst, ctx.Options.FilePath, ctx.IncludePaths);
        var graph = new DependencyGraphBuilder(new FileSystemModuleLoader())
            .Build(ctx.RootAst, ctx.Options.FilePath, ctx);
        graph.GetTopologicalSort();
        return ctx;
    }

    [Fact]
    public void AnInitThatImportsItsOwnPackage_DoesNotCycle_AndLoadsTheSubmodule()
    {
        var ctx = Build("from pkg import sub\n", "import pkg\n\ndef main():\n    pkg.sub.f(1)\n");

        Assert.True(ctx.NamedModules.ContainsKey("pkg.sub"),
            "the submodule the package re-exports should have been loaded");
    }

    [Fact]
    public void TheSelfImportIsRewrittenToTheSubmodule()
    {
        var ctx = Build("from pkg import sub\n", "import pkg\n\ndef main():\n    pkg.sub.f(1)\n");

        var init = ctx.NamedModules["pkg"];
        var rewritten = Assert.Single(init.Imports, i => i.ModuleName == "pkg.sub");
        Assert.Empty(rewritten.Symbols);
        Assert.Equal("sub", rewritten.ModuleAlias);
    }

    [Fact]
    public void ANameThePackageReallyDefines_IsNotTakenForASubmodule()
    {
        // The self-import of a name the package defines itself stays a symbol import: only
        // the binding THIS statement makes is discounted, not the package's own def.
        var ctx = Build("from pkg import sub\ndef sub():\n    return 1\n",
                        "import pkg\n\ndef main():\n    pkg.sub()\n");

        Assert.False(ctx.NamedModules.ContainsKey("pkg.sub"),
            "the package defines the name, so the file beside it must not stand in for it");
    }
}

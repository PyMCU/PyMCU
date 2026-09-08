using System.IO;
using PyMCU.Frontend;
using Xunit;

namespace PyMCU.Tests.Frontend;

/// <summary>
/// `from pkg import sub` where `sub` is a submodule file (PyMCU#264).
///
/// CPython imports `pkg`, fails the attribute lookup, then falls back to importing `pkg.sub`.
/// That fallback is why an EMPTY `__init__.py` is a working layout, and it is what the Adafruit
/// wheels ship: `adafruit_bus_device/__init__.py` and `adafruit_bme280/__init__.py` are both
/// 0 bytes. Without it the import failed with "the module was found and does not define that
/// name", which is true and useless -- the module really is empty and the name really is a file
/// beside it.
///
/// The rewrite produces `import pkg.sub as sub`, a spelling that already worked. These tests are
/// written against the RESOLVER rather than a compile, because the property is that the two
/// spellings reach the loader identically; asserting on emitted code would test the loader twice
/// and this rewrite not at all.
///
/// The refusals matter as much as the acceptance. The fallback is deliberately narrower than
/// CPython: it declines whenever the package's own module so much as mentions the name, so a
/// package that binds `sub` in its `__init__` keeps the binding it has today. A future change
/// that widens that is a decision; one that widens it by accident is a bug, and only the
/// negative cases here catch it.
/// </summary>
public class PackageSubmoduleImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "pymcu-264-" + Guid.NewGuid().ToString("N")[..8]);

    public PackageSubmoduleImportTests()
    {
        // pkg/ with an EMPTY __init__.py -- the layout the wheels ship.
        Directory.CreateDirectory(Path.Combine(_root, "pkg"));
        File.WriteAllText(Path.Combine(_root, "pkg", "__init__.py"), "");
        File.WriteAllText(Path.Combine(_root, "pkg", "sub.py"), "def f():\n    pass\n");

        // bound/ whose __init__ defines the same name as a sibling file.
        Directory.CreateDirectory(Path.Combine(_root, "bound"));
        File.WriteAllText(Path.Combine(_root, "bound", "__init__.py"), "def sub():\n    pass\n");
        File.WriteAllText(Path.Combine(_root, "bound", "sub.py"), "def g():\n    pass\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ProgramNode Resolve(string src)
    {
        var ast = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        RelativeImportResolver.Rewrite(ast, Path.Combine(_root, "main.py"), new[] { _root });
        return ast;
    }

    private static ImportStmt Single(ProgramNode ast) => Assert.Single(ast.Imports);

    [Fact]
    public void FromPackageImportSubmodule_BecomesADottedModuleImport()
    {
        var imp = Single(Resolve("from pkg import sub\n"));

        Assert.Equal("pkg.sub", imp.ModuleName);
        Assert.Equal("sub", imp.ModuleAlias);
        Assert.Empty(imp.Symbols);
    }

    [Fact]
    public void AnAliasOnTheImportIsCarriedToTheModuleAlias()
    {
        // `from adafruit_bme280 import basic as adafruit_bme280` is Adafruit's documented
        // spelling, so the alias is the case that actually ships rather than a variant.
        var imp = Single(Resolve("from pkg import sub as s\n"));

        Assert.Equal("pkg.sub", imp.ModuleName);
        Assert.Equal("s", imp.ModuleAlias);
    }

    [Fact]
    public void ANameThePackageModuleBinds_IsLeftAlone()
    {
        // bound/__init__.py defines `sub`, and bound/sub.py also exists. Today the binding
        // wins; this fallback must not quietly change which one a program gets.
        var imp = Single(Resolve("from bound import sub\n"));

        Assert.Equal("bound", imp.ModuleName);
        Assert.Equal(new[] { "sub" }, imp.Symbols);
    }

    [Fact]
    public void ANameThatIsNotASubmodule_IsLeftAlone()
    {
        var imp = Single(Resolve("from pkg import not_a_file\n"));

        Assert.Equal("pkg", imp.ModuleName);
        Assert.Equal(new[] { "not_a_file" }, imp.Symbols);
    }

    [Fact]
    public void AnOrdinaryModuleImportIsUntouched()
    {
        var imp = Single(Resolve("from pymcu.types import uint8\n"));

        Assert.Equal("pymcu.types", imp.ModuleName);
        Assert.Equal(new[] { "uint8" }, imp.Symbols);
    }

    [Fact]
    public void AMixedImportSplitsIntoTheModuleAndTheRest()
    {
        // `from pkg import sub, other` is one statement covering two different things. The
        // submodule becomes its own import and the plain symbol stays on the original, which
        // is what the relative branch has always done for `from . import a, b`.
        var ast = Resolve("from pkg import sub, other\n");

        Assert.Equal(2, ast.Imports.Count);
        var plain = ast.Imports.Single(i => i.ModuleName == "pkg");
        var asMod = ast.Imports.Single(i => i.ModuleName == "pkg.sub");
        Assert.Equal(new[] { "other" }, plain.Symbols);
        Assert.Equal("sub", asMod.ModuleAlias);
    }

    [Fact]
    public void TheRelativeSpellingStillResolvesTheSameWay()
    {
        // `from . import sub` inside the package produced `import pkg.sub as sub` before this
        // change. Both spellings must land on the identical node, because module keys and
        // symbol mangling are derived from the name string.
        var ast = new Parser(new Lexer("from . import sub\n").Tokenize()).ParseProgram();
        RelativeImportResolver.Rewrite(ast, Path.Combine(_root, "pkg", "user.py"), new[] { _root });
        var rel = Assert.Single(ast.Imports);

        var abs = Single(Resolve("from pkg import sub\n"));

        Assert.Equal(abs.ModuleName, rel.ModuleName);
        Assert.Equal(abs.ModuleAlias, rel.ModuleAlias);
    }
}

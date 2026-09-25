using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Infrastructure;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A bare `import math` / `import time` resolves to the pymcu stdlib file of the same name.
/// That alias is a FALLBACK over the whole stdlib directory, so it also answered `import types`
/// with pymcu/types.py -- a file that is not the Python `types` module, and that the type system
/// resolves rather than the loader. Sent through the file path it was parsed, and
/// `class ptr(Generic[T])` came out as a SyntaxError inside the stdlib, reported against a
/// program whose only line was the import (#482).
///
/// Adafruit libraries ask for `types` inside `try: ... except ImportError`, where a SyntaxError
/// is not what the handler catches, so the guard's remaining names never bound either.
/// </summary>
public class TopLevelStdlibAliasTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "pymcu-482-" + Guid.NewGuid().ToString("N")[..12]);

    public TopLevelStdlibAliasTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "pymcu", "chips"));
        File.WriteAllText(Path.Combine(_root, "pymcu", "types.py"), "class ptr(Generic[T]):\n    pass\n");
        File.WriteAllText(Path.Combine(_root, "pymcu", "time.py"), "def delay_ms(n):\n    pass\n");
        File.WriteAllText(Path.Combine(_root, "pymcu", "chips", "__init__.py"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private CompilationContext Context()
    {
        var ctx = new CompilationContext(new CompilerOptions(
            FilePath: Path.Combine(_root, "main.py"), OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        ctx.IncludePaths.Add(_root);
        return ctx;
    }

    [Fact]
    public void ImportTypes_DoesNotResolveThePymcuTypeSystemModule()
    {
        var ex = Assert.ThrowsAny<Exception>(
            () => new FileSystemModuleLoader().ResolveModulePath("types", Context().Options.FilePath, Context()));

        Assert.DoesNotContain("types.py", ex.Message);
        Assert.Contains("types", ex.Message);
    }

    [Fact]
    public void ImportTypes_SaysWhatTheNameIsInsteadOfOfferingToInstallIt()
    {
        var ex = Assert.ThrowsAny<Exception>(
            () => new FileSystemModuleLoader().ResolveModulePath("types", Context().Options.FilePath, Context()));

        Assert.DoesNotContain("pymcu install types", ex.Message);
        Assert.Contains("Python standard module", ex.Message);
        Assert.Contains("pymcu.types", ex.Message);
    }

    [Fact]
    public void ImportChips_DoesNotResolveThePymcuChipPackage()
    {
        var ex = Assert.ThrowsAny<Exception>(
            () => new FileSystemModuleLoader().ResolveModulePath("chips", Context().Options.FilePath, Context()));

        Assert.DoesNotContain(Path.Combine("pymcu", "chips"), ex.Message);
    }

    [Fact]
    public void ARealAlias_StillResolvesToTheStdlibFile()
    {
        // The fallback itself is not narrowed: `time` is the pymcu stdlib under the name every
        // Python program types, and it must keep resolving.
        var path = new FileSystemModuleLoader().ResolveModulePath("time", Context().Options.FilePath, Context());

        Assert.Equal(Path.Combine(_root, "pymcu", "time.py"), path);
    }
}

using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Infrastructure;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Which names may be told to run `pymcu install` (issue #189).
///
/// One sentence, "if it is a PyMCU library, install it into this project with `pymcu install
/// {name}`", is reached by every unresolved top-level name. It is sound advice for a name
/// that really could be a library, and a round trip to nowhere for `ustruct`, which is part
/// of MicroPython and will never be on an index.
///
/// The half that has to hold is the negative one: a name that really could be a library must
/// keep the advice, or this trades a useless suggestion for a missing one.
/// </summary>
public class StandardModuleAdviceTests : IDisposable
{
    private const string BadAdvice = "install it into this project";

    private readonly string _root;

    public StandardModuleAdviceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pymcu-std-advice-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Resolve(string moduleName)
    {
        var ctx = new CompilationContext(new CompilerOptions(
            FilePath: Path.Combine(_root, "main.py"), OutputPath: "", Arch: "avr",
            Target: "atmega328p", Frequency: 16000000, Configs: [], Includes: [],
            ResetVector: 0, InterruptVector: 0, Verbose: false));
        ctx.IncludePaths.Clear();
        ctx.IncludePaths.Add(_root);

        var ex = Assert.ThrowsAny<Exception>(
            () => new FileSystemModuleLoader().ResolveModulePath(moduleName, ctx.Options.FilePath, ctx));
        return ex.Message;
    }

    [Theory]
    [InlineData("ustruct")]   // the issue's own reproducer
    [InlineData("ujson")]
    [InlineData("uos")]
    [InlineData("uctypes")]
    [InlineData("struct")]
    [InlineData("json")]
    [InlineData("os")]
    [InlineData("sys")]
    [InlineData("typing")]
    [InlineData("threading")]
    [InlineData("datetime")]
    [InlineData("re")]
    public void AStandardModule_IsNotOfferedAnInstall(string moduleName)
    {
        var msg = Resolve(moduleName);

        Assert.DoesNotContain(BadAdvice, msg);
        Assert.Contains("standard module", msg);
        Assert.Contains("nothing to fetch", msg);
    }

    [Fact]
    public void AMicroPythonModule_IsNamedAsOneRatherThanAsAPythonModule()
    {
        Assert.Contains("MicroPython standard", Resolve("ustruct"));
        Assert.Contains("Python standard", Resolve("struct"));
        Assert.DoesNotContain("MicroPython", Resolve("struct"));
    }

    [Fact]
    public void TheReasonIsGiven_NotJustTheRefusal()
    {
        Assert.Contains("no heap", Resolve("ustruct"));
        Assert.Contains("annotations", Resolve("typing"));
    }

    // A u-spelling of something PyMCU really does have should say where it is, not that it
    // is unavailable.
    [Fact]
    public void AUSpellingOfAModuleThatExists_PointsAtTheNameThatWorks()
    {
        Assert.Contains("import random", Resolve("urandom"));
        Assert.Contains("import collections", Resolve("ucollections"));
    }

    // The negative half. Losing this advice would be a regression of its own.
    [Theory]
    [InlineData("totally_made_up")]
    [InlineData("neopixel")]
    [InlineData("ssd1306")]
    public void ANameThatCouldBeALibrary_KeepsTheInstallAdvice(string moduleName)
    {
        var msg = Resolve(moduleName);

        Assert.Contains(BadAdvice, msg);
        Assert.Contains($"pymcu install {moduleName}", msg);
    }

    // These resolve to the pymcu stdlib under exactly these names, so claiming they are
    // missing would be a new wrong answer rather than a better one.
    [Theory]
    [InlineData("math")]
    [InlineData("time")]
    [InlineData("random")]
    [InlineData("collections")]
    [InlineData("asyncio")]
    public void AModuleThatPyMCUDoesProvide_IsNotDescribedAsAbsent(string moduleName)
    {
        Assert.False(StandardModuleNames.TryDescribe(moduleName, out _, out _),
            $"'{moduleName}' resolves to the pymcu stdlib; it must not be listed as unavailable");
    }
}

using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.Infrastructure;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `from m import name` where `m` resolves and binds no `name` (issue #158).
///
/// It used to build clean, and the name became an unassigned local at every use site, so the
/// firmware read whatever the RAM held. The half of these tests that matters most is the
/// other half: the shapes that must keep working, because a check that refuses a program
/// which runs is worse than the silence it replaces.
/// </summary>
public class ImportedNameCheckTests : IDisposable
{
    private readonly string _root;

    public ImportedNameCheckTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pymcu-import-name-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string name, string source)
        => File.WriteAllText(Path.Combine(_root, name), source);

    /// Loads `main.py` and every module it imports, folds them, and runs the check, which is
    /// the order the real phase uses.
    private Exception? Run()
    {
        var ctx = new CompilationContext(new CompilerOptions(
            FilePath: Path.Combine(_root, "main.py"), OutputPath: "", Arch: "avr",
            Target: "atmega328p", Frequency: 16000000, Configs: [], Includes: [],
            ResetVector: 0, InterruptVector: 0, Verbose: false));
        ctx.IncludePaths.Clear();
        ctx.IncludePaths.Add(_root);
        ctx.DeviceConfig.Chip = "atmega328p";
        ctx.DeviceConfig.Arch = "avr";

        ctx.RootAst = new Parser(new Lexer(File.ReadAllText(ctx.Options.FilePath)).Tokenize()).ParseProgram();

        var loader = new FileSystemModuleLoader();
        new DependencyGraphBuilder(loader).Build(ctx.RootAst, ctx.Options.FilePath, ctx);

        var folder = new ConditionalCompilator(ctx.DeviceConfig);
        folder.Process(ctx.RootAst);
        foreach (var module in ctx.NamedModules.Values) folder.Process(module);

        return Record.Exception(() => ImportedNameCheck.Check(ctx));
    }

    // ---- the bug -----------------------------------------------------------------------

    [Fact]
    public void AnImportedNameTheModuleDoesNotDefine_IsRejectedAtTheImport()
    {
        Write("helpers.py", "def scale(v):\n    return v * 2\n");
        Write("main.py", "from helpers import scale, LIMIT\n\ndef main():\n    scale(LIMIT)\n");

        var ex = Assert.IsType<CompilerError>(Run());

        Assert.Contains("cannot import 'LIMIT' from 'helpers'", ex.Message);
        Assert.Contains("scale", ex.Message);
        Assert.EndsWith("main.py", ex.File);
        Assert.Equal(1, ex.Line);
    }

    [Fact]
    public void ItIsReportedInTheFileThatWroteIt_NotTheEntryFile()
    {
        Write("helpers.py", "def scale(v):\n    return v * 2\n");
        Write("tasks.py", "from helpers import nope\n\ndef go():\n    return nope(1)\n");
        Write("main.py", "from tasks import go\n\ndef main():\n    go()\n");

        var ex = Assert.IsType<CompilerError>(Run());

        Assert.Contains("cannot import 'nope'", ex.Message);
        Assert.EndsWith("tasks.py", ex.File);
    }

    // ---- what must keep working ---------------------------------------------------------

    [Fact]
    public void ANameTheModuleDefines_IsAccepted()
    {
        Write("helpers.py", "LIMIT = 7\n\ndef scale(v):\n    return v * 2\n");
        Write("main.py", "from helpers import scale, LIMIT\n\ndef main():\n    scale(LIMIT)\n");

        Assert.Null(Run());
    }

    [Fact]
    public void ANameTheModuleOnlyReExports_IsAccepted()
    {
        Write("base.py", "def triple(v):\n    return v * 3\n");
        Write("helpers.py", "from base import triple\n\ndef scale(v):\n    return v * 2\n");
        Write("main.py", "from helpers import triple\n\ndef main():\n    triple(1)\n");

        Assert.Null(Run());
    }

    // A facade binds its names in the winning branch of a compile-time if. Asking before
    // folding would find none of them, which is why the check runs where it does.
    [Fact]
    public void ANameBoundOnlyInTheWinningBranchOfACompileTimeIf_IsAccepted()
    {
        Write("avr_impl.py", "def select(v):\n    return v\n");
        Write("other_impl.py", "def select(v):\n    return v + 1\n");
        Write("facade.py",
            "from pymcu.chips import __CHIP__\n" +
            "\n" +
            "if __CHIP__.arch == \"avr\":\n" +
            "    from avr_impl import select\n" +
            "else:\n" +
            "    from other_impl import select\n");
        Write("main.py", "from facade import select\n\ndef main():\n    select(1)\n");

        Assert.Null(Run());
    }

    // Folding promotes an import written inside a method into ProgramNode.Imports, because
    // there is nowhere else to put it. It still binds a local name, and whether that name
    // exists only matters if the method is compiled.
    [Fact]
    public void AnImportInsideAFunctionBody_IsNotDemanded()
    {
        Write("impl.py", "def present(v):\n    return v\n");
        Write("facade.py",
            "class Pin:\n" +
            "    def irq(self):\n" +
            "        from impl import absent\n" +
            "        return absent(1)\n" +
            "\n" +
            "def make():\n" +
            "    return 0\n");
        Write("main.py", "from facade import make\n\ndef main():\n    make()\n");

        Assert.Null(Run());
    }

    // A module-level `raise CompileError(...)` is a HAL refusing this target. Its own
    // sentence is reported at the use site and says far more than a missing name would.
    [Fact]
    public void AModuleThatRefusesTheTarget_KeepsItsOwnMessage()
    {
        Write("blocked.py", "raise CompileError(\"not available on AVR\")\n");
        Write("main.py", "from blocked import anything\n\ndef main():\n    anything()\n");

        Assert.Null(Run());
    }

    // The runtime exception types are predefined by the IR generator and need no import, so
    // pymcu/exceptions.py deliberately does not declare them. Two shipped examples import
    // ValueError from it anyway: CPython would reject that, this compiler resolves it, and
    // demanding it turned 8 green tests red.
    [Fact]
    public void ABuiltinExceptionName_IsAcceptedEvenThoughTheModuleLacksIt()
    {
        Write("exceptions.py", "class CompileError(Exception):\n    pass\n");
        Write("main.py",
            "from exceptions import ValueError\n" +
            "\n" +
            "def main():\n" +
            "    raise ValueError\n");

        Assert.Null(Run());
    }
}

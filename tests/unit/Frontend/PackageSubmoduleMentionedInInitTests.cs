using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.Infrastructure;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#323. `from &lt;package&gt; import &lt;submodule&gt;` is the first line of every Adafruit guide,
/// and it was refused as soon as the package's own `__init__.py` so much as MENTIONED the
/// submodule's name. The early fallback (#264) decides with a substring test over the file's
/// text -- comments included -- so `adafruit_motor/__init__.py`, whose header comment shows
/// `from adafruit_motor import servo`, declined its own example.
///
/// The fallback now has a second chance once the package is parsed: a name the package does
/// not BIND, with a file of its own beside it, is that file.
/// </summary>
public class PackageSubmoduleMentionedInInitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "pymcu-323-" + Guid.NewGuid().ToString("N")[..12]);

    public PackageSubmoduleMentionedInInitTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "motor"));
        Directory.CreateDirectory(Path.Combine(_root, "bound"));
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

    private CompilationContext Build(string mainSrc)
    {
        var ctx = Context();
        ctx.RootAst = new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram();
        RelativeImportResolver.Rewrite(ctx.RootAst, ctx.Options.FilePath, ctx.IncludePaths);
        new DependencyGraphBuilder(new FileSystemModuleLoader()).Build(ctx.RootAst, ctx.Options.FilePath, ctx);
        return ctx;
    }

    [Fact]
    public void AnInitThatMentionsTheSubmoduleInAComment_StillImportsIt()
    {
        // The comment is the package's own usage example, exactly as adafruit_motor ships it.
        Write("motor/__init__.py", "# Usage:\n#   from motor import servo\n#   s = servo.Servo(pwm)\n");
        Write("motor/servo.py", "def angle(v):\n    return v\n");

        var ctx = Build("from motor import servo\n\ndef main():\n    servo.angle(1)\n");

        Assert.True(ctx.NamedModules.ContainsKey("motor.servo"),
            "the submodule should have been loaded as a module of its own");

        var imp = Assert.Single(ctx.RootAst!.Imports, i => i.ModuleName == "motor.servo");
        Assert.Empty(imp.Symbols);
        Assert.Equal("servo", imp.ModuleAlias);
    }

    [Fact]
    public void AnAliasOnTheSubmodule_IsKept()
    {
        Write("motor/__init__.py", "# servo\n");
        Write("motor/servo.py", "def angle(v):\n    return v\n");

        var ctx = Build("from motor import servo as sv\n\ndef main():\n    sv.angle(1)\n");

        var imp = Assert.Single(ctx.RootAst!.Imports, i => i.ModuleName == "motor.servo");
        Assert.Equal("sv", imp.ModuleAlias);
    }

    [Fact]
    public void APackageThatBindsTheNameItself_KeepsTheBindingItHas()
    {
        // A real binding, not a mention: the import must stay a symbol import of the package.
        Write("bound/__init__.py", "def servo():\n    return 1\n");
        Write("bound/servo.py", "def angle(v):\n    return v\n");

        var ctx = Build("from bound import servo\n\ndef main():\n    servo()\n");

        Assert.False(ctx.NamedModules.ContainsKey("bound.servo"),
            "the package binds the name, so the file beside it must not be loaded instead");
        var imp = Assert.Single(ctx.RootAst!.Imports);
        Assert.Equal("bound", imp.ModuleName);
        Assert.Equal(["servo"], imp.Symbols);
    }

    [Fact]
    public void ANameWithNoFileBehindIt_IsLeftForTheNameCheck()
    {
        Write("motor/__init__.py", "# stepper\n");
        Write("motor/servo.py", "def angle(v):\n    return v\n");

        var ctx = Build("from motor import stepper\n\ndef main():\n    stepper.step()\n");

        Assert.False(ctx.NamedModules.ContainsKey("motor.stepper"));
        var imp = Assert.Single(ctx.RootAst!.Imports);
        Assert.Equal(["stepper"], imp.Symbols);
    }
}

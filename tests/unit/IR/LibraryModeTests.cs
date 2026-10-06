using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using PyMCU.Pipeline.Phases;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// --library mode: the unit has no entry point, so every top-level function of the entry
/// file is a root (the same IsExportC marker @used sets), and a buffer parameter of such a
/// function carries its own length as a hidden trailing parameter that len() reads.
///
/// Both behaviours are gated on the flag itself and not on IsExportC: a @export_c function's
/// ABI was already written down by its C caller, so no argument may be added to it, while a
/// library's callers are generated from the same signatures in the same build.
/// </summary>
public class LibraryModeTests
{
    private static CompilationContext ParseAsLibrary(string source, bool library)
    {
        var ctx = new CompilationContext(new CompilerOptions(
            FilePath: "main.py", OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false, Library: library));
        ctx.SourceCode = source;
        new ParsingPhase().Execute(ctx);
        return ctx;
    }

    private static ProgramIR Gen(string src, bool library) =>
        new IRGenerator { LibraryMode = library }.Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void LibraryMode_RootsEveryTopLevelFunction()
    {
        var ctx = ParseAsLibrary(
            "def add(a: int, b: int) -> int:\n    return a + b\n\n" +
            "def brightness(buf: bytearray) -> None:\n    buf[0] = 7\n",
            library: true);

        Assert.NotNull(ctx.RootAst);
        Assert.Equal(2, ctx.RootAst!.Functions.Count);
        Assert.All(ctx.RootAst.Functions, f => Assert.True(f.IsExportC));
    }

    [Fact]
    public void WithoutLibraryMode_FunctionsAreNotRooted()
    {
        var ctx = ParseAsLibrary(
            "def add(a: int, b: int) -> int:\n    return a + b\n",
            library: false);

        Assert.DoesNotContain(ctx.RootAst!.Functions, f => f.IsExportC);
    }

    [Fact]
    public void LibraryMode_SkipsInlineAndInterruptFunctions()
    {
        var ctx = ParseAsLibrary(
            "@inline\n" +
            "def twice(a: int) -> int:\n    return a + a\n\n" +
            "def go(a: int) -> int:\n    return twice(a)\n",
            library: true);

        var inline = Assert.Single(ctx.RootAst!.Functions, f => f.Name == "twice");
        Assert.False(inline.IsExportC);
        Assert.True(Assert.Single(ctx.RootAst.Functions, f => f.Name == "go").IsExportC);
    }

    [Fact]
    public void LibraryMode_ABufferParamCarriesItsOwnLength()
    {
        var ir = Gen(
            "def plasma(buf: bytearray, step: int) -> int:\n    return len(buf)\n",
            library: true);

        var fn = Assert.Single(ir.Functions);
        Assert.Equal(3, fn.Params.Count);
        // The hidden length rides after the declared parameters, so their positions do not
        // move; the qualified name is the function's own prefix plus __len_<param>.
        Assert.Equal(fn.Name + ".__len_buf", fn.Params[2]);
    }

    [Fact]
    public void WithoutLibraryMode_LenOfABufferParamHasNoAnswer()
    {
        var e = Assert.Throws<CompilerError>(() => Gen(
            "def plasma(buf: bytearray) -> int:\n    return len(buf)\n",
            library: false));
        Assert.Contains("len()", e.Message);
    }

    [Fact]
    public void LibraryMode_EntryWithExecutableStatements_CreatesSyntheticMain()
    {
        // Library mode with executable top-level statements (e.g., a variable assignment)
        // should still synthesize a main function, exactly as on main branch.
        var ir = Gen(
            "x: uint8 = 5\n" +
            "def helper() -> int:\n    return 42\n",
            library: true);

        // Should have synthetic main + helper function
        Assert.Equal(2, ir.Functions.Count);
        Assert.Contains(ir.Functions, f => f.Name == "main");
        Assert.Contains(ir.Functions, f => f.Name == "helper");
        var mainFn = ir.Functions.First(f => f.Name == "main");
        // main should contain the variable assignment (executable statement)
        Assert.NotEmpty(mainFn.Body);
    }

    [Fact]
    public void LibraryMode_DeclarationOnlyEntry_NoSyntheticMain()
    {
        // Library mode with only declarations (imports, function defs, class defs)
        // should NOT synthesize a main function.
        var ir = Gen(
            "def helper() -> int:\n    return 42\n" +
            "class Sensor:\n    def __init__(self):\n        pass\n",
            library: true);

        // Should only have the explicit top-level function, no synthetic main
        // Class methods are only compiled when instantiated, so Sensor.__init__ is not in IR yet
        Assert.Single(ir.Functions);
        Assert.Contains(ir.Functions, f => f.Name == "helper");
        Assert.DoesNotContain(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void LibraryMode_DeclarationWithInstantiation_CreatesSyntheticMain()
    {
        // Library mode with a class instantiation (executable statement)
        // should synthesize a main function.
        var ir = Gen(
            "def helper() -> int:\n    return 42\n" +
            "class Sensor:\n    def __init__(self):\n        pass\n" +
            "s = Sensor()\n",
            library: true);

        // Check what functions are generated
        var funcNames = ir.Functions.Select(f => f.Name).ToList();
        Assert.Contains("helper", funcNames);
        Assert.Contains("main", funcNames);
        // Sensor.__init__ is compiled when the class is instantiated in main
        // (may not be present in this test configuration)
    }

    [Fact]
    public void LibraryMode_SimpleExecutableStatement_CreatesSyntheticMain()
    {
        // Library mode with a simple executable statement (assignment)
        // should synthesize a main function.
        var ir = Gen(
            "x: uint8 = 5\n" +
            "def helper() -> int:\n    return 42\n",
            library: true);

        var funcNames = ir.Functions.Select(f => f.Name).ToList();
        Assert.Contains("helper", funcNames);
        Assert.Contains("main", funcNames);
        Assert.Equal(2, funcNames.Count);
    }

    [Fact]
    public void LibraryMode_ImportOnlyEntry_CreatesSyntheticMain()
    {
        // Library mode with only imports (no functions, no global statements)
        // should synthesize a main (import-only case from original behavior).
        var ir = Gen(
            "import sys\n" +
            "import os\n",
            library: true);

        // Should have synthetic main (import-only case)
        Assert.Single(ir.Functions);
        Assert.Contains(ir.Functions, f => f.Name == "main");
        var mainFn = ir.Functions.First(f => f.Name == "main");
        // main should only have an implicit return (no executable statements)
        Assert.Single(mainFn.Body);
        Assert.IsType<Return>(mainFn.Body[0]);
    }
}

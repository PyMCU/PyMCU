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
}

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A str whose text differs per path (issue #145). PyMCU strings are compile-time values, so
/// the generator used to keep folding one of them: the store was dropped and the program
/// printed the initializer on every path, with no diagnostic. What runs now is a dispatch on
/// the interned id the name holds.
/// </summary>
public class RuntimeDecidedStrTests
{
    // Without a HAL there is no writer for print() to resolve; one definition of the
    // by-reference string writer is all these programs need from it.
    private const string Prelude =
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n";

    private static ProgramIR GenerateIR(string source)
    {
        var lexer = new Lexer(Prelude + source);
        var parser = new Parser(lexer.Tokenize());
        return new IRGenerator().Generate(parser.ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
    }

    /// <summary>Every text written to the stream, in the order the writes are emitted.</summary>
    private static List<string> WrittenTexts(ProgramIR ir)
    {
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        var texts = new Dictionary<string, string>();
        foreach (var fd in body.OfType<FlashData>())
            texts[fd.Name] = new string(fd.Bytes.TakeWhile(b => b != 0).Select(b => (char)b).ToArray());

        return body.OfType<Call>()
            .SelectMany(c => c.Args)
            .OfType<FlashStrAddr>()
            .Select(a => texts.TryGetValue(a.Name, out var t) ? t : "")
            .ToList();
    }

    /// <summary>The ids stored into <paramref name="slot"/>, whatever their order.</summary>
    private static List<int> IdsStoredInto(ProgramIR ir, string slot) =>
        ir.Functions.SelectMany(f => f.Body)
            .OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name == slot && c.Src is Constant)
            .Select(c => ((Constant)c.Src).Value)
            .ToList();

    private static bool DispatchesOn(ProgramIR ir, string slot) =>
        ir.Functions.SelectMany(f => f.Body)
            .OfType<JumpIfNotEqual>()
            .Any(j => j.Src1 is Variable v && v.Name == slot && j.Src2 is Constant);

    [Fact]
    public void StrRebound_InRuntimeBranch_PrintsBothTexts()
    {
        // The issue's first shape. Before the fix the branch emitted no instruction at all,
        // the declaration copied the id into itself (`copy const 256 -> const 256`), and the
        // print folded to "idle" on every path -- "running" was not in the firmware.
        var ir = GenerateIR(
            "def main(seed: uint8):\n" +
            "    s: str = \"idle\"\n" +
            "    if seed > 10:\n" +
            "        s = \"running\"\n" +
            "    print(s)\n");

        Assert.Contains("idle", WrittenTexts(ir));
        Assert.Contains("running", WrittenTexts(ir));
        Assert.Equal(2, IdsStoredInto(ir, "main.s").Distinct().Count());
        Assert.True(DispatchesOn(ir, "main.s"));
    }

    [Fact]
    public void StrSlot_IsSixteenBitsWide()
    {
        // The id is >= 256. Stored in the byte-wide slot the name used to get, it came back
        // truncated and the print reported a number in the low hundreds (issue #80's shape).
        var ir = GenerateIR(
            "def main(seed: uint8):\n" +
            "    s: str = \"idle\"\n" +
            "    if seed > 10:\n" +
            "        s = \"running\"\n" +
            "    print(s)\n");

        Assert.All(ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
                .Where(c => c.Dst is Variable { Name: "main.s" }),
            c => Assert.Equal(DataType.UINT16, ((Variable)c.Dst).Type));
    }

    [Fact]
    public void StrRebound_InUnrolledLoop_PrintsBothTexts()
    {
        // The issue's second shape: the reassignment sits in a `for` the compiler unrolls, so
        // the stores survived but the print fell through to the decimal writer and reported
        // the id truncated to eight bits.
        var ir = GenerateIR(
            "def main(seed: uint8):\n" +
            "    s: str = \"start\"\n" +
            "    for i in range(3):\n" +
            "        if i == seed:\n" +
            "            s = \"found\"\n" +
            "    print(s)\n");

        Assert.Contains("start", WrittenTexts(ir));
        Assert.Contains("found", WrittenTexts(ir));
        Assert.True(DispatchesOn(ir, "main.s"));
    }

    [Fact]
    public void StrRebound_InRuntimeLoop_PrintsBothTexts()
    {
        // A loop body runs zero or more times, so both the value from before the loop and the
        // one the body writes are live at the exit. Folding the body's value printed "looped"
        // even when the loop never ran.
        var ir = GenerateIR(
            "def main(seed: uint8):\n" +
            "    s: str = \"start\"\n" +
            "    i: uint8 = 0\n" +
            "    while i < seed:\n" +
            "        s = \"looped\"\n" +
            "        i = i + 1\n" +
            "    print(s)\n");

        Assert.Contains("start", WrittenTexts(ir));
        Assert.Contains("looped", WrittenTexts(ir));
        Assert.True(DispatchesOn(ir, "main.s"));
    }

    [Fact]
    public void StrGlobal_ReboundByAnotherFunction_PrintsBothTexts()
    {
        // The issue's third shape. The store in bump() was dead: main's print folded to the
        // module initializer, and the module init itself stored a flat zero (the id truncated
        // into a uint8 global).
        var ir = GenerateIR(
            "state: str = \"idle\"\n" +
            "def bump():\n" +
            "    global state\n" +
            "    state = \"running\"\n" +
            "def main(seed: uint8):\n" +
            "    if seed > 10:\n" +
            "        bump()\n" +
            "    print(state)\n");

        Assert.Contains("idle", WrittenTexts(ir));
        Assert.Contains("running", WrittenTexts(ir));
        Assert.Equal(2, IdsStoredInto(ir, "state").Distinct().Count());
        Assert.DoesNotContain(0, IdsStoredInto(ir, "state"));
        Assert.True(DispatchesOn(ir, "state"));
    }

    [Fact]
    public void RuntimeDecidedStr_ComparedWithLiteral_ComparesTheIdAtRuntime()
    {
        // Interning gives equal texts the same id, so `s == "running"` IS the id comparison.
        // Folding it (which is what a compile-time string does) answered a flat False.
        var ir = GenerateIR(
            "def main(seed: uint8):\n" +
            "    s: str = \"idle\"\n" +
            "    if seed > 10:\n" +
            "        s = \"running\"\n" +
            "    if s == \"running\":\n" +
            "        print(\"yes\")\n" +
            "    else:\n" +
            "        print(\"no\")\n");

        Assert.True(DispatchesOn(ir, "main.s"));
        Assert.Contains("yes", WrittenTexts(ir));
        Assert.Contains("no", WrittenTexts(ir));
    }

    [Fact]
    public void RuntimeDecidedStr_UsedAsAValue_IsRefusedByName()
    {
        // Anything other than printing or comparing it would receive the id and treat it as
        // data. The refusal names the texts, because the whole difficulty is that the reader
        // sees several and the compiler used to pick one without saying so.
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => GenerateIR(
            "def main(seed: uint8):\n" +
            "    s: str = \"idle\"\n" +
            "    if seed > 10:\n" +
            "        s = \"running\"\n" +
            "    n: uint8 = len(s)\n"));

        Assert.Contains("no single compile-time value", ex.Message);
        Assert.Contains("\"idle\"", ex.Message);
        Assert.Contains("\"running\"", ex.Message);
    }

    [Fact]
    public void StrReboundUnconditionally_StillFolds()
    {
        // A rebind outside any branch leaves one value again, and that value is the right one
        // to fold from there on: one write_str, no dispatch.
        var ir = GenerateIR(
            "def main(seed: uint8):\n" +
            "    s: str = \"first\"\n" +
            "    if seed > 10:\n" +
            "        s = \"second\"\n" +
            "    s = \"third\"\n" +
            "    print(s)\n");

        Assert.Contains("third", WrittenTexts(ir));
        Assert.DoesNotContain("first", WrittenTexts(ir));
        Assert.DoesNotContain("second", WrittenTexts(ir));
        Assert.False(DispatchesOn(ir, "main.s"));
    }

    [Fact]
    public void CompileTimeBranchOverStrings_StillFolds()
    {
        // The HAL dispatches pin names through if/elif chains over compile-time conditions.
        // Only one branch is ever lowered, so the name keeps a single value and nothing about
        // its lowering may change: one write_str, no slot, no dispatch.
        var ir = GenerateIR(
            "def main():\n" +
            "    port: str = \"PB\"\n" +
            "    if 1 == 2:\n" +
            "        port = \"PD\"\n" +
            "    print(port)\n");

        Assert.Equal(new List<string> { "PB", "\n" }, WrittenTexts(ir));
        Assert.False(DispatchesOn(ir, "main.port"));
    }

    [Fact]
    public void SingleBindingStr_EmitsNoSlotStore()
    {
        // One binding cannot disagree with itself: the fold is always right and the name costs
        // nothing. This is the shape almost every string in the HAL has.
        var ir = GenerateIR(
            "def main():\n" +
            "    banner: str = \"ready\"\n" +
            "    print(banner)\n");

        Assert.Equal(new List<string> { "ready", "\n" }, WrittenTexts(ir));
        Assert.Empty(IdsStoredInto(ir, "main.banner"));
    }

    [Fact]
    public void StrGlobal_OfAnImportedModule_ReboundThere_PrintsBothTexts()
    {
        // An imported module runs its own module level, so its str global has real storage and
        // an initializer of its own; a function of that module rebinding it is the same
        // divergence as in the entry file, one module further away.
        var modLexer = new Lexer(
            "state: str = \"idle\"\n" +
            "def bump():\n" +
            "    global state\n" +
            "    state = \"running\"\n");
        var mod = new Parser(modLexer.Tokenize()).ParseProgram();

        var mainLexer = new Lexer(Prelude +
            "import statemod\n" +
            "def main(seed: uint8):\n" +
            "    if seed > 10:\n" +
            "        statemod.bump()\n" +
            "    print(statemod.state)\n");
        var ir = new IRGenerator().Generate(new Parser(mainLexer.Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode> { ["statemod"] = mod },
            new DeviceConfig { Arch = "avr" });

        Assert.Contains("idle", WrittenTexts(ir));
        Assert.Contains("running", WrittenTexts(ir));
        // The rebinding function stores the id into the module's own global. The module
        // initializer that stores the declared text is synthesized by the driver's module
        // pass, which this bare harness does not run -- the str-runtime-module fixture in
        // pymcu-avr covers that half on the emulator.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Dst is Variable { Name: "statemod_state", Type: DataType.UINT16 });
        Assert.True(DispatchesOn(ir, "statemod_state"));
    }
}

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `d[k]` over a dict whose values are all strings, with a run-time key: the lookup hands
/// back the interned id of the chosen text, and the numeric writer printed it -- 258 for
/// "CD". The texts are compile-time; only which one is decided at run time, so a read
/// dispatches on the id exactly like a name bound to several texts (#145). The binding in
/// `s = d[k]` is the same shape. This is what `errno.errorcode[code]` needs: the table is a
/// module attribute there, not a bare name.
/// </summary>
public class DictOfStringsLookupTests
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

    private static ProgramIR GenerateIR(string mainSrc, params (string Name, string Source)[] modules)
    {
        var imported = new Dictionary<string, ProgramNode>();
        foreach (var (name, source) in modules)
            imported[name] = new Parser(new Lexer(source).Tokenize()).ParseProgram();
        var mainAst = new Parser(new Lexer(Prelude + mainSrc).Tokenize()).ParseProgram();
        var ctx = new PyMCU.Common.CompilationContext(new CompilerOptions(
            FilePath: "main.py", OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        foreach (var (name, _) in modules) ctx.ProjectModules.Add(name);
        return new IRGenerator().Generate(mainAst, imported, new DeviceConfig { Arch = "avr" },
                                          projectModules: ctx.ProjectModules);
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

    /// <summary>The interned ids a comparison decides on: one JumpIfNotEqual per text.</summary>
    private static bool DispatchesOn(ProgramIR ir, string slot) =>
        ir.Functions.SelectMany(f => f.Body)
            .OfType<JumpIfNotEqual>()
            .Any(j => j.Src1 is Variable v && v.Name == slot && j.Src2 is Constant);

    private static bool WritesAnyDecimal(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Any(c => c.FunctionName.Contains("decimal"));

    [Fact]
    public void DictSubscript_RunTimeKey_PrintsThePickedText()
    {
        var ir = GenerateIR(
            "d = {1: \"AB\", 2: \"CD\"}\n" +
            "def main(seed: uint8):\n" +
            "    print(d[seed])\n");

        Assert.Contains("AB", WrittenTexts(ir));
        Assert.Contains("CD", WrittenTexts(ir));
        Assert.False(WritesAnyDecimal(ir));
    }

    [Fact]
    public void DictSubscript_RunTimeKey_BindsTheTextToTheName()
    {
        // `s = d[k]` stores the picked id, and a read of s has to dispatch on it: the
        // number writer used to print the id itself.
        var ir = GenerateIR(
            "d = {1: \"AB\", 2: \"CD\"}\n" +
            "def main(seed: uint8):\n" +
            "    s = d[seed]\n" +
            "    print(s)\n");

        Assert.Contains("AB", WrittenTexts(ir));
        Assert.Contains("CD", WrittenTexts(ir));
        Assert.True(DispatchesOn(ir, "main.s"));
        Assert.False(WritesAnyDecimal(ir));
    }

    [Fact]
    public void DictSubscript_AtModuleLevel_DispatchesOnTheGlobalSlot()
    {
        // Top level runs inside `main`: the `main.<n>` probe of the binding's keys met the
        // assignment record and stopped before the module-global key that carries the mark.
        var ir = GenerateIR(
            "def pick() -> uint8:\n" +
            "    return 2\n" +
            "d = {1: \"AB\", 2: \"CD\"}\n" +
            "k = pick()\n" +
            "s = d[k]\n" +
            "print(s)\n");

        Assert.Contains("AB", WrittenTexts(ir));
        Assert.Contains("CD", WrittenTexts(ir));
        Assert.True(DispatchesOn(ir, "s"));
        Assert.False(WritesAnyDecimal(ir));
    }

    [Fact]
    public void ModuleAttributeDict_RunTimeKey_PrintsThePickedText()
    {
        // `errno_l.errorcode[code]` -- the MicroPython layer's errno table -- reaches the
        // dict through a module attribute, not a bare name.
        var ir = GenerateIR(
            "import errno_l\n" +
            "def main(seed: uint8):\n" +
            "    print(errno_l.errorcode[seed])\n",
            ("errno_l", "errorcode = {1: \"EPERM\", 5: \"EIO\"}\n"));

        Assert.Contains("EPERM", WrittenTexts(ir));
        Assert.Contains("EIO", WrittenTexts(ir));
        Assert.False(WritesAnyDecimal(ir));
    }

    [Fact]
    public void DictGet_RunTimeKey_PrintsTextOrDefault()
    {
        var ir = GenerateIR(
            "d = {1: \"AB\", 2: \"CD\"}\n" +
            "def main(seed: uint8):\n" +
            "    print(d.get(seed, \"ZZ\"))\n");

        Assert.Contains("AB", WrittenTexts(ir));
        Assert.Contains("CD", WrittenTexts(ir));
        Assert.Contains("ZZ", WrittenTexts(ir));
        Assert.False(WritesAnyDecimal(ir));
    }

    [Fact]
    public void DictSubscript_InAnFString_PrintsThePickedText()
    {
        var ir = GenerateIR(
            "d = {1: \"AB\", 2: \"CD\"}\n" +
            "def main(seed: uint8):\n" +
            "    s = d[seed]\n" +
            "    print(f\"->{d[seed]}<-{s}\")\n");

        Assert.Contains("AB", WrittenTexts(ir));
        Assert.Contains("CD", WrittenTexts(ir));
        Assert.False(WritesAnyDecimal(ir));
    }
}

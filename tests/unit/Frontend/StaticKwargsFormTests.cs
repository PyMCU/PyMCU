using System;
using System.Collections.Generic;
using System.Linq;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `**kwargs` and `*args` are compile-time forms, not run-time containers (#368).
///
/// A function that takes `**kwargs` is specialised per call site, exactly as a function taking
/// a ZCA instance already is, so at each site the extra keyword arguments are written out in
/// the source and `kwargs` is a closed mapping of literal keys to expressions. `*args` is the
/// same over positions. Nothing is collected, nothing is allocated.
///
/// Both front ends, separately. They did not refuse these forms with the same words: the C#
/// parser wrote a sentence (`Parser.cs:878`, `:888`) and the CPython bridge wrote the bare
/// feature name (`pymcu_translate.py:1112`, `:1114`), so the same refused program was explained
/// on one front end and merely named on the other. And `f(**d)` had no branch at all in the C#
/// argument loop, so it fell through to `Expected expression` pointed at line 1 of the file --
/// a comment the driver had injected.
///
/// WHAT DISCRIMINATES: every `Parses` assertion. All of them throw today, which is the point.
///
/// WHAT IS INVARIANT: the two refusals that must survive, held at the bottom. A `**` whose
/// operand is a run-time dict, and a key no callee accepts, stay refused -- lifting the form
/// must not lift the model with it. And `**` as the power operator keeps working: the
/// refusals being removed are wired to a token that has a second meaning three productions
/// away.
/// </summary>
public class StaticKwargsFormTests
{
    private static ProgramNode Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    /// The same source through the CPython front end, which reports by raising in
    /// pymcu_translate.py and surfaces here as the SyntaxError the C# parser would have thrown.
    private static ProgramNode Translate(string src) =>
        PythonAstReader.ParseSource(src, "main.py");

    private static string Refusal(string src) =>
        Assert.ThrowsAny<Exception>(() => Parse(src)).Message;

    private static string TranslatorRefusal(string src) =>
        Assert.ThrowsAny<Exception>(() => Translate(src)).Message;

    /// Splicing is decided in the IR generator, not in the parser, so the refusals about a
    /// mapping the compiler cannot see are only observable with the generator run.
    private static string Lowered(ProgramNode prog) =>
        Assert.ThrowsAny<Exception>(() => new IRGenerator().Generate(
            prog, new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" })).Message;

    private static string LoweringRefusal(string src) => Lowered(Parse(src));

    private static string TranslatorLoweringRefusal(string src) => Lowered(Translate(src));

    private const string KwargsDef =
        "def show(a: uint8, **kwargs) -> uint8:\n" +
        "    return a\n";

    private const string ArgsDef =
        "def total(*args) -> uint8:\n" +
        "    return 0\n";

    private const string RunTimeMapping =
        "def f(interval_ms: uint8 = 10) -> uint8:\n" +
        "    return interval_ms\n" +
        "\n" +
        "def main():\n" +
        "    d = 7\n" +
        "    f(**d)\n";

    private const string ForwardingCtor =
        "class Base:\n" +
        "    def __init__(self, pin: uint8, interval_ms: uint8 = 10):\n" +
        "        self.pin = pin\n" +
        "        self.interval_ms = interval_ms\n" +
        "\n" +
        "class Child(Base):\n" +
        "    def __init__(self, pin: uint8, **kwargs):\n" +
        "        super().__init__(pin, **kwargs)\n";

    // -- the `def` side ------------------------------------------------------

    [Fact]
    public void AKeywordDictionaryParameterParses()
    {
        var prog = Parse(KwargsDef);

        Assert.Single(prog.Functions);
        Assert.Contains(prog.Functions[0].Params, p => p.Name == "kwargs");
    }

    [Fact]
    public void AKeywordDictionaryParameterParsesOnTheCPythonFrontEnd()
    {
        var prog = Translate(KwargsDef);

        Assert.Single(prog.Functions);
        Assert.Contains(prog.Functions[0].Params, p => p.Name == "kwargs");
    }

    [Fact]
    public void AVariadicPositionalParameterParses()
    {
        var prog = Parse(ArgsDef);

        Assert.Single(prog.Functions);
        Assert.Contains(prog.Functions[0].Params, p => p.Name == "args");
    }

    [Fact]
    public void AVariadicPositionalParameterParsesOnTheCPythonFrontEnd()
    {
        var prog = Translate(ArgsDef);

        Assert.Single(prog.Functions);
        Assert.Contains(prog.Functions[0].Params, p => p.Name == "args");
    }

    [Fact]
    public void TheBareKeywordOnlySeparatorStillParses()
    {
        // PEP 3102. It shares the token with `*args` and means something else, so a change to
        // the varargs branch is one edit away from taking every CircuitPython signature with it.
        var prog = Parse("def uart(tx: uint8, rx: uint8, *, baudrate: uint16 = 9600) -> uint8:\n"
                         + "    return tx\n");

        Assert.Single(prog.Functions);
        Assert.Contains(prog.Functions[0].Params, p => p.Name == "baudrate");
    }

    // -- the call side -------------------------------------------------------

    [Fact]
    public void ADoubleStarArgumentParses()
    {
        var prog = Parse(
            "def main():\n" +
            "    d = {\"a\": 1}\n" +
            "    f(**d)\n");

        Assert.Single(prog.Functions);
    }

    [Fact]
    public void ADoubleStarArgumentParsesOnTheCPythonFrontEnd()
    {
        var prog = Translate(
            "def main():\n" +
            "    d = {\"a\": 1}\n" +
            "    f(**d)\n");

        Assert.Single(prog.Functions);
    }

    [Fact]
    public void ForwardingToABaseConstructorParses()
    {
        var prog = Parse(ForwardingCtor);

        Assert.Equal(2, prog.GlobalStatements.OfType<ClassDef>().Count());
    }

    [Fact]
    public void ForwardingToABaseConstructorParsesOnTheCPythonFrontEnd()
    {
        var prog = Translate(ForwardingCtor);

        Assert.Equal(2, prog.GlobalStatements.OfType<ClassDef>().Count());
    }

    [Fact]
    public void ASingleStarArgumentStillParses()
    {
        // This one already worked, on both front ends, and builds to identical firmware. It is
        // here so that teaching the argument loop about `**` cannot silently cost it.
        var prog = Parse(
            "def main():\n" +
            "    xs = [1, 2, 3]\n" +
            "    add3(*xs)\n");

        Assert.Single(prog.Functions);
    }

    // -- what stays refused --------------------------------------------------

    [Fact]
    public void ThePowerOperatorIsNotTheKeywordDictionary()
    {
        var prog = Parse("def main():\n    return 2 ** 8\n");

        var ret = Assert.IsType<ReturnStmt>(prog.Functions[0].Body.Statements[0]);
        var bin = Assert.IsType<BinaryExpr>(ret.Value);
        Assert.Equal(BinaryOp.Pow, bin.Op);
    }

    [Fact]
    public void AKeyNoCalleeAcceptsIsRefusedByName()
    {
        // The whole value of splicing at compile time is that a misspelled keyword is a build
        // error rather than an argument silently dropped. The key must appear in the sentence.
        var msg = LoweringRefusal(
            "def f(interval_ms: uint8 = 10) -> uint8:\n" +
            "    return interval_ms\n" +
            "\n" +
            "def main():\n" +
            "    d = {\"intrval_ms\": 7}\n" +
            "    f(**d)\n");

        Assert.Contains("intrval_ms", msg);
    }

    [Fact]
    public void ADoubleStarOverSomethingThatIsNotAMappingIsRefused()
    {
        // Splicing needs the keys now. A name the compiler cannot read a mapping out of has
        // none, so the refusal has to say that rather than emit a call with nothing in it.
        var msg = LoweringRefusal(RunTimeMapping);

        Assert.Contains("compile time", msg);
        Assert.Contains("**", msg);
    }

    [Fact]
    public void BothFrontEndsRefuseANonMappingWithTheSameSentence()
    {
        Assert.Equal(LoweringRefusal(RunTimeMapping), TranslatorLoweringRefusal(RunTimeMapping));
    }
}

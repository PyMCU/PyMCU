using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A string handed to a parameter of a real subroutine (PyMCU#436). The call has always passed
/// the flash address of the text; the parameter was a one-byte slot (`str`), or no string
/// slot at all (unannotated, or a method, which received the interned id), and the body
/// printed a number: `def g(msg=None): print(msg)` with `g('hi')` printed 144.
///
/// A string parameter of a subroutine now holds the address whole, an unannotated parameter
/// that only ever receives strings is one, and what cannot fit such a slot -- None, a string
/// built at run time, a string next to a number -- is refused where the call is written.
/// </summary>
public class StrParameterSlotTests
{
    private const string Prelude =
        "from pymcu.types import const, inline, uint8, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static Function Fn(ProgramIR ir, string name) =>
        ir.Functions.Single(f => f.Name == name);

    private static bool StreamsTheParameter(Function f, string param) =>
        f.Body.Any(i => i is Call c && c.FunctionName.EndsWith("uart_write_str")
                        && c.Args.Any(a => a is Variable v && v.Name == param));

    private static bool WritesADecimal(Function f) =>
        f.Body.Any(i => i is Call c && c.FunctionName.Contains("uart_write_decimal"));

    [Fact]
    public void AnUnannotatedParameterGivenOnlyStrings_StreamsItsText()
    {
        var ir = Gen(
            "def g(msg):\n" +
            "    print(msg)\n" +
            "g('hi')\n" +
            "g('yo')\n");

        var g = Fn(ir, "g");
        Assert.True(StreamsTheParameter(g, "g.msg"));
        Assert.False(WritesADecimal(g));
        Assert.All(Fn(ir, "main").Body.OfType<Call>().Where(c => c.FunctionName == "g"),
            c => Assert.IsType<FlashStrAddr>(Assert.Single(c.Args)));
    }

    [Fact]
    public void AMethodGivenAString_ReceivesItsFlashAddress()
    {
        var ir = Gen(
            "class C:\n" +
            "    def __init__(self):\n" +
            "        self.z: uint8 = G.value\n" +
            "    def g(self, msg):\n" +
            "        print(msg)\n" +
            "        print(self.z)\n" +
            "c = C()\n" +
            "c.g('hi')\n");

        var call = Fn(ir, "main").Body.OfType<Call>().Single(c => c.FunctionName.EndsWith("_g"));
        Assert.IsType<FlashStrAddr>(call.Args[^1]);
        var g = ir.Functions.Single(f => f.Name == call.FunctionName);
        Assert.Contains(g.Body, i => i is Call c && c.FunctionName.EndsWith("uart_write_str")
                                     && c.Args.Any(a => a is Variable));
    }

    [Fact]
    public void NoneForAStrParameterOfASubroutine_IsRefused()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(
            "def g(msg=None):\n" +
            "    print(msg)\n" +
            "g('hi')\n" +
            "g()\n"));
        Assert.Contains("None has none", ex.Message);
    }

    [Fact]
    public void AStringNextToANumber_IsRefusedWhereItIsPassed()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(
            "def g(msg):\n" +
            "    print(msg)\n" +
            "g('hi')\n" +
            "g(5)\n"));
        Assert.Contains("receives a string here", ex.Message);
    }

    // Every text a string slot holds is interned once, so `==` against another known text is
    // the address comparison -- not the flash address against an interned id, which was
    // never equal.
    [Fact]
    public void ComparingAStrParameterWithALiteral_ComparesFlashAddresses()
    {
        var ir = Gen(
            "def g(msg: str):\n" +
            "    if msg == 'hi':\n" +
            "        print('eq')\n" +
            "g('hi')\n");

        var g = Fn(ir, "g");
        Assert.Contains(g.Body, i => i is Copy { Src: FlashStrAddr });
        Assert.DoesNotContain(g.Body, i => i is JumpIfNotEqual { Src2: Constant { Value: >= 256 } });
    }

    [Fact]
    public void AnInlineParameterLeftAtItsNoneDefault_PrintsNone()
    {
        var ir = Gen(
            "@inline\n" +
            "def g(msg=None):\n" +
            "    print(msg)\n" +
            "g()\n");

        Assert.False(WritesADecimal(Fn(ir, "main")));
    }
}

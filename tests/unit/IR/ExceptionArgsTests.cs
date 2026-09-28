using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The message word of `except X as e` is zero for a raise with no argument, and nothing
/// read it that way. `print(e)` handed zero to the flash string writer, which streamed the
/// interrupt vector table ("3" and a control byte), and `e.args[0]` did the same where
/// CPython raises IndexError, args being `()`. `E("")` is not `E()`: its args is `('',)`,
/// so the parser records whether the call had an argument.
/// </summary>
public class ExceptionArgsTests
{
    private const string Prelude =
        "from pymcu.types import uint8, uint16, int32\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n" +
        "def uart_write_decimal_i32(v: int32):\n" +
        "    pass\n" +
        "class E1(Exception):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Fn(ProgramIR ir, string name) =>
        ir.Functions.Single(f => f.Name == name).Body;

    private const string Boom =
        "def boom(k: uint8) -> uint8:\n" +
        "    if k == 0:\n" +
        "        raise E1()\n" +
        "    return k\n";

    [Fact]
    public void PrintOfTheMessageSkipsTheWriterOnZero()
    {
        var main = Fn(Gen(Boom +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as e:\n" +
            "    print(e)\n"), "main");

        main.Any(i => i is JumpIfZero { Condition: Variable { Name: "__exn_msg" } }).Should().BeTrue();
    }

    [Fact]
    public void ArgsZeroOfAnArgumentlessRaiseIsAnIndexError()
    {
        var main = Fn(Gen(Boom +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as e:\n" +
            "    print(e.args[0])\n"), "main");

        main.Any(i => i is JumpIfNotZero { Condition: Variable { Name: "__exn_msg" } }).Should().BeTrue();
        main.Any(i => i is Copy { Src: FlashStrAddr, Dst: Variable { Name: "__exn_msg" } }).Should().BeTrue(because: "the IndexError carries CPython's 'tuple index out of range'");
    }

    private static string FlashText(ProgramIR ir, string name) =>
        new string(ir.Functions.SelectMany(f => f.Body).OfType<FlashData>()
            .Single(fd => fd.Name == name).Bytes.TakeWhile(b => b != 0)
            .Select(b => (char)b).ToArray());

    private static List<string> StrWritesOf(ProgramIR ir, string fn) =>
        ir.Functions.Single(f => f.Name == fn).Body
            .OfType<Copy>()
            .Where(c => c.Dst is Variable { Name: "uart_write_str.s" } && c.Src is FlashStrAddr)
            .Select(c => FlashText(ir, ((FlashStrAddr)c.Src).Name))
            .ToList();

    [Fact]
    public void PrintArgsStreamsTheTupleBehindTheRuntimeArgumentCheck()
    {
        var ir = Gen(Boom +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as e:\n" +
            "    print(e.args)\n");
        var main = Fn(ir, "main");

        // `e.args` is `()` for the argument-less raise and `('x',)` for one with a
        // message -- which of the two is a run-time fact, so the parens are written
        // unconditionally and the element plus its one-element comma behind
        // `__exn_msg != 0`.
        main.Any(i => i is JumpIfNotZero { Condition: Variable { Name: "__exn_msg" } })
            .Should().BeTrue(because: "() or (msg,) is decided by the message word at run time");
        StrWritesOf(ir, "main").Should().Contain("(").And.Contain(",").And.Contain(")");
    }

    [Fact]
    public void LenArgsIsTheRuntimeArgumentCount()
    {
        var ir = Gen(Boom +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as e:\n" +
            "    x = len(e.args)\n" +
            "    print(x)\n");
        var main = Fn(ir, "main");

        main.Any(i => i is Binary { Op: PyMCU.IR.BinaryOp.NotEqual, Src1: Variable { Name: "__exn_msg" } })
            .Should().BeTrue(because: "args is () or (msg,) -- len is 0 or 1, read off the word");
    }

    [Fact]
    public void ArgsBoundToANameIsRefused()
    {
        var act = () => Gen(Boom +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as e:\n" +
            "    t = e.args\n");
        act.Should().Throw<PyMCU.Common.CompilerError>()
            .WithMessage("*the tuple of the raise's argument*");
    }

    [Fact]
    public void ForOverArgsIsRefused()
    {
        var act = () => Gen(Boom +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as e:\n" +
            "    for a in e.args:\n" +
            "        print(a)\n");
        act.Should().Throw<PyMCU.Common.CompilerError>()
            .WithMessage("*no sequence here to step through*");
    }

    [Fact]
    public void AnEmptyStringArgumentIsAnArgument()
    {
        var ir = Gen(
            "def boom(k: uint8) -> uint8:\n" +
            "    if k == 0:\n" +
            "        raise E1(\"\")\n" +
            "    return k\n" +
            "try:\n" +
            "    boom(0)\n" +
            "except E1 as e:\n" +
            "    print(e.args[0])\n");

        Fn(ir, "boom").Any(i =>
            i is Copy { Src: FlashStrAddr, Dst: Variable { Name: "__exn_msg" } }).Should().BeTrue(because: "E(\"\") has args ('',): the word points at an empty string, not zero");
    }
}

using System;
using System.Linq;
using FluentAssertions;
using PyMCU.Frontend;
using PyMCU.Frontend.Pio;
using Xunit;

namespace PyMCU.UnitTests;

public class PioAssemblerTests
{
    private const char NL = '\n';

    // Parse a single @asm_pio function and assemble it.
    private static AssembledPioProgram Asm(string body, string decorator = "@asm_pio")
    {
        var src = decorator + NL + "def prog():" + NL + body;
        var parser = new Parser(new Lexer(src).Tokenize());
        var prog = parser.ParseProgram();
        var fn = prog.Functions.Single();
        fn.IsPioProgram.Should().BeTrue("the decorator must flag the function as a PIO program");
        return PioAssembler.Assemble(fn);
    }

    private static string Line(string s) => "    " + s + NL;

    [Fact]
    public void Parser_FlagsAsmPioDecorator()
    {
        var p = Asm(Line("set(pins, 1)"));
        p.Words.Should().HaveCount(1);
    }

    [Fact]
    public void Parser_FlagsDottedRp2AsmPio()
    {
        var p = Asm(Line("nop()"), decorator: "@rp2.asm_pio");
        p.Words.Should().Equal((ushort)0xA042);
    }

    [Theory]
    [InlineData("set(pins, 1)", 0xE001)]
    [InlineData("set(x, 31)", 0xE03F)]
    [InlineData("out(pins, 1)", 0x6001)]
    [InlineData("out(pindirs, 32)", 0x6080)]   // 32 encodes as 0
    [InlineData("in_(pins, 1)", 0x4001)]
    [InlineData("nop()", 0xA042)]
    [InlineData("push(block)", 0x8020)]
    [InlineData("pull(block)", 0x80A0)]
    [InlineData("push(noblock)", 0x8000)]
    [InlineData("push(iffull)", 0x8060)]
    [InlineData("mov(x, y)", 0xA022)]
    [InlineData("mov(x, invert(y))", 0xA02A)]
    [InlineData("mov(y, reverse(x))", 0xA051)]
    [InlineData("wait(1, pin, 0)", 0x20A0)]
    [InlineData("wait(0, gpio, 5)", 0x2005)]
    [InlineData("irq(5)", 0xC005)]
    [InlineData("irq(clear, 2)", 0xC042)]
    public void Encode_SingleInstruction(string instr, int expected)
    {
        var p = Asm(Line(instr));
        p.Words.Should().Equal((ushort)expected);
    }

    [Fact]
    public void Encode_JmpResolvesLabel()
    {
        // label at pc 0, jmp back to it (unconditional) at pc 0 -> addr 0.
        var p = Asm(Line("label(\"loop\")") + Line("jmp(\"loop\")"));
        p.Words.Should().Equal((ushort)0x0000);   // JMP, cond 0, addr 0
    }

    [Fact]
    public void Encode_JmpConditionAndForwardLabel()
    {
        // nop (pc0), jmp(x_dec, "done") (pc1 -> addr 2), nop (pc2 = "done")
        var p = Asm(Line("nop()") + Line("jmp(x_dec, \"done\")") +
                    Line("label(\"done\")") + Line("nop()"));
        p.Words.Should().Equal((ushort)0xA042,
                               (ushort)(0x0000 | (2 << 5) | 2),   // JMP x_dec -> addr 2
                               (ushort)0xA042);
    }

    [Fact]
    public void Encode_SideSetAndDelay()
    {
        // sideset_init with 1 pin (no opt): delayBits = 4.
        // nop().side(1)[1] -> field = delay(1) | (side(1) << 4) = 0x11 -> <<8 = 0x1100
        var p = Asm(Line("nop().side(1)[1]"), decorator: "@asm_pio(sideset_init=[PIO.OUT_LOW])");
        p.Words.Should().Equal((ushort)(0xA042 | 0x1100));
    }

    [Fact]
    public void Encode_DelayOnly()
    {
        var p = Asm(Line("nop()[3]"));
        p.Words.Should().Equal((ushort)(0xA042 | (3 << 8)));
    }

    [Fact]
    public void WrapMarkers_DefaultAndExplicit()
    {
        var def = Asm(Line("nop()") + Line("nop()"));
        def.WrapTarget.Should().Be(0);
        def.Wrap.Should().Be(1);

        var ex = Asm(Line("nop()") + Line("wrap_target()") + Line("set(x, 0)") + Line("wrap()"));
        ex.WrapTarget.Should().Be(1);   // wrap_target before the 2nd real instr
        ex.Wrap.Should().Be(1);         // wrap after it (pc-1)
        ex.Words.Should().HaveCount(2); // directives don't emit words
    }

    [Fact]
    public void Config_FromDecoratorKwargs()
    {
        var p = Asm(Line("nop()"),
            decorator: "@asm_pio(autopull=True, pull_thresh=8, out_init=[PIO.OUT, PIO.OUT], " +
                       "out_shiftdir=PIO.SHIFT_RIGHT, fifo_join=PIO.JOIN_TX)");
        p.Config.AutoPull.Should().BeTrue();
        p.Config.PullThreshold.Should().Be(8);
        p.Config.OutInitCount.Should().Be(2);
        p.Config.OutShiftDir.Should().Be(PioShiftDir.Right);
        p.Config.FifoJoin.Should().Be(PioFifoJoin.Tx);
    }

    [Fact]
    public void Error_UndefinedLabel()
    {
        Action act = () => Asm(Line("jmp(\"missing\")"));
        act.Should().Throw<PioAsmException>().WithMessage("*undefined label*");
    }

    [Fact]
    public void Error_EmptyProgram()
    {
        Action act = () => Asm(Line("pass"));
        act.Should().Throw<PioAsmException>().WithMessage("*empty*");
    }

    [Fact]
    public void Error_ProgramTooLong()
    {
        var body = string.Concat(Enumerable.Repeat(Line("nop()"), 33));
        Action act = () => Asm(body);
        act.Should().Throw<PioAsmException>().WithMessage("*max 32*");
    }

    [Fact]
    public void Error_SideSetWithoutInit()
    {
        Action act = () => Asm(Line("nop().side(1)"));
        act.Should().Throw<PioAsmException>().WithMessage("*no sideset_init*");
    }
}

using System.Linq;
using FluentAssertions;
using PyMCU.Frontend;
using PyMCU.Frontend.Pio;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// lib/src/pymcu/hal/rp/ws2812.py carries the WS2812 program as four machine words, placed at
/// word 28 of PIO0's instruction memory. They were encoded by hand from the pico-examples
/// source, so here the compiler's own PIO assembler assembles that source and the two have to
/// agree (the jump targets differ by the load offset, since a jump is absolute).
/// </summary>
public class Ws2812PioProgramTests
{
    // What the HAL stores at words 28..31 of PIO0.
    private static readonly int[] HalWords = { 0x6221, 0x113F, 0x141C, 0xA442 };
    private const int LoadOffset = 28;

    private const string Source =
        "@rp2.asm_pio(sideset_init=PIO.OUT_LOW, out_shiftdir=PIO.SHIFT_LEFT, autopull=True, pull_thresh=8)\n" +
        "def ws2812():\n" +
        "    wrap_target()\n" +
        "    label(\"bitloop\")\n" +
        "    out(x, 1).side(0)[2]\n" +
        "    jmp(not_x, \"do_zero\").side(1)[1]\n" +
        "    jmp(\"bitloop\").side(1)[4]\n" +
        "    label(\"do_zero\")\n" +
        "    nop().side(0)[4]\n" +
        "    wrap()\n";

    private static AssembledPioProgram Assemble()
    {
        var prog = new Parser(new Lexer(Source).Tokenize()).ParseProgram();
        return PioAssembler.Assemble(prog.Functions.Single());
    }

    [Fact]
    public void TheHalsWordsAreTheAssemblersWordsMovedToTheTopOfInstructionMemory()
    {
        var assembled = Assemble();

        assembled.Words.Should().HaveCount(4);
        for (int i = 0; i < 4; i++)
        {
            int w = assembled.Words[i];
            bool isJmp = (w >> 13) == 0;
            int moved = isJmp ? (w & ~0x1F) | ((w & 0x1F) + LoadOffset) : w;
            HalWords[i].Should().Be(moved, $"word {i} of the program");
        }
    }

    [Fact]
    public void TheProgramWrapsOverItsWholeBody()
    {
        var assembled = Assemble();

        assembled.WrapTarget.Should().Be(0);
        assembled.Wrap.Should().Be(3);
    }
}

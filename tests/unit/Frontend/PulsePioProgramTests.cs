using System.Linq;
using FluentAssertions;
using PyMCU.Frontend;
using PyMCU.Frontend.Pio;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// lib/src/pymcu/hal/rp/pulse.py loads the pulse-width program as 17 machine words. They are the
/// ones this compiler's PIO assembler makes of the source below (which is also the listing in the
/// module's header), so a mistake in a hand-copied word fails here and not on a bench.
/// </summary>
public class PulsePioProgramTests
{
    // What the HAL loads into words 0..16 of PIO1.
    private static readonly int[] HalWords =
    {
        0x00C3, 0x20A0, 0x0005, 0x2020, 0x000B, 0xA02B, 0x00C8, 0x0009, 0x0046,
        0xA0C9, 0x8000, 0xA02B, 0x00CE, 0x004C, 0xA0C9, 0x8000, 0x0005,
    };

    private const string Source =
        "@rp2.asm_pio()\n" +
        "def pulses():\n" +
        "    label(\"start\")\n" +
        "    jmp(pin, \"hi_first\")\n" +
        "    wait(1, pin, 0)\n" +
        "    jmp(\"count_h\")\n" +
        "    label(\"hi_first\")\n" +
        "    wait(0, pin, 0)\n" +
        "    jmp(\"count_l\")\n" +
        "    label(\"count_h\")\n" +
        "    mov(x, invert(null))\n" +
        "    label(\"h_loop\")\n" +
        "    jmp(pin, \"h_dec\")\n" +
        "    jmp(\"done_h\")\n" +
        "    label(\"h_dec\")\n" +
        "    jmp(x_dec, \"h_loop\")\n" +
        "    label(\"done_h\")\n" +
        "    mov(isr, invert(x))\n" +
        "    push(noblock)\n" +
        "    label(\"count_l\")\n" +
        "    mov(x, invert(null))\n" +
        "    label(\"l_loop\")\n" +
        "    jmp(pin, \"done_l\")\n" +
        "    jmp(x_dec, \"l_loop\")\n" +
        "    label(\"done_l\")\n" +
        "    mov(isr, invert(x))\n" +
        "    push(noblock)\n" +
        "    jmp(\"count_h\")\n";

    [Fact]
    public void TheHalsWordsAreTheAssemblersWords()
    {
        var prog = new Parser(new Lexer(Source).Tokenize()).ParseProgram();
        var assembled = PioAssembler.Assemble(prog.Functions.Single());

        assembled.Words.Select(w => (int)w).Should().Equal(HalWords);
    }

    [Fact]
    public void TheLoopIsAtWordFiveAndEndsAtSixteen()
    {
        // pulse.py programs EXECCTRL with wrap_bottom 5 (count_h) and wrap_top 16, the final jmp.
        HalWords[5].Should().Be(0xA02B);       // mov x, ~null: the head of the loop
        HalWords[16].Should().Be(0x0005);      // jmp 5
    }
}

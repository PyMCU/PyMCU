using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/break-edges.
///
/// Exercises asm("BREAK") as a hardware checkpoint: the firmware stores results
/// in the ATmega328P general-purpose I/O registers (GPIOR0/1/2) and drives PORTB,
/// then halts at each BREAK. Tests use RunToBreak() to stop at each checkpoint
/// and inspect the simulator state directly — no UART required.
///
/// Data-space addresses used:
///   GPIOR0 = 0x3E   GPIOR1 = 0x4A   GPIOR2 = 0x4B   PORTB = 0x25
///
/// Checkpoints:
///   1 — GPIO PB5 high + GPIOR0 = 0xAB
///   2 — uint8 overflow: 200 + 100 → 44 (wraps mod 256)
///   3 — while/break: loop exits when i == 5
///   4 — @inline clamp early return: clamp(200, 10, 100) = 100
///   5 — uint16 byte extraction: 0x1234 → lo=52, hi=18
/// </summary>
[TestFixture]
public class BreakEdgesTests
{
    private SimSession _session = null!;

    // ATmega328P data-space addresses
    private const int GPIOR0_ADDR = 0x3E;
    private const int GPIOR1_ADDR = 0x4A;
    private const int GPIOR2_ADDR = 0x4B;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("break-edges"));

    /// <summary>Advances the simulation through N BREAK checkpoints.</summary>
    private static void SkipBreaks(ArduinoUnoSimulation uno, int count)
    {
        for (var i = 0; i < count; i++)
        {
            uno.RunToBreak();
            uno.RunInstructions(1); // step over the BREAK opcode
        }
    }

    private ArduinoUnoSimulation Boot() => _session.Reset();

    [Test]
    public void Break1_PinHigh_Gpior0HasMagicByte()
    {
        // Checkpoint 1: DDRB[5]=1, PORTB[5]=1, GPIOR0=0xAB
        var uno = Boot();
        uno.RunToBreak();
        uno.PortB.Should().HavePinHigh(5, "PB5 should be driven high at checkpoint 1");
        uno.Data[GPIOR0_ADDR].Should().Be(0xAB, "GPIOR0 must hold the magic byte 0xAB written by firmware");
    }

    [Test]
    public void Break2_Uint8_Overflow_200Plus100_Wraps()
    {
        // Checkpoint 2: c = (uint8)(200 + 100) = (uint8)300 = 44 (0x2C)
        var uno = Boot();
        SkipBreaks(uno, 1);          // past checkpoint 1
        uno.RunToBreak();            // arrive at checkpoint 2
        // 200 + 100 = 300 = 0x12C; low byte = 0x2C = 44
        uno.Data[GPIOR0_ADDR].Should().Be(44,
            "uint8 addition 200+100=300 must wrap to 44 (0x2C) modulo 256");
        uno.PortB.Should().HavePinLow(5, "PB5 cleared before checkpoint 2");
    }

    [Test]
    public void Break3_WhileBreak_LoopExitsAt5()
    {
        // Checkpoint 3: 'while True: if i==5: break; i+=1' → i=5
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(5,
            "loop must exit when i reaches 5 (break statement), not continue");
    }

    [Test]
    public void Break4_InlineClamp_EarlyReturn_ReturnsHi()
    {
        // Checkpoint 4: clamp(200, lo=10, hi=100) — 200 > 100 → early return 100
        var uno = Boot();
        SkipBreaks(uno, 3);
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(100,
            "@inline clamp with early return must clamp 200 to hi=100");
    }

    [Test]
    public void Break5_Uint16_ByteExtraction_Correct()
    {
        // Checkpoint 5: w=0x1234 → lo_byte=0x34=52, hi_byte=0x12=18
        var uno = Boot();
        SkipBreaks(uno, 4);
        uno.RunToBreak();
        uno.Data[GPIOR1_ADDR].Should().Be(52,
            "low byte of 0x1234 is 0x34 = 52; stored in GPIOR1");
        uno.Data[GPIOR2_ADDR].Should().Be(18,
            "high byte of 0x1234 is 0x12 = 18; stored in GPIOR2");
    }
}

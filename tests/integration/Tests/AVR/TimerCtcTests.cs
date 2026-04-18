using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/timer-ctc.
/// Timer1 CTC mode with prescaler 256 and compare value 62499 fires exactly
/// once per second (1.0 s = (62499+1) * 256 / 16 MHz).
/// TIMER1_COMPA vector: byte 0x0016 / word 0x000B.
/// Verifies: CTC mode bits set (WGM12 in TCCR1B), OCIE1A set, ISR fires ~1 Hz,
///           UART reports "CTC\n" on boot and "C\n" on each compare match.
/// </summary>
[TestFixture]
public class TimerCtcTests
{
    private SimSession _session = null!;

    // ATmega328P memory-mapped addresses
    private const int TCCR1A = 0x80;
    private const int TCCR1B = 0x81;
    private const int TIMSK1 = 0x6F;
    private const int OCR1AL = 0x88;
    private const int OCR1AH = 0x89;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.Build("timer-ctc"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CTC");
        uno.Serial.Should().ContainLine("CTC");
    }

    [Test]
    public void Init_CtcModeSet_Tccr1b()
    {
        // WGM12 = bit 3 of TCCR1B must be 1 for CTC mode
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CTC");
        uno.RunMilliseconds(10);
        var tccr1b = uno.Data[TCCR1B];
        (tccr1b & 0x08).Should().Be(0x08, "WGM12 (bit 3 of TCCR1B) must be set for CTC mode");
    }

    [Test]
    public void Init_Ocie1a_Enabled()
    {
        // OCIE1A = bit 1 of TIMSK1 must be 1 for compare match A interrupt
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CTC");
        uno.RunMilliseconds(10);
        var timsk1 = uno.Data[TIMSK1];
        (timsk1 & 0x02).Should().Be(0x02, "OCIE1A (bit 1 of TIMSK1) must be enabled");
    }

    [Test]
    public void Init_Ocr1a_Set_62499()
    {
        // OCR1A should be 62499 = 0xF423
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CTC");
        uno.RunMilliseconds(10);
        var lo = uno.Data[OCR1AL];
        var hi = uno.Data[OCR1AH];
        var ocr1a = lo | (hi << 8);
        ocr1a.Should().Be(62499, "OCR1A should be 62499 for 1 Hz at 16 MHz / prescaler 256");
    }

    [Test]
    public void After1Second_CompareSent()
    {
        // Timer1 CTC fires at 1 Hz; a 'C' byte after the banner '\n' arrives within 1.2 s.
        // If WGM12 or OCIE1A is wrong, the ISR never fires and no additional 'C' arrives.
        // Banner "CTC\n" has 2 C's; after the first tick total C count = 3.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CTC\n");
        uno.RunUntilSerial(uno.Serial, s => s.Count(c => c == 'C') >= 3, maxMs: 1300);
        uno.Serial.Text.Count(c => c == 'C').Should().BeGreaterThanOrEqualTo(3, "Timer1 COMPA ISR must fire within ~1 s");
    }

    [Test]
    public void After2Seconds_TwoComparesSent()
    {
        // Banner has 2 C's; each tick adds 1 C. After 2 ticks total = 4.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CTC\n");
        uno.RunUntilSerial(uno.Serial, s => s.Count(c => c == 'C') >= 4, maxMs: 2600);
        var cCount = uno.Serial.Text.Count(c => c == 'C');
        cCount.Should().BeGreaterThanOrEqualTo(4, "two compare match events fire within 2.5 s (banner=2C + 2 ticks)");
    }

    [Test]
    public void After1Second_LedHasToggled()
    {
        // Wait for banner then wait for a full extra 'C' byte (3 total) before checking LED.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CTC\n");
        var ledBefore = uno.PortB.GetPinState(5);
        uno.RunUntilSerial(uno.Serial, s => s.Count(c => c == 'C') >= 3, maxMs: 1300);
        var ledAfter = uno.PortB.GetPinState(5);
        ledAfter.Should().NotBe(ledBefore, "LED on PB5 must toggle after first CTC compare match");
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}

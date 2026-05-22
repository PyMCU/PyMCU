using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-mp-pwm-freq.
/// Verifies that machine.PWM(pin, freq=...) selects the correct Timer0
/// prescaler via CS bits in TCCR0B, and that pwm.freq() updates it.
///
/// Fixture: PWM("PD6", freq=1000).init() → CS=0x02 (prescaler/8)
///          then pwm.freq(100)          → CS=0x04 (prescaler/256)
/// After setup the firmware sends 0x46 ('F') via machine.UART.
/// </summary>
[TestFixture]
public class CompatMpPwmFreqTests
{
    private SimSession _session = null!;

    // ATmega328P data-space addresses (I/O addr + 0x20)
    private const int TCCR0A = 0x44;
    private const int TCCR0B = 0x45;
    private const int OCR0A  = 0x47;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("compat-mp-pwm-freq"));

    [Test]
    public void Boot_SendsDoneMarker()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 1, maxMs: 50);
        uno.Serial.Bytes[0].Should().Be(0x46, "'F' done marker sent after PWM freq setup");
    }

    [Test]
    public void PwmMode_FastPwm_Configured()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 1, maxMs: 50);
        var tccr0a = uno.Data[TCCR0A];
        (tccr0a & 0x03).Should().Be(0x03, "WGM01|WGM00 bits select Fast PWM mode");
    }

    [Test]
    public void PwmMode_NonInverted_Configured()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 1, maxMs: 50);
        var tccr0a = uno.Data[TCCR0A];
        (tccr0a & 0x80).Should().Be(0x80, "COM0A1 bit enables non-inverted PWM on OC0A");
    }

    [Test]
    public void FreqMethod_SelectsPrescalerDiv256_CS0x04()
    {
        // After pwm.freq(100): freq > 61 and <= 244 => CS bits = 0x04 (prescaler /256)
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 1, maxMs: 50);
        var tccr0b = uno.Data[TCCR0B];
        (tccr0b & 0x07).Should().Be(0x04, "CS bits == 0x04 after freq(100): prescaler /256 = 244 Hz");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim() => _session.Reset();
}

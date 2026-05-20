using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-mp-pwm.
/// Verifies that machine.PWM from the pymcu-micropython compat layer
/// correctly configures Timer0 Fast PWM on PD6 (OC0A) and sets OCR0A.
///
/// Fixture: PWM("PD6").init() + duty(128)
///   -> OCR0A = 128 (50% duty cycle)
///   -> TCCR0A has Fast PWM + COM0A1 bits set
/// After setup the firmware sends 0x44 ('D') via machine.UART.
/// </summary>
[TestFixture]
public class CompatMpPwmTests
{
    private SimSession _session = null!;

    // ATmega328P data-space addresses (I/O addr + 0x20)
    private const int TCCR0A = 0x44;
    private const int OCR0A  = 0x47;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("compat-mp-pwm"));

    [Test]
    public void Boot_SendsDoneMarker()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 1, maxMs: 50);
        uno.Serial.Bytes[0].Should().Be(0x44, "'D' done marker sent after PWM setup");
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
    public void DutyCycle_50Percent_OCR0A_Is128()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 1, maxMs: 50);
        uno.Data[OCR0A].Should().Be(128, "OCR0A = 128 after duty(128)");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim() => _session.Reset();
}

using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests;

/// <summary>
/// Integration tests for examples/avr/pwm-fade.
/// Timer0 Fast PWM on PD6 (OC0A). OCR0A sweeps 0→255→0 with 5ms per step.
/// TCCR0A = 0x83 (WGM01|WGM00|COM0A1 = Fast PWM, non-inverted).
/// </summary>
[TestFixture]
public class PwmFadeTests
{
    private string _hex = null!;
    // ATmega328P data-space addresses (I/O addr + 0x20)
    private const int TCCR0A = 0x44;
    private const int OCR0A  = 0x47;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("pwm-fade");

    [Test]
    public void PwmMode_Configured_FastPwm()
    {
        var uno = Sim();
        uno.RunMilliseconds(10); // init
        // TCCR0A WGM01=1,WGM00=1 (bits 1:0) → Fast PWM; COM0A1=1 (bit 7) → non-inverted
        var tccr0a = uno.Data[TCCR0A];
        (tccr0a & 0x03).Should().Be(0x03, "WGM01|WGM00 = Fast PWM mode");
        (tccr0a & 0x80).Should().Be(0x80, "COM0A1 = non-inverted PWM output");
    }

    [Test]
    public void DutyCycle_IncreasesOverTime()
    {
        var uno = Sim();
        uno.RunMilliseconds(50); // ~10 increments
        var duty50 = uno.Data[OCR0A];
        uno.RunMilliseconds(200); // ~40 more increments
        var duty250 = uno.Data[OCR0A];
        duty250.Should().BeGreaterThan(duty50, "OCR0A increases during fade-in");
    }

    [Test]
    public void DutyCycle_ReachesMax_255()
    {
        var uno = Sim();
        // 255 increments × 5ms = 1275ms; add margin
        uno.RunUntilMs(_ => uno.Data[OCR0A] == 255, maxMs: 1400);
        uno.Data[OCR0A].Should().Be(255);
    }

    [Test]
    public void DutyCycle_DecreasesAfterMax()
    {
        var uno = Sim();
        uno.RunUntilMs(_ => uno.Data[OCR0A] == 255, maxMs: 1400);
        uno.RunMilliseconds(200); // some fade-out steps
        uno.Data[OCR0A].Should().BeLessThan(255, "OCR0A decreases during fade-out");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}

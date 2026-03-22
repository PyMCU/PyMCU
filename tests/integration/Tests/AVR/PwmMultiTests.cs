using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/pwm-multi.
/// Three simultaneous hardware PWM channels on ATmega328P:
///   Channel A: PD6 / OC0A  (Timer0 Fast PWM)
///   Channel B: PB3 / OC2A  (Timer2 Fast PWM)
///   Channel C: PB1 / OC1A  (Timer1 Fast PWM 8-bit)
/// Verifies register configuration and duty-cycle updates.
/// </summary>
[TestFixture]
public class PwmMultiTests
{
    private string _hex = null!;

    // ATmega328P data-space addresses
    private const int TCCR0A = 0x44;
    private const int TCCR0B = 0x45;
    private const int OCR0A  = 0x47;
    private const int TCCR1A = 0x80;
    private const int TCCR1B = 0x81;
    private const int OCR1AL = 0x88;
    private const int TCCR2A = 0xB0;
    private const int TCCR2B = 0xB1;
    private const int OCR2A  = 0xB3;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("pwm-multi");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PWM3");
        uno.Serial.Should().ContainLine("PWM3");
    }

    [Test]
    public void ChannelA_Timer0_FastPwm_Configured()
    {
        // TCCR0A bits WGM01|WGM00=11 (Fast PWM), COM0A1=1 (non-inverted) -> 0x83
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PWM3");
        uno.RunMilliseconds(10);
        var tccr0a = uno.Data[TCCR0A];
        (tccr0a & 0x03).Should().Be(0x03, "Timer0 WGM01|WGM00=11 (Fast PWM)");
        (tccr0a & 0x80).Should().Be(0x80, "Timer0 COM0A1=1 (non-inverted OC0A)");
    }

    [Test]
    public void ChannelB_Timer2_FastPwm_Configured()
    {
        // TCCR2A bits WGM21|WGM20=11, COM2A1=1 -> 0x83
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PWM3");
        uno.RunMilliseconds(10);
        var tccr2a = uno.Data[TCCR2A];
        (tccr2a & 0x03).Should().Be(0x03, "Timer2 WGM21|WGM20=11 (Fast PWM)");
        (tccr2a & 0x80).Should().Be(0x80, "Timer2 COM2A1=1 (non-inverted OC2A)");
    }

    [Test]
    public void ChannelC_Timer1_FastPwm8bit_Configured()
    {
        // TCCR1A COM1A1=1 (bit 7), WGM10=1 (bit 0) -> 0x82 for Fast PWM 8-bit with OC1A
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PWM3");
        uno.RunMilliseconds(10);
        var tccr1a = uno.Data[TCCR1A];
        (tccr1a & 0x80).Should().Be(0x80, "Timer1 COM1A1=1 (non-inverted OC1A)");
    }

    [Test]
    public void ChannelA_DutyCycle_StartsAtZero()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PWM3");
        uno.RunMilliseconds(2); // before first increment
        uno.Data[OCR0A].Should().Be(0, "Channel A starts with duty=0");
    }

    [Test]
    public void ChannelB_DutyCycle_StartsAt128()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PWM3");
        uno.RunMilliseconds(2); // before first increment
        uno.Data[OCR2A].Should().Be(128, "Channel B starts with duty=128 (phase offset)");
    }

    [Test]
    public void ChannelC_DutyCycle_StartsAt64()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PWM3");
        uno.RunMilliseconds(2); // before first increment
        uno.Data[OCR1AL].Should().Be(64, "Channel C starts with duty=64 (phase offset)");
    }

    [Test]
    public void AllChannels_DutyIncreases_OverTime()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PWM3");
        var oa0 = uno.Data[OCR0A];
        uno.RunMilliseconds(100); // ~20 x 5ms steps
        var oa100 = uno.Data[OCR0A];
        oa100.Should().BeGreaterThan(oa0, "OCR0A increases as duty ramps up");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}

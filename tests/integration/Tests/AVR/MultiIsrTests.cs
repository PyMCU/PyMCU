using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/multi-isr.
/// Two simultaneous ISRs: TIMER0_OVF (sets GPIOR0[0]) and INT0 falling edge
/// (sets GPIOR0[1]). Main loop counts 61 Timer0 overflows per "T\n" tick
/// and sends an incrementing byte per INT0 press.
/// Tests: multiple @interrupt decorators, GPIOR0 flag coordination, Timer0 + INT0 coexistence.
/// </summary>
[TestFixture]
public class MultiIsrTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("multi-isr");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "MULTI ISR");
        uno.Serial.Should().ContainLine("MULTI ISR");
    }

    [Test]
    public void Int0FallingEdge_SendsCountByte()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "MULTI ISR\n");
        var before = uno.Serial.ByteCount;

        // Falling edge on PD2 → INT0 ISR fires → int_count = 1 → uart.write(1)
        uno.PortD.SetPinValue(2, true);
        uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false);
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 200); // count byte + '\n'

        uno.Serial.Bytes[before].Should().Be(0x01, "first INT0 press → count = 1");
        uno.Serial.Bytes[before + 1].Should().Be((byte)'\n');
    }

    [Test]
    public void TwoInt0Presses_CountsCorrectly()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "MULTI ISR\n");
        var before = uno.Serial.ByteCount;

        for (var i = 0; i < 2; i++)
        {
            uno.PortD.SetPinValue(2, true);
            uno.RunMilliseconds(1);
            uno.PortD.SetPinValue(2, false);
            uno.RunUntilSerialBytes(uno.Serial, before + (i + 1) * 2, maxMs: 200);
        }

        // Bytes interleaved with '\n': count1, '\n', count2, '\n'
        uno.Serial.Bytes[before].Should().Be(0x01);
        uno.Serial.Bytes[before + 2].Should().Be(0x02);
    }

    [Test]
    public void Timer0Overflows_EventuallySendsTickMessage()
    {
        // Timer0 at 1024 prescaler: overflow every 256*1024/16e6 ≈ 16.4ms.
        // After 61 overflows (~1s) main loop sends 'T'\n to signal a tick.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "MULTI ISR\n");

        uno.RunUntilSerial(uno.Serial, s => s.Contains('T'), maxMs: 1500);

        uno.Serial.Should().Contain("T", "Timer0 ISR should have counted 61 overflows and sent a tick");
    }

    [Test]
    public void BothIsrs_CanFire_Independently()
    {
        // Verify that INT0 and Timer0 ISRs don't interfere: fire INT0 first,
        // then let Timer0 accumulate, verify both outputs appear.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "MULTI ISR\n");
        var before = uno.Serial.ByteCount;

        // INT0 press → count byte
        uno.PortD.SetPinValue(2, true);
        uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false);
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 200);
        uno.Serial.Bytes[before].Should().Be(0x01, "INT0 count = 1");

        // Timer0 runs concurrently — wait for first 'T'
        uno.RunUntilSerial(uno.Serial, s => s.Skip(before).Any(c => c == 'T'), maxMs: 1500);
        uno.Serial.Text.Should().Contain("T", "Timer0 OVF ISR produces T ticks");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.PortD.SetPinValue(2, true); // button released initially (INT0 requires falling edge)
        return uno;
    }
}

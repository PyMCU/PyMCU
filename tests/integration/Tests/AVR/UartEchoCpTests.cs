using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/uart-echo-cp.
/// CircuitPython-style UART echo: sends "READY\n" on boot, then reads one
/// byte at a time, blinks the built-in LED, and echoes the byte back.
/// Adapted from the Adafruit CircuitPython Essentials UART Serial example.
/// </summary>
[TestFixture]
public class UartEchoCpTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.Build("uart-echo-cp"));

    [Test]
    public void Boot_SendsReadyBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY");
        uno.Serial.Should().ContainLine("READY");
    }

    [Test]
    public void Echo_SingleByte()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n");
        var beforeCount = uno.Serial.ByteCount;
        uno.Serial.InjectByte(0x41); // 'A'
        uno.RunUntilSerialBytes(uno.Serial, beforeCount + 1, maxMs: 100);
        uno.Serial.Bytes.Last().Should().Be(0x41);
    }

    [Test]
    public void Echo_MultipleBytes()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n");
        var beforeCount = uno.Serial.ByteCount;
        uno.Serial.InjectByte(0x48); // 'H'
        uno.RunUntilSerialBytes(uno.Serial, beforeCount + 1, maxMs: 100);
        uno.Serial.InjectByte(0x69); // 'i'
        uno.RunUntilSerialBytes(uno.Serial, beforeCount + 2, maxMs: 100);
        var echoed = uno.Serial.Bytes.Skip(beforeCount).Take(2).ToArray();
        echoed.Should().Equal([0x48, 0x69]);
    }

    [Test]
    public void Echo_NullByte()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n");
        var beforeCount = uno.Serial.ByteCount;
        uno.Serial.InjectByte(0x00);
        uno.RunUntilSerialBytes(uno.Serial, beforeCount + 1, maxMs: 100);
        uno.Serial.Bytes.Last().Should().Be(0x00);
    }

    [Test]
    public void Led_LowAtBootBeforeFirstByte()
    {
        // After init the LED direction is OUTPUT (DDRB5=1) but value is LOW.
        // The LED only pulses HIGH during a byte echo; at boot it stays LOW.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n");
        uno.PortB.Should().HavePinLow(5); // D13 = PB5
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}

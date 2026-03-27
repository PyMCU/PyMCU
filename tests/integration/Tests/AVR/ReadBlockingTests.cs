using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/read-blocking.
/// Exercises UART.read_blocking(): blocking receive that polls until a byte arrives.
/// Boot banner: "RB\n". Then echoes every received byte unchanged.
/// </summary>
[TestFixture]
public class ReadBlockingTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("read-blocking");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RB\n", maxMs: 100);
        uno.Serial.Text.Should().Contain("RB");
    }

    [Test]
    public void ReadBlocking_EchoesReceivedByte()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RB\n", maxMs: 100);
        var before = uno.Serial.ByteCount;
        uno.Serial.InjectByte(0x41); // 'A'
        uno.RunUntilSerialBytes(uno.Serial, before + 1, maxMs: 200);
        uno.Serial.Bytes.Last().Should().Be(0x41, "read_blocking should echo the received byte");
    }

    [Test]
    public void ReadBlocking_EchoesMultipleBytes()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RB\n", maxMs: 100);
        var before = uno.Serial.ByteCount;
        uno.Serial.InjectByte(0x48); // 'H'
        uno.RunUntilSerialBytes(uno.Serial, before + 1, maxMs: 200);
        uno.Serial.InjectByte(0x69); // 'i'
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 200);
        var echoed = uno.Serial.Bytes.Skip(before).Take(2).ToArray();
        echoed.Should().Equal([0x48, 0x69], "read_blocking should echo all bytes in order");
    }

    [Test]
    public void ReadBlocking_EchoesNullByte()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RB\n", maxMs: 100);
        var before = uno.Serial.ByteCount;
        uno.Serial.InjectByte(0x00);
        uno.RunUntilSerialBytes(uno.Serial, before + 1, maxMs: 200);
        uno.Serial.Bytes.Last().Should().Be(0x00, "read_blocking should echo 0x00 unchanged");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}

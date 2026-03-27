using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/uart-echo.
/// Boot banner: "ECHO\n" (bytes 69,67,72,79,10).
/// Then echoes every received byte back unchanged.
/// </summary>
[TestFixture]
public class UartEchoTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("uart-echo");

    [Test]
    public void Boot_SendsEchoBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "ECHO");
        uno.Serial.Should().ContainLine("ECHO");
    }

    [Test]
    public void Echo_SingleByte()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "ECHO\n");
        var beforeCount = uno.Serial.ByteCount;
        uno.Serial.InjectByte(0x41); // 'A'
        uno.RunUntilSerialBytes(uno.Serial, beforeCount + 1, maxMs: 100);
        uno.Serial.Bytes.Last().Should().Be(0x41);
    }

    [Test]
    public void Echo_MultipleBytes()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "ECHO\n");
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
        uno.RunUntilSerial(uno.Serial, "ECHO\n");
        var beforeCount = uno.Serial.ByteCount;
        uno.Serial.InjectByte(0x00);
        uno.RunUntilSerialBytes(uno.Serial, beforeCount + 1, maxMs: 100);
        uno.Serial.Bytes.Last().Should().Be(0x00);
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}

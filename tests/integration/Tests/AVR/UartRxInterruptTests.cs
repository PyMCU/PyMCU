using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/uart-rx-interrupt.
/// USART RX interrupt (vector 0x0024) fills a 16-byte ring buffer.
/// Main loop reads with rx_available() / rx_read() and echoes back.
/// Boot banner: "RXIRQ\n".
/// </summary>
[TestFixture]
public class UartRxInterruptTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("uart-rx-interrupt");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RXIRQ\n", maxMs: 200);
        uno.Serial.Should().ContainLine("RXIRQ");
    }

    [Test]
    public void Echo_SingleByte()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RXIRQ\n", maxMs: 200);
        var before = uno.Serial.ByteCount;

        uno.Serial.InjectByte(0x41); // 'A'
        uno.RunUntilSerialBytes(uno.Serial, before + 1, maxMs: 200);

        uno.Serial.Bytes.Skip(before).First().Should().Be(0x41, "received byte should be echoed");
    }

    [Test]
    public void Echo_MultipleBytes()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RXIRQ\n", maxMs: 200);
        var before = uno.Serial.ByteCount;

        uno.Serial.InjectByte(0x48); // 'H'
        uno.RunUntilSerialBytes(uno.Serial, before + 1, maxMs: 200);
        uno.Serial.InjectByte(0x69); // 'i'
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 200);

        var echoed = uno.Serial.Bytes.Skip(before).Take(2).ToArray();
        echoed.Should().Equal([0x48, 0x69], "both bytes should be echoed in order");
    }

    [Test]
    public void AfterBoot_EchoesBack()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RXIRQ\n", maxMs: 200);
        var before = uno.Serial.ByteCount;

        uno.Serial.InjectByte(0x58); // 'X'
        uno.RunUntilSerialBytes(uno.Serial, before + 1, maxMs: 200);

        uno.Serial.Bytes.Last().Should().Be(0x58, "injected byte should be echoed");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}

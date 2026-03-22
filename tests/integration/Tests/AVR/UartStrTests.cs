using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/uart-str.
/// Tests flash-string-pool (write_str/println) and single-char literals.
/// Expected startup output: "Hello, Whipsnake!\nUART string support works!\nTest\n"
/// </summary>
[TestFixture]
public class UartStrTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("uart-str");

    [Test]
    public void Boot_SendsHelloWhipsnake()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "Hello, Whipsnake!");
        uno.Serial.Should().ContainLine("Hello, Whipsnake!");
    }

    [Test]
    public void Boot_SendsUartStrLine()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "UART string support works!");
        uno.Serial.Should().ContainLine("UART string support works!");
    }

    [Test]
    public void Boot_SendsTestViaCharLiterals()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "Test");
        uno.Serial.Should().ContainLine("Test");
    }

    [Test]
    public void Boot_AllThreeMessagesPresent()
    {
        var uno = Sim();
        // All three messages appear before the echo loop
        uno.RunUntilSerial(uno.Serial, "Test\n", maxMs: 200);
        uno.Serial.Should().Contain("Hello, Whipsnake!");
        uno.Serial.Should().Contain("UART string support works!");
        uno.Serial.Should().Contain("Test");
    }

    [Test]
    public void AfterBoot_EchoesBack()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "Test\n");
        var before = uno.Serial.ByteCount;
        uno.Serial.InjectByte(0x5A); // 'Z'
        uno.RunUntilSerialBytes(uno.Serial, before + 1, maxMs: 100);
        uno.Serial.Bytes.Last().Should().Be(0x5A);
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}

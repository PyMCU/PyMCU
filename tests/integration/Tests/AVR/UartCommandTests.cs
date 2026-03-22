using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/uart-command.
/// Interprets single-byte UART commands: B=Blink, H=High, L=Low, T=Toggle, S=Status, ?=Help.
/// </summary>
[TestFixture]
public class UartCommandTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("uart-command");

    [Test]
    public void Boot_SendsReadyBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "UART CMD READY");
        uno.Serial.Should().ContainLine("UART CMD READY");
    }

    [Test]
    public void CommandH_TurnsLedOn()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "UART CMD READY");
        uno.Serial.InjectByte((byte)'H');
        uno.RunUntilSerial(uno.Serial, "LED ON", maxMs: 500);
        uno.Serial.Should().ContainLine("LED ON");
        uno.PortB.Should().HavePinHigh(5);
    }

    [Test]
    public void CommandL_TurnsLedOff()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "UART CMD READY");
        uno.Serial.InjectByte((byte)'H'); // on first
        uno.RunUntilSerial(uno.Serial, "LED ON", maxMs: 500);
        uno.Serial.InjectByte((byte)'L');
        uno.RunUntilSerial(uno.Serial, "LED OFF", maxMs: 500);
        uno.Serial.Should().ContainLine("LED OFF");
        uno.PortB.Should().HavePinLow(5);
    }

    [Test]
    public void CommandT_ToggesLed()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "UART CMD READY");
        uno.Serial.InjectByte((byte)'H'); // LED on
        uno.RunUntilSerial(uno.Serial, "LED ON", maxMs: 500);
        var ledOn = uno.PortB.GetPinState(5);
        uno.Serial.InjectByte((byte)'T'); // toggle
        uno.RunUntilSerial(uno.Serial, "LED OFF", maxMs: 500);
        var ledOff = uno.PortB.GetPinState(5);
        ledOff.Should().NotBe(ledOn);
    }

    [Test]
    public void CommandHelp_SendsHelpText()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "UART CMD READY");
        uno.Serial.InjectByte((byte)'?');
        uno.RunUntilSerial(uno.Serial, "B=Blink", maxMs: 500);
        uno.Serial.Should().Contain("B=Blink");
    }

    [Test]
    public void UnknownCommand_EchoesWithQuestionMark()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "UART CMD READY");
        uno.Serial.InjectByte((byte)'X'); // unknown
        uno.RunUntilSerial(uno.Serial, "?X", maxMs: 500);
        uno.Serial.Should().Contain("?X");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}

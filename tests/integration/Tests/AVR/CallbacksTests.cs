using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/callbacks.
/// Boot: "CALLBACKS\n". Then repeated passes of 10 bytes:
///   DOUBLE  pass: 0x00, 0,2,4,6,8,10,12,14, 0x0A
///   INVERT  pass: 0x01, 255,254,253,252,251,250,249,248, 0x0A
///   SHIFT_L pass: 0x02, 0,2,4,6,8,10,12,14, 0x0A
/// </summary>
[TestFixture]
public class CallbacksTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("callbacks");

    [Test]
    public void Boot_SendsCallbacksBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CALLBACKS");
        uno.Serial.Should().ContainLine("CALLBACKS");
    }

    [Test]
    public void FirstPass_IsDoubleCallback()
    {
        var uno = Sim();
        // "CALLBACKS\n" = 10 bytes, then first pass = 10 bytes
        uno.RunUntilSerialBytes(uno.Serial, 20, maxMs: 200);
        var passBytes = uno.Serial.Bytes.Skip(10).Take(10).ToArray();
        // header = 0x00 (DOUBLE), then 0,2,4,6,8,10,12,14, newline
        passBytes[0].Should().Be(0x00, "first pass uses DOUBLE callback (CB.DOUBLE = 0)");
        passBytes[1].Should().Be(0,  "double(0) = 0");
        passBytes[2].Should().Be(2,  "double(1) = 2");
        passBytes[3].Should().Be(4,  "double(2) = 4");
        passBytes[9].Should().Be(10, "newline separator");
    }

    [Test]
    public void SecondPass_IsInvertCallback()
    {
        var uno = Sim();
        // "CALLBACKS\n" + pass1 + pass2 = 10 + 10 + 10 bytes
        uno.RunUntilSerialBytes(uno.Serial, 30, maxMs: 300);
        var passBytes = uno.Serial.Bytes.Skip(20).Take(10).ToArray();
        passBytes[0].Should().Be(0x01, "second pass uses INVERT callback (CB.INVERT = 1)");
        passBytes[1].Should().Be(255, "invert(0) = 0xFF ^ 0 = 255");
        passBytes[2].Should().Be(254, "invert(1) = 0xFF ^ 1 = 254");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}

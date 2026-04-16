using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/overflow-behavior.
///
/// Verifies that integer overflow and underflow wrap correctly for all
/// signed and unsigned integer types, matching C/avr-gcc semantics.
///
/// Test cases:
///   - uint8: 255 + 1 = 0
///   - uint8: 0 - 1 = 255
///   - int8: 127 + 1 = -128 (0x80)
///   - int8: -128 - 1 = 127 (0x7F)
///   - uint16: 65535 + 1 = 0
///   - int16: -32768 - 1 = 32767
///
/// Data-space addresses (ATmega328P):
///   GPIOR0=0x3E, GPIOR1=0x4A, GPIOR2=0x4B, OCR0A=0x47, OCR0B=0x48, OCR1AH=0x89
/// </summary>
[TestFixture]
public class OverflowBehaviorTests
{
    private const int Gpior0 = 0x3E;
    private const int Gpior1 = 0x4A;
    private const int Gpior2 = 0x4B;
    private const int Ocr0A = 0x47;
    private const int Ocr0B = 0x48;
    private const int Ocr1AH = 0x89;

    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("overflow-behavior");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunToBreak();
        return uno;
    }

    [Test]
    public void Uint8_Overflow_255Plus1_Wraps_To0()
    {
        Boot().Data[Gpior0].Should().Be(0,
            "uint8: 255 + 1 should wrap to 0");
    }

    [Test]
    public void Uint8_Underflow_0Minus1_Wraps_To255()
    {
        Boot().Data[Gpior1].Should().Be(0xFF,
            "uint8: 0 - 1 should wrap to 255 (0xFF)");
    }

    [Test]
    public void Int8_Overflow_127Plus1_Wraps_ToNeg128()
    {
        Boot().Data[Gpior2].Should().Be(0x80,
            "int8: 127 + 1 should overflow to -128 (0x80 in two's complement)");
    }

    [Test]
    public void Int8_Underflow_Neg128Minus1_Wraps_To127()
    {
        Boot().Data[Ocr0A].Should().Be(0x7F,
            "int8: -128 - 1 should underflow to 127 (0x7F)");
    }

    [Test]
    public void Uint16_Overflow_65535Plus1_Wraps_To0()
    {
        Boot().Data[Ocr0B].Should().Be(0,
            "uint16: 65535 + 1 should wrap to 0 (high byte = 0)");
    }

    [Test]
    public void Int16_Underflow_Neg32768Minus1_Wraps_To32767()
    {
        Boot().Data[Ocr1AH].Should().Be(0x7F,
            "int16: -32768 - 1 should underflow to 32767 (high byte = 0x7F)");
    }
}


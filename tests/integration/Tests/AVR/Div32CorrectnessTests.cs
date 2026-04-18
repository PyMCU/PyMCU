using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/div32-correctness.
///
/// Verifies that uint32 division and modulo call __div32 / __mod32 instead
/// of the old __div8 / __mod8 which silently truncated the dividend to 8 bits.
///
/// __div32 ABI (matches the PyMCU 32-bit register layout):
///   Input:  R23:R22:R25:R24 (Dividend, MSB..LSB), R21:R20:R19:R18 (Divisor)
///   Output: R23:R22:R25:R24 (Quotient)
///
/// __mod32 ABI:
///   Input:  R23:R22:R25:R24 (Dividend), R21:R20:R19:R18 (Divisor)
///   Output: R23:R22:R25:R24 (Remainder)
///
/// Test cases (all operands > 65535 to confirm 32-bit range is used):
///   100000 / 1000  = 100
///   100000 % 1000  = 0
///   1000000 / 300  = 3333   (0x0D05)
///   1000000 % 300  = 100
///
/// Data-space addresses (ATmega328P):
///   GPIOR0 = 0x3E   GPIOR1 = 0x4A   GPIOR2 = 0x4B
///   OCR0A  = 0x47   OCR0B  = 0x48   OCR1AL = 0x88
/// </summary>
[TestFixture]
public class Div32CorrectnessTests
{
    private const int Gpior0 = 0x3E;
    private const int Gpior1 = 0x4A;
    private const int Gpior2 = 0x4B;
    private const int Ocr0A  = 0x47;
    private const int Ocr0B  = 0x48;
    private const int Ocr1AL = 0x88;

    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("div32-correctness");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunToBreak();
        return uno;
    }

    // --- 100000 / 1000 = 100 ------------------------------------------------------

    [Test]
    public void Div100000By1000_Quotient_LowByte_Is100() =>
        Boot().Data[Gpior0].Should().Be(100,
            "100000 / 1000 quotient low byte must be 100");

    [Test]
    public void Div100000By1000_Quotient_HighByte_Is0() =>
        Boot().Data[Gpior1].Should().Be(0,
            "100000 / 1000 quotient high byte must be 0");

    // --- 100000 % 1000 = 0 --------------------------------------------------------

    [Test]
    public void Mod100000By1000_Is0() =>
        Boot().Data[Gpior2].Should().Be(0,
            "100000 % 1000 remainder must be 0");

    // --- 1000000 / 300 = 3333 (0x0D05) -------------------------------------------
    // 1000000 > 65535 so this test is only reachable with __div32 (not __div8/16).

    [Test]
    public void Div1000000By300_Quotient_LowByte_Is0x05() =>
        Boot().Data[Ocr0A].Should().Be(0x05,
            "1000000 / 300 = 3333 = 0x0D05; low byte must be 0x05");

    [Test]
    public void Div1000000By300_Quotient_HighByte_Is0x0D() =>
        Boot().Data[Ocr0B].Should().Be(0x0D,
            "1000000 / 300 = 3333 = 0x0D05; high byte must be 0x0D; " +
            "__div8 or __div16 would give wrong result because 1000000 > 65535");

    // --- 1000000 % 300 = 100 ------------------------------------------------------

    [Test]
    public void Mod1000000By300_Is100() =>
        Boot().Data[Ocr1AL].Should().Be(100,
            "1000000 % 300 remainder must be 100");
}

using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/mul16-signed.
///
/// Verifies that signed int16 multiplication uses MULSU for cross-terms so
/// that negative operands produce the correct two's-complement result.
///
/// The bug: unsigned MUL used for all partial products meant that when either
/// operand had a negative high byte (0xFF), the cross-terms produced the wrong
/// sign-extended partial product and the result was incorrect.
///
/// Checkpoint 1: (-1) * 1 = -1 = 0xFFFF
///   GPIOR0 = 0xFF (low), GPIOR1 = 0xFF (high)
///
/// Checkpoint 2: (-100) * 50 = -5000 = 0xEC78
///   GPIOR0 = 0x78 (low), GPIOR1 = 0xEC (high)
///
/// Checkpoint 3: 200 * (-3) = -600 = 0xFDA8
///   GPIOR0 = 0xA8 (low), GPIOR1 = 0xFD (high)
///
/// Data-space addresses (ATmega328P):
///   GPIOR0 = 0x3E   GPIOR1 = 0x4A
/// </summary>
[TestFixture]
public class Mul16SignedTests
{
    private const int Gpior0Addr = 0x3E;
    private const int Gpior1Addr = 0x4A;

    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("mul16-signed");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }

    // ── Checkpoint 1: int16(-1) * int16(1) = -1 = 0xFFFF ──────────────────

    [Test]
    public void Cp1_Neg1_Times_1_LowByte_Is0xFF()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0xFF,
            "(-1) * 1 = -1 = 0xFFFF; low byte must be 0xFF");
    }

    [Test]
    public void Cp1_Neg1_Times_1_HighByte_Is0xFF()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior1Addr].Should().Be(0xFF,
            "(-1) * 1 = -1 = 0xFFFF; high byte must be 0xFF (was 0 before MULSU fix)");
    }

    // ── Checkpoint 2: int16(-100) * int16(50) = -5000 = 0xEC78 ────────────

    [Test]
    public void Cp2_Neg100_Times_50_LowByte_Is0x78()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x78,
            "(-100) * 50 = -5000 = 0xEC78; low byte must be 0x78");
    }

    [Test]
    public void Cp2_Neg100_Times_50_HighByte_Is0xEC()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior1Addr].Should().Be(0xEC,
            "(-100) * 50 = -5000 = 0xEC78; high byte must be 0xEC");
    }

    // ── Checkpoint 3: int16(200) * int16(-3) = -600 = 0xFDA8 ──────────────

    [Test]
    public void Cp3_200_Times_Neg3_LowByte_Is0xA8()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0xA8,
            "200 * (-3) = -600 = 0xFDA8; low byte must be 0xA8");
    }

    [Test]
    public void Cp3_200_Times_Neg3_HighByte_Is0xFD()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior1Addr].Should().Be(0xFD,
            "200 * (-3) = -600 = 0xFDA8; high byte must be 0xFD");
    }
}

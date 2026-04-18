using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/mul16-correctness.
///
/// Verifies that uint16 multiplication preserves both bytes of the result.
/// The bug was: MUL emitted CLR R1 before MOV R25, R1, so the high byte of
/// a uint16 product was always 0.
///
/// Checkpoint 1: mul_u16(300, 200) = 60000 = 0xEA60
///   GPIOR0 = 0x60 (low byte), GPIOR1 = 0xEA (high byte)
///
/// Checkpoint 2: mul_u16(256, 256) = 65536 wraps to 0x0000
///   GPIOR0 = 0x00, GPIOR1 = 0x00
///
/// Data-space addresses (ATmega328P):
///   GPIOR0 = 0x3E   GPIOR1 = 0x4A
/// </summary>
[TestFixture]
public class Mul16CorrectnessTests
{
    private const int Gpior0Addr = 0x3E;
    private const int Gpior1Addr = 0x4A;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("mul16-correctness"));

    private ArduinoUnoSimulation Boot() => _session.Reset();

    [Test]
    public void Cp1_Mul300x200_LowByte_Is0x60()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x60,
            "300 * 200 = 60000 = 0xEA60; low byte must be 0x60");
    }

    [Test]
    public void Cp1_Mul300x200_HighByte_Is0xEA()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior1Addr].Should().Be(0xEA,
            "300 * 200 = 60000 = 0xEA60; high byte must be 0xEA (was 0 before fix)");
    }

    [Test]
    public void Cp2_Mul256x256_Wraps_To_Zero()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x00,
            "256 * 256 = 65536 wraps to 0; low byte must be 0x00");
        uno.Data[Gpior1Addr].Should().Be(0x00,
            "256 * 256 = 65536 wraps to 0; high byte must be 0x00");
    }
}


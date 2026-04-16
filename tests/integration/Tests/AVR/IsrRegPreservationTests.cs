using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/isr-reg-preservation.
///
/// Verifies that R24, R25, and R19 are saved and restored by the ISR
/// context save/restore sequence.
///
/// The bug was: EmitContextSave only pushed R16, R17, R18 and SREG.
/// The AVR codegen uses R24:R25 for all arithmetic results and R19 for
/// the high byte in 16-bit comparisons (CPC R25, R19). An ISR that
/// performs any 16-bit arithmetic would corrupt those registers in the
/// interrupted main() context.
///
/// Strategy:
///   main() sets sentinel = 0xBEEF and records it in GPIOR0/GPIOR1.
///   An ISR fires (Timer0 OVF) and performs a uint16 add (forces R24/R25 use).
///   After the ISR returns, sentinel is re-read; must still be 0xBEEF.
///
/// Data-space addresses (ATmega328P):
///   GPIOR0 = 0x3E, GPIOR1 = 0x4A
/// </summary>
[TestFixture]
public class IsrRegPreservationTests
{
    private const int Gpior0Addr = 0x3E;
    private const int Gpior1Addr = 0x4A;

    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("isr-reg-preservation");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }

    [Test]
    public void Cp1_SentinelLow_Is0xEF()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0xEF,
            "sentinel = 0xBEEF; low byte = 0xEF must be in GPIOR0 before ISR");
    }

    [Test]
    public void Cp1_SentinelHigh_Is0xBE()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior1Addr].Should().Be(0xBE,
            "sentinel = 0xBEEF; high byte = 0xBE must be in GPIOR1 before ISR");
    }

    [Test]
    public void Cp2_AfterIsr_SentinelLow_StillIs0xEF()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak(maxInstructions: 2_000_000);
        uno.Data[Gpior0Addr].Should().Be(0xEF,
            "R24 must be preserved across ISR; sentinel low byte must still be 0xEF");
    }

    [Test]
    public void Cp2_AfterIsr_SentinelHigh_StillIs0xBE()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak(maxInstructions: 2_000_000);
        uno.Data[Gpior1Addr].Should().Be(0xBE,
            "R25 must be preserved across ISR; sentinel high byte must still be 0xBE");
    }
}


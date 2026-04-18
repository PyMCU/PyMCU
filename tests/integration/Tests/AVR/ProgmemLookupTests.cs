using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/progmem-lookup.
///
/// Verifies that const[uint8[N]] global arrays are placed in flash (PROGMEM)
/// and accessed correctly via LPM Z.  The compiler must emit a .db table in
/// .text and generate LPM sequences for every element read.
///
/// Table: SIN_4 = [0, 64, 127, 64]
///
/// Checkpoint 1: SIN_4[0] = 0   → GPIOR0 = 0x00
/// Checkpoint 2: SIN_4[1] = 64  → GPIOR0 = 0x40
/// Checkpoint 3: SIN_4[2] = 127 → GPIOR0 = 0x7F
/// Checkpoint 4: SIN_4[3] = 64  → GPIOR0 = 0x40  (via variable index)
///
/// Data-space address (ATmega328P): GPIOR0 = 0x3E
/// </summary>
[TestFixture]
public class ProgmemLookupTests
{
    private const int Gpior0Addr = 0x3E;

    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("progmem-lookup");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }

    [Test]
    public void Cp1_SIN4_0_Is0()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x00, "SIN_4[0] must be 0");
    }

    [Test]
    public void Cp2_SIN4_1_Is64()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x40, "SIN_4[1] must be 64 = 0x40");
    }

    [Test]
    public void Cp3_SIN4_2_Is127()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x7F, "SIN_4[2] must be 127 = 0x7F");
    }

    [Test]
    public void Cp4_SIN4_VariableIndex3_Is64()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x40, "SIN_4[idx=3] must be 64 = 0x40 (variable index via LPM Z)");
    }
}

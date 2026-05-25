using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/asm-fstring.
///
/// Verifies that asm(f"INSTR {port}, {bit}") emits the correct assembly
/// instruction when all interpolated expressions are compile-time constants
/// (const[uint8] function parameters).
///
/// Checkpoint 1: sbi(0x0A, 5) → SBI DDRD, 5 → PD5 configured as output
///   DDRD data addr = 0x2A; bit 5 set (mask 0x20)
///
/// Checkpoint 2: sbi(0x0B, 5) → SBI PORTD, 5 → PD5 driven high
///   PORTD data addr = 0x2B; bit 5 set
///
/// Checkpoint 3: cbi(0x0B, 5) → CBI PORTD, 5 → PD5 driven low
///   PORTD data addr = 0x2B; bit 5 cleared
///
/// ATmega328P data-space addresses:
///   DDRD  = 0x2A  (I/O 0x0A)
///   PORTD = 0x2B  (I/O 0x0B)
/// </summary>
[TestFixture]
public class AsmFstringTests
{
    private const int DdrdAddr  = 0x2A;
    private const int PortdAddr = 0x2B;
    private const int Bit5Mask  = 0x20;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("asm-fstring"));

    private ArduinoUnoSimulation Boot() => _session.Reset();

    [Test]
    public void Cp1_SbiFString_SetsDdrBit()
    {
        var uno = Boot();
        uno.RunToBreak();
        (uno.Data[DdrdAddr] & Bit5Mask).Should().Be(Bit5Mask,
            "asm(f'SBI {port}, {bit}') with port=0x0A, bit=5 should set DDRD[5]");
    }

    [Test]
    public void Cp2_SbiFString_SetsPortBit()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        (uno.Data[PortdAddr] & Bit5Mask).Should().Be(Bit5Mask,
            "asm(f'SBI {port}, {bit}') with port=0x0B, bit=5 should set PORTD[5]");
    }

    [Test]
    public void Cp3_CbiFString_ClearsPortBit()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        (uno.Data[PortdAddr] & Bit5Mask).Should().Be(0,
            "asm(f'CBI {port}, {bit}') with port=0x0B, bit=5 should clear PORTD[5]");
    }
}

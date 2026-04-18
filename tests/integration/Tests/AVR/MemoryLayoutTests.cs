using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/memory-layout.
///
/// Verifies that hardware control registers are written to the correct data-space
/// addresses using Memory.Should().HaveByteAt(), HaveWordAt(), and HaveBytesAt()
/// — assertion methods that were not used in any prior integration test.
///
/// Data-space addresses (ATmega328P):
///   TCCR0A = 0x44   TCCR0B = 0x45   OCR0A  = 0x47
///   TCCR1A = 0x80   TCCR1B = 0x81
///   ICR1   = 0x86   (uint16 LE: low byte at 0x86, high byte at 0x87)
///   OCR1A  = 0x88   (uint16 LE: low byte at 0x88, high byte at 0x89)
/// </summary>
[TestFixture]
public class MemoryLayoutTests
{
    private SimSession _session = null!;

    // ATmega328P data-space addresses
    private const int TCCR0A_ADDR = 0x44;
    private const int TCCR0B_ADDR = 0x45;
    private const int OCR0A_ADDR  = 0x47;
    private const int TCCR1A_ADDR = 0x80;
    private const int TCCR1B_ADDR = 0x81;
    private const int ICR1_ADDR   = 0x86;   // ICR1L (LE word: low at 0x86, high at 0x87)
    private const int OCR1A_ADDR  = 0x88;   // OCR1AL (LE word: low at 0x88, high at 0x89)

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("memory-layout"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunToBreak();
        return uno;
    }

    [Test]
    public void Tccr0a_ByteAt_0x44_Is_0x83()
    {
        // TCCR0A = 0x83 (COM0A1|WGM01|WGM00) written to data space 0x44
        Boot().Memory.Should().HaveByteAt(TCCR0A_ADDR, 0x83,
            "TCCR0A (Fast PWM, COM0A1|WGM01|WGM00) must be at data-space 0x44");
    }

    [Test]
    public void Timer0_AdjacentControlRegs_AsWord_0x0183()
    {
        // TCCR0A at 0x44 and TCCR0B at 0x45 are adjacent — read as a little-endian
        // word: Data[0x44] = low byte (0x83), Data[0x45] = high byte (0x01) → 0x0183
        Boot().Memory.Should().HaveWordAt(TCCR0A_ADDR, 0x0183,
            "TCCR0A=0x83 (low) and TCCR0B=0x01 (high) must form word 0x0183 at 0x44");
    }

    [Test]
    public void Ocr0a_ByteAt_0x47_Is_200()
    {
        // OCR0A = 200 (0xC8) written to data space 0x47
        Boot().Memory.Should().HaveByteAt(OCR0A_ADDR, 0xC8,
            "OCR0A=200 (0xC8) must be at data-space 0x47");
    }

    [Test]
    public void Timer1_ICR1_WordAt_0x86_Is_16000()
    {
        // ICR1 is a ptr[uint16] in atmega328p.py — the 16-bit write must produce
        // little-endian layout: ICR1L=0x80 at 0x86, ICR1H=0x3E at 0x87
        // ReadUInt16LE(0x86) = 0x80 | (0x3E << 8) = 0x3E80 = 16000
        Boot().Memory.Should().HaveWordAt(ICR1_ADDR, 16000,
            "ICR1=16000 (0x3E80) must be written as LE word at 0x86 via ptr[uint16]");
    }

    [Test]
    public void Timer1_OCR1A_WordAt_0x88_Is_8000()
    {
        // OCR1A is a ptr[uint16] in atmega328p.py — same pattern as ICR1
        // ReadUInt16LE(0x88) = 0x40 | (0x1F << 8) = 0x1F40 = 8000
        Boot().Memory.Should().HaveWordAt(OCR1A_ADDR, 8000,
            "OCR1A=8000 (0x1F40) must be written as LE word at 0x88 via ptr[uint16]");
    }
}

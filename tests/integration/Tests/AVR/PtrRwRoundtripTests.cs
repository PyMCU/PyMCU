using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/ptr-rw-roundtrip.
///
/// Tests the ptr[uint8] and ptr[uint16] READ-BACK codegen paths.
///
/// The existing memory-layout tests verify ptr[uint16] WRITE (STS pair) only.
/// This fixture exercises the READ path:
///   w: uint16 = ICR1.value   ->   LDS R24, 0x0086 / LDS R25, 0x0087
/// and then writes the read-back value to a different register (OCR1A), verifying
/// that the 16-bit value is preserved through the LDS → STS round-trip.
///
/// Checkpoint 1 — ptr[uint8] round-trip:
///   GPIOR0 = 0xAB (OUT 0x1E, R24)
///   readback = GPIOR0.value (IN R24, 0x1E)
///   GPIOR1 = readback (must equal 0xAB)
///
/// Checkpoint 2 — ptr[uint16] write / read-back / derive / write:
///   ICR1.value = 12345  (0x3039)   -> STS 0x0086 / STS 0x0087
///   w = ICR1.value                  -> LDS 0x0086 / LDS 0x0087  (READ-BACK)
///   OCR1A.value = w                 -> STS 0x0088 / STS 0x0089
///   => ICR1 and OCR1A must both contain 12345; byte layout must be little-endian.
///
/// Data-space addresses (ATmega328P):
///   GPIOR0 = 0x3E   GPIOR1 = 0x4A
///   ICR1   = 0x86   (ICR1L = 0x86, ICR1H = 0x87)
///   OCR1A  = 0x88   (OCR1AL = 0x88, OCR1AH = 0x89)
/// </summary>
[TestFixture]
public class PtrRwRoundtripTests
{
    private SimSession _session = null!;

    private const int GPIOR0_ADDR = 0x3E;
    private const int GPIOR1_ADDR = 0x4A;
    private const int ICR1_ADDR   = 0x86;
    private const int OCR1A_ADDR  = 0x88;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("ptr-rw-roundtrip"));

    private ArduinoUnoSimulation BootCp1()
    {
        var uno = _session.Reset();
        uno.RunToBreak();
        return uno;
    }

    private ArduinoUnoSimulation BootCp2()
    {
        var uno = _session.Reset();
        uno.RunToBreak();
        uno.RunInstructions(1); // step over BREAK at cp1
        uno.RunToBreak();       // arrive at cp2
        return uno;
    }

    // --- Checkpoint 1: ptr[uint8] round-trip ---

    [Test]
    public void Cp1_PtrUint8_WriteReadBack_MatchesMagicByte()
    {
        // GPIOR0 written with 0xAB; read back via IN; stored to GPIOR1.
        // Tests that IN Rd, io_addr correctly reads back a value written by OUT.
        BootCp1().Data[GPIOR1_ADDR].Should().Be(0xAB,
            "ptr[uint8] read-back: GPIOR1 must equal GPIOR0 write value 0xAB");
    }

    // --- Checkpoint 2: ptr[uint16] write / read-back / derive ---

    [Test]
    public void Cp2_PtrUint16_ICR1_WriteVerify_Is12345()
    {
        // ICR1.value = 12345 (0x3039): verifies STS pair write via HaveWordAt.
        BootCp2().Memory.Should().HaveWordAt(ICR1_ADDR, 12345,
            "ICR1=12345 must be written as LE word at 0x86 (ICR1L=0x39, ICR1H=0x30)");
    }

    [Test]
    public void Cp2_PtrUint16_OCR1A_DerivedFromReadBack_Is12345()
    {
        // OCR1A = read-back of ICR1: tests LDS pair READ codegen path.
        // If LDS pair reads the wrong bytes (H/L swapped, or only one byte read),
        // OCR1A will differ from 12345.
        BootCp2().Memory.Should().HaveWordAt(OCR1A_ADDR, 12345,
            "OCR1A must equal ICR1 read-back value 12345; verifies LDS-pair READ path");
    }

    [Test]
    public void Cp2_ICR1_LowByteAt0x86_Is0x39()
    {
        // 12345 = 0x3039; little-endian: low byte (0x39) at lower address (0x86)
        BootCp2().Memory.Should().HaveByteAt(ICR1_ADDR, 0x39,
            "ICR1L at 0x86 must be 0x39 (low byte of 12345 = 0x3039)");
    }

    [Test]
    public void Cp2_ICR1_HighByteAt0x87_Is0x30()
    {
        // 12345 = 0x3039; little-endian: high byte (0x30) at higher address (0x87)
        BootCp2().Memory.Should().HaveByteAt(ICR1_ADDR + 1, 0x30,
            "ICR1H at 0x87 must be 0x30 (high byte of 12345 = 0x3039)");
    }
}

using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/mmio-spy.
///
/// Uses Cpu.Mmio.RegisterWrite() hooks — a TestKit capability not exercised in any
/// prior integration test — to spy on hardware register writes during firmware
/// execution. Tests verify: write counts, captured values, write ordering, and
/// pin-toggle sequences, all without relying on UART output.
///
/// Hooks must be registered BEFORE running the firmware. The hook signature is:
///   Func&lt;byte value, byte oldValue, ushort address, byte mask, bool&gt;
/// Returning false allows the write to proceed normally; returning true vetoes it.
///
/// Data-space addresses used:
///   TCCR0A = 0x44   TCCR0B = 0x45   OCR0A = 0x47   PORTB = 0x25
/// </summary>
[TestFixture]
public class MmioSpyTests
{
    private SimSession _session = null!;

    // ATmega328P data-space addresses
    private const int TCCR0A_ADDR = 0x44;
    private const int TCCR0B_ADDR = 0x45;
    private const int OCR0A_ADDR  = 0x47;
    private const int PORTB_ADDR  = 0x25;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("mmio-spy"));

    [Test]
    public void Tccr0a_WrittenExactlyOnce()
    {
        // The firmware writes TCCR0A once (0x83). The hook must fire exactly once.
        var uno = _session.Reset();
        var count = 0;
        uno.Cpu.Mmio.RegisterWrite(TCCR0A_ADDR, (v, o, a, m) => { count++; return false; });
        uno.RunToBreak();
        count.Should().Be(1, "TCCR0A must be configured exactly once during Timer0 setup");
    }

    [Test]
    public void Ocr0a_CapturedValue_Is128()
    {
        // OCR0A is written with value 128 (0x80) for 50% duty cycle.
        var uno = _session.Reset();
        byte capturedValue = 0;
        uno.Cpu.Mmio.RegisterWrite(OCR0A_ADDR, (v, o, a, m) =>
        {
            capturedValue = v;
            return false;
        });
        uno.RunToBreak();
        capturedValue.Should().Be(128, "OCR0A must be set to 128 (50% duty cycle)");
    }

    [Test]
    public void ConfigOrder_Tccr0a_Before_Tccr0b()
    {
        // Good practice: configure mode (TCCR0A) before enabling the prescaler clock
        // (TCCR0B). The firmware intentionally writes in this order.
        var uno = _session.Reset();
        var writeOrder = new List<string>();
        uno.Cpu.Mmio.RegisterWrite(TCCR0A_ADDR, (v, o, a, m) => { writeOrder.Add("TCCR0A"); return false; });
        uno.Cpu.Mmio.RegisterWrite(TCCR0B_ADDR, (v, o, a, m) => { writeOrder.Add("TCCR0B"); return false; });
        uno.RunToBreak();
        writeOrder.Should().ContainInOrder(new[] { "TCCR0A", "TCCR0B" },
            "TCCR0A (mode) must be configured before TCCR0B (clock enable) to avoid glitching");
    }

    [Test]
    public void PortB_ToggleCount_IsAtLeastThree()
    {
        // The firmware drives PORTB[5] high, low, then high again = 3 writes to PORTB.
        // (DDRB is a separate register; PORTB writes are counted independently.)
        var uno = _session.Reset();
        var portBWriteCount = 0;
        uno.Cpu.Mmio.RegisterWrite(PORTB_ADDR, (v, o, a, m) => { portBWriteCount++; return false; });
        uno.RunToBreak();
        portBWriteCount.Should().BeGreaterThanOrEqualTo(3,
            "firmware writes to PORTB at least 3 times (high/low/high toggle sequence)");
    }

    [Test]
    public void PortB_LastWrittenValue_HasBit5_Set()
    {
        // The last PORTB write in the firmware is PORTB[5]=1, so bit 5 (mask 0x20)
        // must be set in the final value captured by the hook.
        var uno = _session.Reset();
        byte lastPortBValue = 0;
        uno.Cpu.Mmio.RegisterWrite(PORTB_ADDR, (v, o, a, m) =>
        {
            lastPortBValue = v;
            return false;
        });
        uno.RunToBreak();
        (lastPortBValue & 0x20).Should().Be(0x20,
            "last PORTB write is high (PORTB[5]=1), so bit 5 (0x20) must be set");
    }
}

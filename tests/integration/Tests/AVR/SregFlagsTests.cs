using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/sreg-flags.
///
/// Verifies that arithmetic operations produce the correct SREG flag state
/// at each BREAK checkpoint. Uses Cpu.Should().HaveCarryFlag(),
/// HaveZeroFlag(), HaveNegativeFlag(), HaveOverflowFlag(), and
/// HaveInterruptsEnabled() -- assertion methods not used in prior tests.
///
/// Key property: STS/OUT instructions used to export results to GPIOR0 do
/// NOT modify SREG, so the CPU flags at each BREAK directly reflect the
/// preceding ADD or SUB instruction.
///
/// Checkpoints:
///   1 -- 255 + 1 = 0     : C=1, Z=1 (unsigned overflow wraps to zero)
///   2 -- 64  + 64 = 128  : N=1, V=1 (signed overflow into MSB), C=0
///   3 -- 10  - 10 = 0    : Z=1, C=0, N=0 (pure subtraction zero)
///   4 -- SEI executed    : I flag set (global interrupts enabled)
/// </summary>
[TestFixture]
public class SregFlagsTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("sreg-flags"));

    /// <summary>Advances the simulation through N BREAK checkpoints.</summary>
    private static void SkipBreaks(ArduinoUnoSimulation uno, int count)
    {
        for (var i = 0; i < count; i++)
        {
            uno.RunToBreak();
            uno.RunInstructions(1); // step over the BREAK opcode
        }
    }

    private ArduinoUnoSimulation Boot() => _session.Reset();

    // ── Checkpoint 1: 255 + 1 = 0 ────────────────────────────────────────────

    [Test]
    public void Cp1_UnsignedOverflow_CarryFlag_IsSet()
    {
        // ADD 0xFF + 0x01 = 0x100 -> result byte 0x00; carry out of bit 7 -> C=1
        var uno = Boot();
        uno.RunToBreak();
        uno.Cpu.Should().HaveCarryFlag(true,
            "ADD 0xFF+0x01 produces a carry out of bit 7");
    }

    [Test]
    public void Cp1_UnsignedOverflow_ZeroFlag_IsSet()
    {
        // Result byte is 0x00 -> Z=1
        var uno = Boot();
        uno.RunToBreak();
        uno.Cpu.Should().HaveZeroFlag(true,
            "ADD 0xFF+0x01 wraps to 0x00 which sets the zero flag");
    }

    [Test]
    public void Cp1_GlobalInterrupts_AreDisabled()
    {
        // SEI has not been called yet at checkpoint 1
        var uno = Boot();
        uno.RunToBreak();
        uno.Cpu.Should().HaveInterruptsEnabled(false,
            "global interrupts must be disabled before any SEI call");
    }

    // ── Checkpoint 2: 64 + 64 = 128 ──────────────────────────────────────────

    [Test]
    public void Cp2_SignedOverflow_NegativeFlag_IsSet()
    {
        // Result 0x80 has bit 7 set -> N=1
        var uno = Boot();
        SkipBreaks(uno, 1);
        uno.RunToBreak();
        uno.Cpu.Should().HaveNegativeFlag(true,
            "ADD 0x40+0x40 = 0x80 has bit-7 set, so N=1");
    }

    [Test]
    public void Cp2_SignedOverflow_OverflowFlag_IsSet()
    {
        // Signed: +64 + +64 = +128 overflows signed 8-bit range -> V=1
        var uno = Boot();
        SkipBreaks(uno, 1);
        uno.RunToBreak();
        uno.Cpu.Should().HaveOverflowFlag(true,
            "adding two positive values (+64+64) that produce a negative result sets V=1");
    }

    [Test]
    public void Cp2_SignedOverflow_CarryFlag_IsClear()
    {
        // 0x40 + 0x40 = 0x80 -- no carry out of bit 7 -> C=0
        var uno = Boot();
        SkipBreaks(uno, 1);
        uno.RunToBreak();
        uno.Cpu.Should().HaveCarryFlag(false,
            "ADD 0x40+0x40 = 0x80 does not carry out of bit 7, so C=0");
    }

    // ── Checkpoint 3: 10 - 10 = 0 ────────────────────────────────────────────

    [Test]
    public void Cp3_SubtractSelf_ZeroFlag_IsSet()
    {
        // 10 - 10 = 0 -> Z=1
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        uno.Cpu.Should().HaveZeroFlag(true,
            "SUB where both operands are equal must set the zero flag");
    }

    [Test]
    public void Cp3_SubtractSelf_CarryFlag_IsClear()
    {
        // 10 - 10: no borrow -> C=0
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        uno.Cpu.Should().HaveCarryFlag(false,
            "SUB 10-10 has no borrow, so carry (borrow) flag must be clear");
    }

    // ── Checkpoint 4: SEI ─────────────────────────────────────────────────────

    [Test]
    public void Cp4_AfterSEI_InterruptsEnabled()
    {
        // asm("SEI") sets the I flag in SREG; BREAK stops before executing the
        // next instruction, so the I flag is visible at this point
        var uno = Boot();
        SkipBreaks(uno, 3);
        uno.RunToBreak();
        uno.Cpu.Should().HaveInterruptsEnabled(true,
            "global interrupts must be enabled immediately after SEI");
    }
}

using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/augmented-sreg.
///
/// Verifies that AugAssign operators (|=, &=, ^=) emit the correct AVR logical
/// instructions (OR, AND, EOR) AND that the resulting SREG flags are correct.
///
/// AugAssign uses a separate IR node and codegen path from plain Assign; a bug
/// there could emit wrong instructions or clobber/omit the result.
///
/// AVR flag rules for OR / AND / EOR:
///   N = result[7]   (Negative: bit 7 of result)
///   Z = (result == 0)
///   V = 0           (always cleared by logical instructions)
///   C unchanged     (logical instructions do not touch C)
///
/// Non-inline wrappers (or_u8, and_u8, xor_u8) receive operands as R24 / R22
/// (AVR calling convention), making them runtime from the callee's perspective
/// and preventing constant folding. After the ALU instruction:
///   MOV / OUT / BREAK — none of these touch SREG.
/// Therefore SREG at each BREAK directly reflects the preceding OR / AND / EOR.
///
/// Checkpoints:
///   1 — or_u8(0x00, 0x80)  = 0x80 : N=1 (bit7 set), Z=0
///   2 — and_u8(0xF0, 0x0F) = 0x00 : N=0, Z=1 (result is zero)
///   3 — xor_u8(0xAA, 0xAA) = 0x00 : N=0, Z=1 (self-cancellation)
///
/// Data-space address:
///   GPIOR0 = 0x3E
/// </summary>
[TestFixture]
public class AugmentedSregTests
{
    private SimSession _session = null!;

    private const int GPIOR0_ADDR = 0x3E;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("augmented-sreg"));

    private static void SkipBreaks(ArduinoUnoSimulation uno, int count)
    {
        for (var i = 0; i < count; i++)
        {
            uno.RunToBreak();
            uno.RunInstructions(1);
        }
    }

    private ArduinoUnoSimulation Boot() => _session.Reset();

    // --- Checkpoint 1: OR  (0x00 | 0x80 = 0x80) ---

    [Test]
    public void Cp1_Or_Result_Is0x80()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(0x80,
            "|= 0x00 | 0x80 must produce 0x80");
    }

    [Test]
    public void Cp1_Or_NegativeFlag_IsSet()
    {
        // OR result = 0x80; bit7 = 1 -> N=1
        var uno = Boot();
        uno.RunToBreak();
        uno.Cpu.Should().HaveNegativeFlag(true,
            "OR 0x80 has bit7 set; N flag must be 1");
    }

    [Test]
    public void Cp1_Or_ZeroFlag_IsClear()
    {
        // OR result = 0x80 != 0 -> Z=0
        var uno = Boot();
        uno.RunToBreak();
        uno.Cpu.Should().HaveZeroFlag(false,
            "OR result 0x80 is non-zero; Z flag must be 0");
    }

    // --- Checkpoint 2: AND  (0xF0 & 0x0F = 0x00) ---

    [Test]
    public void Cp2_And_Result_Is0()
    {
        var uno = Boot();
        SkipBreaks(uno, 1);
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(0,
            "&= 0xF0 & 0x0F must produce 0x00 (no common bits)");
    }

    [Test]
    public void Cp2_And_ZeroFlag_IsSet()
    {
        // AND result = 0x00 -> Z=1
        var uno = Boot();
        SkipBreaks(uno, 1);
        uno.RunToBreak();
        uno.Cpu.Should().HaveZeroFlag(true,
            "AND 0xF0 & 0x0F = 0x00; Z flag must be 1");
    }

    [Test]
    public void Cp2_And_NegativeFlag_IsClear()
    {
        // AND result = 0x00; bit7 = 0 -> N=0
        var uno = Boot();
        SkipBreaks(uno, 1);
        uno.RunToBreak();
        uno.Cpu.Should().HaveNegativeFlag(false,
            "AND result 0x00 has bit7=0; N flag must be 0");
    }

    // --- Checkpoint 3: XOR self-cancel  (0xAA ^ 0xAA = 0x00) ---

    [Test]
    public void Cp3_Xor_SelfCancel_Result_Is0()
    {
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(0,
            "^= 0xAA ^ 0xAA must self-cancel to 0x00");
    }

    [Test]
    public void Cp3_Xor_ZeroFlag_IsSet()
    {
        // EOR result = 0x00 -> Z=1
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        uno.Cpu.Should().HaveZeroFlag(true,
            "EOR 0xAA ^ 0xAA = 0x00; Z flag must be 1");
    }

    [Test]
    public void Cp3_Xor_NegativeFlag_IsClear()
    {
        // EOR result = 0x00; bit7 = 0 -> N=0
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        uno.Cpu.Should().HaveNegativeFlag(false,
            "EOR result 0x00 has bit7=0; N flag must be 0");
    }
}

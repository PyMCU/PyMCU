using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/isr-sp-preservation.
///
/// Verifies that the compiler-generated ISR context save/restore sequence
/// leaves the stack pointer exactly where it was before the interrupt fired.
///
/// The ISR context save (EmitContextSave) pushes R16, R17, R18, and the SREG
/// value (4 bytes total). EmitContextRestore pops them in reverse order before
/// RETI. Any asymmetry between save and restore would manifest as SP drift.
///
/// Strategy:
///   Checkpoint 1 (before SEI): record SP baseline via Cpu.Sp.
///   Checkpoint 2 (after ISR returns): assert Cpu.Should().HaveSP(baseline).
///
/// The ISR body uses only SBI (no extra stack frame), so any SP change between
/// the two checkpoints is caused by a bug in context save/restore.
///
/// Data-space addresses:
///   GPIOR0 = 0x3E   (ISR flag byte)
/// </summary>
[TestFixture]
public class IsrSpPreservationTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("isr-sp-preservation");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }

    [Test]
    public void Startup_SP_Equals_RamEnd()
    {
        // At checkpoint 1 (first instruction in main, after the init code that
        // explicitly sets SP to RAMEND): SP = RAMEND = 0x08FF.
        // The reset vector uses RJMP main (not CALL), so no return address is
        // pushed; and main() itself initialises SP to RAMEND via OUT SPH/SPL.
        const ushort expected = 0x08FF;
        var uno = Boot();
        uno.RunToBreak();
        uno.Cpu.Should().HaveSP(expected,
            "SP must equal RAMEND (0x08FF) at checkpoint 1 after SP init in main()");
    }

    [Test]
    public void AfterISR_SP_Equals_Baseline()
    {
        // Record SP at checkpoint 1 (before enabling interrupts).
        var uno = Boot();
        uno.RunToBreak();
        var spBefore = uno.Cpu.Sp;

        // Step past checkpoint 1, then run until checkpoint 2 (after ISR has
        // fired and completed its full RETI path).
        uno.RunInstructions(1);
        uno.RunToBreak(maxInstructions: 500_000);

        // SP must match the baseline; any context save/restore bug will show here.
        uno.Cpu.Should().HaveSP(spBefore,
            "SP after ISR must equal SP before ISR; context save must be symmetric");
    }

    [Test]
    public void AfterISR_GPIOR0_Bit0_IsSet()
    {
        // Confirms the ISR actually fired (sanity check for the test setup).
        const int GPIOR0_ADDR = 0x3E;
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak(maxInstructions: 500_000);
        (uno.Data[GPIOR0_ADDR] & 0x01).Should().Be(1,
            "Timer0 OVF ISR must have fired and set GPIOR0 bit 0");
    }
}

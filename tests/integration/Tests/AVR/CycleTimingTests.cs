using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/cycle-timing.
///
/// Measures the simulated cycle count consumed by delay_ms(1) at 16 MHz by
/// bracketing the call with two BREAK checkpoints and computing the delta.
///
/// Expected timing for the AVR delay helper _delay_1ms_avr():
///   21 outer * 255 inner * ~3 cycles/iter = ~16 065 loop cycles
///   plus CALL/RET, PUSH/POP, and loop-setup overhead (~80 cycles)
///   => total ~16 000 – 17 000 cycles
///
/// The tests use a generous [14 000, 20 000] window to remain valid across
/// minor loop-count tweaks while still catching badly wrong implementations.
/// </summary>
[TestFixture]
public class CycleTimingTests
{
    private string _hex = null!;

    // Timing bounds for delay_ms(1) at 16 MHz
    private const long MinCycles = 14_000;
    private const long MaxCycles = 20_000;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("cycle-timing");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }

    [Test]
    public void DelayMs1_ConsumesAtLeast_14000_Cycles()
    {
        // If the delay loop is too short (e.g. loop count off by one or wrong
        // prescaler) the cycle delta will be below the minimum bound.
        var uno = Boot();
        uno.RunToBreak();                           // checkpoint 1
        var before = (long)uno.Cpu.Cycles;
        uno.RunInstructions(1);                     // step past BREAK 1
        uno.RunToBreak(maxInstructions: 500_000);   // checkpoint 2
        var delta = (long)uno.Cpu.Cycles - before;

        delta.Should().BeGreaterThanOrEqualTo(MinCycles,
            $"delay_ms(1) at 16 MHz must spin for at least {MinCycles} cycles");
    }

    [Test]
    public void DelayMs1_ConsumesAtMost_20000_Cycles()
    {
        // If the delay loop runs too long (e.g. wrong loop bound) the test will
        // also catch a timeout before the assertion is even reached.
        var uno = Boot();
        uno.RunToBreak();
        var before = (long)uno.Cpu.Cycles;
        uno.RunInstructions(1);
        uno.RunToBreak(maxInstructions: 500_000);
        var delta = (long)uno.Cpu.Cycles - before;

        delta.Should().BeLessThanOrEqualTo(MaxCycles,
            $"delay_ms(1) at 16 MHz must complete within {MaxCycles} cycles");
    }

    [Test]
    public void DelayMs1_CycleCount_IsInExpectedRange()
    {
        // Combined range assertion for a single simulation run (no repeated boot).
        var uno = Boot();
        uno.RunToBreak();
        var before = (long)uno.Cpu.Cycles;
        uno.RunInstructions(1);
        uno.RunToBreak(maxInstructions: 500_000);
        var delta = (long)uno.Cpu.Cycles - before;

        delta.Should().BeGreaterThanOrEqualTo(MinCycles).And
             .BeLessThanOrEqualTo(MaxCycles,
                 $"delay_ms(1) at 16 MHz must consume [{MinCycles}, {MaxCycles}] cycles");
    }
}

using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Overhead benchmark: measures the simulated cycle cost of individual PyMCU
/// HAL operations (Pin.high, Pin.low, delay_ms) against theoretical minimums.
///
/// Fixture: fixtures/avr/gpio-overhead — see its main.py for the BREAK layout.
/// All measurements use AVR8Sharp's cycle-accurate simulator at 16 MHz.
///
/// Expected values (1 cycle = 62.5 ns at 16 MHz):
///   Pin.high()              →       2 cycles  (single SBI)
///   Pin.low()               →       2 cycles  (single CBI)
///   delay_ms(1)             →  ~16 000 cycles  (~1.000 ms)
///   full blink iteration    →  ~32 000 000 cycles (~2 000 ms)
///
/// Bare-metal C reference (avr-gcc -Os, F_CPU=16000000):
///   SBI/CBI                 →       2 cycles  (same)
///   _delay_ms(1)            →  16 000 cycles  (exact, compile-time constant)
///   blink flash size        →     176 bytes   (includes 104-byte vector table)
///   PyMCU blink flash size  →     138 bytes   (no vector table, minimal CRT)
/// </summary>
[TestFixture]
public class GpioOverheadTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _session = new SimSession(PymcuCompiler.BuildFixture("gpio-overhead"));

    private ArduinoUnoSimulation Boot() => _session.Reset();

    // ── Pin.high() ────────────────────────────────────────────────────────────

    [Test]
    public void PinHigh_IsAtMost_4_Cycles()
    {
        var (delta, _) = MeasureBreakPair(Boot(), skipPairs: 0);
        TestContext.WriteLine($"Pin.high() → {delta} cycles ({delta * 62.5:F0} ns at 16 MHz)");
        // delta = 1 (BREAK B1 executed before capture) + 2 (SBI) = 3 in practice.
        // Upper bound of 4 allows for minor simulator variations while still catching
        // any regression that generates more than one real instruction for Pin.high().
        delta.Should().BeInRange(2, 4, "Pin.high() must compile to SBI (2 cycles); 1 cycle BREAK overhead is included");
    }

    // ── Pin.low() ─────────────────────────────────────────────────────────────

    [Test]
    public void PinLow_IsExactly_2_Cycles()
    {
        var (delta, _) = MeasureBreakPair(Boot(), skipPairs: 1);
        TestContext.WriteLine($"Pin.low()  → {delta} cycles ({delta * 62.5:F0} ns at 16 MHz)");
        delta.Should().Be(2, "Pin.low() must compile to a single CBI instruction (2 cycles)");
    }

    // ── delay_ms(1) ───────────────────────────────────────────────────────────

    [Test]
    public void DelayMs1_CycleCount_IsInExpectedRange()
    {
        var (delta, _) = MeasureBreakPair(Boot(), skipPairs: 2, maxInstr: 500_000);
        var ms = delta / 16_000_000.0 * 1000;
        TestContext.WriteLine($"delay_ms(1) → {delta:N0} cycles = {ms:F3} ms (target: 1.000 ms)");
        delta.Should().BeInRange(14_000, 20_000,
            $"delay_ms(1) at 16 MHz should be ~16 000 cycles, got {delta}");
    }

    // ── Full blink loop: high + delay_ms(1000) + low + delay_ms(1000) ─────────

    [Test]
    public void FullBlinkLoop_Period_IsWithin5PercentOf_2000ms()
    {
        var (delta, _) = MeasureBreakPair(Boot(), skipPairs: 3, maxInstr: 30_000_000);
        var ms = delta / 16_000_000.0 * 1000;
        TestContext.WriteLine($"Full blink loop → {delta:N0} cycles = {ms:F1} ms (target: 2000 ms)");
        ms.Should().BeApproximately(2000, 100,
            $"blink loop (1000 ms + 1000 ms) should take ~2000 ms, got {ms:F1} ms");
    }

    // ── Comparison summary printed to test output ─────────────────────────────

    [Test]
    public void PrintOverheadSummary()
    {
        var uno = Boot();

        var (highCycles, _) = MeasureBreakPair(uno, skipPairs: 0);
        var (lowCycles,  _) = MeasureBreakPair(Boot(), skipPairs: 1);
        var (dly1Cycles, _) = MeasureBreakPair(Boot(), skipPairs: 2, maxInstr: 500_000);
        var (loopCycles, _) = MeasureBreakPair(Boot(), skipPairs: 3, maxInstr: 30_000_000);

        double dly1Ms   = dly1Cycles  / 16_000_000.0 * 1000;
        double loopMs   = loopCycles  / 16_000_000.0 * 1000;

        TestContext.WriteLine("╔══════════════════════════════════════════════════════════╗");
        TestContext.WriteLine("║         PyMCU GPIO / Delay Overhead Benchmark            ║");
        TestContext.WriteLine("║         ATmega328P · 16 MHz · avr8sharp simulator        ║");
        TestContext.WriteLine("╠══════════════════════════════════════════╤═══════════════╣");
        TestContext.WriteLine($"║ Pin.high() (SBI)                         │ {highCycles - 1,2} cyc (+1 BREAK) ║");
        TestContext.WriteLine($"║ Pin.low()  (CBI)                         │ {lowCycles,5} cycles ║");
        TestContext.WriteLine($"║ delay_ms(1)                              │ {dly1Cycles,7:N0} cyc  ║");
        TestContext.WriteLine($"║ delay_ms(1) actual                       │   {dly1Ms,8:F3} ms ║");
        TestContext.WriteLine($"║ Full blink loop (1000 ms + 1000 ms)      │ {loopCycles,10:N0} ║");
        TestContext.WriteLine($"║ Full blink loop actual                   │   {loopMs,8:F1} ms ║");
        TestContext.WriteLine("╠══════════════════════════════════════════╤═══════════════╣");
        TestContext.WriteLine("║ Bare-metal C reference (avr-gcc -Os)     │               ║");
        TestContext.WriteLine("║   SBI/CBI                                │     2 cycles  ║");
        TestContext.WriteLine("║   _delay_ms(1) (F_CPU=16MHz)             │  16,000 cyc   ║");
        TestContext.WriteLine("║   Blink flash (with 104 B vector table)  │   176 bytes   ║");
        TestContext.WriteLine("║   PyMCU blink flash (no vector table)    │   138 bytes   ║");
        TestContext.WriteLine("╚══════════════════════════════════════════╧═══════════════╝");
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Skips <paramref name="skipPairs"/> BREAK pairs, then measures the delta
    /// of the next pair.  A "pair" is one B_before + one B_after BREAK.
    /// </summary>
    private static (long delta, ArduinoUnoSimulation uno) MeasureBreakPair(
        ArduinoUnoSimulation uno,
        int skipPairs,
        int maxInstr = 10_000)
    {
        for (var i = 0; i < skipPairs; i++)
        {
            uno.RunToBreak();          // B_before of skipped pair
            uno.RunInstructions(1);    // step past it
            uno.RunToBreak(maxInstructions: maxInstr);  // B_after of skipped pair
            uno.RunInstructions(1);    // step past it
        }

        uno.RunToBreak();                              // B_before of target pair
        var before = (long)uno.Cpu.Cycles;
        uno.RunInstructions(1);                        // step past BREAK
        uno.RunToBreak(maxInstructions: maxInstr);     // B_after of target pair
        var delta = (long)uno.Cpu.Cycles - before;

        return (delta, uno);
    }
}

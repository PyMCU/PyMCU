// SPDX-License-Identifier: MIT
using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for the t-flag-errors fixture.
///
/// Verifies the T-flag error propagation model (SET/CLT/BRTS) that replaced
/// setjmp/longjmp (SJLJ) for exception handling.
///
/// Key ABI invariants exercised:
///   - CanFail error path   : LDD R22, code; SET; RET  (3 instructions)
///   - CanFail success path : [result in R24]; CLT; RET  (T cleared before return)
///   - try/except call site : BRTC skip; RJMP catch_dispatch (zero cost when T=0)
///   - Catch dispatch       : R22 compared against each handler's exception code
///
/// Scenarios (all single-type to remain independent of exception-code constant
/// folding, which is a separate compiler concern):
///   A - raise ValueError caught by except ValueError
///   B - no raise, except not triggered, happy path returns value
///   C - CanFail function with two parameters, raise on invalid arg
///   D - T flag is CLT'd after success; subsequent try not fooled by stale T=1
///   E - return value is correct from a successful CanFail call (safe_add(8,2)=10)
///   F - three sequential raises each caught independently (counter = 3 = 0x03)
/// </summary>
[TestFixture]
public class TFlagErrorsTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware()
        => _session = new SimSession(PymcuCompiler.BuildFixture("t-flag-errors"));

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Full run: resets simulation and waits for the DONE sentinel.</summary>
    private ArduinoUnoSimulation FullRun(int maxMs = 5000)
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, "DONE\n", maxMs: maxMs);
        return uno;
    }

    // ── Boot ──────────────────────────────────────────────────────────────────

    [Test]
    public void Boot_PrintsBanner()
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, "TFLAG\n", maxMs: 500);
        uno.Serial.Should().ContainLine("TFLAG");
    }

    // ── Scenario A: raise caught ───────────────────────────────────────────────

    [Test]
    public void A_RaiseValueError_IsCaughtByExcept()
    {
        // must_positive(0) → LDD R22, code; SET; RET → BranchOnError fires → "A:caught"
        var uno = FullRun();
        uno.Serial.Should().ContainLine("A:caught",
            "raise ValueError inside try must be caught by except ValueError via T-flag BRTS");
    }

    [Test]
    public void A_RaiseValueError_DoesNotPrintMissed()
    {
        var uno = FullRun();
        uno.Serial.Text.Should().NotContain("A:missed",
            "the try body after the raise must not execute once SET fires");
    }

    // ── Scenario B: no raise, happy path ──────────────────────────────────────

    [Test]
    public void B_NoRaise_TryBodyCompletes()
    {
        // must_positive(7) → CLT; RET → BranchOnError does not fire → "B:ok"
        var uno = FullRun();
        uno.Serial.Should().ContainLine("B:ok",
            "when no raise occurs, CLT clears T so BRTS does not fire");
    }

    [Test]
    public void B_NoRaise_ExceptNotTriggered()
    {
        var uno = FullRun();
        uno.Serial.Text.Should().NotContain("B:missed",
            "the except handler must not fire when CLT was emitted by the callee");
    }

    // ── Scenario C: CanFail function with two args ────────────────────────────

    [Test]
    public void C_TwoArgCanFail_RaiseCaught()
    {
        // safe_add(201, 1): a > 200 → raise ValueError → catch → "C:caught"
        var uno = FullRun();
        uno.Serial.Should().ContainLine("C:caught",
            "safe_add with invalid arg raises ValueError; T-flag catch must fire");
    }

    [Test]
    public void C_TwoArgCanFail_MissedNotPrinted()
    {
        var uno = FullRun();
        uno.Serial.Text.Should().NotContain("C:missed");
    }

    // ── Scenario D: T flag cleared after success ──────────────────────────────

    [Test]
    public void D_TFlagClearedAfterSuccessfulCall_NoSpuriousCatch()
    {
        // must_positive(1) succeeds → emits CLT before RET → T = 0.
        // The next try/except sees T = 0 → BRTS does not fire → "D:ok".
        // Without CLT, T could be stale = 1 from a prior SET, causing a spurious catch.
        var uno = FullRun();
        uno.Serial.Should().ContainLine("D:ok",
            "CLT in the success RET path must clear T so the next BranchOnError does not fire spuriously");
    }

    [Test]
    public void D_NoSpuriousCatch_AfterPreviousSetT()
    {
        var uno = FullRun();
        uno.Serial.Text.Should().NotContain("D:spurious",
            "a stale T=1 from a prior raise must not trigger a new except handler after a successful CLT return");
    }

    // ── Scenario E: correct return value ─────────────────────────────────────

    [Test]
    public void E_SuccessfulCanFailCall_ReturnValueIsCorrect()
    {
        // safe_add(8, 2) = 10 = 0x0A; CLT; RET.
        // The return value in R24 must survive across the CLT before RET.
        var uno = FullRun();
        uno.Serial.Should().ContainLine("E:0A",
            "safe_add(8,2) must return 10=0x0A; CLT must not corrupt R24");
    }

    [Test]
    public void E_SuccessfulCanFailCall_ExceptNotTriggered()
    {
        var uno = FullRun();
        uno.Serial.Text.Should().NotContain("E:missed");
    }

    // ── Scenario F: sequential raises ────────────────────────────────────────

    [Test]
    public void F_ThreeSequentialRaises_AllCaught_CounterIsThree()
    {
        // Three independent try/except blocks each catching one raise.
        // Each catch increments a counter; final value = 3 = 0x03.
        var uno = FullRun();
        uno.Serial.Should().ContainLine("F:03",
            "three sequential raises each caught independently must yield counter=3=0x03");
    }

    // ── Full sequence ─────────────────────────────────────────────────────────

    [Test]
    public void FullSequence_AllScenariosComplete()
    {
        var uno = FullRun();

        uno.Serial.Should().ContainLine("TFLAG");
        uno.Serial.Should().ContainLine("A:caught");
        uno.Serial.Text.Should().NotContain("A:missed");
        uno.Serial.Should().ContainLine("B:ok");
        uno.Serial.Text.Should().NotContain("B:missed");
        uno.Serial.Should().ContainLine("C:caught");
        uno.Serial.Text.Should().NotContain("C:missed");
        uno.Serial.Should().ContainLine("D:ok");
        uno.Serial.Text.Should().NotContain("D:spurious");
        uno.Serial.Should().ContainLine("E:0A");
        uno.Serial.Text.Should().NotContain("E:missed");
        uno.Serial.Should().ContainLine("F:03");
        uno.Serial.Should().ContainLine("DONE");
    }
}

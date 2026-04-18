using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for the implicit-int fixture.
/// Verifies that Python's built-in <c>int</c> type annotation (maps to
/// <c>int16</c>) works without importing <c>pymcu.types</c>, and that the
/// <c>int(val)</c> cast expression likewise works without an explicit import.
///
/// Expected serial output (in order): "IINT\n" banner, "A:PASS\n",
/// "B:PASS\n", "C:PASS\n".
/// </summary>
[TestFixture]
public class ImplicitIntTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("implicit-int"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "IINT", maxMs: 200);
        uno.Serial.Should().ContainLine("IINT");
    }

    [Test]
    public void IntAnnotation_Addition_IsCorrect()
    {
        // int annotation: x=100 + y=200 = 300 → "A:PASS"
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "A:PASS", maxMs: 300);
        uno.Serial.Text.Should().Contain("A:PASS");
    }

    [Test]
    public void IntCast_WorksWithoutImport()
    {
        // int() cast: raw=42, casted=int(raw)=42 → "B:PASS"
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "B:PASS", maxMs: 300);
        uno.Serial.Text.Should().Contain("B:PASS");
    }

    [Test]
    public void Int_IsSigned_CanHoldNegativeOne()
    {
        // int is signed int16: neg starts at 0, decremented to -1 → "C:PASS"
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "C:PASS", maxMs: 300);
        uno.Serial.Text.Should().Contain("C:PASS");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim() => _session.Reset();
}

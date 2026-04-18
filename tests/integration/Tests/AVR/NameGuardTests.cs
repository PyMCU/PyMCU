using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/name-guard.
///
/// Verifies that <c>if __name__ == "__main__":</c> is evaluated at compile time:
/// the guard body executes because the entry file has __name__ == "__main__".
///
/// Checkpoints (via BREAK + GPIOR0/GPIOR1):
///   1 — GPIOR0 = 0xAA (guard body ran)
///   2 — GPIOR1 = 0xBB (code after guard also ran)
/// </summary>
[TestFixture]
public class NameGuardTests
{
    private SimSession _session = null!;

    private const int GPIOR0_ADDR = 0x3E;
    private const int GPIOR1_ADDR = 0x4A;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("name-guard"));

    private ArduinoUnoSimulation Boot() => _session.Reset();

    [Test]
    public void NameGuard_BodyExecutes()
    {
        // Checkpoint 1: inside if __name__ == "__main__":
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(0xAA, "GPIOR0 should be 0xAA — __name__ guard body must execute in entry file");
    }

    [Test]
    public void NameGuard_CodeAfterGuardAlsoRuns()
    {
        // Checkpoint 2: code after the if-guard
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[GPIOR1_ADDR].Should().Be(0xBB, "GPIOR1 should be 0xBB — code after __name__ guard must run");
    }
}

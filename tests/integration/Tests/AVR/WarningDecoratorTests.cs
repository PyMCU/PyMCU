using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/warning-decorator.
/// Verifies that @warning("...") is informational: a call to a decorated
/// function still compiles and executes (the old @compile_message aborted).
/// </summary>
[TestFixture]
public class WarningDecoratorTests
{
    private static SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("warning-decorator"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, "WARN\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("WARN");

    [Test]
    public void WarningDecoratedFunction_StillCompilesAndRuns()
    {
        // compute() is @warning-decorated; it must still run and return 0x2A.
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("V:2A"), maxMs: 300);
        uno.Serial.Text.Should().Contain("V:2A",
            "@warning is informational, so the decorated function executes");
    }
}

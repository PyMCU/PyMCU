// SPDX-License-Identifier: MIT
using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

[TestFixture]
public class ExceptionsFinallyTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("exceptions-finally"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "FINALLY");
        uno.Serial.Should().ContainLine("FINALLY");
    }

    [Test]
    public void RaiseAndCatch_FinallyRunsAfterHandler()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "A:fin\n", maxMs: 2000);
        uno.Serial.Should().ContainLine("A:caught");
        uno.Serial.Should().ContainLine("A:fin");
        uno.Serial.Should().NotContain("A:missed");
    }

    [Test]
    public void NoRaise_FinallyStillRuns()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "B:fin\n", maxMs: 2000);
        uno.Serial.Should().ContainLine("B:ok");
        uno.Serial.Should().ContainLine("B:fin");
        uno.Serial.Should().NotContain("B:missed");
    }

    [Test]
    public void FullSequence_MatchesExpected()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "DONE\n", maxMs: 3000);
        uno.Serial.Should().ContainLine("FINALLY");
        uno.Serial.Should().ContainLine("A:caught");
        uno.Serial.Should().ContainLine("A:fin");
        uno.Serial.Should().ContainLine("B:ok");
        uno.Serial.Should().ContainLine("B:fin");
        uno.Serial.Should().ContainLine("DONE");
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}

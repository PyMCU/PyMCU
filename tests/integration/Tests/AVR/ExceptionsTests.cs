// SPDX-License-Identifier: MIT
using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for the exceptions-basic fixture.
/// Verifies that try/except/raise using avr-libc setjmp/longjmp works correctly.
/// </summary>
[TestFixture]
public class ExceptionsTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("exceptions-basic"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "EXNS");
        uno.Serial.Should().ContainLine("EXNS");
    }

    [Test]
    public void Raise_CaughtByExcept()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "A:caught\n", maxMs: 2000);
        uno.Serial.Should().ContainLine("A:caught");
        uno.Serial.Should().NotContain("A:missed");
    }

    [Test]
    public void NoRaise_ExceptNotTriggered()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "B:ok\n", maxMs: 2000);
        uno.Serial.Should().ContainLine("B:ok");
        uno.Serial.Should().NotContain("B:caught");
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}

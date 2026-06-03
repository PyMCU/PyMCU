using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for the type-inference fixture.
/// Verifies that unannotated variable assignments infer the correct DataType
/// from the RHS rather than defaulting to uint8.
/// </summary>
[TestFixture]
public class TypeInferenceTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("type-inference"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "INFER");
        uno.Serial.Should().ContainLine("INFER");
    }

    [Test]
    public void UnannotatedFromUint16Function_NotTruncated()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "A:PASS\n", maxMs: 2000);
        uno.Serial.Should().ContainLine("A:PASS");
        uno.Serial.Should().NotContain("A:FAIL");
    }

    [Test]
    public void UnannotatedIntegerLiteralOver255_Preserved()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "B:PASS\n", maxMs: 2000);
        uno.Serial.Should().ContainLine("B:PASS");
        uno.Serial.Should().NotContain("B:FAIL");
    }

    [Test]
    public void UnannotatedFromArithmeticExpression_Preserved()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "C:PASS\n", maxMs: 2000);
        uno.Serial.Should().ContainLine("C:PASS");
        uno.Serial.Should().NotContain("C:FAIL");
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}

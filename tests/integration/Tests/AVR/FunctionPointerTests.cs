using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for function pointer support via Callable-typed variables.
/// Fixture: tests/integration/fixtures/avr/funcptr
/// Boot: "FUNCPTR\n", then:
///   byte 0x0B (11) — add_one(10) via ICALL
///   byte 0x0C (12) — add_two(10) via ICALL
///   byte 0x16 (22) — add_two(20) via ICALL
///   byte 0x0A ('\n')
/// </summary>
[TestFixture]
public class FunctionPointerTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("funcptr"));

    [Test]
    public void Boot_SendsFuncptrBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "FUNCPTR");
        uno.Serial.Should().ContainLine("FUNCPTR");
    }

    [Test]
    public void AddOne_ViaCallable_Returns11()
    {
        var uno = Sim();
        // "FUNCPTR\n" = 8 bytes, then 4 result bytes
        uno.RunUntilSerialBytes(uno.Serial, 12, maxMs: 200);
        var result = uno.Serial.Bytes[8];
        result.Should().Be(11, "add_one(10) called via Callable fn should return 11");
    }

    [Test]
    public void AddTwo_ViaCallable_Returns12()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 12, maxMs: 200);
        var result = uno.Serial.Bytes[9];
        result.Should().Be(12, "add_two(10) called via Callable fn2 should return 12");
    }

    [Test]
    public void AddTwo_SecondCallable_Returns22()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 12, maxMs: 200);
        var result = uno.Serial.Bytes[10];
        result.Should().Be(22, "add_two(20) called via Callable fn3 should return 22");
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}

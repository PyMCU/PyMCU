using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/static-method.
///
/// Verifies that @staticmethod on @inline ZCA class methods is accepted and
/// produces correct code when called as Class.method().
///
/// Checkpoint 1: Math.clamp(200, 10, 100) → 100 = 0x64
///   GPIOR0 = 0x64
///
/// Checkpoint 2: Math.abs_diff(30, 7) → 23 = 0x17
///   GPIOR0 = 0x17
///
/// Checkpoint 3: Math.clamp(5, 10, 100) → 10 = 0x0A (below lower bound)
///   GPIOR0 = 0x0A
///
/// Data-space address (ATmega328P): GPIOR0 = 0x3E
/// </summary>
[TestFixture]
public class StaticMethodTests
{
    private const int Gpior0Addr = 0x3E;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("static-method"));

    private ArduinoUnoSimulation Boot() => _session.Reset();

    [Test]
    public void Cp1_Clamp_AboveUpperBound_ReturnsUpperBound()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x64,
            "Math.clamp(200, 10, 100) should return 100 = 0x64");
    }

    [Test]
    public void Cp2_AbsDiff_ReturnsCorrectDifference()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x17,
            "Math.abs_diff(30, 7) should return 23 = 0x17");
    }

    [Test]
    public void Cp3_Clamp_BelowLowerBound_ReturnsLowerBound()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x0A,
            "Math.clamp(5, 10, 100) should return 10 = 0x0A (below lower bound)");
    }
}

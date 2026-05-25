using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/reversed-named-array.
///
/// Verifies the reversed() code path where the argument is a named array
/// variable (not an inline list literal).  The compiler resolves the array
/// from arraySizes and iterates the constant element slots in reverse order.
///
/// Checkpoint 1: reversed(vals) where vals = [5, 10, 15, 20]
///   Iteration order: 20, 15, 10, 5  →  sum = 50 = 0x32
///   GPIOR0 = 0x32
///
/// Checkpoint 2: reversed(trio) where trio = [7, 14, 21]
///   Iteration order: 21, 14, 7  →  sum = 42 = 0x2A
///   GPIOR0 = 0x2A
///
/// Data-space address (ATmega328P): GPIOR0 = 0x3E
/// </summary>
[TestFixture]
public class ReversedNamedArrayTests
{
    private const int Gpior0Addr = 0x3E;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _session = new SimSession(PymcuCompiler.BuildFixture("reversed-named-array"));

    private ArduinoUnoSimulation Boot() => _session.Reset();

    [Test]
    public void Cp1_Reversed_FourElementArray_SumsCorrectly()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x32,
            "reversed([5,10,15,20]) iterated as 20+15+10+5 should sum to 50 = 0x32");
    }

    [Test]
    public void Cp2_Reversed_ThreeElementArray_SumsCorrectly()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x2A,
            "reversed([7,14,21]) iterated as 21+14+7 should sum to 42 = 0x2A");
    }
}

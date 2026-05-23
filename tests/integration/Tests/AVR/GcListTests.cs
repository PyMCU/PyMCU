using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/gc-list.
///
/// Verifies end-to-end list[T] heap-allocated list operations:
///   - list() allocates a GC-managed list with default capacity
///   - append() adds elements to the list (fast path)
///   - len() returns the correct runtime length
///   - x[i] indexing reads the correct element
///   - for v in x: iterates all elements in order
///   - Program reaches DONE without crashing
/// </summary>
[TestFixture]
public class GcListTests
{
    private static SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _session = new SimSession(PymcuCompiler.BuildFixture("gc-list"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, "LIST\n", maxMs: 300);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("LIST");

    [Test]
    public void List_Len_IsCorrect()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("L:Y\n") || s.Contains("L:N\n"), maxMs: 500);
        uno.Serial.Text.Should().Contain("L:Y", "len(x) should be 3 after 3 appends");
    }

    [Test]
    public void List_Index_IsCorrect()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("I:Y\n") || s.Contains("I:N\n"), maxMs: 600);
        uno.Serial.Text.Should().Contain("I:Y", "x[1] should be 20");
    }

    [Test]
    public void List_ForIn_Sum_IsCorrect()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("S:Y\n") || s.Contains("S:N\n"), maxMs: 700);
        uno.Serial.Text.Should().Contain("S:Y", "sum of [10,20,30] via for-in should be 60");
    }

    [Test]
    public void Program_ReachesDone_WithoutCrash()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("DONE\n"), maxMs: 800);
        uno.Serial.Text.Should().Contain("DONE", "program should reach DONE without crashing");
    }

    [Test]
    public void Program_AllChecksPass()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("DONE\n"), maxMs: 800);
        var text = uno.Serial.Text;
        text.Should().Contain("L:Y", "len check must pass");
        text.Should().Contain("I:Y", "index check must pass");
        text.Should().Contain("S:Y", "sum check must pass");
        text.Should().Contain("DONE");
        text.Should().NotContain("L:N");
        text.Should().NotContain("I:N");
        text.Should().NotContain("S:N");
    }
}

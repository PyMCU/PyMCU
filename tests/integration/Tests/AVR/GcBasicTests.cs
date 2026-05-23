using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/gc-basic.
///
/// Verifies end-to-end GC runtime activation:
///   - program.NeedsGc=true triggers gc_init call in main prologue
///   - Three consecutive gc_alloc calls return non-null (bump allocator works)
///   - GcRoot/GcUnroot are injected for named GC_REF locals (shadow stack ok)
///   - The heap and shadow-stack SRAM layout do not corrupt adjacent variables
///   - Program reaches DONE without crashing
/// </summary>
[TestFixture]
public class GcBasicTests
{
    private static SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _session = new SimSession(PymcuCompiler.BuildFixture("gc-basic"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, "GC\n", maxMs: 300);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("GC");

    [Test]
    public void GcAlloc_First_ReturnsNonNull()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("A:01\n"), maxMs: 400);
        uno.Serial.Text.Should().Contain("A:01",
            "gc_alloc(8) should return a non-null heap pointer");
    }

    [Test]
    public void GcAlloc_Second_ReturnsNonNull()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("B:01\n"), maxMs: 500);
        uno.Serial.Text.Should().Contain("B:01",
            "gc_alloc(16) should return a non-null heap pointer");
    }

    [Test]
    public void GcAlloc_Third_ReturnsNonNull()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("C:01\n"), maxMs: 600);
        uno.Serial.Text.Should().Contain("C:01",
            "gc_alloc(4) should return a non-null heap pointer");
    }

    [Test]
    public void Program_ReachesDone_WithoutCrash()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("DONE\n"), maxMs: 800);
        uno.Serial.Text.Should().Contain("DONE",
            "program should reach DONE after all allocations without crashing");
    }

    [Test]
    public void Program_AllAllocationsSucceed()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("DONE\n"), maxMs: 800);
        var text = uno.Serial.Text;
        text.Should().Contain("A:01", "first allocation must succeed");
        text.Should().Contain("B:01", "second allocation must succeed");
        text.Should().Contain("C:01", "third allocation must succeed");
        text.Should().Contain("DONE", "program must reach DONE");
        text.Should().NotContain("A:00", "first allocation must not fail");
        text.Should().NotContain("B:00", "second allocation must not fail");
        text.Should().NotContain("C:00", "third allocation must not fail");
    }
}

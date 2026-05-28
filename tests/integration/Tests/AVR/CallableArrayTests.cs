using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for Callable[N] array of function pointers.
/// Fixture: tests/integration/fixtures/avr/callable-array
///
/// Verifies that a Callable[N] SRAM array can be declared with function-name
/// initializers and dispatched via ICALL with both constant and variable indices.
///
/// Data-space address: GPIOR0 = 0x3E
///
/// Checkpoints:
///   1 — _tasks[0]() constant index → GPIOR0 = 0xAA
///   2 — _tasks[1]() constant index → GPIOR0 = 0xBB
///   3 — _tasks[idx]() variable index idx=0 → GPIOR0 = 0xAA
///   4 — _tasks[idx]() variable index idx=1 → GPIOR0 = 0xBB
/// </summary>
[TestFixture]
public class CallableArrayTests
{
    private SimSession _session = null!;

    private const int GPIOR0_ADDR = 0x3E;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("callable-array"));

    private static void SkipBreaks(ArduinoUnoSimulation uno, int count)
    {
        for (var i = 0; i < count; i++)
        {
            uno.RunToBreak();
            uno.RunInstructions(1);
        }
    }

    private ArduinoUnoSimulation Boot() => _session.Reset();

    [Test]
    public void Break1_ConstantIndex0_CallsSetAa()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(0xAA,
            "GPIOR0 must be 0xAA after _tasks[0]() via constant index calls set_aa");
    }

    [Test]
    public void Break2_ConstantIndex1_CallsSetBb()
    {
        var uno = Boot();
        SkipBreaks(uno, 1);
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(0xBB,
            "GPIOR0 must be 0xBB after _tasks[1]() via constant index calls set_bb");
    }

    [Test]
    public void Break3_VariableIndex0_CallsSetAa()
    {
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(0xAA,
            "GPIOR0 must be 0xAA after _tasks[idx]() with idx=0 calls set_aa");
    }

    [Test]
    public void Break4_VariableIndex1_CallsSetBb()
    {
        var uno = Boot();
        SkipBreaks(uno, 3);
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(0xBB,
            "GPIOR0 must be 0xBB after _tasks[idx]() with idx=1 calls set_bb");
    }
}

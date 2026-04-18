using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/inline-multisite.
///
/// Validates that @inline functions containing conditional branches can be called
/// at multiple call sites within the same function scope without producing duplicate
/// assembler labels. Each call site must expand to a unique set of labels.
///
/// The fixture uses three @inline functions (min_u8, clamp, abs_diff), each called
/// three times with runtime arguments loaded from hardware I/O registers immediately
/// before the call site. This prevents constant folding by the IRGenerator and
/// ensures real BRLO/BRSH branch instructions are emitted at every site.
///
/// Checkpoints:
///   1 — min_u8 at 3 sites:  min(10,20)=10, min(30,5)=5, min(7,7)=7
///   2 — clamp at 3 sites:   clamp(200,10,100)=100, clamp(5,10,100)=10, clamp(50,10,100)=50
///   3 — abs_diff at 3 sites: abs(10,3)=7, abs(3,10)=7, abs(5,5)=0
///
/// Data-space addresses:
///   GPIOR0 = 0x3E   GPIOR1 = 0x4A   GPIOR2 = 0x4B
/// </summary>
[TestFixture]
public class InlineMultisiteTests
{
    private SimSession _session = null!;

    private const int GPIOR0_ADDR = 0x3E;
    private const int GPIOR1_ADDR = 0x4A;
    private const int GPIOR2_ADDR = 0x4B;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("inline-multisite"));

    private static void SkipBreaks(ArduinoUnoSimulation uno, int count)
    {
        for (var i = 0; i < count; i++)
        {
            uno.RunToBreak();
            uno.RunInstructions(1);
        }
    }

    private ArduinoUnoSimulation Boot() => _session.Reset();

    // --- Checkpoint 1: min_u8 ---

    [Test]
    public void Cp1_MinU8_Site1_AlessB_Returns10()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(10,
            "min_u8(10, 20): a < b path; result must be a=10");
    }

    [Test]
    public void Cp1_MinU8_Site2_BlessA_Returns5()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[GPIOR1_ADDR].Should().Be(5,
            "min_u8(30, 5): b < a path; result must be b=5");
    }

    [Test]
    public void Cp1_MinU8_Site3_Equal_Returns7()
    {
        // a == b: a is not < b, so takes the 'return b' path; both are 7.
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[GPIOR2_ADDR].Should().Be(7,
            "min_u8(7, 7): equal values; result must be 7");
    }

    // --- Checkpoint 2: clamp ---

    [Test]
    public void Cp2_Clamp_AboveHi_ReturnsHi()
    {
        // clamp(200, lo=10, hi=100): x > hi path -> returns 100
        var uno = Boot();
        SkipBreaks(uno, 1);
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(100,
            "clamp(200, 10, 100): x > hi path must clamp to hi=100");
    }

    [Test]
    public void Cp2_Clamp_BelowLo_ReturnsLo()
    {
        // clamp(5, lo=10, hi=100): x < lo path -> returns 10
        var uno = Boot();
        SkipBreaks(uno, 1);
        uno.RunToBreak();
        uno.Data[GPIOR1_ADDR].Should().Be(10,
            "clamp(5, 10, 100): x < lo path must clamp to lo=10");
    }

    [Test]
    public void Cp2_Clamp_InRange_ReturnsX()
    {
        // clamp(50, lo=10, hi=100): in-range path -> returns 50
        var uno = Boot();
        SkipBreaks(uno, 1);
        uno.RunToBreak();
        uno.Data[GPIOR2_ADDR].Should().Be(50,
            "clamp(50, 10, 100): in-range path must return x=50 unchanged");
    }

    // --- Checkpoint 3: abs_diff ---

    [Test]
    public void Cp3_AbsDiff_AgreaterB_Returns7()
    {
        // abs_diff(10, 3): a >= b path -> returns a - b = 7
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(7,
            "abs_diff(10, 3): a>=b path must return a-b=7");
    }

    [Test]
    public void Cp3_AbsDiff_BgreaterA_Returns7()
    {
        // abs_diff(3, 10): b > a path -> returns b - a = 7
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        uno.Data[GPIOR1_ADDR].Should().Be(7,
            "abs_diff(3, 10): b>a path must return b-a=7");
    }

    [Test]
    public void Cp3_AbsDiff_Equal_Returns0()
    {
        // abs_diff(5, 5): a == b, takes a>=b path -> returns a - b = 0
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        uno.Data[GPIOR2_ADDR].Should().Be(0,
            "abs_diff(5, 5): equal values must return 0");
    }
}

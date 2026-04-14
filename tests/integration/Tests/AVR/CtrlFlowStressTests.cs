using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/ctrl-flow-stress.
///
/// Stress-tests control flow features that hit different compiler paths:
///
///   1. continue statement inside while — jumps back to loop header (ContinueLabel),
///      skipping the rest of the loop body.
///   2. Nested while loops — verifies that two independently scoped loop stacks
///      produce correct iteration counts.
///   3. 4-argument function — exercises all four AVR calling-convention argument
///      registers (R24 / R22 / R20 / R18) with no overflow.
///   4. 4-argument function with overflow — same call path; result wraps mod 256.
///   5. Multiple early-return paths — classify() has three distinct return sites;
///      all three must be reachable and return the correct discriminator value.
///
/// Data-space addresses:
///   GPIOR0 = 0x3E   GPIOR1 = 0x4A   GPIOR2 = 0x4B
/// </summary>
[TestFixture]
public class CtrlFlowStressTests
{
    private string _hex = null!;

    private const int GPIOR0_ADDR = 0x3E;
    private const int GPIOR1_ADDR = 0x4A;
    private const int GPIOR2_ADDR = 0x4B;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("ctrl-flow-stress");

    /// <summary>Advances the simulation through N complete BREAK checkpoints.</summary>
    private static void SkipBreaks(ArduinoUnoSimulation uno, int count)
    {
        for (var i = 0; i < count; i++)
        {
            uno.RunToBreak();
            uno.RunInstructions(1); // step over the BREAK opcode
        }
    }

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }

    // --- Checkpoint 1: continue ---

    [Test]
    public void Cp1_ContinueSkipsEvenNumbers_SumOfOddsIs25()
    {
        // j iterates 1..10; even j values are skipped via continue.
        // Odd values: 1+3+5+7+9 = 25.
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(25,
            "continue must skip even j; sum of odd 1..9 is 25");
    }

    // --- Checkpoint 2: nested while ---

    [Test]
    public void Cp2_NestedWhile_3x4_CountIs12()
    {
        // outer loops 3 times; inner loops 4 times per outer iteration => 12 total.
        var uno = Boot();
        SkipBreaks(uno, 1);
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(12,
            "nested while (3 outer x 4 inner) must produce 12 total increments");
    }

    // --- Checkpoint 3: 4-arg call, no overflow ---

    [Test]
    public void Cp3_Sum4_NoOverflow_Is100()
    {
        // sum4(10, 20, 30, 40): exercises R24/R22/R20/R18 argument registers.
        // 10+20+30+40 = 100; no uint8 overflow.
        var uno = Boot();
        SkipBreaks(uno, 2);
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(100,
            "sum4(10,20,30,40) must equal 100; verifies 4-arg calling convention");
    }

    // --- Checkpoint 4: 4-arg call with overflow ---

    [Test]
    public void Cp4_Sum4_WithOverflow_Wraps()
    {
        // sum4(200, 100, 0, 0) = 300; 300 mod 256 = 44 (0x2C).
        var uno = Boot();
        SkipBreaks(uno, 3);
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(44,
            "sum4(200,100,0,0)=300 must wrap to 44 (0x2C) in uint8");
    }

    // --- Checkpoint 5: early-return paths ---

    [Test]
    public void Cp5_ClassifyBelow_Returns1()
    {
        // classify(5): x < 10 -> first return site -> 1
        var uno = Boot();
        SkipBreaks(uno, 4);
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(1,
            "classify(5) must take the x<10 early-return path and return 1");
    }

    [Test]
    public void Cp5_ClassifyInRange_Returns2()
    {
        // classify(50): 10 <= x <= 100 -> fall-through return -> 2
        var uno = Boot();
        SkipBreaks(uno, 4);
        uno.RunToBreak();
        uno.Data[GPIOR1_ADDR].Should().Be(2,
            "classify(50) must fall through to the in-range return and return 2");
    }

    [Test]
    public void Cp5_ClassifyAbove_Returns3()
    {
        // classify(200): x > 100 -> second return site -> 3
        var uno = Boot();
        SkipBreaks(uno, 4);
        uno.RunToBreak();
        uno.Data[GPIOR2_ADDR].Should().Be(3,
            "classify(200) must take the x>100 early-return path and return 3");
    }

    [Test]
    public void Cp5_AllEarlyReturnPaths_Correct()
    {
        // Combined: all three classify() return sites in a single assertion.
        var uno = Boot();
        SkipBreaks(uno, 4);
        uno.RunToBreak();
        uno.Data[GPIOR0_ADDR].Should().Be(1, "classify(5)   -> 1 (x<10)");
        uno.Data[GPIOR1_ADDR].Should().Be(2, "classify(50)  -> 2 (in-range)");
        uno.Data[GPIOR2_ADDR].Should().Be(3, "classify(200) -> 3 (x>100)");
    }
}

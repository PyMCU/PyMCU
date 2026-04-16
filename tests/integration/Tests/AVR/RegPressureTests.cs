using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/reg-pressure.
///
/// Verifies the three-tier variable storage fallback in AvrCodeGen:
///   R4-R15 (global named, via AvrRegisterAllocator)
///     -> R16-R17 (per-function temporaries, via AvrLinearScan)
///       -> Y+offset (stack spill, via StackAllocator / LDD / STD)
///
/// The fixture creates 13 unique named variables across main() + helper():
///   main:   v0..v9 (10 vars), lo_sum, hi_sum, result  = 13
///   helper: a, b, mid                                  = 3
/// Total distinct names = 13 live in main scope. AvrRegisterAllocator assigns
/// R4-R15 (12 slots); the 13th and beyond spill to Y+offset.
///
/// Variables v0..v7 land in R8..R15; v8, v9 spill to STD Y+9, Y+10;
/// result, lo_sum, hi_sum also spill. After the CALL to helper(), all spilled
/// variables must be read correctly via LDD Y+n.
///
/// make_val(n) is a non-inline passthrough that forces each vN to be a runtime
/// value from the IR's perspective, preventing constant folding and ensuring
/// the allocator actually assigns/spills them.
///
/// Checkpoint:
///   1 — result=12, lo_sum=150, hi_sum=144
///       (verified via GPIOR0/GPIOR1/GPIOR2 before BREAK)
///
/// Data-space addresses:
///   GPIOR0 = 0x3E   GPIOR1 = 0x4A   GPIOR2 = 0x4B
/// </summary>
[TestFixture]
public class RegPressureTests
{
    private string _hex = null!;

    private const int GPIOR0_ADDR = 0x3E;
    private const int GPIOR1_ADDR = 0x4A;
    private const int GPIOR2_ADDR = 0x4B;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("reg-pressure");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunToBreak();
        return uno;
    }

    [Test]
    public void Result_AfterHelperCall_Is12()
    {
        // helper(5, 7) = mid = 5+7 = 12.
        // result spills to Y+offset; must be correctly read via LDD before OUT.
        Boot().Data[GPIOR0_ADDR].Should().Be(12,
            "helper(5,7) must return 12; result is stack-spilled and read via LDD");
    }

    [Test]
    public void LoSum_V0toV4_SurvivedCall_Is150()
    {
        // v0+v1+v2+v3+v4 = 10+20+30+40+50 = 150.
        // These vars are register-allocated (R8..R12); must survive CALL to helper().
        Boot().Data[GPIOR1_ADDR].Should().Be(150,
            "v0..v4 (R8-R12) must survive the CALL to helper(); sum must be 150");
    }

    [Test]
    public void HiSum_V5toV9_SurvivedCall_Is144()
    {
        // v5+v6+v7 in R13-R15; v8+v9 spilled to Y+9, Y+10.
        // 60+70+80+90+100 = 400; 400 mod 256 = 144 (0x90).
        Boot().Data[GPIOR2_ADDR].Should().Be(144,
            "v5..v9 (R13-R15 + LDD Y+9/Y+10) must survive CALL; sum 400 mod 256 = 144");
    }

    [Test]
    public void AllOutputs_Correct_InSingleBoot()
    {
        // Combined: verifies all three outputs from a single simulation run.
        var uno = Boot();
        uno.Data[GPIOR0_ADDR].Should().Be(12, "result = helper(5,7) = 12");
        uno.Data[GPIOR1_ADDR].Should().Be(150, "lo_sum = v0..v4 = 150");
        uno.Data[GPIOR2_ADDR].Should().Be(144, "hi_sum = v5..v9 mod 256 = 144");
    }
}

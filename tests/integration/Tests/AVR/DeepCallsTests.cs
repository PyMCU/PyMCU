using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/deep-calls.
///
/// Verifies callee-saved register preservation across a 3-level non-inline
/// call chain: main() -> middle(1) -> inner(1).
///
/// Each function assigns a magic byte to a local variable BEFORE calling the
/// next level, then stores that local to a GPIOR register AFTER the callee
/// returns. If the compiler's register allocator does not properly save and
/// restore R4-R15 across call boundaries, an outer local will be overwritten
/// by an inner function that reuses the same physical register.
///
/// Expected values after the single BREAK in main():
///   GPIOR0 = 0xAA  (main's local_main, must survive call to middle)
///   GPIOR1 = 0xBB  (middle's local_middle, must survive call to inner)
///   GPIOR2 = 0x88  (computed return value: 1 + 0xCC + 0xBB = 0x188 -> 0x88)
///
/// Data-space addresses:
///   GPIOR0 = 0x3E   GPIOR1 = 0x4A   GPIOR2 = 0x4B
/// </summary>
[TestFixture]
public class DeepCallsTests
{
    private string _hex = null!;

    // ATmega328P data-space addresses
    private const int GPIOR0_ADDR = 0x3E;
    private const int GPIOR1_ADDR = 0x4A;
    private const int GPIOR2_ADDR = 0x4B;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("deep-calls");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunToBreak();
        return uno;
    }

    [Test]
    public void MainLocal_NotCorrupted_After_3LevelReturn()
    {
        // main() sets local_main = 0xAA, then calls middle() which calls inner().
        // Both callees use their own locals. After the full call chain returns,
        // main() stores local_main to GPIOR0. It must still be 0xAA.
        Boot().Data[GPIOR0_ADDR].Should().Be(0xAA,
            "main's local_main=0xAA must not be overwritten by middle() or inner()");
    }

    [Test]
    public void MiddleLocal_NotCorrupted_After_InnerReturn()
    {
        // middle() sets local_middle = 0xBB, then calls inner().
        // After inner() returns, middle() stores local_middle to GPIOR1.
        // It must still be 0xBB — not clobbered by inner().
        Boot().Data[GPIOR1_ADDR].Should().Be(0xBB,
            "middle's local_middle=0xBB must not be overwritten by inner()");
    }

    [Test]
    public void ReturnValue_ThroughChain_IsCorrect()
    {
        // inner(1) returns 1 + 0xCC = 0xCD.
        // middle(1) returns 0xCD + 0xBB = 0x188 -> 0x88 (uint8 wrap).
        // main() stores this in GPIOR2.
        Boot().Data[GPIOR2_ADDR].Should().Be(0x88,
            "return value through 3-level chain: 1+0xCC=0xCD, 0xCD+0xBB=0x188->0x88");
    }
}

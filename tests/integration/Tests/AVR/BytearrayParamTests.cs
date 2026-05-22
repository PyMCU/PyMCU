using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/bytearray-param.
///
/// Verifies that a local uint8[N] array can be passed as a bytearray pointer
/// argument to functions, which can then read and write individual bytes via
/// pointer-indirect addressing (buf[i]).
///
/// Checkpoints (ATmega328P data-space):
///   GPIOR0 (0x3E) = buf[0] after fill_ascending(0x10) = 0x10
///   GPIOR1 (0x4A) = buf[2] after fill_ascending(0x10) = 0x12
///   GPIOR2 (0x4B) = sum_buf(buf) = 0x10 + 0x11 + 0x12 = 0x33
/// </summary>
[TestFixture]
public class BytearrayParamTests
{
    private const int Gpior0Addr = 0x3E;
    private const int Gpior1Addr = 0x4A;
    private const int Gpior2Addr = 0x4B;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("bytearray-param"));

    private ArduinoUnoSimulation Boot() => _session.Reset();

    [Test]
    public void Buf0_After_Fill_Is0x10()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x10,
            "buf[0] should be 0x10 after fill_ascending(0x10)");
    }

    [Test]
    public void Buf2_After_Fill_Is0x12()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior1Addr].Should().Be(0x12,
            "buf[2] should be 0x12 after fill_ascending(0x10)");
    }

    [Test]
    public void Sum_Of_Buf_Is0x33()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior2Addr].Should().Be(0x33,
            "sum_buf should return 0x10 + 0x11 + 0x12 = 0x33");
    }
}

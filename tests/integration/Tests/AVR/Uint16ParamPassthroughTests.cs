using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/uint16-param-passthrough.
///
/// Verifies that uint16 function arguments are fully copied into the function
/// body (both low and high bytes).
///
/// The bug was: the function prologue only emitted one MOV/STD for the low byte
/// (argReg[k] -> dest). The high byte (GetHighReg(argReg[k])) was never copied.
///
/// Checkpoint 1: double_u16(300) = 600 = 0x0258
///   GPIOR0 = 0x58 (low byte = 88), GPIOR1 = 0x02 (high byte = 2)
///
/// Checkpoint 2: pass_u16(0x1234) = 0x1234 (identity)
///   GPIOR0 = 0x34, GPIOR1 = 0x12
///
/// Data-space addresses (ATmega328P):
///   GPIOR0 = 0x3E   GPIOR1 = 0x4A
/// </summary>
[TestFixture]
public class Uint16ParamPassthroughTests
{
    private const int Gpior0Addr = 0x3E;
    private const int Gpior1Addr = 0x4A;

    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("uint16-param-passthrough");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }

    [Test]
    public void Cp1_Double300_LowByte_Is0x58()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x58,
            "double_u16(300) = 600 = 0x0258; low byte must be 0x58");
    }

    [Test]
    public void Cp1_Double300_HighByte_Is0x02()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.Data[Gpior1Addr].Should().Be(0x02,
            "double_u16(300) = 600 = 0x0258; high byte must be 0x02 (was 0 before fix)");
    }

    [Test]
    public void Cp2_PassU16_0x1234_LowByte_Is0x34()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0x34,
            "pass_u16(0x1234) must return 0x1234; low byte = 0x34");
    }

    [Test]
    public void Cp2_PassU16_0x1234_HighByte_Is0x12()
    {
        var uno = Boot();
        uno.RunToBreak();
        uno.RunInstructions(1);
        uno.RunToBreak();
        uno.Data[Gpior1Addr].Should().Be(0x12,
            "pass_u16(0x1234) must return 0x1234; high byte = 0x12 (was 0 before fix)");
    }
}


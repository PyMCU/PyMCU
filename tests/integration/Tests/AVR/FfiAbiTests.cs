using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/ffi-abi.
///
/// Validates the AVR calling convention (ABI) for PyMCU @extern FFI calls.
/// Each C probe function returns one of its arguments so we can verify that
/// PyMCU's codegen placed every positional argument in the correct physical
/// register before the CALL instruction.
///
/// AVR GCC calling convention (avr-gcc, -mmcu=atmega328p):
///   arg0 -> R24    arg1 -> R22    arg2 -> R20    arg3 -> R18
///   uint8 return -> R24
///
/// Expected UART output:
///   "ABI\n"  -- boot banner
///   "0:0A\n" -- abi_echo_arg0(10, 20, 30) = 10 (arg0 via R24)
///   "1:14\n" -- abi_echo_arg1(10, 20, 30) = 20 (arg1 via R22)
///   "2:1E\n" -- abi_echo_arg2(10, 20, 30) = 30 (arg2 via R20)
///   "3:04\n" -- abi_echo_arg3(1, 2, 3, 4) =  4 (arg3 via R18)
///   "S:46\n" -- abi_sub8(100, 30) = 70     (arg order: not 0xBA = swapped)
///   "K:AA\n" -- local 0xAA survives a C call (callee-saved reg preserved)
///   "OK\n"   -- done
/// </summary>
[TestFixture]
public class FfiAbiTests
{
    private static SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.Build("ffi-abi"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, "ABI\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("ABI");

    [Test]
    public void Arg0_ViaR24_EchoedCorrectly()
    {
        // abi_echo_arg0(10, 20, 30) must return 10 (0x0A) — arg0 placed in R24.
        // If arg0 were in R22 or R20 instead, the function would return 20 or 30.
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("0:0A\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("0:0A",
            "abi_echo_arg0(10,20,30) must return 10=0x0A; proves arg0 is in R24");
    }

    [Test]
    public void Arg1_ViaR22_EchoedCorrectly()
    {
        // abi_echo_arg1(10, 20, 30) must return 20 (0x14) — arg1 placed in R22.
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("1:14\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("1:14",
            "abi_echo_arg1(10,20,30) must return 20=0x14; proves arg1 is in R22");
    }

    [Test]
    public void Arg2_ViaR20_EchoedCorrectly()
    {
        // abi_echo_arg2(10, 20, 30) must return 30 (0x1E) — arg2 placed in R20.
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("2:1E\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("2:1E",
            "abi_echo_arg2(10,20,30) must return 30=0x1E; proves arg2 is in R20");
    }

    [Test]
    public void Arg3_ViaR18_EchoedCorrectly()
    {
        // abi_echo_arg3(1, 2, 3, 4) must return 4 (0x04) — arg3 placed in R18.
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("3:04\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("3:04",
            "abi_echo_arg3(1,2,3,4) must return 4=0x04; proves arg3 is in R18");
    }

    [Test]
    public void ArgOrder_NonCommutative_Sub100Minus30_Is70()
    {
        // abi_sub8(a, b) = a - b. With (100, 30): correct = 70 = 0x46.
        // If PyMCU swapped arg0/arg1: (30 - 100) mod 256 = 186 = 0xBA.
        // This test catches argument-order reversal regardless of which
        // specific registers are used.
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("S:46\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("S:46",
            "abi_sub8(100,30) must return 70=0x46; swapped args would give 186=0xBA");
        uno.Serial.Text.Should().NotContain("S:BA",
            "0xBA = 186 would indicate args 0 and 1 were passed in reverse order");
    }

    [Test]
    public void PostCall_CalleeSavedReg_NotCorruptedByCAbi()
    {
        // A local variable (stored in a callee-saved register R4-R15 by the
        // PyMCU codegen) is set to 0xAA before a C call, then read afterwards.
        // The C function (abi_sub8) uses working registers; avr-gcc is responsible
        // for saving and restoring any callee-saved registers it touches.
        // If the register is corrupted, 'K' will not be 0xAA.
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("K:AA\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("K:AA",
            "local variable 0xAA must survive the C call; callee-saved regs must be preserved by the C ABI");
    }

    [Test]
    public void AllProbes_Done_Marker_Present()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("OK\n"), maxMs: 400);
        uno.Serial.Text.Should().Contain("OK",
            "firmware must print OK after all ABI probe calls complete");
    }
}

using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#483 / PyMCU#492. An 8-bit core reaches a 16-bit peripheral register pair through a
/// single shared TEMP latch. Writing the HIGH byte only fills TEMP; writing the LOW byte is
/// what commits both halves at once. So a 16-bit store must put the high byte out FIRST.
///
/// The compiler did the opposite. A constant store was split here, in the IR, into low-then-
/// high byte copies, so `TCNT1.value = 0x1234` committed the pair as (stale TEMP &lt;&lt; 8) | 0x34
/// and the copy that followed only refilled TEMP. Measured on silicon it read back 0x0334.
/// Nothing in the shipped stdlib tripped on it because the HAL writes the byte halves by hand
/// (hal/avr/timer, hal/avr/pwm), but RFC 0012's grouped surface puts the 16-bit name in reach
/// of user code, and that surface is the one the project promises to keep.
///
/// Reads keep the OPPOSITE order, low byte first, because reading the low byte is what latches
/// the high one; the last test pins that the fix did not flip the read side too.
/// </summary>
public class SixteenBitRegisterStoreOrderTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Code(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).ToList();

    private const string Header =
        "from pymcu.types import ptr, uint8, uint16\n" +
        "\n" +
        "TCNT1: ptr[uint16] = ptr(0x84)\n";

    [Fact]
    public void TheHalvesOfAConstantSixteenBitStore_GoOutHighByteFirst()
    {
        var ir = Gen(Header +
            "def main():\n" +
            "    TCNT1.value = 0x1234\n");

        var stores = Code(ir).OfType<Copy>()
            .Where(c => c.Dst is MemoryAddress { Address: 0x84 or 0x85 })
            .Select(c => ((MemoryAddress)c.Dst).Address + "=" + ((Constant)c.Src).Value)
            .ToList();

        // 0x85 is TCNT1H and goes to TEMP; 0x84 is TCNT1L and commits the pair.
        Assert.Equal(new List<string> { "133=18", "132=52" }, stores);
    }

    [Fact]
    public void ARuntimeSixteenBitStore_StaysOneWideCopyForTheBackendToOrder()
    {
        // The non-constant value is NOT split here: it reaches the backend as one 16-bit
        // copy, and the AVR code generator is what puts the high byte out first. Splitting
        // it here would hide the width from the backend, which is how the constant path
        // grew the bug in the first place.
        var ir = Gen(Header +
            "def main():\n" +
            "    v: uint16 = 0x1234\n" +
            "    TCNT1.value = v\n");

        Assert.Contains(
            Code(ir).OfType<Copy>(),
            c => c.Dst is MemoryAddress { Address: 0x84, Type: DataType.UINT16 }
                 && c.Src is not Constant);
        Assert.DoesNotContain(
            Code(ir).OfType<Copy>(),
            c => c.Dst is MemoryAddress { Address: 0x85 });
    }

    [Fact]
    public void ASixteenBitRegisterRead_StaysOneWideLoad()
    {
        // The read side is not split and must not be: the backend loads low then high,
        // which is the order the latch requires for a READ.
        var ir = Gen(Header +
            "def main():\n" +
            "    c: uint16 = TCNT1.value\n");

        Assert.Contains(
            Code(ir).OfType<Copy>(),
            c => c.Src is MemoryAddress { Address: 0x84, Type: DataType.UINT16 });
        Assert.DoesNotContain(
            Code(ir).OfType<Copy>(),
            c => c.Src is MemoryAddress { Address: 0x85 });
    }
}

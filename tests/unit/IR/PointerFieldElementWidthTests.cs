using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#484 and PyMCU#485. A field holding a register pointer has an ELEMENT WIDTH, and
/// `.value` on it is an access of exactly that width. Three spellings bind such a field:
/// a bare `ptr(addr)`, a `ptr[T]` parameter, and a register name the chip file exports.
/// Only a fourth one -- a `-> ptr[T]`-annotated selector function, which is what the PWM
/// HAL uses -- carried the width, because the width travelled with the return annotation.
///
/// The other three lost it. The field kept whatever the class scan had guessed for it,
/// UINT16, because what a pointer field holds is an address, so every `.value` write became
/// a two-byte store into I/O space: a `tccrb` field at 0x81 wrote TCCR1C at 0x82 on top of
/// it, a `timsk` field at 0x6F wrote TIMSK2. Silent, and against the chip's register map.
///
/// The spelling that would have said the width outright, `self.reg: ptr[uint8] = ...`, was
/// refused instead: the bracket was read as an array size and the message said
/// "Array size 'uint8' is not a compile-time constant" about a program with no array and no
/// size in it. The same subscript on a parameter and at module level was already accepted.
/// </summary>
public class PointerFieldElementWidthTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Code(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).ToList();

    private static List<int> StoreAddresses(ProgramIR ir) =>
        Code(ir).OfType<Copy>()
            .Where(c => c.Dst is MemoryAddress)
            .Select(c => ((MemoryAddress)c.Dst).Address)
            .ToList();

    private const string Header =
        "from pymcu.types import ptr, uint8, uint16, const, inline\n" +
        "\n" +
        "TCCR1B: ptr[uint8] = ptr(0x81)\n" +
        "TCNT1: ptr[uint16] = ptr(0x84)\n";

    [Fact]
    public void AFieldBoundFromABarePtr_WritesOneByte()
    {
        var ir = Gen(Header +
            "class Hw:\n" +
            "    @inline\n" +
            "    def __init__(self, base: const[uint16]):\n" +
            "        self.tccrb = ptr(base + 1)\n" +
            "\n" +
            "def main():\n" +
            "    h = Hw(0x80)\n" +
            "    h.tccrb.value = 3\n");

        // 0x82 is TCCR1C. It used to be written on every `.value` of this field.
        Assert.Equal(new List<int> { 0x81 }, StoreAddresses(ir));
    }

    [Fact]
    public void AFieldBoundFromAPtrParameter_KeepsTheParametersElementWidth()
    {
        var ir = Gen(Header +
            "class Hw:\n" +
            "    @inline\n" +
            "    def __init__(self, tccrb: ptr[uint8], cnt: ptr[uint16]):\n" +
            "        self.tccrb = tccrb\n" +
            "        self.cnt = cnt\n" +
            "\n" +
            "def main():\n" +
            "    h = Hw(TCCR1B, TCNT1)\n" +
            "    h.tccrb.value = 3\n" +
            "    h.cnt.value = 0x1234\n");

        // One byte for the uint8 parameter, both halves for the uint16 one, high first.
        Assert.Equal(new List<int> { 0x81, 0x85, 0x84 }, StoreAddresses(ir));
    }

    [Fact]
    public void AFieldBoundFromARegisterName_KeepsTheNamesElementWidth()
    {
        var ir = Gen(Header +
            "class Hw:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        self.ctrl = TCCR1B\n" +
            "        self.cnt = TCNT1\n" +
            "\n" +
            "def main():\n" +
            "    h = Hw()\n" +
            "    h.ctrl.value = 5\n" +
            "    h.cnt.value = 0x1234\n");

        Assert.Equal(new List<int> { 0x81, 0x85, 0x84 }, StoreAddresses(ir));
    }

    [Fact]
    public void AnAnnotatedPointerField_IsAcceptedAndSetsTheWidth()
    {
        // The annotation is what the program says, so it wins over the value's own width:
        // a bare ptr() says UINT8 and `ptr[uint16]` here widens the field to the pair.
        var ir = Gen(Header +
            "class B:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        self.reg: ptr[uint8] = TCCR1B\n" +
            "        self.cnt: ptr[uint16] = ptr(0x84)\n" +
            "\n" +
            "def main():\n" +
            "    b = B()\n" +
            "    b.reg.value = 3\n" +
            "    b.cnt.value = 0x1234\n");

        Assert.Equal(new List<int> { 0x81, 0x85, 0x84 }, StoreAddresses(ir));
    }
}

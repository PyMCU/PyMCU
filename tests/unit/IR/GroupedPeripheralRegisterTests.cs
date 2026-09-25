using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// RFC 0012, grouped peripherals. A chip definition exposes every special function register
/// as a loose module-level name, which is a surface the project does not want to promise.
/// The grouped form declares the same registers as attributes of a class named after the
/// peripheral:
///
///     class Timer1:
///         TCCR1A: ptr[uint8] = ptr(0x80)
///         TCNT1: ptr[uint16] = ptr(0x84)
///
///     Timer1.TCCR1A.value = 0x82
///
/// A class-level ptr declaration is a REGISTER, not a class constant: it carries an address
/// AND a width, and both have to survive to the MMIO paths. Folded to a plain Constant it
/// lost the width and the write side refused the Constant target outright.
///
/// The bar is byte identity with the loose spelling, so the last test compares the two
/// programs instruction by instruction rather than asserting on shapes.
/// </summary>
public class GroupedPeripheralRegisterTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string GroupHeader =
        "from pymcu.types import ptr, uint8, uint16\n" +
        "\n" +
        "class Timer1:\n" +
        "    TCCR1A: ptr[uint8] = ptr(0x80)\n" +
        "    TCCR1B: ptr[uint8] = ptr(0x81)\n" +
        "    TCNT1: ptr[uint16] = ptr(0x84)\n" +
        "    OCR1A: ptr[uint16] = ptr(0x88)\n" +
        "    TIFR1: ptr[uint8] = ptr(0x36)\n" +
        "\n";

    private const string LooseHeader =
        "from pymcu.types import ptr, uint8, uint16\n" +
        "\n" +
        "TCCR1A: ptr[uint8] = ptr(0x80)\n" +
        "TCCR1B: ptr[uint8] = ptr(0x81)\n" +
        "TCNT1: ptr[uint16] = ptr(0x84)\n" +
        "OCR1A: ptr[uint16] = ptr(0x88)\n" +
        "TIFR1: ptr[uint8] = ptr(0x36)\n" +
        "\n";

    private static List<Instruction> Code(ProgramIR ir) =>
        ir.Functions
            .SelectMany(f => f.Body)
            .Where(i => i is not DebugLine)
            .ToList();

    [Fact]
    public void AGroupedRegisterWrite_StoresToTheAddressAtItsDeclaredWidth()
    {
        var ir = Gen(GroupHeader +
            "def main():\n" +
            "    Timer1.TCCR1A.value = 0x82\n");

        Assert.Contains(
            Code(ir).OfType<Copy>(),
            c => c.Dst is MemoryAddress { Address: 0x80, Type: DataType.UINT8 }
                 && c.Src is Constant { Value: 0x82 });
    }

    [Fact]
    public void AGroupedSixteenBitRegister_KeepsTheWidthOfItsAnnotation()
    {
        // 1500 = 0x05DC. On AVR a constant 16-bit store splits into the two byte halves,
        // which is only reachable when the symbol carried UINT16 rather than a bare int.
        var ir = Gen(GroupHeader +
            "def main():\n" +
            "    Timer1.OCR1A.value = 1500\n");

        var stores = Code(ir).OfType<Copy>()
            .Where(c => c.Dst is MemoryAddress { Address: 0x88 or 0x89 })
            .Select(c => ((MemoryAddress)c.Dst).Address + "=" + ((Constant)c.Src).Value)
            .ToList();

        Assert.Equal(new List<string> { "136=220", "137=5" }, stores);
    }

    [Fact]
    public void AGroupedRegisterRead_LoadsFromTheAddress()
    {
        var ir = Gen(GroupHeader +
            "def main():\n" +
            "    c: uint16 = Timer1.TCNT1.value\n");

        Assert.Contains(
            Code(ir).OfType<Copy>(),
            c => c.Src is MemoryAddress { Address: 0x84, Type: DataType.UINT16 });
    }

    [Fact]
    public void AGroupedRegisterBitWrite_SetsAndClearsTheBit()
    {
        var ir = Gen(GroupHeader +
            "def main():\n" +
            "    Timer1.TIFR1[0] = 1\n" +
            "    Timer1.TIFR1[2] = 0\n");

        Assert.Contains(Code(ir).OfType<BitSet>(),
            b => b.Target is MemoryAddress { Address: 0x36 } && b.Bit == 0);
        Assert.Contains(Code(ir).OfType<BitClear>(),
            b => b.Target is MemoryAddress { Address: 0x36 } && b.Bit == 2);
    }

    [Fact]
    public void AGroupedRegisterBitRead_TestsTheBit()
    {
        var ir = Gen(GroupHeader +
            "from pymcu.types import uint8\n" +
            "def main():\n" +
            "    if Timer1.TIFR1[0]:\n" +
            "        Timer1.TCCR1B.value = 1\n");

        Assert.Contains(Code(ir).OfType<JumpIfBitClear>(),
            j => j.Source is MemoryAddress { Address: 0x36 } && j.Bit == 0);
    }

    [Fact]
    public void TheGroupedAndLooseSpellings_GenerateTheSameInstructions()
    {
        const string body =
            "def main():\n" +
            "    TCCR1A.value = 0x82\n" +
            "    TCCR1B.value = TCCR1B.value | 0x08\n" +
            "    OCR1A.value = 1500\n" +
            "    TCNT1.value = 0\n" +
            "    TIFR1[0] = 1\n" +
            "    TIFR1[2] = 0\n" +
            "    while True:\n" +
            "        c: uint16 = TCNT1.value\n" +
            "        if TIFR1[5]:\n" +
            "            OCR1A.value = c\n";

        var loose = Code(Gen(LooseHeader + body));
        var grouped = Code(Gen(GroupHeader + body
            .Replace("TCCR1A", "Timer1.TCCR1A")
            .Replace("TCCR1B", "Timer1.TCCR1B")
            .Replace("OCR1A", "Timer1.OCR1A")
            .Replace("TCNT1", "Timer1.TCNT1")
            .Replace("TIFR1", "Timer1.TIFR1")));

        Assert.NotEmpty(loose);
        Assert.Equal(
            loose.Select(i => i.ToString()).ToList(),
            grouped.Select(i => i.ToString()).ToList());
    }

    [Fact]
    public void AGroupedRegister_TakesItsAddressFromTheLooseName()
    {
        // One copy of every address. The grouped surface re-groups what the module
        // already declares, so the two spellings cannot drift apart.
        var ir = Gen(
            "from pymcu.types import ptr, uint8, uint16\n" +
            "\n" +
            "TCCR1A: ptr[uint8] = ptr(0x80)\n" +
            "TCNT1: ptr[uint16] = ptr(0x84)\n" +
            "\n" +
            "class Timer1:\n" +
            "    TCCR1A: ptr[uint8] = ptr(TCCR1A)\n" +
            "    TCNT1: ptr[uint16] = ptr(TCNT1)\n" +
            "    TCCR1B: ptr[uint8] = ptr(TCCR1A + 1)\n" +
            "\n" +
            "def main():\n" +
            "    Timer1.TCCR1A.value = 0x82\n" +
            "    Timer1.TCCR1B.value = 0x19\n" +
            "    Timer1.TCNT1.value = 0\n");

        var stores = Code(ir).OfType<Copy>()
            .Where(c => c.Dst is MemoryAddress)
            .Select(c => ((MemoryAddress)c.Dst).Address)
            .ToList();

        // 0x80, 0x81, then the two byte halves of the 16-bit 0x84.
        Assert.Equal(new List<int> { 0x80, 0x81, 0x84, 0x85 }, stores);
    }

    [Fact]
    public void AGroupedRegisterWhoseAddressIsNotKnown_IsRefusedAndNotFiledAsAVariable()
    {
        // It used to fall to the generic fold, throw inside it, and be filed as a
        // mutable class attribute: every write then landed in a dead SRAM byte and
        // nothing was said.
        var ex = Assert.Throws<CompilerError>(() => Gen(
            "from pymcu.types import ptr, uint8\n" +
            "\n" +
            "def base() -> uint8:\n" +
            "    return 0x80\n" +
            "\n" +
            "class Timer1:\n" +
            "    TCCR1A: ptr[uint8] = ptr(base())\n" +
            "\n" +
            "def main():\n" +
            "    Timer1.TCCR1A.value = 0x82\n"));

        Assert.Contains("TCCR1A", ex.Message);
        Assert.Contains("known while compiling", ex.Message);
    }

    [Fact]
    public void ARegisterGroupCannotBeInstantiated_AndSaysSo()
    {
        // The group is a namespace over the silicon, not a type. The call used to be
        // accepted and produce nothing at all: the name still resolved to the group, so
        // the register accesses after it worked and the meaningless call went unsaid.
        var ex = Assert.Throws<CompilerError>(() => Gen(GroupHeader +
            "def main():\n" +
            "    t = Timer1()\n" +
            "    t.TCCR1A.value = 0x82\n"));

        Assert.Contains("Timer1", ex.Message);
        Assert.Contains("not a class to instantiate", ex.Message);
        // The message says what to write instead.
        Assert.Contains("Timer1.<REGISTER>.value", ex.Message);
    }

    [Fact]
    public void AnOrdinaryClassIsStillInstantiable()
    {
        // The refusal is keyed on the class declaring a register, not on being a class.
        var ir = Gen(
            "from pymcu.types import ptr, uint8\n" +
            "\n" +
            "class Counter:\n" +
            "    def __init__(self, n: uint8):\n" +
            "        self.n = n\n" +
            "\n" +
            "def main():\n" +
            "    c = Counter(7)\n");

        Assert.NotEmpty(Code(ir));
    }
}

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `buf[i]` on a parameter with no type annotation used to be read as a REGISTER BIT index
/// rather than an element index, because nothing said otherwise and the register path is where
/// an unrecognised subscript fell through to.
///
/// A run-time index failed to build as "Bit index must be constant for reading" -- a message
/// that names neither the buffer nor the parameter, and describes an operation the program does
/// not contain. A CONSTANT index was worse: it compiled, silently, into a bit test of the
/// buffer's ADDRESS. The callers were never wrong; an array argument is passed by its base
/// address either way.
///
/// The values that come out are measured on the simulator (pymcu-avr fixtures/buffer-param).
/// What is pinned here is which instruction the subscript lowers to, and the one shape that
/// still cannot work.
/// </summary>
public class BufferParamTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(),
                                          new DeviceConfig { Arch = "avr" });
    }

    private static IEnumerable<Instruction> Body(ProgramIR ir, string fn)
        => ir.Functions.Single(f => f.Name == fn).Body;

    private const string Preamble = "from pymcu.types import uint8, ptr\n\n";

    [Fact]
    public void ARuntimeIndexOnAnUnannotatedParameter_Compiles()
    {
        var ir = Gen(Preamble +
                     "buf3: uint8[3] = [1, 2, 3]\n" +
                     "def total(buf, n: uint8) -> uint8:\n" +
                     "    s: uint8 = 0\n" +
                     "    i: uint8 = 0\n" +
                     "    while i < n:\n" +
                     "        s = s + buf[i]\n" +
                     "        i = i + 1\n" +
                     "    return s\n" +
                     "def main():\n" +
                     "    a: uint8 = total(buf3, 3)\n");

        Assert.Contains(Body(ir, "total"), i => i is BytearrayLoad);
    }

    [Fact]
    public void AConstantIndex_LoadsAByte_RatherThanTestingABitOfTheAddress()
    {
        // The silent one: this built, ran, and answered 0 or 1 where a byte was expected.
        var ir = Gen(Preamble +
                     "buf3: uint8[3] = [1, 2, 3]\n" +
                     "def first(buf) -> uint8:\n" +
                     "    return buf[0]\n" +
                     "def main():\n" +
                     "    a: uint8 = first(buf3)\n");

        Assert.Contains(Body(ir, "first"), i => i is BytearrayLoad);
        Assert.DoesNotContain(Body(ir, "first"), i => i is BitCheck);
    }

    [Fact]
    public void WritingThroughAnUnannotatedParameter_StoresAByte()
    {
        var ir = Gen(Preamble +
                     "buf3: uint8[3] = [1, 2, 3]\n" +
                     "def fill(buf, n: uint8):\n" +
                     "    i: uint8 = 0\n" +
                     "    while i < n:\n" +
                     "        buf[i] = i\n" +
                     "        i = i + 1\n" +
                     "def main():\n" +
                     "    fill(buf3, 3)\n");

        Assert.Contains(Body(ir, "fill"), i => i is BytearrayStore);
    }

    [Fact]
    public void AnInlineCalleeReachesAModuleLevelBuffer_NotItsAddressBit()
    {
        // Inside an inline expansion the parameter's alias resolves to the function-qualified
        // name ("main.buf3") while a module array is registered bare ("buf3"), so the lookup
        // missed and the subscript fell through to the bit path.
        var ir = Gen(Preamble +
                     "buf3: uint8[3] = [1, 2, 3]\n" +
                     "@inline\n" +
                     "def first(buf) -> uint8:\n" +
                     "    return buf[0]\n" +
                     "def main():\n" +
                     "    a: uint8 = first(buf3)\n");

        Assert.DoesNotContain(Body(ir, "main"), i => i is BitCheck);
    }

    [Fact]
    public void AParameterThatIsNeverSubscripted_IsLeftAScalar()
    {
        // The inference has to be narrow: only a parameter the body indexes becomes a pointer.
        var ir = Gen(Preamble +
                     "def twice(x) -> uint8:\n" +
                     "    return x + x\n" +
                     "def main():\n" +
                     "    a: uint8 = twice(3)\n");

        Assert.DoesNotContain(Body(ir, "twice"), i => i is BytearrayLoad);
    }

    [Fact]
    public void PassingAChipRegisterToABufferParameter_IsRefusedByName()
    {
        // A register argument passes its CONTENTS. Before, this compiled two different wrong
        // ways depending on whether the index was constant, and never said anything.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            Preamble +
            "PORTB: ptr[uint8] = ptr(0x25)\n" +
            "def setbit(reg):\n" +
            "    reg[5] = 1\n" +
            "def main():\n" +
            "    setbit(PORTB)\n"));

        Assert.Contains("PORTB", ex.Message);
        Assert.Contains("reg", ex.Message);
        Assert.Contains("chip register", ex.Message);
        Assert.DoesNotContain("Bit index must be constant", ex.Message);
    }

    [Fact]
    public void AConstantBitIndexOnARegisterItself_StillWorks()
    {
        // The register bit path is not what changed; only what a PARAMETER subscript means.
        var ir = Gen(Preamble +
                     "PORTB: ptr[uint8] = ptr(0x25)\n" +
                     "def main():\n" +
                     "    PORTB[5] = 1\n");

        Assert.Contains(Body(ir, "main"), i => i is BitSet);
    }
}

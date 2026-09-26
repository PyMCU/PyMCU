using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// A bit subscript through a RUNTIME pointer reaches the byte the pointer points at.
//
// `p: ptr[uint8] = ptr(a)` with `a` known only at run time holds an ADDRESS in `p`. The bit
// instructions take their operand as the storage to change, and the subscript lowering handed
// them `p` itself: `p[0] = 1` compiled to `bset g.p, 0`, which set a bit of the address and
// left the register alone, and `p[2]` tested a bit of the address. Both front ends, in a
// function, an @inline expansion and at module level, with nothing said. A run-time bit index
// was refused outright on the same target.
//
// WHAT DISCRIMINATES: every assertion. Against the unfixed compiler the bit instructions name
// the pointer variable and no indirect access is emitted.
public class RuntimePointerBitTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR1\n" +
        "from pymcu.types import uint8, uint16, ptr\n\n";

    private static bool TouchesPointerBits(Function f, string ptrName) =>
        f.Body.Any(i => i switch
        {
            BitSet b => b.Target is Variable { Name: var n } && n == ptrName,
            BitClear b => b.Target is Variable { Name: var n } && n == ptrName,
            BitWrite b => b.Target is Variable { Name: var n } && n == ptrName,
            BitCheck b => b.Source is Variable { Name: var n } && n == ptrName,
            _ => false
        });

    [Fact]
    public void AConstantBitWriteGoesThroughThePointer()
    {
        var ir = Gen(Preamble +
            "def wr(a: uint16):\n" +
            "    p: ptr[uint8] = ptr(a)\n" +
            "    p[0] = 1\n" +
            "    p[1] = 0\n" +
            "wr(uint16(GPIOR1.value) + 0x40)\n");
        var f = ir.Functions.Single(fn => fn.Name == "wr");
        Assert.False(TouchesPointerBits(f, "wr.p"));
        Assert.Contains(f.Body, i => i is LoadIndirect { SrcPtr: Variable { Name: "wr.p" } });
        Assert.Contains(f.Body, i => i is StoreIndirect { DstPtr: Variable { Name: "wr.p" } });
    }

    [Fact]
    public void AConstantBitReadGoesThroughThePointer()
    {
        var ir = Gen(Preamble +
            "def rd(a: uint16) -> uint8:\n" +
            "    p: ptr[uint8] = ptr(a)\n" +
            "    return p[2]\n" +
            "GPIOR1.value = rd(uint16(GPIOR1.value) + 0x40)\n");
        var f = ir.Functions.Single(fn => fn.Name == "rd");
        Assert.False(TouchesPointerBits(f, "rd.p"));
        Assert.Contains(f.Body, i => i is LoadIndirect { SrcPtr: Variable { Name: "rd.p" } });
    }

    [Fact]
    public void ARunTimeBitIndexThroughThePointerIsAReadModifyWrite()
    {
        var ir = Gen(Preamble +
            "def wr(a: uint16, b: uint8):\n" +
            "    p: ptr[uint8] = ptr(a)\n" +
            "    p[b] = 1\n" +
            "wr(uint16(GPIOR1.value) + 0x40, GPIOR1.value)\n");
        var f = ir.Functions.Single(fn => fn.Name == "wr");
        Assert.Contains(f.Body, i => i is LoadIndirect { SrcPtr: Variable { Name: "wr.p" } });
        Assert.Contains(f.Body, i => i is StoreIndirect { DstPtr: Variable { Name: "wr.p" } });
    }
}

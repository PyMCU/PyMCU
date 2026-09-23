using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `x = []` is a compile-time empty sequence while nothing mutates it, but a
/// later `x.append(v)` in the same function means the program wanted a runtime
/// heap list whose element type is only knowable at that append. The binding
/// site must then emit the empty heap object (count and capacity, no payload)
/// and leave the element type pending; the first append resolves it and the
/// grow path sizes the real buffer.
///
/// Without the promotion the append saw an untyped `[]` and refused, which is
/// what unmodified adafruit_irremote does in `read_pulses`:
/// `received = []` inside a `while` arm, then `received.append(pulse)`.
/// </summary>
public class EmptyListPromotionTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void EmptyListThenAppend_AllocatesHeapList()
    {
        var ir = Gen(
            "def f() -> uint16:\n" +
            "    x = []\n" +
            "    x.append(300)\n" +
            "    return x[0]\n" +
            "def main():\n" +
            "    f()\n");
        var fn = Assert.Single(ir.Functions, f => f.Name == "f");
        Assert.Contains(fn.Body, i => i is GcAlloc);
        // The element store must be 2 bytes: 300 learned the list a uint16 element type.
        Assert.Contains(fn.Body, i => i is StoreIndirect { Elem: DataType.UINT16 });
    }

    [Fact]
    public void EmptyListThenAppend_NestedInsideIf()
    {
        var ir = Gen(
            "def f(flag: uint8) -> uint8:\n" +
            "    x = []\n" +
            "    if flag:\n" +
            "        x.append(7)\n" +
            "    return len(x)\n" +
            "def main():\n" +
            "    f(1)\n");
        var fn = Assert.Single(ir.Functions, f => f.Name == "f");
        Assert.Contains(fn.Body, i => i is GcAlloc);
        Assert.Contains(fn.Body, i => i is StoreIndirect { Elem: DataType.UINT8 });
    }

    [Fact]
    public void EmptyListNeverMutated_StaysCompileTime()
    {
        var ir = Gen(
            "x = []\n" +
            "y = len(x)\n");
        Assert.DoesNotContain(ir.Functions.SelectMany(f => f.Body), i => i is GcAlloc);
    }

    [Fact]
    public void EmptyListAppendedInAnotherFunction_StillRefuses()
    {
        // The promotion is same-function only: an append in a different
        // function keeps the honest untyped-[] refusal.
        Assert.Throws<NameError>(() => Gen(
            "x = []\n" +
            "def f():\n" +
            "    x.append(1)\n" +
            "def main():\n" +
            "    f()\n"));
    }

    [Fact]
    public void ModuleLevelEmptyList_LenReadsGlobalSlot()
    {
        // A module-level `p: list[uint16] = []` is a GLOBAL filed bare (`p`). The
        // promotion scan seeded `main.p` too, and ResolveListVarQualified prefers
        // the function-qualified key, so len(p) dereferenced a slot nobody writes
        // while p.append()/p[i] used the real one -- len read 0 on a 3-element list.
        var ir = Gen(
            "p: list[uint16] = []\n" +
            "p.append(9000)\n" +
            "n: uint16 = len(p)\n");
        var fn = Assert.Single(ir.Functions, f => f.Name == "main");
        Assert.Contains(fn.Body, i => i is LoadIndirect { SrcPtr: Variable v }
            && v.Name == "p" && v.Type == DataType.GC_REF);
        Assert.DoesNotContain(fn.Body, i => i is LoadIndirect { SrcPtr: Variable v }
            && v.Name == "main.p");
    }

    [Fact]
    public void ModuleLevelEmptyAssign_LenReadsGlobalSlot()
    {
        // Same phantom through the unannotated `p = []` promotion path.
        var ir = Gen(
            "p = []\n" +
            "p.append(9000)\n" +
            "n: uint16 = len(p)\n");
        var fn = Assert.Single(ir.Functions, f => f.Name == "main");
        Assert.Contains(fn.Body, i => i is LoadIndirect { SrcPtr: Variable v }
            && v.Name == "p" && v.Type == DataType.GC_REF);
        Assert.DoesNotContain(fn.Body, i => i is LoadIndirect { SrcPtr: Variable v }
            && v.Name == "main.p");
    }

    [Fact]
    public void EmptyListThenListAppend_MarksRefPayload()
    {
        // Appending a heap list makes the elements GC_REFs; the object was
        // allocated before the element type was known, so the append must set
        // the ref-bearing bit in the GC mark byte (user_ptr - 2, bit 0x40).
        var ir = Gen(
            "def f() -> uint16:\n" +
            "    x = []\n" +
            "    inner: list[uint16] = [1, 2]\n" +
            "    x.append(inner)\n" +
            "    return x[0][0]\n" +
            "def main():\n" +
            "    f()\n");
        var fn = Assert.Single(ir.Functions, f => f.Name == "f");
        Assert.Contains(fn.Body, i => i is StoreIndirect { Elem: DataType.GC_REF });
        Assert.Contains(fn.Body, i => i is Binary { Op: PyMCU.IR.BinaryOp.BitOr, Src2: Constant c } && c.Value == 0x40);
    }
}

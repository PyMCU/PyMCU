using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Every GC_REF local is pushed onto the GC shadow stack in the function
/// prologue, but the slot only receives its first real value at the point of
/// declaration. Between the prologue and that first store the slot held
/// whatever the stack happened to contain: the mark phase reads every rooted
/// slot, and a garbage value that fell inside [heap_start, heap_top) was
/// marked as a live object, which desynchronised compaction and hung the
/// collector. In the emulator the failure surfaced as adafruit_irremote's
/// decode_bits() freezing on the 17th append -- the first grow allocation
/// that ran a collection with several declared-but-uninitialised list locals
/// on the shadow stack (cp-bisect test03).
///
/// Each prologue GcRoot now also stores null into the slot, so a rooted slot
/// always contains a valid (possibly null) GC_REF.
/// </summary>
public class GcRootInitTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void EveryGcRootIsFollowedByANullStoreToTheSameSlot()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n\n" +
            "def f(p: list[uint16]) -> list[uint16]:\n" +
            "    a: list[uint16] = list()\n" +
            "    for v in p:\n" +
            "        a.append(v)\n" +
            "    b: list[uint16] = list()\n" +
            "    c: list[uint16] = list()\n" +
            "    return a\n\n" +
            "xs: list[uint16] = list()\n" +
            "xs.append(1)\n" +
            "r = f(xs)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        int roots = 0;
        for (int i = 0; i < main.Body.Count; i++)
        {
            if (main.Body[i] is not GcRoot root) continue;
            roots++;
            Assert.True(i + 1 < main.Body.Count);
            var init = Assert.IsType<Copy>(main.Body[i + 1]);
            var src = Assert.IsType<Constant>(init.Src);
            Assert.Equal(0, src.Value);
            var dst = Assert.IsType<Variable>(init.Dst);
            Assert.Equal(Assert.IsType<Variable>(root.Var).Name, dst.Name);
            Assert.Equal(DataType.GC_REF, dst.Type);
        }
        Assert.True(roots > 0);
    }
}

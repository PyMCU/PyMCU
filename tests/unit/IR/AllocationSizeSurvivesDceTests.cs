using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The same class as PyMCU#359, one level up.
///
/// #359 was a hand-written whitelist of instruction kinds sitting beside `GetDst`, which
/// already knew the full set; the fix was to ask `GetDst`. But `GetDst` and the two switches
/// next to it, `RegisterUses` and `ReplaceUses`, ARE the exhaustive sets every other pass
/// delegates to, and a kind missing from one of them is missing from all of its callers at
/// once. Three kinds were: `IndirectCall` and `GcAlloc` from `GetDst` and `ReplaceDst`,
/// `GcAlloc` from `RegisterUses`, and all three plus `SignalError` from `ReplaceUses`.
///
/// `GcAlloc` is the one that was measurably wrong. An allocation READS its size, and nothing
/// said so, so a name whose only reader was an allocation counted as dead:
///
///     n: uint16 = 7
///     sz: uint16 = n * 4 + 2
///     p1 = gc_alloc(sz)
///
/// optimised down to a bare allocation of `main.sz` with every instruction that computes
/// `main.sz` deleted. The allocation asked the heap for whatever the frame held, and the
/// compiler said `[BUILD_OK]`. `PYMCU_NO_OPT=1` computes 30, which is the oracle.
///
/// A list that outgrows its capacity reaches the same allocation with a size two `Binary`
/// instructions produce, so this is a form a program hits without asking for it.
///
/// The assertions are on the OPTIMIZED IR, because the unoptimized IR was always right and a
/// test written against it passes with the bug in place.
/// </summary>
public class AllocationSizeSurvivesDceTests
{
    private static ProgramIR Optimized(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return Optimizer.Optimize(ir);
    }

    private static IEnumerable<Instruction> AllBody(ProgramIR ir) => ir.Functions.SelectMany(f => f.Body);

    private const string RuntimeSizedAlloc =
        "from pymcu.types import uint16\n\n" +
        "def main() -> None:\n" +
        "    n: uint16 = 7\n" +
        "    sz: uint16 = n * 4 + 2\n" +
        "    p1 = gc_alloc(sz)\n" +
        "    p2 = gc_alloc(4)\n" +
        "    a1: uint16 = bitcast(uint16, p1)\n" +
        "    a2: uint16 = bitcast(uint16, p2)\n" +
        "    g: uint16 = a2 - a1\n" +
        "    q = gc_alloc(g)\n";

    [Fact]
    public void ARuntimeAllocationSizeIsStillComputed()
    {
        var ir = Optimized(RuntimeSizedAlloc);

        var alloc = AllBody(ir).OfType<GcAlloc>().First();
        if (alloc.Size is Constant) return;   // folded outright is also an answer, and a correct one

        string sizeName = alloc.Size switch
        {
            Variable v => v.Name,
            Temporary t => t.Name,
            _ => throw new Xunit.Sdk.XunitException($"unexpected size operand {alloc.Size}"),
        };

        Assert.Contains(AllBody(ir), i => NameOfDst(i) == sizeName);
    }

    [Fact]
    public void AnAllocationSizeThatFoldsFoldsToTheRightNumber()
    {
        // 7 * 4 + 2. The oracle is PYMCU_NO_OPT=1, which computes 30 the long way; the pass
        // either keeps the computation or folds it, and both answers are 30. What it must not
        // do is delete the computation and keep the name.
        var ir = Optimized(RuntimeSizedAlloc);

        var alloc = AllBody(ir).OfType<GcAlloc>().First();
        if (alloc.Size is Constant c) { Assert.Equal(30, c.Value); return; }

        string sizeName = alloc.Size switch
        {
            Variable v => v.Name, Temporary t => t.Name, _ => "",
        };
        var def = AllBody(ir).FirstOrDefault(i => NameOfDst(i) == sizeName);
        Assert.NotNull(def);
        if (def is Copy { Src: Constant k }) Assert.Equal(30, k.Value);
    }

    [Fact]
    public void AnAllocationDestinationIsAKnownDefinition()
    {
        // `GetDst` is what the other passes ask, so an allocation absent from it is invisible
        // to liveness, to the stale-constant retirement of #359, and to every caller at once.
        var ir = Optimized(RuntimeSizedAlloc);
        var alloc = AllBody(ir).OfType<GcAlloc>().First();

        Assert.False(alloc.Dst is NoneVal, "an allocation that reaches the backend has a home");
        Assert.NotNull(NameOfDst(alloc));
    }

    private static string? NameOfDst(Instruction i)
    {
        Val? d = i switch
        {
            Binary b => b.Dst,
            Unary u => u.Dst,
            Copy c => c.Dst,
            Bitcast bc => bc.Dst,
            Call cl => cl.Dst,
            IndirectCall ic => ic.Dst,
            BitCheck bck => bck.Dst,
            LoadIndirect li => li.Dst,
            ArrayLoad al => al.Dst,
            ArrayLoadFlash alf => alf.Dst,
            FlashLoadPtr flp => flp.Dst,
            BytearrayLoad bld => bld.Dst,
            GcAlloc ga => ga.Dst,
            AugAssign aa => aa.Target,
            _ => null,
        };
        return d switch { Variable v => v.Name, Temporary t => t.Name, _ => null };
    }
}

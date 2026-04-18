using PyMCU.Backend.Targets.AVR;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Unit tests for AvrLinearScan.Allocate() — the greedy linear-scan register
/// allocator that maps short-lived UINT8 temporaries to R16/R17.
/// </summary>
public class AvrLinearScanTests
{
    private static Dictionary<string, string> Allocate(params Instruction[] body)
    {
        var func = new Function { Name = "test", Body = body.ToList() };
        return AvrLinearScan.Allocate(func);
    }

    // ─── Single temporary ─────────────────────────────────────────────────────

    [Fact]
    public void SingleTemporary_AssignedToR16()
    {
        var t1 = new Temporary("t1");
        var result = Allocate(
            new Copy(new Constant(1), t1),
            new Return(t1));

        Assert.True(result.TryGetValue("t1", out var reg));
        Assert.Equal("R16", reg);
    }

    // ─── Two non-overlapping temporaries (R16 is reused) ─────────────────────

    [Fact]
    public void TwoNonOverlapping_BothAssigned_R16Reused()
    {
        // t1 is last used at instruction 1; t2 starts at instruction 2.
        // They do not overlap, so both can share R16.
        var t1 = new Temporary("t1");
        var t2 = new Temporary("t2");
        var result = Allocate(
            new Copy(new Constant(1), t1),   // 0 — t1 def
            new Return(t1),                   // 1 — t1 last use
            new Copy(new Constant(2), t2),   // 2 — t2 def
            new Return(t2));                  // 3 — t2 last use

        Assert.True(result.ContainsKey("t1"));
        Assert.True(result.ContainsKey("t2"));
        // Non-overlapping intervals: t2 may safely reuse t1's register.
        Assert.Equal(result["t1"], result["t2"]);
    }

    // ─── Two overlapping temporaries (R16 and R17 both used) ─────────────────

    [Fact]
    public void TwoOverlapping_AssignedToR16AndR17()
    {
        var t1 = new Temporary("t1");
        var t2 = new Temporary("t2");
        var dummy = new Temporary("dummy");
        var result = Allocate(
            new Copy(new Constant(1), t1),                               // 0 — t1 def
            new Copy(new Constant(2), t2),                               // 1 — t2 def
            new Binary(BinaryOp.Add, t1, t2, dummy),                     // 2 — t1 and t2 live
            new Return(new Constant(0)));                                 // 3

        Assert.True(result.ContainsKey("t1"));
        Assert.True(result.ContainsKey("t2"));
        // Must be assigned different registers
        Assert.NotEqual(result["t1"], result["t2"]);
        var regs = new HashSet<string> { result["t1"], result["t2"] };
        Assert.Subset(new HashSet<string> { "R16", "R17" }, regs);
    }

    // ─── More than two simultaneous live temporaries ──────────────────────────

    [Fact]
    public void ThreeSimultaneous_AtMostTwoAllocated()
    {
        var t1 = new Temporary("t1");
        var t2 = new Temporary("t2");
        var t3 = new Temporary("t3");
        var r12 = new Temporary("r12");
        var r13 = new Temporary("r13");
        var result = Allocate(
            new Copy(new Constant(1), t1),                    // 0 — t1 def
            new Copy(new Constant(2), t2),                    // 1 — t2 def
            new Copy(new Constant(3), t3),                    // 2 — t3 def
            new Binary(BinaryOp.Add, t1, t2, r12),            // 3 — t1 and t2 used
            new Binary(BinaryOp.Add, t2, t3, r13),            // 4 — t2 and t3 used
            new Return(new Constant(0)));                      // 5

        int allocatedCount = new[] { "t1", "t2", "t3" }
            .Count(name => result.ContainsKey(name));
        // Only 2 registers available; at least one must spill.
        Assert.True(allocatedCount <= 2,
            $"Expected ≤ 2 allocated temporaries, got {allocatedCount}");
    }

    // ─── Temporary spanning a function call is not allocated ─────────────────

    [Fact]
    public void TemporarySpanningCall_NotAllocated()
    {
        // t1 is defined before the Call and used after — it spans the call.
        // Scratch registers R16/R17 are caller-saved by AVR-GCC convention,
        // so the allocator must not place such a temporary there.
        var t1 = new Temporary("t1");
        var result = Allocate(
            new Copy(new Constant(42), t1),                          // 0 — t1 def
            new Call("some_func", new List<Val>(), new NoneVal()),    // 1 — call
            new Return(t1));                                          // 2 — t1 last use

        // Interval for t1: Def=0, LastUse=2, spans call at index 1 → must be spilled.
        Assert.False(result.ContainsKey("t1"),
            "t1 spans a call and must not be allocated to a scratch register");
    }

    // ─── UINT16 temporary is not eligible ────────────────────────────────────

    [Fact]
    public void Uint16Temporary_NotAllocated()
    {
        var t1 = new Temporary("t1", DataType.UINT16);
        var result = Allocate(
            new Copy(new Constant(500), t1),
            new Return(t1));

        Assert.False(result.ContainsKey("t1"),
            "UINT16 temporaries must not be placed in R16/R17 (single-byte scratch regs)");
    }

    // ─── Empty function ───────────────────────────────────────────────────────

    [Fact]
    public void EmptyFunction_ReturnsEmptyDictionary()
        => Assert.Empty(Allocate());

    // ─── Non-Temporary values are ignored ────────────────────────────────────

    [Fact]
    public void OnlyVariables_NoTemporaries_ReturnsEmpty()
    {
        var v = new Variable("x");
        var result = Allocate(
            new Copy(new Constant(1), v),
            new Return(v));

        // Variables are managed by the register allocator / stack, not linear scan.
        Assert.Empty(result);
    }

    // ─── AugAssign source and target are visited ─────────────────────────────

    [Fact]
    public void AugAssign_TemporaryInOperand_Allocated()
    {
        var t1 = new Temporary("t1");
        var target = new Variable("x");
        var result = Allocate(
            new Copy(new Constant(3), t1),            // 0 — t1 def
            new AugAssign(BinaryOp.Add, target, t1),  // 1 — t1 last use (operand)
            new Return(new Constant(0)));              // 2

        Assert.True(result.ContainsKey("t1"),
            "t1 used in AugAssign.Operand should be eligible for allocation");
    }

    // ─── Temporary used only in a comparison jump ─────────────────────────────

    [Fact]
    public void TemporaryInJumpSrc_Allocated()
    {
        var t1 = new Temporary("t1");
        var result = Allocate(
            new Copy(new Constant(5), t1),                                // 0 — t1 def
            new JumpIfEqual(t1, new Constant(5), "done"),                 // 1 — t1 last use
            new Label("done"),                                             // 2
            new Return(new Constant(0)));                                  // 3

        Assert.True(result.ContainsKey("t1"));
    }
}

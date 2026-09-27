// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using PyMCU.Backend.Analysis;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Live-interval packing in StackAllocator: compiler temporaries whose mention
/// spans never overlap share a frame slot; named locals and GcRoot'd slots never
/// do. Assertions are on the offsets the allocator hands the backend -- the
/// sharing is correct only when the intervals genuinely cannot meet.
/// </summary>
public class FramePackingTests
{
    private static Function Sink() => new Function
    {
        Name = "sink",
        Params = { "x" },
        Body = { new Return(new NoneVal()) },
    };

    private static Dictionary<string, int> Offsets(ProgramIR prog)
        => new StackAllocator().Allocate(prog).Offsets;

    private static ProgramIR Prog(List<Instruction> body) => new ProgramIR
    {
        Functions =
        {
            new Function { Name = "main", Body = body },
            Sink(),
        },
    };

    private static Call Use(Val v) =>
        new Call("sink", new List<Val> { v }, new Temporary("sink_r"));

    [Fact]
    public void DisjointTempsShareAFrameSlot()
    {
        // tmp_a dies at the first call, tmp_b is born after it: packing must
        // hand both the same slot, where exclusive allocation burned two.
        var offsets = Offsets(Prog(new List<Instruction>
        {
            new Copy(new Constant(5), new Temporary("tmp_a")),
            Use(new Temporary("tmp_a")),
            new Copy(new Constant(7), new Temporary("tmp_b")),
            Use(new Temporary("tmp_b")),
            new Return(new Constant(0)),
        }));

        Assert.Equal(offsets["tmp_a"], offsets["tmp_b"]);
    }

    [Fact]
    public void OverlappingTempsKeepDistinctSlots()
    {
        // tmp_a is still live when tmp_b is written: same-instruction or
        // overlapping intervals never share.
        var offsets = Offsets(Prog(new List<Instruction>
        {
            new Copy(new Constant(5), new Temporary("tmp_a")),
            new Copy(new Constant(7), new Temporary("tmp_b")),
            Use(new Temporary("tmp_a")),
            Use(new Temporary("tmp_b")),
            new Return(new Constant(0)),
        }));

        Assert.NotEqual(offsets["tmp_a"], offsets["tmp_b"]);
    }

    [Fact]
    public void ANamedLocalKeepsAnExclusiveSlot()
    {
        // Only all-temporary canonical groups pack: a named local's slot is
        // never handed to a later temp whose interval is disjoint.
        var offsets = Offsets(Prog(new List<Instruction>
        {
            new Copy(new Constant(5), new Variable("v")),
            Use(new Variable("v")),
            new Copy(new Constant(7), new Temporary("tmp_b")),
            Use(new Temporary("tmp_b")),
            new Return(new Constant(0)),
        }));

        Assert.NotEqual(offsets["v"], offsets["tmp_b"]);
    }

    [Fact]
    public void ANamedLocalInAVacatedTempSlotKeepsItExclusive()
    {
        // `asin(x)` stages `math_atan2.y` and then `math_atan2.x`, the callee's
        // parameters, one instruction apart; the callee reads both after the
        // call. The first took the slot a dead temp left and freed it at its own
        // last mention, so the second landed on it and atan2 saw y == x. A named
        // local owns a reused slot exactly as it owns a fresh one.
        var offsets = Offsets(Prog(new List<Instruction>
        {
            new Copy(new Constant(5), new Temporary("tmp_a")),
            Use(new Temporary("tmp_a")),
            new Copy(new Constant(7), new Variable("callee.y")),
            new Copy(new Constant(9), new Variable("callee.x")),
            Use(new Variable("s")),
            new Return(new Constant(0)),
        }));

        Assert.NotEqual(offsets["callee.y"], offsets["callee.x"]);
    }

    [Fact]
    public void ARootedSlotKeepsAnExclusiveSlot()
    {
        // A GcRoot'd variable is live for the whole body: the shadow stack
        // reads the slot at every collection between push and unroot, so
        // nothing may share it even though its own mentions end early.
        var offsets = Offsets(Prog(new List<Instruction>
        {
            new GcRoot(new Temporary("gc_t")),
            new Copy(new Constant(5), new Temporary("gc_t")),
            Use(new Temporary("gc_t")),
            new Copy(new Constant(7), new Temporary("tmp_b")),
            Use(new Temporary("tmp_b")),
            new GcUnroot(new Temporary("gc_t")),
            new Return(new Constant(0)),
        }));

        Assert.NotEqual(offsets["gc_t"], offsets["tmp_b"]);
    }
}

using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The garbage-collected heap behind list[T]: GcAlloc, GcRoot and GcUnroot call into the
/// runtime the backend carries (compiled from src/gc/gc.c, whose behaviour the host test in
/// tests/riscv_runtimes pins). The runtime is emitted only for a program that uses it.
/// </summary>
public class RISCVGcTests
{
    private static readonly DeviceConfig Ch32v003 = new() { Chip = "ch32v003", Arch = "ch32v" };

    private static string Compile(ProgramIR p)
    {
        var sw = new StringWriter();
        new RiscvCodeGen(Ch32v003).Compile(p, sw);
        return sw.ToString();
    }

    private static ProgramIR Program(params Instruction[] body)
    {
        var p = new ProgramIR();
        p.Functions.Add(new Function { Name = "main", Body = body.ToList() });
        return p;
    }

    private static Variable Ref(string n) => new(n, DataType.GC_REF);

    [Fact]
    public void AnAllocationCallsTheRuntimeWithTheSizeAndTheRefsFlag()
    {
        var asm = Compile(Program(new GcAlloc(new Constant(10), Ref("xs"), Refs: false)));

        Assert.Matches(@"li\ta0, 10\s+li\ta1, 0\s+call\t__pymcu_gc_alloc", asm);
        Assert.Matches(@"(?m)^__pymcu_gc_alloc:", asm);
    }

    [Fact]
    public void AReferenceBearingAllocationSetsTheFlag()
    {
        var asm = Compile(Program(new GcAlloc(new Constant(10), Ref("xs"), Refs: true)));

        Assert.Matches(@"li\ta1, 1\s+call\t__pymcu_gc_alloc", asm);
    }

    [Fact]
    public void TheResultOfTheAllocationIsStoredInTheDestination()
    {
        var asm = Compile(Program(new GcAlloc(new Constant(10), Ref("xs"), Refs: false)));

        Assert.Matches(@"call\t__pymcu_gc_alloc\s+sw\ta0, -\d+\(s0\)", asm);
    }

    [Fact]
    public void RootingAFrameVariablePushesTheAddressOfItsSlot()
    {
        var asm = Compile(Program(new GcRoot(Ref("xs")), new GcUnroot(Ref("xs"))));

        Assert.Matches(@"addi\ta0, s0, -\d+\s+call\t__pymcu_gc_root_push", asm);
        Assert.Contains("call\t__pymcu_gc_root_pop", asm);
    }

    [Fact]
    public void RootingAGlobalPushesItsSymbol()
    {
        var p = Program(new GcRoot(Ref("xs")));
        p.Globals.Add(new Variable("xs", DataType.GC_REF));
        var asm = Compile(p);

        Assert.Matches(@"la\ta0, xs\s+call\t__pymcu_gc_root_push", asm);
    }

    [Fact]
    public void TheShadowStackHasOneWordPerRootAndPerGlobal()
    {
        var p = Program(new GcRoot(Ref("a")), new GcRoot(Ref("b")), new GcUnroot(Ref("b")), new GcUnroot(Ref("a")));
        p.Globals.Add(new Variable("g", DataType.GC_REF));
        var asm = Compile(p);

        Assert.Matches(@"__pymcu_gc_ss_base:\s+\.zero\t12\b", asm);          // 2 roots + 1 global
        Assert.Matches(@"__pymcu_gc_state:\s+\.zero\t12\b", asm);
    }

    [Fact]
    public void TheGlobalRootsAreListedForTheRuntimeToSeed()
    {
        var p = Program(new GcAlloc(new Constant(4), Ref("g"), Refs: false));
        p.Globals.Add(new Variable("g", DataType.GC_REF));
        var asm = Compile(p);

        Assert.Matches(@"__pymcu_gc_global_roots:\s+\.word\tg\b", asm);
        Assert.Matches(@"__pymcu_gc_global_count:\s+\.word\t1\b", asm);
    }

    [Fact]
    public void AListGrowthFixupCallsTheRuntime()
    {
        var asm = Compile(Program(
            new Call("gc_list_fixup", new List<Val> { Ref("old"), Ref("new") }, new NoneVal())));

        Assert.Matches(@"(?m)^gc_list_fixup:", asm);
    }

    [Fact]
    public void AProgramWithoutAHeapCarriesNoRuntime()
    {
        var asm = Compile(Program(new Return(new NoneVal())));

        Assert.DoesNotContain("__pymcu_gc_", asm);
    }

    [Fact]
    public void TheRuntimeLeavesTheErrorRegisterAlone()
    {
        var asm = Compile(Program(new GcAlloc(new Constant(10), Ref("xs"), Refs: true)));
        var runtime = asm[asm.IndexOf("__pymcu_gc_alloc:", StringComparison.Ordinal)..];

        Assert.DoesNotMatch(@"\bs1\b", runtime);
    }

    [Fact]
    public void AFunctionThatAllocatesSavesTheReturnAddress()
    {
        var asm = Compile(Program(new GcAlloc(new Constant(10), Ref("xs"), Refs: false)));
        var main = asm[asm.IndexOf("\nmain:", StringComparison.Ordinal)..];

        Assert.Contains("sw\tra,", main);
    }

    [Fact]
    public void AWordLoadThroughAnObjectOffsetByTwoReadsBytes()
    {
        // A list keeps its length and capacity in two bytes, then the elements: a word element
        // sits on a two-byte offset from a header that is itself word aligned.
        var asm = Compile(Program(
            new Binary(BinaryOp.Add, Ref("xs"), new Constant(2), new Temporary("p", DataType.UINT32)),
            new LoadIndirect(new Temporary("p", DataType.UINT32), new Temporary("v", DataType.UINT32), DataType.UINT32)));

        Assert.DoesNotContain("lw\tt1, 0(t0)", asm);
    }

    [Fact]
    public void AWordElementAtARuntimeIndexIntoAnObjectReadsBytes()
    {
        var asm = Compile(Program(
            new Binary(BinaryOp.Add, Ref("xs"), new Variable("off", DataType.UINT32), new Temporary("p", DataType.UINT32)),
            new LoadIndirect(new Temporary("p", DataType.UINT32), new Temporary("v", DataType.UINT32), DataType.UINT32)));

        Assert.DoesNotContain("lw\tt1, 0(t0)", asm);
    }

    [Fact]
    public void AReferenceIsAWordWhenItIsStoredThroughAPointer()
    {
        // A GC_REF is as wide as a pointer on the part (4 bytes), not the 2 an unconfigured
        // process assumes: a halfword store would keep half of the address.
        var asm = Compile(Program(
            new StoreIndirect(Ref("r"), new Variable("p", DataType.UINT32), DataType.GC_REF)));

        Assert.Contains("sw\tt1, 0(t0)", asm);
        Assert.DoesNotContain("sh\tt1, 0(t0)", asm);
    }

    [Fact]
    public void AReferenceTwoBytesPastTheHeaderOfAnObjectIsReadBytewise()
    {
        // list[list[T]]: the count byte, the capacity byte, then the refs, so the first ref sits
        // at offset 2 and the second at offset 6 of a word-aligned object.
        var asm = Compile(Program(
            new Binary(BinaryOp.Add, Ref("cells"), new Constant(6), new Temporary("p", DataType.UINT32)),
            new LoadIndirect(new Temporary("p", DataType.UINT32), Ref("row"), DataType.GC_REF)));

        Assert.DoesNotContain("lw\tt1, 0(t0)", asm);
        Assert.Contains("lbu\tt2, 3(t0)", asm);
    }

    [Fact]
    public void CompilingDoesNotTouchTheProcessWidePointerWidth()
    {
        // The front end sets it for itself; unit tests share one process with the front end's.
        int before = DataTypeExtensions.PointerWidth;
        Compile(Program(new GcAlloc(new Constant(10), Ref("xs"), Refs: false)));

        Assert.Equal(before, DataTypeExtensions.PointerWidth);
    }
}

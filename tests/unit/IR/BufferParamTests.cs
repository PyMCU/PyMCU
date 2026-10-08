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
    public void AnInlineCalleeWritingIntoAModuleLevelBuffer_StoresAByte()
    {
        // The WRITE half of the alias normalization, and the one nothing was pinning: the
        // read half had a test, so when this hunk was dropped from a commit by hand the
        // suite stayed green while `buf[0] = v` went back to setting bit 0 of the array's
        // ADDRESS. Silent, on a clean build, which is the whole family this issue is in.
        var ir = Gen(Preamble +
                     "buf3: uint8[3] = [1, 2, 3]\n" +
                     "@inline\n" +
                     "def poke(buf, v: uint8):\n" +
                     "    buf[0] = v\n" +
                     "def main():\n" +
                     "    poke(buf3, 5)\n");

        Assert.Contains(Body(ir, "main"), i => i is ArrayStore);
        Assert.DoesNotContain(Body(ir, "main"), i => i is BitSet or BitClear or BitWrite);
    }

    [Fact]
    public void AnInlineCalleeWritingAtARuntimeIndex_AlsoStores()
    {
        var ir = Gen(Preamble +
                     "buf3: uint8[3] = [1, 2, 3]\n" +
                     "@inline\n" +
                     "def fill(buf, n: uint8):\n" +
                     "    i: uint8 = 0\n" +
                     "    while i < n:\n" +
                     "        buf[i] = i\n" +
                     "        i = i + 1\n" +
                     "def main():\n" +
                     "    fill(buf3, 3)\n");

        Assert.DoesNotContain(Body(ir, "main"), i => i is BitSet or BitClear or BitWrite);
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

    [Fact]
    public void AContiguousSliceArgument_MarshalsItsOwnArrayBase()
    {
        // `b[1:3]` of a contiguous buffer materializes into real addressable storage --
        // `__slice_N` registered in arraysWithVariableIndex, populated by ArrayStore
        // copies -- so the argument marshals as `ArrayBase("__slice_N")`, a base that
        // IS allocated. This is the PyMCU/PyMCU#487 hole, closed: without it the call
        // marshaled the slice's first element as a scalar and the callee's `buf[0]`
        // read I/O space at that number's address (adafruit_ht16k33's show() passing
        // `self._buffer[o : o + 17]` to i2c write, which got a one-byte pointer).
        var ir = Gen(Preamble +
                     "def head(buf: bytearray) -> uint8:\n" +
                     "    return buf[0]\n" +
                     "b: uint8[4] = bytearray(4)\n" +
                     "def main():\n" +
                     "    a: uint8 = head(b[1:3])\n");

        var sliceStores = Body(ir, "main").OfType<ArrayStore>()
            .Where(s => s.ArrayName.StartsWith("__slice_")).ToList();
        Assert.Equal(2, sliceStores.Count);
        Assert.Contains(Body(ir, "main"),
            i => i is Call c && c.FunctionName == "head"
                 && c.Args.Any(a => a is ArrayBase ab && ab.ArrayName == sliceStores[0].ArrayName));
    }

    [Fact]
    public void AFlatSequenceSliceArgument_IsNotMarshaledAsADanglingArrayBase()
    {
        // A slice of a flat element sequence (the `xs = [...]` literal form has no
        // contiguous allocation of its own) still lowers to `__slice_N__k` element
        // variables with no `__slice_N:` label. Marshaling that as
        // `ArrayBase("__slice_N")` would hand the backend a base address that was
        // never allocated -- an undefined symbol at link -- so the value marshal stays.
        var ir = Gen(Preamble +
                     "def head(buf: bytearray) -> uint8:\n" +
                     "    return buf[0]\n" +
                     "xs = [1, 2, 3, 4]\n" +
                     "def main():\n" +
                     "    a: uint8 = head(xs[1:3])\n");

        Assert.DoesNotContain(Body(ir, "main"),
            i => i is Call c && c.Args.Any(a => a is ArrayBase));
    }

    // enumbuf case 2: `takesbuf(buf[i])` passes ONE ELEMENT to a parameter declared
    // 'bytearray'. CPython raises TypeError the first time the callee indexes it
    // ('int' object is not subscriptable); PyMCU instead marshalled the element as if
    // it were the pointer a real buffer travels as, so `v[0]` read whatever SRAM byte
    // that number happened to address -- `takesbuf(buf[0])` on `[10, 20, 30]` answered
    // 255, never an error, never CPython's value.
    [Fact]
    public void AnElementSubscriptPassedToABufferParameter_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            Preamble +
            "def takesbuf(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def main():\n" +
            "    buf = bytearray([10, 20, 30])\n" +
            "    a: uint8 = takesbuf(buf[0])\n"));

        Assert.Contains("takesbuf", ex.Message);
        Assert.Contains("buf[", ex.Message);
        Assert.Contains("bytearray", ex.Message);
    }

    // enumbuf case 1: the same hole from the other side -- `b` from
    // `for i, b in enumerate(buf)` passed to a 'bytearray' parameter. `b` IS one
    // element, never the buffer itself; this must refuse exactly like case 2.
    [Fact]
    public void AnEnumerateElement_PassedToABufferParameter_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            Preamble +
            "def takesbuf(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def main():\n" +
            "    buf = bytearray([10, 20, 30])\n" +
            "    for i, b in enumerate(buf):\n" +
            "        a: uint8 = takesbuf(b)\n"));

        Assert.Contains("takesbuf", ex.Message);
        Assert.Contains("'b'", ex.Message);
        Assert.Contains("bytearray", ex.Message);
    }

    // The enumerate element reaching an INTEGER parameter is the legitimate case the
    // bug's shape must not touch: `b` is the element CPython hands the body too, and
    // this already worked before enumbuf (kept here so the new refusal's narrower
    // scope -- "only shapes that reach a BUFFER parameter" -- has a passing control).
    [Fact]
    public void AnEnumerateElement_PassedToANumberParameter_StillCompiles()
    {
        var ir = Gen(Preamble +
                     "def takesint(v: uint8) -> uint8:\n" +
                     "    return v + 1\n" +
                     "def main():\n" +
                     "    buf = bytearray([10, 20, 30])\n" +
                     "    for i, b in enumerate(buf):\n" +
                     "        a: uint8 = takesint(b)\n");

        Assert.Contains(Body(ir, "main"), i => i is Call c && c.FunctionName == "takesint");
    }

    // The whole buffer, passed by name, is the ordinary call this refusal must leave
    // alone -- `buf` names a real buffer, not one of its elements.
    [Fact]
    public void TheWholeBuffer_PassedByName_StillCompiles()
    {
        var ir = Gen(Preamble +
                     "def takesbuf(v: bytearray) -> uint8:\n" +
                     "    return v[0]\n" +
                     "def main():\n" +
                     "    buf = bytearray([10, 20, 30])\n" +
                     "    a: uint8 = takesbuf(buf)\n");

        Assert.Contains(Body(ir, "main"),
            i => i is Call c && c.FunctionName == "takesbuf"
                 && c.Args.Any(a => a is ArrayBase));
    }

    // Two sibling expansions of the SAME @inline method, both passed the real buffer by
    // name: a false positive this refusal must not reintroduce. Two calls at the same
    // inline depth share one generated prefix (`inline1.scan.` for both, not one per call
    // site), so the second expansion's own parameter binding can look, through the shared
    // prefix, like the first expansion's leftover state shadowing the name -- the refusal
    // must resolve the argument's real storage directly rather than trust that question.
    [Fact]
    public void TheWholeBuffer_PassedToTheSameInlineMethodTwice_StillCompiles()
    {
        var ir = Gen(
            "from pymcu.types import uint8, inline\n\n" +
            "class Dev:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def scan(self, buf: bytearray, n: uint8) -> uint8:\n" +
            "        return buf[0] + n\n" +
            "def main():\n" +
            "    d = Dev()\n" +
            "    buf: uint8[8] = bytearray(8)\n" +
            "    n: uint8 = d.scan(buf, 8)\n" +
            "    c: uint8 = d.scan(buf, 8)\n");

        Assert.NotNull(ir);
    }

    // `one = buf[0]` makes `one` a VALUE-TRACKING alias of the temp that carried the
    // loaded byte (Assign.cs's `value is Temporary` branch files one for every such
    // assignment, not only a structural one). NameIsScalarAtThisSite treated ANY
    // presence in variableAliases as "not a scalar", so `first(one)` -- one element,
    // passed where the whole buffer belongs -- compiled silently instead of refusing:
    // `one` held 10, used as an SRAM address, and the call read back whatever byte
    // sits there.
    [Fact]
    public void AValueTrackingAliasOfAnElement_PassedToABufferParameter_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Preamble +
            "def first(buf: bytearray) -> uint8:\n" +
            "    return buf[0]\n" +
            "def main():\n" +
            "    buf = bytearray([10, 20, 30])\n" +
            "    one: uint8 = buf[0]\n" +
            "    a: uint8 = first(one)\n"));

        Assert.Contains("first", ex.Message);
        Assert.Contains("'one'", ex.Message);
        Assert.Contains("bytearray", ex.Message);
    }

    [Fact]
    public void ALocalAliasOfABufferParameter_ForwardsAsTheBuffer()
    {
        // `alias = buf` where `buf` is itself a bytearray PARAMETER makes `alias` a buffer
        // under another name too (PyMCU-review round 3): nothing propagated that fact for a
        // plain local-to-local copy inside a regular function, unlike an @inline parameter
        // binding or a function's array RETURN value, which already had their own
        // propagation -- so `head(alias)` was refused as a scalar reaching a bytearray
        // parameter instead of compiling, exactly like `head(buf)` directly already did.
        var ir = Gen(Preamble +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def forward(buf: bytearray) -> uint8:\n" +
            "    alias = buf\n" +
            "    return head(alias)\n" +
            "def main():\n" +
            "    a: uint8 = forward(bytearray([4, 5]))\n");

        Assert.Contains(Body(ir, "forward"), i => i is Call { FunctionName: "head" });
    }

    [Fact]
    public void ATupleUnpackTarget_BoundFromABufferParameter_ForwardsAsTheBuffer()
    {
        // `alias, ignored = (buf, 0)` -- a tuple-unpack target -- is ANOTHER shape that
        // forwards a buffer parameter under a new name, found by the same review round
        // as the plain-copy case above. VisitTupleUnpack's own Copy-emission never ran
        // through the alias/value-tracking bookkeeping a plain `x = y` does, so `alias`
        // reached NameIsScalarAtThisSite with nothing proving it one way or the other --
        // which the OLD "assume scalar unless excluded" criterion answered wrong. The
        // new "positive proof only" criterion answers it right without having to add yet
        // another exclusion for this one shape.
        var ir = Gen(Preamble +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def forward(buf: bytearray) -> uint8:\n" +
            "    alias, ignored = (buf, 0)\n" +
            "    return head(alias)\n" +
            "def main():\n" +
            "    a: uint8 = forward(bytearray([4, 5]))\n");

        Assert.Contains(Body(ir, "forward"), i => i is Call { FunctionName: "head" });
    }

    [Fact]
    public void ACallResultThatIsJustItsOwnBufferParameter_ForwardsAsTheBuffer()
    {
        // `returned = ident(buf)` where `ident` simply returns the bytearray parameter it
        // was given -- a THIRD shape forwarding a buffer under a new name, found by the
        // same review round. No CopyArrayIdentity/BindArrayAlias fires for a plain
        // bytearray-typed return (those two exist for a fixed array's return and an
        // @inline parameter binding), so `returned` reached NameIsScalarAtThisSite with
        // no record either way -- same fix as the two cases above, for the same reason.
        var ir = Gen(Preamble +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def ident(v: bytearray) -> bytearray:\n" +
            "    return v\n" +
            "def forward(buf: bytearray) -> uint8:\n" +
            "    returned = ident(buf)\n" +
            "    return head(returned)\n" +
            "def main():\n" +
            "    a: uint8 = forward(bytearray([4, 5]))\n");

        Assert.Contains(Body(ir, "forward"), i => i is Call { FunctionName: "head" });
    }

    [Fact]
    public void ARebindThroughAnUnrolledForLoop_DropsAStaleProvenScalarMark()
    {
        // `x = buf[0]` proves `x` a scalar element (correctly, at that point); `for x in
        // (buf,): pass` then REBINDS `x` to the buffer itself. Without clearing the
        // stale mark first, `head(x)` read back the FIRST binding's proof instead of
        // what `x` actually holds now, and was refused as a scalar reaching a bytearray
        // parameter -- CPython returns the element the buffer's own first byte holds
        // (PyMCU-review round 5).
        var ir = Gen(Preamble +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def f(buf: bytearray) -> uint8:\n" +
            "    x = buf[0]\n" +
            "    for x in (buf,):\n" +
            "        pass\n" +
            "    return head(x)\n" +
            "def main():\n" +
            "    a: uint8 = f(bytearray([10, 20, 30]))\n");

        Assert.Contains(Body(ir, "f"), i => i is Call { FunctionName: "head" });
    }

    // Review round 6: four more shapes that let a scalar reach a buffer parameter,
    // each one a positive-proof gap in a different producer or consumer of
    // provenScalarElements (ExpressionIsProvenScalarElement, Call.cs, is the one place
    // every shape below is taught, so a later form it still misses is one more switch
    // arm there, not a new scattered check).

    // Finding 3: a PLAIN `for` loop variable over a real buffer was never recorded as a
    // scalar element -- only the enumerate path did that. This is the small-enough-to-
    // unroll path (EmitSequenceUnroll's own `forSram` branch, not BindUnrolledRuntimeElement,
    // which only handles the compile-time-sequence fallback).
    [Fact]
    public void APlainForLoopElement_OverARealBuffer_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            Preamble +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def main():\n" +
            "    buf = bytearray([10, 20])\n" +
            "    for one in buf:\n" +
            "        a: uint8 = head(one)\n"));

        Assert.Contains("head", ex.Message);
        Assert.Contains("bytearray", ex.Message);
    }

    // Finding 4: a tuple-unpack target bound directly from a subscript
    // (`one, ignored = (buf[0], 0)`) copied the scalar without propagating the proof --
    // only a target bound from a NAME already proven did.
    [Fact]
    public void ATupleUnpackTarget_BoundFromASubscript_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            Preamble +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def main():\n" +
            "    buf = bytearray([10, 20])\n" +
            "    one, ignored = (buf[0], 0)\n" +
            "    a: uint8 = head(one)\n"));

        Assert.Contains("head", ex.Message);
        Assert.Contains("bytearray", ex.Message);
    }

    // Finding 5: a function's return value carries no name of its own to check
    // provenScalarElements against, so a call result was never recognised as a scalar
    // element -- neither passed directly as the argument, nor read back from a name it
    // was first assigned to, and whether the callee is @inline or not makes no
    // difference (functionsReturnProvenScalar is a structural, pre-lowering fact about
    // the callee, computed the same way for either).
    [Fact]
    public void AnInlineCalls_ProvenScalarReturn_PassedDirectly_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            Preamble +
            "from pymcu.types import inline\n\n" +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "@inline\n" +
            "def element(buf: bytearray) -> uint8:\n" +
            "    return buf[0]\n" +
            "def main():\n" +
            "    buf = bytearray([10, 20])\n" +
            "    a: uint8 = head(element(buf))\n"));

        Assert.Contains("head", ex.Message);
        Assert.Contains("bytearray", ex.Message);
    }

    [Fact]
    public void ANonInlineCalls_ProvenScalarReturn_PassedDirectly_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            Preamble +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def element(buf: bytearray) -> uint8:\n" +
            "    return buf[0]\n" +
            "def main():\n" +
            "    buf = bytearray([10, 20])\n" +
            "    a: uint8 = head(element(buf))\n"));

        Assert.Contains("head", ex.Message);
        Assert.Contains("bytearray", ex.Message);
    }

    [Fact]
    public void AProvenScalarReturn_AssignedToANameFirst_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            Preamble +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def element(buf: bytearray) -> uint8:\n" +
            "    return buf[0]\n" +
            "def main():\n" +
            "    buf = bytearray([10, 20])\n" +
            "    e: uint8 = element(buf)\n" +
            "    a: uint8 = head(e)\n"));

        Assert.Contains("head", ex.Message);
        Assert.Contains("'e'", ex.Message);
        Assert.Contains("bytearray", ex.Message);
    }

    // Finding 6: a field, a nested slice, a walrus and a ternary each hid the scalar
    // element from the two-shape matcher ArgumentIsScalarElement used to be.
    [Fact]
    public void AFieldSubscript_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            Preamble +
            "class Box:\n" +
            "    def __init__(self, buf):\n" +
            "        self.buf = buf\n" +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def main():\n" +
            "    obj = Box(bytearray([10, 20]))\n" +
            "    a: uint8 = head(obj.buf[0])\n"));

        Assert.Contains("head", ex.Message);
        Assert.Contains("bytearray", ex.Message);
    }

    [Fact]
    public void AnElementOfASlice_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            Preamble +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def main():\n" +
            "    buf = bytearray([10, 20])\n" +
            "    a: uint8 = head(buf[0:1][0])\n"));

        Assert.Contains("head", ex.Message);
        Assert.Contains("bytearray", ex.Message);
    }

    [Fact]
    public void AWalrusOfASubscript_PassedDirectly_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            Preamble +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def main():\n" +
            "    buf = bytearray([10, 20])\n" +
            "    one: uint8 = 0\n" +
            "    a: uint8 = head(one := buf[0])\n"));

        Assert.Contains("head", ex.Message);
        Assert.Contains("bytearray", ex.Message);
    }

    [Fact]
    public void ATernaryOfTwoSubscripts_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            Preamble +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def main():\n" +
            "    buf = bytearray([10, 20])\n" +
            "    flag: uint8 = 1\n" +
            "    a: uint8 = head(buf[0] if flag else buf[1])\n"));

        Assert.Contains("head", ex.Message);
        Assert.Contains("bytearray", ex.Message);
    }

    // Control for the ternary case: when only ONE arm proves scalar (the other is the
    // buffer itself), the whole expression is left UNPROVEN rather than refused on a
    // guess -- the positive-proof-only stance applies to the whole shape.
    [Fact]
    public void ATernaryWithOnlyOneArmAScalar_IsNotRefused()
    {
        var ir = Gen(Preamble +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def main():\n" +
            "    buf = bytearray([10, 20])\n" +
            "    flag: uint8 = 1\n" +
            "    a: uint8 = head(buf[0] if flag else buf)\n");

        Assert.Contains(Body(ir, "main"), i => i is Call { FunctionName: "head" });
    }
}

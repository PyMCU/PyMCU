using System.Linq;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// RFC 0015 (buffers carry their length) / RFC 0001 (zero-copy instances): a heap
/// `list[T]` whose first `.append(...)` is a constructor call for a SINGLE-FIELD class
/// (the RFC 0001 Model A "factory handle" shape, where the instance IS its one field's
/// scalar value) had no case in the element-type inference at all
/// (`InferExprType`/`InferListElemType`, Core.cs), so it defaulted to UINT8 -- an 8-bit
/// truncation of a 16-bit buffer pointer on AVR, silently wrong once any address exceeded
/// 255 -- and no case in the class-tag side either, so a method or property call on a
/// read-back element (`devices[i].rom`) had no dispatch.
///
/// The deeper regression this chain also found: `f()[k]` where `f` returns the heap list
/// DIRECTLY (no name bound in between, `adafruit_onewire`'s `ow_bus.scan()[0]`) had no
/// GC_REF-list case in the `tupleSrc` branch of VisitIndex (Expr.cs) at all, and fell
/// through to the register-bit default -- silently reading bit 0 of the list's own GC
/// pointer instead of loading its first element. Not a refusal: a wrong answer.
/// </summary>
public class HeapListOfSingleFieldInstancesTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new System.Collections.Generic.Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    // The list-building method lives on a class, and is only ever called through a
    // module-level instance -- the same compile order `OneWireBus.scan()` has in the
    // unmodified adafruit_onewire bus (a method, not a bare top-level function), which
    // avoids an unrelated, pre-existing limitation of a BARE function returning a list of
    // instances when called at module level before its own body compiles.
    private const string AddrAndScanner =
        "from pymcu.types import uint8\n" +
        "class Addr:\n" +
        "    def __init__(self, rom: bytearray) -> None:\n" +
        "        self._rom = rom\n" +
        "    @property\n" +
        "    def rom(self) -> bytearray:\n" +
        "        return self._rom\n" +
        "class Scanner:\n" +
        "    def __init__(self):\n" +
        "        self.count = 0\n" +
        "    def scan(self):\n" +
        "        xs = []\n" +
        "        for _ in range(2):\n" +
        "            xs.append(Addr(bytearray(3)))\n" +
        "        return xs\n" +
        "s = Scanner()\n";

    // None of these tests call len() on the element's OWN buffer field: a buffer read back
    // from a RUNTIME-INDEXED heap list has no compile-time identity for the CONST-ARRAY
    // fallback to chase (the genuine, not-yet-closed gap the session report documents --
    // RFC 0015 phase 1 needs a real runtime (pointer, length) pair for this shape, not
    // another alias hop). What these tests pin is narrower and fully fixed: the LIST
    // ELEMENT itself (the handle) loads at its real width and keeps its class, regardless
    // of whether its OWN field's length can be chased afterwards.

    [Fact]
    public void AnAppendedSingleFieldBufferInstance_WidensTheListToAPointer()
    {
        // xs[0] must load a pointer to the record (the Addr handle, RFC 0016), at the width
        // of a GC reference, not the UINT8 default InferExprType gave a CallExpr before
        // this fix -- a silent truncation of any address above 255.
        var main = Gen(AddrAndScanner +
            "xs = s.scan()\n" +
            "h = xs[0]\n")
            .Functions.Single(f => f.Name == "main").Body;

        Assert.Contains(main, i => i is LoadIndirect { Elem: DataType.GC_REF });
    }

    [Fact]
    public void AnIndexedElement_DispatchesItsPropertyGetter()
    {
        // No UserError means the element's class tag survived the list round-trip: the
        // property getter actually dispatched (reading the handle's own field value)
        // instead of the read falling through to "object has no attribute" or a plain
        // numeric read with no class at all.
        var ir = Gen(AddrAndScanner +
            "xs = s.scan()\n" +
            "h = xs[0]\n" +
            "b = h.rom\n" +
            "h2 = xs[1]\n" +
            "b2 = h2.rom\n");
        Assert.NotNull(ir.Functions.Single(f => f.Name == "main"));
    }

    [Fact]
    public void IndexingTheCallResultDirectly_LoadsTheElement_NotABitOfThePointer()
    {
        // `s.scan()[0]` -- no name bound in between, the exact shape
        // `ow_bus.scan()[0]` has in the unmodified adafruit_onewire bus. Before this fix,
        // VisitIndex's `tupleSrc` branch had no GC_REF-list case and fell through to the
        // register-bit default: a BitCheck of bit 0 of the list's own GC pointer.
        var main = Gen(AddrAndScanner +
            "h = s.scan()[0]\n")
            .Functions.Single(f => f.Name == "main").Body;

        Assert.Contains(main, i => i is LoadIndirect { Elem: DataType.GC_REF });
        Assert.DoesNotContain(main, i => i is BitCheck);
    }
}

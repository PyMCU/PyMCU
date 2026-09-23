using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `import array` / `array.array(typecode)`, read as the heap-bounded `list[T]` the compiler
/// already has, and the gaps that mapping exposed once real programs exercised it
/// (adafruit_dht's `pulses = array.array("H")`, methods taking/returning `array.array`).
///
/// Each of these was a SILENT wrong-code hazard rather than a refusal: a `list[T]`-typed
/// parameter on a plain (non-@inline) function used to be refused outright; `len()` on a
/// list[T] bound through an @inline parameter alias fell through to a generic refusal; and a
/// bare `x = f()` where f returns `list[T]` typed x as UNKNOWN (no case in StringToDataType for
/// "list["), so a later `x[i]` silently compiled into a bit-test of x's address instead of a
/// list read.
/// </summary>
public class ArrayArrayAsListTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    // ── array.array(typecode) construction ───────────────────────────────────────────────

    [Fact]
    public void ArrayArrayHConstructsAUint16List()
    {
        var ir = Gen(
            "import array\n\n" +
            "def main() -> int:\n" +
            "    pulses = array.array(\"H\")\n" +
            "    pulses.append(500)\n" +
            "    return pulses[0]\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        // A uint16 element store (500 does not fit uint8) is what says T came from "H".
        Assert.Contains(main.Body, i => i is StoreIndirect s && s.Elem == PyMCU.IR.DataType.UINT16);
    }

    [Theory]
    [InlineData("f")]
    [InlineData("d")]
    [InlineData("q")]
    public void AFloatOr64BitTypecodeIsRefusedByName(string typecode)
    {
        string src = "import array\n\n" +
            $"def main() -> int:\n    pulses = array.array(\"{typecode}\")\n    return 0\n";
        Assert.Contains(typecode, Refusal(src));
    }

    [Fact]
    public void AnUnknownTypecodeNamesWhatArrayArrayAccepts()
        => Assert.Contains("B, b, H, h, I, L, i or l", Refusal(
            "import array\n\ndef main() -> int:\n    pulses = array.array(\"Z\")\n    return 0\n"));

    // ── module-level `xs: list[T]` method dispatch ───────────────────────────────────────

    [Fact]
    public void AModuleLevelListAppendUsesTheDeclaredSlot()
    {
        // Module-level statements replay inside the synthesized `main`; the list
        // declaration used to register `main.xs` while every other path that names
        // the global spells it bare (`xs`) -- ResolveBinding, groot/gunroot,
        // gc_list_fixup, `xs[i]` reads. Two slots grew: the pointer lived in one
        // and the appends/readers used the other, so the list the program saw
        // stayed empty. The declaration now files the global's bare name.
        var ir = Gen(
            "from pymcu.types import uint8\n\n" +
            "xs: list[uint8] = list()\n" +
            "xs.append(4)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        var fixups = main.Body.OfType<Call>()
            .Where(c => c.FunctionName == "gc_list_fixup").ToList();
        Assert.NotEmpty(fixups);
        foreach (var c in fixups)
            Assert.Equal("xs", Assert.IsType<Variable>(c.Args[0]).Name);

        Assert.Contains(main.Body,
            i => i is LoadIndirect li && li.SrcPtr is Variable v && v.Name == "xs");
        Assert.DoesNotContain(main.Body, i =>
            (i is LoadIndirect li2 && li2.SrcPtr is Variable lv && lv.Name == "main.xs") ||
            (i is StoreIndirect si && si.DstPtr is Variable sv && sv.Name == "main.xs") ||
            (i is Copy cp2 && cp2.Dst is Variable dv && dv.Name == "main.xs"));

        // The pointer operand must be typed GC_REF, not the UNKNOWN ResolveBinding
        // reports for a module-level list: a 1-byte operand makes the backend load
        // only the pointer's low byte and dereference a shadow address in low SRAM.
        Assert.All(main.Body.OfType<LoadIndirect>()
                .Where(li => li.SrcPtr is Variable lv && lv.Name == "xs"),
            li => Assert.Equal(DataType.GC_REF, ((Variable)li.SrcPtr).Type));
        Assert.All(main.Body.OfType<StoreIndirect>()
                .Where(si => si.DstPtr is Variable sv && sv.Name == "xs"),
            si => Assert.Equal(DataType.GC_REF, ((Variable)si.DstPtr).Type));
    }

    [Fact]
    public void AModuleLevelListGlobalIsSizedForAPointer()
    {
        // The mutableGlobals entry for `xs` kept the annotation-derived UNKNOWN
        // (1-byte) type after the declaration resolved to a heap list: the
        // backend sizes globals from that table, so `xs` was allotted a single
        // byte and the next global's slot began inside it. `code: uint32`
        // landed at xs+1, its four-byte init store overwrote the list pointer's
        // high byte, and the next append dereferenced a wild address (the
        // emulator died in Cpu.ReadData). variableTypes had GC_REF; the global
        // table is what needed it.
        var ir = Gen(
            "from pymcu.types import uint16, uint32\n\n" +
            "xs: list[uint16] = list()\n" +
            "code: uint32 = 0x00FF906F\n" +
            "xs.append(1)\n");
        var xs = ir.Globals.Single(g => g.Name == "xs");
        Assert.Equal(DataType.GC_REF, xs.Type);
    }

    [Fact]
    public void LenOnAModuleLevelListReadsTheDeclaredSlot()
    {
        // The variable-indexed-array pre-scan files every `x: list[T]` it meets under the
        // enclosing function's prefix -- `main.xs` for a module-level declaration. The
        // emitter then settles the same decl on the bare global (`xs`), leaving a phantom
        // `main.xs` entry in listVarElemTypes. Append and `xs[i]` resolve through
        // ResolveNameKey, which never consults that table, so they kept working; len()
        // resolves through ResolveListVarQualified, which checks the function-prefixed
        // key FIRST and handed back `main.xs` -- a slot nobody writes, so len(xs) read 0
        // no matter how full the list was. adafruit_irremote's read_pulses() saw every
        // drained batch as empty and blocked forever (the cp-bisect self-loopback hang).
        var ir = Gen(
            "from pymcu.types import uint16\n\n" +
            "xs: list[uint16] = list()\n" +
            "xs.append(4)\n" +
            "n = len(xs)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        Assert.Contains(main.Body, i => i is LoadIndirect li
            && li.SrcPtr is Variable v && v.Name == "xs");
        Assert.DoesNotContain(main.Body, i =>
            i is LoadIndirect li2 && li2.SrcPtr is Variable lv && lv.Name == "main.xs");
    }

    [Fact]
    public void AListParameterAppendInsideAnExpansionWritesTheCallersList()
    {
        // `grow(xs, v)` binds `ys` to the caller's list through variableAliases; the
        // append inside the expansion must read and update THAT list's header, not a
        // same-named dead slot.
        var ir = Gen(
            "from pymcu.types import uint8\n\n" +
            "def grow(ys: list[uint8], v: uint8) -> None:\n" +
            "    ys.append(v)\n\n" +
            "xs: list[uint8] = list()\n" +
            "grow(xs, 9)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        // The expansion's header ops must address the caller's list slot, GC_REF-typed.
        Assert.Contains(main.Body, i => i is LoadIndirect li
            && li.SrcPtr is Variable v && v.Name == "xs" && v.Type == DataType.GC_REF);
        Assert.Contains(main.Body, i => i is StoreIndirect si
            && si.DstPtr is Variable v && v.Name == "xs" && v.Type == DataType.GC_REF);
        Assert.DoesNotContain(main.Body, i =>
            (i is LoadIndirect li2 && li2.SrcPtr is Variable lv && lv.Name.EndsWith(".ys")) ||
            (i is StoreIndirect si2 && si2.DstPtr is Variable sv && sv.Name.EndsWith(".ys")));
    }

    // ── `list[T]` parameter on a REGULAR (non-@inline) function ──────────────────────────

    [Fact]
    public void AListParameterOnARegularFunctionReadsTheCallersList()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n\n" +
            "class Foo:\n" +
            "    def total(self, buf: list[uint16], n: uint16) -> uint16:\n" +
            "        s: uint16 = 0\n" +
            "        i: uint16 = 0\n" +
            "        while i < n:\n" +
            "            s = s + buf[i]\n" +
            "            i = i + 1\n" +
            "        return s\n\n" +
            "    def run(self) -> uint16:\n" +
            "        buf: list[uint16] = list()\n" +
            "        buf.append(5)\n" +
            "        buf.append(6)\n" +
            "        return self.total(buf, 2)\n\n" +
            "def main() -> uint16:\n" +
            "    f = Foo()\n" +
            "    return f.run()\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        // Force-inlined (imarker), same mechanism a ZCA-class parameter already uses -- not
        // refused, and not compiled as a subroutine with nothing bound to `buf`.
        Assert.Contains(main.Body, i => i is InlineExpansionMarker m && m.FuncName == "Foo_total");
        Assert.Contains(main.Body, i => i is LoadIndirect);
    }

    // ── `array.array`-returning function assigned to a bare local ────────────────────────

    [Fact]
    public void ABareAssignmentFromAListReturningFunctionIsAList()
    {
        var ir = Gen(
            "from pymcu.types import uint16, used\n\n" +
            "def make() -> list[uint16]:\n" +
            "    x: list[uint16] = list()\n" +
            "    x.append(5)\n" +
            "    x.append(6)\n" +
            "    return x\n\n" +
            "@used\n" +
            "def main() -> uint16:\n" +
            "    y = make()\n" +
            "    return y[0] + y[1]\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        // Red before the fix: y was typed UNKNOWN (folded to uint8), and y[0]/y[1] compiled to
        // BitCheck -- a silent bit-test of y's address, not a list read.
        Assert.DoesNotContain(main.Body, i => i is BitCheck);
        Assert.Contains(main.Body, i => i is LoadIndirect);
        Assert.Contains(main.Body, i => i is GcRoot);
    }

    [Fact]
    public void ABareAssignmentFromAnArrayArrayReturningMethodIsAList()
    {
        // The exact adafruit_dht shape: a method whose declared return is the bare
        // `array.array` annotation (no typecode -- the element width comes from what the
        // method's OWN body actually builds and returns).
        var ir = Gen(
            "from pymcu.types import uint16, used\n" +
            "import array\n\n" +
            "class Foo:\n" +
            "    def get_pulses(self) -> array.array:\n" +
            "        pulses = array.array(\"H\")\n" +
            "        pulses.append(5)\n" +
            "        pulses.append(6)\n" +
            "        return pulses\n\n" +
            "    def run(self) -> uint16:\n" +
            "        pulses = self.get_pulses()\n" +
            "        return pulses[0] + pulses[1]\n\n" +
            "@used\n" +
            "def main() -> uint16:\n" +
            "    f = Foo()\n" +
            "    return f.run()\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.DoesNotContain(main.Body, i => i is BitCheck);
        Assert.Contains(main.Body, i => i is LoadIndirect);
    }
}

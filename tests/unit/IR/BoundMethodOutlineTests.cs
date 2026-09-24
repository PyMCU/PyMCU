using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Bound-instance outlining: a force-inline method called on a module-level instance
/// compiles its body once as a real subroutine bound to the instance's flattened
/// global storage, and every call site that can share the body emits a Call to it.
/// The ht16k33 matrix demandant inlined show() once per pixel write otherwise.
/// </summary>
public class BoundMethodOutlineTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static ProgramIR GenOpt(string src) =>
        Optimizer.Optimize(Gen(src));

    private static bool IsRuntimeBranch(Instruction i) =>
        i is JumpIfZero or JumpIfNotZero or JumpIfEqual or JumpIfNotEqual
            or JumpIfBitSet or JumpIfBitClear;

    private static bool ConstIndexedBy(ArrayLoad l, string array, int index) =>
        l.ArrayName == array && l.Index is Constant { Value: var v } && v == index;

    private const string TwoCallSink =
        "buf = bytearray(2)\n" +
        "class Sink:\n" +
        "    def __init__(self):\n" +
        "        self.buf = bytearray(4)\n" +
        "        self.n = 0\n" +
        "        self.total = 0\n" +
        "    def put(self, v: uint8) -> None:\n" +
        "        self.buf[self.n] = v\n" +
        "        self.n = self.n + 1\n" +
        "        self.total = self.total + v\n" +
        "        self.total = self.total + self.n\n" +
        "\n" +
        "s = Sink()\n" +
        "s.put(1)\n" +
        "s.put(2)\n" +
        "s.put(3)\n";

    [Fact]
    public void RepeatedCallsToTheSameMethod_ShareOneSynthesizedBody()
    {
        var ir = GenOpt(TwoCallSink);
        var synths = ir.Functions.Where(f => f.Name.StartsWith("_bound_")).ToList();

        synths.Select(f => f.Name).Should().ContainSingle(n => n.EndsWith("_put"),
            because: "put() on a module-level instance is outlined once, not per site");

        var calls = ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Where(c => c.FunctionName.EndsWith("_put")).ToList();
        calls.Should().HaveCount(3,
            because: "the site census sees the repeats before the first call " +
                     "emits, so every site emits a real call into the shared body");
    }

    [Fact]
    public void TheSharedBody_ReadsTheInstancesFlattenedFields()
    {
        var ir = GenOpt(TwoCallSink);
        var put = ir.Functions.Single(f => f.Name.EndsWith("_put"));

        // self.buf[i] is the global array s_buf; self.n is the global s_n.
        put.Body.OfType<ArrayStore>().Should().Contain(s => s.ArrayName == "s_buf",
            because: "the bound body addresses the instance's flattened storage");
    }

    [Fact]
    public void AMutableFieldReadInsideTheSharedBody_IsNotFoldedToTheCtorValue()
    {
        var ir = GenOpt(
            "buf = bytearray(1)\n" +
            "class Gate:\n" +
            "    def __init__(self):\n" +
            "        self.on = True\n" +
            "    def read(self) -> uint8:\n" +
            "        return 1 if self.on else 0\n" +
            "\n" +
            "g = Gate()\n" +
            "g.on = False\n" +
            "buf[0] = g.read()\n" +
            "buf[0] = g.read()\n");

        var read = ir.Functions.SingleOrDefault(f => f.Name.EndsWith("_read"));
        read.Should().NotBeNull(because: "read() on a module-level instance is outlined");
        // The body's `if self.on` must branch on the global, not fold to the ctor's True.
        read!.Body.Should().Contain(i => IsRuntimeBranch(i),
            because: "self.on was rewritten after construction, so the test is runtime");
    }

    [Fact]
    public void AConstantArgumentToAnArrayIndexedParam_KeepsTheExpansionFold()
    {
        // d.at(1) must still produce the constant-indexed ArrayLoad the tuple/array
        // field tests pin; only the runtime-arg site shares the body.
        var ir = GenOpt(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "buf = bytearray(2)\n" +
            "class Dev:\n" +
            "    def __init__(self):\n" +
            "        self.scale = bytearray([10, 20, 30])\n" +
            "    def at(self, n: uint8) -> uint8:\n" +
            "        return self.scale[n] + self.scale[0] + self.scale[1] + self.scale[2]\n" +
            "\n" +
            "d = Dev()\n" +
            "buf[0] = d.at(1)\n" +
            "buf[1] = d.at(GPIOR0.value)\n" +
            "buf[0] = d.at(GPIOR0.value)\n");

        ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>()
            .Should().Contain(l => ConstIndexedBy(l, "d_scale", 1),
                because: "the const-arg site keeps the per-site constant index fold");
        ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Should().Contain(c => c.FunctionName.EndsWith("_at"),
                because: "the runtime-arg site shares the synthesized body");
    }

    [Fact]
    public void AClassDictIndexedByAParam_StaysInlineEverywhere()
    {
        // self.T[n] with T a class-level dict folds per site; a shared body emits
        // a compare chain -- a different shape. Both sites keep the expansion.
        var ir = GenOpt(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "buf = bytearray(2)\n" +
            "class Dev:\n" +
            "    T = {0: 10, 1: 20, 2: 30}\n" +
            "    def __init__(self):\n" +
            "        self.x = 0\n" +
            "    def at(self, n: uint8) -> uint8:\n" +
            "        return self.T[n]\n" +
            "\n" +
            "d = Dev()\n" +
            "buf[0] = d.at(0)\n" +
            "buf[1] = d.at(GPIOR0.value)\n");

        ir.Functions.Should().NotContain(f => f.Name.StartsWith("_bound_"),
            because: "a compile-time collection indexed by a parameter cannot share a body");
    }

    [Fact]
    public void WithAsBindingAnEnterResult_StaysInline()
    {
        var ir = GenOpt(
            "class Inner:\n" +
            "    def __init__(self):\n" +
            "        self.v = 1\n" +
            "    def __enter__(self):\n" +
            "        return self\n" +
            "    def __exit__(self):\n" +
            "        return None\n" +
            "class Outer:\n" +
            "    def __init__(self):\n" +
            "        self.m = Inner()\n" +
            "    def go(self) -> uint8:\n" +
            "        with self.m as x:\n" +
            "            return x.v\n" +
            "\n" +
            "o = Outer()\n" +
            "buf = bytearray(1)\n" +
            "buf[0] = o.go()\n" +
            "buf[0] = o.go()\n");

        ir.Functions.Should().NotContain(f => f.Name.StartsWith("_bound_"),
            because: "the with-bound name's class is the __enter__ result bound per site");
    }

    [Fact]
    public void ABoundOptionalParameter_CarriesItsTagInTheSharedCall()
    {
        var ir = GenOpt(
            "buf = bytearray(1)\n" +
            "class M:\n" +
            "    def __init__(self):\n" +
            "        self.seen = 0\n" +
            "        self.miss = 0\n" +
            "        self.hit = 0\n" +
            "    def px(self, x: uint8, c: Optional[bool] = None) -> None:\n" +
            "        if c is None:\n" +
            "            self.seen = self.seen + x\n" +
            "            self.miss = self.miss + 1\n" +
            "        else:\n" +
            "            self.seen = self.seen + x + 1\n" +
            "            self.hit = self.hit + 1\n" +
            "\n" +
            "m = M()\n" +
            "m.px(1, True)\n" +
            "m.px(2, None)\n" +
            "m.px(3, True)\n");

        var synth = ir.Functions.Single(f => f.Name.EndsWith("_px"));
        var calls = ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Where(c => c.FunctionName == synth.Name).ToList();
        calls.Should().HaveCount(3,
            because: "the site census sees the repeats before the first call " +
                     "emits, so every site emits a real call into the shared body");
        calls.Should().OnlyContain(c => c.Args.Count == 3,
            because: "payload x, payload c, then c's tag byte in the argument run");
    }

    [Fact]
    public void ABoundMethodWithTryExcept_KeepsItsCatchInsideTheSharedBody()
    {
        // The shared body is generated while the caller's try/catch context may be
        // live; a SignalError inside it must aim at a label the body itself defines
        // (or propagate with null), never at a caller-owned catch label.
        var ir = GenOpt(
            "buf = bytearray(1)\n" +
            "class M:\n" +
            "    def __init__(self):\n" +
            "        self.n = 0\n" +
            "    def bump(self, x: uint8) -> None:\n" +
            "        try:\n" +
            "            if x > 9:\n" +
            "                raise ValueError(1)\n" +
            "            self.n = self.n + x\n" +
            "        except ValueError:\n" +
            "            self.n = self.n + 1\n" +
            "\n" +
            "m = M()\n" +
            "m.bump(1)\n" +
            "m.bump(2)\n");

        var synth = ir.Functions.SingleOrDefault(f => f.Name.EndsWith("_bump"));
        synth.Should().NotBeNull(
            because: "bump() on a module-level instance is outlined");
        var ownLabels = synth!.Body.OfType<Label>().Select(l => l.Name).ToHashSet();
        synth.Body.OfType<SignalError>().Should().OnlyContain(
            s => s.CatchLabel == null || ownLabels.Contains(s.CatchLabel),
            because: "a catch inside the shared body targets a label in that body");
    }

    [Fact]
    public void ABoundOptionalReturn_PassesATagBackToTheCaller()
    {
        var ir = GenOpt(
            "buf = bytearray(2)\n" +
            "class M:\n" +
            "    def __init__(self):\n" +
            "        self.n = 0\n" +
            "    def get(self, x: uint8) -> Optional[bool]:\n" +
            "        self.n = self.n + 1\n" +
            "        if x > 4:\n" +
            "            return x > 2\n" +
            "        return None\n" +
            "\n" +
            "m = M()\n" +
            "r = m.get(9)\n" +
            "if r is None:\n" +
            "    buf[0] = 1\n" +
            "else:\n" +
            "    buf[0] = 2\n" +
            "r = m.get(2)\n" +
            "if r is None:\n" +
            "    buf[1] = 1\n" +
            "else:\n" +
            "    buf[1] = 2\n" +
            "r = m.get(1)\n" +
            "if r is None:\n" +
            "    buf[0] = 3\n");

        var synth = ir.Functions.SingleOrDefault(f => f.Name.EndsWith("_get"));
        synth.Should().NotBeNull(
            because: "get() on a module-level instance is outlined");
        synth!.ReturnMembers.Should().NotBeNull(
            because: "the declared Optional[bool] return reaches both members");
        ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Where(c => c.FunctionName == synth.Name)
            .Should().OnlyContain(c => c.TagDst != null,
                because: "a tagged-return callee hands the caller a tag destination");
    }

    [Fact]
    public void ABoundMethodReadingACallerFrameArenaLen_IsNotOutlined()
    {
        // self.buf = bytearray(n) binds the buffer's length to a temp minted by the
        // caller-side __init__ expansion (inlineNN___init___buf__arena_len). A shared
        // body reading that name reads a caller frame slot whose write can die to DCE;
        // the audit must refuse and keep peek() expanding per site.
        var ir = GenOpt(
            "class Dev:\n" +
            "    def __init__(self, n: uint16):\n" +
            "        self.buf = bytearray(n)\n" +
            "    def peek(self, i: uint16) -> uint8:\n" +
            "        self.buf[i] = self.buf[i] + 1\n" +
            "        return self.buf[i]\n" +
            "\n" +
            "d = Dev(4)\n" +
            "d.peek(0)\n" +
            "d.peek(1)\n" +
            "d.peek(2)\n");

        ir.Functions.Should().NotContain(f => f.Name.EndsWith("_peek"),
            because: "peek() reads the caller-minted arena_len slot, so it must not " +
                     "be lifted into a shared body");
    }
}

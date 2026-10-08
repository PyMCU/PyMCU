using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An unannotated slot -- a local, a parameter, a return, a field -- takes its width from the
/// evidence in hand when the compiler first has to choose one, and a later write could need
/// more. The store truncated in silence: `e = d + 300` / `e = d - 1` in the two arms of an
/// `if` made `e` a uint16 and -1 read back as 65535; `f(GPIOR0.value + 900)` into `def f(x)`
/// arrived as 132; the countdown in adafruit_framebuf's `scroll()` (`y += dt_y` with dt_y = -1)
/// wrapped a byte and never reached its `y != yend` exit.
///
/// The write now records the width the slot needs and the compilation runs again with the
/// slot declared at that width (WidthSeeds). These tests drive the generator the way the
/// pipeline does: run, and run again while a slot asked for more.
/// </summary>
public class InferredSlotWidthTests
{
    private static (ProgramIR Ir, int Runs) GenSeeded(string src)
    {
        var seeds = new WidthSeeds();
        for (int run = 1; ; run++)
        {
            seeds.BeginRun();
            var ir = new IRGenerator { WidthSeeds = seeds }.Generate(
                new Parser(new Lexer(src).Tokenize()).ParseProgram(),
                new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" });
            if (!seeds.Grew || run == 5) return (Optimizer.Optimize(ir), run);
        }
    }

    private static IEnumerable<Variable> VariablesNamed(ProgramIR ir, string name) =>
        ir.Functions.SelectMany(f => f.Body).SelectMany(Operands)
            .OfType<Variable>().Where(v => v.Name == name);

    private static IEnumerable<Val> Operands(Instruction ins) => ins switch
    {
        Copy c => new[] { c.Src, c.Dst },
        Binary b => new[] { b.Src1, b.Src2, b.Dst },
        AugAssign a => new[] { a.Target, a.Operand },
        Return r => new[] { r.Value },
        JumpIfEqual j => new[] { j.Src1, j.Src2 },
        JumpIfNotEqual j => new[] { j.Src1, j.Src2 },
        Call call => call.Args.Append(call.Dst),
        _ => Array.Empty<Val>(),
    };

    private static void ShouldHold(ProgramIR ir, string name, long min, long max)
    {
        var vars = VariablesNamed(ir, name).ToList();
        vars.Should().NotBeEmpty($"'{name}' is stored to");
        foreach (var v in vars)
        {
            var (lo, hi) = RangeOf(v.Type);
            lo.Should().BeLessThanOrEqualTo(min, $"'{name}' is {v.Type} and holds {min}");
            hi.Should().BeGreaterThanOrEqualTo(max, $"'{name}' is {v.Type} and holds {max}");
        }
    }

    private static (long, long) RangeOf(DataType t) => t switch
    {
        DataType.UINT8 => (0, 255), DataType.INT8 => (-128, 127),
        DataType.UINT16 => (0, 65535), DataType.INT16 => (-32768, 32767),
        DataType.UINT32 => (0, uint.MaxValue), _ => (int.MinValue, int.MaxValue),
    };

    [Fact]
    public void A_local_joined_from_a_wide_and_a_negative_arm_holds_both()
    {
        // join4: the first store fixed uint16, the second arm's -1 read back as 65535.
        var (ir, runs) = GenSeeded(
            "def f(d: uint8) -> bool:\n" +
            "    if d > 5:\n" +
            "        e = d + 300\n" +
            "    else:\n" +
            "        e = d - 1\n" +
            "    return e == -1\n" +
            "r: bool = f(0)\n");
        ShouldHold(ir, "f.e", -1, 555);
        runs.Should().Be(2);
    }

    [Fact]
    public void A_countdown_in_an_inline_body_goes_below_zero()
    {
        // adafruit_framebuf's scroll(): `y = 3` bound a byte and `y -= 1` wrapped it, so
        // `y != d - 1` was never false and the loop wrote past the buffer.
        var (ir, _) = GenSeeded(
            "n: uint8 = 0\n" +
            "@inline\n" +
            "def f(d: uint8):\n" +
            "    global n\n" +
            "    y = 3\n" +
            "    while y != d - 1:\n" +
            "        y -= 1\n" +
            "        n = n + 1\n" +
            "k: uint8 = n\n" +
            "f(k)\n");
        ShouldHold(ir, "inline1.f.y", -1, 3);
    }

    [Fact]
    public void An_accumulator_is_as_wide_as_what_feeds_it()
    {
        // `c = c + w` with c a byte and w sixteen bits stored the sum's low byte. The store
        // reads its own slot, so it answers for the width of w -- and not for the promoted
        // sum, which would ask for one more step on every run.
        var (ir, runs) = GenSeeded(
            "w: uint16 = 0\n" +
            "def f(d: uint8) -> uint16:\n" +
            "    c = d\n" +
            "    c = c + w\n" +
            "    return c\n" +
            "r: uint16 = f(3)\n");
        ShouldHold(ir, "f.c", 0, 65535);
        runs.Should().Be(2);
    }

    [Fact]
    public void An_unannotated_parameter_holds_an_argument_no_call_site_could_type()
    {
        // w2: TypeInference cannot type `w + 900` read inside a function (a module global is
        // not in that scope), so `v` kept the uint8 default and 900 arrived as 132.
        var (ir, _) = GenSeeded(
            "w: uint16 = 0\n" +
            "def f(v):\n" +
            "    return v\n" +
            "def g() -> uint16:\n" +
            "    return f(w + 900)\n" +
            "r: uint16 = g()\n");
        ShouldHold(ir, "f.v", 900, 65535 + 900L);
    }

    [Fact]
    public void An_unannotated_parameter_holds_a_negative_argument()
    {
        var (ir, _) = GenSeeded(
            "w: uint8 = 0\n" +
            "def f(v):\n" +
            "    return v < 0\n" +
            "def g() -> bool:\n" +
            "    return f(w - 1)\n" +
            "r: bool = g()\n");
        ShouldHold(ir, "f.v", -1, 254);
    }

    [Fact]
    public void An_unannotated_inline_parameter_takes_its_run_time_arguments_width()
    {
        // Not a seed: the binding has the argument in hand. The uint8 default truncated it,
        // `f(GPIOR0.value + 900)` into `@inline def f(x)` printed 132.
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(
                "w: uint16 = 0\n" +
                "r: uint16 = 0\n" +
                "@inline\n" +
                "def f(x):\n" +
                "    global r\n" +
                "    r = x\n" +
                "def g():\n" +
                "    f(w + 900)\n" +
                "g()\n").Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        ShouldHold(ir, "inline1.g.f.x", 900, 65535 + 900L);
    }

    [Fact]
    public void An_inline_parameter_declared_narrower_than_its_argument_holds_it_converted()
    {
        // `@inline def f(n: uint8)` bound n as an alias of the caller's uint16 variable and
        // printed 300 where the outlined twin printed 44 (type-system.md: a declared width
        // wraps). The parameter is now a slot of its own, at its declared width.
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(
                "r: uint16 = 0\n" +
                "@inline\n" +
                "def f(n: uint8) -> uint16:\n" +
                "    return n\n" +
                "def g(w: uint16):\n" +
                "    global r\n" +
                "    r = f(w)\n" +
                "g(300)\n").Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable { Name: "inline1.g.f.n" })
            .Should().ContainSingle()
            .Which.Dst.Should().BeOfType<Variable>().Which.Type.Should().Be(DataType.UINT8);
    }

    [Fact]
    public void A_method_returning_a_field_with_no_return_type_declares_the_fields_width()
    {
        // #519: `def helper(self): return self.base` kept the "void" default, the caller took
        // the call for void and read the return register at its own width -- the right low
        // byte under a garbage high byte from the second expansion on.
        var (ir, _) = GenSeeded(
            "class Dev:\n" +
            "    def __init__(self, base: uint8):\n" +
            "        self.base: uint8 = base\n" +
            "    def helper(self):\n" +
            "        return self.base\n" +
            "    @inline\n" +
            "    def probe(self, k: uint8):\n" +
            "        return self.helper() * 10 + k\n" +
            "w: uint8 = 3\n" +
            "a = Dev(w)\n" +
            "b = Dev(w + 7)\n" +
            "r: uint16 = a.probe(1)\n" +
            "q: uint16 = b.probe(2)\n");
        ir.Functions.Should().Contain(f => f.Name.EndsWith("helper"))
            .Which.ReturnType.Should().Be(DataType.UINT8);
    }

    [Fact]
    public void An_unannotated_inline_parameter_bound_to_a_variable_reads_at_its_width()
    {
        // `@inline def f(x): print(x)` with `t: uint32` printed t's low byte: the parameter
        // aliased the variable but carried the uint8 default as its own type.
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(
                "r: uint32 = 0\n" +
                "@inline\n" +
                "def f(x):\n" +
                "    global r\n" +
                "    r = x\n" +
                "def g(t: uint32):\n" +
                "    f(t)\n" +
                "g(946684800)\n").Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        var stores = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable { Name: "r" } && c.Src is Variable).ToList();
        stores.Should().NotBeEmpty();
        stores.Should().OnlyContain(c => ((Variable)c.Src).Type == DataType.UINT32);
    }

    [Fact]
    public void A_field_stored_from_a_nested_inline_keeps_the_declared_result_width()
    {
        // mpbus: `self._top = top_of(freq)` where top_of is `-> uint16` and calls another
        // @inline returning 1 or 8. The field took the byte width of that constant and kept
        // 15999 & 0xFF = 127, even with an explicit uint16() cast around the division.
        var (ir, _) = GenSeeded(
            "@inline\n" +
            "def divider(freq: uint16) -> uint16:\n" +
            "    if freq > 244:\n" +
            "        return 1\n" +
            "    return 8\n" +
            "@inline\n" +
            "def top_of(freq: uint16) -> uint16:\n" +
            "    return uint16(16000000 // (divider(freq) * freq) - 1)\n" +
            "class P:\n" +
            "    def __init__(self, freq: uint16):\n" +
            "        self._top = top_of(freq)\n" +
            "    @inline\n" +
            "    def top(self) -> uint16:\n" +
            "        return self._top\n" +
            "p = P(1000)\n" +
            "r: uint16 = p.top()\n");
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Src is Constant { Value: 127 }).Should().BeEmpty();
    }

    private static string WarningsOf(string src)
    {
        var saved = Console.Error;
        var buf = new StringWriter();
        Console.SetError(buf);
        try { GenSeeded(src); return buf.ToString(); }
        finally { Console.SetError(saved); }
    }

    private static string Accumulator(string decl) =>
        "def f(d: uint8) -> uint16:\n" +
        "    " + decl + "\n" +
        "    i: uint8 = 0\n" +
        "    while i < 10:\n" +
        "        c += 1\n" +
        "        i += 1\n" +
        "    return c\n" +
        "r: uint16 = f(3)\n";

    [Fact]
    public void An_unannotated_accumulator_in_a_loop_is_told_its_width()
    {
        // `c = GPIOR0.value` then `c += 1` three hundred times printed 44. No width holds
        // every count a loop can reach, so the width stays -- and the program is told which
        // one it is, and where, instead of wrapping in silence.
        WarningsOf(Accumulator("c = d")).Should()
            .Contain("'c' has no annotation").And.Contain("uint8 (0..255)").And.Contain("line 5");
    }

    [Fact]
    public void An_annotated_accumulator_is_the_programs_decision_and_says_nothing()
    {
        WarningsOf(Accumulator("c: uint16 = d")).Should().NotContain("has no annotation");
    }

    [Fact]
    public void A_program_whose_slots_all_fit_compiles_once()
    {
        // The zero-cost half: nothing too narrow, no second run, the same IR as before.
        var (_, runs) = GenSeeded(
            "def f(d: uint8) -> uint16:\n" +
            "    e = d + 300\n" +
            "    return e\n" +
            "r: uint16 = f(3)\n");
        runs.Should().Be(1);
    }

    [Fact]
    public void An_inline_local_minted_wide_in_a_later_expansion_stores_wide_from_the_start()
    {
        // surfacecov-neopixel's `_set_item.offset`: every expansion of an @inline callee
        // mints its locals under the same generated name -- `inline1.put.off` here -- at
        // whatever width THAT call's value carries. The first call's folded 0 gives a
        // byte, the second's u8 index a u16 product, and `j` a u32-range sum whose store
        // into the u16 slot files the seed. On the widened run the FIRST expansion still
        // minted its byte -- a u8 store under the name a later expansion uses at u32.
        // The mint now starts at the seed, so the widened run is consistent end to end.
        // (The byte store on the FIRST run, before any seed exists, is the shape the
        // corpus baseline already admits; the bug was it surviving into the seed's run.)
        var seeds = new WidthSeeds();
        ProgramIR lastIr = null!;
        for (int run = 1; ; run++)
        {
            seeds.BeginRun();
            lastIr = new IRGenerator { WidthSeeds = seeds }.Generate(
                new Parser(new Lexer(
                    "base: uint8 = 3\n" +
                    "a: list[uint8] = [0] * 8\n" +
                    "@inline\n" +
                    "def put(idx, v):\n" +
                    "    off = base + idx * 700\n" +
                    "    a[off] = v\n" +
                    "def main():\n" +
                    "    put(0, 7)\n" +
                    "    i8: uint8 = 5\n" +
                    "    put(i8, i8)\n" +
                    "    j: uint16 = 300\n" +
                    "    put(j, 7)\n" +
                    "main()\n").Tokenize()).ParseProgram(),
                new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" });
            if (!seeds.Grew || run == 5) break;
        }
        Verifier.Verify(lastIr)
            .Where(v => v.Check == "storage-width" && v.Detail.Contains("put.off"))
            .Should().BeEmpty();
    }

    [Fact]
    public void A_seed_filed_mid_run_widens_the_next_run_not_this_one()
    {
        // nested-inline-closes-over-self's `c`: `self.value = self.value + 1` is a member
        // store, so it answers for the promoted sum -- u8+1 asks u16 mid-run 1, u16+1 asks
        // u32 mid-run 2. A floor that read the seed the moment it was filed widened the
        // later stores of the SAME run and left its earlier ones narrow: run 1 emitted
        // u8,u8,u16 under one name and the per-run verify pass saw the miscompile it
        // exists to catch. The floor is the seed the run began with, so every run stays
        // a single width and the cascade converges instead of self-reporting.
        var seeds = new WidthSeeds();
        var violations = new List<string>();
        ProgramIR lastIr = null!;
        for (int run = 1; ; run++)
        {
            seeds.BeginRun();
            lastIr = new IRGenerator { WidthSeeds = seeds }.Generate(
                new Parser(new Lexer(
                    "class Counter:\n" +
                    "    def __init__(self, start: uint8):\n" +
                    "        self.value = start\n" +
                    "    def bump_twice(self):\n" +
                    "        @inline\n" +
                    "        def bump():\n" +
                    "            self.value = self.value + 1\n" +
                    "        bump()\n" +
                    "        bump()\n" +
                    "        return self.value\n" +
                    "c = Counter(5)\n" +
                    "r: uint16 = c.bump_twice()\n").Tokenize()).ParseProgram(),
                new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" });
            violations.AddRange(Verifier.Verify(lastIr)
                .Where(v => v.Check == "storage-width" && v.Detail.Contains("'c'"))
                .Select(v => $"run{run}: {v.Detail}"));
            if (!seeds.Grew || run == 5) break;
        }
        violations.Should().BeEmpty();
        ShouldHold(lastIr, "c", 0, uint.MaxValue);
    }

    [Fact]
    public void ARebindToAWiderValueThanAnEarlierBinding_SeedsAConsistentWidth()
    {
        // `x = buf[0]` (a byte) then `for x in (buf,): pass` (rebinds `x` to the
        // buffer's own address, 16 bits on AVR) -- PyMCU-review round 5. The for-loop-
        // variable rebind needed more width than the earlier binding of the same bare
        // name assumed, the same shape every other unannotated-slot width case is: a
        // seed, a rerun, and the settled run holds 'f.x' at one consistent width
        // throughout -- not split across a UINT8 write and a UINT16 one, which the
        // backend's single physical slot per name cannot both satisfy.
        var (ir, _) = GenSeeded(
            "from pymcu.types import uint8\n" +
            "def head(v: bytearray) -> uint8:\n" +
            "    return v[0]\n" +
            "def f(buf: bytearray) -> uint8:\n" +
            "    x = buf[0]\n" +
            "    for x in (buf,):\n" +
            "        pass\n" +
            "    return head(x)\n" +
            "def main():\n" +
            "    a: uint8 = f(bytearray([10, 20, 30]))\n");

        var violations = Verifier.Verify(ir)
            .Where(v => v.Check == "storage-width" && v.Detail.Contains("'f.x'"))
            .ToList();
        violations.Should().BeEmpty();
    }

    [Fact]
    public void ABreakInATupleOfMixedWidths_KeepsTheWiderType()
    {
        // `for x in (b, a): break` with b: uint16, a: uint8 -- PyMCU-review round 7.
        // Unconditionally setting variableTypes to each element's OWN width in turn left
        // 'f.x' at uint8 (a's width, the LAST unrolled element) even though break fires
        // on the FIRST iteration, when x holds b's full 16 bits. The type after the loop
        // has to be the widest of every element AND whatever the name held before the
        // loop, not whichever element happened to unroll last -- and the EARLIER `x = a`
        // binding needs the same seeded rerun round 5/6 already use, since nothing can
        // rewrite its own Copy after the fact.
        var (ir, _) = GenSeeded(
            "from pymcu.types import uint8, uint16\n" +
            "def f(a: uint8, b: uint16) -> uint16:\n" +
            "    x = a\n" +
            "    for x in (b, a):\n" +
            "        break\n" +
            "    return x\n" +
            "def main():\n" +
            "    r: uint16 = f(1, 300)\n");

        var violations = Verifier.Verify(ir)
            .Where(v => v.Check == "storage-width" && v.Detail.Contains("'f.x'"))
            .ToList();
        violations.Should().BeEmpty();
    }

    [Fact]
    public void ATupleOfMixedWidthsWithNoBreak_KeepsTheWiderType()
    {
        // The no-break sibling: the loop runs to completion and ends at the LAST element
        // (a: uint8), but every iteration in between still has to use the SAME
        // consistent width (uint16, b's own) -- the backend homes one slot per name at
        // one width for every write into it, not a different width per iteration.
        var (ir, _) = GenSeeded(
            "from pymcu.types import uint8, uint16\n" +
            "def f(a: uint8, b: uint16) -> uint16:\n" +
            "    x = a\n" +
            "    for x in (b, a):\n" +
            "        pass\n" +
            "    return x\n" +
            "def main():\n" +
            "    r: uint16 = f(1, 300)\n");

        var violations = Verifier.Verify(ir)
            .Where(v => v.Check == "storage-width" && v.Detail.Contains("'f.x'"))
            .ToList();
        violations.Should().BeEmpty();
    }

    [Fact]
    public void ASignedAndUnsignedMix_JoinsToASignedWiderType()
    {
        // The signed/unsigned sibling: a: int8, b: uint16. Joining a signed and an
        // unsigned type for the widest-width decision needs a signed type one step above
        // the unsigned one (int32), not just the larger of the two byte counts, or a
        // negative `a` reads back wrong once it shares a slot with an unsigned, wider
        // element.
        var (ir, _) = GenSeeded(
            "from pymcu.types import int8, uint16\n" +
            "def f(a: int8, b: uint16) -> int32:\n" +
            "    x = a\n" +
            "    for x in (b, a):\n" +
            "        break\n" +
            "    return x\n" +
            "def main():\n" +
            "    r: int32 = f(-1, 300)\n");

        var violations = Verifier.Verify(ir)
            .Where(v => v.Check == "storage-width" && v.Detail.Contains("'f.x'"))
            .ToList();
        violations.Should().BeEmpty();
    }
}

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
        ShouldHold(ir, "inline1.f.x", 900, 65535 + 900L);
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
            .Where(c => c.Dst is Variable { Name: "inline1.f.n" })
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
}

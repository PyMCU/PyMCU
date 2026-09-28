using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A single-field scalar class collapses onto the field itself: `f` IS `self.value`. The
/// collapsed handle must carry the field's declared (or seed-widened) width at every point
/// that touches it -- construction, `.value` reads and writes, `+=`, parameter binding,
/// returns and factories. Each path used to disagree: the constructor minted the anchor at
/// the byte its first store happened to be, a `.value` write reached the pointer rules and
/// either truncated at u8 or was refused ("16-bit .value assignment requires constant
/// address"), and an unannotated `return self.value` read the register-tables' u8 default
/// for a module global, so the call's result temp minted a byte and truncated the field.
///
/// These tests run the generator the way the pipeline does -- again while a slot asks for
/// more -- and pin that every mention of the anchor holds the values the program stores.
/// </summary>
public class CollapsedFieldAnchorTests
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

    private static IEnumerable<Variable> VariablesNamed(ProgramIR ir, string name) =>
        ir.Functions.SelectMany(f => f.Body).SelectMany(Operands)
            .OfType<Variable>().Where(v => v.Name == name);

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

    /// Every copy into <paramref name="dstName"/> comes from a value whose type holds
    /// <paramref name="max"/> -- the read side of `r: uint16 = f.up(300)` is where the
    /// collapsed handle's real width shows up.
    private static void ResultSrcHolds(ProgramIR ir, string dstName, long max)
    {
        var copies = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name == dstName).ToList();
        copies.Should().NotBeEmpty($"'{dstName}' is assigned");
        foreach (var c in copies)
        {
            var t = c.Src switch
            {
                Variable v => v.Type,
                Temporary t2 => t2.Type,
                MemoryAddress m => m.Type,
                Constant k => NarrowestFor(k.Value),
                _ => DataType.UINT8,
            };
            RangeOf(t).Hi.Should().BeGreaterThanOrEqualTo(max,
                $"the value stored into '{dstName}' is {t} but {max} was stored");
        }
    }

    private static (long Lo, long Hi) RangeOf(DataType t) => t switch
    {
        DataType.UINT8 => (0, 255), DataType.INT8 => (-128, 127),
        DataType.UINT16 => (0, 65535), DataType.INT16 => (-32768, 32767),
        DataType.UINT32 => (0, uint.MaxValue), _ => (int.MinValue, int.MaxValue),
    };

    private static DataType NarrowestFor(long v) => v switch
    {
        >= 0 and <= 255 => DataType.UINT8,
        >= 0 and <= 65535 => DataType.UINT16,
        >= 0 => DataType.UINT32,
        _ => DataType.INT32,
    };

    // A register read keeps every value runtime -- nothing here may fold to a constant.
    private const string Reg =
        "from pymcu.types import uint8, uint16, uint32, inline, ptr\n" +
        "P: ptr[uint8] = ptr(0x3E)\n";

    [Fact]
    public void A_declared_wide_field_returns_at_its_width()
    {
        // `up`'s result temp minted u8 off a return annotation seeded while the field was
        // still a byte, and `print(f.up(300))` read the low byte back (300 -> 44).
        var (ir, _) = GenSeeded(Reg +
            "class Fader:\n" +
            "    def __init__(self):\n" +
            "        self.value: uint16 = P.value\n" +
            "    def up(self, n):\n" +
            "        self.value = self.value + n\n" +
            "        return self.value\n" +
            "f = Fader()\n" +
            "r: uint16 = f.up(300)\n");
        ShouldHold(ir, "f", 0, 300);
        ResultSrcHolds(ir, "r", 300);
    }

    [Fact]
    public void A_value_write_on_a_collapsed_anchor_is_a_variable_store()
    {
        // `f.value = 300` went through the register path: at u8 it silently truncated, at
        // u16 it was refused outright -- and the ctor's `self.value: uint16 = P.value`
        // filed the anchor as a pointer ALIAS, so the write could land on the register
        // itself. The anchor is the field's scalar: a plain variable store, read back.
        var (ir, _) = GenSeeded(Reg +
            "class Fader:\n" +
            "    def __init__(self):\n" +
            "        self.value: uint16 = P.value\n" +
            "f = Fader()\n" +
            "f.value = 300\n" +
            "r: uint16 = f.value\n");
        ir.Functions.SelectMany(fn => fn.Body).Where(i =>
            i is Copy { Dst: Variable v } && v.Name == "f" && RangeOf(v.Type).Hi >= 300)
            .Should().NotBeEmpty("the store must land on the variable 'f' at a width holding 300");
        ResultSrcHolds(ir, "r", 300);
    }

    [Fact]
    public void A_value_aug_assign_on_a_collapsed_anchor_keeps_the_field_width()
    {
        // `f.value += n` used to fail to compile at all: the augmented-assignment member
        // path only knew pointers and register addresses, and a Variable target fell
        // through to "requires a pointer or register target".
        var (ir, _) = GenSeeded(Reg +
            "class Fader:\n" +
            "    def __init__(self):\n" +
            "        self.value: uint16 = P.value\n" +
            "f = Fader()\n" +
            "f.value += 300\n" +
            "r: uint16 = f.value\n");
        ShouldHold(ir, "f", 0, 300);
        ResultSrcHolds(ir, "r", 300);
    }

    [Fact]
    public void An_unannotated_field_widened_by_a_store_returns_wide()
    {
        // No annotation anywhere: `self.value = P.value` lays the field out at u8 and the
        // `+ n` store widens it through the seed machinery; the return must follow it.
        var (ir, _) = GenSeeded(Reg +
            "class Fader:\n" +
            "    def __init__(self):\n" +
            "        self.value = P.value\n" +
            "    def up(self, n):\n" +
            "        self.value = self.value + n\n" +
            "        return self.value\n" +
            "f = Fader()\n" +
            "r: uint16 = f.up(300)\n");
        ShouldHold(ir, "f", 0, 300);
        ResultSrcHolds(ir, "r", 300);
    }

    [Fact]
    public void An_inline_method_returns_a_32_bit_argument_whole()
    {
        // `set_to`'s unannotated return and `value`'s store both saw the uint32 argument
        // only through a byte-wide mint: 946684800 came back as 128.
        var (ir, _) = GenSeeded(Reg +
            "class Fader:\n" +
            "    def __init__(self):\n" +
            "        self.value: uint16 = P.value\n" +
            "    @inline\n" +
            "    def set_to(self, n):\n" +
            "        self.value = n\n" +
            "        return self.value\n" +
            "t: uint32 = 946684800 + P.value\n" +
            "f = Fader()\n" +
            "r: uint32 = f.set_to(t)\n");
        ResultSrcHolds(ir, "r", 946684800);
    }

    [Fact]
    public void An_inline_function_taking_the_object_writes_the_anchor()
    {
        // `o.value = ...` where `o` binds the collapsed instance: the write went to the
        // pointer rules and either truncated or was refused; the return read back u8.
        var (ir, _) = GenSeeded(Reg +
            "class Fader:\n" +
            "    def __init__(self):\n" +
            "        self.value: uint16 = P.value\n" +
            "@inline\n" +
            "def bump(o, n):\n" +
            "    o.value = o.value + n\n" +
            "    return o.value\n" +
            "f = Fader()\n" +
            "r: uint16 = bump(f, 300)\n" +
            "s: uint16 = f.value\n");
        ResultSrcHolds(ir, "r", 300);
        ResultSrcHolds(ir, "s", 300);
    }

    [Fact]
    public void An_inline_parameter_named_self_binds_the_anchor()
    {
        // Same shape with the receiver parameter spelled `self` -- the alias machinery
        // treated the two spellings differently.
        var (ir, _) = GenSeeded(Reg +
            "class Fader:\n" +
            "    def __init__(self):\n" +
            "        self.value: uint16 = P.value\n" +
            "@inline\n" +
            "def bump(self, n):\n" +
            "    self.value = self.value + n\n" +
            "    return self.value\n" +
            "f = Fader()\n" +
            "r: uint16 = bump(f, 300)\n");
        ResultSrcHolds(ir, "r", 300);
    }

    [Fact]
    public void An_annotated_scalar_field_reads_the_register_once_not_forever()
    {
        // `self.tag: uint16 = P.value` on a MULTI-field class filed f_tag as a pointer
        // alias to the register, so `f.tag` reads answered the register's live contents
        // instead of the snapshot the constructor took -- and a later `f.tag = v` wrote
        // I/O space instead of the field. An annotated scalar field holds the byte read.
        var (ir, _) = GenSeeded(Reg +
            "class Fader:\n" +
            "    def __init__(self):\n" +
            "        self.value: uint16 = P.value\n" +
            "        self.tag: uint16 = P.value\n" +
            "f = Fader()\n" +
            "f.value = 300\n" +
            "r: uint16 = f.value\n" +
            "t: uint16 = f.tag\n");
        ResultSrcHolds(ir, "r", 300);
        // The snapshot is a variable, not a live register read.
        var tagReads = ir.Functions.SelectMany(fn => fn.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name == "t").ToList();
        tagReads.Should().NotBeEmpty();
        tagReads.All(c => c.Src is Variable or Temporary).Should().BeTrue(
            "f.tag is the stored field, not a dereference of register 0x3E");
    }

    [Fact]
    public void A_seeded_anchor_stores_at_the_slot_width_from_the_first_store()
    {
        // zca-method-loop-return: the constructor's `self.value = 0` stores into the
        // collapsed anchor `main.f` BEFORE the assignment that mints it runs, so a run
        // that already knew the slot needed u16 still emitted the byte store while the
        // loop's later writes were u16 -- a u8 write under u16 reads. The anchor's own
        // seed now floors CollapsedAnchorWidth, and every run writes it at one width.
        var seeds = new WidthSeeds();
        for (int run = 1; ; run++)
        {
            seeds.BeginRun();
            var ir = new IRGenerator { WidthSeeds = seeds }.Generate(
                new Parser(new Lexer(
                    "class Fader:\n" +
                    "    def __init__(self):\n" +
                    "        self.value = 0\n" +
                    "    def up(self, n):\n" +
                    "        for i in range(n):\n" +
                    "            self.value = self.value + i\n" +
                    "        return self.value\n" +
                    "def consume(x: uint16):\n" +
                    "    pass\n" +
                    "def main():\n" +
                    "    f = Fader()\n" +
                    "    consume(f.up(10))\n" +
                    "main()\n").Tokenize()).ParseProgram(),
                new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" });
            Verifier.Verify(ir)
                .Where(v => v.Check == "storage-width" && v.Detail.Contains("'main.f'"))
                .Should().BeEmpty($"run {run} must write 'main.f' at the slot's width");
            if (!seeds.Grew || run == 5) return;
        }
    }

    [Fact]
    public void A_factory_returned_anchor_carries_the_field_width()
    {
        // The factory hands the collapsed field back as the handle; `bump` then writes
        // through `o.value`, which refused or truncated before.
        var (ir, _) = GenSeeded(Reg +
            "class Fader:\n" +
            "    def __init__(self, v: uint16):\n" +
            "        self.value: uint16 = v\n" +
            "def make(v: uint16) -> Fader:\n" +
            "    return Fader(v)\n" +
            "@inline\n" +
            "def bump(o, n):\n" +
            "    o.value = o.value + n\n" +
            "    return o.value\n" +
            "f = make(P.value + 7)\n" +
            "r: uint16 = bump(f, 300)\n");
        ResultSrcHolds(ir, "r", 307);
    }
}

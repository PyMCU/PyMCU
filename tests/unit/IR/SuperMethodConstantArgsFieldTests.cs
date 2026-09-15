using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#430. `super().describe() + self.extra` computed the wrong result when a subclass adds
/// its own field on top of the base's, for two INDEPENDENT reasons compounding on the exact
/// reported shape:
///
/// 1. `EmitUnboundMethodBody` (Call.cs) allocated its result temp up front from the base
///    method's declared return type -- but an UNANNOTATED `def describe(self): ...` parses as
///    "void" (Parser.cs's default), so no temp was ever allocated. `VisitReturn` already has a
///    lazy fallback for exactly this ("the first value return decides the width"), but it
///    mutates the `InlineContext` object, and `EmitUnboundMethodBody` returned its own STALE
///    local copy of the temp reference instead of reading the mutated context back: every
///    super() call to an unannotated method answered NoneVal, regardless of field names.
///
/// 2. A field literally named "value" (the reported shape's own field, but any class can spell
///    a field this way) collides with the compiler's MMIO/pointer `.value` read AND write
///    convention. The read side already guarded against this for a LIVE slot instance; the
///    write side had no guard at all. Neither guard covered a receiver whose constructor
///    arguments folded entirely to compile-time constants (RFC 0001's "fast construction path"
///    allocates a slot only when something needs one at run time) -- so a `self.value = value`
///    super() delegation into such a receiver silently dropped the store, and the later read
///    landed on an uninitialized flattened variable instead of the field's real value.
/// </summary>
public class SuperMethodConstantArgsFieldTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8\n\n\n";

    [Fact]
    public void AnUnannotatedSuperMethodCallCarriesItsReturnValueOut()
    {
        // Isolates reason 1 above, with no field named "value" anywhere in the picture:
        // `describe` returns a value from an UNANNOTATED super() call, which used to be
        // NoneVal regardless of what the base method actually computed.
        var ir = Gen(Preamble +
            "class Base:\n" +
            "    def measure(self):\n" +
            "        return GPIOR0.value + 1\n\n" +
            "class Sub(Base):\n" +
            "    def measure(self):\n" +
            "        return super().measure() + 1\n\n" +
            "s = Sub()\n" +
            "GPIOR1.value = s.measure()\n");

        // The chain must fold to seed + 2 (one +1 in Base, one in Sub) -- not a NoneVal
        // swallowing the base call's contribution.
        var stores = ir.Functions.SelectMany(f => f.Body).OfType<Binary>()
            .Where(b => b.Src2 is Constant { Value: 1 })
            .ToList();
        Assert.True(stores.Count >= 1, "expected at least the Sub-level +1 to survive lowering");
        Assert.DoesNotContain(
            ir.Functions.SelectMany(f => f.Body).OfType<Binary>(),
            b => b.Src1 is NoneVal || b.Src2 is NoneVal);
    }

    [Fact]
    public void ASubclassAddedFieldWithConstantArgsAndAFieldNamedValueComputesCorrectly()
    {
        // The exact reported shape. GPIOR0-seeded, not a literal, so this measures the
        // runtime value thread; the ORIGINAL bug was specific to constant constructor
        // arguments, so a sibling assertion below pins that exact form too.
        var ir = Gen(Preamble +
            "class Base:\n" +
            "    def __init__(self, value):\n" +
            "        self.value = value\n" +
            "    def describe(self):\n" +
            "        return self.value\n\n" +
            "class Sub(Base):\n" +
            "    def __init__(self, value, extra):\n" +
            "        super().__init__(value)\n" +
            "        self.extra = extra\n" +
            "    def describe(self):\n" +
            "        return super().describe() + self.extra\n\n" +
            "s = Sub(GPIOR0.value, 4)\n" +
            "GPIOR1.value = s.describe()\n");

        Assert.DoesNotContain(
            ir.Functions.SelectMany(f => f.Body).OfType<Binary>(),
            b => b.Src1 is NoneVal || b.Src2 is NoneVal);

        // No literal constant args here, so the field can't be folded -- self.value must be a
        // real runtime read that reaches this Binary's left operand as a Variable, chased all
        // the way to the instance's own storage, not left as an orphaned inline-frame name.
        var addOps = ir.Functions.SelectMany(f => f.Body).OfType<Binary>()
            .Where(b => b.Op == IrBinaryOp.Add && b.Src2 is Constant { Value: 4 })
            .ToList();
        Assert.Single(addOps);
        var left = addOps[0].Src1;
        string? leftName = left switch { Variable v => v.Name, Temporary t => t.Name, _ => null };
        Assert.NotNull(leftName);
        Assert.DoesNotContain("describe", leftName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportedShapeWithConstantConstructorArgumentsComputesCorrectly()
    {
        // The precise reproduction from the issue: Sub(3, 4).describe() must fold to 7 (the
        // family of bugs this resembles -- constant folding crossing function boundaries --
        // is specifically about constant arguments behaving differently from runtime ones).
        var ir = Gen(Preamble +
            "class Base:\n" +
            "    def __init__(self, value):\n" +
            "        self.value = value\n" +
            "    def describe(self):\n" +
            "        return self.value\n\n" +
            "class Sub(Base):\n" +
            "    def __init__(self, value, extra):\n" +
            "        super().__init__(value)\n" +
            "        self.extra = extra\n" +
            "    def describe(self):\n" +
            "        return super().describe() + self.extra\n\n" +
            "def report(obj: Sub) -> int:\n" +
            "    return obj.describe()\n\n" +
            "s = Sub(3, 4)\n" +
            "GPIOR1.value = report(s)\n");

        // Nothing computed from super().describe() may be a NoneVal (reason 1), and nothing
        // may read back an orphaned inline-frame "self" (reason 2) -- together these are what
        // let 3 + 4 fold to 7 instead of miscomputing.
        Assert.DoesNotContain(
            ir.Functions.SelectMany(f => f.Body).OfType<Binary>(),
            b => b.Src1 is NoneVal || b.Src2 is NoneVal);
        var allNames = ir.Functions.SelectMany(f => f.Body)
            .SelectMany(instr => instr switch
            {
                Copy c => new[] { c.Src, c.Dst },
                Binary b => new[] { b.Src1, b.Src2, (Val)b.Dst },
                _ => Array.Empty<Val>(),
            })
            .Select(v => v switch { Variable vv => vv.Name, Temporary t => t.Name, _ => null })
            .Where(n => n != null);
        Assert.DoesNotContain(allNames, n => n!.Contains("describe", StringComparison.Ordinal));
    }
}

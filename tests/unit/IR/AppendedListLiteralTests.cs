using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `value = [a, b, c]` followed by `value.append(d)` in the same function: the literal's
/// length is a run-time fact, so the name is a heap list whose element type the elements
/// give, the way `x = []` + append is promoted. It was refused as an untyped `[]` with
/// no runtime list, which is what unmodified adafruit_pixelbuf's `_getitem` hit (the
/// white channel is appended under `if self._has_white`).
///
/// Nothing DECLARED the element type, so an append of a value the elements cannot hold
/// is refused rather than stored in their width.
/// </summary>
public class AppendedListLiteralTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void ARuntimeLiteralThenAppend_IsAHeapList()
    {
        var ir = Gen(
            "from pymcu.types import uint8, ptr\n" +
            "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
            "def f(w: uint8) -> uint8:\n" +
            "    v = [GPIOR0.value, GPIOR0.value + 1, GPIOR0.value + 2]\n" +
            "    if w:\n" +
            "        v.append(GPIOR0.value + 3)\n" +
            "    return len(v)\n" +
            "n: uint8 = f(GPIOR0.value)\n");
        var fn = Assert.Single(ir.Functions, f => f.Name == "f");
        Assert.Contains(fn.Body, i => i is GcAlloc);
        Assert.Contains(fn.Body, i => i is StoreIndirect { Elem: DataType.UINT8 });
    }

    [Fact]
    public void AConstantLiteralThenAppend_IsNotACompileTimeSequence()
    {
        // All-constant and short: the literal would otherwise be a compile-time sequence,
        // which has no length to grow.
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "def f() -> uint8:\n" +
            "    v = [1, 2, 3]\n" +
            "    v.append(4)\n" +
            "    return len(v)\n" +
            "n: uint8 = f()\n");
        var fn = Assert.Single(ir.Functions, f => f.Name == "f");
        Assert.Contains(fn.Body, i => i is GcAlloc);
    }

    [Fact]
    public void AnAppendUnderAFoldedBranch_IsNotPromoted()
    {
        // adafruit_pixelbuf's `_getitem` appends the white channel under
        // `if self._has_white`, which folds to False for an RGB strip -- the dead
        // append must not promote the literal, or every read pays for a heap list.
        var ir = Gen(
            "from pymcu.types import uint8, ptr\n" +
            "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
            "def f() -> uint8:\n" +
            "    v = [GPIOR0.value, GPIOR0.value + 1]\n" +
            "    if False:\n" +
            "        v.append(GPIOR0.value + 2)\n" +
            "    return v[0]\n" +
            "n: uint8 = f()\n");
        var fn = Assert.Single(ir.Functions, f => f.Name == "f");
        Assert.DoesNotContain(fn.Body, i => i is GcAlloc);
    }

    [Fact]
    public void AnAppendUnderAFoldedConstantBranch_IsNotPromoted()
    {
        // Same dead append, decided through a compile-time name instead of a literal.
        var ir = Gen(
            "from pymcu.types import uint8, ptr\n" +
            "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
            "HAS_WHITE = 0\n" +
            "def f() -> uint8:\n" +
            "    v = [GPIOR0.value, GPIOR0.value + 1]\n" +
            "    if HAS_WHITE:\n" +
            "        v.append(GPIOR0.value + 2)\n" +
            "    return v[0]\n" +
            "n: uint8 = f()\n");
        var fn = Assert.Single(ir.Functions, f => f.Name == "f");
        Assert.DoesNotContain(fn.Body, i => i is GcAlloc);
    }

    [Fact]
    public void AnAppendWiderThanTheInferredElements_IsRefused()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(
            "from pymcu.types import uint8\n" +
            "def f() -> uint8:\n" +
            "    v = [1, 2]\n" +
            "    v.append(300)\n" +
            "    return len(v)\n" +
            "n: uint8 = f()\n"));
        Assert.Contains("list[uint16]", ex.Message);
        Assert.Equal(4, ex.Line);
    }
}

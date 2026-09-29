using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The descriptor protocol (PyMCU#360/#419) reaches `inst.attr` through TryDescriptorRead /
/// TryDescriptorWrite, but a class attribute named exactly "value" never got there: Expr.cs and
/// Assign.cs both special-case `.value` FIRST, for the MMIO register load/store (`PORTB.value`)
/// and the single-field-instance collapse, and that special case ran unconditionally, before the
/// descriptor check could ever be asked. `class Slot: def __get__(...)` used as `class Box:
/// value = Slot()` -- the exact shape adafruit_register and digitalio spell their descriptors
/// with -- fell through to the `.value` shortcut and answered with un-constructed storage
/// instead of calling __get__/__set__, silently, with no diagnostic.
///
/// DescriptorProtocolTests pins the general mechanism with an attribute named "reg"; these pin
/// the same mechanism for the one name that collides with the MMIO shortcut, plus the two shapes
/// that have no correct compiled answer and must be refused instead of guessing.
/// </summary>
public class DescriptorValueNameCollisionTests
{
    private static ProgramIR Gen(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return Optimizer.Optimize(ir);
    }

    private static Val LastStored(ProgramIR ir, int slot) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    private const string DataDescriptorProgram =
        "buf = bytearray([0, 0, 0, 0])\n" +
        "class Slot:\n" +
        "    def __get__(self, obj, objtype=None) -> uint8:\n" +
        "        return obj.raw + 1\n" +
        "    def __set__(self, obj, v: uint8) -> None:\n" +
        "        obj.raw = v * 2\n" +
        "class Box:\n" +
        "    value = Slot()\n" +
        "    def __init__(self) -> None:\n" +
        "        self.raw = 3\n" +
        "b = Box()\n";

    // `b.value` must be the descriptor's answer (3 + 1 = 4), not the MMIO/collapsed-scalar
    // shortcut's 0.
    [Fact]
    public void ReadingADataDescriptorNamedValue_CallsGet()
    {
        var ir = Gen(DataDescriptorProgram + "buf[0] = b.value\n");

        Assert.Equal(new Constant(4), LastStored(ir, 0));
    }

    // `b.value = 5` must reach __set__ (raw = 5 * 2 = 10), not the MMIO/collapsed-scalar
    // shortcut, which wrote 5 into storage __get__ never reads.
    [Fact]
    public void WritingADataDescriptorNamedValue_CallsSet()
    {
        var ir = Gen(DataDescriptorProgram +
            "b.value = 5\n" +
            "buf[1] = b.raw\n");

        Assert.Equal(new Constant(10), LastStored(ir, 1));
    }

    // A class attribute named "value" whose class defines __get__ but not __set__ is a
    // non-data descriptor. CPython lets `b.value = 5` there create a per-instance override in
    // b.__dict__ that shadows the class attribute for every later read; PyMCU lays instances
    // out at compile time and has no per-instance dict to create one in. Refused by name
    // instead of silently taking the MMIO/collapsed-scalar write path, which stored the value
    // where the descriptor's own __get__ never looks.
    [Fact]
    public void WritingANonDataDescriptorNamedValue_IsRefused()
    {
        var src =
            "buf = bytearray([0, 0])\n" +
            "class Slot:\n" +
            "    def __get__(self, obj, objtype=None) -> uint8:\n" +
            "        return obj.raw + 1\n" +
            "class Box:\n" +
            "    value = Slot()\n" +
            "    def __init__(self) -> None:\n" +
            "        self.raw = 3\n" +
            "b = Box()\n" +
            "b.value = 5\n";

        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src));
        Assert.Contains("non-data descriptor", ex.Message);
    }

    // `Box.value` -- reading a descriptor through the class itself, not through an instance.
    // CPython calls `type(attr).__get__(attr, None, Box)`; PyMCU's rewrite always evaluates a
    // receiver INSTANCE to pass as obj, and there is no instance in a class-level read. Refused
    // by name instead of reading the flattened, never-constructed `Box_value` slot as a plain
    // value.
    [Fact]
    public void ReadingADescriptorThroughTheClassItself_IsRefused()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            DataDescriptorProgram + "buf[0] = Box.value\n"));
        Assert.Contains("through the class itself", ex.Message);
    }

    // A descriptor whose class defines __set_name__ expects CPython to call it once, at class
    // creation, so the descriptor learns its own owner and attribute name. PyMCU's rewrite
    // never calls it -- nothing runs user code at "the class body finished evaluating" time --
    // so a descriptor that reads what __set_name__ would have stored answers with
    // never-initialised storage instead. Refused by name on every access, not just the ones
    // that would observably differ: whether __set_name__'s effect is actually read back is not
    // something the compiler tries to prove.
    [Fact]
    public void ADescriptorDefiningSetName_IsRefused()
    {
        var src =
            "buf = bytearray([0, 0])\n" +
            "class Slot:\n" +
            "    def __set_name__(self, owner, name) -> None:\n" +
            "        pass\n" +
            "    def __get__(self, obj, objtype=None) -> uint8:\n" +
            "        return obj.raw + 1\n" +
            "class Box:\n" +
            "    value = Slot()\n" +
            "    def __init__(self) -> None:\n" +
            "        self.raw = 3\n" +
            "b = Box()\n" +
            "buf[0] = b.value\n";

        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src));
        Assert.Contains("__set_name__", ex.Message);
    }
}

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#268. A class-level attribute is reachable through the class name and invisible through
/// an instance, on a program CPython runs:
///
///     class Cell:
///         LIMIT = 7
///     c = Cell(2)
///     print(c.LIMIT)      # CPython 7, PyMCU: 'Cell' has no attribute 'LIMIT'
///
/// The instance read builds the flattened name `c_LIMIT`, which nothing registers, and the
/// class's own namespace is never consulted. `Cell.LIMIT` works, so the value exists and only
/// the lookup through the receiver is missing.
///
/// Three shapes, because the fix has to reach all three: a folded ALL-CAPS constant, a
/// run-time class attribute whose slot already exists, and an attribute declared on a base
/// class. Class attributes do not inherit today either -- inheritance copies methods and
/// leaves `globals` and `mutableGlobals` keyed under the base's prefix.
///
/// This is the shape every CircuitPython driver uses to declare its register map, which is why
/// three Adafruit libraries stop here. The descriptor half of that is #360; this issue is only
/// about finding the attribute at all.
/// </summary>
public class ClassAttributeOnTheInstanceTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    /// <summary>What the program's last store into buf[0] carries.</summary>
    private static Val LastStored(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == 0)
            .Select(s => s.Src).Last();

    private const string Buf = "buf = bytearray([0, 0, 0, 0])\n";

    [Fact]
    public void AnUpperCaseClassConstant_IsReadableThroughTheInstance()
    {
        var ir = Gen(Buf +
            "class Cell:\n" +
            "    LIMIT = 7\n" +
            "    def __init__(self, n: uint8) -> None:\n" +
            "        self.n = n\n" +
            "c = Cell(2)\n" +
            "buf[0] = c.LIMIT\n");

        Assert.Equal(new Constant(7), LastStored(ir));
    }

    [Fact]
    public void ALowerCaseClassAttribute_IsReadableThroughTheInstance()
    {
        var ir = Gen(Buf +
            "class Cell:\n" +
            "    limit = 7\n" +
            "    def __init__(self, n: uint8) -> None:\n" +
            "        self.n = n\n" +
            "c = Cell(2)\n" +
            "buf[0] = c.limit\n");

        // The run-time half has a slot, so what reaches the store is the slot, not a constant.
        Assert.False(LastStored(ir) is Constant,
            $"stored {LastStored(ir)}; a lower-case class attribute has a slot and is read from it");
    }

    [Fact]
    public void AClassConstantDeclaredOnABase_IsReadableThroughTheSubclassInstance()
    {
        var ir = Gen(Buf +
            "class Base:\n" +
            "    LIMIT = 7\n" +
            "class Cell(Base):\n" +
            "    def __init__(self, n: uint8) -> None:\n" +
            "        self.n = n\n" +
            "c = Cell(2)\n" +
            "buf[0] = c.LIMIT\n");

        Assert.Equal(new Constant(7), LastStored(ir));
    }

    // The half that works today, kept so the fix cannot be had by breaking it.
    [Fact]
    public void AClassConstantThroughTheClassName_StillFolds()
    {
        var ir = Gen(Buf +
            "class Cell:\n" +
            "    LIMIT = 7\n" +
            "buf[0] = Cell.LIMIT\n");

        Assert.Equal(new Constant(7), LastStored(ir));
    }
}

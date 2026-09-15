using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#390. `with obj as x:` bound `x` to a name qualified by the enclosing FUNCTION
/// (`currentFunction + "." + name`), which is not how the object it stands for is actually
/// named: a module-level instance is a module global under its bare name (the same reason
/// `SlotInstanceKey` exists for construction), and a `with` inside a force-inlined method
/// names its locals by the INLINE frame, not the function. Either mismatch left `x` reading
/// storage nothing else ever wrote (fields silently 0), or -- for a method call on `x`, the
/// shape adafruit_bmp280.py:463 stops on (`with self._i2c as i2c: i2c.write(...)`) -- left the
/// receiver with no class at all, reported as a plain integer.
/// </summary>
public class WithBoundNameFieldReadTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2, GPIOR3\n" +
        "from pymcu.types import uint8\n\n\n";

    [Fact]
    public void AModuleLevelWithBoundNameReadsTheFieldEnterWrote()
    {
        // #390's first reproducer: the field is set INSIDE __enter__, read through the
        // bound name while still in the block. It used to read 0 (the pre-__enter__ value);
        // it has to read what __enter__ just wrote.
        var ir = Gen(Preamble +
            "class Gate:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.state: uint8 = 0\n\n" +
            "    def __enter__(self) -> \"Gate\":\n" +
            "        self.state = 1\n" +
            "        return self\n\n" +
            "    def __exit__(self, typ, val, tb) -> bool:\n" +
            "        self.state = self.state + 2\n" +
            "        return False\n\n" +
            "g = Gate()\n" +
            "with g as h:\n" +
            "    GPIOR1.value = h.state\n");

        // The read has to come from the SAME storage __enter__ just wrote -- the bare
        // "g_state" a module-level instance's field is actually stored under (matching
        // g.state read directly), not "main.g_state" (a name the bug wrote the alias to but
        // nothing else ever wrote), which is indistinguishable from a fresh zero-initialized
        // global at read time.
        var stores = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR1", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(stores, s => s.Src is Variable sv && sv.Name == "g_state");
        Assert.DoesNotContain(stores, s => s.Src is Variable sv && sv.Name == "main.g_state");
    }

    [Fact]
    public void TwoWithBoundNamesInOneStatementBothReadTheirOwnFields()
    {
        // #390's second reproducer: two managers in one `with`, fields set entirely in
        // __init__ (before the with statement runs at all). CPython: 3 (1 + 2). The bug
        // read 0 for the sum (both bound names read 0) while a.v/b.v after the block, read
        // through the ORIGINAL names, were already correct (11, 12).
        var ir = Gen(Preamble +
            "class Gate:\n" +
            "    def __init__(self, v: uint8) -> None:\n" +
            "        self.v: uint8 = v\n\n" +
            "    def __enter__(self) -> \"Gate\":\n" +
            "        return self\n\n" +
            "    def __exit__(self, typ, val, tb) -> bool:\n" +
            "        self.v = self.v + 10\n" +
            "        return False\n\n" +
            "a = Gate(1)\n" +
            "b = Gate(2)\n" +
            "with a as x, b as y:\n" +
            "    GPIOR1.value = x.v + y.v\n" +
            "GPIOR2.value = a.v\n" +
            "GPIOR3.value = b.v\n");

        var stores = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR1", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(stores, s => s.Src is Constant { Value: 3 });
    }

    [Fact]
    public void AMethodCallOnAWithBoundNameInsideAnInlinedMethodDispatchesCorrectly()
    {
        // The adafruit_bmp280.py:463 shape: `with self._i2c as i2c: i2c.write(...)` inside
        // a method that is itself force-inlined at its own call site. The bound name's
        // qualification has to follow the INLINE frame here, not the enclosing function --
        // otherwise the alias is written under one name and the call site reads another,
        // and the call degrades to an undefined `<name>_write`.
        var ir = Gen(Preamble +
            "class Bus:\n" +
            "    def __init__(self, addr: uint8) -> None:\n" +
            "        self.addr: uint8 = addr\n\n" +
            "    def __enter__(self) -> \"Bus\":\n" +
            "        return self\n\n" +
            "    def __exit__(self, typ, val, tb) -> bool:\n" +
            "        return False\n\n" +
            "    def write(self, value: uint8) -> None:\n" +
            "        GPIOR1.value = self.addr + value\n\n" +
            "class Device:\n" +
            "    def __init__(self, bus: Bus) -> None:\n" +
            "        self._bus: Bus = bus\n\n" +
            "    def poke(self, value: uint8) -> None:\n" +
            "        with self._bus as bus:\n" +
            "            bus.write(value)\n\n" +
            "b = Bus(5)\n" +
            "d = Device(b)\n" +
            "d.poke(2)\n");

        Assert.Contains(ir.Functions, f => f.Name == "Bus_write");
        var calls = ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Where(c => c.FunctionName == "Bus_write").ToList();
        Assert.NotEmpty(calls);
    }
}

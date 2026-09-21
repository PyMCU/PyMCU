using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A plain (non-<c>@inline</c>) function whose <c>return</c> hands back a
/// multi-field ZCA instance -- <c>def I2C(): return _board_i2c(SCL, SDA)</c> in
/// the CircuitPython board layer. The instance is compile-time per binding, so
/// a shared subroutine cannot carry it: the field stores were lowered against
/// the callee's frame and dropped, and the caller read field storage nothing
/// wrote (the real driver emitted 0 I2C transactions against the CPython
/// oracle; worse, the receiver's class tag was lost so the method call mangled
/// to an undefined name here). The factory is force-inlined, the same as a
/// function taking a ZCA parameter, so the construction happens at the call
/// site exactly like <c>i2c = busio.I2C(...)</c> written directly.
/// </summary>
public class ZcaFactoryReturnTests
{
    private static ProgramIR GenImported(string hal, string layer, string main, bool optimize = true)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["hal"] = new Parser(new Lexer(hal).Tokenize()).ParseProgram(),
            ["layer"] = new Parser(new Lexer(layer).Tokenize()).ParseProgram(),
        };
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { "hal", "layer" });
        return optimize ? Optimizer.Optimize(ir) : ir;
    }

    private static IEnumerable<Instruction> Body(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body);

    private static Val LastStored(ProgramIR ir, int slot) =>
        Body(ir).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    // Two fields on Bus, like _I2C's _freq/_mode: a multi-field class whose
    // nested instance is the field the returned object must keep.
    private const string Hal =
        "from pymcu.types import uint8, inline\n" +
        "class Bus:\n" +
        "    def __init__(self):\n" +
        "        self.mode: uint8 = 99\n" +
        "        self.pin: uint8 = 7\n" +
        "    @inline\n" +
        "    def write(self, b: uint8) -> uint8:\n" +
        "        if self.mode == 99:\n" +
        "            return b\n" +
        "        return 0\n";

    private const string Layer =
        "from pymcu.types import uint8, inline\n" +
        "from hal import Bus\n" +
        "class I2C:\n" +
        "    @inline\n" +
        "    def __init__(self, scl: uint8, sda: uint8):\n" +
        "        self._bus = Bus()\n" +
        "        self._locked: uint8 = 0\n" +
        "def make():\n" +
        "    return I2C(1, 2)\n";

    private const string Main =
        "from pymcu.types import uint8\n" +
        "from layer import make\n" +
        "i2c = make()\n" +
        "buf = bytearray([0])\n" +
        "buf[0] = i2c._bus.write(5)\n";

    [Fact]
    public void AReturnedZcaInstance_KeepsItsFieldsAcrossTheFunctionBoundary()
    {
        var ir = GenImported(Hal, Layer, Main);

        LastStored(ir, 0).Should().Be(new Constant(5),
            because: "write() returns b only when self.mode == 99, so the " +
                     "constant must have reached the field the compare reads");
    }

    [Fact]
    public void AFactoryReturningAZcaInstance_IsExpandedAtTheCallSite()
    {
        var ir = GenImported(Hal, Layer, Main, optimize: false);

        Body(ir).OfType<Call>().Select(c => c.FunctionName)
            .Should().NotContain("layer_make",
                because: "a function returning a ZCA construction has no standalone " +
                         "form; it expands where it is called");
        Body(ir).OfType<Copy>()
            .Where(c => c.Src is Constant k && k.Value == 99)
            .Select(c => c.Dst).OfType<Variable>()
            .Select(v => v.Name)
            .Should().Contain(n => n.Contains("mode"),
                because: "self.mode = 99 inside the nested construction must " +
                         "emit a store to the caller-visible field storage; " +
                         "before the fix it survived only as a dbg marker");
    }
}

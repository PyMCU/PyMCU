using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#373. A method call on <c>self</c>, from inside another method reached from
/// <c>__init__</c>, used to report the receiver as an integer -- reduced from
/// adafruit_bmp280.py and adafruit_veml7700.py, where the base defines the method it calls as
/// `raise NotImplementedError()` and a subclass overrides it.
///
/// The root cause is in an OUTLINED method (RFC 0001: compiled once, `self` bound to the
/// DECLARING class, not the constructed instance). A `self.&lt;sibling&gt;()` call inside one
/// can only forward statically, to whatever the declaring class's own definition resolves to
/// (TryEmitSelfOutlinedMethodCall) -- sound only when that sibling is ALSO outlined. Base's own
/// `_write_register_byte` is not (its body is `raise`, which IsOutlineSafe refuses), so the
/// forward fails and the generic per-instance receiver resolver runs instead, with no instance
/// behind this SHARED body's `self` to resolve -- and reports it as the plain machine value its
/// slot pointer's own storage type is.
///
/// The fix demotes such an outlined method to force-inline once scanning is complete and every
/// override is known (DemoteUnsafeOutlinedSelfCalls), which is what an ordinary undecorated
/// `self._reset()` written directly in `__init__` already does correctly -- it resolves the
/// receiver from the concrete instance at each call site instead of sharing one generic body.
/// </summary>
public class OutlinedSelfCallDispatchTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8\n\n\n";

    // A base with >= 2 fields (so the class is a shared/outlined "slot" class), whose
    // __init__ reaches a sibling self-call two hops deep (__init__ -> _reset ->
    // _write_register_byte), where the base's own version of the innermost method is not
    // outline-safe (it raises) and a subclass overrides it.
    private const string ClassesShaped =
        "class Base:\n" +
        "    def __init__(self) -> None:\n" +
        "        self._mode: uint8 = 0\n" +
        "        self._standby: uint8 = 1\n" +
        "        self._reset()\n\n" +
        "    def _reset(self) -> None:\n" +
        "        self._write_register_byte(0xB6)\n\n" +
        "    def _write_register_byte(self, value: uint8) -> None:\n" +
        "        raise ValueError(\"range\")\n\n" +
        "class I2C(Base):\n" +
        "    def __init__(self) -> None:\n" +
        "        super().__init__()\n\n" +
        "    def _write_register_byte(self, value: uint8) -> None:\n" +
        "        GPIOR1.value = value\n";

    [Fact]
    public void TheSelfCallInsideAnOutlinedMethodBuildsCleanly()
    {
        // The whole defect in one assertion: it used to raise UserError("'self' is an
        // integer: '_write_register_byte()' is not available. ...") right here.
        var ir = Gen(Preamble + ClassesShaped + "\nd = I2C()\n");

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void TheOverriddenVersionRunsNotTheBases()
    {
        // Asserting only that it builds passes even on wrong dispatch (silently running
        // Base's raise, or nothing at all). The call that ends up in the image has to be
        // the override I2C actually defines, at the address I2C's body writes.
        var ir = Gen(Preamble + ClassesShaped + "\nd = I2C()\n");

        var stores = ir.Functions
            .SelectMany(f => f.Body)
            .OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR1", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(stores, s => s.Src is Constant { Value: 0xB6 });
    }
}

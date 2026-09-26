using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A property written `None` must read back `None` -- the disable idiom the Adafruit
/// libraries use (`servo.angle = None` writes the backing field through a HAL call,
/// which makes it a run-time value, after which the getter's `field == 0` is a real
/// branch and its result a union the result slot cannot tag). The write marks the
/// member None-valued; the read answers the mark, and a dependent getter's
/// `self.prop is None` folds on it. A non-None write to ANY property on the instance
/// clears the marks, because siblings derive from shared state.
///
/// Independently, an expansion whose deciding return produced `None` reports the call
/// result as NoneVal (InlineContext.ResultIsNone), so `print(s.fraction)` on a servo
/// that was never enabled writes "None" rather than the residue the declared-type
/// result slot happened to hold (180.0 on the measured program).
/// </summary>
public class PropertyGetterNoneTests
{
    private const string Hdr =
        "from pymcu.types import uint8\n" +
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    // _BaseServo.fraction + Servo.angle reduced: an inner property derived from a
    // field, an outer property derived from the inner one, both taking None to disable.
    // The getters are unannotated on purpose: `-> Optional[uint8]` with a reached
    // `return None` is the refusal OptionalIsTheTypeTests pins. They once compiled
    // anyway because the setter's `None` return overwrote the getter's entry in
    // functionReturnTypes -- a5d6e66 stopped that, and the annotation went with it.
    private const string Pair =
        "class S:\n" +
        "    def __init__(self) -> None:\n" +
        "        self.duty: uint8 = 1\n" +
        "    @property\n" +
        "    def inner(self):\n" +
        "        if self.duty == 0:\n" +
        "            return None\n" +
        "        return self.duty\n" +
        "    @inner.setter\n" +
        "    def inner(self, value) -> None:\n" +
        "        if value is None:\n" +
        "            self.duty = 0\n" +
        "            return\n" +
        "        self.duty = value\n" +
        "    @property\n" +
        "    def outer(self):\n" +
        "        if self.inner is None:\n" +
        "            return None\n" +
        "        return self.inner * 2\n" +
        "    @outer.setter\n" +
        "    def outer(self, v) -> None:\n" +
        "        if v is None:\n" +
        "            self.inner = None\n" +
        "            return\n" +
        "        self.inner = v / 2\n" +
        "s = S()\n";

    [Fact]
    public void APropertyWrittenNoneReadsBackNone()
    {
        // `s.outer = None` disables; `if s.outer is None:` must fold true. Before the
        // write marked the member, the read expanded the getter, `self.inner is None`
        // folded false against a slot that never got a value, and the else arm ran.
        var ir = Gen(Hdr + Pair +
            "def main():\n" +
            "    s.outer = None\n" +
            "    if s.outer is None:\n" +
            "        GPIOR0.value = 7\n");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 7);
    }

    [Fact]
    public void ANonNoneSiblingWriteClearsTheMark()
    {
        // `s.inner = 5` re-derives `s.outer` (inner * 2): the stale `s.outer` mark from
        // the earlier disable would have folded `s.outer is None` true and dropped the
        // else arm. Two different writes put `duty` out of compile-time reach, so the
        // inner getter's `duty == 0` is a run-time branch between `return None` and a
        // value, and the answer comes from that union's tag: both arms stay. (This used
        // to assert the 99 arm away, which only held because the untagged union folded
        // to "not None" whatever duty held.)
        var ir = Gen(Hdr + Pair +
            "def main():\n" +
            "    s.outer = None\n" +
            "    s.inner = 5\n" +
            "    if s.outer is None:\n" +
            "        GPIOR0.value = 99\n" +
            "    else:\n" +
            "        GPIOR1.value = 10\n");
        var copies = ir.Functions.SelectMany(f => f.Body).OfType<Copy>().ToList();
        Assert.Contains(copies, c => c.Src is Constant k && k.Value == 10);
    }

    [Fact]
    public void AGetterWhoseReachesAreAllNoneYieldsNone()
    {
        // No property write at all: duty stays the const 0 __init__ gave it, the inner
        // getter's `duty == 0` folds, `return None` ends the expansion, and the call's
        // result is the None the program meant -- not the result slot's residue. (The
        // getter is unannotated here: an `Optional[uint8]` return with a reached
        // `return None` is the refusal OptionalIsTheTypeTests pins, not this path.)
        var ir = Gen(Hdr +
            "class T:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.duty: uint8 = 0\n" +
            "    @property\n" +
            "    def inner(self):\n" +
            "        if self.duty == 0:\n" +
            "            return None\n" +
            "        return self.duty\n" +
            "t = T()\n" +
            "def main():\n" +
            "    if t.inner is None:\n" +
            "        GPIOR0.value = 9\n");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 9);
    }

    [Fact]
    public void ADisabledReadStillAnswersAfterTheFieldWentRuntime()
    {
        // Write two different constants through the inner setter so `duty` leaves
        // compile-time tracking (killedConstants); the None answer then rides on the
        // write's mark, not on a foldable `duty == 0` inside the getter.
        var ir = Gen(Hdr + Pair +
            "def main():\n" +
            "    s.inner = 3\n" +
            "    s.inner = 4\n" +
            "    s.outer = None\n" +
            "    if s.outer is None:\n" +
            "        GPIOR0.value = 11\n");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 11);
    }
}

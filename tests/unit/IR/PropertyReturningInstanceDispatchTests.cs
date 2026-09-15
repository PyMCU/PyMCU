using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#445. A `@property` getter that returns a ZCA instance (`return self._q`) loses that
/// instance for further method dispatch: `owner.q.bump()` refused with "'.bump()' cannot be
/// dispatched: its receiver is not a name bound to an object, a register, or a value PyMCU
/// defines methods on." Replacing the `@property` with a plain instance attribute
/// (`self.q = Queue()`, everything else identical) compiles and runs `owner.q.bump()` without
/// complaint, which is what this fix makes the property spelling do too.
///
/// Root cause: a `@property` getter is force-inlined at its call site like any other instance
/// method (the same mechanism #421 uses for a factory METHOD), and its result -- a
/// single-field ZCA instance -- is an ALIAS of the field's own flattened storage
/// (`variableAliases["tmp_N"] == "owner__q"`), not a name `instanceClasses` tags directly. Two
/// call sites in the dispatch path checked `instanceClasses.ContainsKey` on the bare Temporary
/// -- one hop short of the alias chain `GetValClass` already walks for exactly this shape --
/// so the receiver's class was never found, and (once that was fixed) `self` bound to a SECOND,
/// independently re-evaluated temp with the identical gap. A single alias-following helper,
/// `ResolveClassCarryingName`, closes both call sites.
/// </summary>
public class PropertyReturningInstanceDispatchTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8, inline\n\n\n";

    private const string QueueAndOwner =
        "class Queue:\n" +
        "    @inline\n" +
        "    def __init__(self):\n" +
        "        self._n = 0\n\n" +
        "    @inline\n" +
        "    def bump(self):\n" +
        "        self._n = self._n + 1\n\n" +
        "class Owner:\n" +
        "    @inline\n" +
        "    def __init__(self):\n" +
        "        self._q = Queue()\n\n" +
        "    @property\n" +
        "    def q(self):\n" +
        "        return self._q\n\n";

    [Fact]
    public void APropertyReturningAZcaInstance_DispatchesAMethodOnTheResult()
    {
        // The issue's own minimal reproduction, plus a read of the bumped field so this
        // checks the VALUE reaches the same storage a direct field call would use, not just
        // that the program builds without throwing.
        var ir = Gen(Preamble + QueueAndOwner +
            "owner = Owner()\n" +
            "owner.q.bump()\n" +
            "GPIOR1.value = owner._q._n\n");

        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 1);
    }

    [Fact]
    public void ThreeBumpsThroughTheProperty_AllTargetTheSameStorage()
    {
        // Three separate `owner.q.bump()` call sites, each re-evaluating the property getter
        // independently (a fresh Temporary aliasing the field every time): every one of them
        // has to resolve to the SAME underlying field storage, not three different untagged
        // names that each start over from the constructor's initial value.
        var ir = Gen(Preamble + QueueAndOwner +
            "owner = Owner()\n" +
            "owner.q.bump()\n" +
            "owner.q.bump()\n" +
            "owner.q.bump()\n" +
            "GPIOR1.value = owner._q._n\n");

        var writesToTheField = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name == "owner__q__n")
            .ToList();
        // The constructor's own initial write, plus one per bump() call site.
        Assert.Equal(4, writesToTheField.Count);
    }

    [Fact]
    public void APlainAttribute_IsUnaffectedAndStillDispatches()
    {
        // The control: the shape that already worked before this fix, kept green so the fix
        // cannot be had by breaking it.
        var ir = Gen(Preamble +
            "class Queue:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        self._n = 0\n\n" +
            "    @inline\n" +
            "    def bump(self):\n" +
            "        self._n = self._n + 1\n\n" +
            "class Owner:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        self.q = Queue()\n\n" +
            "owner = Owner()\n" +
            "owner.q.bump()\n" +
            "GPIOR1.value = owner.q._n\n");

        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 1);
    }
}

using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A raise inside an @inline expansion leaves the callee before it reaches any `finally` the
/// CALLER pushed -- a `with` around the call, most often. Python runs that `__exit__` once, as
/// the exception leaves the `with`. The generator expanded it at the raise site instead,
/// inside the callee's frame, where the callee was still marked as being expanded: a
/// `__exit__` that calls the same method the body calls was refused as recursive.
///
/// The demandant is cp-servo's `with ContinuousServo(pwm) as cx: cx.throttle = 0.5`: the
/// servo's `__exit__` writes the throttle again, both writes reach pwmio's set_duty_u16, and
/// set_duty_u16 carries a run-time division whose zero check is a raise.
/// </summary>
public class RaiseCrossingCallerFinallyTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Prelude =
        "from pymcu.types import uint8, uint16, inline, ptr\n" +
        "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
        "class Dev:\n" +
        "    def __init__(self):\n" +
        "        self.n = 0\n" +
        "    @inline\n" +
        "    def put(self, x: uint16):\n" +
        "        self.n = 60000 // x\n" +
        "class Mgr:\n" +
        "    def __init__(self, d):\n" +
        "        self._d = d\n" +
        "    def __enter__(self):\n" +
        "        return self\n" +
        "    def __exit__(self, a, b, c):\n" +
        "        self._d.put(GPIOR0.value + 3)\n";

    [Fact]
    public void AnExitThatCallsTheBodysRaisingMethod_IsNotRecursion()
    {
        var ir = Gen(Prelude +
            "d = Dev()\n" +
            "with Mgr(d) as m:\n" +
            "    d.put(GPIOR0.value + 7)\n");

        // Three expansions of put: the body's, the exit on the normal path, and the exit on
        // the landing the body's zero check jumps to. Each one divides once.
        var main = Assert.Single(ir.Functions, f => f.Name == "main");
        Assert.Equal(3, main.Body.Count(i => i is Binary { Op: PyMCU.IR.BinaryOp.FloorDiv }));
    }

    [Fact]
    public void TheLandingRunsTheExitBeforeTheHandlerSeesTheError()
    {
        var ir = Gen(Prelude +
            "d = Dev()\n" +
            "try:\n" +
            "    with Mgr(d) as m:\n" +
            "        d.put(GPIOR0.value)\n" +
            "except ZeroDivisionError:\n" +
            "    d.n = 1\n");

        var body = Assert.Single(ir.Functions, f => f.Name == "main").Body;
        // The body's zero check jumps to a landing; the landing holds the exit's division
        // and then the delivery to the handler.
        var landingJumps = body.OfType<Jump>().Select(j => j.Target).ToHashSet();
        int landing = body.FindIndex(i => i is Label l && landingJumps.Contains(l.Name)
            && body.Skip(body.IndexOf(i) + 1).TakeWhile(x => x is not SignalError)
                   .Any(x => x is Binary { Op: PyMCU.IR.BinaryOp.FloorDiv }));
        Assert.True(landing >= 0, "no landing runs the exit before signalling");
        var signal = body.Skip(landing).OfType<SignalError>().First();
        Assert.NotNull(signal.CatchLabel);
    }
}

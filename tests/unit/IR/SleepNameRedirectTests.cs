using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A call is compiled from the function it names. The call dispatcher used to rewrite any
/// callee spelled sleep_ms, sleep_us, delay_ms or delay_us (bare, `time_` or `pymcu_time_`)
/// to pymcu.time's delay_ms/delay_us before looking it up, falling back to the first inline
/// function whose name ends in delay_ms/delay_us. A function the program or a library
/// defined under one of those names never ran: a user `def sleep_ms(n)` that adds n to a
/// counter printed 0 instead of 302, silently, and the MicroPython layer's
/// `time.sleep_ms(ms: uint32)` became the 16-bit stdlib delay, so sleep_ms(65636) slept
/// 100 ms.
/// </summary>
public class SleepNameRedirectTests
{
    private const string StdlibTime =
        "@inline\n" +
        "def delay_ms(ms: uint16):\n" +
        "    pass\n" +
        "@inline\n" +
        "def delay_us(us: uint16):\n" +
        "    pass\n";

    private static ProgramIR Gen(string main, Dictionary<string, string> modules)
    {
        var imported = modules.ToDictionary(
            kv => kv.Key, kv => new Parser(new Lexer(kv.Value).Tokenize()).ParseProgram());
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" });
        return Optimizer.Optimize(ir);
    }

    private static List<Call> CallsTo(ProgramIR ir, string suffix) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Where(c => c.FunctionName == suffix || c.FunctionName.EndsWith("_" + suffix))
            .ToList();

    [Theory]
    [InlineData("sleep_ms")]
    [InlineData("sleep_us")]
    [InlineData("delay_ms")]
    [InlineData("delay_us")]
    public void AProgramFunctionNamedLikeADelay_IsTheOneCalled(string name)
    {
        var ir = Gen(
            "from pymcu.time import delay_ms as _d\n" +
            "hits: uint16 = 0\n" +
            $"def {name}(n: uint16):\n" +
            "    global hits\n" +
            "    hits = hits + n\n" +
            "s: uint16 = 0\n" +
            $"{name}(s + 300)\n" +
            $"{name}(s + 2)\n",
            new Dictionary<string, string> { ["pymcu.time"] = StdlibTime });

        CallsTo(ir, name).Should().HaveCount(2,
            because: $"both calls name the program's own {name}, not pymcu.time's delay");
    }

    [Theory]
    [InlineData("from time import sleep_ms\nsleep_ms(s + 65636)\n", "sleep_ms")]
    [InlineData("import time\ntime.sleep_ms(s + 65636)\n", "sleep_ms")]
    [InlineData("from time import sleep_us\nsleep_us(s + 65636)\n", "sleep_us")]
    [InlineData("import time\ntime.sleep_us(s + 65636)\n", "sleep_us")]
    public void ALibraryTimeModule_KeepsItsOwnSleep(string call, string name)
    {
        // The MicroPython layer's time.py, reduced: a 32-bit sleep that is not the stdlib's.
        const string layerTime =
            "from pymcu.time import delay_ms as _delay_ms\n" +
            "def sleep_ms(ms: uint32):\n" +
            "    _delay_ms(1)\n" +
            "def sleep_us(us: uint32):\n" +
            "    _delay_ms(2)\n";
        var ir = Gen(
            "s: uint32 = 0\n" + call,
            new Dictionary<string, string> { ["pymcu.time"] = StdlibTime, ["time"] = layerTime });

        var calls = CallsTo(ir, name);
        calls.Should().ContainSingle(because: $"the call reaches the layer's {name}");
        calls[0].Args.Should().ContainSingle();
    }
}

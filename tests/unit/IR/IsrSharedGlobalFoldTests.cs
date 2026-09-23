using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A `global`-declared store inside a function records the constant it writes into
/// `localConstantValues` under the GLOBAL's resolved name. That map is meant to answer
/// "what does this function-local provably hold here" -- but a module global is shared
/// storage: an ISR (or any routine generated later) reads the same name, and the last
/// value some other function happened to store is not the value this read can see at
/// run time.
///
/// Measured: `pulse_capture_clear()` stores `_pulse_armed = 0` while the constructor is
/// being lowered; `pulse_isr` is generated later by `compile_isr`, and its
/// `if _pulse_armed == 0:` folded to a compile-time true on the stale 0. The arm-branch
/// emitted unconditionally and the whole delta/store path after it was dropped -- the
/// ISR armed on the first edge and did nothing on every edge after it, so PulseIn's
/// count stayed 0 forever.
///
/// The same stale fold had `_pulse_len` read as 0 inside `popleft`/`get` and
/// `_pulse_maxlen` read as the constructor's argument inside `get`.
/// </summary>
public class IsrSharedGlobalFoldTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Prelude =
        "from pymcu.types import uint8, compile_isr\n" +
        "\n" +
        "_armed: uint8 = 0\n" +
        "\n" +
        "def clear():\n" +
        "    global _armed\n" +
        "    _armed = 0\n" +
        "\n";

    [Fact]
    public void AGlobalStoredByAnotherFunction_DoesNotFoldInsideTheIsr()
    {
        var ir = Gen(Prelude +
            "def isr():\n" +
            "    global _armed\n" +
            "    if _armed == 0:\n" +
            "        _armed = 1\n" +
            "        return\n" +
            "    _armed = 2\n" +
            "    return\n" +
            "\n" +
            "def main():\n" +
            "    clear()\n" +
            "    compile_isr(isr, 0x0004)\n" +
            "\n" +
            "main()\n");
        var isr = ir.Functions.Single(f => f.Name == "isr");

        // The comparison must stay a run-time branch: `_armed = 2` (the delta path in
        // the real ISR) lives behind it and must be emitted.
        Assert.Contains(isr.Body, i => i is JumpIfNotEqual jne
            && jne.Src1 is Variable v && v.Name == "_armed");
        Assert.Contains(isr.Body, i => i is Copy c
            && c.Src is Constant { Value: 2 });
    }

    [Fact]
    public void AnInitOnlyGlobal_StillFoldsAsAnArgumentConstant()
    {
        // The companion case the refusal must not take with it: `display_width = 128`
        // is written once, at module level, and never touched by any function -- its
        // one store IS the initializer every reader sees. adafruit_ssd1306 hands it
        // to SSD1306_I2C.__init__, whose `bytearray(((height // 8) * width) + 1)`
        // sizes a fixed SRAM buffer from it; bound as a plain alias the size cannot
        // fold and the field takes the arena path, where `memoryview(self.buffer)`
        // then refuses a scalar.
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "\n" +
            "width = 128\n" +
            "\n" +
            "def buffer_for(w: uint8):\n" +
            "    buf = bytearray((w // 8) + 1)\n" +
            "    return buf\n" +
            "\n" +
            "b = buffer_for(width)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        // A statically-sized bytearray emits one ArrayStore per element; the arena
        // path emits a call to pymcu.arena.alloc instead, and on a bare unit-test
        // program refuses with "needs the pymcu.arena allocator".
        Assert.Contains(main.Body, i => i is ArrayStore a && a.Count == 17);
    }

    [Fact]
    public void ABareGlobalTruthTest_DoesNotFoldInsideTheIsrEither()
    {
        var ir = Gen(Prelude +
            "def isr():\n" +
            "    global _armed\n" +
            "    if _armed:\n" +
            "        return\n" +
            "    _armed = 1\n" +
            "    return\n" +
            "\n" +
            "def main():\n" +
            "    clear()\n" +
            "    compile_isr(isr, 0x0004)\n" +
            "\n" +
            "main()\n");
        var isr = ir.Functions.Single(f => f.Name == "isr");

        Assert.Contains(isr.Body, i => i is JumpIfZero);
        Assert.Contains(isr.Body, i => i is Copy c
            && c.Src is Constant { Value: 1 });
    }
}

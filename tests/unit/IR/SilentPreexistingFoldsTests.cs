using System.Linq;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Two folds that answered the wrong value in silence, both found through adafruit_onewire:
///
/// - a tuple-returning function with more than one `return a, b` left the constant of the LAST
///   return walked in the result slot, so the caller folded the second element of
///   `return 7, d - 1` / `return 7, 0` to 0 for every call;
/// - a property read on an instance held in a field (`self._c.value`) took the register shortcut
///   for `.value` and answered the field itself, so the getter never ran.
/// </summary>
public class SilentPreexistingFoldsTests
{
    private static ProgramIR Gen(string src, string arch) =>
        new IRGenerator().Generate(new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new System.Collections.Generic.Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = arch });

    private static ProgramIR GenOptimized(string src, string arch) => Optimizer.Optimize(Gen(src, arch));

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void ATupleSlotTwoReturnsFillDifferently_IsNotFoldedToTheLastOne(string arch)
    {
        var ir = Gen(
            "def search(d: int) -> tuple[int, int]:\n" +
            "    if d > 1:\n" +
            "        return 7, d - 1\n" +
            "    return 7, 0\n" +
            "def run(d: int) -> int:\n" +
            "    x, y = search(d)\n" +
            "    return y\n", arch);
        var run = ir.Functions.Single(f => f.Name == "run").Body;
        Assert.DoesNotContain(run, i => i is Return { Value: Constant });
    }

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void ATupleSlotEveryReturnFillsAlike_StillFolds(string arch)
    {
        var ir = Gen(
            "def search(d: int) -> tuple[int, int]:\n" +
            "    if d > 1:\n" +
            "        return 7, 4\n" +
            "    return 7, 4\n" +
            "def run(d: int) -> int:\n" +
            "    x, y = search(d)\n" +
            "    return y\n", arch);
        var run = ir.Functions.Single(f => f.Name == "run").Body;
        Assert.Contains(run, i => i is Return { Value: Constant { Value: 4 } });
    }

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void APropertyOnAFieldInstance_RunsItsGetter(string arch)
    {
        var ir = Gen(
            "_g = 0\n" +
            "class Inner:\n" +
            "    @property\n" +
            "    def value(self) -> int:\n" +
            "        return _g\n" +
            "class Holder:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._c = Inner()\n" +
            "    def get(self) -> int:\n" +
            "        return self._c.value\n" +
            "h = Holder()\n" +
            "def run() -> int:\n" +
            "    return h.get()\n", arch);
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        // The getter's body reads the module global; the shortcut never reached it.
        Assert.Contains(body, i => i is Copy { Src: Variable { Name: var n } } && n.EndsWith("_g"));
    }

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void AFieldHeldInstanceMutatedByACallInALoop_IsNotFoldedOnTheSecondTurn(string arch)
    {
        // `self.w.read()` writes `phase` and `alive_*`; the loop body is lowered once and runs
        // many times, so what `reset()` left in them must not fold the tests on the first
        // turn's values. The receiver is an instance a field holds, which the loop's
        // invalidation of the fields a call writes only knew for a bare name.
        var ir = GenOptimized(
            "class Wire:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.a = bytearray(2)\n" +
            "        self.b = bytearray(2)\n" +
            "        self.a[0] = 0x12\n" +
            "        self.a[1] = 0x34\n" +
            "        self.b[0] = 0x12\n" +
            "        self.b[1] = 0xB4\n" +
            "        self.alive_a = True\n" +
            "        self.alive_b = True\n" +
            "        self.pos = 0\n" +
            "        self.phase = 0\n" +
            "\n" +
            "    def bit_of(self, rom: bytearray, pos: int) -> int:\n" +
            "        return (rom[pos // 8] >> (pos % 8)) & 1\n" +
            "\n" +
            "    def reset(self) -> None:\n" +
            "        self.alive_a = True\n" +
            "        self.alive_b = True\n" +
            "        self.pos = 0\n" +
            "        self.phase = 0\n" +
            "\n" +
            "    def read(self) -> int:\n" +
            "        v = 1\n" +
            "        if self.phase == 0:\n" +
            "            if self.alive_a and self.bit_of(self.a, self.pos) == 0:\n" +
            "                v = 0\n" +
            "            if self.alive_b and self.bit_of(self.b, self.pos) == 0:\n" +
            "                v = 0\n" +
            "            self.phase = 1\n" +
            "        else:\n" +
            "            if self.alive_a and self.bit_of(self.a, self.pos) == 1:\n" +
            "                v = 0\n" +
            "            if self.alive_b and self.bit_of(self.b, self.pos) == 1:\n" +
            "                v = 0\n" +
            "            self.phase = 2\n" +
            "        return v\n" +
            "\n" +
            "    def write(self, bit: int) -> None:\n" +
            "        if self.alive_a and self.bit_of(self.a, self.pos) != bit:\n" +
            "            self.alive_a = False\n" +
            "        if self.alive_b and self.bit_of(self.b, self.pos) != bit:\n" +
            "            self.alive_b = False\n" +
            "        self.pos = self.pos + 1\n" +
            "        self.phase = 0\n" +
            "\n" +
            "\n" +
            "class Bus:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.w = Wire()\n" +
            "\n" +
            "    def run(self, diff: int) -> int:\n" +
            "        self.w.reset()\n" +
            "        i = 16\n" +
            "        for byte in range(2):\n" +
            "            for bit in range(8):\n" +
            "                b = self.w.read()\n" +
            "                if self.w.read():\n" +
            "                    if b:\n" +
            "                        return 99\n" +
            "                elif not b:\n" +
            "                    if diff > i:\n" +
            "                        b = 1\n" +
            "                self.w.write(b)\n" +
            "                i -= 1\n" +
            "        return 0\n" +
            "\n" +
            "\n" +
            "bus = Bus()\n" +
            "r = bus.run(17)\n", arch);
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.Contains(body, i =>
            (i is JumpIfNotEqual { Src1: Variable { Name: var a } } && a.EndsWith("phase"))
            || (i is JumpIfEqual { Src1: Variable { Name: var b } } && b.EndsWith("phase")));
    }
}

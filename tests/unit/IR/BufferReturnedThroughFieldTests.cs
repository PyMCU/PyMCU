using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// adafruit_onewire's `OneWireAddress.rom` is a @property returning `self._rom`, a field
/// that was handed a bytearray. The getter's `return self._rom` lowered as a scalar read,
/// so `len(addr.rom)` and `bus.write(addr.rom)` -- whose body takes `len(buf)` -- were
/// refused as "len() argument must be a fixed-size array".
/// </summary>
public class BufferReturnedThroughFieldTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    private const string Addr =
        "from pymcu.types import uint8\n" +
        "class Addr:\n" +
        "    def __init__(self, rom: bytearray) -> None:\n" +
        "        self._rom = rom\n" +
        "    @property\n" +
        "    def rom(self) -> bytearray:\n" +
        "        return self._rom\n" +
        "class Bus:\n" +
        "    def __init__(self):\n" +
        "        self.acc = 0\n" +
        "    def write(self, buf, end=None) -> None:\n" +
        "        if end is None:\n" +
        "            end = len(buf)\n" +
        "        for i in range(0, end):\n" +
        "            self.acc += buf[i]\n" +
        "r = bytearray(3)\n" +
        "a = Addr(r)\n" +
        "bus = Bus()\n";

    [Fact]
    public void LenOfAPropertyReturningABufferFieldIsItsLength()
    {
        var main = Gen(Addr + "n: uint8 = len(a.rom)\n").Functions.Single(f => f.Name == "main").Body;

        main.Any(i => i is Copy { Src: Constant { Value: 3 }, Dst: Variable { Name: "n" } }).Should().BeTrue();
    }

    [Fact]
    public void APropertyReturningABufferFieldPassesTheBuffer()
    {
        var main = Gen(Addr + "bus.write(a.rom)\n").Functions.Single(f => f.Name == "main").Body;

        main.OfType<ArrayLoad>().Should().Contain(l => l.ArrayName == "r",
            because: "the callee's buf[i] reads the array the property handed back");
    }

    // RFC 0015 (buffers carry their length): the shape that was still unresolved after the
    // two tests above -- a bytearray built INSIDE a function, returned, and only THEN handed
    // to the constructor (`Addr(make_rom())` instead of a bare named `Addr(r)`). The field
    // alias (`self._rom = rom`) and the property getter's own name resolution worked already;
    // what was missing was the ADDRESS half of the pair at the call-argument marshalling site
    // (BindArrayAlias not propagating moduleSramArrays, and TryResolveArrayStorageKey
    // stopping on the field's own copied size fact instead of following its alias) --
    // without it, `consume(a.rom)` linked with "undefined reference" to a label the static
    // allocator never allocated for the field.
    private const string AddrFromFunction =
        "from pymcu.types import uint8\n" +
        "def make_rom() -> bytearray:\n" +
        "    rom = bytearray(3)\n" +
        "    rom[0] = 9\n" +
        "    return rom\n" +
        "class Addr:\n" +
        "    def __init__(self, rom: bytearray) -> None:\n" +
        "        self._rom = rom\n" +
        "    @property\n" +
        "    def rom(self) -> bytearray:\n" +
        "        return self._rom\n" +
        "def consume(buf: bytearray) -> uint8:\n" +
        "    return len(buf)\n" +
        "a = Addr(make_rom())\n";

    [Fact]
    public void APropertyReturningABufferBuiltInsideAFunctionAndStoredByAConstructorPassesTheBuffer()
    {
        var program = Gen(AddrFromFunction + "n: uint8 = consume(a.rom)\n");
        var main = program.Functions.Single(f => f.Name == "main").Body;

        // The buffer's address must be marshalled as the ARRAY the function built (an
        // ArrayBase/label reference the static allocator actually owns), not a bare
        // Variable naming a label nothing allocates -- the undefined-reference regression.
        main.OfType<Call>().Should().Contain(c => c.FunctionName == "consume"
            && c.Args.Any(a => a is ArrayBase),
            because: "the field's buffer, built inside make_rom(), must be passed by a real array base");
    }

    // The same shape one hop further: the buffer comes back through a tuple-unpack
    // (`rom, diff = search_rom(...)`) before reaching the constructor -- the exact chain
    // `OneWireBus.scan()` uses on an unmodified adafruit_onewire bus (`_search_rom`'s own
    // `bytearray(8)`, tuple-returned, then handed to `OneWireAddress(rom)`).
    private const string AddrFromTupleReturn =
        "from pymcu.types import uint8\n" +
        "def search_rom(rom: bytearray, diff: uint8):\n" +
        "    new_rom = bytearray(3)\n" +
        "    new_rom[0] = rom[0] + 1\n" +
        "    new_diff = diff + 1\n" +
        "    return new_rom, new_diff\n" +
        "class Addr:\n" +
        "    def __init__(self, rom: bytearray) -> None:\n" +
        "        self._rom = rom\n" +
        "    @property\n" +
        "    def rom(self) -> bytearray:\n" +
        "        return self._rom\n" +
        "def consume(buf: bytearray) -> uint8:\n" +
        "    return len(buf)\n" +
        "rom = bytearray(3)\n" +
        "diff: uint8 = 0\n" +
        "rom, diff = search_rom(rom, diff)\n" +
        "a = Addr(rom)\n";

    [Fact]
    public void APropertyReturningABufferFromATupleUnpackAndStoredByAConstructorPassesTheBuffer()
    {
        var program = Gen(AddrFromTupleReturn + "n: uint8 = consume(a.rom)\n");
        var main = program.Functions.Single(f => f.Name == "main").Body;

        main.OfType<Call>().Should().Contain(c => c.FunctionName == "consume"
            && c.Args.Any(a => a is ArrayBase),
            because: "the tuple-unpacked buffer, stored by the constructor, must be passed by a real array base");
    }
}

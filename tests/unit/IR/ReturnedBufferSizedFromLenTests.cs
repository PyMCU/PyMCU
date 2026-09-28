using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `out = bytearray(2 * len(data))` in an @inline that returns `out`: len() of a buffer
/// parameter did not fold in a size, so the buffer took the run-time arena path, and the
/// `return out` handed back its arena offset as a number. `a = dbl(src)` then read the bits
/// of that number -- a buffer of zeros, and `len(a)` refused (binascii.hexlify in the
/// MicroPython layer is this shape).
/// </summary>
public class ReturnedBufferSizedFromLenTests
{
    // A runtime-sized bytearray(n) lowers to pymcu.arena.alloc, so the module has to
    // resolve like the driver's loaded stdlib does (ArenaAllocatorTests explains why
    // ARENA_SIZE is substituted: shipped 0 folds every alloc's bounds check to a
    // compile-time MemoryError).
    private static readonly ProgramNode ArenaModuleAst = new Parser(new Lexer(
        System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(FindRepoFile("lib/src/pymcu/arena.py")),
            @"^ARENA_SIZE: uint16 = \d+$", "ARENA_SIZE: uint16 = 64",
            System.Text.RegularExpressions.RegexOptions.Multiline))
        .Tokenize()).ParseProgram();

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"could not find '{relativePath}' walking up from {AppContext.BaseDirectory}");
    }

    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode> { ["pymcu.arena"] = ArenaModuleAst },
            new DeviceConfig { Arch = "avr" }));

    [Fact]
    public void LenOfABufferParameterSizesTheReturnedBuffer()
    {
        var main = Gen(
            "from pymcu.types import uint8, inline\n" +
            "@inline\n" +
            "def dbl(data: bytearray) -> bytearray:\n" +
            "    out = bytearray(2 * len(data))\n" +
            "    out[0] = data[0] + 1\n" +
            "    return out\n" +
            "src = bytearray(3)\n" +
            "a = dbl(src)\n" +
            "n: uint8 = len(a)\n").Functions.Single(f => f.Name == "main").Body;

        main.Any(i => i is Copy { Src: Constant { Value: 6 }, Dst: Variable { Name: "n" } })
            .Should().BeTrue(because: "the returned buffer is 2 * len(src) = 6 bytes");
        main.OfType<BitCheck>().Should().BeEmpty();
    }

    [Fact]
    public void ABufferSizedAtRunTimeCannotBeReturned()
    {
        // The @inline is expanded at module level, so bytearray(k) itself is a legal
        // once-only arena allocation; it is handing that buffer back through `return`
        // that has no answer -- the expansion has no name the caller can take over.
        var act = () => Gen(
            "import pymcu.arena as _pymcu_arena\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "from pymcu.types import uint8, inline\n" +
            "@inline\n" +
            "def mk(k: uint8) -> bytearray:\n" +
            "    out = bytearray(k)\n" +
            "    return out\n" +
            "a = mk(GPIOR0.value + 3)\n");

        act.Should().Throw<Exception>().WithMessage("*sized at run time*cannot be*returned*");
    }

    [Fact]
    public void ABufferSizedAtRunTimeInsideACalledFunctionIsRefusedFirst()
    {
        // The same shape one call deeper: the arena allocation is refused before the
        // return is even reached, because `mk` expands inside `go` and a buffer
        // allocated there runs once per call the arena can never free.
        var act = () => Gen(
            "import pymcu.arena as _pymcu_arena\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "from pymcu.types import uint8, inline\n" +
            "@inline\n" +
            "def mk(k: uint8) -> bytearray:\n" +
            "    out = bytearray(k)\n" +
            "    return out\n" +
            "def go(k: uint8) -> uint8:\n" +
            "    a = mk(k)\n" +
            "    return a[0]\n" +
            "x = go(GPIOR0.value + 3)\n");

        act.Should().Throw<Exception>().WithMessage("*run at most once*");
    }
}

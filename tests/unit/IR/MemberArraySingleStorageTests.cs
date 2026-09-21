using FluentAssertions;
using PyMCU.Backend.Analysis;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An array a constructor stores on `self` (`self.buf = bytearray(n)`, the SSD1306
/// framebuffer's `self.buffer = bytearray(513)`) is created ONCE, inside whichever function
/// inlined the constructor. The mark that says it has contiguous SRAM storage lived in the
/// per-function scan set, which was cleared when the creating function finished -- so a
/// `for i, b in enumerate(dev.buf)` inside a LATER function missed the mark and unrolled the
/// loop into per-element slot Variables `dev_buf__0 .. dev_buf__299`. Those slots were never
/// written: the element reads answered garbage, and the allocator paid for 300 dead cells on
/// top of the real array -- the framebuffer was allocated twice.
///
/// One fixed-size array has ONE storage. These tests count the storage declarations the
/// backend would see: the array's own block, and nothing else carrying its name.
/// </summary>
public class MemberArraySingleStorageTests
{
    private const string Dev =
        "from pymcu.types import uint8, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "class Dev:\n" +
        "    def __init__(self):\n" +
        "        self.buf = bytearray(300)\n" +
        "dev = Dev()\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    /// <summary>The SRAM offsets the allocator hands the backend, which become its .equ lines.</summary>
    private static Dictionary<string, int> Offsets(ProgramIR ir) =>
        new StackAllocator().Allocate(ir).Offsets;

    private static List<string> ElementSlots(Dictionary<string, int> offsets) =>
        offsets.Keys.Where(k => System.Text.RegularExpressions.Regex.IsMatch(k, @"buf__\d+$"))
            .ToList();

    private static List<ArrayLoad> LoadsOn(ProgramIR ir, string array) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>()
            .Where(l => l.ArrayName == array).ToList();

    [Fact]
    public void EnumeratingAnInstanceArrayInAnotherFunction_KeepsOneStorage()
    {
        var ir = Gen(Dev +
            "def work():\n" +
            "    for i, b in enumerate(dev.buf):\n" +
            "        GPIOR0.value = b\n" +
            "work()\n");

        Offsets(ir).Should().ContainKey("dev_buf",
            because: "the member array's contiguous block is its one storage");
        ElementSlots(Offsets(ir)).Should().BeEmpty(
            because: "a fixed-size array has ONE storage -- no dev_buf__N element slots");

        LoadsOn(ir, "dev_buf").Where(l => l.Index is Variable).Should().NotBeEmpty(
            because: "a 300-element SRAM array iterates as a counter loop, not 300 unrolled reads");
    }

    [Fact]
    public void EnumeratingAnInstanceArrayThroughAnInlineParameter_KeepsOneStorage()
    {
        // The demandant's shape: writeto(buffer) is @inline and walks `enumerate(buffer)`,
        // so the element reads land in the CALLER's function on a name bound by alias.
        var ir = Gen(Dev +
            "@inline\n" +
            "def send(buf):\n" +
            "    for i, b in enumerate(buf):\n" +
            "        GPIOR0.value = b\n" +
            "def work():\n" +
            "    send(dev.buf)\n" +
            "work()\n");

        Offsets(ir).Should().ContainKey("dev_buf");
        ElementSlots(Offsets(ir)).Should().BeEmpty(
            because: "the alias must not materialise per-element storage beside the real array");
        LoadsOn(ir, "dev_buf").Count(l => l.Index is Variable).Should().BeGreaterThan(0,
            because: "the loop reads the array's real storage, not element slots");
    }

    [Fact]
    public void EnumeratingASmallInstanceArrayInAnotherFunction_LoadsTheRealElements()
    {
        // Inside the unroll limit the loop still unrolls, but each element must come from the
        // array's storage (a constant-index load), never from a `dev_buf__k` slot.
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "class Dev:\n" +
            "    def __init__(self):\n" +
            "        self.buf = bytearray(6)\n" +
            "dev = Dev()\n" +
            "def work():\n" +
            "    for i, b in enumerate(dev.buf):\n" +
            "        GPIOR0.value = b\n" +
            "work()\n");

        ElementSlots(Offsets(ir)).Should().BeEmpty();
        LoadsOn(ir, "dev_buf").Should().NotBeEmpty(
            because: "every unrolled element read is an indexed load on the real array");
    }
}

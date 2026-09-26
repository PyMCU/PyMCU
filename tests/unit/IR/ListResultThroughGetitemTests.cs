using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `print(obj[i])` where __getitem__ hands back a heap list printed the list's pointer as an
/// integer: the dunder expansion dropped the element type its `return` recorded, and print
/// had no subscript case for a list value. adafruit_pixelbuf's `print(buf[0])` is the shape.
/// </summary>
public class ListResultThroughGetitemTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string PrintPrelude =
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write(c: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n";

    private static List<string> StrWrites(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body)
            .OfType<Copy>()
            .Where(c => c.Dst is Variable { Name: "uart_write_str.s" } && c.Src is FlashStrAddr)
            .Select(c => new string(ir.Functions.SelectMany(f => f.Body).OfType<FlashData>()
                .Single(fd => fd.Name == ((FlashStrAddr)c.Src).Name).Bytes
                .TakeWhile(b => b != 0).Select(b => (char)b).ToArray()))
            .ToList();

    [Fact]
    public void AListReturnedThroughGetitem_PrintsAsAList()
    {
        // `print(obj[i])` of a list-returning __getitem__ printed the heap pointer as an
        // integer: the dunder expansion dropped the element type its `return` recorded, and
        // print had no subscript case for a list value.
        var ir = Gen(
            "from pymcu.types import uint8, uint16, ptr\n" +
            PrintPrelude +
            "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
            "class P:\n" +
            "    def __init__(self):\n" +
            "        self.w = GPIOR0.value\n" +
            "    def _get(self, i: uint8) -> list[uint8]:\n" +
            "        v: list[uint8] = [i, i + 1, i + 2]\n" +
            "        if self.w:\n" +
            "            v.append(i + 3)\n" +
            "        return v\n" +
            "    def __getitem__(self, i: uint8):\n" +
            "        return self._get(i)\n" +
            "p = P()\n" +
            "print(p[GPIOR0.value])\n");
        // The bracket goes out through the string writer or, one character long, through the
        // byte writer; the decimal writer alone is the pointer being printed.
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        bool bracket = StrWrites(ir).Any(t => t.Contains('['))
            || body.OfType<Copy>().Any(c => c.Src is Constant { Value: '[' }
                                            && c.Dst is Variable { Name: "uart_write.c" });
        Assert.True(bracket, "print(p[i]) wrote no '[': the list was printed as a number");
    }
}

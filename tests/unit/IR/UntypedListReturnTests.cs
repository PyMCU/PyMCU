using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A function or method compiled as a subroutine that returns a local list, with no
/// `-> list[T]` on its def, was called as a void one whenever the caller was compiled
/// before the body (every module-level caller) or the method was outlined: `print(f())`
/// printed whatever the result register held. It is refused and names the annotation;
/// a `-> list[T]` method result is registered so print, len() and indexing find it.
/// </summary>
public class UntypedListReturnTests
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
    public void AnUnannotatedListReturnCalledBeforeTheBody_IsRefused()
    {
        // A module-level caller compiles before the function body, and nothing on the def
        // says it returns a list: the call was lowered as a void one and `print(g())` wrote
        // whatever the result register held.
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(
            "from pymcu.types import uint8, uint16, ptr\n" +
            PrintPrelude +
            "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
            "def g(w: uint8):\n" +
            "    v: list[uint8] = [w, w + 1]\n" +
            "    if w:\n" +
            "        v.append(w + 2)\n" +
            "    return v\n" +
            "print(g(GPIOR0.value))\n"));
        Assert.Contains("-> list[", ex.Message);
    }

    [Fact]
    public void AnUnannotatedListReturnWhoseResultIsDiscarded_StillCompiles()
    {
        var ir = Gen(
            "from pymcu.types import uint8, ptr\n" +
            "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
            "def g(w: uint8):\n" +
            "    v: list[uint8] = [w, w + 1]\n" +
            "    if w:\n" +
            "        v.append(w + 2)\n" +
            "    return v\n" +
            "g(GPIOR0.value)\n");
        Assert.Contains(ir.Functions.Single(f => f.Name == "main").Body,
            i => i is Call { FunctionName: "g" });
    }

    [Fact]
    public void AnOutlinedMethodReturningAnUnannotatedList_IsRefused()
    {
        // Compiled as a subroutine, the method was called as a void one: `print(p._get(i))`
        // printed the result register.
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(
            "from pymcu.types import uint8, uint16, ptr\n" +
            PrintPrelude +
            "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
            "class P:\n" +
            "    def __init__(self):\n" +
            "        self.w = GPIOR0.value\n" +
            "    def _get(self, i: uint8):\n" +
            "        v: list[uint8] = [i, i + 1, i + 2]\n" +
            "        if self.w:\n" +
            "            v.append(i + 3)\n" +
            "        return v\n" +
            "p = P()\n" +
            "print(p._get(GPIOR0.value))\n"));
        Assert.Contains("-> list[", ex.Message);
    }

    [Fact]
    public void AnOutlinedMethodAnnotatedListReturn_PrintsAsAList()
    {
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
            "p = P()\n" +
            "print(p._get(GPIOR0.value))\n");
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        bool bracket = StrWrites(ir).Any(t => t.Contains('['))
            || body.OfType<Copy>().Any(c => c.Src is Constant { Value: '[' }
                                            && c.Dst is Variable { Name: "uart_write.c" });
        Assert.True(bracket, "print(p._get(i)) wrote no '[': the list was printed as a number");
    }
}

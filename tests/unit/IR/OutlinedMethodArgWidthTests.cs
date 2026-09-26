using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// An argument to an outlined METHOD at a width other than its parameter's.
//
//     class Acc:
//         def add(self, n: uint16):
//             self.hits = self.hits + n
//     a.add(s + 300)     # s: uint16, so the sum is a uint32 temp
//
// printed 300, 2, 7 for CPython's 300, 302, 309. The Call instruction is marshalled by each
// argument's own width, and a plain call narrows every argument to its parameter first. The
// method paths (the Model A write-back call, a Class[N] element's method, and a sibling
// `self.m(...)` forwarded from an outlined method) did not: the uint32 temp filled R22..R25,
// and R24:R25 belong to the parameter before it, `self_hits`. The first call looked right
// because the field was still 0, which is also the high word of the sum.
//
// WHAT DISCRIMINATES: every argument of every call to a method has its parameter's width.
// Against the unfixed compiler the `n` argument is UINT32.
//
// WHAT IS INVARIANT: an argument already at its parameter's width is passed unchanged.
public class OutlinedMethodArgWidthTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Acc =
        "from pymcu.types import uint16, outline\n" +
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n\n\n" +
        "class Acc:\n" +
        "    def __init__(self):\n" +
        "        self.hits: uint16 = 0\n\n" +
        "    def add(self, n: uint16):\n" +
        "        self.hits = self.hits + n\n\n" +
        "    @outline\n" +
        "    def add_twice(self, n: uint16):\n" +
        "        self.add(n + 300)\n" +
        "        self.add(n + 2)\n\n\n";

    private static DataType WidthOf(Val v) => v switch
    {
        Variable var => var.Type,
        Temporary t => t.Type,
        _ => DataType.UNKNOWN,
    };

    private static List<Call> CallsTo(ProgramIR ir, string fn) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Call>().Where(c => c.FunctionName == fn).ToList();

    [Fact]
    public void AWiderArgumentIsNarrowedToTheMethodsParameter()
    {
        var ir = Gen(Acc +
            "a = Acc()\n" +
            "s: uint16 = GPIOR0.value\n" +
            "a.add(s + 300)\n" +
            "a.add(s + 2)\n" +
            "GPIOR1.value = a.hits\n");
        var calls = CallsTo(ir, "Acc_add");
        Assert.NotEmpty(calls);
        Assert.All(calls, c => Assert.All(c.Args, a => Assert.Equal(DataType.UINT16, WidthOf(a))));
    }

    [Fact]
    public void ASiblingCallFromAnOutlinedMethodIsNarrowedToo()
    {
        var ir = Gen(Acc +
            "a = Acc()\n" +
            "s: uint16 = GPIOR0.value\n" +
            "a.add_twice(s)\n" +
            "GPIOR1.value = a.hits\n");
        var body = ir.Functions.Single(f => f.Name == "Acc_add_twice").Body;
        var calls = body.OfType<Call>().Where(c => c.FunctionName == "Acc_add").ToList();
        Assert.NotEmpty(calls);
        Assert.All(calls, c => Assert.All(c.Args, a => Assert.Equal(DataType.UINT16, WidthOf(a))));
    }

    [Fact]
    public void AnArgumentAtTheParametersWidthIsPassedAsIs()
    {
        var ir = Gen(Acc +
            "a = Acc()\n" +
            "k: uint16 = GPIOR0.value\n" +
            "a.add(k)\n" +
            "GPIOR1.value = a.hits\n");
        var call = Assert.Single(ir.Functions.Single(f => f.Name == "main").Body
            .OfType<Call>().Where(c => c.FunctionName == "Acc_add"));
        Assert.Contains(call.Args, a => a is Variable { Name: "k" });
    }
}

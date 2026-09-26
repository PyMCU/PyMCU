using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// A `const[...]` local belongs to the scope that declares it.
//
// The set of const names was keyed by the BARE name, so one local declaration made that name
// constant in every function the generator lowered after it:
//
//     @inline
//     def f(x: const[uint32]):
//         ms: const[uint32] = x * 2
//         helper(ms)
//
// refused `ms = ms - 1` inside `helper`, which declares nothing const, with "cannot assign to
// constant 'ms'". Named `k`, the refusal landed in the stdlib's uart_text.py. Whether it fired
// depended on the order the functions were lowered in, so a helper written ABOVE the declaring
// function compiled and the same helper written below it did not. A module constant leaked
// the same way into every other module.
//
// WHAT DISCRIMINATES: the four builds below. Against the unfixed compiler each is refused.
//
// WHAT IS INVARIANT: a const local rebound in its own scope, in a plain function, an @inline
// function and a module, is still refused (ConstRebindTests pins the module spellings).
public class ConstLocalScopeTests
{
    private static void Build(string src, params (string Name, string Source)[] modules)
    {
        var imported = new Dictionary<string, ProgramNode>();
        foreach (var (name, source) in modules)
            imported[name] = new Parser(new Lexer(source).Tokenize()).ParseProgram();
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(), imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string>(modules.Select(m => m.Name)));
    }

    private static PyMCU.Common.CompilerError Reject(string src)
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Build(src));

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR1\n" +
        "from pymcu.types import uint8, uint32, const, inline\n\n";

    private const string Helper =
        "def helper(ms: uint32):\n" +
        "    ms = ms - 1\n" +
        "    GPIOR1.value = ms\n";

    [Fact]
    public void AnInlineConstLocalDoesNotReachAnotherFunction()
    {
        Build(Preamble + Helper +
            "@inline\n" +
            "def f(x: const[uint32]):\n" +
            "    ms: const[uint32] = x * 2\n" +
            "    helper(ms)\n" +
            "f(5)\n");
    }

    [Fact]
    public void AFunctionConstLocalDoesNotReachAFunctionLoweredAfterIt()
    {
        Build(Preamble +
            "def f(x: uint32):\n" +
            "    ms: const[uint32] = 10\n" +
            "    helper(ms + x)\n" +
            Helper +
            "f(GPIOR1.value)\n");
    }

    [Fact]
    public void AnInlineConstLocalDoesNotReachTheNextInlineExpansion()
    {
        Build(Preamble +
            "@inline\n" +
            "def f(x: const[uint32]):\n" +
            "    ms: const[uint32] = x * 2\n" +
            "    GPIOR1.value = ms\n" +
            "@inline\n" +
            "def g(y: uint32):\n" +
            "    ms = y + 1\n" +
            "    ms = ms + 1\n" +
            "    GPIOR1.value = ms\n" +
            "f(5)\n" +
            "g(GPIOR1.value)\n");
    }

    [Fact]
    public void AModuleConstantDoesNotReachAnotherModule()
    {
        Build(Preamble +
            "from cfg import get\n" +
            "def f(x: uint32) -> uint32:\n" +
            "    LIM = x + 300\n" +
            "    return LIM\n" +
            "GPIOR1.value = f(GPIOR1.value) + get()\n",
            ("cfg",
             "from pymcu.types import uint32, const\n" +
             "LIM: const[uint32] = 10\n" +
             "def get() -> uint32:\n" +
             "    return LIM\n"));
    }

    // --- invariants --------------------------------------------------------------

    [Fact]
    public void AnInlineConstLocalIsStillRefusedARebindInItsOwnBody()
    {
        var ex = Reject(Preamble +
            "@inline\n" +
            "def f(x: const[uint32]):\n" +
            "    ms: const[uint32] = x * 2\n" +
            "    ms = 3\n" +
            "    GPIOR1.value = ms\n" +
            "f(5)\n");

        Assert.Contains("cannot assign to constant 'ms'", ex.Message);
    }

    [Fact]
    public void AFunctionConstLocalIsStillRefusedAnAugmentedRebind()
    {
        var ex = Reject(Preamble +
            "def f(x: uint32):\n" +
            "    ms: const[uint32] = 10\n" +
            "    ms += x\n" +
            "    GPIOR1.value = ms\n" +
            "f(GPIOR1.value)\n");

        Assert.Contains("cannot assign to constant 'ms'", ex.Message);
    }

    [Fact]
    public void AMethodConstLocalIsStillRefusedARebind()
    {
        var ex = Reject(Preamble +
            "class A:\n" +
            "    def run(self):\n" +
            "        ms: const[uint32] = 10\n" +
            "        ms = 4\n" +
            "        GPIOR1.value = ms\n" +
            "a = A()\n" +
            "a.run()\n");

        Assert.Contains("cannot assign to constant 'ms'", ex.Message);
    }
}

using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The append-instance refusal (AppendClassInstanceRefusalTests) covered one spelling
/// of a wider hole: every path that writes a class instance into element storage a
/// flattened object does not have. A PyMCU instance's fields live at their own slots;
/// the bare handle names no byte a list element can hold, so any of these compiled
/// silently to repeated or never-written storage:
///
///   xs.extend([Counter(0)])   -- the .extend twin of .append
///   xs[i] = Counter(i)        -- element store into scalar/list storage
///   a = xs[i]                 -- reading an element of a Pair[N] instance array as a value
///   return inst, val          -- an instance riding a tuple return's scalar slot
///
/// Each now fails naming what the program actually tried to store.
/// </summary>
public class InstanceSequenceElementTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<CompilerError>(() => Gen(src)).Message;

    private const string Counter =
        "from pymcu.types import uint8\n" +
        "class Counter:\n" +
        "    def __init__(self, n: uint8) -> None:\n" +
        "        self._n = n\n";

    private const string Pair =
        "from pymcu.types import uint8\n" +
        "class Pair:\n" +
        "    def __init__(self, n: uint8, m: uint8) -> None:\n" +
        "        self._n = n\n" +
        "        self._m = m\n";

    [Fact]
    public void ExtendWithAnInstance_IsRefused()
    {
        var msg = Refusal(Counter +
            "xs = []\n" +
            "xs.extend([Counter(0), Counter(1)])\n");
        Assert.Contains(".extend()", msg);
        Assert.Contains("instance of 'Counter'", msg);
    }

    [Fact]
    public void ElementStoreOfAnInstance_IsRefused()
    {
        var msg = Refusal(Counter +
            "xs = [0, 0]\n" +
            "xs[0] = Counter(1)\n");
        Assert.Contains("instance of 'Counter'", msg);
    }

    [Fact]
    public void ElementStoreOfAnInstance_AtARuntimeIndex_IsRefused()
    {
        var msg = Refusal(Counter +
            "xs = []\n" +
            "xs.append(0)\n" +
            "xs.append(0)\n" +
            "i = 1\n" +
            "xs[i] = Counter(i)\n");
        Assert.Contains("instance of 'Counter'", msg);
    }

    [Fact]
    public void BareReadOfAnInstanceArrayElement_IsRefused()
    {
        // `xs[i]` on a Pair[N] names the element INSTANCE, not a byte. Field and
        // method access stay legal; binding the element as a value is the part
        // with no storage behind it.
        var msg = Refusal(Pair +
            "xs: Pair[3]\n" +
            "i = 0\n" +
            "xs[i] = Pair(i, i + 10)\n" +
            "a = xs[i]\n");
        Assert.Contains("xs[i]", msg);
        Assert.Contains("Pair", msg);
    }

    [Fact]
    public void AnInstanceInsideATupleReturn_IsRefused()
    {
        var msg = Refusal(Counter +
            "def f():\n" +
            "    inst = Counter(7)\n" +
            "    return inst, 5\n" +
            "a, b = f()\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Counter", msg);
    }

    [Fact]
    public void NestedListElementStoreOfAnInstance_IsRefused()
    {
        // `bins[0][0] = c` takes the nested-list fast path, which stored the
        // bare instance handle's stale byte -- `bool(bins[0][0])` read False
        // where CPython sees the object and prints True.
        var msg = Refusal(Counter +
            "bins: list[list[uint8]] = [[0]]\n" +
            "bins[0][0] = Counter(0)\n");
        Assert.Contains("instance of 'Counter'", msg);
        Assert.Contains("bins", msg);
    }

    [Fact]
    public void AMemberCallReturningAnInstance_InATupleReturn_IsRefused()
    {
        // `return factory.make(), 5`: the member-callee spelling declares
        // `-> Counter` the same as a bare factory call, but the check only knew
        // the VariableExpr callee -- the call evaluated to a dead handle and the
        // caller's `bool(a)` read False where CPython sees the object.
        var msg = Refusal(Counter +
            "class Factory:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._x = 0\n\n" +
            "    def make(self) -> Counter:\n" +
            "        return Counter(0)\n\n" +
            "factory = Factory()\n\n" +
            "def f():\n" +
            "    return factory.make(), 5\n\n" +
            "a, b = f()\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Counter", msg);
    }

    [Fact]
    public void AnnotatedListInitWithAnInstance_IsRefused()
    {
        // `xs: list[uint8] = [c]` built the heap payload straight from the
        // evaluated elements -- the one list-literal path that never asked
        // InstanceClassOfValueExpr -- so `xs[0]` stored the instance's bare
        // handle and read back False.
        var msg = Refusal(Counter +
            "c = Counter(0)\n" +
            "xs: list[uint8] = [c]\n");
        Assert.Contains("instance of 'Counter'", msg);
        Assert.Contains("xs", msg);
    }

    [Fact]
    public void FieldAccessOnAnInstanceArrayElement_StillCompiles()
    {
        // The read refusal is narrow: xs[i]._n is the supported spelling.
        var ir = Gen(Pair +
            "xs: Pair[3]\n" +
            "i = 0\n" +
            "xs[i] = Pair(i, i + 10)\n" +
            "a = xs[i]._n\n" +
            "b = xs[i]._m\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }
}

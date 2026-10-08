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
    public void AChainedMemberCallReturningAnInstance_InATupleReturn_IsRefused()
    {
        // `holder.factory.make()`: the receiver is an attribute chain, not a bare
        // name, and the class lookup only knew the VariableExpr spelling -- the
        // method's `-> Counter` went unseen, the instance rode a scalar slot, and
        // the caller's `bool(a)` read False where CPython holds the object.
        var msg = Refusal(Counter +
            "class Factory:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._x = 0\n\n" +
            "    def make(self) -> Counter:\n" +
            "        return Counter(0)\n\n" +
            "class Holder:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.factory = Factory()\n\n" +
            "holder = Holder()\n\n" +
            "def f():\n" +
            "    return holder.factory.make(), 5\n\n" +
            "a, b = f()\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Counter", msg);
    }

    [Fact]
    public void ACallReceiverMemberCallReturningAnInstance_InATupleReturn_IsRefused()
    {
        // `get_factory().make()`: the receiver is itself a CALL returning the
        // factory, so no name anchors anywhere -- the method's `-> Counter` can
        // only come from the receiver's evaluated type, not its syntax.
        var msg = Refusal(Counter +
            "class Factory:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._x = 0\n\n" +
            "    def make(self) -> Counter:\n" +
            "        return Counter(0)\n\n" +
            "factory = Factory()\n\n" +
            "def get_factory() -> Factory:\n" +
            "    return factory\n\n" +
            "def f():\n" +
            "    return get_factory().make(), 5\n\n" +
            "a, b = f()\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Counter", msg);
    }

    [Fact]
    public void ASeqIndexedReceiverCallReturningAnInstance_InATupleReturn_IsRefused()
    {
        // `factories[0].make()`: the receiver is an element of a compile-time
        // list of instances -- `factories__0` holds the Factory the index names --
        // but the receiver lookup only knew the `Cls[N]` spelling of an index, so
        // the method's `-> Counter` went unseen and the instance rode a scalar
        // slot to the caller's `bool(a)` False where CPython holds the object.
        var msg = Refusal(Counter +
            "class Factory:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._x = 0\n\n" +
            "    def make(self) -> Counter:\n" +
            "        return Counter(0)\n\n" +
            "factories = [Factory()]\n\n" +
            "def f():\n" +
            "    return factories[0].make(), 5\n\n" +
            "a, b = f()\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Counter", msg);
    }

    [Fact]
    public void CopiedInstanceArrayTupleResult_BareElementRead_IsRefused()
    {
        // `xs: Pair[1]` is a callee-local instance array the tuple unpack copies
        // home. The copy used to land only the bytes: a Cls[N] also lives in
        // arraysWithVariableIndex and moduleSramArrays, so EmitSequenceCopy took
        // the SRAM branch and returned before instanceArrayClass propagated --
        // `a[0]` then read the slot's first byte (False) where the alias kept
        // the element's class and refused, which is what CPython's `True` asks.
        var msg = Refusal(Pair +
            "def f():\n" +
            "    xs: Pair[1] = [Pair(9, 9)]\n" +
            "    xs[0] = Pair(0, 7)\n" +
            "    return xs, 0\n\n" +
            "a, _ = f()\n" +
            "x = a[0]\n");
        Assert.Contains("a[i]", msg);
        Assert.Contains("Pair", msg);
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

    private const string GetitemFactory =
        "class Factory:\n" +
        "    def __init__(self) -> None:\n" +
        "        self._x = 0\n\n" +
        "    def make(self) -> Counter:\n" +
        "        return Counter(0)\n\n" +
        "class D:\n" +
        "    def __init__(self) -> None:\n" +
        "        self._f = Factory()\n\n" +
        "    def __getitem__(self, k: uint8) -> Factory:\n" +
        "        return self._f\n\n" +
        "d = D()\n\n";

    [Fact]
    public void AGetitemReceiverCallReturningAnInstance_InATupleReturn_IsRefused()
    {
        // `d[0].make()`: the receiver comes from __getitem__, a shape no
        // receiver-syntax probe names -- the dispatched callee's `-> Counter`
        // is what decides. It used to compile: the caller's `bool(a)` read a
        // dead slot as False where CPython holds the object.
        var msg = Refusal(Counter + GetitemFactory +
            "def f():\n" +
            "    return d[0].make(), 5\n\n" +
            "a, b = f()\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Counter", msg);
    }

    [Fact]
    public void AGetitemResult_InATupleReturn_IsRefused()
    {
        // `return d[0], 5`: the element is not a call at all, yet it still
        // evaluates to the Factory instance the __getitem__ dispatch produced.
        var msg = Refusal(Counter + GetitemFactory +
            "def f():\n" +
            "    return d[0], 5\n\n" +
            "a, b = f()\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Factory", msg);
    }

    [Fact]
    public void AGetattrResolvedReceiverCall_InATupleReturn_IsRefused()
    {
        // `getattr(mod, "factory").make()`: the receiver is a compile-time
        // attribute lookup, another spelling the syntax probe cannot see.
        var lib = new Parser(new Lexer(Counter +
            "class Factory:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._x = 0\n\n" +
            "    def make(self) -> Counter:\n" +
            "        return Counter(0)\n\n" +
            "factory = Factory()\n").Tokenize()).ParseProgram();
        var main = new Parser(new Lexer(
            "import mod\n\n" +
            "def f():\n" +
            "    return getattr(mod, \"factory\").make(), 5\n\n" +
            "a, b = f()\n").Tokenize()).ParseProgram();
        var ex = Assert.ThrowsAny<CompilerError>(() =>
            new IRGenerator().Generate(main,
                new Dictionary<string, ProgramNode> { ["mod"] = lib },
                new DeviceConfig { Arch = "avr" }));
        Assert.Contains("instance", ex.Message);
        Assert.Contains("Counter", ex.Message);
    }

    [Fact]
    public void AModuleFunctionCallReturningAnInstance_InATupleReturn_IsRefused()
    {
        // `mod.make()`: the callee is a module attribute, not a bare name, so
        // the receiver probes never asked what it returns. A single-field
        // Counter answer would ride the scalar slot; a multi-field Pair one
        // builds the caller's target through the constructor and leaves the
        // slot dead -- the declared return type decides both.
        var lib = new Parser(new Lexer(Counter + Pair +
            "def make() -> Counter:\n" +
            "    return Counter(0)\n\n" +
            "def make_pair() -> Pair:\n" +
            "    return Pair(1, 2)\n").Tokenize()).ParseProgram();
        var main = new Parser(new Lexer(
            "import mod\n\n" +
            "def f():\n" +
            "    return mod.make(), 5\n\n" +
            "def g():\n" +
            "    return mod.make_pair(), 6\n\n" +
            "a, b = f()\n" +
            "c, d = g()\n").Tokenize()).ParseProgram();
        var ex = Assert.ThrowsAny<CompilerError>(() =>
            new IRGenerator().Generate(main,
                new Dictionary<string, ProgramNode> { ["mod"] = lib },
                new DeviceConfig { Arch = "avr" }));
        Assert.Contains("instance", ex.Message);
        Assert.Contains("Counter", ex.Message);
    }

    [Fact]
    public void AGetitemReceiverScalarCall_InATupleReturn_StillCompiles()
    {
        // The refusal asks the DISPATCHED callee's return type, so a scalar
        // answer through the same __getitem__ receiver stays legal.
        var ir = Gen(Counter +
            "class Factory:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._x = 0\n\n" +
            "    def val(self) -> uint8:\n" +
            "        return self._x + 1\n\n" +
            "class D:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._f = Factory()\n\n" +
            "    def __getitem__(self, k: uint8) -> Factory:\n" +
            "        return self._f\n\n" +
            "d = D()\n\n" +
            "def f():\n" +
            "    return d[0].val(), 5\n\n" +
            "a, b = f()\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    // Round 2: the tuple element is not a call at all. A wrapper expression --
    // ternary, `and`, `or` -- or a `*(...)` splice produces a scalar temp that only
    // CARRIES the instance a dispatched callee made, so the check reads the class
    // off the evaluated value, never off the element's spelling.

    private const string CounterFactory =
        "from pymcu.types import uint8\n" +
        "class Counter:\n" +
        "    def __init__(self, n: uint8) -> None:\n" +
        "        self._n = n\n\n" +
        "def make(n: uint8) -> Counter:\n" +
        "    return Counter(n)\n\n";

    [Fact]
    public void ATernaryProducingAnInstance_InATupleReturn_IsRefused()
    {
        // `(make(1) if flag else make(2))`: the merge temp lost the arms' class
        // when each arm's proof lived in its own branch state. It compiled and
        // the caller read a field byte where CPython holds the object.
        var msg = Refusal(CounterFactory +
            "def f(flag: bool):\n" +
            "    return (make(1) if flag else make(2)), 7\n\n" +
            "a, b = f(True)\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Counter", msg);
    }

    [Fact]
    public void AnAndProducingAnInstance_InATupleReturn_IsRefused()
    {
        var msg = Refusal(CounterFactory +
            "def f(flag: bool):\n" +
            "    return (flag and make(1)), 7\n\n" +
            "a, b = f(True)\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Counter", msg);
    }

    [Fact]
    public void AnOrProducingAnInstance_InATupleReturn_IsRefused()
    {
        var msg = Refusal(CounterFactory +
            "def f(flag: bool):\n" +
            "    return (flag or make(1)), 7\n\n" +
            "a, b = f(False)\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Counter", msg);
    }

    [Fact]
    public void AVariadicSplicedCall_InATupleReturn_IsRefused()
    {
        // `mod.make(*(1, 2))`: the `*args` splice rewrites the CallExpr, so any
        // stamp tied to the original node is left behind -- the produced temp
        // is what carries the class.
        var lib = new Parser(new Lexer(
            "from pymcu.types import uint8\n" +
            "class Pair:\n" +
            "    def __init__(self, n: uint8, m: uint8) -> None:\n" +
            "        self._n = n\n" +
            "        self._m = m\n\n" +
            "def make(x: uint8, y: uint8) -> Pair:\n" +
            "    return Pair(x, y)\n").Tokenize()).ParseProgram();
        var main = new Parser(new Lexer(
            "import mod\n\n" +
            "def f():\n" +
            "    return mod.make(*(1, 2)), 7\n\n" +
            "a, b = f()\n").Tokenize()).ParseProgram();
        var ex = Assert.ThrowsAny<CompilerError>(() =>
            new IRGenerator().Generate(main,
                new Dictionary<string, ProgramNode> { ["mod"] = lib },
                new DeviceConfig { Arch = "avr" }));
        Assert.Contains("instance", ex.Message);
        Assert.Contains("Pair", ex.Message);
    }

    [Fact]
    public void AnUnannotatedRelayOfAnInstance_InATupleReturn_IsRefused()
    {
        // `relay` declares no return type; its body returns what `make`
        // dispatched to, and inference records `-> Counter` on it. The temp
        // the caller receives still names the class.
        var msg = Refusal(CounterFactory +
            "def relay():\n" +
            "    return make(3)\n\n" +
            "def f():\n" +
            "    return relay(), 0\n\n" +
            "a, b = f()\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Counter", msg);
    }

    [Fact]
    public void AnOutlinedMethodCallInATernary_InATupleReturn_IsRefused()
    {
        // The deepest combination: an @outline method dispatched to a real
        // subroutine, wrapped in a ternary. Neither the call's own syntax
        // nor a branch-local registration reaches the element check; only
        // the produced temp does.
        var msg = Refusal(Counter +
            "class Fac:\n" +
            "    def __init__(self) -> None:\n" +
            "        pass\n\n" +
            "    @outline\n" +
            "    def make(self, n: uint8) -> Counter:\n" +
            "        return Counter(n)\n\n" +
            "fac = Fac()\n\n" +
            "def f(flag: bool):\n" +
            "    return (fac.make(1) if flag else fac.make(2)), 7\n\n" +
            "a, b = f(True)\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Counter", msg);
    }

    [Fact]
    public void ATernaryOfScalars_InATupleReturn_StillCompiles()
    {
        var ir = Gen(CounterFactory +
            "def f(flag: bool):\n" +
            "    return (1 if flag else 2), 7\n\n" +
            "a, b = f(True)\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void ShortCircuitsOfScalars_InATupleReturn_StillCompile()
    {
        var ir = Gen(CounterFactory +
            "def f(flag: bool):\n" +
            "    return (flag and 3), (flag or 4)\n\n" +
            "a, b = f(True)\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    // Round 3: the value-decided check cut both ways. A scalar VIEW sharing a
    // single-field carrier's storage -- a field read, a declared-scalar dunder,
    // an alias of either -- used to inherit the class the alias walk reached.
    // None of these is the object, so every one must keep compiling.

    private const string SingleField =
        "from pymcu.types import uint8\n" +
        "class C:\n" +
        "    def __init__(self, n: uint8) -> None:\n" +
        "        self._n = n\n\n" +
        "    def dup(self) -> \"C\":\n" +
        "        return C(self._n + 1)\n\n" +
        "    def __len__(self) -> int:\n" +
        "        return self._n\n\n" +
        "def make(n: uint8) -> C:\n" +
        "    return C(n)\n\n";

    [Fact]
    public void AScalarFieldOfAProducedInstance_InATupleReturn_StillCompiles()
    {
        // `make(3)._n`: the produced value is an instance, but the field read
        // on it is a scalar sharing the carrier's one byte of storage.
        var ir = Gen(SingleField +
            "def f():\n" +
            "    return make(3)._n, 0\n\n" +
            "a, b = f()\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void LenOfAProducedInstance_InATupleReturn_StillCompiles()
    {
        // `len(make(3))`: __len__ declares -> int, so the produced answer is a
        // scalar wherever its byte physically lives.
        var ir = Gen(SingleField +
            "def f():\n" +
            "    return len(make(3)), 0\n\n" +
            "a, b = f()\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void AScalarFieldOfAMethodReturnedInstance_InATupleReturn_StillCompiles()
    {
        // `c.dup()._n`: the same read one hop deeper -- the dispatched callee
        // declares -> C, and the field read still answers the scalar.
        var ir = Gen(SingleField +
            "def f():\n" +
            "    c = make(3)\n" +
            "    return c.dup()._n, 0\n\n" +
            "a, b = f()\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void AnAliasedScalarField_InATupleReturn_StillCompiles()
    {
        // `y = x._n` then `return y, 0`: the alias carries the field's byte,
        // not the object -- a bind is a read, and this read was a scalar.
        var ir = Gen(SingleField +
            "def f():\n" +
            "    x = make(3)\n" +
            "    y = x._n\n" +
            "    return y, 0\n\n" +
            "a, b = f()\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void AWalrusBoundInstance_InATupleReturn_IsRefused()
    {
        // `(x := make(1))`: the walrus binds the produced instance and hands
        // it back, so the element is the object -- the binding used to drop
        // the class the call had just stamped.
        var msg = Refusal(
            "from pymcu.types import uint8\n" +
            "class Pair:\n" +
            "    def __init__(self, a: uint8, b: uint8) -> None:\n" +
            "        self.a = a\n" +
            "        self.b = b\n\n" +
            "def make(n: uint8) -> Pair:\n" +
            "    return Pair(n, n + 1)\n\n" +
            "def f():\n" +
            "    return (x := make(1)), 0\n\n" +
            "a, b = f()\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Pair", msg);
    }

    [Fact]
    public void AnOrWithAnInstanceOperand_InATupleReturn_IsRefused()
    {
        // `c or 7` with a truthy c evaluates to c itself -- the operand, not
        // its truthiness byte. The element is the instance, refused.
        var msg = Refusal(
            "from pymcu.types import uint8\n" +
            "class C:\n" +
            "    def __init__(self, n: uint8) -> None:\n" +
            "        self._n = n\n\n" +
            "    def __bool__(self) -> bool:\n" +
            "        return self._n != 0\n\n" +
            "c = C(1)\n\n" +
            "def f():\n" +
            "    return (c or 7), 0\n\n" +
            "a, b = f()\n");
        Assert.Contains("instance", msg);
        Assert.Contains("C", msg);
    }

    // Round 4 (silentfix5): the refusal has to fire when the element is an
    // instance on only SOME of the paths that reach it, and must stop firing
    // once a later write has genuinely rebound the name to a scalar.

    [Fact]
    public void AnInstanceOnOneBranchArm_InATupleReturn_IsRefused()
    {
        // `x` is a byte on one arm and a Pair on the other: the merge dropped
        // the class because the arms disagreed, and the slot copy handed the
        // caller the scalar path's byte as if it were the object. The flag is
        // run-time (a caller's parameter) or the dead arm folds away and the
        // merge never sees the disagreement.
        var msg = Refusal(Pair +
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "def f(flag: bool):\n" +
            "    if flag:\n" +
            "        x = 7\n" +
            "    else:\n" +
            "        x = Pair(1, 2)\n" +
            "    return x, 0\n\n" +
            "a, b = f(GPIOR0.value != 0)\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Pair", msg);
    }

    [Fact]
    public void AnInstanceOnOneBranchArm_AsASingleReturn_IsRefused()
    {
        // The same merge feeding a bare `return x` -- the tuple check's twin:
        // the result slot copy would carry the scalar path's byte.
        var msg = Refusal(Pair +
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "def f(flag: bool):\n" +
            "    if flag:\n" +
            "        x = 7\n" +
            "    else:\n" +
            "        x = Pair(1, 2)\n" +
            "    return x\n\n" +
            "a = f(GPIOR0.value != 0)\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Pair", msg);
    }

    [Fact]
    public void AnAliasToAProducedInstance_SurvivesALabel_AndIsRefused()
    {
        // `x = Pair() if flag else Pair()` binds x to the ternary's result
        // temp. A label between the bind and the return (`y = gate and 1`
        // emits one) used to drop the alias as value-tracking state, leaving x
        // answering as an ordinary scalar.
        var msg = Refusal(Pair +
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "def f(flag: bool, gate: bool):\n" +
            "    x = Pair(1, 2) if flag else Pair(3, 4)\n" +
            "    y = gate and 1\n" +
            "    return x, 0\n\n" +
            "a, b = f(GPIOR0.value != 0, GPIOR0.value != 0)\n");
        Assert.Contains("instance", msg);
        Assert.Contains("Pair", msg);
    }

    [Fact]
    public void AScalarRebindAfterAnInstance_StillCompiles()
    {
        // `x = Pair(); x = 5`: the second write rebinds the name to a byte;
        // the old class no longer describes it.
        var ir = Gen(Pair +
            "def f():\n" +
            "    x = Pair(1, 2)\n" +
            "    x = 5\n" +
            "    return x, 0\n\n" +
            "a, b = f()\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void AScalarWalrusRebindAfterAnInstance_StillCompiles()
    {
        // `y = (x := 5)` is the same write spelled differently.
        var ir = Gen(Pair +
            "def f():\n" +
            "    x = Pair(1, 2)\n" +
            "    y = (x := 5)\n" +
            "    return x, 0\n\n" +
            "a, b = f()\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void AScalarLoopVarRebindAfterAnInstance_StillCompiles()
    {
        // `for x in [5]` writes x every iteration it runs -- the Pair the name
        // used to hold is gone the moment the loop binds the element.
        var ir = Gen(Pair +
            "def f():\n" +
            "    x = Pair(1, 2)\n" +
            "    for x in [5]:\n" +
            "        pass\n" +
            "    return x, 0\n\n" +
            "a, b = f()\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void ARuntimeLoopVarRebindAfterAnInstance_StillCompiles()
    {
        // `for x in range(3)` rebinds through the run-time counter path, not
        // the literal unroll -- the pre-loop sweep has to cover it too.
        var ir = Gen(Pair +
            "def f():\n" +
            "    x = Pair(1, 2)\n" +
            "    y = 0\n" +
            "    for x in range(3):\n" +
            "        y = x\n" +
            "    return y, 0\n\n" +
            "a, b = f()\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void ALoopVarBoundToAnInstanceElement_StillAnswersItsFields()
    {
        // `for x in (p,)` rebinds a scalar name to the object: the alias the
        // iteration binds must clear the stale constant `x = 5` left, or
        // `x.a` folds to 5 while x IS p.
        var ir = Gen(Pair +
            "def f():\n" +
            "    x = 5\n" +
            "    p = Pair(1, 2)\n" +
            "    for x in (p,):\n" +
            "        return x._n, x._m\n" +
            "    return 9, 9\n\n" +
            "a, b = f()\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }
}

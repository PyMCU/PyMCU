using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A lowercase module global in an INSTALLED module (the stdlib, a compat layer) has storage,
/// and its literal initializer was never written to it: only a project module's level runs,
/// and only the ALL-CAPS spelling folds to a constant. The compat layers' `sys.maxsize =
/// 2147483647` read 0, `pymcu.random`'s `_state: uint32 = 1` started its sequence from 0, and
/// `microcontroller.watchdog = WatchDogTimer()` never ran its constructor, so its timeout read
/// 0.0 s. The modules below are passed with no project modules, so every one of them is installed.
/// </summary>
public class InstalledModuleGlobalInitTests
{
    private static ProgramIR Gen(string modSrc, string mainSrc)
    {
        var mods = new Dictionary<string, ProgramNode>
        {
            ["lib"] = new Parser(new Lexer(modSrc).Tokenize()).ParseProgram(),
        };
        var config = new DeviceConfig { Arch = "avr", Chip = "atmega328p" };
        var program = new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram();
        new ConditionalCompilator(config).Process(program);
        return new IRGenerator().Generate(program, mods, config);
    }

    private static bool Seeds(ProgramIR ir, string global, int value) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Any(c => c.Src is Constant k && k.Value == value && c.Dst is Variable v && v.Name == global);

    [Fact]
    public void LowercaseLiteralGlobal_IsSeededWithItsInitializer()
    {
        var ir = Gen("maxsize = 2147483647\n",
            "import lib\nbuf = bytearray(4)\nbuf[0] = lib.maxsize\n");
        Assert.True(Seeds(ir, "lib_maxsize", 2147483647));
    }

    [Fact]
    public void AnnotatedGlobal_WrittenByAFunction_StartsFromItsInitializer()
    {
        var ir = Gen(
            "_state: uint32 = 1\n" +
            "def nxt() -> uint32:\n" +
            "    global _state\n" +
            "    _state = _state * 3 + 1\n" +
            "    return _state\n",
            "import lib\nbuf = bytearray(4)\nbuf[0] = lib.nxt()\n");
        Assert.True(Seeds(ir, "lib__state", 1));
    }

    [Fact]
    public void RebindingAtTopLevel_IsSeededInOrder_TheLastOneWins()
    {
        var ir = Gen("two = 1\ntwo = 300\n",
            "import lib\nbuf = bytearray(4)\nbuf[0] = lib.two\n");
        var init = ir.Functions.Single(f => f.Name == "lib___module_init");
        var seeds = init.Body.OfType<Copy>()
            .Where(c => c.Dst is Variable { Name: "lib_two" } && c.Src is Constant)
            .Select(c => ((Constant)c.Src).Value).ToList();
        Assert.Equal(300, seeds.Last());
    }

    // Controls: what the seeding must NOT touch.

    [Fact]
    public void ZeroInitializer_EmitsNoModuleInit()
    {
        // Storage starts at zero; seeding it would only grow every program that imports one.
        var ir = Gen("_count = 0\n", "import lib\nbuf = bytearray(4)\nbuf[0] = lib._count\n");
        Assert.DoesNotContain(ir.Functions, f => f.Name == "lib___module_init");
    }

    [Fact]
    public void CallInitializer_IsRunForTheValue()
    {
        // Python runs the call on import to get the value; the global holds its result.
        var ir = Gen("def f() -> uint8:\n    return 7\nv = f()\n",
            "import lib\nbuf = bytearray(4)\nbuf[0] = lib.v\n");
        Assert.Contains(ir.Functions, f => f.Name == "lib___module_init");
    }

    [Fact]
    public void InstanceWhoseConstructorSetsAField_IsConstructed()
    {
        var ir = Gen(
            "class W:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        self._t = 1000\n" +
            "    def t(self) -> uint16:\n" +
            "        return self._t\n" +
            "w = W()\n",
            "import lib\nx: uint16 = lib.w.t()\nbuf = bytearray(4)\nbuf[0] = x\n");
        Assert.True(ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Any(c => c.Src is Constant { Value: 1000 }));
    }

    [Fact]
    public void InstanceWithNoFields_IsNotConstructed()
    {
        // `machine.mem8 = _Mem8()`: nothing to write, and constructing it cost every
        // program that imports the module a call.
        var ir = Gen(
            "class M:\n" +
            "    def get(self) -> uint8:\n" +
            "        return 3\n" +
            "m = M()\n",
            "import lib\nbuf = bytearray(4)\nbuf[0] = lib.m.get()\n");
        Assert.DoesNotContain(ir.Functions, f => f.Name == "lib___module_init");
    }

    [Fact]
    public void BareCallAtTopLevel_IsNotRun()
    {
        // Not a binding: the part of an installed level written knowing it does not run.
        var ir = Gen("def g():\n    pass\nn = 5\ng()\n",
            "import lib\nbuf = bytearray(4)\nbuf[0] = lib.n\n");
        var init = ir.Functions.Single(f => f.Name == "lib___module_init");
        Assert.DoesNotContain(init.Body, i => i is Call c && c.FunctionName.EndsWith("g"));
    }

    [Fact]
    public void BindingNestedInABlock_IsRefused()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "m = 1\ntry:\n    m = 2\nexcept Exception:\n    pass\n",
            "import lib\nbuf = bytearray(4)\nbuf[0] = lib.m\n"));
        Assert.Contains("inside a block of the module level", ex.Message);
    }

    [Fact]
    public void GlobalNothingReads_IsNotSeeded()
    {
        // `import sys` for sys.implementation must not pay for seeding sys.maxsize.
        var ir = Gen("maxsize = 2147483647\nk = 3\n",
            "import lib\nbuf = bytearray(4)\nbuf[0] = lib.k\n");
        Assert.False(Seeds(ir, "lib_maxsize", 2147483647));
        Assert.True(Seeds(ir, "lib_k", 3));
    }
}

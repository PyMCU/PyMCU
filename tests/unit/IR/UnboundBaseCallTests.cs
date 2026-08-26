using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Regression tests for the unbound spelling of a base-class call, `Base.method(self, ...)`
/// (issue #131). Callee resolution mangled it as &lt;CallingClass&gt;_&lt;Base&gt;_&lt;method&gt;
/// and the build failed naming a function the program never mentions
/// (`Child_Base___init__`). It is ordinary Python and reaches the same body the bound call
/// reaches, so it must lower to what `super()` lowers to.
///
/// The oracle is the super() spelling: the two write the same program, so they must generate
/// the same IR.
/// </summary>
public class UnboundBaseCallTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig());
    }

    private static List<string> Render(ProgramIR ir) =>
        ir.Functions.SelectMany(f => new[] { "FUNC " + f.Name }
            .Concat(f.Body.Where(i => i is not DebugLine).Select(i => i.ToString() ?? ""))).ToList();

    private const string Preamble =
        "from pymcu.types import uint8, uint16, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n";

    // Reproducer A of the issue: the constructor forward. This is the common one -- it is how
    // a great deal of ordinary Python initialises a subclass.
    private const string CtorProgram =
        Preamble +
        "class Base:\n" +
        "    def __init__(self, offset: uint16):\n" +
        "        self.offset: uint16 = offset\n" +
        "    def read(self, raw: uint16) -> uint16:\n" +
        "        return raw + self.offset\n" +
        "class Child(Base):\n" +
        "    def __init__(self, offset: uint16):\n" +
        "        {DELEGATE}\n" +
        "def main():\n" +
        "    seed: uint8 = G.value\n" +
        "    c = Child(10)\n" +
        "    G.value = uint8(c.read(uint16(seed) + 5))\n";

    [Fact]
    public void UnboundBaseCtor_DoesNotCallAMangledName()
    {
        // The defect signature: a Call to `Child_Base___init__`, the caller's own prefix joined
        // to `Base.__init__` as if the base class name were part of the method name. Nothing
        // emits that function, so the build failed naming a symbol the program never mentions.
        var ir = Gen(CtorProgram.Replace("{DELEGATE}", "Base.__init__(self, offset)"));
        Assert.DoesNotContain(ir.Functions.SelectMany(f => f.Body),
            i => i is Call c && c.FunctionName.Contains("Child_Base"));
    }

    [Fact]
    public void UnboundBaseCtor_LowersLikeSuper()
    {
        var unbound = Render(Gen(CtorProgram.Replace("{DELEGATE}", "Base.__init__(self, offset)")));
        var viaSuper = Render(Gen(CtorProgram.Replace("{DELEGATE}", "super().__init__(offset)")));
        Assert.Equal(viaSuper, unbound);
    }

    // Reproducer B of the issue: an ordinary method, same root cause. `Base.read(self, raw) * 2`
    // must reach the base body with the receiver's own fields.
    private const string MethodProgram =
        Preamble +
        "class Base:\n" +
        "    def __init__(self, offset: uint16):\n" +
        "        self.offset: uint16 = offset\n" +
        "    def read(self, raw: uint16) -> uint16:\n" +
        "        return raw + self.offset\n" +
        "class Child(Base):\n" +
        "    def read(self, raw: uint16) -> uint16:\n" +
        "        return {DELEGATE} * 2\n" +
        "def main():\n" +
        "    seed: uint8 = G.value\n" +
        "    c = Child(10)\n" +
        "    G.value = uint8(c.read(uint16(seed) + 5))\n";

    [Fact]
    public void UnboundBaseMethod_ReachesTheBaseBody()
    {
        // Before the fix: a Call to `Child_Base_read`, which nothing emits.
        var ir = Gen(MethodProgram.Replace("{DELEGATE}", "Base.read(self, raw)"));
        var body = ir.Functions.First(f => f.Name == "main").Body;
        Assert.DoesNotContain(ir.Functions.SelectMany(f => f.Body),
            i => i is Call c && c.FunctionName.Contains("Child_Base"));

        // The base body is reached, and the doubling it wraps is emitted.
        //
        // This used to assert that a Binary added the CONSTANT 10, which was an accident of the
        // unbound spelling being force-inlined: the offset folded into the expansion. #160 made
        // the spelling outline like super(), so the offset now arrives as a run-time parameter
        // and no literal 10 appears. The assertion was anchored to a lowering detail rather than
        // to behaviour, and it went red on a change that made the program strictly better.
        // What is actually being claimed is that the base body runs, so claim that.
        var all = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.Contains(all, i => i is Binary { Op: IR.BinaryOp.Mul } or Binary { Op: IR.BinaryOp.LShift });
        Assert.True(
            ir.Functions.Any(f => f.Name.EndsWith("_read"))
            || all.Any(i => i is Call c2 && c2.FunctionName.EndsWith("_read")),
            "the base body must be reachable, as its own function or through a call to one");
        Assert.NotEmpty(body);
    }

    // The realistic shape from the issue's "where it was found": a base plus a subclass that
    // has its OWN field and forwards the base's through the unbound ctor. The subclass's slot
    // layout has to merge the base's fields, which the layout scan only did for super().
    private const string TwoFieldProgram =
        Preamble +
        "class Sensor:\n" +
        "    def __init__(self, offset: uint16):\n" +
        "        self.offset: uint16 = offset\n" +
        "    def raw_to_value(self, raw: uint16) -> uint16:\n" +
        "        return raw + self.offset\n" +
        "class Hot(Sensor):\n" +
        "    def __init__(self, offset: uint16, scale: uint16):\n" +
        "        {DELEGATE}\n" +
        "        self.scale: uint16 = scale\n" +
        "    def read(self, raw: uint16) -> uint16:\n" +
        "        return self.raw_to_value(raw) * self.scale\n" +
        "def main():\n" +
        "    seed: uint8 = G.value\n" +
        "    h = Hot(10, 3)\n" +
        "    G.value = uint8(h.read(uint16(seed) + 5))\n";

    [Fact]
    public void UnboundBaseCtor_WithOwnFields_LowersLikeSuper()
    {
        var unbound = Render(Gen(TwoFieldProgram.Replace("{DELEGATE}", "Sensor.__init__(self, offset)")));
        var viaSuper = Render(Gen(TwoFieldProgram.Replace("{DELEGATE}", "super().__init__(offset)")));
        Assert.Equal(viaSuper, unbound);
    }

    // A base call binds its arguments in its own loop, which stopped at the end of the
    // parameter list and dropped anything past it. An ordinary call has refused that since
    // #151 ("too many arguments in call to constructor of 'Box'"), so a base call must too:
    // `Base.__init__(self, offset, 99)` built clean and the 99 vanished. Both spellings share
    // the emitter, so both are covered; the diagnostic names what the user wrote, not the
    // mangled callee.
    [Theory]
    [InlineData("Base.__init__(self, offset, 99)", "Base.__init__")]
    [InlineData("super().__init__(offset, 99)", "super().__init__")]
    public void BaseCall_WithTooManyArguments_IsRefused(string delegateCall, string shown)
    {
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => Gen(CtorProgram.Replace("{DELEGATE}", delegateCall)));
        Assert.Contains("too many arguments", ex.Message);
        Assert.Contains(shown, ex.Message);
    }

    // A base method whose declared return type is a multi-field class (PyMCU#157). The base
    // body used to vanish and the caller added two slots nobody wrote, reading zero, silently.
    // Both spellings must now reach the body and compute the same value.
    //
    // DISCRIMINATING: the invariant below. Before the fix, `Child_widen` was two instructions
    // adding `p_a` and `p_b`, neither of which any instruction writes, in both spellings.
    // INVARIANT: that the program builds at all, and that no mangled `Child_Base_split`
    // appears. Both held for `super()` before the fix, so neither alone would have caught it.
    private const string MultiFieldReturn =
        "class Pair:\n" +
        "    def __init__(self, a: uint16, b: uint16):\n" +
        "        self.a: uint16 = a\n" +
        "        self.b: uint16 = b\n" +
        "class Base:\n" +
        "    def __init__(self, offset: uint16):\n" +
        "        self.offset: uint16 = offset\n" +
        "    def split(self, raw: uint16) -> Pair:\n" +
        "        return Pair(raw + self.offset, raw)\n" +
        "class Child(Base):\n" +
        "    def __init__(self, offset: uint16):\n" +
        "        {CTOR}\n" +
        "    def widen(self, raw: uint16) -> uint16:\n" +
        "        p = {DELEGATE}\n" +
        "        return p.a + p.b\n" +
        "def main():\n" +
        "    seed: uint8 = G.value\n" +
        "    c = Child(10)\n" +
        "    G.value = uint8(c.widen(uint16(seed) + 5))\n";

    [Theory]
    [InlineData("Base.__init__(self, offset)", "Base.split(self, raw)")]
    [InlineData("super().__init__(offset)", "super().split(raw)")]
    public void BaseCallReturningAMultiFieldClass_ReadsNothingUnwritten(string ctor, string delegateCall)
    {
        var ir = Gen(Preamble + MultiFieldReturn.Replace("{CTOR}", ctor).Replace("{DELEGATE}", delegateCall));

        Assert.DoesNotContain(ir.Functions.SelectMany(f => f.Body),
            i => i is Call c && c.FunctionName.Contains("Child_Base"));

        // Every Variable an instruction READS must be written somewhere, or be a parameter of
        // the function that reads it. Two unwritten slots being added together IS the defect.
        foreach (var fn in ir.Functions)
        {
            var written = new HashSet<string>(
                fn.Body.SelectMany(WrittenNames).Concat(fn.Params));
            foreach (var name in fn.Body.SelectMany(ReadNames))
                Assert.True(written.Contains(name),
                    $"{fn.Name} reads '{name}', which nothing in it writes and which is not a parameter");
        }
    }

    private static IEnumerable<string> WrittenNames(Instruction i) => i switch
    {
        Copy { Dst: Variable v } => [v.Name],
        Binary { Dst: Variable v } => [v.Name],
        Unary { Dst: Variable v } => [v.Name],
        Call { Dst: Variable v } => [v.Name],
        _ => [],
    };

    private static IEnumerable<string> ReadNames(Instruction i) => i switch
    {
        Copy { Src: Variable v } => [v.Name],
        Binary b => new[] { b.Src1, b.Src2 }.OfType<Variable>().Select(v => v.Name),
        Unary { Src: Variable v } => [v.Name],
        Return { Value: Variable v } => [v.Name],
        _ => [],
    };

    // The two spellings of a base call must LOWER the same, not merely compute the same. The
    // outlining scan read the leading `self` of `Base.read(self, raw)` as a bare self passed by
    // value, refused outlining for that spelling alone, and force-inlined it at every call site
    // while super() emitted one shared subroutine. For a base method with control flow that was
    // up to 130% of program size (PyMCU#160).
    //
    // DISCRIMINATING: `Child_read` existing as a function in the unbound lowering. Before the
    // fix it existed for super() and not for the unbound spelling, which is the whole defect.
    // INVARIANT: that both compute the same value. That held before and says nothing.
    private const string BaseCallInMethod =
        "class Base:\n" +
        "    def __init__(self, offset: uint16):\n" +
        "        self.offset: uint16 = offset\n" +
        "    def read(self, raw: uint16) -> uint16:\n" +
        "        t: uint16 = raw + self.offset\n" +
        "        if t > 500:\n" +
        "            t = t - 100\n" +
        "        return t\n" +
        "class Child(Base):\n" +
        "    def read(self, raw: uint16) -> uint16:\n" +
        "        return {DELEGATE} * 2\n" +
        "def main():\n" +
        "    seed: uint16 = uint16(G.value)\n" +
        "    c = Child(10)\n" +
        "    G.value = uint8(c.read(seed + 1))\n" +
        "    G.value = uint8(c.read(seed + 2))\n" +
        "    G.value = uint8(c.read(seed + 3))\n";

    [Fact]
    public void BothSpellingsOfABaseCall_LowerIdentically()
    {
        var unbound = Render(Gen(Preamble + BaseCallInMethod.Replace("{DELEGATE}", "Base.read(self, raw)")));
        var viaSuper = Render(Gen(Preamble + BaseCallInMethod.Replace("{DELEGATE}", "super().read(raw)")));

        // The shared subroutine exists in BOTH. Before the fix it was absent from the unbound
        // one, whose body had been expanded into every call site instead.
        Assert.Contains("FUNC Child_read", unbound);
        Assert.Contains("FUNC Child_read", viaSuper);
        Assert.Equal(viaSuper, unbound);
    }

    // The guard on the fix: `Cls.helper(...)` where helper takes no receiver is NOT a method
    // call on an instance, and must keep resolving the way it always did.
    [Fact]
    public void ClassLevelStaticCall_IsUnaffected()
    {
        var ir = Gen(
            Preamble +
            "class Util:\n" +
            "    @staticmethod\n" +
            "    def twice(x: uint8) -> uint8:\n" +
            "        return x * 2\n" +
            "def main():\n" +
            "    seed: uint8 = G.value\n" +
            "    G.value = Util.twice(seed)\n");

        // It stays an ordinary call to the class-qualified function, with the argument in the
        // callee's own parameter slot -- not a method expansion with the argument bound as self.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body),
            i => i is Call { FunctionName: "Util_twice" });
        Assert.Contains(ir.Functions.SelectMany(f => f.Body),
            i => i is Copy { Dst: Variable { Name: "Util_twice.x" } });
    }

    // Deciding whether the receiver names an instance must EMIT NOTHING, because that decision
    // can come out no. When it does, the call falls through to the ordinary path, which
    // evaluates the receiver itself -- so a receiver evaluated by the test AND by the path that
    // handles it runs TWICE. `Base.read(r.probe(), x)` emitted two calls to probe(), and a
    // receiver expression with a side effect performed it twice with nothing reported.
    //
    // Counting the emitted calls is the whole assertion: the program is correct either way if
    // you only look at the value, which is why this went unnoticed.
    [Fact]
    public void ReceiverThatIsACall_IsEvaluatedExactlyOnce()
    {
        var ir = Gen(
            Preamble +
            "class Base:\n" +
            "    def __init__(self, offset: uint16):\n" +
            "        self.offset: uint16 = offset\n" +
            "    def read(self, raw: uint16) -> uint16:\n" +
            "        return raw + self.offset\n" +
            "class Child(Base):\n" +
            "    def __init__(self, offset: uint16):\n" +
            "        Base.__init__(self, offset)\n" +
            "class Registry:\n" +
            "    def __init__(self, offset: uint16):\n" +
            "        self._probe = Child(offset)\n" +
            "    def probe(self) -> Child:\n" +
            "        return self._probe\n" +
            "def main():\n" +
            "    seed: uint8 = G.value\n" +
            "    r = Registry(10)\n" +
            "    G.value = uint8(Base.read(r.probe(), uint16(seed) + 5))\n");

        int probeCalls = ir.Functions
            .SelectMany(f => f.Body)
            .Count(i => i is Call { FunctionName: "Registry_probe" });
        Assert.Equal(1, probeCalls);
    }
}

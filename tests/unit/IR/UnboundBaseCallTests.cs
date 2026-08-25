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

        // The base body is reached and the base's field arrives with the constructor's value:
        // the doubling shift is emitted over a value derived from raw + 10.
        Assert.Contains(body.Concat(ir.Functions.SelectMany(f => f.Body)),
            i => i is Binary { Op: IR.BinaryOp.Add, Src2: Constant { Value: 10 } });
        Assert.Contains(body.Concat(ir.Functions.SelectMany(f => f.Body)),
            i => i is Binary { Op: IR.BinaryOp.Mul } or Binary { Op: IR.BinaryOp.LShift });
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

    // A base method whose declared return type is a multi-field class is force-inlined (#49),
    // and the definition registered for it is the OUTLINED rewrite, whose instance arrives as
    // one `self_<field>` parameter per field. The unbound call is refused rather than expanded,
    // because super() expands that shape and MISCOMPILES it silently (the base body vanishes
    // and the caller reads unwritten self_* slots as zero). What this pins is that the refusal
    // names the construct, not the internal mangled name `Child_Base_split` #131 complains about.
    [Fact]
    public void UnboundBaseCall_ReturningAMultiFieldClass_IsNamedNotMangled()
    {
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => Gen(
            Preamble +
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
            "        Base.__init__(self, offset)\n" +
            "    def widen(self, raw: uint16) -> uint16:\n" +
            "        return Base.split(self, raw).a\n" +
            "def main():\n" +
            "    seed: uint8 = G.value\n" +
            "    c = Child(10)\n" +
            "    G.value = uint8(c.widen(uint16(seed) + 5))\n"));

        Assert.Contains("Base.split", ex.Message);
        Assert.DoesNotContain("Child_Base_split", ex.Message);
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
}

using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A battery over the single-inheritance surface: the shapes a user actually writes
/// (inherited methods, properties, super() calls, cross-module bases) plus the exotic
/// corners where receiver resolution has broken before. Failing shapes are skipped
/// against their tracking issues:
///
///   #471 -- a base class attribute is invisible through the subclass NAME
///   #477 -- super() only resolves as the receiver of a call, never of a property read
///   #478 -- an unannotated method's return value is dropped when it outlines
/// </summary>
public class InheritanceSurfaceTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static ProgramIR GenWithModules(string mainSrc, params (string Name, string Source)[] modules)
    {
        var imported = new Dictionary<string, ProgramNode>();
        foreach (var (name, source) in modules)
            imported[name] = new Parser(new Lexer(source).Tokenize()).ParseProgram();

        var mainAst = new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram();
        var ctx = new PyMCU.Common.CompilationContext(new CompilerOptions(
            FilePath: "main.py", OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        foreach (var (name, _) in modules) ctx.ProjectModules.Add(name);

        return new IRGenerator().Generate(mainAst, imported, new DeviceConfig { Arch = "avr" },
                                          projectModules: ctx.ProjectModules);
    }

    private static Val StoredAt(ProgramIR ir, long index) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == index)
            .Select(s => s.Src).Last();

    private static string StoredName(ProgramIR ir, long index) =>
        StoredAt(ir, index) switch
        {
            Variable v => v.Name,
            Temporary t => t.Name,
            var other => other.ToString() ?? "",
        };

    private const string Buf = "buf = bytearray([0, 0, 0, 0])\n";

    [Fact]
    public void AnInheritedMethod_ResolvesOnTheSubclassInstance()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self.x = 1\n" +
            "    def get(self) -> int:\n" +
            "        return self.x\n" +
            "class B(A):\n" +
            "    pass\n" +
            "b = B()\n" +
            "buf[0] = b.get()\n");
        Assert.False(StoredAt(ir, 0) is NoneVal);
    }

    [Fact]
    public void AnInheritedPropertyGetter_ResolvesOnTheSubclassInstance()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self.x = 1\n" +
            "    @property\n" +
            "    def v(self) -> int:\n" +
            "        return self.x\n" +
            "class B(A):\n" +
            "    pass\n" +
            "b = B()\n" +
            "buf[0] = b.v\n");
        Assert.False(StoredAt(ir, 0) is NoneVal);
    }

    [Fact]
    public void AnInheritedPropertySetter_ResolvesOnTheSubclassInstance()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self.x = 0\n" +
            "    @property\n" +
            "    def v(self) -> int:\n" +
            "        return self.x\n" +
            "    @v.setter\n" +
            "    def v(self, n):\n" +
            "        self.x = n\n" +
            "class B(A):\n" +
            "    pass\n" +
            "b = B()\n" +
            "b.v = 3\n" +
            "buf[0] = b.v\n");
        Assert.False(StoredAt(ir, 0) is NoneVal);
    }

    [Fact]
    public void AThreeLevelChain_ResolvesTheGrandparentMethod()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self.x = 1\n" +
            "    def get(self) -> int:\n" +
            "        return self.x\n" +
            "class B(A):\n" +
            "    pass\n" +
            "class C(B):\n" +
            "    pass\n" +
            "c = C()\n" +
            "buf[0] = c.get()\n");
        Assert.False(StoredAt(ir, 0) is NoneVal);
    }

    [Fact]
    public void IsinstanceOnASubclassInstance_FoldsAgainstTheBase()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    pass\n" +
            "class B(A):\n" +
            "    pass\n" +
            "b = B()\n" +
            "if isinstance(b, A):\n" +
            "    buf[0] = 1\n");
        Assert.Equal(new Constant(1), StoredAt(ir, 0));
    }

    [Fact]
    public void IsinstanceAgainstATupleContainingTheBase_Folds()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    pass\n" +
            "class B(A):\n" +
            "    pass\n" +
            "class D:\n" +
            "    pass\n" +
            "b = B()\n" +
            "if isinstance(b, (D, A)):\n" +
            "    buf[0] = 1\n");
        Assert.Equal(new Constant(1), StoredAt(ir, 0));
    }

    [Fact]
    public void TwoSiblingSubclasses_KeepSeparateFieldStorage()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self.x = 0\n" +
            "    def set(self, n):\n" +
            "        self.x = n\n" +
            "class B(A):\n" +
            "    pass\n" +
            "class C(A):\n" +
            "    pass\n" +
            "b = B()\n" +
            "c = C()\n" +
            "b.set(3)\n" +
            "c.set(9)\n" +
            "buf[0] = b.x\n" +
            "buf[1] = c.x\n");
        Assert.Equal("b_x", StoredName(ir, 0));
        Assert.Equal("c_x", StoredName(ir, 1));
    }

    [Fact]
    public void ABaseClassAttribute_ReadsThroughTheSubclassInstance()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    v = 1\n" +
            "class B(A):\n" +
            "    pass\n" +
            "b = B()\n" +
            "buf[0] = b.v\n");
        Assert.Equal("A_v", StoredName(ir, 0));
    }

    [Fact]
    public void AMemberArrayStore_InAMethod_WritesTheField()
    {
        var ir = Gen(
            "class C:\n" +
            "    def __init__(self):\n" +
            "        self.a: uint8[4] = [0] * 4\n" +
            "    def set(self, i, v):\n" +
            "        self.a[i] = v\n" +
            "c = C()\n" +
            "c.set(1, 7)\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void AMemberArrayStore_OnAnInheritedField_Writes()
    {
        var ir = Gen(
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self.a: uint8[4] = [0] * 4\n" +
            "class B(A):\n" +
            "    def set(self, i, v):\n" +
            "        self.a[i] = v\n" +
            "b = B()\n" +
            "b.set(1, 9)\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void ABaseMethodReadingASubclassOnlyField_Resolves()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    def get(self) -> int:\n" +
            "        return self.extra\n" +
            "class B(A):\n" +
            "    def __init__(self):\n" +
            "        self.extra = 5\n" +
            "b = B()\n" +
            "buf[0] = b.get()\n");
        Assert.False(StoredAt(ir, 0) is NoneVal);
    }

    [Fact]
    public void SuperInitWithKeywordArguments_BindsBaseParameters()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    def __init__(self, lo=1, hi=9):\n" +
            "        self.lo = lo\n" +
            "        self.hi = hi\n" +
            "class B(A):\n" +
            "    def __init__(self, n):\n" +
            "        super().__init__(lo=n, hi=8)\n" +
            "b = B(4)\n" +
            "buf[0] = b.lo\n");
        Assert.Equal(new Constant(4), StoredAt(ir, 0));
    }

    [Fact]
    public void APropertyOverrideInTheSubclass_Wins()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self.x = 1\n" +
            "    @property\n" +
            "    def v(self) -> int:\n" +
            "        return self.x\n" +
            "class B(A):\n" +
            "    @property\n" +
            "    def v(self) -> int:\n" +
            "        return self.x + 10\n" +
            "b = B()\n" +
            "buf[0] = b.v\n");
        Assert.False(StoredAt(ir, 0) is NoneVal);
    }

    [Fact]
    public void AnInheritedMethodCallingAnInheritedMethod_Resolves()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self.x = 2\n" +
            "    def inner(self) -> int:\n" +
            "        return self.x\n" +
            "    def outer(self) -> int:\n" +
            "        return self.inner() + 1\n" +
            "class B(A):\n" +
            "    pass\n" +
            "b = B()\n" +
            "buf[0] = b.outer()\n");
        Assert.False(StoredAt(ir, 0) is NoneVal);
    }

    [Fact]
    public void AClassAttributeOverrideInTheSubclass_Wins()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    v = 1\n" +
            "class B(A):\n" +
            "    v = 9\n" +
            "b = B()\n" +
            "buf[0] = b.v\n");
        Assert.Equal("B_v", StoredName(ir, 0));
    }

    [Fact]
    public void AMethodCallThroughAListElement_Resolves()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self.x = 1\n" +
            "    def get(self) -> int:\n" +
            "        return self.x\n" +
            "class B(A):\n" +
            "    pass\n" +
            "bs = [B(), B()]\n" +
            "buf[0] = bs[0].get()\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void AnInheritedPropertyAcrossAModuleBoundary_Resolves()
    {
        var ir = GenWithModules(Buf +
            "import adafruit_motor.servo as servo\n" +
            "s = servo.Servo()\n" +
            "s.angle = 90\n" +
            "buf[0] = s.fraction\n",
            ("adafruit_motor", ""),
            ("adafruit_motor.servo",
                "class _BaseServo:\n" +
                "    def __init__(self):\n" +
                "        self.duty = 0\n" +
                "    @property\n" +
                "    def fraction(self) -> int:\n" +
                "        return self.duty\n" +
                "    @fraction.setter\n" +
                "    def fraction(self, v):\n" +
                "        self.duty = v\n" +
                "class Servo(_BaseServo):\n" +
                "    @property\n" +
                "    def angle(self) -> int:\n" +
                "        return self.fraction\n" +
                "    @angle.setter\n" +
                "    def angle(self, n):\n" +
                "        self.fraction = n\n"));
        Assert.False(StoredAt(ir, 0) is NoneVal);
    }

    [Fact]
    public void ASuperMethodCallInsideAPropertyBody_Resolves()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self.x = 1\n" +
            "    def base(self) -> int:\n" +
            "        return self.x\n" +
            "class B(A):\n" +
            "    @property\n" +
            "    def v(self) -> int:\n" +
            "        return super().base()\n" +
            "b = B()\n" +
            "buf[0] = b.v\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void MultipleInheritance_IsRefusedNamingTheConstruct()
    {
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(
            "class A:\n" +
            "    pass\n" +
            "class B:\n" +
            "    pass\n" +
            "class C(A, B):\n" +
            "    pass\n"));
        Assert.Contains("multiple inheritance", ex.Message);
    }

    [Fact(Skip = "#471")]
    public void ABaseClassAttribute_ReadsThroughTheSubclassName()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    v = 1\n" +
            "class B(A):\n" +
            "    pass\n" +
            "buf[0] = B.v\n");
        Assert.Equal("A_v", StoredName(ir, 0));
    }

    [Fact]
    public void AVariableReboundOverItsModuleAlias_ReadsAsTheInstanceOnFieldRead()
    {
        var ir = GenWithModules(Buf +
            "import m.thing as thing\n" +
            "thing = thing.Thing()\n" +
            "thing.x = 1\n" +
            "buf[0] = thing.x\n",
            ("m", ""),
            ("m.thing",
                "class Thing:\n" +
                "    def __init__(self):\n" +
                "        self.x = 0\n" +
                "    def get(self) -> int:\n" +
                "        return self.x\n"));
        Assert.False(StoredAt(ir, 0) is NoneVal);
    }

    [Fact]
    public void AVariableReboundOverItsModuleAlias_ReadsAsTheInstanceOnMethodCall()
    {
        var ir = GenWithModules(Buf +
            "import m.thing as thing\n" +
            "thing = thing.Thing()\n" +
            "thing.x = 4\n" +
            "buf[0] = thing.get()\n",
            ("m", ""),
            ("m.thing",
                "class Thing:\n" +
                "    def __init__(self):\n" +
                "        self.x = 0\n" +
                "    def get(self) -> int:\n" +
                "        return self.x\n"));
        Assert.False(StoredAt(ir, 0) is NoneVal);
    }

    [Fact]
    public void AVariableReboundOverItsModuleAlias_ReadsAnInheritedProperty()
    {
        var ir = GenWithModules(Buf +
            "import adafruit_motor.servo as servo\n" +
            "servo = servo.Servo()\n" +
            "servo.angle = 90\n" +
            "buf[0] = servo.fraction\n",
            ("adafruit_motor", ""),
            ("adafruit_motor.servo",
                "class _BaseServo:\n" +
                "    def __init__(self):\n" +
                "        self.duty = 0\n" +
                "    @property\n" +
                "    def fraction(self) -> int:\n" +
                "        return self.duty\n" +
                "    @fraction.setter\n" +
                "    def fraction(self, v):\n" +
                "        self.duty = v\n" +
                "class Servo(_BaseServo):\n" +
                "    @property\n" +
                "    def angle(self) -> int:\n" +
                "        return self.fraction\n" +
                "    @angle.setter\n" +
                "    def angle(self, n):\n" +
                "        self.fraction = n\n"));
        Assert.False(StoredAt(ir, 0) is NoneVal);
    }

    [Fact(Skip = "#477")]
    public void ASuperPropertyRead_ResolvesTheBaseGetter()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self.x = 1\n" +
            "    @property\n" +
            "    def v(self) -> int:\n" +
            "        return self.x\n" +
            "class B(A):\n" +
            "    @property\n" +
            "    def v(self) -> int:\n" +
            "        return super().v + 1\n" +
            "b = B()\n" +
            "buf[0] = b.v\n");
        Assert.False(StoredAt(ir, 0) is NoneVal);
    }

    [Fact(Skip = "#478")]
    public void AnUnannotatedInheritedMethod_KeepsItsReturnValueAtTheCallSite()
    {
        var ir = Gen(Buf +
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self.x = 1\n" +
            "    def get(self):\n" +
            "        return self.x\n" +
            "class B(A):\n" +
            "    pass\n" +
            "b = B()\n" +
            "buf[0] = b.get()\n");
        Assert.False(StoredAt(ir, 0) is NoneVal);
    }
}

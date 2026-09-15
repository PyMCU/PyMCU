using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#360. A class attribute whose class defines `__get__` is a descriptor: CPython rewrites
/// `inst.attr` into `type(inst).attr.__get__(inst, type(inst))` and `inst.attr = v` into
/// `__set__`. PyMCU does the lookup but never the rewrite, so the only spelling that compiles
/// is `Dev.reg.__get__(d, Dev)`, which nobody writes.
///
/// The explicit spelling already works because `Dev.reg` resolves to an ordinary module global
/// that instanceClasses knows and `__get__` dispatches like any other method. These assertions
/// are that the implicit spelling reaches the same place: the value that leaves the program is
/// the descriptor's answer, not the descriptor object and not a refusal.
///
/// A class attribute whose class defines neither dunder keeps its #268 meaning, which is the
/// last test here: the two features must not be one feature.
///
/// adafruit_register is seven descriptor classes and nothing else, so this is what decides
/// whether adafruit_ina219 and adafruit_veml7700 can be compiled without editing them.
/// </summary>
public class DescriptorProtocolTests
{
    // Optimized, because the assertion is on the VALUE the descriptor answers with. The
    // unoptimized IR carries the call's result temporary, which says the rewrite happened and
    // nothing about whether it read the right register.
    private static ProgramIR Gen(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return Optimizer.Optimize(ir);
    }

    private static Val LastStored(ProgramIR ir, int slot) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    private const string Program =
        "buf = bytearray([0, 0, 0, 0])\n" +
        "class Field:\n" +
        "    def __init__(self, addr: uint8) -> None:\n" +
        "        self.addr = addr\n" +
        "    def __get__(self, obj, objtype=None) -> uint8:\n" +
        "        return self.addr + obj.base\n" +
        "    def __set__(self, obj, value: uint8) -> None:\n" +
        "        obj.base = value\n" +
        "class Dev:\n" +
        "    reg = Field(9)\n" +
        "    def __init__(self, base: uint8) -> None:\n" +
        "        self.base = base\n" +
        "d = Dev(2)\n";

    // `d.reg` must be the descriptor's answer, 9 + 2.
    [Fact]
    public void ReadingADescriptorAttribute_CallsGet()
    {
        var ir = Gen(Program + "buf[0] = d.reg\n");

        Assert.Equal(new Constant(11), LastStored(ir, 0));
    }

    // `d.reg = 5` must reach __set__, which writes through to obj.base.
    [Fact]
    public void WritingADescriptorAttribute_CallsSet()
    {
        var ir = Gen(Program +
            "d.reg = 5\n" +
            "buf[1] = d.base\n");

        Assert.Equal(new Constant(5), LastStored(ir, 1));
    }

    // The explicit spelling works today. The control is that it keeps working AND that the two
    // spellings store the same thing: a rewrite measured against itself proves nothing.
    [Fact]
    public void TheExplicitSpelling_StillCompilesAndAgreesWithTheImplicitOne()
    {
        var explicitly = Gen(Program + "buf[0] = Dev.reg.__get__(d, Dev)\n");
        var implicitly = Gen(Program + "buf[0] = d.reg\n");

        Assert.Equal(LastStored(explicitly, 0).ToString(), LastStored(implicitly, 0).ToString());
    }

    // A class attribute that is an instance of a class WITHOUT __get__ keeps its #268 meaning:
    // it is the object, and a method call on it reaches that object's method.
    [Fact]
    public void AClassAttributeWithoutGet_IsStillTheObject()
    {
        // #268 is the prerequisite: this one is refused today for the same reason, and must
        // come back as the object rather than as the descriptor protocol.
        var ir = Gen(
            "buf = bytearray([0, 0, 0, 0])\n" +
            "class Plain:\n" +
            "    def __init__(self, addr: uint8) -> None:\n" +
            "        self.addr = addr\n" +
            "    def read(self) -> uint8:\n" +
            "        return self.addr + 1\n" +
            "class Dev:\n" +
            "    reg = Plain(9)\n" +
            "    def __init__(self, base: uint8) -> None:\n" +
            "        self.base = base\n" +
            "d = Dev(2)\n" +
            "buf[0] = d.reg.read()\n");

        // The method ran: what is stored is its result, not a refusal and not the descriptor
        // protocol's answer. Whether 9 + 1 is folded here is the optimizer's business, so the
        // assertion is on the dispatch and not on the arithmetic.
        Assert.True(LastStored(ir, 0) is Temporary or Variable or Constant { Value: 10 },
            $"stored {LastStored(ir, 0)}; d.reg.read() must reach Plain.read()");
    }
}

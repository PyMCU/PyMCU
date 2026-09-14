using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A single-field instance mutated inside a method's loop, and the method's value: three
/// seams of the zero-cost class machinery met on one program.
///
///  - The parser files an unannotated def as "void" and return-type inference skips class
///    methods, so `return self.value` had no result temporary to land in: the caller read
///    None, and printed whatever the register held.
///  - A single-field instance IS its field, so the constant tracked for the field lived under
///    the instance's own name, which the loop-body invalidation never dropped: every read
///    after `self.value = self.value + 1` folded to the constructor's value.
///  - `.value` is also the register / pointer read, and that path ran first: once the instance
///    had an SRAM slot the writes went to the slot and the reads still came from the scalar.
/// </summary>
public class ZcaMethodReturnAfterLoopTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Main(string src) => Gen(src).Functions.Single(f => f.Name == "main").Body;

    private const string Fader =
        "class Fader:\n" +
        "    def __init__(self):\n" +
        "        self.value = 3\n" +
        "\n" +
        "    def up(self, n):\n" +
        "        for i in range(n):\n" +
        "            self.value = self.value + i\n" +
        "        return self.value\n" +
        "\n" +
        "f = Fader()\n" +
        "y = f.up(10)\n";

    [Fact]
    public void AnUnannotatedMethod_HandsItsValueBack()
    {
        var body = Main(Fader);
        var stores = body.OfType<Copy>().Where(c => c.Dst is Variable { Name: "y" }).ToList();
        Assert.NotEmpty(stores);
        Assert.DoesNotContain(stores, c => c.Src is NoneVal);
    }

    [Fact]
    public void AFieldWrittenInTheLoop_IsNotFoldedToTheConstructorValue()
    {
        var body = Main(Fader);
        var stores = body.OfType<Copy>().Where(c => c.Dst is Variable { Name: "y" }).ToList();
        Assert.NotEmpty(stores);
        Assert.DoesNotContain(stores, c => c.Src is Constant { Value: 3 });
    }

    [Fact]
    public void AConstantReturnAfterAWhile_ReachesTheCaller()
    {
        var body = Main(
            "class A:\n" +
            "    def __init__(self):\n" +
            "        self.value = 3\n" +
            "\n" +
            "    def m(self):\n" +
            "        k = 0\n" +
            "        while k < 2:\n" +
            "            self.value = self.value + 1\n" +
            "            k = k + 1\n" +
            "        return 7\n" +
            "\n" +
            "a = A()\n" +
            "y = a.m()\n");
        var stores = body.OfType<Copy>().Where(c => c.Dst is Variable { Name: "y" }).ToList();
        Assert.NotEmpty(stores);
        Assert.DoesNotContain(stores, c => c.Src is NoneVal);
        Assert.Contains(stores, c => c.Src is Constant { Value: 7 } || c.Src is Temporary);
    }
}

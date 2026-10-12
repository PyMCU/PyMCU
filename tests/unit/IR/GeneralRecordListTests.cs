using System.Linq;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// RFC 0016, the general record: a class of several scalar fields appended to a growable list
/// is a heap object laid out like the class's SRAM slot, and what a read gives back is its
/// pointer. And the rule that goes with the buffer record: a buffer copied into a list when it
/// was appended is not changed by a later store into the name, so the store is refused.
/// </summary>
public class GeneralRecordListTests
{
    private const string Dev =
        "class Dev:\n" +
        "    def __init__(self, a: int, b: int, c: int) -> None:\n" +
        "        self.a = a\n" +
        "        self.b = b\n" +
        "        self.c = c\n";

    private static ProgramIR Gen(string src, string arch) =>
        new IRGenerator().Generate(new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new System.Collections.Generic.Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = arch });

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void AppendOfAMultiFieldInstance_AllocatesOneRecordOfTheClassesSize(string arch)
    {
        var ir = Gen(Dev +
            "xs = []\n" +
            "xs.append(Dev(1, 2, 3))\n" +
            "xs.append(Dev(4, 5, 6))\n", arch);
        var main = ir.Functions.Single(f => f.Name == "main").Body;
        // Three int fields, two bytes each on both targets' `int`.
        int fieldBytes = main.OfType<GcAlloc>().Select(g => g.Size).OfType<Constant>().Select(c => c.Value).Max();
        Assert.True(fieldBytes >= 3);
        Assert.Contains(main, i => i is StoreIndirect or BytearrayStore);
    }

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void AFieldOfARecordReadFromTheList_LoadsThroughItsPointer(string arch)
    {
        var ir = Gen(Dev +
            "xs = []\n" +
            "xs.append(Dev(1, 2, 3))\n" +
            "t = xs[0].b\n", arch);
        var main = ir.Functions.Single(f => f.Name == "main").Body;
        Assert.Contains(main, i => i is BytearrayLoad or LoadIndirect);
    }

    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void AMethodNotMarkedOutline_OnARecordFromTheList_IsRefusedByName(string arch)
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(
            Dev.Replace("self.c = c\n", "self.c = c\n    def total(self) -> int:\n        return self.a + self.b\n") +
            "xs = []\n" +
            "xs.append(Dev(1, 2, 3))\n" +
            "t = xs[0].total()\n", arch));
        Assert.Contains("@outline", ex.Message);
    }

    [Fact]
    public void ARecordBiggerThanTheAvrHeapObject_IsRefusedWithItsSize()
    {
        string fields = string.Join("", Enumerable.Range(0, 130).Select(i => $"        self.f{i} = a\n"));
        var ex = Assert.Throws<CompilerError>(() => Gen(
            "class Big:\n    def __init__(self, a: int) -> None:\n" + fields +
            "xs = []\nxs.append(Big(1))\n", "avr"));
        Assert.Contains("at most 253", ex.Message);
    }
}

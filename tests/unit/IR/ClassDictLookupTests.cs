using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A class-level dict is a compile-time lookup table. Adafruit VEML7700 writes
/// <c>gain_values = {ALS_GAIN_2: 2, ALS_GAIN_1: 1, ALS_GAIN_1_4: 0.25, ...}</c>
/// on the class and reads <c>self.gain_values[gain]</c>. The class body never
/// registered that dict, so the subscript fell through to a register bit index:
/// "Bit index must be constant for reading".
/// </summary>
public class ClassDictLookupTests
{
    private static ProgramIR Gen(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return Optimizer.Optimize(ir);
    }

    private static ProgramIR GenImported(string pack, string sensor, string main)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["sensor"] = new Parser(new Lexer(sensor).Tokenize()).ParseProgram(),
        };
        if (pack.Length > 0)
            imported["pack"] = new Parser(new Lexer(pack).Tokenize()).ParseProgram();
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string>(imported.Keys));
        return Optimizer.Optimize(ir);
    }

    private static Val LastStored(ProgramIR ir, int slot) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    [Fact]
    public void AClassLevelDict_IsIndexedThroughSelf()
    {
        var ir = Gen(
            "buf = bytearray([0])\n" +
            "class Dev:\n" +
            "    T = {0: 10, 1: 20, 2: 30}\n" +
            "    def at(self, n: uint8) -> uint8:\n" +
            "        return self.T[n]\n" +
            "d = Dev()\n" +
            "buf[0] = d.at(1)\n");

        LastStored(ir, 0).Should().Be(new Constant(20),
            because: "self.T is the class dict, so T[1] is 20, not a register bit");
    }

    [Fact]
    public void AClassLevelDictWithClassConstKeys_FoldsThroughAnImportedMethod()
    {
        const string sensor =
            "class Dev:\n" +
            "    ALS_GAIN_1 = 0\n" +
            "    ALS_GAIN_2 = 1\n" +
            "    ALS_GAIN_X = 2\n" +
            "    vals = {ALS_GAIN_2: 2, ALS_GAIN_1: 1, ALS_GAIN_X: 0.25}\n" +
            "    def scaled(self, n: uint8) -> uint8:\n" +
            "        return uint8(self.vals[n] * 100)\n";
        var ir = GenImported("", sensor,
            "from sensor import Dev\n" +
            "buf = bytearray([0, 0])\n" +
            "d = Dev()\n" +
            "buf[0] = d.scaled(1)\n" +
            "buf[1] = d.scaled(2)\n");

        LastStored(ir, 0).Should().Be(new Constant(200),
            because: "vals[ALS_GAIN_2] is 2, times 100");
        LastStored(ir, 1).Should().Be(new Constant(25),
            because: "vals[ALS_GAIN_X] is 0.25, times 100 is 25 -- a truncated int dict would store 0");
    }

    [Fact]
    public void ARuntimeKeyIntoAClassLevelDict_DoesNotAskForABitIndex()
    {
        var act = () => Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "buf = bytearray([0])\n" +
            "class Dev:\n" +
            "    T = {0: 10, 1: 20, 2: 30}\n" +
            "    def at(self, n: uint8) -> uint8:\n" +
            "        return self.T[n]\n" +
            "d = Dev()\n" +
            "buf[0] = d.at(GPIOR0.value)\n");

        act.Should().NotThrow<PyMCU.Common.CompilerError>(
            because: "a run-time key into a class dict is a compare chain, not a bit index");
    }
}

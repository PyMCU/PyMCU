using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// A field read on the instance a factory returned, never bound to a name, issue #526.
//
//     def make(n: uint8) -> Src:
//         return Src(n + GPIOR0.value)
//     bound = make(2)
//     print(bound.base)      # 2
//     print(make(2).base)    # 0
//
// A single-field class returned by an outlined factory is a handle: the call returns the
// field itself (RFC 0001 Model B). HandleFieldRead knew that, but it is asked only when the
// receiver is a NAME, so the temporary flattened to `tmp_N_base`, which nothing writes.
//
// WHAT DISCRIMINATES: no instruction reads a `tmp_*_base` name. Against the unfixed compiler
// the unbound read is exactly such a name.
//
// WHAT IS INVARIANT: the bound spelling, which always read the call's result.
//
// The values are checked in the emulator against CPython with pymcu-avr's oracle harness.
public class FactoryTemporaryFieldTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Factory =
        "from pymcu.types import uint16\n" +
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n\n\n" +
        "class Src:\n" +
        "    def __init__(self, base: uint16):\n" +
        "        self.base: uint16 = base\n\n\n" +
        "def make(n: uint16) -> Src:\n" +
        "    return Src(n + GPIOR0.value)\n\n\n";

    private static IEnumerable<string> ReadNames(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Select(c => c.Src).OfType<Variable>().Select(v => v.Name);

    private static IEnumerable<Call> CallsTo(ProgramIR ir, string fn) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Call>().Where(c => c.FunctionName == fn);

    [Fact]
    public void AnUnboundFactoryResultReadsTheCallResult()
    {
        var ir = Gen(Factory + "GPIOR1.value = make(300).base\n");
        Assert.DoesNotContain(ReadNames(ir),
            n => n.StartsWith("tmp_", StringComparison.Ordinal) && n.EndsWith("_base", StringComparison.Ordinal));
        Assert.Contains(CallsTo(ir, "make"), c => c.Dst is not NoneVal);
    }

    [Fact]
    public void TheResultIsTheFieldsWidth()
    {
        // A uint16 field over 255 is what tells a moved low byte from the value.
        var ir = Gen(Factory + "GPIOR1.value = make(300).base\n");
        var call = Assert.Single(CallsTo(ir, "make"));
        var dst = call.Dst switch { Temporary t => t.Type, Variable v => v.Type, _ => DataType.UNKNOWN };
        Assert.Equal(DataType.UINT16, dst);
    }

    [Fact]
    public void TheBoundSpellingStillReadsTheCallResult()
    {
        var ir = Gen(Factory + "bound = make(300)\nGPIOR1.value = bound.base\n");
        Assert.DoesNotContain(ReadNames(ir), n => n.EndsWith("_base", StringComparison.Ordinal));
    }
}

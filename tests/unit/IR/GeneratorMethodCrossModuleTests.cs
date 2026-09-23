using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A generator METHOD consumed across a module boundary. The transform runs once per
/// module: the imported module lowers `Decoder.read` to its machine class and records
/// `Decoder.read -&gt; gen_Decoder_read` in a shared catalog; the entry module's
/// `for v in d.read(5)` resolves `d`'s class from that catalog and desugars to
/// `gen_Decoder_read(d, 5)`.
///
/// Measured: with each module's catalog private to its own pass, the entry walk could
/// not name `d`'s class -- `Decoder` is not declared in the entry file -- so the member
/// call survived to IR, resolved the bare machine name, and died as "missing required
/// argument 'n' in call to constructor of 'read'", the receiver never injected.
/// This is the adafruit_irremote shape: NonblockingGenericDecode.read is a generator
/// method imported from the library and driven by test code.
/// </summary>
public class GeneratorMethodCrossModuleTests
{
    private static ProgramIR Gen(string entrySrc, params (string Name, string Source)[] modules)
    {
        var imported = new Dictionary<string, ProgramNode>();
        foreach (var (name, source) in modules)
            imported[name] = new Parser(new Lexer(source).Tokenize()).ParseProgram();

        return new IRGenerator().Generate(
            new Parser(new Lexer(entrySrc).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" });
    }

    private const string Lib =
        "from pymcu.types import uint8\n" +
        "class Decoder:\n" +
        "    def read(self, n: uint8) -> uint8:\n" +
        "        yield n\n";

    [Fact]
    public void AForLoopOverAnImportedGeneratorMethod_ConstructsTheMachine()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "from gen import Decoder\n" +
            "d = Decoder()\n" +
            "for v in d.read(5):\n" +
            "    pass\n",
            ("gen", Lib));

        // The machine construction reached the imported class's __init__: the receiver
        // was injected, the prefixed machine name resolved, and the poll loop drives it.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body),
            i => i is InlineExpansionMarker m
                 && m.FuncName == "@inl:gen_Decoder_read___init__");
    }

    [Fact]
    public void AnAssignedMachineFromAnImportedMethod_IteratesTheSameInstance()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "from gen import Decoder\n" +
            "d = Decoder()\n" +
            "g = d.read(5)\n" +
            "for v in g:\n" +
            "    pass\n",
            ("gen", Lib));

        Assert.Contains(ir.Functions.SelectMany(f => f.Body),
            i => i is InlineExpansionMarker m
                 && m.FuncName == "@inl:gen_Decoder_read___init__");
    }
}

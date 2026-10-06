using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `class F(ValueError, OSError)`: every base contributes an IS-A path, so `except OSError`
/// must catch an `F` raised anywhere in the program. The base scan used to stop at the
/// first base that resolved at all -- ValueError -- which records no OSError-subtree edge,
/// so the class got no parent and `except OSError` emitted a single compare that `F`'s
/// code never satisfies: the raise fell through to the unhandled path.
/// </summary>
public class ExceptionMultiBaseTests
{
    private const string Prelude =
        "from pymcu.types import uint8\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Fn(ProgramIR ir, string name) =>
        ir.Functions.Single(f => f.Name == name).Body;

    private static int ErrorCodeCompares(List<Instruction> fn) =>
        fn.Count(i => i is Binary { Op: PyMCU.IR.BinaryOp.Equal, Src1: Variable { Name: var n } }
                      && n.StartsWith("__exn_", StringComparison.Ordinal));

    [Fact]
    public void ASecondBaseInsideTheOSErrorSubtreeStillCatches()
    {
        // The first resolvable base (ValueError) is outside the subtree; the second
        // (OSError) carries the edge. `except OSError` must compare against both the
        // OSError code and F's own code -- a single compare only matches OSError
        // exactly and lets `raise F(5)` escape.
        var main = Fn(Gen(
            "class F(ValueError, OSError):\n" +
            "    pass\n" +
            "try:\n" +
            "    raise F(5)\n" +
            "except OSError:\n" +
            "    print(\"caught\")\n"), "main");

        ErrorCodeCompares(main).Should().BeGreaterOrEqualTo(2,
            because: "the handler accepts OSError itself and the raised subclass F");
    }

    [Fact]
    public void AFirstBaseInsideTheSubtreeStillCatches()
    {
        // Order flipped: the OSError-subtree base comes first and must record the edge
        // exactly as before -- a later non-subtree base adds nothing but must not cost it.
        var main = Fn(Gen(
            "class F(OSError, ValueError):\n" +
            "    pass\n" +
            "try:\n" +
            "    raise F(5)\n" +
            "except OSError:\n" +
            "    print(\"caught\")\n"), "main");

        ErrorCodeCompares(main).Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public void ASubtleTreeBaseThroughAnotherUserClassCatches()
    {
        // `class G(F)` where F already carries an OSError edge: the scan looks at the
        // parent MAP, so a base whose own edge was just recorded must chain through.
        var main = Fn(Gen(
            "class F(OSError):\n" +
            "    pass\n" +
            "class G(F, ValueError):\n" +
            "    pass\n" +
            "try:\n" +
            "    raise G(5)\n" +
            "except OSError:\n" +
            "    print(\"caught\")\n"), "main");

        ErrorCodeCompares(main).Should().BeGreaterOrEqualTo(2,
            because: "G descends from F, which descends from OSError");
    }
}

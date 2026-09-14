using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#346. `except (ImportError, NotImplementedError):` was refused with advice to write one
/// clause per type. The compiler now follows that advice itself: the alternatives are carried
/// as one comma-joined string and the dispatcher compares the error code against each, all of
/// them reaching the one body.
///
/// Which exception is actually caught is asserted in avr8sharp by the AVR fixture; these own
/// the shapes, the refusals, and the promise that a single type costs what it always did.
/// </summary>
public class ExceptTupleTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Boom =
        "def boom():\n" +
        "    raise ValueError(\"x\")\n\n";

    [Fact]
    public void ATupleOfTypes_Compiles()
    {
        Assert.NotNull(Gen(Boom +
            "def main():\n" +
            "    try:\n" +
            "        boom()\n" +
            "    except (ValueError, TypeError):\n" +
            "        pass\n"));
    }

    [Fact]
    public void ThreeAlternatives_Compile()
    {
        Assert.NotNull(Gen(Boom +
            "def main():\n" +
            "    try:\n" +
            "        boom()\n" +
            "    except (ValueError, TypeError, IndexError):\n" +
            "        pass\n"));
    }

    [Fact]
    public void ATupleAndThenAnotherClause_Compile()
    {
        Assert.NotNull(Gen(Boom +
            "def main():\n" +
            "    try:\n" +
            "        boom()\n" +
            "    except (ValueError, TypeError):\n" +
            "        pass\n" +
            "    except IndexError:\n" +
            "        pass\n"));
    }

    [Fact]
    public void TheOptionalImportFallbackEveryDriverOpensWith_Compiles()
    {
        // The exact four lines that stop adafruit_dht, adafruit_hcsr04 and adafruit_motor.
        Assert.NotNull(Gen(
            "def main():\n" +
            "    try:\n" +
            "        boom()\n" +
            "    except (ImportError, NotImplementedError):\n" +
            "        pass\n\n" +
            "def boom():\n" +
            "    raise ValueError(\"x\")\n"));
    }

    [Fact]
    public void ASingleTypeStillEmitsOneComparisonAndOneSkip()
    {
        // A tuple of one is the same program as a bare name and must not cost more than one,
        // which is also what keeps every existing fixture byte-identical.
        var bare = Gen(Boom +
            "def main():\n" +
            "    try:\n" +
            "        boom()\n" +
            "    except ValueError:\n" +
            "        pass\n");
        var parenthesised = Gen(Boom +
            "def main():\n" +
            "    try:\n" +
            "        boom()\n" +
            "    except (ValueError):\n" +
            "        pass\n");
        Assert.Equal(CountJumps(bare), CountJumps(parenthesised));
    }

    private static int CountJumps(ProgramIR ir)
    {
        int n = 0;
        foreach (var f in ir.Functions)
            foreach (var i in f.Body)
                if (i is Jump or JumpIfZero or JumpIfNotZero) n++;
        return n;
    }

    [Fact]
    public void AnEmptyTuple_SaysNothingCanReachTheHandler()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Boom +
            "def main():\n" +
            "    try:\n" +
            "        boom()\n" +
            "    except ():\n" +
            "        pass\n"));
        Assert.Contains("names no exception type", ex.Message);
    }
}

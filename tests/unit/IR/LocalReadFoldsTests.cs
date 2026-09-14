using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#331. A READ of a function-local the compiler tracks folds to what it holds.
///
/// `localConstantValues` has recorded that since #327 and was only ever asked by a CALL, so a
/// callee dispatching on a value still saw it when the caller put it in a local first. Every
/// ordinary read went to run time, and that is what an @inline HAL computing at full width
/// pays: the exact-Timer1 helpers have to be written through 32-bit locals, because unfolded
/// the 16-bit expressions truncate, and the same arithmetic then cost 148 bytes where the
/// expression form cost 78.
///
/// Measured over the 322-fixture corpus: 84 smaller, none bigger, 6 894 bytes saved.
///
/// The two things this file pins are the ones that made the fold WRONG before they were fixed,
/// because both are silent: a loop whose accumulator kept its starting value, and a dict miss
/// the program catches turning into a compile error.
/// </summary>
public class LocalReadFoldsTests
{
    private const string Hdr =
        "from pymcu.types import uint8, uint16\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void AChainOfLocalsFoldsToItsAnswer()
    {
        // The shape the HAL is written in: every intermediate is a wide local, on purpose,
        // because unfolded the expressions truncate. Folded, the whole chain is one number.
        var ir = Gen(Hdr +
            "def main():\n" +
            "    top: uint32 = 16000000 // (8 * 50) - 1\n" +
            "    half: uint32 = top // 2\n" +
            "    GPIOR0.value = uint8(half & 0xFF)\n");

        Assert.Empty(ir.Functions.SelectMany(f => f.Body).OfType<Binary>());
    }

    [Fact]
    public void AnAccumulatorInARunTimeLoopKeepsItsRunTimeValue()
    {
        // `for v in <list>: total = total + v`. The body is lowered ONCE and runs many times,
        // so `total` must not be folded from the 0 it holds on the way in. The range and while
        // paths always dropped it; the list and string loops did not, and the defect only
        // became reachable when reads of locals began to fold: the sum answered 0, silently.
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n\n" +
            "def main():\n" +
            "    x: list[uint8] = list()\n" +
            "    x.append(10)\n" +
            "    x.append(20)\n" +
            "    total: uint8 = 0\n" +
            "    for v in x:\n" +
            "        total = total + v\n" +
            "    GPIOR0.value = total\n");

        // The addition survives as an instruction: it is the loop's work, and folding it away
        // is the wrong answer rather than a smaller one.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Binary>(),
            b => b.Op == PyMCU.IR.BinaryOp.Add);
    }

    // The RUN-TIME STRING loop takes the same invalidation and has no unit test: reaching that
    // path needs an f-string built from a register, which needs the strfmt helpers the driver
    // injects and this single-source harness cannot. It is covered by the corpus, where the
    // list loop above is the same code shape and caught the defect.

    [Fact]
    public void ADictMissTheProgramCatchesIsRaised_NotRefused()
    {
        // `k = 7` folds now, so the compiler can see the key is missing. Seeing it is not a
        // reason to refuse a program that HANDLES it: getting better at reading a program must
        // not make a working program stop building. The raise goes where the handler can take
        // it, which is the same instruction the run-time key path emits for the same miss.
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n\n" +
            "SCALE = {1: 10, 2: 20}\n\n" +
            "def main():\n" +
            "    try:\n" +
            "        k: uint8 = 7\n" +
            "        bad: uint8 = SCALE[k]\n" +
            "        GPIOR0.value = bad\n" +
            "    except KeyError:\n" +
            "        GPIOR0.value = 255\n");

        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<SignalError>(),
            e => e.Code is Constant c && c.Value == 4);
    }

    [Fact]
    public void ADictMissWithNoHandlerIsStillRefusedAtCompileTime()
    {
        // The refusal is right when nothing catches it: the program cannot do anything with a
        // key that is not there, and saying so at compile time is the whole point of reading
        // the key.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "from pymcu.types import uint8\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n\n" +
            "SCALE = {1: 10, 2: 20}\n\n" +
            "def main():\n" +
            "    k: uint8 = 7\n" +
            "    GPIOR0.value = SCALE[k]\n"));
        Assert.Contains("KeyError", ex.Message);
    }

    [Fact]
    public void ALocalWrittenInABranchIsNotFoldedAfterIt()
    {
        // The arms disagree, so nothing is known at the join. This is the invalidation that
        // already existed and that the fold now depends on.
        var ir = Gen(Hdr +
            "def main():\n" +
            "    n: uint8 = 0\n" +
            "    if GPIOR0.value == 1:\n" +
            "        n = 5\n" +
            "    else:\n" +
            "        n = 9\n" +
            "    GPIOR0.value = n + 1\n");

        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Binary>(),
            b => b.Op == PyMCU.IR.BinaryOp.Add);
    }
}

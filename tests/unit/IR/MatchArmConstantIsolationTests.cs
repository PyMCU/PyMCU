using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Match arms are sibling run-time paths: a compile-time value recorded while lowering one
/// arm must not answer reads in the next arm, and the state after the match is only what
/// every reachable path agrees on. Before the arms were reconciled the way the if/elif
/// chain already was, each arm lowered against the previous arm's leftovers -- a local
/// written `= 1` in `case 1:` and `= 0` in `case 2:` read as 0 in the wildcard arm and
/// after the match, folding `led_on == 1` to false and cutting the arm that raised the
/// LED (uart-command's 'T' handler).
/// </summary>
public class MatchArmConstantIsolationTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<int> ReturnConstants(ProgramIR ir, string func) =>
        ir.Functions.Single(f => f.Name == func).Body.OfType<Return>()
            .Select(r => r.Value).OfType<Constant>().Select(c => c.Value).ToList();

    [Fact]
    public void ANameDisagreedOnAcrossArms_DoesNotFoldAfterTheMatch()
    {
        var ir = Gen(
            "def pick(cmd: uint8) -> uint8:\n" +
            "    led_on: uint8 = 0\n" +
            "    match cmd:\n" +
            "        case 1:\n" +
            "            led_on = 1\n" +
            "        case 2:\n" +
            "            led_on = 0\n" +
            "        case _:\n" +
            "            pass\n" +
            "    if led_on == 1:\n" +
            "        return 170\n" +
            "    return 187\n");

        // `led_on` leaves the match holding 1, 0, or the pre-match 0 depending on the path,
        // so the comparison stays a run-time one and BOTH outcomes are emitted.
        var returns = ReturnConstants(ir, "pick");
        Assert.Contains(170, returns);
        Assert.Contains(187, returns);

        // The comparison is still asked of the hardware: a jump on the name itself,
        // not a compile-time answer that skips it.
        Assert.Contains(ir.Functions.Single(f => f.Name == "pick").Body, op =>
            op is JumpIfEqual or JumpIfNotEqual
            && (op is JumpIfEqual je && je.Src1 is Variable jv && jv.Name.EndsWith("led_on")
                || op is JumpIfNotEqual jne && jne.Src1 is Variable nv && nv.Name.EndsWith("led_on")));
    }

    [Fact]
    public void ANameBoundOnlyInsideArms_DoesNotLeakIntoASiblingArm()
    {
        var ir = Gen(
            "def pick(cmd: uint8) -> uint8:\n" +
            "    match cmd:\n" +
            "        case 1:\n" +
            "            v: uint8 = 7\n" +
            "        case 2:\n" +
            "            v = 8\n" +
            "        case _:\n" +
            "            if v == 8:\n" +
            "                return 34\n" +
            "            return 51\n" +
            "    return 0\n");

        // On the wildcard path no earlier arm ran, so `v` was never written -- the value
        // case 2 recorded is a fiction of lowering order, and the comparison must stay
        // run-time: both of its outcomes are emitted.
        var returns = ReturnConstants(ir, "pick");
        Assert.Contains(34, returns);
        Assert.Contains(51, returns);
    }

    [Fact]
    public void ANameEveryArmAgreesOn_KeepsItsConstantAfterTheMatch()
    {
        var ir = Gen(
            "def pick(cmd: uint8) -> uint8:\n" +
            "    match cmd:\n" +
            "        case 1:\n" +
            "            tag = 9\n" +
            "        case _:\n" +
            "            tag = 9\n" +
            "    if tag == 9:\n" +
            "        return 77\n" +
            "    return 88\n");

        // Every path through the match writes the same value, so the join keeps it and the
        // comparison folds: the dead `return 88` is never emitted.
        var returns = ReturnConstants(ir, "pick");
        Assert.Contains(77, returns);
        Assert.DoesNotContain(88, returns);
    }
}

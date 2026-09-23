using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An instance carried through a `*args` call must still answer its field reads:
/// the element's fields live under the caller-side base (`a__deadline`), so the
/// pin is an ALIAS to the name the class hangs on, not a copy of the head.
/// </summary>
public class VariadicSequenceTests
{
    private static ProgramIR Gen(string src)
    {
        var config = new DeviceConfig { Arch = "avr", Chip = "atmega328p" };
        var program = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        new ConditionalCompilator(config).Process(program);
        return new IRGenerator().Generate(
            program, new Dictionary<string, ProgramNode>(), config);
    }

    [Fact]
    public void AnInstanceThroughStarArgs_AnswersItsFieldsInsideTheCallee()
    {
        var ir = Gen(
            "class Alarm:\n" +
            "    def __init__(self, deadline: uint8):\n" +
            "        self.deadline = deadline\n" +
            "@inline\n" +
            "def first_deadline(*alarms) -> uint8:\n" +
            "    return alarms[0].deadline\n" +
            "a = Alarm(7)\n" +
            "buf = bytearray(1)\n" +
            "buf[0] = first_deadline(a)\n");

        // a_deadline folds to 7, and the read inside the *args expansion emits a SECOND
        // copy of that 7: the element dispatched the field read to the caller's slot.
        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        var sevens = body.OfType<Copy>().Count(c => c.Src is Constant k && k.Value == 7);
        Assert.True(sevens >= 2,
            "alarms[0].deadline should read the caller's instance field, not a pinned head");
    }

    [Fact]
    public void AnEmptyTupleKeywordArgument_BindsAnEmptySequence()
    {
        // preserve_dios=() -- the empty tuple is a compile-time EMPTY SEQUENCE, not a
        // runtime value a Val can carry.
        var ir = Gen(
            "@inline\n" +
            "def drain(pins) -> uint8:\n" +
            "    n = 0\n" +
            "    for p in pins:\n" +
            "        n = n + 1\n" +
            "    return n\n" +
            "buf = bytearray(1)\n" +
            "buf[0] = drain(pins=())\n");

        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.Contains(body.OfType<ArrayStore>(), s => s.Src is Constant k && k.Value == 0);
    }

    [Fact]
    public void AnEmptyTupleParameterDefault_BindsAnEmptySequence()
    {
        var ir = Gen(
            "@inline\n" +
            "def drain(pins=()) -> uint8:\n" +
            "    n = 0\n" +
            "    for p in pins:\n" +
            "        n = n + 1\n" +
            "    return n\n" +
            "buf = bytearray(1)\n" +
            "buf[0] = drain()\n");

        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.Contains(body.OfType<ArrayStore>(), s => s.Src is Constant k && k.Value == 0);
    }

    [Fact]
    public void ForInOverAStarArgsSequence_BindsInstanceElements()
    {
        // `for a in alarms` inside a *args callee: the bound sequence's elements are
        // instance-carrying names, not integers -- previously refused outright.
        var ir = Gen(
            "class Alarm:\n" +
            "    def __init__(self, d: uint8):\n" +
            "        self.deadline = d\n" +
            "@inline\n" +
            "def soonest(*alarms) -> uint8:\n" +
            "    best = 255\n" +
            "    for a in alarms:\n" +
            "        if a.deadline < best:\n" +
            "            best = a.deadline\n" +
            "    return best\n" +
            "x = Alarm(9)\n" +
            "y = Alarm(4)\n" +
            "buf = bytearray(1)\n" +
            "buf[0] = soonest(x, y)\n");

        var body = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.True(
            body.OfType<Copy>().Any(c => c.Src is Constant k && k.Value == 4) ||
            body.OfType<ArrayStore>().Any(s => s.Src is Constant k && k.Value == 4),
            "the loop should reach y.deadline (4) through the carried instance names");
    }
}

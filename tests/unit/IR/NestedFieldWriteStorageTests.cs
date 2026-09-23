using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// A write through a field that HOLDS another instance -- `o.bag.total = v` spelled at the
// top level, or `self.bag.total = v` inside a method called as `o.bump()` -- was invisible
// to the mutation scan. `MarkModuleInstanceFields` followed `self.<field>.<method>()` calls
// and read `prog.Functions`, so a direct member write through the held instance, or any
// method call at module level, left `bag_total` registered as the constructor's constant.
// The write emitted a real store but the READ in the same statement -- and every later
// read in main -- folded the stale value:
//
//     class Bag:
//         def __init__(self): self.total = 0
//     bag = Bag(); o = Outer(bag)
//     while i < n: o.bag.total = o.bag.total + i
//
// emitted `copy i -> bag_total` each pass -- `o.bag.total` folded to 0 before the write
// killed the constant -- and printed the last i instead of the sum.
//
// WHAT DISCRIMINATES: `bag_total` in ir.Globals -- Mark() registering real storage -- and
// the sum expression surviving as a Binary on a Variable, not `copy <addend> -> bag_total`.
//
// WHAT IS INVARIANT: a field never written through a held instance keeps folding, and a
// receiver whose method writes nothing marks nothing.
public class NestedFieldWriteStorageTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Classes =
        "class Bag:\n" +
        "    def __init__(self):\n" +
        "        self.total = 0\n" +
        "class Outer:\n" +
        "    def __init__(self, b: Bag):\n" +
        "        self.bag = b\n";

    private static bool HasGlobal(ProgramIR ir, string name) =>
        ir.Globals.Any(g => g.Name == name);

    private static IEnumerable<Copy> Copies(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>();

    // --- the defect: a nested member write at module level ------------------------

    [Fact]
    public void AHeldFieldWrittenAtModuleLevel_GetsStorage()
    {
        var ir = Gen(Classes +
            "bag = Bag()\n" +
            "o = Outer(bag)\n" +
            "o.bag.total = o.bag.total + 1\n");

        Assert.True(HasGlobal(ir, "bag_total"),
            "o.bag.total writes through the held instance; bag_total must be real storage");
    }

    [Fact]
    public void AHeldFieldWrittenAtModuleLevel_ReadsTheVariableNotTheConstant()
    {
        // `o.bag.total = o.bag.total + 1`: if the RHS read folds, the store is `copy 1 ->`.
        var ir = Gen(Classes +
            "bag = Bag()\n" +
            "o = Outer(bag)\n" +
            "o.bag.total = o.bag.total + 1\n");

        // The constructor's `copy 0 -> bag_total` is the one constant store the field may
        // keep -- it initializes the real storage. A second constant store is the folded
        // RHS landing the addend where the sum belongs.
        Assert.Single(Copies(ir),
            c => c.Dst is Variable { Name: "bag_total" } && c.Src is Constant { Value: 0 });
        Assert.DoesNotContain(Copies(ir),
            c => c.Dst is Variable { Name: "bag_total" } && c.Src is Constant { Value: 1 });
    }

    // --- the same write reached through a method call at module level -------------

    [Fact]
    public void AHeldFieldWrittenThroughAMethod_GetsStorage()
    {
        var src =
            "class Bag:\n" +
            "    def __init__(self):\n" +
            "        self.total = 0\n" +
            "class Outer:\n" +
            "    def __init__(self, b: Bag):\n" +
            "        self.bag = b\n" +
            "    def bump(self, i: int):\n" +
            "        self.bag.total = self.bag.total + i\n" +
            "bag = Bag()\n" +
            "o = Outer(bag)\n" +
            "o.bump(1)\n";
        var ir = Gen(src);

        Assert.True(HasGlobal(ir, "bag_total"),
            "self.bag.total inside bump() is a write through the held instance; "
            + "bag_total must be real storage");
    }

    [Fact]
    public void AHeldFieldWrittenThroughAMethod_ReadsTheVariableNotTheConstant()
    {
        // `i` mutable so the argument binds a Variable: if `self.bag.total` folds in the
        // RHS, the store is `copy i -> bag_total` -- the addend alone, not the sum.
        var src =
            "class Bag:\n" +
            "    def __init__(self):\n" +
            "        self.total = 0\n" +
            "class Outer:\n" +
            "    def __init__(self, b: Bag):\n" +
            "        self.bag = b\n" +
            "    def bump(self, i: int):\n" +
            "        self.bag.total = self.bag.total + i\n" +
            "bag = Bag()\n" +
            "o = Outer(bag)\n" +
            "i = 0\n" +
            "i = i + 1\n" +
            "o.bump(i)\n";
        var ir = Gen(src);

        Assert.DoesNotContain(Copies(ir),
            c => c.Dst is Variable { Name: "bag_total" }
                 && c.Src is Variable { Name: var n } && n.EndsWith("i"));
    }

    // --- the same write reached through a generator machine's receiver -------------

    [Fact]
    public void AHeldFieldWrittenInsideAGeneratorMethod_GetsStorage()
    {
        // `_recv.bag.total` inside `poll` is the machine spelling of `self.bag.total`
        // in `read`: the receiver is a field of `Rdr_read`, and the write still lands
        // on `bag_total`.
        var src =
            "class Bag:\n" +
            "    def __init__(self):\n" +
            "        self.total = 0\n" +
            "class Rdr:\n" +
            "    def __init__(self, b: Bag):\n" +
            "        self.bag = b\n" +
            "    def read(self):\n" +
            "        i = 0\n" +
            "        while i < 3:\n" +
            "            i = i + 1\n" +
            "            self.bag.total = self.bag.total + i\n" +
            "            yield i\n" +
            "bag = Bag()\n" +
            "r = Rdr(bag)\n" +
            "for v in r.read():\n" +
            "    pass\n";
        var ir = Gen(src);

        Assert.True(HasGlobal(ir, "bag_total"),
            "self.bag.total inside read() writes through the held instance; "
            + "bag_total must be real storage");
    }

    // --- invariant: read-only shapes still fold ------------------------------------

    [Fact]
    public void AHeldFieldThatIsOnlyRead_StaysFolded()
    {
        var ir = Gen(Classes +
            "bag = Bag()\n" +
            "o = Outer(bag)\n" +
            "x = o.bag.total\n");

        Assert.False(HasGlobal(ir, "bag_total"),
            "a read marks nothing: the constructor's constant is the right answer");
    }

    [Fact]
    public void AReceiverWhoseMethodWritesNothing_MarksNothing()
    {
        var src =
            "class Bag:\n" +
            "    def __init__(self):\n" +
            "        self.total = 0\n" +
            "class Outer:\n" +
            "    def __init__(self, b: Bag):\n" +
            "        self.bag = b\n" +
            "    def peek(self):\n" +
            "        pass\n" +
            "bag = Bag()\n" +
            "o = Outer(bag)\n" +
            "o.peek()\n";
        var ir = Gen(src);

        Assert.False(HasGlobal(ir, "bag_total"),
            "peek() writes no field; the all-or-nothing mark must not fire");
    }
}

using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The defects an unmodified adafruit_bmp280 driver surfaced: state that must be
/// fresh per inline expansion or evaluated once, and was neither.
/// </summary>
public class InlineExpansionFreshnessTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    /// <summary>
    /// Same-depth inline expansions share one frame slot for a local, so the
    /// STORAGE bound (arraySizes) stays grown at 24 while the second expansion's
    /// <c>buf = bytearray(3)</c> declares 3. len() must answer the declared count,
    /// not the shared slot bound -- bmp280's temperature readinto pulled 24 bytes
    /// off the wire because the earlier 24-byte calibration read had grown it.
    /// </summary>
    [Fact]
    public void ASecondInlineExpansionReportsItsOwnBytearrayLength()
    {
        var ir = Gen(
            "from pymcu.types import used, uint8\n\n" +
            "def read_reg(reg: uint8, n: uint8) -> bytearray:\n" +
            "    buf = bytearray(n)\n" +
            "    buf[0] = reg\n" +
            "    return buf\n\n" +
            "@used\n" +
            "def main() -> uint8:\n" +
            "    a = read_reg(0x88, 24)\n" +
            "    b = read_reg(0xFA, 3)\n" +
            "    return len(b)\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is Return r && r.Value is Constant { Value: 3 });
        Assert.DoesNotContain(main.Body, i => i is Return r && r.Value is Constant { Value: 24 });
    }

    /// <summary>
    /// The companion reader: `for b in f()` over the second expansion's smaller
    /// buffer unrolls its own count, not the shared slot's grown one.
    /// </summary>
    [Fact]
    public void AForOverTheSecondExpansionUnrollsItsDeclaredLength()
    {
        var ir = Gen(
            "from pymcu.types import used, uint8\n\n" +
            "def read_reg(reg: uint8, n: uint8) -> bytearray:\n" +
            "    buf = bytearray(n)\n" +
            "    buf[0] = reg\n" +
            "    return buf\n\n" +
            "@used\n" +
            "def main() -> uint8:\n" +
            "    a = read_reg(0x88, 24)\n" +
            "    b = read_reg(0xFA, 3)\n" +
            "    t: uint8 = 0\n" +
            "    for x in b:\n" +
            "        t = t + x\n" +
            "    return t\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        // The unrolled loop body reads b[0], b[1], b[2] -- three ArrayLoads on b's
        // own storage, not 24. `b` now copies the callee-local buffer home, so the
        // loads this test counts are the ones on `main.b`, not on the shared
        // callee cell (the copy itself loads that cell a further declared-count
        // times, which is the point of it).
        Assert.Equal(3, main.Body.Count(i => i is ArrayLoad al && al.ArrayName == "main.b"));
    }

    /// <summary>
    /// `return` inside `with` must still run __exit__ on the way out. Before the
    /// pending-finally fix it jumped to the expansion's exit label past the
    /// emitted __exit__, and i2c_device's lock stayed held: the next `with`
    /// spun in try_lock forever.
    /// </summary>
    [Fact]
    public void AnEarlyReturnInsideWithRunsExitBeforeJumpingOut()
    {
        var ir = Gen(
            "from pymcu.types import used, uint8, inline\n\n" +
            "class Lock:\n" +
            "    def __init__(self):\n" +
            "        self.locked: uint8 = 0\n" +
            "    @inline\n" +
            "    def __enter__(self):\n" +
            "        self.locked = 1\n" +
            "        return self\n" +
            "    @inline\n" +
            "    def __exit__(self, a=None, b=None, c=None):\n" +
            "        self.locked = 0\n\n" +
            "lk = Lock()\n\n" +
            "@inline\n" +
            "def read_reg() -> uint8:\n" +
            "    with lk:\n" +
            "        return 7\n\n" +
            "@used\n" +
            "def main() -> uint8:\n" +
            "    return read_reg()\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        var body = main.Body.ToList();

        // The expansion-exit label is defined just before the read_reg end marker;
        // the early return jumps to it. The release (locked = 0) must be emitted
        // BEFORE that jump -- the unfixed code emitted it after, where nothing
        // reaches it.
        int endIdx = body.FindIndex(
            i => i is InlineExpansionMarker m && m.IsEnd && m.FuncName.Contains("read_reg"));
        Assert.True(endIdx > 0 && body[endIdx - 1] is Label,
            "read_reg expansion did not end with an exit label");
        string exitLabel = ((Label)body[endIdx - 1]).Name;
        int escape = body.FindLastIndex(endIdx - 1,
            i => i is Jump j && j.Target == exitLabel);
        int acquire = body.FindIndex(
            i => i is Copy { Src: Constant { Value: 1 }, Dst: Variable v }
                 && v.Name.Contains("locked"));
        // The release -- the __exit__ store -- is the locked = 0 AFTER the acquire;
        // __init__'s own locked = 0 sits before it and is not the one under test.
        int release = body.FindIndex(acquire + 1,
            i => i is Copy { Src: Constant { Value: 0 }, Dst: Variable v }
                 && v.Name.Contains("locked"));
        Assert.True(release >= 0, "no lock-release store was emitted");
        Assert.True(escape >= 0, "no jump to the expansion exit was emitted");
        Assert.True(release < escape,
            $"release at {release} must precede the escape jump at {escape}");
    }

    /// <summary>
    /// `f()[i][j]` subscripts an IndexExpr target, which reached the generic
    /// tail of VisitIndex -- where the target expression was evaluated a second
    /// time after the __getitem__ probe already ran it once. The whole nested
    /// expansion replayed: every side effect twice.
    /// </summary>
    [Fact]
    public void ASubscriptedSubscriptEvaluatesItsTargetOnce()
    {
        var ir = Gen(
            "from pymcu.types import used, uint8\n\n" +
            "scratch = bytearray(4)\n\n" +
            "def writeto(addr: uint8, buf: bytearray, n: uint8):\n" +
            "    x = buf[0]\n" +
            "    if x == 0:\n" +
            "        pass\n\n" +
            "def read_reg(reg: uint8) -> bytearray:\n" +
            "    buf = bytearray(1)\n" +
            "    buf[0] = reg\n" +
            "    writeto(reg, scratch, 1)\n" +
            "    return buf\n\n" +
            "@used\n" +
            "def main() -> uint8:\n" +
            "    return read_reg(getn())[0][0]\n\n" +
            "def getn() -> uint8:\n" +
            "    return scratch[1]\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Single(main.Body, i => i is Call c && c.FunctionName == "writeto");
        Assert.Single(main.Body, i => i is Call c && c.FunctionName == "getn");
    }

    /// <summary>
    /// `while get_status() & 0x08:` is not a comparison, so VisitCondition hands
    /// it back -- but only after eagerly lowering both operands, which doubled
    /// the status-register transaction per poll.
    /// </summary>
    [Fact]
    public void ANonComparisonConditionRunsItsCalleeOncePerPoll()
    {
        var ir = Gen(
            "from pymcu.types import used, uint8\n\n" +
            "polls = bytearray(4)\n\n" +
            "def get_status() -> uint8:\n" +
            "    polls[0] = 42\n" +
            "    return polls[1]\n\n" +
            "@used\n" +
            "def main() -> uint8:\n" +
            "    while get_status() & 0x08:\n" +
            "        pass\n" +
            "    return 1\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Single(main.Body, i => i is Call c && c.FunctionName == "get_status");
    }

    /// <summary>
    /// `bytes([reg & 0xFF])` with a const-bound reg folds to a compile-time
    /// sequence bound to the inline `write`'s parameter -- a binding with no
    /// storage. Forwarded to the real `writeto` it resolved an unbacked name and
    /// the write went out as 0x00 -- the BMP280 chip-id write. The marshal now
    /// materializes the elements into a hidden buffer.
    /// </summary>
    [Fact]
    public void AConstSequenceArgToARealSubroutineGetsStorage()
    {
        var ir = Gen(
            "from pymcu.types import used, uint8, inline\n\n" +
            "def writeto(addr: uint8, buf: bytearray, n: uint8):\n" +
            "    x = buf[0]\n" +
            "    if x == 0:\n" +
            "        pass\n\n" +
            "@inline\n" +
            "def write(buf: bytearray):\n" +
            "    writeto(0x77, buf, 1)\n\n" +
            "@inline\n" +
            "def read_register(reg: uint8):\n" +
            "    write(bytes([reg & 0xFF]))\n\n" +
            "@used\n" +
            "def main() -> uint8:\n" +
            "    read_register(0xD0)\n" +
            "    return 1\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        // The sequence materializes: a store of the folded byte into the hidden
        // buffer, and the call carries that buffer's base -- not the unbacked
        // param name.
        Assert.Contains(main.Body, i => i is ArrayStore s
            && s.ArrayName.Contains("__seqarg") && s.Src is Constant { Value: 0xD0 });
        Assert.Contains(main.Body, i => i is Call c
            && c.FunctionName == "writeto"
            && c.Args.Any(a => a is ArrayBase ab && ab.ArrayName.Contains("__seqarg")));
    }
}

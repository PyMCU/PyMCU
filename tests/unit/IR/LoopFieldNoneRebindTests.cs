using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A method call inside a loop must not drop the noneValued record of a field
/// the method assigns, when that field holds an instance rather than a tagged
/// union. adafruit_framebuf's <c>text()</c> stores <c>BitmapFont(...)</c> into
/// <c>self._font</c>, a field <c>__init__</c> bound to <c>None</c>; there is no
/// union tag for it (instances have no member slot), so the noneValued record
/// IS the field's None-ness. The loop invalidation used to strip it for every
/// written field of the receiver: <c>not self._font</c> unfolded, the branch
/// join dropped the class binding, and <c>self._font.draw_char()</c> resolved
/// to <c>display__font_draw_char</c>, a callee nobody emits.
/// </summary>
public class LoopFieldNoneRebindTests
{
    private static ProgramIR Gen(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return Optimizer.Optimize(ir);
    }

    private static ProgramIR GenImported(string framebuf, string main)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["framebuf"] = new Parser(new Lexer(framebuf).Tokenize()).ParseProgram(),
        };
        return Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { "framebuf" }));
    }

    private static IEnumerable<string> Expanded(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body)
            .OfType<InlineExpansionMarker>()
            .Where(m => !m.IsEnd)
            .Select(m => m.FuncName);

    // Font is declared AFTER FrameBuffer on purpose: FrameBuffer's scan records
    // fieldClasses[FrameBuffer|_font] while Font's layout is not yet registered,
    // so the stored value is the bare name -- the same gap that made
    // ResolveConcreteClass("BitmapFont") fail in adafruit_framebuf.
    private const string FrameBuf =
        "class FrameBuffer:\n" +
        "    def __init__(self):\n" +
        "        self._font = None\n" +
        "    def text(self, s: str):\n" +
        "        if not self._font:\n" +
        "            self._font = Font(s)\n" +
        "        self._font.draw()\n" +
        "class Font:\n" +
        "    def __init__(self, s: str):\n" +
        "        self.name = s\n" +
        "    def draw(self):\n" +
        "        self.name = self.name + \"!\"\n";

    [Fact]
    public void AFieldAssignedAnInstanceInALoopCalledMethod_StillResolvesItsMethods()
    {
        var ir = GenImported(
            FrameBuf,
            "import framebuf\n" +
            "o = framebuf.FrameBuffer()\n" +
            "while True:\n" +
            "    o.text(\"x\")\n");

        Expanded(ir).Should().Contain("framebuf_Font_draw",
            because: "self._font.draw() must resolve to the Font method, not a free o__font_draw");
    }

    [Fact]
    public void TheSameWithoutTheLoop_AlsoResolves()
    {
        var ir = GenImported(
            FrameBuf,
            "import framebuf\n" +
            "o = framebuf.FrameBuffer()\n" +
            "o.text(\"x\")\n");

        Expanded(ir).Should().Contain("framebuf_Font_draw");
    }

    // The other side of the guard: `reading` sees a None write in __init__ AND a
    // scalar write in poll(), which makes it a tagged union field -- payload plus
    // a tag byte. Its noneValued/narrowed records ARE as stale across the loop
    // back-edge as a bare name's: poll()'s write can leave the field on a
    // different member next iteration, so `self.reading is None` must ask the
    // tag, not the entry state. Keeping the mark folds the check to "still
    // None" and `self.hits = 7` goes unconditional.
    private const string UnionField =
        "class Meter:\n" +
        "    def __init__(self):\n" +
        "        self.reading = None\n" +
        "        self.hits = 0\n" +
        "    def poll(self, v: int):\n" +
        "        if self.reading is None:\n" +
        "            self.hits = 7\n" +
        "        self.reading = v\n";

    [Fact]
    public void AUnionFieldWrittenByALoopCalledMethod_StillAnswersThroughItsTag()
    {
        var ir = Gen(
            UnionField +
            "m = Meter()\n" +
            "while True:\n" +
            "    m.poll(9)\n");

        var body = ir.Functions.Single(f => f.Name == "main").Body;
        body.OfType<JumpIfNotEqual>()
            .Where(j => j.Src1 is Variable v && v.Name.EndsWith("reading$tag"))
            .Should().ContainSingle(
                because: "`self.reading is None` inside a loop-called method must " +
                         "read the run-time tag, not a stale mark");
    }
}

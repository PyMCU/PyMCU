using System.Linq;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// P2 AVR gaps bundle, item 3. A name first bound to constant text, then reassigned to a
/// genuinely run-time value, kept answering reads with the FIRST text -- silently, not a
/// refusal:
///
///   text = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
///   print(text)          # correct: the long text
///   text = f"{n}"        # n a run-time value (GPIOR0-seeded, nothing folds)
///   print(text)          # wrong: printed the OLD long text again, not n's digits
///
/// Two causes, both in how a name stops being "known constant text":
///
/// 1. VisitAssign's preamble (StaticStringOf-driven) clears strConstantVariables on every
///    assignment whose right-hand side is not known text -- but TryExpandFStringValue and its
///    siblings (TryEmitJoinAssign's runtime branch, hex()/bin()/oct(), str()/repr()) return
///    true and make VisitAssign return EARLY, before that preamble ever runs. A name's FIRST
///    assignment going through one of these (a run-time f-string, say) never left a stale
///    entry to clear; the bug needs the FIRST assignment to be constant text (a plain string
///    literal, or -- since the previous commit -- a fully compile-time f-string) so
///    strConstantVariables is actually populated before the SECOND, run-time assignment tries
///    to clear it.
///
/// 2. Even the ORIGINAL preamble's own clear was incomplete for the entry file's top level:
///    `main` is not a scope OVER its own module globals the way a function is (the same fact
///    LocalScopeKeys/TryGetMultiStr's own comments rely on) -- ScanGlobals' one-time,
///    pre-generation scan of `name = "literal"` files the text under the BARE module-global
///    key, but the per-statement clear only ever propagated to that bare key for an imported
///    module's `__module_init`, never for `main`. Clearing "main.text" left the bare "text"
///    entry exactly as ScanGlobals wrote it, and StaticStringOf's VariableExpr case falls
///    back to the bare name last -- finding the stale entry there regardless of how correctly
///    the scoped key was cleared.
///
/// The fix: a shared ClearStaleConstantText, called from every early-return runtime-buffer
/// path, clears both keys the same way the preamble's own (now-corrected) else-branch does.
/// </summary>
public class StrConstantReassignmentTests
{
    private const string Prelude =
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "from pymcu.types import uint8\n" +
        "import pymcu.strfmt as _pymcu_strfmt\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write(c: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n";

    private static readonly Dictionary<string, ProgramNode> StrfmtModule = new()
    {
        ["pymcu.strfmt"] = new Parser(new Lexer(
            "from pymcu.types import uint8, uint16, uint32, int32\n" +
            "def _fs_text(buf: bytearray, pos: uint16, s: const[str]) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_u32(buf: bytearray, pos: uint16, v: uint32) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_i32(buf: bytearray, pos: uint16, v: int32) -> uint16:\n" +
            "    return pos\n" +
            "def _fs_frepr(buf: bytearray, pos: uint16, value: float) -> uint16:\n" +
            "    return pos\n").Tokenize()).ParseProgram(),
    };

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            StrfmtModule, new DeviceConfig { Arch = "avr" });

    private static string FlashText(ProgramIR ir, string name) =>
        new string(ir.Functions.SelectMany(f => f.Body).OfType<FlashData>()
            .Single(fd => fd.Name == name).Bytes.TakeWhile(b => b != 0)
            .Select(b => (char)b).ToArray());

    private static List<string> StrWrites(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body)
            .OfType<Copy>()
            .Where(c => c.Dst is Variable { Name: "uart_write_str.s" } && c.Src is FlashStrAddr)
            .Select(c => FlashText(ir, ((FlashStrAddr)c.Src).Name))
            .ToList();

    // The run-time reassignment's own fixed-buffer store: `main.text[i] = ...` for the
    // digits strfmt writes. Its presence is the positive half of the assertion -- the
    // second print must come from THIS, not a repeated flash-string call.
    private static bool BuiltARuntimeBuffer(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body)
            .Any(i => i is Call c && c.FunctionName.Contains("_fs_"));

    private const string Long = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    [Fact]
    public void ConstantTextThenRuntimeFString_TheSecondReadIsNotTheOldText()
    {
        var ir = Gen(
            "text = \"" + Long + "\"\n" +
            "print(text)\n" +
            "n: uint8 = GPIOR0.value\n" +
            "text = f\"{n}\"\n" +
            "print(text)\n");

        // The long text streams exactly once -- from the FIRST print. A second occurrence
        // would be the stale-cache bug: the second print repeating the old text instead of
        // building n's digits.
        Assert.Equal(1, StrWrites(ir).Count(t => t == Long));
        Assert.True(BuiltARuntimeBuffer(ir));
    }

    [Fact]
    public void ConstantFStringThenRuntimeFString_TheSecondReadIsNotTheOldText()
    {
        // The same bug, but the FIRST assignment is itself a fully compile-time f-string
        // (the shape the previous commit made fold to text at all) rather than a plain
        // string literal.
        var ir = Gen(
            "text = f\"{'" + Long + "'}\"\n" +
            "print(text)\n" +
            "n: uint8 = GPIOR0.value\n" +
            "text = f\"{n}\"\n" +
            "print(text)\n");

        Assert.Equal(1, StrWrites(ir).Count(t => t == Long));
        Assert.True(BuiltARuntimeBuffer(ir));
    }

    [Fact]
    public void ConstantTextThenAnOrdinaryRuntimeReassignment_ClearsTheOldText()
    {
        // Not through an f-string at all -- an ordinary scalar reassignment to a run-time
        // value exhibits the SAME bug (cause 2 above, the bare module-global key), with no
        // f-string mechanism involved on either side.
        var ir = Gen(
            "text = \"" + Long + "\"\n" +
            "print(text)\n" +
            "n: uint8 = GPIOR0.value\n" +
            "text = n\n" +
            "print(text)\n");

        Assert.Equal(1, StrWrites(ir).Count(t => t == Long));
        Assert.Contains(ir.Functions.SelectMany(f => f.Body),
            i => i is Call c && c.FunctionName.Contains("uart_write_decimal"));
    }

    [Fact]
    public void ConstantTextThenAnotherConstantText_StillCompiles()
    {
        // Regression guard only: reassigning a constant-text name to ANOTHER constant text
        // (no run-time value at all, so ClearStaleConstantText is never even reached -- both
        // TryExpandFStringValue and the preamble decline the whole statement as fully
        // constant) must keep compiling. Two DIFFERENT compile-time texts bound to the same
        // name in sequence is exactly what MarkMultiStr's own dispatch mechanism is for
        // (tested elsewhere, e.g. ConditionalStringExprTests) -- not re-tested here.
        Gen(
            "text = \"" + Long + "\"\n" +
            "print(text)\n" +
            "text = \"short\"\n" +
            "print(text)\n");
    }
}

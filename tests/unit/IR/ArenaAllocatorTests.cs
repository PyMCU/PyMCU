/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

using Xunit;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;

namespace PyMCU.UnitTests;

/// <summary>
/// docs/rfcs/0004-arena-allocator.md. `bytearray(n)` where n is not a compile-time
/// constant: accepted (arena.alloc, not a static array) where the once rule can prove the
/// statement runs at most once, refused with a located, reason-naming diagnostic
/// everywhere else. Every test imports `pymcu.arena as _pymcu_arena` itself (the entry
/// file preamble injection this stands in for is `pymcu build`'s job, tested separately
/// in the driver suite) so a missing-import refusal is never what these are checking.
///
/// Before this feature existed, every one of the "accepted" cases below raised
/// `bytearray: could not determine buffer size from initializer.` (verified by hand while
/// building this change, the same refusal BytearraySizeColumnTests pins for the cases that
/// still do) -- this file is the regression coverage for the split between "still refused"
/// and "now accepted".
/// </summary>
public class ArenaAllocatorTests
{
    private const string Prelude =
        "import pymcu.arena as _pymcu_arena\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "from pymcu.types import uint8, uint16, inline\n\n";

    // pymcu.types names (uint8, inline, ...) are compiler intrinsics and need no entry
    // here -- every other test in this file relies on that already. pymcu.arena is an
    // ordinary stdlib module with real Python logic, so unlike a chip import it has to be
    // parsed and handed in explicitly, the way the driver hands every stdlib module it
    // loads from disk to the real Generate() call.
    //
    // The shipped ARENA_SIZE default is 0 (arena unused -- see arena.py); `pymcu build`
    // always replaces it with the real reservation before a program that uses the arena
    // is ever compiled (`_inject_arena_shim`, build.py). Every test here needs the same
    // substitution: with ARENA_SIZE left at 0, `alloc()`'s bounds check on a fresh
    // (_arena_pos == 0) arena is provably unsatisfiable however wide n is, which VisitRaise
    // folds to an unconditional MemoryError at compile time -- correct on the shipped
    // default, and not what any of these tests are checking.
    private static readonly ProgramNode ArenaModuleAst = new Parser(new Lexer(
        System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(FindRepoFile("lib/src/pymcu/arena.py")),
            @"^ARENA_SIZE: uint16 = \d+$", "ARENA_SIZE: uint16 = 64",
            System.Text.RegularExpressions.RegexOptions.Multiline))
        .Tokenize()).ParseProgram();

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"could not find '{relativePath}' walking up from {AppContext.BaseDirectory}");
    }

    private static ProgramIR Generate(string body) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + body).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode> { ["pymcu.arena"] = ArenaModuleAst },
            new DeviceConfig { Arch = "avr" });

    private static CompilerError Fails(string body) =>
        Assert.ThrowsAny<CompilerError>(() => Generate(body));

    // ------------------------------------------------------------------
    // Accepted: module-level, not in a loop.
    // ------------------------------------------------------------------

    [Fact]
    public void ModuleLevelRuntimeSizeAllocatesFromTheArena()
    {
        var program = Generate(
            "n: uint16 = 5\n" +
            "buf: bytearray = bytearray(n)\n" +
            "buf[0] = 42\n" +
            "x: uint8 = buf[0]\n" +
            "l: uint16 = len(buf)\n");

        var main = Assert.Single(program.Functions, f => f.Name == "main");
        string dump = string.Join("\n", main.Body.Select(i => i.ToString()));

        // The allocation is a CALL into arena.alloc (inlined -- so its own body shows up as
        // ordinary Binary/Copy/SignalError instructions, not a `Call` record naming
        // "alloc"), never a static array: no ArrayStore/ArrayLoad zero-initializing a
        // buffer named "buf", and no GcAlloc (that is list[T]'s allocator, a different
        // mechanism -- see the RFC, "Model").
        Assert.DoesNotContain(main.Body, i => i is GcAlloc);
        Assert.DoesNotContain(main.Body,
            i => i is ArrayStore st && st.ArrayName == "buf");
        // The MemoryError guard (arena.alloc's own bounds check) is present, proving the
        // call was actually inlined rather than silently dropped.
        Assert.Contains(main.Body, i => i is SignalError);
        // buf[0] = 42 and x = buf[0] both go through the SAME array ("_arena", write8/read8
        // inlined), not a per-buffer array -- the offset is what makes them the same buffer.
        Assert.Contains(main.Body, i => i is ArrayStore ws && ws.ArrayName == "_arena");
        Assert.Contains(main.Body, i => i is ArrayLoad rd && rd.ArrayName == "_arena");
    }

    [Fact]
    public void UnannotatedModuleLevelRuntimeSizeAlsoAllocates()
    {
        // The unannotated spelling (`buf = bytearray(n)`) redirects into the same VarDecl
        // path as the annotated one (Assign.cs:280) -- covered separately because that
        // redirect is itself a place a future refactor could silently bypass.
        Generate(
            "n: uint16 = 5\n" +
            "buf = bytearray(n)\n" +
            "buf[0] = 1\n");
    }

    [Fact]
    public void ConstantSizedBytearrayIsUnaffected()
    {
        // The invariant this feature must not disturb: a compile-time size stays on the
        // static-array path (arraySizes), not the arena, whether or not pymcu.arena
        // happens to be imported in the same file.
        var program = Generate("buf: bytearray = bytearray(8)\n" + "buf[0] = 1\n");
        var main = Assert.Single(program.Functions, f => f.Name == "main");
        Assert.Contains(main.Body,
            i => i is ArrayStore st && st.ArrayName == "buf"
                                     && st.Count == 8);
    }

    // ------------------------------------------------------------------
    // Refused: the once rule.
    // ------------------------------------------------------------------

    [Fact]
    public void InsideAWhileLoopIsRefused()
    {
        var ex = Fails(
            "i: uint16 = 0\n" +
            "while i < 3:\n" +
            "    n: uint16 = i\n" +
            "    buf: bytearray = bytearray(n)\n" +
            "    i = i + 1\n");

        Assert.Contains("loop runs", ex.Message);
        Assert.Contains("arena never frees", ex.Message);
    }

    [Fact]
    public void InsideARuntimeForLoopIsRefused()
    {
        var ex = Fails(
            "def make(k: uint16) -> uint16:\n" +
            "    return k\n\n" +
            "n: uint16 = 3\n" +
            "for i in range(n):\n" +
            "    buf: bytearray = bytearray(make(i))\n");

        Assert.Contains("loop runs", ex.Message);
    }

    [Fact]
    public void InsideAnUnrolledCompileTimeForLoopIsStillRefused()
    {
        // `for x in [1, 2, 3]:` unrolls at compile time -- three separate lowerings of the
        // SAME source statement, each an independent alloc() call. Still "not once": the
        // once rule counts textual loop nesting, not whether the compiler happens to know
        // the trip count. The size has to come from a function call (make(k)) rather than
        // the bare loop variable k -- k itself folds to 1/2/3 per unrolled iteration, which
        // would take the CONSTANT-size path this feature must not touch, never reaching
        // (or needing) the once rule at all.
        var ex = Fails(
            "def make(k: uint16) -> uint16:\n" +
            "    return k\n\n" +
            "for k in [1, 2, 3]:\n" +
            "    buf: bytearray = bytearray(make(k))\n");

        Assert.Contains("loop runs", ex.Message);
    }

    [Fact]
    public void InsideAPlainFunctionIsRefused()
    {
        // Refused unconditionally, however many times the function is actually called --
        // phase 1 does not attempt the general call-site-count proof (RFC section 2/6).
        var ex = Fails(
            "def make(n: uint16) -> uint8:\n" +
            "    buf: bytearray = bytearray(n)\n" +
            "    return buf[0]\n\n" +
            "n: uint16 = 4\n" +
            "x: uint8 = make(n)\n");

        Assert.Contains("cannot be proven to run at most once", ex.Message);
        Assert.Contains("'make'", ex.Message);
    }

    [Fact]
    public void CalledFromTwoSitesIsRefused()
    {
        var ex = Fails(
            "def make(n: uint16) -> uint8:\n" +
            "    buf: bytearray = bytearray(n)\n" +
            "    return buf[0]\n\n" +
            "n: uint16 = 4\n" +
            "a: uint8 = make(n)\n" +
            "b: uint8 = make(n)\n");

        Assert.Contains("cannot be proven to run at most once", ex.Message);
    }

    [Fact]
    public void InsideAnIsrHandlerIsRefused()
    {
        // An ISR handler is always a real (non-inlined) function with a fixed vector
        // address, so it is caught by the same "not main / not a library module init"
        // check as any other plain function -- no ISR-specific code needed (RFC section 2).
        var ex = Fails(
            "from pymcu.types import compile_isr\n\n" +
            "def on_ovf() -> None:\n" +
            "    n: uint16 = 4\n" +
            "    buf: bytearray = bytearray(n)\n\n" +
            "compile_isr(on_ovf, 0x000A)\n");

        Assert.Contains("cannot be proven to run at most once", ex.Message);
    }

    // ------------------------------------------------------------------
    // Accepted: an @inline __init__ expanding at a module-level construction site.
    // ------------------------------------------------------------------

    [Fact]
    public void LocalInsideAModuleLevelConstructedInlineInitIsAccepted()
    {
        // `self.buf: bytearray = bytearray(n)` (a per-instance FIELD) does not compile at
        // all yet, on any target, with or without this feature -- an unrelated, pre-
        // existing ZCA field-construction gap (RFC section 6). This is the part that IS
        // this feature: a LOCAL inside an @inline __init__ that constructs at module
        // level, which currentFunction sees as "main" throughout the inlining exactly like
        // a bare module-level statement (RFC section 2).
        var program = Generate(
            "class Dev:\n" +
            "    @inline\n" +
            "    def __init__(self, n: uint16):\n" +
            "        buf: bytearray = bytearray(n)\n" +
            "        self.off = buf\n\n" +
            // A genuinely runtime seed: a plain `n: uint16 = 4` module-level literal, passed
            // as a call argument to an @inline constructor, is itself a compile-time
            // constant by the time __init__ sees it ("a call argument that holds a compile-
            // time constant is passed as that constant" -- docs/language/limitations.md),
            // which would take the CONSTANT-size static-array path this feature must not
            // touch, and the test would pass without ever exercising the once rule.
            "n: uint16 = uint16(GPIOR0.value)\n" +
            "d: Dev = Dev(n)\n");

        var main = Assert.Single(program.Functions, f => f.Name == "main");
        // alloc()'s own MemoryError bounds check is present, proving the once rule accepted
        // the allocation and it was actually inlined (not silently dropped). Indexing a
        // freshly-allocated arena buffer from a SECOND level of inlining (module ->
        // __init__ -> write8, as opposed to module -> write8 tested above) hit a separate,
        // narrower pre-existing resolution gap unrelated to the once rule this test is
        // about; not exercised here, and not a claim this feature makes (see the RFC).
        Assert.Contains(main.Body, i => i is SignalError);
    }

    [Fact]
    public void TwoModuleLevelConstructionsEachAllocateIndependently()
    {
        // Two DIFFERENT module-level statements, each running once, are not a "called
        // twice" violation -- @inline expansion gives each its own independent code, so
        // this reserves two buffers rather than growing one arena entry unboundedly.
        Generate(
            "class Dev:\n" +
            "    @inline\n" +
            "    def __init__(self, n: uint16):\n" +
            "        buf: bytearray = bytearray(n)\n" +
            "        self.off = buf\n\n" +
            "n: uint16 = uint16(GPIOR0.value)\n" +
            "a: Dev = Dev(n)\n" +
            "b: Dev = Dev(n)\n");
    }

    // ------------------------------------------------------------------
    // MemoryError is a real builtin exception code.
    // ------------------------------------------------------------------

    [Fact]
    public void MemoryErrorIsCatchable()
    {
        // Compiles at all: `except MemoryError:` needs no import, exactly like ValueError.
        // Module level, not a loop -- the once rule is orthogonal to this and must not
        // itself be why this compiles or fails.
        Generate(
            "n: uint16 = 5\n" +
            "x: uint8 = 0\n" +
            "try:\n" +
            "    buf: bytearray = bytearray(n)\n" +
            "    x = buf[0]\n" +
            "except MemoryError:\n" +
            "    x = 0\n");
    }

    // ------------------------------------------------------------------
    // self.field = bytearray(n): construction (PyMCU#392 made the field-assignment
    // target reach the bytearray-aware path at all) and indexing the result (PyMCU#418:
    // a field holding an arena buffer was never marked as one at the field-write site,
    // so indexing it fell through to the generic bit-index fallback and silently
    // compiled to a bit operation instead of a byte access -- fixed here, not just
    // documented).
    // ------------------------------------------------------------------

    [Fact]
    public void RuntimeSizedFieldAssignmentAllocatesFromTheArena()
    {
        var program = Generate(
            "class Dev:\n" +
            "    @inline\n" +
            "    def __init__(self, n: uint16):\n" +
            "        self.buf = bytearray(n)\n\n" +
            "n: uint16 = uint16(GPIOR0.value) + 3\n" +
            "d: Dev = Dev(n)\n");

        var main = Assert.Single(program.Functions, f => f.Name == "main");
        // alloc()'s own MemoryError bounds check is present, proving the once rule
        // accepted the allocation and it was actually inlined -- same evidence as the
        // local-variable case (LocalInsideAModuleLevelConstructedInlineInitIsAccepted).
        Assert.Contains(main.Body, i => i is SignalError);
    }

    [Fact]
    public void IndexingAndLenOnAnArenaFieldWorkThroughMethods()
    {
        // PyMCU#418 regression: before the fix, this raised "runtime bit index is only
        // supported on a chip register" from inside write8()'s own inlined body (the
        // field write landed in a flattened name TryResolveArenaBuffer had never heard
        // of). poke()/peek() are themselves separate @inline methods, so this also
        // covers indexing two @inline levels deep through a field (module -> poke/peek
        // -> write8/read8), the same nesting depth #415 was originally, and wrongly,
        // thought to still refuse.
        var program = Generate(
            "class Dev:\n" +
            "    @inline\n" +
            "    def __init__(self, n: uint16):\n" +
            "        self.buf = bytearray(n)\n\n" +
            "    @inline\n" +
            "    def poke(self, i: uint16, v: uint8) -> None:\n" +
            "        self.buf[i] = v\n\n" +
            "    @inline\n" +
            "    def peek(self, i: uint16) -> uint8:\n" +
            "        return self.buf[i]\n\n" +
            "    @inline\n" +
            "    def size(self) -> uint16:\n" +
            "        return len(self.buf)\n\n" +
            "n: uint16 = uint16(GPIOR0.value) + 3\n" +
            "d: Dev = Dev(n)\n" +
            "d.poke(0, 11)\n" +
            "x: uint8 = d.peek(0)\n" +
            "sz: uint16 = d.size()\n");

        var main = Assert.Single(program.Functions, f => f.Name == "main");
        Assert.Contains(main.Body, i => i is ArrayStore st && st.ArrayName == "_arena");
        Assert.Contains(main.Body, i => i is ArrayLoad ld && ld.ArrayName == "_arena");
    }

    [Fact]
    public void ClassDictIndexingCoexistsWithArenaBuffers()
    {
        // `cls.string[k]` is a compile-time class-dict access, not an instance field --
        // and `cls` is a pseudo-receiver that resolves to no variable. Before
        // TryResolveArenaBufferField skipped class receivers, probing it evaluated the
        // name and raised "name 'cls' is not defined" whenever ANY arena buffer was
        // registered in the program (with none, the probe never ran). This is
        // ClassMethodCvTests' scenario with an arena buffer present.
        var program = Generate(
            "class CV:\n" +
            "    @classmethod\n" +
            "    def add_values(cls, value_tuples):\n" +
            "        cls.string = {}\n" +
            "        for value_tuple in value_tuples:\n" +
            "            name, value, string = value_tuple\n" +
            "            setattr(cls, name, value)\n" +
            "            cls.string[value] = string\n\n" +
            "class Mode(CV):\n" +
            "    pass\n\n" +
            "Mode.add_values(((\"LOW\", 0xE0, \"lo\"),))\n" +
            "n: uint16 = uint16(GPIOR0.value) + 3\n" +
            "buf: bytearray = bytearray(n)\n" +
            "buf[0] = Mode.LOW\n");

        var main = Assert.Single(program.Functions, f => f.Name == "main");
        Assert.Contains(main.Body, i => i is ArrayStore st && st.ArrayName == "_arena");
    }
}

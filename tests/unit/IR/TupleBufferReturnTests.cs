using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `rom, diff = f(...)` where `f` writes a bytearray and returns `(buf, scalar)`: the
/// tuple unpack used to give the caller's `rom` its own storage, so every `rom[k]`
/// read back whatever that storage held -- never the bytes the callee wrote. CPython
/// sees a different object per call: a buffer born inside the callee's frame is one
/// cell every call shares, so the target takes its bytes home as its OWN storage;
/// a buffer that outlives the call (member storage, module globals, a forwarded
/// parameter) still aliases it, which is the object identity CPython gives it.
///
/// Covered in both tuple positions, for a force-inlined ordinary `def` and for an
/// explicit `@inline`, because those are the two spellings a driver uses.
/// </summary>
public class TupleBufferReturnTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    /// <summary>Array names any function's body loads from or stores into.</summary>
    private static List<string> ArraysTouched(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body)
            .SelectMany(i => i switch
            {
                ArrayLoad al => new[] { al.ArrayName },
                ArrayStore st => new[] { st.ArrayName },
                _ => Array.Empty<string>(),
            }).Distinct().ToList();

    private const string Fill =
        "def f(n: uint8):\n" +
        "    buf = bytearray(3)\n" +
        "    buf[0] = n\n" +
        "    buf[1] = 40\n" +
        "    buf[2] = 8\n";

    [Fact]
    public void BufferInFirstTuplePosition_CallerReadsTheReturnedStorage()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n\n" + Fill +
            "    return buf, n + 5\n\n" +
            "rom, diff = f(30)\n" +
            "x = rom[1]\n" +
            "y = rom[2]\n" +
            "z = diff\n");

        // The reads must name `rom`'s own storage, filled from the callee's bytes
        // at the unpack. Without the copy-home the target aliased the callee's one
        // shared cell and a second call's write would overwrite `rom` underneath.
        var loads = ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>().ToList();
        Assert.Contains(loads, l => l.Index is Constant { Value: 1 } && l.ArrayName == "main.rom");
        Assert.Contains(loads, l => l.Index is Constant { Value: 2 } && l.ArrayName == "main.rom");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>(),
            s => s.ArrayName == "main.rom");
    }

    [Fact]
    public void BufferInSecondTuplePosition_CallerReadsTheReturnedStorage()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n\n" + Fill +
            "    return n + 5, buf\n\n" +
            "diff, rom = f(30)\n" +
            "x = rom[1]\n" +
            "z = diff\n");

        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>(),
            l => l.Index is Constant { Value: 1 } && l.ArrayName == "main.rom");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>(),
            s => s.ArrayName == "main.rom");
    }

    [Fact]
    public void BufferReturn_FromAnExplicitInline_CopiesTheSameWay()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n\n" +
            "@inline\n" + Fill +
            "    return buf, n + 5\n\n" +
            "rom, diff = f(30)\n" +
            "x = rom[1]\n");

        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>(),
            l => l.Index is Constant { Value: 1 } && l.ArrayName == "main.rom");
    }

    [Fact]
    public void TwoCallsToTheSameCalleeLocalBuffer_GetTheirOwnStorage()
    {
        // `a` and `b` are different objects in CPython: f's local buf is one cell
        // every call shares, so aliasing it made both names read the last write.
        // A callee-local buffer now copies its bytes into the target's own array;
        // a buffer that outlives the call still aliases (member storage, module
        // globals, forwarded parameters).
        var ir = Gen(
            "from pymcu.types import uint8\n\n" + Fill +
            "    return buf, 0\n\n" +
            "a, x = f(1)\n" +
            "b, y = f(2)\n" +
            "p = a[0]\n" +
            "q = b[0]\n");

        var loads = ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>()
            .Where(l => l.Index is Constant { Value: 0 })
            .Select(l => l.ArrayName).ToList();
        Assert.Contains(loads, n => n == "main.a");
        Assert.Contains(loads, n => n == "main.b");
        // And each target's storage was actually filled from the callee's bytes.
        var stores = ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Select(s => s.ArrayName).ToList();
        Assert.Contains(stores, n => n == "main.a");
        Assert.Contains(stores, n => n == "main.b");
    }

    [Fact]
    public void ScalarTupleElementsStillGetTheirOwnSlots()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n\n" + Fill +
            "    return buf, n + 5\n\n" +
            "rom, diff = f(30)\n" +
            "z = diff\n");

        // The scalar element still crosses as an ordinary copy -- only the buffer
        // element aliases.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Dst is Variable { Name: "main.diff" });
    }

    /// <summary>Array names any function's body stores into.</summary>
    private static List<string> ArraysStored(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Select(s => s.ArrayName).Distinct().ToList();

    [Fact]
    public void ForwardedParameterBufferReturnedInTuple_AliasesTheCallerStorage()
    {
        // `b is a` in CPython: ident hands the caller's own buffer straight back,
        // so a write through b lands on a. Classifying `outer.a` as callee-local
        // -- it carries the enclosing function's `outer.` prefix, not the
        // expansion's -- copied it to `outer.b` instead, and `b[0] = 9` never
        // reached `a`.
        var ir = Gen(
            "from pymcu.types import uint8\n\n" +
            "def ident(buf: bytearray):\n" +
            "    return buf, 0\n\n" +
            "def outer() -> uint8:\n" +
            "    a = bytearray(1)\n" +
            "    a[0] = 7\n" +
            "    b, unused = ident(a)\n" +
            "    b[0] = 9\n" +
            "    return a[0]\n\n" +
            "x = outer()\n");

        // `b[0] = 9` stores straight onto `outer.a`; there is no `outer.b` array
        // at all -- no copy home, because there is nothing to copy home to.
        Assert.Contains(ArraysStored(ir), n => n == "outer.a");
        Assert.DoesNotContain(ArraysStored(ir), n => n == "outer.b");
    }

    [Fact]
    public void TheSameBufferInTwoTupleSlots_BindsBothTargetsToOneStorage()
    {
        // `a is b` in CPython: `return buf, buf` hands the same object twice. The
        // callee-local copy still runs -- the bytes leave the frame -- but the
        // second slot must bind the FIRST target's storage, not a second copy,
        // or `a[0] = 9` is invisible through `b`.
        var ir = Gen(
            "from pymcu.types import uint8\n\n" +
            "def f():\n" +
            "    buf = bytearray(1)\n" +
            "    buf[0] = 1\n" +
            "    return buf, buf\n\n" +
            "a, b = f()\n" +
            "a[0] = 9\n" +
            "q = b[0]\n");

        // One copy home, onto `a`'s storage; `b` aliases it -- `b[0]` loads the
        // array `a[0] = 9` wrote, and no `main.b` array ever exists.
        Assert.Contains(ArraysStored(ir), n => n == "main.a");
        Assert.DoesNotContain(ArraysStored(ir), n => n == "main.b");
        Assert.Contains(ArraysTouched(ir), n => n == "main.a");
        Assert.DoesNotContain(ArraysTouched(ir), n => n == "main.b");
    }

    [Fact]
    public void AnnotatedCalleeLocalArrayReturnedInTuple_CopiesIntoEachCallerTarget()
    {
        // `buf: uint8[1]` is born inside the callee, so each unpack target takes
        // its bytes home: CPython gives `a` and `b` two different objects and the
        // second call cannot overwrite the first. The annotated spelling used to
        // file under `f.buf` -- the caller's spelling, not the expansion's
        // `inline1.f.buf` -- so the locality check saw outliving storage and
        // aliased: both targets named the one shared cell and `a[0]` read the
        // second call's 2 where CPython prints 1.
        var ir = Gen(
            "from pymcu.types import uint8\n\n" +
            "def f(n: uint8):\n" +
            "    buf: uint8[1] = [n]\n" +
            "    return buf, 0\n\n" +
            "a, x = f(1)\n" +
            "b, y = f(2)\n" +
            "u = a[0]\n" +
            "v = b[0]\n");

        // One copy home per call, onto that target's own slot. Both expansions
        // reuse the same `inline1.f.` frame name -- depth, not a call counter --
        // which is exactly why the copy matters: the frame slot is one shared
        // cell, so each target must hold its own bytes. Aliased -- as `f.buf`
        // was -- neither Copy exists at all and both names read that cell.
        var copies = ir.Functions.SelectMany(fn => fn.Body).OfType<Copy>().ToList();
        Assert.Contains(copies, c =>
            c.Src is Variable { Name: "inline1.f.buf__0" }
            && c.Dst is Variable { Name: "main.a__0" });
        Assert.Contains(copies, c =>
            c.Src is Variable { Name: "inline1.f.buf__0" }
            && c.Dst is Variable { Name: "main.b__0" });
    }
}

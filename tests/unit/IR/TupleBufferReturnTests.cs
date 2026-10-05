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
/// sees the same object; PyMCU must alias the destination onto the returned buffer,
/// the way a single-value `b = f()` buffer return already does.
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

        // The load must name the callee's buffer storage, not a fresh `rom` array
        // that the callee never wrote. Without the alias the unpack emitted copies
        // into `rom`'s own slots and `rom[1]` read those.
        var loads = ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>()
            .Where(l => l.Index is Constant { Value: 1 or 2 }).ToList();
        loads.Should().NotBeEmpty(because: "rom[1] and rom[2] are constant-indexed reads");
        loads.Should().OnlyContain(l => l.ArrayName.EndsWith(".buf"),
            because: "the destination aliases the callee's buffer storage");
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
            l => l.Index is Constant { Value: 1 } && l.ArrayName.EndsWith(".buf"));
    }

    [Fact]
    public void BufferReturn_FromAnExplicitInline_AliasesTheSameWay()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n\n" +
            "@inline\n" + Fill +
            "    return buf, n + 5\n\n" +
            "rom, diff = f(30)\n" +
            "x = rom[1]\n");

        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>(),
            l => l.Index is Constant { Value: 1 } && l.ArrayName.EndsWith(".buf"));
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
}

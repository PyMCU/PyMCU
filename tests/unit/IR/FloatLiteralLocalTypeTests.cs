using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Regression tests for the TYPE given to an unannotated local (PyMCU#216).
///
/// FloatConstant is a sibling of Constant rather than a subclass, so no branch in the type
/// choice matched a bare float literal and the local kept the UINT8 it is initialised to.
/// `f = 1.5` was an 8-bit integer from that point on: it printed 1, and `f = 300.5` printed 44,
/// truncated AND wrapped.
///
/// The observable a user sees is a printed float, so the executable oracle is the AVR fixture
/// float-local-not-truncated. These assert the same thing one step earlier, on the type in the
/// IR, and run in milliseconds instead of in a four-minute suite. That is the whole reason they
/// exist: the defect was found and fixed twice through integration runs, and this is the check
/// that would have failed first.
/// </summary>
public class FloatLiteralLocalTypeTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig());
    }

    private static DataType? TypeOf(ProgramIR ir, string name) =>
        ir.Functions.SelectMany(f => f.Body)
            .Select(i => i switch
            {
                Copy { Dst: Variable v } when v.Name == name => (DataType?)v.Type,
                Unary { Dst: Variable v } when v.Name == name => v.Type,
                Binary { Dst: Variable v } when v.Name == name => v.Type,
                _ => null,
            })
            .FirstOrDefault(t => t != null);

    private const string Preamble =
        "from pymcu.types import uint8, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n";

    // DISCRIMINATING. Before the fix this was UINT8.
    [Fact]
    public void AnUnannotatedLocalBoundToAFloatLiteralIsAFloat()
    {
        var ir = Gen(Preamble + "def main():\n    f = 1.5\n    G.value = uint8(f)\n");
        Assert.Equal(DataType.FLOAT, TypeOf(ir, "main.f"));
    }

    // DISCRIMINATING, and the row the report did not have: 300 does not fit in a byte, so the
    // wrong answer was not the truncated integer but that integer wrapped, 44.
    [Fact]
    public void AValueAboveAByteIsStillAFloat()
    {
        var ir = Gen(Preamble + "def main():\n    f = 300.5\n    G.value = uint8(f)\n");
        Assert.Equal(DataType.FLOAT, TypeOf(ir, "main.f"));
    }

    // INVARIANT. `-1.5` was never affected: negation makes the value a typed Temporary that the
    // branch above the gap already caught. Present so the fix cannot regress the shape that
    // happened to work, and because it is why the trigger is a bare POSITIVE float literal.
    [Fact]
    public void ANegatedFloatLiteralWasAlreadyAFloat()
    {
        var ir = Gen(Preamble + "def main():\n    f = -1.5\n    G.value = uint8(f)\n");
        Assert.Equal(DataType.FLOAT, TypeOf(ir, "main.f"));
    }

    // INVARIANT, the control a careless fix breaks: an integer local must not become a float.
    [Fact]
    public void AnIntegerLiteralLocalIsNotAFloat()
    {
        var ir = Gen(Preamble + "def main():\n    n = 7\n    G.value = uint8(n)\n");
        Assert.NotEqual(DataType.FLOAT, TypeOf(ir, "main.n"));
    }
}

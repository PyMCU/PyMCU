using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Turns the IR verifier on for the whole unit-test process: every
/// IRGenerator.Generate call in every test then runs it, so a regression in a
/// test nobody wrote for the verifier still surfaces as a warning.
/// </summary>
internal static class IRVerifierSuiteEnable
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Enable()
        => Environment.SetEnvironmentVariable("PYMCU_VERIFY_IR", "1");
}

/// <summary>
/// The between-passes IR verifier (<see cref="Verifier"/>, PYMCU_VERIFY_IR). Each test
/// builds the smallest IR that violates one invariant, or compiles a known-good program
/// and asserts the verifier stays silent on it.
/// </summary>
public class IRVerifierTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(),
                                          new DeviceConfig { Arch = "avr" });
    }

    private static IEnumerable<string> Checks(ProgramIR ir)
        => Verifier.Verify(ir).Select(v => v.Check);

    private static ProgramIR IrWith(Function f)
        => new() { Functions = { f } };

    [Fact]
    public void AHealthyProgram_HasNoViolations()
    {
        var ir = Gen(
            "xs: list[uint8] = []\n" +
            "def fill(buf: bytearray, n: uint8):\n" +
            "    i: uint8 = 0\n" +
            "    while i < n:\n" +
            "        buf[i] = i\n" +
            "        i = i + 1\n" +
            "def main():\n" +
            "    xs.append(3)\n" +
            "    b = bytearray(4)\n" +
            "    fill(b, 4)\n");

        Verifier.Verify(ir).Should().BeEmpty();
    }

    [Fact]
    public void OneStorageKey_FlagsAQualifiedTwinOfADeclaredGlobal()
    {
        // main.xs written where xs is the declared global: the module-list split.
        var ir = IrWith(new Function
        {
            Name = "main",
            Body =
            {
                new Copy(new Constant(1), new Variable("main.xs")),
            }
        });
        ir.Globals.Add(new Variable("xs", DataType.GC_REF));

        Checks(ir).Should().Contain("one-storage-key");
    }

    [Fact]
    public void OneStorageKey_AllowsAFunctionLocalShadowingAGlobal()
    {
        // `def f(): data: int = 3` while `data` is a module-level array is a
        // legal shadow -- only a module-init frame can never hold such a local.
        var ir = IrWith(new Function
        {
            Name = "shadowed",
            Body =
            {
                new Copy(new Constant(3), new Variable("shadowed.data")),
                new Return(new Variable("shadowed.data")),
            }
        });
        ir.GlobalArrays["data"] = 2;

        Checks(ir).Should().NotContain("one-storage-key");
    }

    [Fact]
    public void BufferParamScalar_FlagsAScalarArgumentForABufferParameter()
    {
        var ir = IrWith(new Function
        {
            Name = "fill",
            Params = { "fill.buf", "fill.n" },
            Body =
            {
                new BytearrayStore("fill.buf", new Variable("fill.i"),
                    new Variable("fill.i")),
            }
        });
        ir.Functions.Add(new Function
        {
            Name = "main",
            Body =
            {
                // The #487 shape: the callee reads fill.buf as a pointer, but the
                // caller hands it a one-byte value.
                new Call("fill",
                    new List<Val> { new Variable("main.b_0"), new Constant(4) },
                    new NoneVal()),
            }
        });

        Checks(ir).Should().Contain("buffer-param-scalar");
    }

    [Fact]
    public void BufferParamScalar_AcceptsAnArrayBase()
    {
        var ir = IrWith(new Function
        {
            Name = "fill",
            Params = { "fill.buf" },
            Body =
            {
                new BytearrayStore("fill.buf", new Constant(0), new Constant(1)),
            }
        });
        ir.Functions.Add(new Function
        {
            Name = "main",
            Body =
            {
                new Call("fill", new List<Val> { new ArrayBase("main.b") },
                    new NoneVal()),
            }
        });
        ir.GlobalArrays["main.b"] = 4;

        Checks(ir).Should().NotContain("buffer-param-scalar");
    }

    [Fact]
    public void WriteExceedsSlot_FlagsAConstantPastTheDestinationWidth()
    {
        var ir = IrWith(new Function
        {
            Name = "main",
            Body =
            {
                new Copy(new Constant(300), new Variable("main.x")),
            }
        });

        Checks(ir).Should().Contain("write-exceeds-slot");
    }

    [Fact]
    public void ReadNeverWritten_FlagsABitCheckOnAnUnwrittenSlot()
    {
        var ir = IrWith(new Function
        {
            Name = "main",
            Body =
            {
                new BitCheck(new Variable("main.flags"), 0,
                    new Temporary("tmp_0")),
            }
        });

        Checks(ir).Should().Contain("read-never-written");
    }

    [Fact]
    public void IndexWidth_FlagsAByteIndexIntoAWideAddressedArray()
    {
        var ir = IrWith(new Function
        {
            Name = "main",
            Body =
            {
                new ArrayStore("main.big", new Variable("main.i"),
                    new Constant(0), DataType.UINT8, 300),
            }
        });
        ir.GlobalArrays["main.big"] = 300;

        Checks(ir).Should().Contain("index-width");
    }

    [Fact]
    public void TagContract_FlagsATaggedReturnWithNoTagAndACallWithNoTagDst()
    {
        var ir = IrWith(new Function
        {
            Name = "maybe",
            ReturnMembers = new List<string> { "uint8", "None" },
            ReturnType = DataType.UINT8,
            Body =
            {
                new Return(new Constant(1)), // no Tag
            }
        });
        ir.Functions.Add(new Function
        {
            Name = "main",
            Body =
            {
                new Call("maybe", new List<Val>(),
                    new Variable("main.r")), // no TagDst
            }
        });

        Checks(ir).Should().Contain("tag-contract");
    }

    [Fact]
    public void JumpTarget_FlagsABranchToAnUndefinedLabel()
    {
        var ir = IrWith(new Function
        {
            Name = "main",
            Body =
            {
                new JumpIfZero(new Variable("main.x"), "nowhere"),
            }
        });

        Checks(ir).Should().Contain("jump-target");
    }

    [Fact]
    public void FlashNameResolves_FlagsAnArrayLoadFlashWithNoTable()
    {
        var ir = IrWith(new Function
        {
            Name = "main",
            Body =
            {
                new ArrayLoadFlash("ghost_table", new Constant(0),
                    new Temporary("tmp_0")),
            }
        });

        Checks(ir).Should().Contain("flash-name-resolves");
    }
}

using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A CPython builtin that is a type this compiler stores is a valid annotation:
/// <c>memoryview()</c> as a call and <c>-&gt; memoryview</c> as a return are the
/// same name. Adafruit pca9685 annotates <c>_get_buffer(...) -&gt; memoryview</c>,
/// which was refused as unknown because the annotation check only consulted the
/// scalar-width list.
///
/// Builtin functions (<c>print</c>) are not types. Builtin types without a
/// representation here (<c>complex</c>) stay refused.
/// </summary>
public class BuiltinTypeAnnotationTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    [Theory]
    [MemberData(nameof(RepresentedTypeNames))]
    public void ARepresentedBuiltinType_IsAValidReturnAnnotation(string typeName)
    {
        var act = () => Gen(
            "from pymcu.types import uint8\n" +
            $"def f(x: uint8) -> {typeName}:\n" +
            "    raise NotImplementedError\n");

        act.Should().NotThrow<CompilerError>(
            because: $"{typeName} is a CPython builtin type this compiler stores, so the annotation is that type");
    }

    public static IEnumerable<object[]> RepresentedTypeNames() =>
        PythonBuiltinNames.RepresentedTypes.Select(t => new object[] { t });

    [Fact]
    public void ABuiltinFunction_IsNotATypeAnnotation()
    {
        var act = () => Gen(
            "def f(x: print) -> uint8:\n" +
            "    return 1\n");

        act.Should().Throw<CompilerError>()
            .Which.Message.Should().Contain("unknown type 'print'",
                because: "print is a builtin function, not a type this compiler stores");
    }

    [Fact]
    public void IndexingAnAnnotatedMemoryviewReturn_ReadsTheUnderlyingBuffer()
    {
        var ir = Optimizer.Optimize(Gen(
            "from pymcu.types import uint8\n" +
            "out = bytearray([0])\n" +
            "buf = bytearray([3, 4, 5])\n" +
            "def view_of(b: bytearray) -> memoryview:\n" +
            "    return memoryview(b)\n" +
            "v = view_of(buf)\n" +
            "out[0] = v[0]\n"));

        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant { Value: 0 })
            .Select(s => s.Src)
            .Should().Contain(new Constant(3),
                because: "v[0] through a -> memoryview return is the first byte of buf");
    }
}

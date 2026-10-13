using PyMCU.Frontend;
using Xunit;

namespace PyMCU.Tests.Frontend;

/// <summary>
/// PEP 695 type parameters (#521). The list sits in a FIELD of the function or class node that the
/// CPython front end never read, so `def f[T, T]` compiled (CPython rejects it) and a `T` in the
/// body resolved to the module's T. The hand-written parser stopped at the bracket. Both refuse the
/// bracket now, with the same sentence.
/// </summary>
public class TypeParamsParityTests
{
    private static string? Csharp(string src) =>
        Record.Exception(() => new Parser(new Lexer(src).Tokenize()).ParseProgram())?.Message;

    private static string? CPython(string src) =>
        Record.Exception(() => PythonAstReader.ParseSource(src, "main.py"))?.Message;

    private static void BothRefuse(string src, string sentence)
    {
        var cs = Csharp(src);
        var py = CPython(src);
        Assert.True(cs != null && cs.Contains(sentence), "hand-written parser: " + (cs ?? "accepted it"));
        Assert.True(py != null && py.Contains(sentence), "CPython front end: " + (py ?? "accepted it"));
    }

    private const string TypeParams = "PEP 695 type parameters are not supported";

    [Fact]
    public void AGenericFunctionIsRefused() =>
        BothRefuse("def f[T](a: T) -> T:\n    return a\n", TypeParams);

    [Fact]
    public void ADuplicateTypeParameterIsRefusedNotCompiled() =>
        BothRefuse("def f[T, T](n: uint8) -> uint8:\n    return n\n", TypeParams);

    [Fact]
    public void ABoundOrVariadicTypeParameterIsRefused()
    {
        BothRefuse("def f[T: uint8](n: uint8) -> uint8:\n    return n\n", TypeParams);
        BothRefuse("def f[*Ts](n: uint8) -> uint8:\n    return n\n", TypeParams);
        BothRefuse("def f[**P](n: uint8) -> uint8:\n    return n\n", TypeParams);
    }

    [Fact]
    public void AGenericClassIsRefused() =>
        BothRefuse("class Box[T]:\n    pass\n", TypeParams);

    [Fact]
    public void AClassWithBasesAndTypeParametersIsRefused() =>
        BothRefuse("class Box[T](Base):\n    pass\n", TypeParams);

    [Fact]
    public void APlainFunctionAndClassStillParse()
    {
        const string src = "def f(a: uint8) -> uint8:\n    return a\n\nclass C(B):\n    pass\n";
        Assert.Null(Csharp(src));
        Assert.Null(CPython(src));
    }
}

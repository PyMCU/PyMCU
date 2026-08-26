using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A generator lowers to a class named after the function, so every call of the generator
/// protocol resolved to a symbol built from that name: `g.send(1)` came back as "call to
/// undefined function 'gen_send'", and `g.__next__()` as 'gen___next__'. Both name something
/// the program never wrote, and the "(typo, or a missing import?)" tail points at neither of
/// the two things that are actually true -- the method is real Python, and it is the feature
/// that is missing.
///
/// The discriminating assertion in each test is that the protocol method is named and the
/// reason given. The `DoesNotContain` on the mangled symbol is the invariant.
/// </summary>
public class GeneratorProtocolDiagnosticTests
{
    private const string Generator = """
        from pymcu.types import uint8

        def gen():
            i: uint8 = 0
            while i < 3:
                yield i
                i = i + 1

        """;

    private static string ErrorFor(string body)
    {
        var ast = new Parser(new Lexer(Generator + body).Tokenize()).ParseProgram();
        AsyncTransform.TransformProgram(ast);
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(
            () => new IRGenerator().Generate(
                ast, new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" }));
        return ex.Message;
    }

    private const string Send = """
        def main():
            g = gen()
            x: uint8 = g.send(1)
            print(x)
        """;

    [Fact]
    public void Send_NamesTheMethod_NotAMangledSymbol()
    {
        var msg = ErrorFor(Send);

        Assert.Contains("send", msg);
        Assert.DoesNotContain("gen_send", msg);
        Assert.DoesNotContain("typo", msg);
    }

    [Fact]
    public void Send_GivesTheReason_AndSaysWhatIsSupported()
    {
        var msg = ErrorFor(Send);

        // The reason a value cannot go in: `x = yield v` is a statement here, not an
        // expression, so there is nowhere for the sent value to arrive.
        Assert.Contains("yield", msg);
        Assert.Contains("for", msg);
    }

    // `next(g)` is the form the reported program used. The generic builtin refusal said
    // "Loop over the sequence itself", which is advice for a list: a generator is not a
    // sequence and there is nothing else to loop over.

    [Fact]
    public void Next_OnAGenerator_NamesTheGenerator_NotASequence()
    {
        var msg = ErrorFor("""
            def main():
                g = gen()
                x: uint8 = next(g)
                print(x)
            """);

        Assert.Contains("generator", msg);
        Assert.DoesNotContain("Loop over the sequence itself", msg);
    }

    [Fact]
    public void Next_OnAGenerator_PointsAtTheForThatWorks()
    {
        var msg = ErrorFor("""
            def main():
                g = gen()
                x: uint8 = next(g)
                print(x)
            """);

        Assert.Contains("for v in gen(", msg);
    }

    [Fact]
    public void Close_NamesTheMethod()
    {
        var msg = ErrorFor("""
            def main():
                g = gen()
                g.close()
            """);

        Assert.Contains("close", msg);
        Assert.DoesNotContain("gen_close", msg);
    }

    [Fact]
    public void Throw_NamesTheMethod()
    {
        var msg = ErrorFor("""
            def main():
                g = gen()
                g.throw(1)
            """);

        Assert.Contains("throw", msg);
        Assert.DoesNotContain("gen_throw", msg);
    }

    [Fact]
    public void DunderNext_NamesTheMethod()
    {
        var msg = ErrorFor("""
            def main():
                g = gen()
                x: uint8 = g.__next__()
                print(x)
            """);

        Assert.Contains("__next__", msg);
        Assert.DoesNotContain("gen___next__", msg);
    }
}

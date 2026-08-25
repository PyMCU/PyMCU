using PyMCU.Frontend;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Which spellings of the asyncio import satisfy `async def` (PyMCU#113).
///
/// `uasyncio` is the same module under the name most of the existing MicroPython code base
/// uses, and the one every pre-1.13 tutorial shows. It was not recognised here, so
/// `import uasyncio as asyncio` resolved the module and then failed with "this module uses
/// `async def` but never imports asyncio" -- pointing at an import the program had already
/// written, under the other name.
/// </summary>
public class AsyncioAliasTests
{
    private static void Transform(string src)
    {
        var ast = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        AsyncTransform.TransformProgram(ast);
    }

    private const string Coroutine =
        "async def blink():\n" +
        "    await asyncio.sleep_ms(100)\n";

    [Theory]
    [InlineData("import asyncio\n")]
    [InlineData("import uasyncio as asyncio\n")]
    [InlineData("import pymcu.asyncio as asyncio\n")]
    [InlineData("from pymcu import asyncio\n")]
    public void EverySpellingOfTheImport_SatisfiesAsyncDef(string import)
    {
        Transform(import + Coroutine);
    }

    [Fact]
    public void TheBareUSpelling_SatisfiesItToo()
    {
        Transform(
            "import uasyncio\n" +
            "async def blink():\n" +
            "    await uasyncio.sleep_ms(100)\n");
    }

    [Fact]
    public void NoImportAtAll_IsStillRefusedAndSaysWhatToAdd()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Transform(Coroutine));

        Assert.Contains("never imports asyncio", ex.Message);
        Assert.Contains("import asyncio", ex.Message);
    }
}

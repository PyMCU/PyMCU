using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// postb1 smoke finding. An entry file holding only pure declarations -- the
/// shape `pymcu install`'s verify build writes (`import adafruit_dht` alone) --
/// synthesized no `main`: imports are top-level pure declarations, so the
/// statements list was empty and the whole synthesis sat behind a
/// `Count > 0` guard. The CRT still calls `main`, and the link failed with
/// "undefined reference to `main'" on a program that had compiled clean.
/// </summary>
public class ImportOnlyProgramTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void AnEntryFileWithOnlyImportsStillEmitsMain()
    {
        var ir = Gen("import dht\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void AnEntryFileWithOnlyFunctionDefinitionStillEmitsMain()
    {
        var ir = Gen("def f(x):\n    return x\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void AnEntryFileWithOnlyClassDefinitionStillEmitsMain()
    {
        var ir = Gen("class A:\n    pass\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void AnEntryFileWithImportsAndFunctionStillEmitsMain()
    {
        var ir = Gen("import dht\n\ndef f(x):\n    return x\n");
        Assert.Contains(ir.Functions, f => f.Name == "main");
    }
}

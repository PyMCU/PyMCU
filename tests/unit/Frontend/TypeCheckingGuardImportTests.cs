using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.Infrastructure;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#480 / #481. The nested Adafruit TYPE_CHECKING guard:
/// <c>try: from pwmio import PWMOut / except NotImplementedError: from
/// circuitpython_typing.pwmio import PWMOut</c> inside an outer
/// <c>except ImportError</c>. The inner except used to drop the resolved
/// PWMOut (#480) or load the stub package even when pwmio was there (#481).
/// </summary>
[Trait("Issue", "480")]
public class TypeCheckingGuardImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "pymcu-480-" + Guid.NewGuid().ToString("N")[..12]);

    public TypeCheckingGuardImportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static DeviceConfig Avr() => new() { Arch = "avr", Chip = "atmega328p", Frequency = 16000000 };

    private static ImportStmt Imp(string module, params string[] symbols) =>
        new(module, new List<string>(symbols));

    private void Write(string name, string source) =>
        File.WriteAllText(Path.Combine(_root, name), source);

    [Fact]
    public void ExceptNotImplementedError_KeepsTheBodyImport()
    {
        var body = Imp("pwmio", "PWMOut");
        var stub = Imp("circuitpython_typing.pwmio", "PWMOut");
        var inner = new TryStmt(
            new List<Statement> { body },
            new List<(string, List<Statement>)>
            {
                ("NotImplementedError", new List<Statement> { stub }),
            });
        var prog = new ProgramNode();
        prog.GlobalStatements.Add(inner);

        new ConditionalCompilator(Avr()).Process(prog);

        prog.Imports.Should().Contain(i => i.ModuleName == "pwmio" && i.Symbols.Contains("PWMOut"),
            because: "except NotImplementedError does not catch ImportError, so the body import stays");
        prog.Imports.Should().NotContain(i => i.ModuleName.Contains("circuitpython_typing"),
            because: "the stub handler never runs when the body import is the branch taken");
    }

    [Fact]
    public void NestedAdafruitGuard_KeepsPwmOut_AndDoesNotTakeTheStub()
    {
        var typing = Imp("typing", "Optional");
        typing.IsOptional = true;
        var pwmio = Imp("pwmio", "PWMOut");
        var stub = Imp("circuitpython_typing.pwmio", "PWMOut");
        var inner = new TryStmt(
            new List<Statement> { pwmio },
            new List<(string, List<Statement>)>
            {
                ("NotImplementedError", new List<Statement> { stub }),
            });
        var outer = new TryStmt(
            new List<Statement> { typing, inner },
            new List<(string, List<Statement>)>
            {
                ("ImportError", new List<Statement>()),
            });
        var prog = new ProgramNode();
        prog.GlobalStatements.Add(outer);

        new ConditionalCompilator(Avr()).Process(prog);

        prog.Imports.Should().Contain(i => i.ModuleName == "pwmio",
            because: "the inner except NotImplementedError must not drop PWMOut (#480)");
        prog.Imports.Should().Contain(i => i.ModuleName == "typing",
            because: "the outer except ImportError still keeps the resolved typing import");
        prog.Imports.Should().NotContain(i => i.ModuleName.Contains("circuitpython_typing"),
            because: "pwmio resolved, so the stub fallback is not loaded (#481)");
    }

    [Fact]
    [Trait("Issue", "481")]
    public void ASuccessfulOptionalImport_DoesNotLoadTheExceptHandlerModule()
    {
        Write("pwmio.py", "class PWMOut:\n    pass\n");
        Write("main.py",
            "try:\n" +
            "    from pwmio import PWMOut\n" +
            "except ImportError:\n" +
            "    from circuitpython_typing.pwmio import PWMOut\n");

        var ctx = new CompilationContext(new CompilerOptions(
            FilePath: Path.Combine(_root, "main.py"), OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        ctx.IncludePaths.Clear();
        ctx.IncludePaths.Add(_root);
        ctx.RootAst = new Parser(new Lexer(File.ReadAllText(Path.Combine(_root, "main.py"))).Tokenize())
            .ParseProgram();

        var act = () => new DependencyGraphBuilder(new FileSystemModuleLoader())
            .Build(ctx.RootAst, ctx.Options.FilePath, ctx);

        act.Should().NotThrow(
            because: "the try body resolved pwmio, so the missing stub in the handler is never imported");
        ctx.NamedModules.Should().ContainKey("pwmio",
            because: "pwmio is the branch that ran");
        ctx.NamedModules.Should().NotContainKey("circuitpython_typing.pwmio",
            because: "CPython does not execute the except ImportError handler when the import succeeds");
    }

    [Fact]
    public void NestedGuard_LoadsPwmioThroughTheGraph_WithoutTheStubPackage()
    {
        Write("pwmio.py", "class PWMOut:\n    pass\n");
        Write("main.py",
            "try:\n" +
            "    from typing import Optional, Type\n" +
            "    try:\n" +
            "        from pwmio import PWMOut\n" +
            "    except NotImplementedError:\n" +
            "        from circuitpython_typing.pwmio import PWMOut\n" +
            "except ImportError:\n" +
            "    pass\n");

        var ctx = new CompilationContext(new CompilerOptions(
            FilePath: Path.Combine(_root, "main.py"), OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        ctx.IncludePaths.Clear();
        ctx.IncludePaths.Add(_root);
        ctx.RootAst = new Parser(new Lexer(File.ReadAllText(Path.Combine(_root, "main.py"))).Tokenize())
            .ParseProgram();

        var act = () => new DependencyGraphBuilder(new FileSystemModuleLoader())
            .Build(ctx.RootAst, ctx.Options.FilePath, ctx);

        act.Should().NotThrow(
            because: "the inner except NotImplementedError must still extract from pwmio (#480)");
        ctx.NamedModules.Should().ContainKey("pwmio",
            because: "PWMOut is bound from the try body");
        ctx.NamedModules.Should().NotContainKey("circuitpython_typing.pwmio",
            because: "the stub is only the NotImplementedError handler, which does not run (#481)");
    }
}

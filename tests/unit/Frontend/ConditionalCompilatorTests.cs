using Xunit;
using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;

namespace PyMCU.UnitTests;

public class ConditionalCompilatorTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static DeviceConfig AvrConfig(string chip = "atmega328p", string arch = "avr", ulong freq = 16000000) =>
        new() { Chip = chip, Arch = arch, Frequency = freq };

    private static ProgramNode EmptyProgram() => new();

    private static ImportStmt MakeImport(string module, params string[] symbols) =>
        new(module, new List<string>(symbols), 0);

    private static Block MakeBlock(params Statement[] stmts)
    {
        var b = new Block();
        b.Statements.AddRange(stmts);
        return b;
    }

    private static CaseBranch MakeCaseBranch(Expression? pattern, params Statement[] stmts) =>
        new() { Pattern = pattern, Body = MakeBlock(stmts) };

    // -------------------------------------------------------------------------
    // ImportStmt stripping
    // -------------------------------------------------------------------------

    [Fact]
    public void Import_IsMovedToImportsList_AndRemovedFromGlobals()
    {
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(MakeImport("pymcu.avr", "DDRB"));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().BeEmpty();
        prog.Imports.Should().ContainSingle()
            .Which.ModuleName.Should().Be("pymcu.avr");
    }

    // -------------------------------------------------------------------------
    // if __CHIP__.arch == "avr"
    // -------------------------------------------------------------------------

    [Fact]
    public void If_TrueBranch_IsKept_WhenConditionMatches()
    {
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(new IfStmt(
            new BinaryExpr(new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"), BinaryOp.Equal, new StringLiteral("avr")),
            MakeBlock(new ReturnStmt(new IntegerLiteral(1)))));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().ContainSingle()
            .Which.Should().BeOfType<ReturnStmt>();
    }

    [Fact]
    public void If_FalseBranch_IsEliminated_WhenConditionDoesNotMatch()
    {
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(new IfStmt(
            new BinaryExpr(new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"), BinaryOp.Equal, new StringLiteral("pic")),
            MakeBlock(new ReturnStmt(new IntegerLiteral(99)))));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().BeEmpty();
    }

    [Fact]
    public void If_ElseBranch_IsUsed_WhenConditionIsFalse()
    {
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(new IfStmt(
            new BinaryExpr(new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"), BinaryOp.Equal, new StringLiteral("pic")),
            MakeBlock(new ReturnStmt(new IntegerLiteral(1))),
            elseBranch: MakeBlock(new ReturnStmt(new IntegerLiteral(2)))));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().ContainSingle()
            .Which.Should().BeOfType<ReturnStmt>()
            .Which.Value.Should().BeOfType<IntegerLiteral>()
            .Which.Value.Should().Be(2);
    }

    [Fact]
    public void If_ElifBranch_IsUsed_WhenFirstConditionFalseAndElifTrue()
    {
        var prog = EmptyProgram();
        var elifBranches = new List<(Expression, Statement)>
        {
            (new BinaryExpr(new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"), BinaryOp.Equal, new StringLiteral("avr")),
             MakeBlock(new ReturnStmt(new IntegerLiteral(3))))
        };
        prog.GlobalStatements.Add(new IfStmt(
            new BinaryExpr(new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"), BinaryOp.Equal, new StringLiteral("pic")),
            MakeBlock(new ReturnStmt(new IntegerLiteral(1))),
            elifBranches));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().ContainSingle()
            .Which.Should().BeOfType<ReturnStmt>()
            .Which.Value.Should().BeOfType<IntegerLiteral>()
            .Which.Value.Should().Be(3);
    }

    [Fact]
    public void If_ImportsInsideBranch_AreMovedToImportsList()
    {
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(new IfStmt(
            new BinaryExpr(new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"), BinaryOp.Equal, new StringLiteral("avr")),
            MakeBlock(MakeImport("pymcu.avr", "PORTB"))));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().BeEmpty();
        prog.Imports.Should().ContainSingle()
            .Which.ModuleName.Should().Be("pymcu.avr");
    }

    [Fact]
    public void If_NonCompileTimeCondition_IsLeftAsIs()
    {
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(new IfStmt(
            new BinaryExpr(new VariableExpr("some_var"), BinaryOp.Equal, new IntegerLiteral(1)),
            MakeBlock(new ReturnStmt(new IntegerLiteral(1)))));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().ContainSingle()
            .Which.Should().BeOfType<IfStmt>();
    }

    // -------------------------------------------------------------------------
    // __CHIP__ / __FREQ__
    // -------------------------------------------------------------------------

    [Fact]
    public void If_ChipName_MatchesDirectly()
    {
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(new IfStmt(
            new BinaryExpr(new VariableExpr("__CHIP__"), BinaryOp.Equal, new StringLiteral("atmega328p")),
            MakeBlock(new ReturnStmt(new IntegerLiteral(7)))));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().ContainSingle()
            .Which.Should().BeOfType<ReturnStmt>();
    }

    [Fact]
    public void If_FrequencyCondition_MatchesCorrectly()
    {
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(new IfStmt(
            new BinaryExpr(new VariableExpr("__FREQ__"), BinaryOp.Equal, new IntegerLiteral(16000000)),
            MakeBlock(new ReturnStmt(new IntegerLiteral(5)))));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().ContainSingle()
            .Which.Should().BeOfType<ReturnStmt>();
    }

    // -------------------------------------------------------------------------
    // match __CHIP__.arch:
    // -------------------------------------------------------------------------

    [Fact]
    public void Match_StringLiteral_MatchingArm_IsKept()
    {
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(new MatchStmt(
            new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"),
            new List<CaseBranch> { MakeCaseBranch(new StringLiteral("avr"), new ReturnStmt(new IntegerLiteral(10))) }));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().ContainSingle()
            .Which.Should().BeOfType<ReturnStmt>();
    }

    [Fact]
    public void Match_StringLiteral_NonMatchingArm_IsEliminated()
    {
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(new MatchStmt(
            new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"),
            new List<CaseBranch> { MakeCaseBranch(new StringLiteral("pic"), new ReturnStmt(new IntegerLiteral(10))) }));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().BeEmpty();
    }

    [Fact]
    public void Match_IntegerLiteral_MatchesFrequency()
    {
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(new MatchStmt(
            new VariableExpr("__FREQ__"),
            new List<CaseBranch> { MakeCaseBranch(new IntegerLiteral(16000000), new ReturnStmt(new IntegerLiteral(20))) }));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().ContainSingle()
            .Which.Should().BeOfType<ReturnStmt>();
    }

    [Fact]
    public void Match_WildcardBranch_IsAlwaysSelected()
    {
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(new MatchStmt(
            new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"),
            new List<CaseBranch>
            {
                MakeCaseBranch(new StringLiteral("pic"), new ReturnStmt(new IntegerLiteral(1))),
                MakeCaseBranch(null, new ReturnStmt(new IntegerLiteral(0)))  // wildcard
            }));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().ContainSingle()
            .Which.Should().BeOfType<ReturnStmt>()
            .Which.Value.Should().BeOfType<IntegerLiteral>()
            .Which.Value.Should().Be(0);
    }

    [Fact]
    public void Match_OrPattern_MatchesAnyAlternative()
    {
        var prog = EmptyProgram();
        var orPattern = new BinaryExpr(new StringLiteral("avr"), BinaryOp.BitOr, new StringLiteral("avr8"));
        prog.GlobalStatements.Add(new MatchStmt(
            new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"),
            new List<CaseBranch> { MakeCaseBranch(orPattern, new ReturnStmt(new IntegerLiteral(42))) }));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().ContainSingle()
            .Which.Should().BeOfType<ReturnStmt>();
    }

    // -------------------------------------------------------------------------
    // startswith
    // -------------------------------------------------------------------------

    [Fact]
    public void Condition_StartsWith_ReturnsTrueWhenPrefixMatches()
    {
        var prog = EmptyProgram();
        var callExpr = new CallExpr(
            new MemberAccessExpr(new MemberAccessExpr(new VariableExpr("__CHIP__"), "chip"), "startswith"),
            new List<Expression> { new StringLiteral("atmega") });
        prog.GlobalStatements.Add(new IfStmt(callExpr, MakeBlock(new ReturnStmt(new IntegerLiteral(1)))));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().ContainSingle()
            .Which.Should().BeOfType<ReturnStmt>();
    }

    // -------------------------------------------------------------------------
    // And / Or
    // -------------------------------------------------------------------------

    [Fact]
    public void Condition_And_BothTrue_ReturnsTrue()
    {
        var prog = EmptyProgram();
        var cond = new BinaryExpr(
            new BinaryExpr(new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"), BinaryOp.Equal, new StringLiteral("avr")),
            BinaryOp.And,
            new BinaryExpr(new VariableExpr("__CHIP__"), BinaryOp.Equal, new StringLiteral("atmega328p")));
        prog.GlobalStatements.Add(new IfStmt(cond, MakeBlock(new ReturnStmt(new IntegerLiteral(1)))));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().ContainSingle()
            .Which.Should().BeOfType<ReturnStmt>();
    }

    [Fact]
    public void Condition_Or_OneTrue_ReturnsTrue()
    {
        var prog = EmptyProgram();
        var cond = new BinaryExpr(
            new BinaryExpr(new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"), BinaryOp.Equal, new StringLiteral("pic")),
            BinaryOp.Or,
            new BinaryExpr(new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"), BinaryOp.Equal, new StringLiteral("avr")));
        prog.GlobalStatements.Add(new IfStmt(cond, MakeBlock(new ReturnStmt(new IntegerLiteral(1)))));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().ContainSingle()
            .Which.Should().BeOfType<ReturnStmt>();
    }

    // -------------------------------------------------------------------------
    // NotEqual
    // -------------------------------------------------------------------------

    [Fact]
    public void Condition_NotEqual_ReturnsTrueWhenDifferent()
    {
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(new IfStmt(
            new BinaryExpr(new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"), BinaryOp.NotEqual, new StringLiteral("pic")),
            MakeBlock(new ReturnStmt(new IntegerLiteral(1)))));

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.GlobalStatements.Should().ContainSingle()
            .Which.Should().BeOfType<ReturnStmt>();
    }
    // -------------------------------------------------------------------------
    // PyMCU#417: typing-only names from a guarded import
    // -------------------------------------------------------------------------

    [Fact]
    public void OptionalImportFailed_RecordsItsSymbolsAsTypingOnly()
    {
        // The try/except ImportError idiom every CircuitPython driver opens with
        // (adafruit_register/i2c_bits.py): the module is not there on this target, the try
        // folds to its (empty) handler, and the names it would have bound are recorded so an
        // annotation naming one is accepted rather than reported as a typo.
        var failedImport = new ImportStmt(
            "circuitpython_typing.device_drivers", new List<string> { "I2CDeviceDriver" })
        {
            IsOptional = true,
            OptionalLoadFailed = true,
        };
        var tryStmt = new TryStmt(
            new List<Statement> { failedImport },
            new List<(string, List<Statement>)> { ("ImportError", new List<Statement>()) });
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(tryStmt);

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.TypingOnlyNames.Should().Contain("I2CDeviceDriver");
    }

    [Fact]
    public void MixedOptionalImports_ResolvedOneKeepsItsBinding_WhenASiblingFails()
    {
        // `from typing import Tuple` resolves (typing is a builtin module) while
        // `from circuitpython_typing.device_drivers import I2CDeviceDriver` does not --
        // the shape every Adafruit driver opens with. CPython binds the resolved names
        // before the failing line raises, so the fold must keep them: dropped entirely,
        // `-> Tuple:` in adafruit_register/i2c_struct.py read as an unknown type.
        var resolvedImport = new ImportStmt("typing", new List<string> { "Tuple" })
        {
            IsOptional = true,
            OptionalLoadFailed = false,
        };
        var failedImport = new ImportStmt(
            "circuitpython_typing.device_drivers", new List<string> { "I2CDeviceDriver" })
        {
            IsOptional = true,
            OptionalLoadFailed = true,
        };
        var tryStmt = new TryStmt(
            new List<Statement> { resolvedImport, failedImport },
            new List<(string, List<Statement>)> { ("ImportError", new List<Statement>()) });
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(tryStmt);

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.Imports.Should().ContainSingle(i => i.ModuleName == "typing");
        prog.TypingOnlyNames.Should().Contain("I2CDeviceDriver");
    }

    [Fact]
    public void IfTypeChecking_RecordsItsImportsSymbolsAsTypingOnly_AndDropsTheBlock()
    {
        // The other spelling of the same guard: `if TYPE_CHECKING:` around the import, no
        // try/except at all. TYPE_CHECKING is False at run time by definition, so the body
        // never runs -- CompileTimeEvaluator has no notion of the name and would otherwise
        // throw "Unsupported condition", leaving the whole `if` as ordinary (unresolvable)
        // control flow and its imports as plain undefined names.
        var guardedImport = new ImportStmt(
            "circuitpython_typing", new List<string> { "ReadableBuffer" });
        var thenBlock = MakeBlock(guardedImport);
        var ifStmt = new IfStmt(new VariableExpr("TYPE_CHECKING"), thenBlock);
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(ifStmt);

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.TypingOnlyNames.Should().Contain("ReadableBuffer");
        // Folded away like a dead `if False:` branch: nothing of the guard survives.
        prog.GlobalStatements.Should().NotContain(ifStmt);
        prog.Imports.Should().BeEmpty();
    }

    [Fact]
    public void IfTypeChecking_DottedSpelling_IsAlsoRecognized()
    {
        var guardedImport = new ImportStmt(
            "circuitpython_typing", new List<string> { "ReadableBuffer" });
        var thenBlock = MakeBlock(guardedImport);
        var cond = new MemberAccessExpr(new VariableExpr("typing"), "TYPE_CHECKING");
        var ifStmt = new IfStmt(cond, thenBlock);
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(ifStmt);

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.TypingOnlyNames.Should().Contain("ReadableBuffer");
    }

    [Fact]
    public void IfTypeChecking_WithAnElse_StillRunsTheElseBranch()
    {
        // Rare in practice, but Python allows it and the else DOES run: only the `then` body
        // is dead code here, not the whole statement.
        var guardedImport = new ImportStmt(
            "circuitpython_typing", new List<string> { "ReadableBuffer" });
        var elseImport = MakeImport("pymcu.avr", "DDRB");
        var ifStmt = new IfStmt(
            new VariableExpr("TYPE_CHECKING"), MakeBlock(guardedImport),
            elseBranch: MakeBlock(elseImport));
        var prog = EmptyProgram();
        prog.GlobalStatements.Add(ifStmt);

        new ConditionalCompilator(AvrConfig()).Process(prog);

        prog.Imports.Should().ContainSingle().Which.ModuleName.Should().Be("pymcu.avr");
    }

    // -------------------------------------------------------------------------
    // PyMCU#266: dead-branch elimination on `sys.implementation.name`, not just __CHIP__
    // -------------------------------------------------------------------------

    private static DeviceConfig CircuitPythonConfig() =>
        new() { Chip = "atmega328p", Arch = "avr", Stdlib = "circuitpython" };

    private static Expression SysImplementationNameExpr() =>
        new MemberAccessExpr(new MemberAccessExpr(new VariableExpr("sys"), "implementation"), "name");

    [Fact]
    public void If_SysImplementationName_DeadBranchImport_IsSkipped()
    {
        // adafruit_requests's actual guard (PyMCU#266):
        //   if not sys.implementation.name == "circuitpython":
        //       from typing import Optional
        // On the CircuitPython layer this branch is dead; `typing` must never be resolved.
        var prog = EmptyProgram();
        var deadImport = MakeImport("typing", "Optional");
        var cond = new UnaryExpr(UnaryOp.Not,
            new BinaryExpr(SysImplementationNameExpr(), BinaryOp.Equal, new StringLiteral("circuitpython")));
        prog.GlobalStatements.Add(new IfStmt(cond, MakeBlock(deadImport)));

        new ConditionalCompilator(CircuitPythonConfig()).Process(prog);

        prog.Imports.Should().BeEmpty();
    }

    [Fact]
    public void If_SysImplementationName_LiveBranchImport_IsKept()
    {
        var prog = EmptyProgram();
        var liveImport = MakeImport("pymcu.avr", "DDRB");
        var cond = new BinaryExpr(SysImplementationNameExpr(), BinaryOp.Equal, new StringLiteral("circuitpython"));
        prog.GlobalStatements.Add(new IfStmt(cond, MakeBlock(liveImport)));

        new ConditionalCompilator(CircuitPythonConfig()).Process(prog);

        prog.Imports.Should().ContainSingle().Which.ModuleName.Should().Be("pymcu.avr");
    }
}

public class CompileTimeEvaluatorTests
{
    private static DeviceConfig AvrConfig() =>
        new() { Chip = "atmega328p", Arch = "avr", Frequency = 16000000 };

    private static CompileTimeEvaluator Evaluator() => new(AvrConfig());

    // -------------------------------------------------------------------------
    // Resolve
    // -------------------------------------------------------------------------

    [Fact]
    public void Resolve_Chip_ReturnsChipName()
        => Evaluator().Resolve(new VariableExpr("__CHIP__")).Should().Be("atmega328p");

    [Fact]
    public void Resolve_Freq_ReturnsFrequencyString()
        => Evaluator().Resolve(new VariableExpr("__FREQ__")).Should().Be("16000000");

    [Fact]
    public void Resolve_FCPU_ReturnsFrequencyString()
        => Evaluator().Resolve(new VariableExpr("F_CPU")).Should().Be("16000000");

    [Fact]
    public void Resolve_ChipArch_ReturnsArch()
        => Evaluator().Resolve(new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch")).Should().Be("avr");

    [Fact]
    public void Resolve_ChipDotChip_ReturnsChipName()
        => Evaluator().Resolve(new MemberAccessExpr(new VariableExpr("__CHIP__"), "chip")).Should().Be("atmega328p");

    [Fact]
    public void Resolve_ChipDotName_ReturnsChipName()
        => Evaluator().Resolve(new MemberAccessExpr(new VariableExpr("__CHIP__"), "name")).Should().Be("atmega328p");

    [Fact]
    public void Resolve_StringLiteral_ReturnsSameValue()
        => Evaluator().Resolve(new StringLiteral("avr")).Should().Be("avr");

    [Fact]
    public void Resolve_IntegerLiteral_ReturnsStringRepresentation()
        => Evaluator().Resolve(new IntegerLiteral(8000000)).Should().Be("8000000");

    [Fact]
    public void Resolve_UnknownVar_Throws()
    {
        var act = () => Evaluator().Resolve(new VariableExpr("runtime_var"));
        act.Should().Throw<Exception>();
    }

    // -------------------------------------------------------------------------
    // MatchesPattern
    // -------------------------------------------------------------------------

    [Fact]
    public void MatchesPattern_Null_IsWildcard()
        => Evaluator().MatchesPattern(null, "avr").Should().BeTrue();

    [Fact]
    public void MatchesPattern_StringLiteral_MatchesExact()
        => Evaluator().MatchesPattern(new StringLiteral("avr"), "avr").Should().BeTrue();

    [Fact]
    public void MatchesPattern_StringLiteral_DoesNotMatchDifferent()
        => Evaluator().MatchesPattern(new StringLiteral("pic"), "avr").Should().BeFalse();

    [Fact]
    public void MatchesPattern_IntegerLiteral_MatchesTarget()
        => Evaluator().MatchesPattern(new IntegerLiteral(16000000), "16000000").Should().BeTrue();

    [Fact]
    public void MatchesPattern_OrPattern_MatchesFirstAlternative()
    {
        var or = new BinaryExpr(new StringLiteral("avr"), BinaryOp.BitOr, new StringLiteral("avr8"));
        Evaluator().MatchesPattern(or, "avr").Should().BeTrue();
    }

    [Fact]
    public void MatchesPattern_OrPattern_MatchesSecondAlternative()
    {
        var or = new BinaryExpr(new StringLiteral("pic"), BinaryOp.BitOr, new StringLiteral("avr"));
        Evaluator().MatchesPattern(or, "avr").Should().BeTrue();
    }

    [Fact]
    public void MatchesPattern_OrPattern_ReturnsFalseWhenNoneMatch()
    {
        var or = new BinaryExpr(new StringLiteral("pic"), BinaryOp.BitOr, new StringLiteral("riscv"));
        Evaluator().MatchesPattern(or, "avr").Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // EvaluateCondition
    // -------------------------------------------------------------------------

    [Fact]
    public void EvaluateCondition_Null_ReturnsFalse()
        => Evaluator().EvaluateCondition(null).Should().BeFalse();

    [Fact]
    public void EvaluateCondition_Equal_TrueWhenMatch()
    {
        var cond = new BinaryExpr(
            new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"),
            BinaryOp.Equal,
            new StringLiteral("avr"));
        Evaluator().EvaluateCondition(cond).Should().BeTrue();
    }

    [Fact]
    public void EvaluateCondition_Equal_FalseWhenNoMatch()
    {
        var cond = new BinaryExpr(
            new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"),
            BinaryOp.Equal,
            new StringLiteral("pic"));
        Evaluator().EvaluateCondition(cond).Should().BeFalse();
    }

    [Fact]
    public void EvaluateCondition_NotEqual_TrueWhenDifferent()
    {
        var cond = new BinaryExpr(
            new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"),
            BinaryOp.NotEqual,
            new StringLiteral("pic"));
        Evaluator().EvaluateCondition(cond).Should().BeTrue();
    }

    [Fact]
    public void EvaluateCondition_And_FalseWhenOneIsFalse()
    {
        var cond = new BinaryExpr(
            new BinaryExpr(new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"), BinaryOp.Equal, new StringLiteral("avr")),
            BinaryOp.And,
            new BinaryExpr(new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"), BinaryOp.Equal, new StringLiteral("pic")));
        Evaluator().EvaluateCondition(cond).Should().BeFalse();
    }

    [Fact]
    public void EvaluateCondition_UnsupportedExpr_Throws()
    {
        var act = () => Evaluator().EvaluateCondition(new IntegerLiteral(1));
        act.Should().Throw<Exception>();
    }

    // -------------------------------------------------------------------------
    // sys.implementation.name / .version, sys.platform, os.uname() (docs/rfcs/0007)
    // -------------------------------------------------------------------------

    private static DeviceConfig CircuitPythonPicoConfig() =>
        new() { Chip = "rp2040", Arch = "arm", Board = "raspberry_pi_pico", Stdlib = "circuitpython" };

    private static DeviceConfig CircuitPythonPico2Config() =>
        new() { Chip = "rp2350", Arch = "arm", Board = "raspberry_pi_pico2", Stdlib = "circuitpython" };

    private static DeviceConfig CircuitPythonAvrConfig() =>
        new() { Chip = "atmega328p", Arch = "avr", Stdlib = "circuitpython" };

    private static DeviceConfig MicroPythonPicoConfig() =>
        new() { Chip = "rp2040", Arch = "arm", Board = "raspberry_pi_pico", Stdlib = "micropython" };

    private static DeviceConfig MicroPythonPico2Config() =>
        new() { Chip = "rp2350", Arch = "arm", Board = "raspberry_pi_pico2", Stdlib = "micropython" };

    private static Expression SysImplementationName() =>
        new MemberAccessExpr(new MemberAccessExpr(new VariableExpr("sys"), "implementation"), "name");

    private static Expression SysPlatform() => new MemberAccessExpr(new VariableExpr("sys"), "platform");

    private static Expression SysImplementationVersionIndex(int i) =>
        new IndexExpr(
            new MemberAccessExpr(new MemberAccessExpr(new VariableExpr("sys"), "implementation"), "version"),
            new IntegerLiteral(i));

    private static Expression BareUnameCall() => new CallExpr(new VariableExpr("uname"), new List<Expression>());
    private static Expression DottedUnameCall() =>
        new CallExpr(new MemberAccessExpr(new VariableExpr("os"), "uname"), new List<Expression>());

    [Fact]
    public void Resolve_SysImplementationName_CircuitPython()
        => new CompileTimeEvaluator(CircuitPythonAvrConfig()).Resolve(SysImplementationName()).Should().Be("circuitpython");

    [Fact]
    public void Resolve_SysImplementationName_MicroPython()
        => new CompileTimeEvaluator(MicroPythonPicoConfig()).Resolve(SysImplementationName()).Should().Be("micropython");

    [Fact]
    public void Resolve_SysImplementationName_NoStdlib_Throws()
    {
        var act = () => Evaluator().Resolve(SysImplementationName());
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Resolve_SysPlatform_CircuitPythonRp2040_IsUppercaseMcuName()
        => new CompileTimeEvaluator(CircuitPythonPicoConfig()).Resolve(SysPlatform()).Should().Be("RP2040");

    [Fact]
    public void Resolve_SysPlatform_CircuitPythonRp2350_IsUppercaseMcuName()
        => new CompileTimeEvaluator(CircuitPythonPico2Config()).Resolve(SysPlatform()).Should().Be("RP2350");

    [Fact]
    public void Resolve_SysPlatform_MicroPythonRp2040AndRp2350_BothAnswerRp2()
    {
        new CompileTimeEvaluator(MicroPythonPicoConfig()).Resolve(SysPlatform()).Should().Be("rp2");
        new CompileTimeEvaluator(MicroPythonPico2Config()).Resolve(SysPlatform()).Should().Be("rp2");
    }

    [Fact]
    public void Resolve_SysPlatform_NoUpstreamPort_FallsBackToChipName()
        => new CompileTimeEvaluator(CircuitPythonAvrConfig()).Resolve(SysPlatform()).Should().Be("atmega328p");

    [Fact]
    public void Resolve_UnameSysname_CircuitPythonRp2040_IsLowercaseMcuName()
        => new CompileTimeEvaluator(CircuitPythonPicoConfig())
            .Resolve(new MemberAccessExpr(BareUnameCall(), "sysname")).Should().Be("rp2040");

    [Fact]
    public void Resolve_UnameSysname_CircuitPythonRp2350_IsExactSiliconVariant_NotPyMcuChipId()
        // rp2350a, not PyMCU's own simplified "rp2350" chip id -- docs/rfcs/0007 section 0.3.
        => new CompileTimeEvaluator(CircuitPythonPico2Config())
            .Resolve(new MemberAccessExpr(DottedUnameCall(), "sysname")).Should().Be("rp2350a");

    [Fact]
    public void Resolve_UnameSysname_MicroPythonRp2040_IsPortName_NotMcuName()
        // "rp2", the MicroPython port short name -- NOT "RP2040", a different upstream concept
        // than CircuitPython's os.uname().sysname (docs/rfcs/0007 section 2.1).
        => new CompileTimeEvaluator(MicroPythonPicoConfig())
            .Resolve(new MemberAccessExpr(BareUnameCall(), "sysname")).Should().Be("rp2");

    [Fact]
    public void Resolve_UnameSysname_NoUpstreamPort_FallsBackToChipName_BothLayers()
    {
        new CompileTimeEvaluator(CircuitPythonAvrConfig())
            .Resolve(new MemberAccessExpr(BareUnameCall(), "sysname")).Should().Be("atmega328p");
        var mpAvr = new DeviceConfig { Chip = "atmega328p", Arch = "avr", Stdlib = "micropython" };
        new CompileTimeEvaluator(mpAvr).Resolve(new MemberAccessExpr(BareUnameCall(), "sysname")).Should().Be("atmega328p");
    }

    [Fact]
    public void Resolve_UnameMachine_CircuitPythonPico_MatchesUpstreamConstruction()
        => new CompileTimeEvaluator(CircuitPythonPicoConfig())
            .Resolve(new MemberAccessExpr(BareUnameCall(), "machine")).Should().Be("Raspberry Pi Pico with rp2040");

    [Fact]
    public void Resolve_UnameMachine_MicroPythonPico2_UsesUpstreamsOwnBoardSpelling_NoSpaceBeforeTwo()
        // MicroPython's RPI_PICO2 board reports "Raspberry Pi Pico2" (no space), CircuitPython's
        // raspberry_pi_pico2 reports "Raspberry Pi Pico 2" (with space) -- both are upstream's
        // own strings, not reconciled (docs/rfcs/0007 section 4.3).
        => new CompileTimeEvaluator(MicroPythonPico2Config())
            .Resolve(new MemberAccessExpr(BareUnameCall(), "machine")).Should().Be("Raspberry Pi Pico2 with RP2350");

    [Fact]
    public void EvaluateCondition_SysImplementationVersionIndex0_FeatureDetectionGuard()
    {
        // neopixel.py: `sys.implementation.version[0] >= 7` -- must read TRUE on this layer's
        // claimed CircuitPython 10.3.1 API surface (docs/rfcs/0007 section 3).
        var cond = new BinaryExpr(SysImplementationVersionIndex(0), BinaryOp.GreaterEq, new IntegerLiteral(7));
        new CompileTimeEvaluator(CircuitPythonPicoConfig()).EvaluateCondition(cond).Should().BeTrue();
    }

    [Fact]
    public void EvaluateCondition_LinuxNotInUname_AlwaysTrueOnAnyPyMcuBoard()
    {
        // adafruit_dht.py:77 exactly: `"Linux" not in uname()`. No PyMCU board is ever Linux.
        var cond = new BinaryExpr(new StringLiteral("Linux"), BinaryOp.NotIn, BareUnameCall());
        new CompileTimeEvaluator(CircuitPythonPicoConfig()).EvaluateCondition(cond).Should().BeTrue();
        new CompileTimeEvaluator(CircuitPythonAvrConfig()).EvaluateCondition(cond).Should().BeTrue();
        new CompileTimeEvaluator(MicroPythonPicoConfig()).EvaluateCondition(cond).Should().BeTrue();
    }

    [Fact]
    public void EvaluateCondition_SysImplementationNameEqualsCircuitPython_TrueOnCircuitPythonLayer()
    {
        var cond = new BinaryExpr(SysImplementationName(), BinaryOp.Equal, new StringLiteral("circuitpython"));
        new CompileTimeEvaluator(CircuitPythonAvrConfig()).EvaluateCondition(cond).Should().BeTrue();
        new CompileTimeEvaluator(MicroPythonPicoConfig()).EvaluateCondition(cond).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // The u-spellings: usys / uos answer the same table sys / os do.
    // -------------------------------------------------------------------------

    private static Expression UsysPlatform() => new MemberAccessExpr(new VariableExpr("usys"), "platform");

    private static Expression UsysImplementationName() =>
        new MemberAccessExpr(new MemberAccessExpr(new VariableExpr("usys"), "implementation"), "name");

    private static Expression UosDottedUnameCall() =>
        new CallExpr(new MemberAccessExpr(new VariableExpr("uos"), "uname"), new List<Expression>());

    [Fact]
    public void Resolve_UsysPlatform_MicroPythonRp2040_AnswersRp2LikeSys()
        => new CompileTimeEvaluator(MicroPythonPicoConfig()).Resolve(UsysPlatform()).Should().Be("rp2");

    [Fact]
    public void Resolve_UsysImplementationName_MicroPython_AnswersMicropython()
        => new CompileTimeEvaluator(MicroPythonPicoConfig()).Resolve(UsysImplementationName()).Should().Be("micropython");

    [Fact]
    public void Resolve_UosUnameSysname_MicroPythonRp2040_AnswersThePortName()
        => new CompileTimeEvaluator(MicroPythonPicoConfig())
            .Resolve(new MemberAccessExpr(UosDottedUnameCall(), "sysname")).Should().Be("rp2");

    [Fact]
    public void EvaluateCondition_UsysImplementationVersionIndex_MicroPython_Answers129()
    {
        var versionIndex = new IndexExpr(
            new MemberAccessExpr(new MemberAccessExpr(new VariableExpr("usys"), "implementation"), "version"),
            new IntegerLiteral(1));
        var cond = new BinaryExpr(versionIndex, BinaryOp.Equal, new IntegerLiteral(29));
        new CompileTimeEvaluator(MicroPythonPicoConfig()).EvaluateCondition(cond).Should().BeTrue();
    }

    [Fact]
    public void EvaluateCondition_LinuxNotInUosUname_AlwaysTrue()
    {
        var cond = new BinaryExpr(new StringLiteral("Linux"), BinaryOp.NotIn, UosDottedUnameCall());
        new CompileTimeEvaluator(MicroPythonPicoConfig()).EvaluateCondition(cond).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // `import X as Y` -- ModuleAliases maps the used name to the real module, so
    // `s.platform` after `import usys as s` folds the way `usys.platform` does.
    // -------------------------------------------------------------------------

    [Fact]
    public void Resolve_UsysAliasedAsS_Platform_AnswersTheTable()
    {
        var ev = new CompileTimeEvaluator(MicroPythonPicoConfig());
        ev.ModuleAliases["s"] = "usys";
        ev.Resolve(new MemberAccessExpr(new VariableExpr("s"), "platform")).Should().Be("rp2");
    }

    [Fact]
    public void Resolve_UosAliasedAsO_UnameMachine_AnswersTheTable()
    {
        var ev = new CompileTimeEvaluator(MicroPythonPicoConfig());
        ev.ModuleAliases["o"] = "uos";
        var call = new CallExpr(new MemberAccessExpr(new VariableExpr("o"), "uname"), new List<Expression>());
        ev.Resolve(new MemberAccessExpr(call, "machine")).Should().Be("raspberry_pi_pico with RP2040");
    }

    [Fact]
    public void Resolve_UsysAliasedAsS_VersionIndex_Answers129()
    {
        var ev = new CompileTimeEvaluator(MicroPythonPicoConfig());
        ev.ModuleAliases["s"] = "usys";
        var versionIndex = new IndexExpr(
            new MemberAccessExpr(new MemberAccessExpr(new VariableExpr("s"), "implementation"), "version"),
            new IntegerLiteral(2));
        var cond = new BinaryExpr(versionIndex, BinaryOp.Equal, new IntegerLiteral(0));
        ev.EvaluateCondition(cond).Should().BeTrue();
    }

    [Fact]
    public void Resolve_AnUnrelatedAlias_DoesNotFold()
    {
        // `import time as s` must not turn s.platform into the sys table.
        var ev = new CompileTimeEvaluator(MicroPythonPicoConfig());
        ev.ModuleAliases["s"] = "time";
        var act = () => ev.Resolve(new MemberAccessExpr(new VariableExpr("s"), "platform"));
        act.Should().Throw<Exception>();
    }
}

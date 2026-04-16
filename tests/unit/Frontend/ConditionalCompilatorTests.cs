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
}


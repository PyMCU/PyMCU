using Xunit;
using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;

namespace PyMCU.UnitTests;

/// <summary>
/// `if __CHIP__.arch not in ("pic12", "pic14"):` at module level. A chain of `!=` joined by
/// `and` folded at compile time and the membership test over a literal tuple did not, so the
/// classes written under it were left to be resolved at run time and were never defined.
/// </summary>
public class ConditionalCompilatorMembershipTests
{
    private static DeviceConfig Config(string chip, string arch) =>
        new() { Chip = chip, Arch = arch, Frequency = 16000000 };

    private static IfStmt ArchMembership(BinaryOp op, params string[] names) =>
        new(new BinaryExpr(
                new MemberAccessExpr(new VariableExpr("__CHIP__"), "arch"), op,
                new TupleExpr(names.Select(n => (Expression)new StringLiteral(n)).ToList())),
            Block(new ReturnStmt(new IntegerLiteral(1))));

    private static Block Block(params Statement[] stmts)
    {
        var b = new Block();
        b.Statements.AddRange(stmts);
        return b;
    }

    // How many bodies survive: 1 when the condition folded to true, 0 when it folded to false,
    // and 0 with the `if` still standing when it did not fold at all.
    private static (int Bodies, int Ifs) Run(IfStmt stmt, DeviceConfig cfg)
    {
        var prog = new ProgramNode();
        prog.Imports.Add(new ImportStmt("pymcu.chips", new List<string> { "__CHIP__" }));
        prog.GlobalStatements.Add(stmt);
        new ConditionalCompilator(cfg).Process(prog);
        return (prog.GlobalStatements.OfType<ReturnStmt>().Count(), prog.GlobalStatements.OfType<IfStmt>().Count());
    }

    [Fact]
    public void NotInTuple_KeepsTheBody_WhenTheArchIsNotListed() =>
        Run(ArchMembership(BinaryOp.NotIn, "pic12", "pic14", "pic18"), Config("atmega328p", "avr"))
            .Should().Be((1, 0));

    [Fact]
    public void NotInTuple_DropsTheBody_WhenTheArchIsListed() =>
        Run(ArchMembership(BinaryOp.NotIn, "pic12", "avr"), Config("atmega328p", "avr"))
            .Should().Be((0, 0));

    [Fact]
    public void InTuple_KeepsTheBody_WhenTheArchIsListed() =>
        Run(ArchMembership(BinaryOp.In, "arm", "avr"), Config("atmega328p", "avr"))
            .Should().Be((1, 0));

    [Fact]
    public void InTuple_AnswersLikeTheEqualityChainOnArm()
    {
        var arm = Config("rp2040", "arm");
        Run(ArchMembership(BinaryOp.NotIn, "pic12", "pic14"), arm).Should().Be((1, 0));
        Run(ArchMembership(BinaryOp.NotIn, "arm", "pic14"), arm).Should().Be((0, 0));
    }
}

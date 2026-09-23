using Xunit;
using FluentAssertions;
using PyMCU.Common;
using PyMCU.Frontend;
using PyMCU.IR.IRGenerator;
using PyMCU.IR;
using PyMCU.Common.Models;

namespace PyMCU.UnitTests;

/// <summary>
/// A `global` declaration inside a generator names the module global: `T = 9` in the
/// machine's body must write `T`, not a `self.T` field the machine invents. Before this
/// fix, `CollectAssignedLocals` counted every assigned name without consulting `global`,
/// so the write was silently captured by a field and the module global never moved --
/// a wrong-program compile with no diagnostic.
/// </summary>
public class GeneratorGlobalTests
{
    private static ClassDef TransformedClass(string src, string name)
    {
        var ast = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        AsyncTransform.TransformProgram(ast);
        return ast.GlobalStatements.OfType<ClassDef>().Single(c => c.Name == name);
    }

    private static IEnumerable<Statement> AllStatements(Statement s)
    {
        yield return s;
        switch (s)
        {
            case Block b:
                foreach (var st in b.Statements)
                foreach (var x in AllStatements(st)) yield return x;
                break;
            case IfStmt i:
                foreach (var x in AllStatements(i.ThenBranch)) yield return x;
                if (i.ElseBranch != null)
                    foreach (var x in AllStatements(i.ElseBranch)) yield return x;
                foreach (var (_, eb) in i.ElifBranches)
                foreach (var x in AllStatements(eb)) yield return x;
                break;
            case WhileStmt w:
                foreach (var x in AllStatements(w.Body)) yield return x;
                break;
            case ForStmt f:
                foreach (var x in AllStatements(f.Body)) yield return x;
                break;
        }
    }

    private static IEnumerable<Expression> AllExpressions(Statement s)
    {
        foreach (var e in AllStatements(s).SelectMany(st => st switch
        {
            ExprStmt es => new[] { es.Expr },
            AssignStmt a => new Expression[] { a.Target, a.Value },
            ReturnStmt r when r.Value != null => new[] { r.Value },
            IfStmt i => new[] { i.Condition },
            WhileStmt w => new[] { w.Condition },
            _ => System.Array.Empty<Expression>(),
        }))
        {
            yield return e;
            if (e is MemberAccessExpr m) yield return m.Object;
            if (e is CallExpr c) { yield return c.Callee; foreach (var a in c.Args) yield return a; }
            if (e is BinaryExpr b) { yield return b.Left; yield return b.Right; }
        }
    }

    [Fact]
    public void ADeclaredGlobalIsNotPromotedToAMachineField()
    {
        var cls = TransformedClass("""
            T = 0
            def g():
                global T
                yield T
                T = 9
                yield T
            """, "g");

        var body = (Block)cls.Body;
        // No `self.T` anywhere in the machine: not in __init__'s declarations and not in
        // poll's rewritten statements.
        var selfFieldWrites = body.Statements
            .OfType<FunctionDef>()
            .SelectMany(fn => AllExpressions(fn.Body))
            .OfType<MemberAccessExpr>()
            .Where(m => m.Member == "T")
            .ToList();
        selfFieldWrites.Should().BeEmpty("a `global` name is the module global, not a field");

        // The declaration itself survives into the machine body, so the IR still knows
        // `T` is the global.
        body.Statements.OfType<FunctionDef>()
            .SelectMany(fn => AllStatements(fn.Body))
            .OfType<GlobalStmt>()
            .Should().ContainSingle(gs => gs.Names.Contains("T"));
    }
}

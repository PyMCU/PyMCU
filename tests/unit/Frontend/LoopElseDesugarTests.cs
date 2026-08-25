using PyMCU.Frontend;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `for ... else` and `while ... else` used to be rejected with the advice "move the else body
/// to after the loop", which compiles into a DIFFERENT program: the else clause runs only when
/// the loop finished without a break, and code after the loop runs on every path. The construct
/// is now lowered the way Python describes it -- a flag set before the loop, cleared by every
/// break that belongs to it, tested afterwards.
///
/// The behaviour is measured on the simulator (pymcu-avr fixtures/loop-else); what is pinned
/// here is the shape of the rewrite, which is what keeps the later passes seeing plain nodes.
/// </summary>
public class LoopElseDesugarTests
{
    private static ProgramNode Parse(string src)
        => new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static Block BodyOf(ProgramNode p, string fn)
        => (Block)p.Functions.Single(f => f.Name == fn).Body;

    /// <summary>Every statement in a subtree, so a break can be found wherever it was written.</summary>
    private static IEnumerable<Statement> Flatten(Statement? s)
    {
        if (s == null) yield break;
        yield return s;

        IEnumerable<Statement?> children = s switch
        {
            Block b => b.Statements,
            ForStmt f => [f.Body],
            WhileStmt w => [w.Body],
            WithStmt w => [w.Body],
            IfStmt i => [i.ThenBranch, i.ElseBranch, .. i.ElifBranches.Select(e => e.Body)],
            TryStmt t => [.. t.Body, .. t.Handlers.SelectMany(h => h.Handler),
                          .. t.ElseBody ?? [], .. t.Finally ?? []],
            MatchStmt m => m.Branches.Select(br => br.Body),
            _ => [],
        };

        foreach (var c in children)
            foreach (var inner in Flatten(c))
                yield return inner;
    }

    [Fact]
    public void ForElse_Parses()
    {
        var p = Parse("def main():\n" +
                      "    for i in range(5):\n" +
                      "        if i == 3:\n" +
                      "            break\n" +
                      "    else:\n" +
                      "        x = 1\n");

        Assert.Single(p.Functions);
    }

    [Fact]
    public void ForElse_WithABreak_BecomesFlagInit_Loop_AndATestAfterwards()
    {
        var body = BodyOf(Parse("def main():\n" +
                                "    for i in range(5):\n" +
                                "        if i == 3:\n" +
                                "            break\n" +
                                "    else:\n" +
                                "        x = 1\n"), "main");

        var wrapper = Assert.IsType<Block>(Assert.Single(body.Statements));
        Assert.Collection(wrapper.Statements,
            s => Assert.IsType<AssignStmt>(s),
            s => Assert.IsType<ForStmt>(s),
            s => Assert.IsType<IfStmt>(s));
    }

    [Fact]
    public void TheBreakCarriesTheSameFlagTheTestReads()
    {
        var body = BodyOf(Parse("def main():\n" +
                                "    for i in range(5):\n" +
                                "        if i == 3:\n" +
                                "            break\n" +
                                "    else:\n" +
                                "        x = 1\n"), "main");

        var wrapper = (Block)body.Statements[0];
        var init = (AssignStmt)wrapper.Statements[0];
        var flag = ((VariableExpr)init.Target).Name;

        var brk = Flatten(wrapper.Statements[1]).OfType<BreakStmt>().Single();
        Assert.Equal(flag, brk.LoopElseFlag);

        var test = (IfStmt)wrapper.Statements[2];
        var cmp = Assert.IsType<BinaryExpr>(test.Condition);
        Assert.Equal(flag, ((VariableExpr)cmp.Left).Name);
    }

    [Fact]
    public void WithNoBreakInTheBody_TheElseBodyIsEmittedUnconditionally()
    {
        // Nothing can skip the else clause, so the flag and the test would both be dead weight.
        var body = BodyOf(Parse("def main():\n" +
                                "    for i in range(5):\n" +
                                "        x = i\n" +
                                "    else:\n" +
                                "        y = 1\n"), "main");

        var wrapper = Assert.IsType<Block>(Assert.Single(body.Statements));
        Assert.Collection(wrapper.Statements,
            s => Assert.IsType<ForStmt>(s),
            s => Assert.IsType<AssignStmt>(s));
    }

    [Fact]
    public void ABreakInANestedLoop_BelongsToThatLoop_AndIsNotTagged()
    {
        var body = BodyOf(Parse("def main():\n" +
                                "    for i in range(3):\n" +
                                "        for j in range(3):\n" +
                                "            if j == 1:\n" +
                                "                break\n" +
                                "    else:\n" +
                                "        x = 1\n"), "main");

        var brk = Flatten(body.Statements[0]).OfType<BreakStmt>().Single();
        Assert.Equal("", brk.LoopElseFlag);
    }

    [Fact]
    public void ABreakInsideATry_StillBelongsToTheLoop()
    {
        var body = BodyOf(Parse("def main():\n" +
                                "    for i in range(3):\n" +
                                "        try:\n" +
                                "            if i == 1:\n" +
                                "                break\n" +
                                "        finally:\n" +
                                "            x = 1\n" +
                                "    else:\n" +
                                "        y = 1\n"), "main");

        var wrapper = (Block)body.Statements[0];
        var brk = Flatten(wrapper.Statements[1]).OfType<BreakStmt>().Single();
        Assert.NotEqual("", brk.LoopElseFlag);
    }

    [Fact]
    public void WhileElse_GetsTheSameShape()
    {
        var body = BodyOf(Parse("def main():\n" +
                                "    i = 0\n" +
                                "    while i < 5:\n" +
                                "        if i == 3:\n" +
                                "            break\n" +
                                "        i = i + 1\n" +
                                "    else:\n" +
                                "        x = 1\n"), "main");

        var wrapper = Assert.IsType<Block>(body.Statements[1]);
        Assert.Collection(wrapper.Statements,
            s => Assert.IsType<AssignStmt>(s),
            s => Assert.IsType<WhileStmt>(s),
            s => Assert.IsType<IfStmt>(s));
    }

    [Fact]
    public void TwoLoopElsesInOneFunction_GetDistinctFlags()
    {
        var body = BodyOf(Parse("def main():\n" +
                                "    for i in range(3):\n" +
                                "        if i == 1:\n" +
                                "            break\n" +
                                "    else:\n" +
                                "        x = 1\n" +
                                "    for j in range(3):\n" +
                                "        if j == 2:\n" +
                                "            break\n" +
                                "    else:\n" +
                                "        y = 1\n"), "main");

        var flags = body.Statements
            .OfType<Block>()
            .Select(b => ((VariableExpr)((AssignStmt)b.Statements[0]).Target).Name)
            .ToList();

        Assert.Equal(2, flags.Count);
        Assert.Equal(2, flags.Distinct().Count());
    }

    [Fact]
    public void APlainLoopWithNoElse_IsLeftAlone()
    {
        var body = BodyOf(Parse("def main():\n" +
                                "    for i in range(5):\n" +
                                "        if i == 3:\n" +
                                "            break\n"), "main");

        var loop = Assert.IsType<ForStmt>(Assert.Single(body.Statements));
        Assert.Equal("", Flatten(loop.Body).OfType<BreakStmt>().Single().LoopElseFlag);
    }
}

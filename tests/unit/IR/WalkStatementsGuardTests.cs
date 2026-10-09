using FluentAssertions;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The one-traversal rule: inside <c>src/compiler/IR/IRGenerator</c> no pass walks a
/// statement list by hand. Every enumeration of a body's statements goes through
/// <c>TypeInference.WalkStatements</c>, which reaches if/elif/else, loops, with, try
/// (body, handlers, else, finally) and match arms at any depth.
///
/// The twenty-five hand-rolled walks this replaced each covered a different subset of
/// that grammar, and a name written inside a construct the walker skipped was a name
/// the pass never saw. The scan below cannot see recursion, so it flags the surface a
/// recursive walk must touch -- a statement-list property iterated directly -- and
/// requires every hit to be inside an allowlisted function whose reason is written
/// next to it. A new walk that fails here either converts to the shared walk or gets
/// an entry that says why it cannot.
/// </summary>
public class WalkStatementsGuardTests
{
    // Iterating a statement container outside the shared walk. `.Branches` and bare
    // `.Body` are deliberately absent: match-arm loops in leaf switches never recurse
    // (the recursion goes through the walker's own Block case, which .Statements
    // catches), and `f.Body` on an IR Function is a List<Instruction>, not AST.
    private static readonly System.Text.RegularExpressions.Regex StatementListIteration =
        new(@"foreach\s*\([^)]*\bin\s+[\w.()]*\.(Statements|Finally|ElseBody|Handlers)\b"
            + @"|\b\w+\.(Statements|Finally|ElseBody|Handlers)\.(Any|All|Where|Select|Count|ToList|Aggregate|FirstOrDefault)\s*\(",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // (file, enclosing function) pairs allowed to iterate a statement list directly,
    // with the reason the shared walk cannot express what they do.
    private static readonly HashSet<(string File, string Function)> Allowed = new()
    {
        // TypeInference.cs -- the shared walker's own internals.
        ("TypeInference.cs", "WalkStatement"),
        ("TypeInference.cs", "WalkStatements"),
        ("TypeInference.cs", "WalkExpressions"),
        ("TypeInference.cs", "MethodsOf"),          // class-member iteration, not a body walk
        ("TypeInference.cs", "InferReturnType"),    // hands the list to CollectReturns, which walks it
        ("TypeInference.cs", "InferOptionalReturn"),
        ("TypeInference.cs", "InferUnionReturnMembers"), // hands the list to CollectReturns, which walks it
        ("TypeInference.cs", "HasValueReturn"),

        // Structural predicates: the recursion IS the semantics ("does every arm
        // return", "does a break belong to THIS loop"). A flat enumeration would
        // answer a different question.
        ("Statements.cs", "AlwaysReturns"),
        ("Scan.cs", "AlwaysLeaves"),
        ("Scan.cs", "HasOwnBreak"),
        ("Iteration.cs", "LoopBodyHasBreakOrContinue"),
        ("Iteration.cs", "LoopBodyCanReturnOrRaise"), // recurses INTO nested loops on purpose:
                                                       // return/raise unwind through them, unlike
                                                       // break/continue just above
        ("Core.cs", "ContainsReturn"),

        // Flow-sensitive scans whose per-arm state cannot come from a flat list.
        ("Grid2d.cs", "ScanRowStmt"),                       // stops at a rebind; later binds are illegal
        ("Grid2d.cs", "ScanRowAliasUses"),
        ("OptionalUnion.cs", "ScanStmtsForRuntimeNone"),    // joins none/optional sets per arm
        ("OptionalUnion.cs", "ScanIfForRuntimeNone"),
        ("OptionalUnion.cs", "ArmStatements"),

        // The lowering visitor's own dispatch: it must visit each statement in order
        // and keep the try/match structure; flattening it would change the output.
        ("ControlFlow.cs", "VisitTry"),
        ("ControlFlow.cs", "VisitMatchBody"),
        ("ControlFlow.cs", "EmitFinallyBody"),

        // Class-member iteration: a class body's members are defs and declarations,
        // not statement bodies, and the walk does not descend into them anyway.
        ("Assign.cs", "CheckSignatureAnnotations"),
        ("Scan.cs", "ScanClassBodyAttributes"),
        ("Scan.cs", "ScanFunctions"),
        ("Scan.cs", "InitCallsSuperInit"),
        ("Scan.cs", "DeriveFieldLayout"),
        ("Scan.cs", "RecordSourcePaths"),
        ("Scan.cs", "ScanNestedClassMembers"),
        ("Scan.cs", "FunctionsOfClass"),
        ("Scan.cs", "FunctionsWithOwners"),
        // Same class-member iteration, to recover the owning ClassDef (and build a
        // class-prefix string) that TypeInference.ClassMethods/MethodsOf already walks
        // but does not hand back -- the two foreach(body.Statements) here never look
        // past a FunctionDef/ClassDef member, same as ScanNestedClassMembers above.
        ("ProvenDivisors.cs", "ScanWholeProgramConstantDivisors"),
        ("ProvenScalarReturn.cs", "ComputeFunctionsReturnProvenScalar"),

        // LoopVarLiveness walks AST NODES (statements AND expressions, with a skip
        // boundary for nested scopes) -- a different granularity than a statement list.
        ("LoopVarLiveness.cs", "AstChildren"),
        ("LoopVarLiveness.cs", "AstNodes"),
    };

    private static string EnclosingFunction(string[] lines, int lineIndex)
    {
        // The nearest preceding member declaration at class indent (4 spaces); local
        // functions are indented deeper and are ignored, so a local walker's hits
        // land on the member that holds it.
        var decl = new System.Text.RegularExpressions.Regex(
            @"^\s{4}(?:private|internal|public|protected)(?:\s+static)?\s+[\w<>\[\],? ().]*?\b(\w+)\s*\(");
        for (int i = lineIndex; i >= 0; i--)
        {
            var m = decl.Match(lines[i]);
            if (m.Success) return m.Groups[1].Value;
        }
        return "?";
    }

    [Fact]
    public void NoHandRolledStatementWalks_InIRGenerator()
    {
        var dirs = new[]
        {
            RepositoryFile.Under("src", "compiler", "IR", "IRGenerator"),
            RepositoryFile.Under("src", "compiler", "Frontend"),
        };
        var offenders = new List<string>();
        foreach (var dir in dirs)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
            {
                var name = Path.GetFileName(file);
                if (dir.EndsWith("Frontend") && name != "TypeInference.cs") continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (!StatementListIteration.IsMatch(line)) continue;
                    if (line.Contains("WalkStatements")) continue;   // the blessed shape
                    var fn = EnclosingFunction(lines, i);
                    if (Allowed.Contains((name, fn))) continue;
                    offenders.Add($"{name}:{i + 1} in {fn}: {line.Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "every statement-list iteration in IRGenerator must go through "
            + "TypeInference.WalkStatements or carry an allowlist entry explaining why "
            + "the shared walk cannot express it");
    }

    /// <summary>
    /// The walk's reach, observed through a public compile: the missing-return check
    /// is waived for a body that contains `asm(...)` only when the scan actually
    /// reaches the statement. An asm inside try/except counts; an asm inside a nested
    /// def must NOT count for the outer function (the scope boundary is deliberate).
    /// </summary>
    [Fact]
    public void WalkReach_AsmInsideTry_WaivesMissingReturn()
    {
        var src = """
            from pymcu.types import uint8

            def f() -> uint8:
                try:
                    asm("    RET")
                except:
                    pass
            """;
        var tokens = new PyMCU.Frontend.Lexer(src).Tokenize();
        var ast = new PyMCU.Frontend.Parser(tokens).ParseProgram();
        var act = () => new PyMCU.IR.IRGenerator.IRGenerator().Generate(
            ast, new Dictionary<string, PyMCU.Frontend.ProgramNode>(),
            new PyMCU.Common.Models.DeviceConfig { Arch = "avr" });
        act.Should().NotThrow("the walk reaches asm() inside try/except");
    }

    [Fact]
    public void WalkReach_AsmInsideNestedDef_DoesNotCountForOuter()
    {
        var src = """
            from pymcu.types import uint8

            def f() -> uint8:
                @inline
                def inner():
                    asm("    NOP")
            """;
        var tokens = new PyMCU.Frontend.Lexer(src).Tokenize();
        var ast = new PyMCU.Frontend.Parser(tokens).ParseProgram();
        var act = () => new PyMCU.IR.IRGenerator.IRGenerator().Generate(
            ast, new Dictionary<string, PyMCU.Frontend.ProgramNode>(),
            new PyMCU.Common.Models.DeviceConfig { Arch = "avr" });
        act.Should().Throw<PyMCU.Common.CompilerError>(
            "a nested def's body is its own scope: its asm() is not the outer "
            + "function's, so f still falls off the end without a return");
    }
}

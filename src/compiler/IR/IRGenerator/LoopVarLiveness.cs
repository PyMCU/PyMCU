/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */


using PyMCU.Frontend;

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    /// Every `for` whose loop variable is read somewhere outside the loop itself, in the same
    /// scope (the enclosing function body, or the module's own statements). Python leaves the
    /// variable at the last value the loop visited; a runtime range loop's counter stops one
    /// step past it, and an unrolled loop keeps the value in a constant binding it drops on
    /// exit. Both only owe the program a store when something reads the name afterwards, and
    /// that is a question about the source, answered once here before any lowering runs.
    /// (PyMCU#285)
    private readonly HashSet<ForStmt> loopVarReadAfter = new(ReferenceEqualityComparer.Instance);

    private void ScanLoopVarReadsAfter(ProgramNode mainAst, IEnumerable<ProgramNode> importedModules)
    {
        foreach (var prog in importedModules.Prepend(mainAst))
        {
            ScanLoopVarScope(prog.GlobalStatements);
            foreach (var f in AstNodes(prog, descendIntoFunctions: true).OfType<FunctionDef>())
                ScanLoopVarScope(new[] { f.Body });
        }
    }

    // One scope: the loops directly in it (nested functions are scopes of their own), and
    // for each, whether its variable is read anywhere in the scope but the loop's own subtree.
    private void ScanLoopVarScope(IEnumerable<ASTNode> roots)
    {
        var rootList = roots.ToList();
        var loops = rootList.SelectMany(r => AstNodes(r, descendIntoFunctions: false)).OfType<ForStmt>().ToList();
        if (loops.Count == 0) return;

        foreach (var loop in loops)
        {
            bool read = rootList
                .SelectMany(r => AstNodes(r, descendIntoFunctions: false, skip: loop))
                .OfType<VariableExpr>()
                .Any(v => v.Name == loop.VarName
                          || (!string.IsNullOrEmpty(loop.Var2Name) && v.Name == loop.Var2Name));
            if (read) loopVarReadAfter.Add(loop);
        }
    }

    // Every AST node under `root`, `root` included. Written out by node class rather than
    // by reflection: the shipped compiler is a NativeAOT binary, and the trimmer keeps no
    // property metadata for a reflective walk to find (it saw only the top-level statements).
    // A nested FunctionDef is a scope boundary unless the caller asks to cross it; `skip` and
    // its subtree are left out entirely. A node class this switch does not know has no
    // children as far as the scan is concerned, which errs on "not read" -- the pre-fix
    // behaviour -- and never on a wrong store.
    private static IEnumerable<ASTNode> AstNodes(ASTNode root, bool descendIntoFunctions, ASTNode? skip = null)
    {
        var stack = new Stack<ASTNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (ReferenceEquals(n, skip)) continue;
            yield return n;
            if (n is FunctionDef && !ReferenceEquals(n, root) && !descendIntoFunctions) continue;
            foreach (var child in AstChildren(n))
                if (child != null) stack.Push(child);
        }
    }

    private static IEnumerable<ASTNode?> AstChildren(ASTNode n)
    {
        switch (n)
        {
            case ProgramNode p:
                foreach (var f in p.Functions) yield return f;
                foreach (var st in p.GlobalStatements) yield return st;
                break;
            case ClassDef c: yield return c.Body; break;
            case FunctionDef f:
                foreach (var prm in f.Params) yield return prm;
                yield return f.Body;
                break;
            case Param prm: yield return prm.DefaultValue; break;
            case Block b: foreach (var st in b.Statements) yield return st; break;
            case VarDecl v: yield return v.Init; break;
            case AnnAssign a: yield return a.Value; break;
            case AssignStmt a: yield return a.Target; yield return a.Value; break;
            case AugAssignStmt a: yield return a.Target; yield return a.Value; break;
            case TupleUnpackStmt t: yield return t.Value; break;
            case ReturnStmt r: yield return r.Value; break;
            case ExprStmt e: yield return e.Expr; break;
            case AssertStmt a: yield return a.Condition; break;
            case IfStmt i:
                yield return i.Condition;
                yield return i.ThenBranch;
                foreach (var (cond, body) in i.ElifBranches) { yield return cond; yield return body; }
                yield return i.ElseBranch;
                break;
            case MatchStmt m:
                yield return m.Target;
                foreach (var br in m.Branches) { yield return br.Pattern; yield return br.Guard; yield return br.Body; }
                break;
            case WhileStmt w: yield return w.Condition; yield return w.Body; break;
            case ForStmt f:
                yield return f.RangeStart; yield return f.RangeStop; yield return f.RangeStep;
                yield return f.Iterable; yield return f.Body;
                break;
            case WithStmt w: yield return w.ContextExpr; yield return w.Body; break;
            case TryStmt t:
                foreach (var st in t.Body) yield return st;
                foreach (var (_, handler) in t.Handlers) foreach (var st in handler) yield return st;
                if (t.Finally != null) foreach (var st in t.Finally) yield return st;
                if (t.ElseBody != null) foreach (var st in t.ElseBody) yield return st;
                break;
            case DictExpr d: foreach (var (k, v) in d.Entries) { yield return k; yield return v; } break;
            case SetExpr s: foreach (var e in s.Elements) yield return e; break;
            case ListExpr l: foreach (var e in l.Elements) yield return e; break;
            case TupleExpr t: foreach (var e in t.Elements) yield return e; break;
            case FStringExpr f: foreach (var part in f.Parts) yield return part.Expr; break;
            case SliceExpr s: yield return s.Start; yield return s.Stop; yield return s.Step; break;
            case IndexExpr i: yield return i.Target; yield return i.Index; break;
            case ListCompExpr l:
                yield return l.Element; yield return l.Iterable; yield return l.Iterable2; yield return l.Filter;
                break;
            case MemberAccessExpr m: yield return m.Object; break;
            case CallExpr c: yield return c.Callee; foreach (var a in c.Args) yield return a; break;
            case StarArgExpr s: yield return s.Value; break;
            case KeywordArgExpr k: yield return k.Value; break;
            case BinaryExpr b: yield return b.Left; yield return b.Right; break;
            case UnaryExpr u: yield return u.Operand; break;
            case AwaitExpr a: yield return a.Operand; break;
            case YieldExpr y: yield return y.Value; break;
            case WalrusExpr w: yield return w.Value; break;
            case TernaryExpr t: yield return t.Condition; yield return t.TrueVal; yield return t.FalseVal; break;
            case LambdaExpr l: foreach (var prm in l.Params) yield return prm; yield return l.Body; break;
        }
    }
}

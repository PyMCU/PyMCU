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

using PyMCU.Common.Models;

namespace PyMCU.Frontend;

// Walks the AST and eliminates compile-time if/match blocks, promoting the
// selected branch into the surrounding statement list.
// Condition and pattern evaluation is delegated to CompileTimeEvaluator.
public class ConditionalCompilator(DeviceConfig config)
{
    private readonly CompileTimeEvaluator _evaluator = new(config);

    // Current module name — set by ConditionalCompilationProcessor before Process().
    public string ModuleName
    {
        get => _evaluator.ModuleName;
        set => _evaluator.ModuleName = value;
    }

    // Set while walking a function or class body, so an import promoted out of one is
    // marked as what it is. See ImportStmt.InFunctionScope.
    private bool _inFunctionBody;

    public void Process(ProgramNode program)
    {
        var newGlobals = new List<Statement>();
        _inFunctionBody = false;

        foreach (var stmt in program.GlobalStatements)
        {
            if (!ProcessStatement(stmt, program, newGlobals))
                newGlobals.Add(stmt);
        }

        program.GlobalStatements.Clear();
        program.GlobalStatements.AddRange(newGlobals);

        foreach (var stmt in program.GlobalStatements)
        {
            _inFunctionBody = false;
            ProcessBlock(stmt, program);
        }

        foreach (var func in program.Functions)
        {
            _inFunctionBody = true;
            ProcessBlock(func.Body, program);
        }
        _inFunctionBody = false;
    }

    // Moves all statements in a Block into newStmts, routing ImportStmt to prog.Imports.
    // Recursively applies static evaluation so nested compile-time blocks
    // (e.g. `match __CHIP__.arch:` inside a `case _:` body) are also eliminated.
    private void FlushBlock(Statement? body, ProgramNode prog, List<Statement> newStmts)
    {
        if (body is not Block block) return;

        foreach (var inner in block.Statements)
        {
            if (inner is ImportStmt imp)
                prog.Imports.Add(CloneImport(imp));
            else if (!ProcessStatement(inner, prog, newStmts))
                newStmts.Add(inner);
        }
    }

    private void ProcessBlock(Statement? stmt, ProgramNode prog)
    {
        while (true)
        {
            switch (stmt)
            {
                case null:
                    return;
                case Block block:
                {
                    var newStmts = new List<Statement>();
                    foreach (var s in block.Statements)
                    {
                        if (!ProcessStatement(s, prog, newStmts))
                            newStmts.Add(s);
                    }

                    block.Statements.Clear();
                    block.Statements.AddRange(newStmts);

                    foreach (var s in block.Statements) ProcessBlock(s, prog);
                    break;
                }
                case FunctionDef func:
                    _inFunctionBody = true;
                    stmt = func.Body;
                    continue;
                case ClassDef cls:
                    _inFunctionBody = true;
                    stmt = cls.Body;
                    continue;
                case WhileStmt loop:
                    stmt = loop.Body;
                    continue;
                case ForStmt forLoop:
                    stmt = forLoop.Body;
                    continue;
                case IfStmt ifStmt:
                {
                    ProcessBlock(ifStmt.ThenBranch, prog);
                    foreach (var branch in ifStmt.ElifBranches) ProcessBlock(branch.Body, prog);
                    if (ifStmt.ElseBranch != null)
                    {
                        stmt = ifStmt.ElseBranch;
                        continue;
                    }

                    break;
                }
                case MatchStmt matchStmt:
                {
                    foreach (var branch in matchStmt.Branches) ProcessBlock(branch.Body, prog);
                    break;
                }
            }

            break;
        }
    }

    /// <summary>
    /// The statements a try holding an optional import reduces to, or null when it holds none
    /// and must keep its own lowering (#351).
    ///
    /// The try body when every optional import in it resolved, the first handler's body when
    /// any did not. `else` is folded in with the body, as Python runs it when nothing raised;
    /// a `finally` runs either way, so it follows the chosen branch.
    /// </summary>
    private static List<Statement>? FoldedBranchOf(TryStmt tryStmt)
    {
        // `except NotImplementedError:` around an import (the inner Adafruit TYPE_CHECKING
        // guard) is not the optional-import idiom: an import raises ImportError, which this
        // handler does not catch, so the handler never runs. Fold to the body so the names
        // it bound stay in scope (#480) and the handler's stub import is not loaded (#481).
        if (!ConditionalImportExtractor.CatchesImportError(tryStmt) && IsImportOnlyBody(tryStmt.Body))
        {
            var taken = new List<Statement>(tryStmt.Body);
            if (tryStmt.ElseBody != null) taken.AddRange(tryStmt.ElseBody);
            if (tryStmt.Finally != null) taken.AddRange(tryStmt.Finally);
            return taken;
        }

        var optional = tryStmt.Body.OfType<ImportStmt>().Where(i => i.IsOptional).ToList();
        if (optional.Count == 0) return null;

        var chosen = new List<Statement>();
        if (optional.Any(i => i.OptionalLoadFailed))
        {
            // A sibling import failed, so the handler runs -- but an optional import that
            // RESOLVED already bound its names, and CPython keeps them bound: `from typing
            // import Tuple` then `from circuitpython_typing import X` leaves Tuple usable
            // even as the except takes over. Folding the whole body to the handler dropped
            // those names entirely -- not imported, not typing-only -- and `-> Tuple:` then
            // read as an unknown type. Keep the resolved imports ahead of the handler.
            chosen.AddRange(optional.Where(i => !i.OptionalLoadFailed));
            if (tryStmt.Handlers.Count > 0) chosen.AddRange(tryStmt.Handlers[0].Handler);
        }
        else
        {
            chosen.AddRange(tryStmt.Body);
            if (tryStmt.ElseBody != null) chosen.AddRange(tryStmt.ElseBody);
        }
        if (tryStmt.Finally != null) chosen.AddRange(tryStmt.Finally);
        return chosen;
    }

    /// <summary>
    /// Records `Name = &lt;type expression&gt;` assignments in a try body that is being
    /// discarded because an optional import in it failed. The expression counts as a type
    /// only when its head names something the failed import would have bound (or an alias
    /// already recorded): `_USE_PULSEIO = True` in the same body is a value, and is left
    /// alone.
    /// </summary>
    private static void RecordDiscardedTypeAliases(List<Statement> body, ProgramNode prog)
    {
        foreach (var s in body)
        {
            string? name = s switch
            {
                AssignStmt { Target: VariableExpr v } => v.Name,
                AnnAssign an => an.Target,
                _ => null,
            };
            Expression? value = s switch
            {
                AssignStmt a => a.Value,
                AnnAssign an => an.Value,
                _ => null,
            };
            if (name == null || value == null || !IsTypeAliasValue(value, prog)) continue;
            prog.TypeAliases[name] =
                PyMCU.Common.AnnotationText.Normalize(RenderTypeAnnotation(value));
        }
    }

    /// <summary>
    /// The heads a type alias's right-hand side may be built on. The `typing` spellings a
    /// module gets from a skipped-builtin or folded import, plus the annotation forms this
    /// compiler already reads -- `Union`, `Optional`, `Tuple`, the sequence spellings, and
    /// the `|`-union members.
    /// </summary>
    private static readonly HashSet<string> TypeAliasHeads = new()
    {
        "Union", "Optional", "Tuple", "tuple", "List", "list", "Dict", "dict", "Set", "set",
        "FrozenSet", "Callable", "Literal", "Annotated", "Type", "Sequence", "Iterable",
        "Iterator", "Mapping", "MutableMapping", "MutableSequence", "Collection", "ClassVar",
        "Final", "Generic", "TypeVar", "Protocol", "Any", "NoReturn", "NamedTuple", "Deque",
        "DefaultDict", "Counter", "ChainMap", "OrderedDict", "type", "None",
        "int", "float", "bool", "str", "bytes", "bytearray", "object",
        "uint8", "uint16", "uint32", "int8", "int16", "int32", "pin", "const",
    };

    /// <summary>
    /// Whether <paramref name="e"/> is a type expression -- renders to annotation text whose
    /// head is an annotation form, a typing-only name, or an alias already recorded. That is
    /// what separates `ColorUnion = Union[...]` from `x = foo[0]` one line lower in the same
    /// discarded body: only the first names something no run-time object stands behind.
    /// </summary>
    private static bool IsTypeAliasValue(Expression e, ProgramNode prog)
    {
        if (RenderTypeAnnotation(e) is not { } text) return false;
        string head = text;
        int cut = head.IndexOfAny(TypeHeadCuts);
        if (cut >= 0) head = head[..cut];
        head = head[(head.LastIndexOf('.') + 1)..];
        return TypeAliasHeads.Contains(head)
               || prog.TypingOnlyNames.Contains(head)
               || prog.TypeAliases.ContainsKey(head);
    }

    private static readonly char[] TypeHeadCuts = { '[', '|' };

    /// <summary>
    /// Renders the shapes a type alias is written in -- `Union[a, b]`, `Optional[x]`,
    /// `Tuple[int, int]`, `a | b`, a bare or dotted name -- back to the annotation text
    /// CheckAnnotationNames reads. Anything else (a call, an arithmetic expression, a
    /// subscript on a runtime value) answers null, and the assignment is not an alias.
    /// </summary>
    private static string? RenderTypeAnnotation(Expression e)
    {
        switch (e)
        {
            case VariableExpr v:
                return v.Name;
            case MemberAccessExpr m:
            {
                string? obj = RenderTypeAnnotation(m.Object);
                return obj == null ? null : obj + "." + m.Member;
            }
            case IndexExpr ix:
            {
                string? head = RenderTypeAnnotation(ix.Target);
                string? args = ix.Index is TupleExpr te
                    ? RenderTypeAnnotationList(te.Elements)
                    : RenderTypeAnnotation(ix.Index);
                return head == null || args == null ? null : head + "[" + args + "]";
            }
            case BinaryExpr { Op: BinaryOp.BitOr } b:
            {
                string? l = RenderTypeAnnotation(b.Left);
                string? r = RenderTypeAnnotation(b.Right);
                return l == null || r == null ? null : l + "|" + r;
            }
            case IntegerLiteral il:
                return il.Value.ToString();
            case StringLiteral sl:
                return "\"" + sl.Value + "\"";
            default:
                return null;
        }
    }

    private static string? RenderTypeAnnotationList(List<Expression> elements)
    {
        var parts = new List<string>();
        foreach (var el in elements)
        {
            if (RenderTypeAnnotation(el) is not { } p) return null;
            parts.Add(p);
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// True when the try body is only imports (and nested import-only tries / pass).
    /// The Adafruit inner guard is that shape; a try that also runs runtime code keeps
    /// its handler.
    /// </summary>
    private static bool IsImportOnlyBody(List<Statement> body)
    {
        if (body.Count == 0) return false;
        foreach (var s in body)
        {
            switch (s)
            {
                case ImportStmt:
                case PassStmt:
                    continue;
                case TryStmt inner when IsImportOnlyBody(inner.Body):
                    continue;
                default:
                    return false;
            }
        }
        return true;
    }

    private ImportStmt CloneImport(ImportStmt src) =>
        new(src.ModuleName, [..src.Symbols], src.RelativeLevel)
        {
            InFunctionScope = src.InFunctionScope || _inFunctionBody,
            ModuleAlias = src.ModuleAlias,
            Aliases = new Dictionary<string, string>(src.Aliases),
            WasStarImport = src.WasStarImport,
            IsOptional = src.IsOptional,
            Line = src.Line,
        };

    // `if TYPE_CHECKING:` or `if typing.TYPE_CHECKING:` -- the bare name (however it was
    // imported, typically under the very same kind of guard this file already folds) or the
    // dotted spelling written without importing the name at all. Only this exact shape: a
    // compound condition (`if x or TYPE_CHECKING:`) is not this idiom and is left to the
    // generic evaluator, which reports its own "Unsupported condition" rather than silently
    // taking a branch on a guess about the other operand.
    private static bool IsTypeCheckingGuard(Expression? cond) => cond switch
    {
        VariableExpr { Name: "TYPE_CHECKING" } => true,
        MemberAccessExpr { Object: VariableExpr, Member: "TYPE_CHECKING" } => true,
        _ => false,
    };

    // Returns the winning branch body for a compile-time if/elif/else.
    // Throws if any condition is not evaluable at compile time.
    private Statement? ChooseBranch(IfStmt ifStmt)
    {
        if (_evaluator.EvaluateCondition(ifStmt.Condition)) return ifStmt.ThenBranch;

        foreach (var (cond, body) in ifStmt.ElifBranches)
        {
            if (_evaluator.EvaluateCondition(cond)) return body;
        }

        return ifStmt.ElseBranch;
    }

    private bool ProcessStatement(Statement stmt, ProgramNode prog, List<Statement> newStmts)
    {
        switch (stmt)
        {
            case ImportStmt imp:
                prog.Imports.Add(CloneImport(imp));
                return true;
            // A `try` holding an optional import is a COMPILE-TIME branch, like `if __CHIP__`
            // above it (#351). Whether the module is there is decided by the loader and by
            // nothing at run time, so one of the two branches is dead and folding it away is
            // what keeps the program honest: the try body's `_USE_PULSEIO = True` used to be
            // emitted whether or not pulseio existed, because there is no run-time ImportError
            // for the handler to catch. The flag then said True on a chip with no pulseio, and
            // the program took a branch that cannot work.
            //
            // Untouched for every other try: a try with no optional import in it is ordinary
            // control flow and keeps its own lowering.
            case TryStmt tryStmt when FoldedBranchOf(tryStmt) is { } chosen:
                // The names the absent module would have bound (#367). Once the try folds to
                // its handler the import is gone, and with it every trace that `Type` and
                // `TracebackType` are annotation names standing for nothing at run time rather
                // than types the reader misspelled. Recorded here because this is the last
                // place that knows.
                foreach (var failed in tryStmt.Body.OfType<ImportStmt>()
                             .Where(i => i.IsOptional && i.OptionalLoadFailed))
                {
                    foreach (var sym in failed.Symbols) prog.TypingOnlyNames.Add(sym);
                    foreach (var alias in failed.Aliases.Values) prog.TypingOnlyNames.Add(alias);
                    if (!string.IsNullOrEmpty(failed.ModuleAlias))
                        prog.TypingOnlyNames.Add(failed.ModuleAlias!);

                    // `ColorUnion = Union[int, Tuple[int, int, int]]` sits next to the failed
                    // `from typing import Union` -- a type alias, not a value binding. When
                    // the body is discarded the name vanishes with it, and `color: ColorUnion`
                    // then reads as an unknown type in a signature the program may never
                    // even call. Record the alias text so the annotation reader resolves the
                    // name to the union it spelled.
                    RecordDiscardedTypeAliases(tryStmt.Body, prog);
                }
                foreach (var inner in chosen)
                {
                    if (inner is ImportStmt nested) prog.Imports.Add(CloneImport(nested));
                    else if (!ProcessStatement(inner, prog, newStmts)) newStmts.Add(inner);
                }
                return true;
            // `if TYPE_CHECKING:` is the other spelling of an optional-import guard (#367):
            // TYPE_CHECKING is False at run time by definition (a type checker sets it True,
            // this compiler never does), so the body never runs and its imports bind nothing
            // here either. Handled before the generic IfStmt case below, because
            // CompileTimeEvaluator has no notion of TYPE_CHECKING and would otherwise throw
            // "Unsupported condition", leaving the whole `if` as ordinary (and unresolvable)
            // control flow -- which is what used to happen, so the names it imports (a
            // `circuitpython_typing` device driver, most often) were plain undefined rather
            // than typing-only.
            case IfStmt { Condition: var cond } ifStmt when IsTypeCheckingGuard(cond):
            {
                foreach (var imp in (ifStmt.ThenBranch as Block)?.Statements.OfType<ImportStmt>()
                                     ?? Enumerable.Empty<ImportStmt>())
                {
                    foreach (var sym in imp.Symbols) prog.TypingOnlyNames.Add(sym);
                    foreach (var alias in imp.Aliases.Values) prog.TypingOnlyNames.Add(alias);
                    if (!string.IsNullOrEmpty(imp.ModuleAlias))
                        prog.TypingOnlyNames.Add(imp.ModuleAlias!);
                }
                // No `else` on this idiom in practice, but Python allows one and it DOES run.
                if (ifStmt.ElseBranch != null) FlushBlock(ifStmt.ElseBranch, prog, newStmts);
                return true;
            }
            case IfStmt ifStmt:
                try
                {
                    FlushBlock(ChooseBranch(ifStmt), prog, newStmts);
                    return true;
                }
                catch
                {
                    return false;
                }
            case MatchStmt matchStmt:
                try
                {
                    var targetVal = _evaluator.Resolve(matchStmt.Target);

                    foreach (var branch in matchStmt.Branches.Where(branch =>
                                 _evaluator.MatchesPattern(branch.Pattern, targetVal)))
                    {
                        FlushBlock(branch.Body, prog, newStmts);
                        return true;
                    }

                    return true; // No case matched — eliminate the match
                }
                catch
                {
                    return false;
                }
            // `ColorUnion = Union[int, Tuple[int, int, int]]` -- a type alias, not a value
            // binding. The right-hand side is built from annotation spellings (`Union`,
            // `Optional`, `Tuple`, a name a folded import marked typing-only) and no run-time
            // object exists for it to bind, so the statement lowers as nothing and the name
            // is what annotations resolve it to.
            case AssignStmt { Target: VariableExpr aliasTarget, Value: { } aliasValue }
                    when IsTypeAliasValue(aliasValue, prog):
                prog.TypeAliases[aliasTarget.Name] =
                    PyMCU.Common.AnnotationText.Normalize(RenderTypeAnnotation(aliasValue));
                return true;
            case AnnAssign { Value: { } annAliasValue } annAlias
                    when IsTypeAliasValue(annAliasValue, prog):
                prog.TypeAliases[annAlias.Target] =
                    PyMCU.Common.AnnotationText.Normalize(RenderTypeAnnotation(annAliasValue));
                return true;
            default:
                return false;
        }
    }
}
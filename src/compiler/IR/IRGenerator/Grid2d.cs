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

using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using AstBinOp = PyMCU.Frontend.BinaryOp;

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    // A compile-time 2-D grid is the spelling a CircuitPython program uses for a
    // table: `g = [[v] * W for _ in range(H)]`, `g = [bytearray(W) for _ in
    // range(H)]`, or `self.g = <same>` as a field. It lowers to ONE flat fixed
    // array of W * H elements; `g[y][x]` is `g[y * W + x]` with the same index
    // arithmetic the hand-flattened spelling emits, so the grid costs nothing.
    //
    // A row `g[y]` is deliberately NOT a value: there is no row object. Reads
    // and writes may touch its elements (`g[y][x]`), its length (`len(g[y])`)
    // or its elements as an iterable (`for x in g[y]`); a bare `g[y]` anywhere
    // else is refused. The one escape is `for row in g`, where `row` is a view
    // pinned to the loop counter, and `r = g[y]` where every later use of `r`
    // in the same block is `r[x]` / `len(r)` / `for x in r` -- verified by a
    // scan of the block's statements before the binding is emitted.
    private readonly Dictionary<string, (int W, int H)> gridDims = new();

    private sealed class GridRowAlias
    {
        public required string GridKey { get; init; }
        // Evaluated per use: the loop counter for `for row in g`, or a pinned
        // temp for `r = g[y]` so `y` is evaluated once at the bind.
        public required Expression RowIndexExpr { get; set; }
        // The exact IndexExpr/CallExpr nodes the block scan approved; null for
        // a loop-carried alias, whose nodes are revisited per unrolled iteration.
        public HashSet<ASTNode>? Uses { get; set; }
    }

    private readonly Dictionary<string, GridRowAlias> gridRowAliases = new();
    private int gridTempId = 0;

    private const string GridDimsConstMsg =
        "a grid's dimensions must be compile-time constants -- a literal, a const " +
        "name, or a constructor argument that is a literal at every call site, " +
        "exactly like a fixed array's size";

    // ---------- shape recognition --------------------------------------------

    // `g = [[v]*W for _ in range(H)]` / `g = [bytearray(W) for _ in range(H)]`:
    // a single for-comprehension whose element is one row.
    private static bool IsGridComprehension(ListCompExpr comp) =>
        comp.Filter == null && IsGridRowShape(comp.Element, out _, out _);

    // The ELEMENT of a grid comprehension: `[e]*<count>` or `bytearray(<count>)`.
    private static bool IsGridRowShape(Expression e, out CallExpr? rowByteCtor, out Expression? rowListRepeat)
    {
        rowByteCtor = null;
        rowListRepeat = null;
        switch (e)
        {
            case CallExpr { Callee: VariableExpr { Name: "bytearray" }, Args: [Expression oneArg] } ba:
                rowByteCtor = ba;
                return true;
            case BinaryExpr { Op: AstBinOp.Mul, Left: ListExpr lit, Right: Expression cnt } br
                when lit.Elements.Count >= 1:
                rowListRepeat = cnt;
                return true;
            case BinaryExpr { Op: AstBinOp.Mul, Left: Expression cnt2, Right: ListExpr lit2 } br2
                when lit2.Elements.Count >= 1:
                rowListRepeat = cnt2;
                return true;
            default:
                return false;
        }
    }

    // `[[v]*W] * H` / `[bytearray(W)] * H`: CPython makes H names for ONE row
    // object, so g[0][1] = v writes every row. It is refused outright rather
    // than lowered as if the rows were independent -- that would be unfaithful.
    private static bool IsAliasedRowRepeat(Expression e) =>
        e is BinaryExpr { Op: AstBinOp.Mul } b
        && ((b.Left is ListExpr l && l.Elements.Count >= 1 && l.Elements.All(el => el is ListExpr || IsGridRowShape(el, out _, out _)))
            || (b.Right is ListExpr l2 && l2.Elements.Count >= 1 && l2.Elements.All(el => el is ListExpr || IsGridRowShape(el, out _, out _))));

    private PyMCU.Common.CompilerError AliasedRowRepeatError(ASTNode at) => UserError(
        "`[row] * H` creates H aliases of ONE row object -- writing g[0][1] = v " +
        "writes the same cell of every row. Write the comprehension, which gives " +
        "each row its own storage and compiles to one flat array: " +
        "g = [[0] * W for _ in range(H)]", at);

    // ---------- dimensions ----------------------------------------------------

    private (int W, int H) GridDimsOf(ListCompExpr comp, ASTNode? at = null)
    {
        at ??= comp;
        if (comp.Iterable is not CallExpr { Callee: VariableExpr { Name: "range" } } rangeCall)
            throw UserError(GridDimsConstMsg, comp.Iterable);
        int h = FoldRangeCount(rangeCall, comp.VarName, at);
        int w;
        if (IsGridRowShape(comp.Element, out var baCtor, out var repeatCount)
            && baCtor != null)
            w = FoldGridDim(baCtor.Args[0], "width", at);
        else if (repeatCount != null)
            w = FoldGridDim(repeatCount, "width", at);
        else
            throw UserError(GridDimsConstMsg, comp.Element);
        if (w <= 0)
            throw UserError($"a grid needs a positive row width; this one is {w}", at);
        if (h <= 0)
            throw UserError($"a grid needs a positive height; this one is {h}", at);
        if ((long)w * h > ushort.MaxValue)
            throw UserError($"a {w}x{h} grid is {w * (long)h} elements -- the flat array cannot exceed 65535", at);
        return (w, h);
    }

    private int FoldGridDim(Expression e, string axis, ASTNode at)
    {
        int v;
        try { v = EvaluateConstantExpr(e); }
        catch (Exception) { throw UserError(GridDimsConstMsg, e); }
        if (v == 0)
            throw UserError($"a grid's {axis} cannot be zero", e);
        return v;
    }

    // `range(n)` / `range(a, b)` / `range(a, b, s)` as a row count; the var name
    // is only substituted when the element mentions it, and here it never does.
    private int FoldRangeCount(CallExpr rangeCall, string varName, ASTNode at)
    {
        if (rangeCall.Args.Count is < 1 or > 3)
            throw UserError("range() takes 1 to 3 arguments", rangeCall);
        long start = 0, stop, step = 1;
        try
        {
            switch (rangeCall.Args.Count)
            {
                case 1:
                    stop = EvaluateConstantExpr(rangeCall.Args[0]);
                    break;
                case 2:
                    start = EvaluateConstantExpr(rangeCall.Args[0]);
                    stop = EvaluateConstantExpr(rangeCall.Args[1]);
                    break;
                default:
                    start = EvaluateConstantExpr(rangeCall.Args[0]);
                    stop = EvaluateConstantExpr(rangeCall.Args[1]);
                    step = EvaluateConstantExpr(rangeCall.Args[2]);
                    break;
            }
        }
        catch (Exception)
        {
            throw UserError(GridDimsConstMsg, rangeCall);
        }
        if (step <= 0)
            throw UserError("a grid's rows must count upward -- range() needs a positive step", rangeCall);
        return (int)Math.Max(0, (stop - start + step - 1) / step);
    }

    private bool TryFoldGridDims(ListCompExpr comp, out (int W, int H) dims)
    {
        try { dims = GridDimsOf(comp); return true; }
        catch { dims = default; return false; }
    }

    // The element type of the flat array: `bytearray(W)` rows are uint8, `[v]*W`
    // rows take the type of the row's elements.
    private DataType GridElemTypeOf(ListCompExpr comp)
    {
        if (comp.Element is CallExpr) return DataType.UINT8;
        if (comp.Element is BinaryExpr { Op: AstBinOp.Mul } be)
        {
            var lit = (be.Left as ListExpr) ?? (be.Right as ListExpr);
            if (lit != null)
            {
                var vals = new List<int>();
                bool folded = true;
                foreach (var el in lit.Elements)
                {
                    if (TryEvalElemConst(el, out int c)) vals.Add(c);
                    else { folded = false; break; }
                }
                if (folded && vals.Count > 0) return WidestElemType(vals);
                return InferExprType(lit.Elements[0]);
            }
        }
        return DataType.UINT8;
    }

    // ---------- name resolution -----------------------------------------------

    // The storage key a subscript of `name` resolves to, mirroring VisitIndex's
    // array resolution: current inline prefix, the function scope, the alias
    // chain, then module scope -- unless a local binding shadows the name.
    private string? GridKeyOfName(string name)
    {
        string qualified = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : name);
        if (gridDims.ContainsKey(qualified)) return qualified;
        if (variableAliases.TryGetValue(qualified, out var aq) && aq != null)
        {
            string followed = FollowAliases(aq);
            if (gridDims.ContainsKey(followed)) return followed;
        }
        if (LocalScopeBinds(name)) return null;
        if (gridDims.ContainsKey(name)) return name;
        if (!string.IsNullOrEmpty(currentFunction))
        {
            string mod = ModuleScopeArrayName(qualified);
            if (mod != qualified && gridDims.ContainsKey(mod)) return mod;
        }
        return null;
    }

    private string? ResolveGridKey(Expression e) => e switch
    {
        VariableExpr v => GridKeyOfName(v.Name),
        MemberAccessExpr m => ProbeMemberGridKey(m),
        _ => null
    };

    // The member branch is a PROBE: `mem.Object` may name something not yet
    // defined (`cls` inside a classmethod scan, a module the import never
    // bound). ResolveMemberArrayName lowers the object expression and would
    // throw a name error for a question the grid tables alone cannot answer --
    // catch it and answer null; the real path reports the name properly.
    private string? ProbeMemberGridKey(MemberAccessExpr m)
    {
        string? flat;
        try { flat = ResolveMemberArrayName(m); }
        catch (PyMCU.Common.CompilerError) { return null; }
        return flat != null && gridDims.ContainsKey(flat) ? flat : null;
    }

    // `g[y]` -- the row is an IndexExpr over a grid whose index is a scalar.
    private bool IsGridRowExpr(Expression e) =>
        e is IndexExpr { Index: not SliceExpr and not TupleExpr } ix
        && ResolveGridKey(ix.Target) != null;

    // A grid passed to an OUTLINED callee arrives as a base pointer: inside,
    // `param[y]` reads a flat element where Python reads a row -- the callee
    // cannot see the shape. Refuse at the call, where the grid is still named.
    // (An inline callee keeps the caller's name through variableAliases, so its
    // `param[y][x]` / `len(param)` / `for row in param` resolve faithfully and
    // a bare `param[y]` meets the row refusal there.)
    private void RefuseGridArgument(Expression arg)
    {
        if (ResolveGridKey(arg) != null)
            throw UserError(
                "a 2-D grid cannot be passed to an outlined function -- inside the " +
                "callee the rows are gone, so `param[y]` reads a flat element where " +
                "Python reads a row. Index elements at the call site (g[y][x]), " +
                "mark the callee @inline, or pass a flat bytearray.", arg);
    }

    // `r` -- a name bound to a row: `r = g[y]` with approved uses, or the loop
    // variable of `for row in g`.
    private GridRowAlias? ResolveRowRef(Expression e)
    {
        if (e is not VariableExpr v) return null;
        if (gridRowAliases.TryGetValue(currentInlinePrefix + v.Name, out var rr)) return rr;
        if (!string.IsNullOrEmpty(currentFunction)
            && gridRowAliases.TryGetValue(currentFunction + "." + v.Name, out rr)) return rr;
        if (gridRowAliases.TryGetValue(v.Name, out rr)) return rr;
        return null;
    }

    // ---------- emission --------------------------------------------------------

    private void RegisterGrid(string key, int w, int h, DataType elemDt, bool moduleLevel = false)
    {
        arraySizes[key] = w * h;
        gridDims[key] = (w, h);
        arrayElemTypes[key] = elemDt;
        variableTypes[key] = elemDt;
        arraysWithVariableIndex.Add(key);
        // A module-level array is a global allocation (the overlay algorithm must
        // not alias it with locals across calls); a local or a field is not.
        if (moduleLevel) moduleSramArrays.Add(key);
    }

    // `y` evaluated once, range-checked against the grid dimension it indexes
    // when it is a compile-time constant (a negative constant normalizes like
    // any array index: g[-1] is the last row).
    private Val EvalGridIndex(Expression e, int bound, string axis)
    {
        Val v = VisitExpression(e);
        if (v is Constant c)
        {
            int idx = c.Value < 0 ? c.Value + bound : c.Value;
            if (idx < 0 || idx >= bound)
                throw new IndexError(
                    $"{axis} index {c.Value} out of range for a grid of {bound}",
                    e.Line > 0 ? e.Line : lastLine, e.Column);
            return new Constant(idx);
        }
        return v;
    }

    // rowV * W + colV -- the same mul+add the hand-flattened `g[y * W + x]`
    // spelling emits; constants fold to one index.
    private Val GridFlatIndex(Val rowV, Val colV, int w)
    {
        Val mul = rowV is Constant rc
            ? new Constant(rc.Value * w)
            : VisitExpression(new BinaryExpr(new PreEvaluatedExpr(rowV, null), AstBinOp.Mul,
                new IntegerLiteral(w)));
        if (mul is Constant mc && colV is Constant cc) return new Constant(mc.Value + cc.Value);
        return VisitExpression(new BinaryExpr(new PreEvaluatedExpr(mul, null), AstBinOp.Add,
            new PreEvaluatedExpr(colV, null)));
    }

    private Val EmitGridElemLoadVal(string gridKey, Val rowV, Val colV)
    {
        var (w, _) = gridDims[gridKey];
        Val flat = GridFlatIndex(rowV, colV, w);
        RemapArrayAccess(gridKey, flat, out var name, out var midx, out var size, out var dt);
        Temporary tmp = MakeTemp(dt);
        Emit(new ArrayLoad(name, midx, tmp, dt, size));
        return tmp;
    }

    private void EmitGridElemStoreVal(string gridKey, Val rowV, Val colV, Val src)
    {
        var (w, _) = gridDims[gridKey];
        Val flat = GridFlatIndex(rowV, colV, w);
        RemapArrayAccess(gridKey, flat, out var name, out var midx, out var size, out var dt);
        Emit(new ArrayStore(name, midx, src, dt, size));
    }

    private Val EmitGridElemLoad(string gridKey, Expression rowExpr, Expression colExpr)
    {
        var (w, h) = gridDims[gridKey];
        return EmitGridElemLoadVal(gridKey,
            EvalGridIndex(rowExpr, h, "row"), EvalGridIndex(colExpr, w, "column"));
    }

    private void EmitGridElemStore(string gridKey, Expression rowExpr, Expression colExpr, Expression valueExpr)
    {
        var (w, h) = gridDims[gridKey];
        Val rowV = EvalGridIndex(rowExpr, h, "row");
        Val colV = EvalGridIndex(colExpr, w, "column");
        EmitGridElemStoreVal(gridKey, rowV, colV, VisitExpression(valueExpr));
    }

    private Val EmitRowElemLoad(GridRowAlias rr, Expression colExpr)
    {
        var (w, _) = gridDims[rr.GridKey];
        return EmitGridElemLoadVal(rr.GridKey,
            VisitExpression(rr.RowIndexExpr), EvalGridIndex(colExpr, w, "column"));
    }

    private void EmitRowElemStore(GridRowAlias rr, Expression colExpr, Val src)
    {
        var (w, _) = gridDims[rr.GridKey];
        EmitGridElemStoreVal(rr.GridKey,
            VisitExpression(rr.RowIndexExpr), EvalGridIndex(colExpr, w, "column"), src);
    }

    // ---------- initialization --------------------------------------------------

    // One row's elements, evaluated once each and stored in order.
    private void EmitGridFill(string key, ListCompExpr comp, int w, int h, DataType elemDt)
    {
        var rowExprs = ExpandCtListComp(comp);
        if (rowExprs == null || rowExprs.Count != h)
            throw UserError("a grid's rows could not be expanded at compile time", comp);
        int k = 0;
        foreach (var rowExpr in rowExprs)
        {
            if (rowExpr is CallExpr { Callee: VariableExpr { Name: "bytearray" } })
            {
                for (int x = 0; x < w; x++)
                    Emit(new ArrayStore(key, new Constant(k++), new Constant(0), elemDt, w * h));
                continue;
            }
            if (rowExpr is BinaryExpr { Op: AstBinOp.Mul } be)
            {
                var lit = (be.Left as ListExpr) ?? (be.Right as ListExpr);
                if (lit == null)
                    throw UserError("a grid row must be `[v] * W` or `bytearray(W)`", rowExpr);
                for (int x = 0; x < w; x++)
                    Emit(new ArrayStore(key, new Constant(k++), VisitExpression(lit.Elements[x % lit.Elements.Count]), elemDt, w * h));
                continue;
            }
            throw UserError("a grid row must be `[v] * W` or `bytearray(W)`", rowExpr);
        }
    }

    // `g = [[v]*W for _ in range(H)]` as a local or module-level binding.
    private void EmitLocalGridInit(string name, ListCompExpr comp, ASTNode at)
    {
        var (w, h) = GridDimsOf(comp, at);
        DataType elemDt = GridElemTypeOf(comp);
        string qualified = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : name);
        // A module-level grid is filed under its BARE name by ScanGlobals; the
        // replay of the declaration inside the synthesized init aliases the
        // qualified spelling onto it -- the same rule `name = bytearray(N)`
        // follows.
        bool replayingModuleLevel = string.IsNullOrEmpty(currentInlinePrefix)
            && (currentFunction == "main"
                || currentFunction.EndsWith("___module_init", StringComparison.Ordinal));
        bool moduleLevel = false;
        if (replayingModuleLevel && !arraySizes.ContainsKey(qualified) && arraySizes.ContainsKey(name))
        {
            variableAliases[qualified] = name;
            qualified = name;
            moduleLevel = true;
        }
        else if (string.IsNullOrEmpty(currentFunction) && string.IsNullOrEmpty(currentInlinePrefix))
        {
            moduleLevel = true;
        }
        RegisterGrid(qualified, w, h, elemDt, moduleLevel);
        EmitGridFill(qualified, comp, w, h, elemDt);
    }

    // `self.g = [[v]*W for _ in range(H)]` -- a grid FIELD: one flat array per
    // instance, named `<instance>_<member>` exactly like `self.buf = bytearray(n)`.
    private void EmitMemberGridInit(MemberAccessExpr mem, ListCompExpr comp, DataType? elemDt = null)
    {
        var (w, h) = GridDimsOf(comp, mem);
        var objVal = VisitExpression(mem.Object);
        string? baseName = objVal is Variable v ? v.Name : (objVal is Temporary t ? t.Name : null);
        while (baseName != null && variableAliases.TryGetValue(baseName, out var alias)) baseName = alias;
        if (string.IsNullOrEmpty(baseName))
            throw UserError("a grid field can only be initialized on a directly resolved instance", mem.Object);
        string flat = baseName + "_" + mem.Member;
        DataType dt = elemDt ?? GridElemTypeOf(comp);
        RegisterGrid(flat, w, h, dt);
        EmitGridFill(flat, comp, w, h, dt);
    }

    // ---------- row aliases ------------------------------------------------------

    // `r = g[y]` binds a row view. It is legal only while every later use of
    // `r` in this block is an element read/write, `len(r)`, or `for x in r`;
    // the scan that proves it runs BEFORE the binding is emitted. Returns false
    // when `r` is never indexed at all -- the statement is then lowered like
    // any `x = <row>` and refused there.
    private bool TryBindRowAlias(VariableExpr target, Expression rowIndexExpr, string gridKey,
                                 IReadOnlyList<Statement> stmts, int index)
    {
        var acc = new GridRowAlias { GridKey = gridKey, RowIndexExpr = rowIndexExpr, Uses = new() };
        ScanRowAliasUses(stmts, index + 1, target.Name, acc, rebindRefuses: false);
        if (acc.Uses.Count == 0) return false;
        // Pin the row index: `r = g[y]` evaluates y once, at the bind.
        Val rowV = EvalGridIndex(rowIndexExpr, gridDims[gridKey].H, "row");
        if (rowV is Constant rc)
        {
            acc.RowIndexExpr = new IntegerLiteral(rc.Value) { Line = rowIndexExpr.Line };
        }
        else
        {
            string pin = "__gr" + (++gridTempId);
            string pinKey = QualifyLoopVar(pin);
            DataType pinType = GetValType(rowV);
            variableTypes[pinKey] = pinType;
            Emit(new Copy(rowV, new Variable(pinKey, pinType)));
            acc.RowIndexExpr = new VariableExpr(pin) { Line = rowIndexExpr.Line };
        }
        gridRowAliases[QualifyLoopVar(target.Name)] = acc;
        boundNames.Add(QualifyLoopVar(target.Name));
        return true;
    }

    // `for row in g` lowers to `for <ctr> in range(H)` with `row` bound to the
    // counter's row. `for i, row in enumerate(g)` shares the path: VarName is
    // then the visible counter and Var2Name the row.
    private void EmitGridRowLoop(ForStmt stmt, string gridKey)
    {
        var (w, h) = gridDims[gridKey];
        string rowName = !string.IsNullOrEmpty(stmt.Var2Name) ? stmt.Var2Name : stmt.VarName;
        string counter = !string.IsNullOrEmpty(stmt.Var2Name) ? stmt.VarName : "__gri" + (++gridTempId);
        var acc = new GridRowAlias
        {
            GridKey = gridKey,
            RowIndexExpr = new VariableExpr(counter) { Line = stmt.Line },
            Uses = null
        };
        var bodyList = stmt.Body is Block b ? b.Statements : new List<Statement> { stmt.Body };
        ScanRowAliasUses(bodyList, 0, rowName, acc, rebindRefuses: true);
        // The counter the row view pins to: a fresh name for `for row in g`,
        // the user's own index variable for enumerate().
        if (string.IsNullOrEmpty(stmt.Var2Name))
            variableTypes[QualifyLoopVar(counter)] = NarrowestTypeFor(0, Math.Max(h, 1));
        string rowKey = QualifyLoopVar(rowName);
        GridRowAlias? saved = gridRowAliases.TryGetValue(rowKey, out var prev) ? prev : null;
        gridRowAliases[rowKey] = acc;
        try
        {
            var syn = new ForStmt(counter,
                new IntegerLiteral(0) { Line = stmt.Line },
                new IntegerLiteral(h) { Line = stmt.Line }, null, stmt.Body) { Line = stmt.Line };
            if (loopVarReadAfter.Contains(stmt)) loopVarReadAfter.Add(syn);
            VisitStatement(syn);
        }
        finally
        {
            if (saved != null) gridRowAliases[rowKey] = saved;
            else gridRowAliases.Remove(rowKey);
        }
    }

    // `for x in g[y]` / `for x in r` walks one row's elements. The row index is
    // evaluated once, pinned, and the element loop goes through the row alias.
    private void EmitGridRowElemLoop(ForStmt stmt, Expression rowIndexExpr, string gridKey)
    {
        var (w, h) = gridDims[gridKey];
        Val rowV = EvalGridIndex(rowIndexExpr, h, "row");
        string pin = "__grr" + (++gridTempId);
        Expression pinned;
        if (rowV is Constant rc)
        {
            pinned = new IntegerLiteral(rc.Value) { Line = stmt.Line };
        }
        else
        {
            string pinKey = QualifyLoopVar(pin);
            DataType pinType = GetValType(rowV);
            variableTypes[pinKey] = pinType;
            Emit(new Copy(rowV, new Variable(pinKey, pinType)));
            // Pre-evaluated, not a name: the pin itself is registered as the row
            // alias, so visiting `pin` as a VariableExpr would trip the escape
            // refusal.
            pinned = new PreEvaluatedExpr(new Variable(pinKey, pinType), null) { Line = stmt.Line };
        }
        string seqKey = QualifyLoopVar(pin);
        gridRowAliases[seqKey] = new GridRowAlias { GridKey = gridKey, RowIndexExpr = pinned, Uses = null };
        try
        {
            EmitIndexedCounterLoop(stmt, new VariableExpr(pin) { Line = stmt.Line }, 0, w, 1);
        }
        finally
        {
            gridRowAliases.Remove(seqKey);
        }
    }

    // `name` is being bound to something that is not a row view -- any alias a
    // `name = g[y]` established earlier in the block ends here. Called wherever
    // a name binds: assignment, decl, unpack, loop variable, `with ... as`.
    private void KillRowAlias(string bareName) => gridRowAliases.Remove(QualifyLoopVar(bareName));

    // ---------- the block scan ----------------------------------------------------

    // Walks the statements that FOLLOW `name = g[y]` in a block, approving
    // exactly the uses a row view supports: `name[i]` reads/writes, `len(name)`
    // and `for x in name`. Anything that needs the row to be a value is refused
    // here, before the binding is emitted -- a row never escapes.
    private void ScanRowAliasUses(IReadOnlyList<Statement> stmts, int startAt, string name,
                                  GridRowAlias acc, bool rebindRefuses)
    {
        for (int i = startAt; i < stmts.Count; i++)
            if (ScanRowStmt(stmts[i], name, acc, topLevel: !rebindRefuses)) return;
    }

    private PyMCU.Common.CompilerError RowAliasRebindError(ASTNode s, string name) => UserError(
        $"'{name}' is bound to a row of a 2-D grid; rebinding it inside a loop, " +
        "branch or handler would leave it meaning two things on different paths -- " +
        "rebind it at block level, or use another name", s);

    // Returns true when the statement rebinds `name` at THIS level (the scan
    // ends there; the binding that follows belongs to the new value).
    private bool ScanRowStmt(Statement s, string name, GridRowAlias acc, bool topLevel)
    {
        bool Rebind() => topLevel ? true : throw RowAliasRebindError(s, name);
        void Nested(IReadOnlyList<Statement> list) => ScanRowAliasUses(list, 0, name, acc, rebindRefuses: true);
        void NestedStmt(Statement? st)
        {
            if (st is Block nb) Nested(nb.Statements);
            else if (st != null) { ScanRowStmt(st, name, acc, topLevel: false); }
        }
        switch (s)
        {
            case AssignStmt a:
                ScanRowExpr(a.Value, name, acc);
                if (a.Target is VariableExpr atv) return atv.Name == name ? Rebind() : false;
                ScanRowExpr(a.Target, name, acc);
                return false;
            case AnnAssign a:
                ScanRowExpr(a.Value, name, acc);
                return a.Target == name ? Rebind() : false;
            case VarDecl v:
                ScanRowExpr(v.Init, name, acc);
                return v.Name == name ? Rebind() : false;
            case TupleUnpackStmt t:
                ScanRowExpr(t.Value, name, acc);
                return t.Targets.Contains(name) ? Rebind() : false;
            case AugAssignStmt a:
                ScanRowExpr(a.Value, name, acc);
                if (a.Target is VariableExpr av && av.Name == name)
                    throw RowAliasNotAValue(av);
                ScanRowExpr(a.Target, name, acc);
                return false;
            case ExprStmt e: ScanRowExpr(e.Expr, name, acc); return false;
            case ReturnStmt r: ScanRowExpr(r.Value, name, acc); return false;
            case AssertStmt a: ScanRowExpr(a.Condition, name, acc); return false;
            case RaiseStmt r: ScanRowExpr(r.MessageExpr, name, acc); return false;
            case IfStmt f:
                ScanRowExpr(f.Condition, name, acc);
                NestedStmt(f.ThenBranch);
                foreach (var (_, body) in f.ElifBranches) { NestedStmt(body); }
                NestedStmt(f.ElseBranch);
                return false;
            case WhileStmt w:
                ScanRowExpr(w.Condition, name, acc);
                NestedStmt(w.Body);
                return false;
            case ForStmt f:
                if (f.Iterable is VariableExpr iv && iv.Name == name) acc.Uses?.Add(f);
                else ScanRowExpr(f.Iterable, name, acc);
                ScanRowExpr(f.RangeStart, name, acc);
                ScanRowExpr(f.RangeStop, name, acc);
                ScanRowExpr(f.RangeStep, name, acc);
                if (f.VarName == name || f.Var2Name == name) return Rebind();
                NestedStmt(f.Body);
                return false;
            case MatchStmt m:
                ScanRowExpr(m.Target, name, acc);
                foreach (var br in m.Branches)
                {
                    ScanRowExpr(br.Guard, name, acc);
                    // `case r:` captures -- binds r on one path only, so after the
                    // match the name means two different things. Refused outright.
                    if (br.CaptureName == name) throw RowAliasRebindError(m, name);
                    NestedStmt(br.Body);
                }
                return false;
            case WithStmt w:
                ScanRowExpr(w.ContextExpr, name, acc);
                if (w.AsName == name) return Rebind();
                NestedStmt(w.Body);
                return false;
            case TryStmt t:
                Nested(t.Body);
                foreach (var (_, handler) in t.Handlers) Nested(handler);
                // `except X as r:` binds r only on the exceptional path.
                foreach (var hn in t.HandlerNames)
                    if (hn == name) throw RowAliasRebindError(t, name);
                if (t.ElseBody != null) Nested(t.ElseBody);
                if (t.Finally != null) Nested(t.Finally);
                return false;
            case FunctionDef fd:
                foreach (var p in fd.Params)
                    ScanRowExpr(p.DefaultValue, name, acc);
                // A parameter named `r` binds inside the body: `def f(r)` cannot
                // capture the outer row alias no matter how often it says `r`.
                if (!fd.Params.Any(p => p.Name == name) && MentionsName(fd.Body, name))
                    throw UserError(
                        $"a nested function cannot capture '{name}' -- it is a row of a " +
                        "2-D grid, a view with no value to capture", fd);
                return false;
            case ClassDef cd:
                if (MentionsName(cd.Body, name))
                    throw UserError(
                        $"a class body cannot capture '{name}' -- it is a row of a " +
                        "2-D grid, a view with no value to capture", cd);
                return false;
            case GlobalStmt g: if (g.Names.Contains(name)) return Rebind(); return false;
            case NonlocalStmt n: if (n.Names.Contains(name)) return Rebind(); return false;
            case Block b: Nested(b.Statements); return false;
            default: return false;   // break/continue/pass/import/del
        }
    }

    private PyMCU.Common.CompilerError RowAliasNotAValue(Expression e) => UserError(
        "a row of a 2-D grid is a view into the flat array, not a value -- index " +
        "an element (r[x]), take its length (len(r)), or iterate it (for x in r); " +
        "it cannot be passed, returned, stored or compared", e);

    private void ScanRowExpr(Expression? e, string name, GridRowAlias acc, bool opaque = false)
    {
        switch (e)
        {
            case null: return;
            case VariableExpr v:
                if (v.Name == name)
                    throw opaque
                        ? UserError($"a lambda cannot capture '{name}' -- it is a row of a 2-D grid, a view with no value to capture", v)
                        : RowAliasNotAValue(v);
                return;
            case IndexExpr ix:
                if (ix.Target is VariableExpr tv && tv.Name == name)
                {
                    if (ix.Index is SliceExpr)
                        throw UserError(
                            "a row of a 2-D grid cannot be sliced -- a slice would be a " +
                            "view object; index (r[x]) or iterate (for x in r) its elements", ix.Index);
                    if (ix.Index is TupleExpr)
                        throw UserError(
                            "subscript a row with one index -- r[x]; r[x, y] is not supported", ix.Index);
                    acc.Uses?.Add(ix);
                    ScanRowExpr(ix.Index, name, acc, opaque);
                    return;
                }
                ScanRowExpr(ix.Target, name, acc, opaque);
                ScanRowExpr(ix.Index, name, acc, opaque);
                return;
            case CallExpr c:
                if (c.Callee is VariableExpr { Name: "len" } && c.Args.Count == 1
                    && c.Args[0] is VariableExpr lv && lv.Name == name && !opaque)
                {
                    acc.Uses?.Add(c);
                    return;
                }
                ScanRowExpr(c.Callee, name, acc, opaque);
                foreach (var a in c.Args) ScanRowExpr(a, name, acc, opaque);
                return;
            case MemberAccessExpr m:
                ScanRowExpr(m.Object, name, acc, opaque);
                return;
            case BinaryExpr b:
                ScanRowExpr(b.Left, name, acc, opaque);
                ScanRowExpr(b.Right, name, acc, opaque);
                return;
            case UnaryExpr u: ScanRowExpr(u.Operand, name, acc, opaque); return;
            case TernaryExpr t:
                ScanRowExpr(t.Condition, name, acc, opaque);
                ScanRowExpr(t.TrueVal, name, acc, opaque);
                ScanRowExpr(t.FalseVal, name, acc, opaque);
                return;
            case SliceExpr sl:
                ScanRowExpr(sl.Start, name, acc, opaque);
                ScanRowExpr(sl.Stop, name, acc, opaque);
                ScanRowExpr(sl.Step, name, acc, opaque);
                return;
            case ListExpr l: foreach (var el in l.Elements) ScanRowExpr(el, name, acc, opaque); return;
            case TupleExpr t: foreach (var el in t.Elements) ScanRowExpr(el, name, acc, opaque); return;
            case SetExpr s: foreach (var el in s.Elements) ScanRowExpr(el, name, acc, opaque); return;
            case DictExpr d:
                foreach (var (dk, dv) in d.Entries) { ScanRowExpr(dk, name, acc, opaque); ScanRowExpr(dv, name, acc, opaque); }
                return;
            case FStringExpr f:
                foreach (var p in f.Parts) if (p.Expr != null) ScanRowExpr(p.Expr, name, acc, opaque);
                return;
            case ListCompExpr lc:
                ScanCompRowExpr(lc, name, acc, opaque);
                return;
            case GeneratorExpr ge:
                // `(x for x in r)` iterates lazily -- the row would have to be a
                // value. A list comprehension's iterable is expanded at compile
                // time, so only that spelling may iterate a row.
                if (ge.Iterable is VariableExpr gv && gv.Name == name) throw RowAliasNotAValue(gv);
                ScanRowExpr(ge.Iterable, name, acc, opaque);
                ScanRowExpr(ge.Iterable2, name, acc, opaque);
                ScanRowExpr(ge.Filter, name, acc, opaque);
                if (ge.VarName != name && ge.Var2Name != name)
                    ScanRowExpr(ge.Element, name, acc, opaque);
                return;
            case WalrusExpr w:
                ScanRowExpr(w.Value, name, acc, opaque);
                if (w.VarName == name) throw RowAliasRebindError(w, name);
                return;
            case LambdaExpr l:
                foreach (var p in l.Params)
                    ScanRowExpr(p.DefaultValue, name, acc, opaque);
                if (!l.Params.Any(p => p.Name == name))
                    ScanRowExpr(l.Body, name, acc, opaque: true);
                return;
            case StarArgExpr s: ScanRowExpr(s.Value, name, acc, opaque); return;
            case DoubleStarArgExpr d: ScanRowExpr(d.Value, name, acc, opaque); return;
            case KeywordArgExpr k: ScanRowExpr(k.Value, name, acc, opaque); return;
            case AwaitExpr a: ScanRowExpr(a.Operand, name, acc, opaque); return;
            case YieldExpr y: ScanRowExpr(y.Value, name, acc, opaque); return;
            default: return;   // literals, PreEvaluatedExpr
        }
    }

    // A comprehension's iterator names shadow the row alias INSIDE the element
    // expression; the iterables are still evaluated in the enclosing scope and
    // may iterate the row (`[x for x in r]`), which is a legal use.
    private void ScanCompRowExpr(ListCompExpr lc, string name, GridRowAlias acc, bool opaque)
    {
        bool Iter(Expression? it)
        {
            if (it is VariableExpr iv && iv.Name == name) { acc.Uses?.Add(lc); return true; }
            ScanRowExpr(it, name, acc, opaque);
            return false;
        }
        Iter(lc.Iterable);
        Iter(lc.Iterable2);
        ScanRowExpr(lc.Filter, name, acc, opaque);
        if (lc.VarName != name && lc.Var2Name != name)
            ScanRowExpr(lc.Element, name, acc, opaque);
    }

    // Whether ANY subtree mentions `name` -- used only where a mention is
    // already an error (nested functions, class bodies).
    private bool MentionsName(Statement? s, string name)
    {
        var acc = new GridRowAlias { GridKey = "", RowIndexExpr = new IntegerLiteral(0), Uses = new() };
        try { ScanRowStmt(s!, name, acc, topLevel: false); return acc.Uses.Count > 0; }
        catch (PyMCU.Common.CompilerError) { return true; }
    }
}

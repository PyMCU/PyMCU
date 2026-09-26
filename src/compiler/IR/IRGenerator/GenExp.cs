using PyMCU.Frontend;
using PyMCU.IR;
using AstBinOp = PyMCU.Frontend.BinaryOp;

namespace PyMCU.IR.IRGenerator;

/// <summary>
/// Generator expressions. PyMCU has no iterator protocol and no heap, so a generator
/// expression is never a VALUE: it is lowered only as the argument of the five reductions
/// all(), any(), sum(), min() and max(), where it unrolls at compile time into the same
/// code a `for` over the iterable plus an accumulator would produce -- the reading
/// `all(0 <= c <= 255 for c in val)` in adafruit_pixelbuf needs.
///
/// The iterable's length must therefore be decidable when the program is compiled: a
/// list/tuple literal, a name bound to a short constant sequence, an @inline parameter
/// bound to one, a fixed-size array, a compile-time string, or range() with constant
/// bounds. Anything else is refused here rather than materialized as an object, and the
/// refusal names that requirement because it is the thing the writer can change.
/// </summary>
public partial class IRGenerator
{
    /// The most elements a generator expression may unroll to: the work is compile-time
    /// only when the elements fold, but a runtime element emits its code once per step, so
    /// the cap exists for the same reason the `for` unroller's does.
    private const int GenExpUnrollLimit = 256;

    /// <summary>
    /// Where a generator expression is allowed to appear. Shared by the value-position
    /// refusal in VisitExpression and the iterable-position refusal in VisitFor, so both
    /// sentences say the same thing.
    /// </summary>
    private const string GenExpWhere =
        "a generator expression has no value of its own -- PyMCU has no iterators to carry "
        + "one. It is supported only as the single argument of all(), any(), sum(), min() "
        + "or max(), where it unrolls at compile time over a sequence whose length is known "
        + "when the program is compiled";

    /// <summary>
    /// Entry point from the five reduction builtins: <paramref name="expr"/> is the
    /// reduction call and <paramref name="name"/> is its name. The generator is the first
    /// (and for all/any/min/max the only) argument; sum() takes its optional start second.
    /// </summary>
    private Val EmitGenExpReduction(CallExpr expr, string name)
    {
        var gen = (GeneratorExpr)expr.Args[0]!;
        switch (name)
        {
            case "all":
            case "any":
                if (expr.Args.Count != 1)
                    throw UserError($"{name}() takes ONE generator expression", ArgAt(expr, 1));
                return EmitGenExpAllAny(gen, name == "all");
            case "sum":
                if (expr.Args.Count > 2)
                    throw UserError("sum() takes a generator expression and at most a start", ArgAt(expr, 2));
                return EmitGenExpSum(gen, expr);
            default:
                if (expr.Args.Count != 1)
                    throw UserError(expr.Args.Skip(1).Any(a => a is KeywordArgExpr)
                        ? $"{name}() over a generator expression does not take 'key=' or "
                          + "'default=' -- there is no runtime iterator for a key function to "
                          + "visit. Write the comparison out over the sequence instead"
                        : $"{name}() takes ONE generator expression", ArgAt(expr, 1));
                return EmitGenExpMinMax(gen, name == "min");
        }
    }

    /// <summary>
    /// The compile-time elements of a generator's iterable, or null when the length is not
    /// decidable at compile time -- which is the refusal, not a different lowering. The
    /// element list mirrors what `for x in <iterable>` would unroll over.
    /// </summary>
    private List<Expression>? GenExpIterableElements(Expression iterable)
    {
        switch (iterable)
        {
            case ListExpr le: return le.Elements;
            case TupleExpr te: return te.Elements;
            case StringLiteral sl:
            {
                var chars = new List<Expression>(sl.Value.Length);
                foreach (char ch in sl.Value)
                    chars.Add(new StringLiteral(ch.ToString()) { Line = iterable.Line });
                return chars;
            }
            case CallExpr { Callee: VariableExpr { Name: "range" } } rcall:
            {
                // The same bounds the for-range unroller folds: the general constant
                // evaluator with locals in scope, so `range(WIDTH)` folds like `range(4)`.
                int? Bound(Expression? e, int whenAbsent)
                {
                    if (e == null) return whenAbsent;
                    if (e is IntegerLiteral il) return il.Value;
                    if (e is UnaryExpr { Op: Frontend.UnaryOp.Negate, Operand: IntegerLiteral n })
                        return -n.Value;
                    bool savedFold = foldLocalConstants;
                    foldLocalConstants = true;
                    try { return EvaluateConstantExpr(e); }
                    catch { return null; }
                    finally { foldLocalConstants = savedFold; }
                }

                if (rcall.Args.Count is < 1 or > 3) return null;
                int? start, stop, step;
                if (rcall.Args.Count == 1) { start = 0; stop = Bound(rcall.Args[0], 0); step = 1; }
                else
                {
                    start = Bound(rcall.Args[0], 0);
                    stop = Bound(rcall.Args[1], 0);
                    step = rcall.Args.Count == 3 ? Bound(rcall.Args[2], 1) : 1;
                }
                if (!start.HasValue || !stop.HasValue || !step.HasValue || step.Value == 0)
                    return null;
                var elems = new List<Expression>();
                for (int i = start.Value; step > 0 ? i < stop : i > stop; i += step.Value)
                    elems.Add(new IntegerLiteral(i) { Line = rcall.Line });
                return elems;
            }
            case VariableExpr ve:
            {
                // A name bound to a short constant sequence (`pins = [11, 12, 13]`) or a
                // parameter handed such a literal.
                if (ResolveConstSequenceExpr(ve) is { } bound) return bound;
                // A name bound to a compile-time string iterates its characters.
                if (StaticStringOf(ve) is { } vstr)
                {
                    var chars = new List<Expression>(vstr.Length);
                    foreach (char ch in vstr)
                        chars.Add(new StringLiteral(ch.ToString()) { Line = ve.Line });
                    return chars;
                }
                // A fixed-size array: the elements are reads of its slots, which is what
                // keeps a runtime array's contents in the answer.
                if (ResolveArrayVar(ve.Name) is { } arr)
                {
                    var elems = new List<Expression>(arr.Size);
                    for (int i = 0; i < arr.Size; i++)
                        elems.Add(new IndexExpr(ve, new IntegerLiteral(i)) { Line = ve.Line });
                    return elems;
                }
                return null;
            }
            case MemberAccessExpr mae:
            {
                // A compile-time sequence or string held in a field (`self._pins`,
                // `self.name`).
                if (ResolveConstSequenceExpr(mae) is { } memSeq) return memSeq;
                if (StaticStringOf(mae) is { } mstr)
                {
                    var chars = new List<Expression>(mstr.Length);
                    foreach (char ch in mstr)
                        chars.Add(new StringLiteral(ch.ToString()) { Line = mae.Line });
                    return chars;
                }
                if (SequenceKeyOf(mae) is { } key && arraySizes.TryGetValue(key, out int sz))
                {
                    var elems = new List<Expression>(sz);
                    for (int i = 0; i < sz; i++)
                        elems.Add(new IndexExpr(mae, new IntegerLiteral(i)) { Line = mae.Line });
                    return elems;
                }
                return null;
            }
            default:
                return null;
        }
    }

    private string GenExpLengthError(string name) =>
        $"{name}() over a generator expression unrolls it when the program is compiled, and "
        + "the iterable's length is not known when the program is compiled. The forms that "
        + "give it one: a list or tuple literal, a parameter bound to one, a fixed-size "
        + "array, a compile-time string, or range() with constant bounds.";

    /// <summary>
    /// The compile-time walk over one or two generator clauses: the element expression is
    /// handed to <paramref name="emit"/> once per produced element, with the loop variable
    /// (and the second clause's variable) bound to that iteration's element. Returning
    /// false stops the walk -- all() and any() use it when a constant element decides the
    /// answer and CPython would produce no later element. The second argument tells the
    /// callback the element sits behind a runtime filter: a compile-time verdict reached
    /// there is conditional, so it must come out as a guarded jump, not as the answer.
    /// </summary>
    private void GenExpWalk(GeneratorExpr gen, string name, Func<GeneratorExpr, bool, bool> emit,
                            Action? beforeFilterJump = null)
    {
        var outer = GenExpIterableElements(gen.Iterable)
            ?? throw UserError(GenExpLengthError(name), gen.Iterable);
        if (outer.Count > GenExpUnrollLimit)
            throw UserError(
                $"{name}() over a generator expression unrolls {outer.Count} elements, past "
                + $"the {GenExpUnrollLimit} cap -- the elements exist only as compile-time "
                + "bindings, so the whole walk is code the compiler writes out. Rewrite it "
                + "as a `for` with an accumulator, which lowers to a real loop.", gen.Iterable);
        string varKey = QualifyLoopVar(gen.VarName);
        string? var2Key = string.IsNullOrEmpty(gen.Var2Name) ? null : QualifyLoopVar(gen.Var2Name);

        foreach (var oe in outer)
        {
            BindGenExpVar(varKey, oe);
            bool keepGoing;
            if (gen.Iterable2 != null)
            {
                var inner = GenExpIterableElements(gen.Iterable2)
                    ?? throw UserError(GenExpLengthError(name), gen.Iterable2);
                keepGoing = true;
                foreach (var ie in inner)
                {
                    BindGenExpVar(var2Key!, ie);
                    keepGoing = EmitGenExpElement(gen, emit, beforeFilterJump);
                    UnbindUnrolledVar(var2Key!);
                    if (!keepGoing) break;
                }
            }
            else
            {
                keepGoing = EmitGenExpElement(gen, emit, beforeFilterJump);
            }
            UnbindUnrolledVar(varKey);
            if (!keepGoing) break;
        }
    }

    /// <summary>
    /// One produced element: applies the optional `if` filter -- a constant-false filter
    /// drops the element without running <paramref name="emit"/>, a runtime one emits a
    /// skip -- then runs it. Returns what the callback returned.
    /// </summary>
    private bool EmitGenExpElement(GeneratorExpr gen, Func<GeneratorExpr, bool, bool> emit,
                                   Action? beforeFilterJump = null)
    {
        if (gen.Filter != null)
        {
            Val fv = VisitExpression(gen.Filter);
            if (fv is Constant fvc)
            {
                if (fvc.Value == 0) return true;
            }
            else
            {
                // The callback's chance to emit state that must exist before the skip jump:
                // min()/max() initialize their `produced` flag here so a runtime filter that
                // excludes every element still reports the empty sequence it made.
                beforeFilterJump?.Invoke();
                string skip = MakeLabel();
                Emit(new JumpIfZero(fv, skip));
                bool keep = emit(gen, true);
                Emit(new Label(skip));
                return keep;
            }
        }
        return emit(gen, false);
    }

    /// <summary>
    /// Binds one produced element to the generator's variable, mirroring the state a
    /// `for` unroll leaves the name in: a number lands in constantVariables, a string
    /// binds its TEXT (strConstantVariables) plus the number the same literal lowers to --
    /// the char code for one character, the interned id for a longer one -- an instance
    /// aliases, and a nested tuple/list binds its elements. Anything else is a runtime
    /// value: materialize it into a variable of the loop-var's name so reads resolve to a
    /// real slot, and make sure no stale constant from an earlier iteration answers first.
    /// </summary>
    private void BindGenExpVar(string key, Expression elem)
    {
        // A string element first: TryFoldConstElement would answer the INTERNED ID of a
        // StringLiteral before the text ever gets bound, which is the gap that kept
        // `for s in ("a", "b")` from seeing a string.
        if (TryEvalConstStrElement(elem, out var elemText))
        {
            BindUnrolledString(key, elemText);
            return;
        }
        if (BindUnrolledElement(key, elem)) return;
        Val v = VisitExpression(elem);
        if (v is Constant gc)
        {
            constantVariables[key] = gc.Value;
            if (gc.Text != null) strConstantVariables[key] = gc.Text;
            else strConstantVariables.Remove(key);
            return;
        }
        UnbindUnrolledVar(key);
        variableTypes[key] = GetValType(v);
        Emit(new Copy(v, new Variable(key, variableTypes[key])));
    }

    /// <summary>
    /// all()/any() over a generator. The first element that decides the answer -- a false
    /// one for all(), a true one for any() -- ends the walk: a constant element stops the
    /// unrolling outright (later elements are never produced, as CPython's iterator would
    /// not produce them), and a runtime one jumps past the rest of the unrolled code so
    /// later element expressions do not run either.
    /// </summary>
    private Val EmitGenExpAllAny(GeneratorExpr gen, bool isAll)
    {
        string name = isAll ? "all" : "any";
        int init = isAll ? 1 : 0;
        int hitVal = isAll ? 0 : 1;

        Temporary? result = null;
        string hitLbl = MakeLabel();
        string endLbl = MakeLabel();
        bool decided = false;

        GenExpWalk(gen, name, (g, guarded) =>
        {
            Val v = VisitExpression(g.Element);
            if (v is Constant c)
            {
                if ((c.Value != 0) == !isAll)
                {
                    if (!guarded)
                    {
                        decided = true;
                        return false;
                    }
                    // A constant verdict under a runtime filter is only reached when the
                    // filter passed -- `all(x > 0 for x in (0, 5) if flag)` is True when
                    // flag is 0 and the sequence the predicate sees is empty. Emit the
                    // runtime short-circuit (unconditional once the guard is passed) and
                    // keep walking for the path where it failed.
                    if (result == null)
                    {
                        result = MakeTemp();
                        Emit(new Copy(new Constant(init), result));
                    }
                    Emit(isAll ? new JumpIfZero(v, hitLbl)
                               : (Instruction)new JumpIfNotZero(v, hitLbl));
                    return true;
                }
                return true;
            }
            if (result == null)
            {
                result = MakeTemp();
                Emit(new Copy(new Constant(init), result));
            }
            Emit(isAll ? new JumpIfZero(v, hitLbl) : (Instruction)new JumpIfNotZero(v, hitLbl));
            return true;
        });

        if (decided)
        {
            // A constant element settled the answer at compile time. Runtime elements seen
            // earlier still jump to hitLbl -- which lands them at the end with the same
            // answer -- so the labels stay live even though the folded result needs no temp.
            if (result != null)
            {
                Emit(new Label(hitLbl));
                Emit(new Label(endLbl));
            }
            return new Constant(hitVal);
        }
        if (result == null) return new Constant(init);   // every element folded, none decided
        Emit(new Jump(endLbl));
        Emit(new Label(hitLbl));
        Emit(new Copy(new Constant(hitVal), result));
        Emit(new Label(endLbl));
        return result;
    }

    /// <summary>
    /// sum() over a generator: the running total is the optional start argument, folded
    /// while the elements fold and accumulated with Binary(Add) once anything is runtime.
    /// sum() of an empty walk is the start, which is CPython's answer too.
    /// </summary>
    private Val EmitGenExpSum(GeneratorExpr gen, CallExpr expr)
    {
        Expression? startArg = expr.Args.Count == 2
            ? expr.Args[1] is KeywordArgExpr kwa
                ? (kwa.Key == "start" ? kwa.Value
                    : throw UserError("sum() over a generator expression takes no keyword "
                                      + $"argument '{kwa.Key}=' -- only 'start'", expr.Args[1]))
                : expr.Args[1]
            : null;
        Val acc = startArg != null ? VisitExpression(startArg) : new Constant(0);
        Temporary? accVar = null;
        GenExpWalk(gen, "sum", (g, guarded) =>
        {
            Val v = VisitExpression(g.Element);
            if (!guarded && acc is Constant ca && v is Constant cv)
                acc = new Constant(ca.Value + cv.Value);
            else if (!guarded)
            {
                var t = MakeTemp(DataTypeExtensions.GetPromotedType(GetValType(acc), GetValType(v)));
                Emit(new Binary(PyMCU.IR.BinaryOp.Add, acc, v, t));
                acc = t;
            }
            else
            {
                // Under a runtime filter the add is conditional -- `sum(x for x in (1, 2)
                // if flag)` is 0 when flag fails, not 3 -- so it accumulates in place into
                // the variable beforeFilterJump seeded ahead of the skip jump.
                Emit(new Binary(PyMCU.IR.BinaryOp.Add, accVar!, v, accVar!));
            }
            return true;
        }, beforeFilterJump: () =>
        {
            // A runtime filter makes the running total conditional: it has to live in a
            // variable the skip jump protects, seeded unconditionally before the jump.
            if (accVar == null)
            {
                accVar = MakeTemp(GetValType(acc));
                Emit(new Copy(acc, accVar));
                acc = accVar;
            }
        });
        return accVar ?? acc;
    }

    /// <summary>
    /// min()/max() over a generator. Constant elements fold pairwise; runtime elements
    /// compare-and-select. A runtime `if` can exclude every element, which is CPython's
    /// ValueError: a `produced` flag tracks whether anything made it past the filter, and
    /// a conditional raise follows the walk.
    /// </summary>
    private Val EmitGenExpMinMax(GeneratorExpr gen, bool isMin)
    {
        string name = isMin ? "min" : "max";
        Val? best = null;
        Temporary? bestVar = null;
        Temporary? produced = null;

        GenExpWalk(gen, name, (g, guarded) =>
        {
            Val v = VisitExpression(g.Element);
            if (produced == null)
            {
                if (best == null) best = v;
                else if (!guarded) best = EmitGenExpMinMaxStep(best, v, isMin);
                else
                {
                    // The step runs only when the filter passed -- it must update bestVar
                    // in place, never fold: `min(x for x in (5, 3) if flag)` is 5 when
                    // flag fails at run time, not the 3 a folded step would have baked in.
                    // beforeFilterJump moved the seed into bestVar ahead of the skip jump.
                    EmitGenExpMinMaxSelect(bestVar!, v, isMin);
                }
            }
            else
            {
                bestVar ??= MakeTemp(DataType.UINT16);
                string haveLbl = MakeLabel();
                string nextLbl = MakeLabel();
                Emit(new JumpIfNotZero(produced, haveLbl));
                Emit(new Copy(v, bestVar));
                Emit(new Copy(new Constant(1), produced));
                Emit(new Jump(nextLbl));
                Emit(new Label(haveLbl));
                EmitGenExpMinMaxSelect(bestVar, v, isMin);
                Emit(new Label(nextLbl));
            }
            return true;
        }, beforeFilterJump: () =>
        {
            // Reached only when an element's filter could not fold, before its skip jump.
            // Two pieces of state have to exist unconditionally by then: `produced`,
            // when nothing so far was guaranteed to survive (every earlier element either
            // folded false or produced nothing -- the flag starts at 0 for the empty case
            // CPython reports as ValueError); and bestVar holding a seeded constant, when
            // an unguarded element already produced one -- a runtime filter makes the best
            // answer conditional, so it has to live somewhere a guarded select can write.
            if (produced != null) return;
            if (best == null)
            {
                produced = MakeTemp();
                Emit(new Copy(new Constant(0), produced));
            }
            else if (bestVar == null)
            {
                bestVar = MakeTemp(GetValType(best));
                Emit(new Copy(best, bestVar));
                best = bestVar;
            }
        });

        if (produced != null)
        {
            // bestVar holds the answer whenever at least one element survived the filter;
            // otherwise CPython raises ValueError("min() arg is an empty sequence").
            string okLbl = MakeLabel();
            Emit(new JumpIfNotZero(produced, okLbl));
            EmitRuntimeRaise("ValueError", $"{name}() arg is an empty sequence");
            Emit(new Label(okLbl));
            return bestVar!;
        }
        if (best == null)
            throw UserError(
                $"{name}() over this generator expression produces no values -- every "
                + "element was excluded at compile time, and " + name
                + "() of an empty sequence is a ValueError at run time. Give the iterable an "
                + "element, or take the test out.", gen);
        return best;
    }

    /// <summary>
    /// One min/max update between two values: folds constants, compare-and-selects the rest.
    /// </summary>
    private Val EmitGenExpMinMaxStep(Val a, Val b, bool isMin)
    {
        if (a is Constant ca && b is Constant cb)
            return new Constant(isMin ? Math.Min(ca.Value, cb.Value) : Math.Max(ca.Value, cb.Value));
        var t = MakeTemp(DataTypeExtensions.GetPromotedType(GetValType(a), GetValType(b)));
        Emit(new Copy(a, t));
        EmitGenExpMinMaxSelect(t, b, isMin);
        return t;
    }

    /// <summary>
    /// `acc = min(acc, v)` (or max) as a runtime compare-and-select on the accumulator.
    /// The strict comparison keeps the FIRST value on a tie, which is CPython's rule.
    /// </summary>
    private void EmitGenExpMinMaxSelect(Val acc, Val v, bool isMin)
    {
        var cmp = MakeTemp();
        Emit(new Binary(isMin ? PyMCU.IR.BinaryOp.LessThan : PyMCU.IR.BinaryOp.GreaterThan, v, acc, cmp));
        string keepLbl = MakeLabel();
        Emit(new JumpIfZero(cmp, keepLbl));
        Emit(new Copy(v, acc));
        Emit(new Label(keepLbl));
    }

    /// <summary>
    /// A runtime raise without VisitRaise's unconditional-abort rule: the empty-sequence
    /// raise sits behind a runtime flag the structured-if lowering never saw, so
    /// _runtimeBranchDepth cannot describe it.
    /// </summary>
    private void EmitRuntimeRaise(string errorType, string message)
    {
        Val code = ResolveBinding(errorType);
        EmitRaiseUnwind(code, unhandledInMain: true);
    }
}

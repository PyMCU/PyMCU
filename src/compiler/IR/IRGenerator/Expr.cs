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
using AstUnOp = PyMCU.Frontend.UnaryOp;

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    private Val VisitExpression(Expression expr)
    {
        // An operand the print lowering already ran, so its side effects happen once and in
        // CPython's order rather than in the middle of the line it is part of (#371).
        if (expr is PreEvaluatedExpr pre) return pre.Value;
        if (expr is BinaryExpr bin) return VisitBinary(bin);
        if (expr is TernaryExpr tern) return VisitTernary(tern);
        if (expr is UnaryExpr un) return VisitUnary(un);
        if (expr is IntegerLiteral num) return VisitLiteral(num);
        if (expr is VariableExpr v) return VisitVariable(v);
        if (expr is CallExpr call) return VisitCall(call);
        if (expr is YieldExpr yieldExpr) return VisitYield(yieldExpr);
        if (expr is IndexExpr idx) return VisitIndex(idx);
        if (expr is MemberAccessExpr mem) return VisitMemberAccess(mem);

        if (expr is BooleanLiteral boolean) return new Constant(boolean.Value ? 1 : 0);

        if (expr is AwaitExpr)
            throw UserError(
                "`await` is only valid inside an `async def`, and the coroutine lowering is not " +
                "implemented yet. Drive a future's poll() from a cooperative loop for now.", expr);

        if (expr is Frontend.DictExpr or Frontend.SetExpr)
            throw UserError(
                "dict/set literals are compile-time lookup tables: bind one to a name " +
                "(`d = {...}`) and use `d[k]` / `x in d` / `len(d)`; they have no runtime " +
                "value in other positions (no heap on bare metal).", expr);

        if (expr is NoneLiteral) return new NoneVal();

        if (expr is StringLiteral str)
        {
            // A one-character literal is its own character code, so `uart.write('A')` passes 65
            // and `c == 'x'` compares numbers. The code alone cannot say whether it means the
            // string or the number, so the text rides along; nothing downstream has to guess.
            if (str.Value.Length == 1) return new Constant((int)str.Value[0], str.Value);

            if (!stringLiteralIds.ContainsKey(str.Value))
            {
                stringLiteralIds[str.Value] = nextStringId;
                stringIdToStr[nextStringId] = str.Value;
                nextStringId++;
            }

            return new Constant(stringLiteralIds[str.Value], str.Value);
        }

        if (expr is FStringExpr fstr) return VisitFStringExpr(fstr);

        if (expr is WalrusExpr walrus)
        {
            Val rhs = VisitExpression(walrus.Value);
            // Qualify like a normal variable reference the body resolves: a function-scoped
            // name gets the `func.` prefix when not inline-expanded. Using the bare name stored
            // the walrus target under "w" while later reads resolved "func.w" -> read 0.
            string key = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + walrus.VarName
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + walrus.VarName : walrus.VarName);
            DataType dt = DataType.UINT8;
            if (variableTypes.TryGetValue(key, out var t)) dt = t;
            var vr = new Variable(key, dt);
            variableTypes[key] = dt;
            Emit(new Copy(rhs, vr));
            // A walrus writes the name like any assignment; it carries a constant only when
            // the value it stores is one. A FUNCTION-WRITTEN module-global target is not
            // remembered: the map answers for every function lowered afterwards, and one
            // function's store is not the value another function's read can see
            // (RecordLocalConstant says why).
            if (rhs is Constant walrusConst && !functionWrittenGlobals.Contains(key))
                localConstantValues[key] = walrusConst.Value;
            else localConstantValues.Remove(key);
            return vr;
        }

        if (expr is LambdaExpr lam) return VisitLambdaExpr(lam);

        if (expr is FloatLiteral floatLit)
            return new FloatConstant(floatLit.Value);

        if (expr is TupleExpr)
            throw UserError(
                "tuples are not supported as runtime values -- use a fixed list " +
                "([a, b, c]) for indexable storage, or unpack directly (x, y = f())", expr);

        // A generator expression in any position that wants a value. all(), any(), sum(),
        // min() and max() unwrap theirs before this is ever asked, so reaching here means
        // the program put one where it cannot work -- an assignment, a return, another
        // call's argument, a `for` iterable -- and the refusal names the five places it can.
        if (expr is GeneratorExpr)
            throw UserError(GenExpWhere + ".", expr);

        // Three different programs reached this line and all three were told there is a
        // filter. `[DigitalInOut(p) for p in pins]` has no `if` in it, and the reader was sent
        // to look for one; what is unsupported is a comprehension of INSTANCES, which have no
        // array slot to live in (#307).
        if (expr is ListCompExpr comp)
        {
            if (comp.Filter != null)
                throw UserError(
                    "a list comprehension with a filter (if) is not supported -- the array " +
                    "length must be a compile-time constant, and a filter decides it at run " +
                    "time. Build the list with an explicit loop, or drop the filter (plain " +
                    "[f(i) for i in range(N)] works)", expr);

            if (ComprehensionElementIsInstance(comp))
                throw UserError(
                    "a list comprehension of class instances is not supported: PyMCU lays an "
                    + "instance out at compile time and it has no array slot to live in. Write "
                    + "the list as a literal of constructions ([A(x), A(y)]), or build each one "
                    + "by name.", expr);

            throw UserError(
                "a list comprehension is only supported where it fills a fixed array whose "
                + "length is a compile-time constant (`xs: uint8[4] = [f(i) for i in "
                + "range(4)]`). In this position there is no array for it to fill.", expr);
        }

        // A bytes or list literal reaching a VALUE position. `b"ab"` parses to a ListExpr of
        // integers, so this answered "Unknown Expression type: ListExpr": a compiler class name
        // for something the reader spelled b"ab", and a phase name for a program that is simply
        // not supported here.
        if (expr is ListExpr)
            throw UserError(
                "a bytes or list literal has no value in this position. " + SequenceIsStorage
                + " The positions that do work reach it through a NAME: bind it "
                + "(`data = b\"ab\"`), then index it (`data[0]`), take its len(), iterate it, "
                + "slice it, or pass the name to a function. A comparison, a return and a "
                + "parameter default are not among them.",
                expr);

        // A keyword argument that no call path bound (#349). It reaches here as a VALUE, which
        // it never is, and the fallback below would answer with the name of a class in this
        // compiler about a word the program does not contain. Say which argument and where.
        //
        // The binding itself is done where the call is lowered -- BindMethodArgs for a base
        // method, ReorderCallArgs for a function -- so arriving here means this particular
        // callee has no binding path yet, and that is what the sentence has to say.
        if (expr is KeywordArgExpr kwArg)
            throw UserError(
                $"the keyword argument '{kwArg.Key}=' is not supported in this call. PyMCU "
                + "binds keyword arguments to functions, to base-class methods and to @inline "
                + "methods; this callee is none of those. Pass the value positionally.",
                expr);

        throw UserError($"IR Generation: Unknown Expression type: {expr.GetType().Name}", expr);
    }

    /// <summary>Whether a comprehension builds class instances rather than values.</summary>
    private bool ComprehensionElementIsInstance(ListCompExpr comp)
    {
        if (comp.Element is not CallExpr call) return false;
        string name = call.Callee switch
        {
            VariableExpr ve => ve.Name,
            MemberAccessExpr ma => ma.Member,
            _ => "",
        };
        if (name.Length == 0) return false;
        if (classNames.Contains(name)) return true;
        string resolved = ResolveCallee(name);
        return classFieldLayout.ContainsKey(resolved) || classDirectMethods.ContainsKey(resolved);
    }

    // The one fact behind every refusal below, so a reader meets it once and the three sites
    // cannot drift into describing three different languages.
    private const string SequenceIsStorage =
        "PyMCU lays one out as a fixed array -- element storage under a name, with no handle "
        + "and no length travelling with it, because there is no heap to hold one -- so there "
        + "is no single value to carry here.";

    /// <summary>
    /// True when the expression is a bytes/list OBJECT rather than a value: a literal, or a
    /// name bound to a fixed array.
    ///
    /// The reported half of #195 crashed, which at least stopped the build. The half that did
    /// not crash is worse and is why this asks about names as well as literals. `a == b` over
    /// two bytes names lowered to a one-byte `jne` between the two array NAMES -- measured,
    /// `b"ab" == b"ab"` and `b"ab" == b"ax"` produce byte-identical IR, so the comparison
    /// answers without reading either operand and one of the two answers it gives has to be
    /// wrong. `return x` did the same at the other end: the array came back as a scalar, and
    /// the caller's `y[0]` lowered to a bit test on it.
    /// </summary>
    /// <summary>
    /// Refuse `==` / `!=` over a bytes or list object. Called from BOTH lowerings of a
    /// comparison: VisitBinary, and the conditional-jump path that an `if` takes instead --
    /// which is the one that was emitting a one-byte `jne` between two array NAMES, so a check
    /// added only to VisitBinary would have left the silent case exactly as it was.
    /// </summary>
    private void RefuseSequenceComparison(BinaryExpr expr)
    {
        if (expr.Op is not (AstBinOp.Equal or AstBinOp.NotEqual)) return;
        if (!IsSequenceObject(expr.Left) && !IsSequenceObject(expr.Right)) return;

        throw UserError(
            "a bytes or list object cannot be compared. " + SequenceIsStorage
            + " Compare the elements that matter (`data[0] == 0x61`), or walk them with a loop "
            + "over range(len(data)). As written the comparison answered without reading either "
            + "side: b\"ab\" == b\"ab\" and b\"ab\" == b\"ax\" compiled to the same "
            + "instructions.",
            expr);
    }

    private bool IsSequenceObject(Expression? e) => e switch
    {
        ListExpr => true,
        VariableExpr v => ResolveArrayVar(v.Name) != null,
        _ => false,
    };

    private Val VisitLambdaExpr(LambdaExpr expr)
    {
        string key = "__lambda_" + lambdaCounter++;
        lambdaFunctionsMap[key] = expr;
        pendingLambdaKey = key;
        return new Constant(0);
    }

    /// <summary>
    /// The one sentence for `m[x, y]` on a class that cannot receive the pair (#352).
    ///
    /// It lived in BOTH readers, word for word, kept in step by a comment asking the next
    /// person to change both. It lives here now because the answer depends on the class and no
    /// reader knows the classes: a class whose `__getitem__`/`__setitem__` takes the key binds
    /// the pair at compile time, and only what is left gets this.
    ///
    /// It says what the construct DOES rather than that it is unsupported, because the reader
    /// who wrote `m[x, y]` is usually not thinking about tuples at all.
    /// </summary>
    internal const string TwoIndexSubscriptRefusal =
        "a subscript with more than one index needs a __getitem__ or __setitem__ that takes " +
        "the pair as one key (`def __getitem__(self, key): x, y = key`), and this one does " +
        "not. Call the method the subscript stands for, passing the indices separately " +
        "(e.g. 'm.pixel(x, y)')";

    /// <summary>
    /// Whether the class of <paramref name="target"/> defines an @inline dunder that can take a
    /// two-index subscript's pair as one key (#352).
    ///
    /// Asked WITHOUT lowering anything: the caller is deciding whether to refuse, and visiting
    /// the target to find out would emit instructions for a program that is about to be
    /// rejected. An OUTLINED dunder is deliberately not enough -- it is a real call, and a
    /// compile-time pair cannot be passed through one -- so those keep the refusal and its
    /// advice, which is to call the method the subscript stands for.
    /// </summary>
    private bool SubscriptTakesAPair(Expression target, string dunder)
    {
        string? cls = target switch
        {
            VariableExpr ve => InstanceClassOfName(ve.Name),
            MemberAccessExpr m when SequenceKeyOf(m) is { } key && instanceClasses.TryGetValue(key, out var mc) => mc,
            _ => null,
        };
        return cls != null && inlineFunctions.ContainsKey(cls + "_" + dunder);
    }

    public string GetValClass(Val v)
    {
        string TryName(string name)
        {
            string cur = name;
            for (int i = 0; i < 10; ++i)
            {
                if (instanceClasses.TryGetValue(cur, out var c)) return c;
                if (!variableAliases.TryGetValue(cur, out string next)) break;
                cur = next;
            }

            return "";
        }

        if (v is Variable varV) return TryName(varV.Name);
        if (v is Temporary tmp) return TryName(tmp.Name);
        return "";
    }

    /// The name that actually carries <paramref name="v"/>'s class in `instanceClasses`, or
    /// null when it carries none -- the same alias chain <see cref="GetValClass"/> walks, but
    /// returning the NAME the chain ends at rather than the class found there.
    ///
    /// A `@property` returning a single-field ZCA instance (`return self._q`, #445) hands
    /// back a bare Temporary that ALIASES the field's own flattened storage
    /// (`variableAliases["tmp_N"] == "owner__q"`); the temp itself is never a key of
    /// `instanceClasses` directly. Two call sites used to check `instanceClasses.ContainsKey`
    /// on the Temporary's OWN name -- one hop short of what this walks -- so `owner.q.bump()`
    /// found no class to dispatch on at all, and once that was fixed here, still found no
    /// receiver to bind `self` to: `Val objVal = VisitExpression(mem2.Object)` there
    /// RE-EVALUATES the property getter, producing a SECOND, DIFFERENT temp with the exact
    /// same one-hop-short problem. One helper closes both, and any future call site with the
    /// same shape.
    private string? ResolveClassCarryingName(Val v)
    {
        string? name = v switch { Variable vv => vv.Name, Temporary tt => tt.Name, _ => null };
        for (int depth = 0; depth < 20 && name != null; ++depth)
        {
            if (instanceClasses.ContainsKey(name)) return name;
            if (!variableAliases.TryGetValue(name, out var next)) return null;
            name = next;
        }
        return null;
    }

    // seqArgs (optional) binds an extra-arg index to a bytes/list/tuple literal
    // so the dunder body can consume that parameter via constant subscript or a
    // for-in unroll -- e.g. pixels[i] = (r, g, b) passes the tuple to __setitem__.
    private Val EmitDunderCall(string selfQname, string className, string funcKey, List<Val> extraArgs,
                               Dictionary<int, Frontend.ListExpr>? seqArgs = null)
    {
        var func = inlineFunctions[funcKey];
        string exitLabel = MakeLabel();
        int newDepth = inlineDepth + 1;
        string newPrefix = $"inline{newDepth}.{func.Name}.";

        variableAliases[newPrefix + "self"] = selfQname;
        instanceClasses[newPrefix + "self"] = className;

        int extraIdx = 0;
        for (int pi = 1; pi < func.Params.Count && extraIdx < extraArgs.Count; ++pi, ++extraIdx)
        {
            string paramKey = newPrefix + func.Params[pi].Name;
            DataType dt = DataTypeExtensions.StringToDataType(func.Params[pi].Type);
            if (seqArgs != null && seqArgs.TryGetValue(extraIdx, out var seqLit))
            {
                listLiteralParams[paramKey] = seqLit;
                constantVariables.Remove(paramKey);
                variableAliases.Remove(paramKey);
                continue;
            }
            listLiteralParams.Remove(paramKey);
            // Clear any binding left from a PRIOR call to this same dunder at the same inline
            // depth (the prefix, hence paramKey, is reused). Without this, a stale alias from a
            // previous call (e.g. b[0]=s aliased v->s) survived and shadowed a fresh Copy on the
            // next call (b[1]=s+10), so the body read the old value -- the +10 silently vanished.
            constantVariables.Remove(paramKey);
            variableAliases.Remove(paramKey);
            if (extraArgs[extraIdx] is Constant c)
            {
                constantVariables[paramKey] = c.Value;
                if (c.Text != null)
                    strConstantVariables[paramKey] = c.Text;
            }
            else if (extraArgs[extraIdx] is Variable v)
            {
                variableAliases[paramKey] = v.Name;
                variableTypes[paramKey] = dt;
            }
            else
            {
                Emit(new Copy(extraArgs[extraIdx], new Variable(paramKey, dt)));
                variableTypes[paramKey] = dt;
            }
        }

        Temporary? result = null;
        if (func.ReturnType != "void" && func.ReturnType != "None")
            result = MakeTemp(DataTypeExtensions.StringToDataType(func.ReturnType));

        var savedPrefix = currentInlinePrefix;
        var savedMod = currentModulePrefix;
        var savedDepth = inlineDepth;
        var savedSourcePath = currentSourcePath;
        var savedSourceFile = currentSourceFile;
        var savedTracksCallee = inlineTracksCalleeLine;
        var savedCalleeLine = inlineCalleeStmtLine;

        currentInlinePrefix = newPrefix;
        currentModulePrefix = className + "_";
        inlineDepth = newDepth;
        var dunderCtx = new InlineContext { ExitLabel = exitLabel, ResultTemp = result, CalleeName = funcKey,
            EntryBranchDepth = _runtimeBranchDepth, CallerSourcePath = currentSourcePath,
            FinallyDepth = finallyStack.Count };
        inlineStack.Add(dunderCtx);

        // The dunder's body is text in the file the method is DEFINED in, which is not the
        // file the `obj[i]` is written in. Until the path moved with the body, a diagnostic
        // raised here kept the module's line under the caller's file -- `isinstance(index,
        // slice)` at adafruit_pixelbuf.py:293 arrived as main.py:293. The same four
        // assignments EmitInlineFunctionCall makes, for the same reason.
        string? calleeSourcePath =
            functionSourcePath.TryGetValue(func, out var calleePath) ? calleePath : null;
        if (calleeSourcePath != null)
        {
            currentSourcePath = calleeSourcePath;
            currentSourceFile = calleeSourcePath.Length > 0 ? SourceFileLabel(calleeSourcePath) : "";
            inlineTracksCalleeLine = true;
            inlineCalleeStmtLine = 0;
        }
        else
        {
            inlineTracksCalleeLine = false;
        }

        int savedLastLine = lastLine;
        lastLine = -1;
        bool savedSeqTerminated = _seqTerminated;
        _seqTerminated = false;
        VisitBlock(func.Body);
        RestoreSeqTerminatedAfterExpansion(savedSeqTerminated, exitLabel);
        lastLine = savedLastLine;
        Emit(new Label(exitLabel));
        inlineStack.RemoveAt(inlineStack.Count - 1);

        inlineDepth = savedDepth;
        currentInlinePrefix = savedPrefix;
        currentModulePrefix = savedMod;
        currentSourcePath = savedSourcePath;
        currentSourceFile = savedSourceFile;
        inlineTracksCalleeLine = savedTracksCallee;
        inlineCalleeStmtLine = savedCalleeLine;

        // An unannotated dunder (`def __getitem__(self, key): return ...`, no `-> T`) makes
        // `func.ReturnType` "void", so `result` above is null and no result slot exists yet
        // when the body is visited. The `return` statement's own handler (VisitStatement,
        // ReturnStmt) covers exactly that case for a plain @inline call: when it finds
        // `ctx.ResultTemp` null it allocates one on the fly, sized from the returned value,
        // and stores it back on the (shared, mutable) InlineContext. This call site never
        // read that back -- it kept returning its own `result` local, still null from before
        // the body ran -- so every two-index dunder subscript with an unannotated return type
        // answered the hardcoded Constant(0) below instead of what the method computed.
        if (result == null) result = dunderCtx.ResultTemp;

        if (result != null) return result;
        return new Constant(0);
    }

    // True when `<obj>.<field>` names a declared field of an instance that lives in an SRAM
    // slot. `.value` is also the register / pointer read, and that path ran first: `self.value`
    // on a single-field instance handed back the instance's own scalar, which IS the field
    // while the instance is flattened (and the snapshot MaterializeSlotFromFlattened takes
    // relies on that) -- but once the instance has a slot the writes go there, and every read
    // through the scalar saw the constructor's value: `self.value = self.value + 1` in a
    // method never added up, and a Fader that summed 0..9 answered 3.
    // Was IsSlotInstanceField, which required a live runtime slot -- a receiver whose
    // constructor arguments folded entirely to compile-time constants never gets one (RFC
    // 0001's "fast construction path" materializes a slot only when something needs one at
    // run time). A field named "value" on such a receiver then fell through to the register-
    // pointer read below, which chased no alias of its own and answered with the raw,
    // never-written inline-frame variable -- not the field's folded constant (#430).
    //
    // Two shapes now qualify, kept apart on purpose:
    //  - A live slot (the original check): covers both a genuinely multi-field instance AND a
    //    single-field one PROMOTED to a real slot because a mutating method's control flow
    //    (if/while/for) needs persistent storage across calls (PyMCU#292's ZcaMethodLoopReturn
    //    shape) -- field count alone does not predict this, so it has to be asked directly.
    //  - A class with MORE THAN ONE field and no live slot yet (Sub in #430): such an instance
    //    never collapses onto its own bare name for ANY of its fields (that collapse is a
    //    single-field-only shorthand), so a member named "value" here is unambiguously a real
    //    field even before construction has stored anything.
    // A single-field class with NO live slot is deliberately excluded: `self` there IS the
    // field's value (RFC 0001 Model B collapse), which is exactly what the `.value` register
    // path below already does for it -- routing it through the field logic instead wrote/read
    // a flattened `c_value` disjoint from the collapsed `c` (regressed a nested-@inline-closure
    // fixture from #427 while first fixing #430).
    private bool IsKnownInstanceField(Expression obj, string field)
    {
        if (obj is not VariableExpr ve) return false;
        foreach (var start in new[]
                 {
                     string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + ve.Name,
                     string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + ve.Name,
                     ve.Name,
                 })
        {
            if (start == null) continue;
            string? key = start;
            for (int depth = 0; depth < 20 && key != null; depth++)
            {
                if (instanceClasses.TryGetValue(key, out var cls) && cls != null
                    && classFieldLayout.TryGetValue(cls, out var layout)
                    && (slotInstances.ContainsKey(key) || layout.Count > 1)
                    && layout.Any(f => f.Field == field))
                    return true;
                if (!variableAliases.TryGetValue(key, out key)) break;
            }
        }
        return false;
    }

    private DataType GetValType(Val v)
    {
        if (v is FloatConstant) return DataType.FLOAT;
        if (v is Variable varV) return varV.Type;
        if (v is Temporary tmp) return tmp.Type;
        if (v is MemoryAddress mem) return mem.Type;
        if (v is Constant c)
        {
            if (c.Value >= 0 && c.Value <= 255) return DataType.UINT8;
            if (c.Value >= -128 && c.Value <= 127) return DataType.INT8;
            if (c.Value >= 0 && c.Value <= 65535) return DataType.UINT16;
            if (c.Value >= -32768 && c.Value <= 32767) return DataType.INT16;
            return DataType.INT32;
        }

        return DataType.UINT8;
    }

    // Full value range of an integer storage type. FLOAT has no integer range and never
    // reaches the range-aware promotion below.
    internal static (long Min, long Max) RangeOfType(DataType t) => t switch
    {
        DataType.UINT8 => (0L, 255L),
        DataType.INT8 => (-128L, 127L),
        DataType.UINT16 => (0L, 65535L),
        DataType.INT16 => (-32768L, 32767L),
        DataType.UINT32 => (0L, 4294967295L),
        _ => (int.MinValue, int.MaxValue),
    };

    // What values this operand can actually hold: exact for a constant, the recorded
    // range for an arithmetic temporary, otherwise the whole range of its declared type.
    // Only these three carry a meaningful integer range -- a MemoryAddress types its
    // ELEMENT, not the address it holds, and the remaining Vals fall back to a guessed
    // uint8 in GetValType. Both would understate the range, so they report the widest.
    private (long Min, long Max) ValRange(Val v)
    {
        switch (v)
        {
            case Constant c: return (c.Value, c.Value);
            case Temporary t:
                return tempRanges.TryGetValue(t.Name, out var r) ? r : RangeOfType(t.Type);
            case Variable varV: return RangeOfType(varV.Type);
            default: return (int.MinValue, int.MaxValue);
        }
    }

    // Range of `a op b` for the promoting operators, or null when it cannot be bounded
    // cheaply (non-constant or out-of-range shift count). Operands are 16-bit or narrower
    // wherever this is consulted, so the long arithmetic cannot overflow.
    private static (long Min, long Max)? BinaryResultRange(
        AstBinOp op, (long Min, long Max) a, (long Min, long Max) b)
    {
        switch (op)
        {
            case AstBinOp.Add: return (a.Min + b.Min, a.Max + b.Max);
            case AstBinOp.Sub: return (a.Min - b.Max, a.Max - b.Min);
            case AstBinOp.Mul:
            {
                long p1 = a.Min * b.Min, p2 = a.Min * b.Max;
                long p3 = a.Max * b.Min, p4 = a.Max * b.Max;
                return (Math.Min(Math.Min(p1, p2), Math.Min(p3, p4)),
                        Math.Max(Math.Max(p1, p2), Math.Max(p3, p4)));
            }
            case AstBinOp.LShift:
                if (b.Min != b.Max || b.Min < 0 || b.Min > 31) return null;
                return (a.Min << (int)b.Min, a.Max << (int)b.Min);
            default: return null;
        }
    }

    private Val VisitFStringExpr(FStringExpr expr)
    {
        string result = "";
        foreach (var part in expr.Parts)
        {
            if (!part.IsExpr) result += part.Text;
            else
            {
                // `{_GAINS}` interpolates a module-level tuple of constants: CPython
                // embeds the repr `(1, 4, 16, 60)` -- the name's scalar binding
                // resolved to 0 and embedded that instead (adafruit_tcs34725).
                if (part.Expr is VariableExpr ftv
                    && ModuleConstListValues(ftv.Name) is { } fcv)
                {
                    result += "(" + string.Join(", ", fcv)
                        + (fcv.Count == 1 ? "," : "") + ")";
                    continue;
                }
                Val val = VisitExpression(part.Expr!);
                if (val is Constant c)
                {
                    string? cText = c.Text ?? (stringIdToStr.TryGetValue(c.Value, out var s) ? s : null);
                    if (cText != null) result += cText;
                    else if (!string.IsNullOrEmpty(part.FormatSpec))
                        result += FormatFStringInt(c.Value, part.FormatSpec);
                    else result += c.Value.ToString();
                }
                else throw new TypeError(
                    "f-string interpolates a runtime value in a position PyMCU does not " +
                    "support. Supported: streaming (print(f\"...\"), uart.write_str/println" +
                    "(f\"...\")) and assignment to a variable (s = f\"...\" builds the string " +
                    "into a fixed buffer). Assign the f-string to a name first, then use " +
                    "that name here.",
                    expr.Line > 0 ? expr.Line : lastLine, expr.Column);
            }
        }

        if (!stringLiteralIds.ContainsKey(result))
        {
            stringLiteralIds[result] = nextStringId;
            stringIdToStr[nextStringId] = result;
            nextStringId++;
        }

        return new Constant(stringLiteralIds[result], result);
    }

    // Fold an int interpolation under a format spec -- the same [0][width][d|x|X|b|o]
    // subset ParseFormatSpec accepts (which throws for anything else) -- into its text,
    // instead of dropping the spec and emitting the decimal digits. `f"{value:X}"` is
    // how adafruit_ht16k33's print_hex renders its digits.
    private string FormatFStringInt(int value, string spec)
    {
        var (width, radix, pad, upper) = ParseFormatSpec(spec);
        bool neg = value < 0;
        ulong mag = neg ? (ulong)(-(long)value) : (ulong)value;
        string digits = radix switch
        {
            16 => mag.ToString(upper ? "X" : "x"),
            2 => Convert.ToString((long)mag, 2),
            8 => Convert.ToString((long)mag, 8),
            _ => mag.ToString(),
        };
        string signed = neg ? "-" + digits : digits;
        int padn = width - signed.Length;
        if (padn > 0)
            signed = pad == '0' && neg
                ? "-" + new string('0', padn) + digits
                : new string(pad, padn) + signed;
        return signed;
    }

    private static Val VisitLiteral(IntegerLiteral expr) => new Constant(expr.Value);

    private Val VisitVariable(VariableExpr expr)
    {
        // A str whose text differs per path has no compile-time value to hand back, and what
        // it does hold -- the interned id -- is not a number the program means. print() and a
        // comparison against a literal read it deliberately (multiStrHandleReads); every other
        // position used to receive the id and treat it as data.
        if (multiStrHandleReads == 0 && TryGetMultiStr(expr.Name, out _, out var vals, out _))
            throw MultiStrUseError(expr.Name, vals, expr);

        // A name bound to a `range(...)` is iterable and is not a value (#363). The `for` and
        // `reversed()` lowerings read the binding without coming through here, so reaching this
        // at all IS the value position: refuse it rather than hand back whatever the binding
        // happens to lower to, which would let `f(order)` through in silence.
        if (RangeBoundKeyOf(expr.Name) is { } rangeName)
            throw UserError(
                $"'{rangeName}' is bound to a range(), which is not a value on this target. Use "
                + $"it as the iterable of a for loop or of reversed(), which is what it is for; "
                + "to pass the numbers around, write them as a list.", expr);

        // A name bound to a row of a 2-D grid (`r = g[y]`, `for r in g`) is a
        // view, not a value -- every legal use (r[x], len(r), for x in r) is
        // intercepted before this, so reaching here IS the escape.
        if (ResolveRowRef(expr) != null)
            throw RowAliasNotAValue(expr);

        Val boundVal = ResolveBinding(expr.Name, expr);

        // RFC 0009 section 8: reading the payload of a runtime-tagged optional outside a
        // narrowing arm is the silent garbage the tag exists to stop. The tag-aware
        // readers -- `is None`, `if r:`, `r or d`, `return r`, `x = r`, an inline
        // parameter bind -- mark their reads with optionalReadAllowed; every other
        // position (arithmetic, .field, call, subscript, print, a real-subroutine
        // argument) refuses by name.
        if (TagOfVal(boundVal) != null && ValNameOf(boundVal) is { } boundName
            && !noneValuedNames.Contains(boundName))
        {
            if (narrowedOptionals.TryGetValue(boundName, out var boundIdx)
                && optionalMembersByName.TryGetValue(boundName, out var boundMembers)
                && boundIdx >= 0 && boundIdx < boundMembers.Count)
                // A narrowed name reads as ITS member: the low payload bytes at the
                // member's width, not the union's widest slot (RFC 0009 section 6).
                return MemberRead(boundVal, boundIdx, boundMembers);
            if (optionalReadAllowed == 0)
                throw UserError(
                    $"'{expr.Name}' may be None here; narrow it first "
                    + $"(`if {expr.Name} is not None:`).", expr);
        }

        return boundVal;
    }

    private static string BinaryOpSymbol(AstBinOp op) => op switch
    {
        AstBinOp.Add => "+", AstBinOp.Sub => "-", AstBinOp.Mul => "*",
        AstBinOp.Div => "/", AstBinOp.FloorDiv => "//", AstBinOp.Mod => "%",
        AstBinOp.BitAnd => "&", AstBinOp.BitOr => "|", AstBinOp.BitXor => "^",
        AstBinOp.LShift => "<<", AstBinOp.RShift => ">>",
        AstBinOp.Equal => "==", AstBinOp.NotEqual => "!=",
        AstBinOp.Less => "<", AstBinOp.LessEq => "<=",
        AstBinOp.Greater => ">", AstBinOp.GreaterEq => ">=",
        _ => op.ToString(),
    };

    /// <summary>
    /// Every spelling a bare name can be filed under in THIS scope, most specific first.
    ///
    /// An expansion owns its locals, then the function's own qualified spelling. Below that,
    /// and only at module level, the module-global spelling: "main" (and
    /// `&lt;mod&gt;___module_init`) IS the module's top level, and QualifyBoundName binds a name
    /// there as the module global of that name. Nothing looks OUTWARD from inside a real
    /// function, because a local `c` in uart_write_decimal_u16 is not the calling program's
    /// module-level object of the same name.
    /// </summary>
    private IEnumerable<string> ScopedNameKeys(string name)
    {
        // Inside an expansion the prefix is the WHOLE scope: a local of the expanded body is
        // filed under it, and `currentFunction` still names the CALLER. Falling through to the
        // caller's spelling answered the `w: uint32` counter of pymcu.hal.rp.cyw43.init with
        // the caller's `w = CYW43()`, and refused `while w > 0:` as an ordering on an object.
        if (!string.IsNullOrEmpty(currentInlinePrefix))
        {
            yield return currentInlinePrefix + name;
            yield break;
        }
        if (!string.IsNullOrEmpty(currentFunction)) yield return currentFunction + "." + name;
        if (string.IsNullOrEmpty(currentFunction)
            || currentFunction == "main"
            || currentFunction.EndsWith("___module_init", StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(currentModulePrefix)) yield return currentModulePrefix + name;
            yield return name;
        }
    }

    /// <summary>
    /// The storage a name stands for when THIS scope files it as an instance, or null.
    ///
    /// The name's own scoped key has to carry the class. An alias chain is not evidence of
    /// one: `absent = self._io.value` aliases the value read to the FIELD's storage, and that
    /// storage carries DigitalInOut, so following the chain inward concluded that a bool named
    /// `absent` was an object and folded `absent != 0` to true, deciding a run-time test of a
    /// pin. Aliases are followed only OUTWARD from a key that already carries the class, to
    /// canonicalize the storage two spellings of one object share.
    /// </summary>
    private string? InstanceStorageName(Expression e)
    {
        if (e is not VariableExpr ve) return null;
        foreach (string key in ScopedNameKeys(ve.Name))
        {
            if (!instanceClasses.TryGetValue(key, out var cls) || string.IsNullOrEmpty(cls))
                continue;
            string storage = key;
            for (int depth = 0; depth < 20; depth++)
            {
                if (!variableAliases.TryGetValue(storage, out var onward)
                    || string.IsNullOrEmpty(onward)) break;
                storage = onward;
            }
            return storage;
        }
        return null;
    }

    /// <summary>
    /// A comparison with an instance on at least one side that dispatches to no dunder.
    ///
    /// The operator used to lower numerically over the flattened instance handle, which is
    /// never written, so `a == b` answered "equal" for every pair of objects, `a == 0` answered
    /// true for any object, and `a &lt; 1` answered "less" for all of them (#491). CPython
    /// answers none of those: without `__eq__` it falls back to IDENTITY, and without an
    /// ordering dunder it raises TypeError, whatever sits on the other side.
    ///
    /// Identity is a compile-time fact here, since every instance owns a distinct static slot,
    /// so `==` / `!=` / `is` / `is not` fold to the answer CPython gives, and nothing that is
    /// not an instance is ever the same object as one. An ordering has no answer to fold to and
    /// is refused by name.
    ///
    /// The other side has to be one this resolver can be certain about: a literal, or a name
    /// that carries no class in this scope. A field, a call result or a subscript could still
    /// be an instance the tables answer for under a spelling this does not ask, so those keep
    /// the path they had.
    /// </summary>
    private Val? TryCompareInstanceOperands(BinaryExpr expr)
    {
        if (expr.Op is not (AstBinOp.Equal or AstBinOp.NotEqual or AstBinOp.Is or AstBinOp.IsNot
                            or AstBinOp.Less or AstBinOp.LessEq
                            or AstBinOp.Greater or AstBinOp.GreaterEq))
            return null;

        string? lname = InstanceStorageName(expr.Left);
        string? rname = InstanceStorageName(expr.Right);
        if (lname == null && rname == null) return null;

        if (lname == null || rname == null)
        {
            // Only a LITERAL on the other side. A name that carries no class here may still be
            // a second spelling of the same object (`b = a` files main.b as an alias and only
            // main.a carries the class), and answering "different objects" for that pair would
            // be as wrong as the handle comparison this replaces.
            Expression other = lname == null ? expr.Left : expr.Right;
            if (other is not (IntegerLiteral or FloatLiteral or StringLiteral)) return null;
        }

        if (expr.Op is AstBinOp.Equal or AstBinOp.NotEqual or AstBinOp.Is or AstBinOp.IsNot)
        {
            bool wantSame = expr.Op is AstBinOp.Equal or AstBinOp.Is;
            bool same = lname != null && rname != null && lname == rname;
            return new Constant(same == wantSame ? 1 : 0);
        }

        var instExpr = (VariableExpr)(lname != null ? expr.Left : expr.Right);
        string cls = InstanceClassOfName(instExpr.Name) ?? "";
        string shown = cls.Contains('_') ? cls[(cls.LastIndexOf('_') + 1)..] : cls;
        string needed = BinaryOpDunder(expr.Op) ?? "the comparison method";
        throw UserError(
            $"'{shown}' defines no {needed}, so '{BinaryOpSymbol(expr.Op)}' on one of its "
            + "instances has no meaning; CPython raises TypeError for it. Define "
            + $"{needed} on the class, or compare a field of the object instead.", expr);
    }

    /// <summary>
    /// The name an operator dunder's `self` has to alias, for the instance named on one side
    /// of a binary operator.
    ///
    /// The scoped spelling is the ordinary answer and is tried first, so nothing that already
    /// resolved changes. It is not the only one: a module-level instance is filed under its
    /// BARE name while `currentFunction` already reads "main", the synthesized module body, so
    /// `a + b` written at top level found no class and lowered numerically over a handle that
    /// is never written -- `print(a + b)` answered 0 while the same two lines inside a function
    /// answered what __add__ returns. ProbeBinding answers with the binding THIS scope sees, so
    /// a local of the same name still shadows the module-level one.
    /// </summary>
    private string BinaryDunderReceiver(string name)
    {
        foreach (string key in ScopedNameKeys(name))
            if (instanceClasses.ContainsKey(key)) return key;
        return string.IsNullOrEmpty(currentInlinePrefix)
            ? (string.IsNullOrEmpty(currentFunction) ? name : currentFunction + "." + name)
            : currentInlinePrefix + name;
    }

    /// <summary>
    /// True when a binary operator over <paramref name="left"/> is a dunder call rather than a
    /// numeric operation, under exactly the conditions <see cref="VisitBinary"/> dispatches on.
    /// The condition path asks this before lowering a comparison as a jump (#491).
    /// </summary>
    private bool BinaryDispatchesToDunder(Expression left, AstBinOp op)
    {
        if (BinaryOpDunder(op) is not { } dunder) return false;

        // A class-typed FIELD is a receiver too: `self.lhs == self.rhs` is the shape a driver
        // writes, and it resolved through neither table because only a bare name was looked up.
        if (left is MemberAccessExpr fieldRecv)
            return FieldInstanceClass(fieldRecv) is { } fieldCls
                   && ClassDefinesMethod(ResolveMROMethod(fieldCls, dunder), dunder);

        if (left is not VariableExpr lv) return false;
        string qname = BinaryDunderReceiver(lv.Name);
        if (!instanceClasses.TryGetValue(qname, out var cls) || string.IsNullOrEmpty(cls))
            return false;
        return inlineFunctions.ContainsKey(cls + "_" + dunder)
               || TryResolveInstanceMethodAst(lv.Name, dunder) != null;
    }

    private string? BinaryOpDunder(AstBinOp op)
    {
        return op switch
        {
            AstBinOp.Add => "__add__",
            AstBinOp.Sub => "__sub__",
            AstBinOp.Mul => "__mul__",
            AstBinOp.Pow => "__pow__",
            AstBinOp.Div => "__truediv__",
            AstBinOp.FloorDiv => "__floordiv__",
            AstBinOp.Mod => "__mod__",
            AstBinOp.BitAnd => "__and__",
            AstBinOp.BitOr => "__or__",
            AstBinOp.BitXor => "__xor__",
            AstBinOp.LShift => "__lshift__",
            AstBinOp.RShift => "__rshift__",
            AstBinOp.Equal => "__eq__",
            AstBinOp.NotEqual => "__ne__",
            AstBinOp.Less => "__lt__",
            AstBinOp.LessEq => "__le__",
            AstBinOp.Greater => "__gt__",
            AstBinOp.GreaterEq => "__ge__",
            _ => null
        };
    }

    // The reflected counterpart of a binary operator's dunder, for `2 + a` where the instance
    // is the RIGHT operand. Arithmetic and bitwise only: a comparison's reflection is a
    // different method rather than an `r`-prefixed name (`__lt__` reflects to `__gt__`), so
    // there is no `__rlt__` to look for and returning null leaves comparisons on their
    // existing path.
    private string? ReflectedOpDunder(AstBinOp op)
    {
        return op switch
        {
            AstBinOp.Add => "__radd__",
            AstBinOp.Sub => "__rsub__",
            AstBinOp.Mul => "__rmul__",
            AstBinOp.Div => "__rtruediv__",
            AstBinOp.FloorDiv => "__rfloordiv__",
            AstBinOp.Mod => "__rmod__",
            AstBinOp.BitAnd => "__rand__",
            AstBinOp.BitOr => "__ror__",
            AstBinOp.BitXor => "__rxor__",
            AstBinOp.LShift => "__rlshift__",
            AstBinOp.RShift => "__rrshift__",
            _ => null
        };
    }

    // True when an expression is None: the None literal, or a name currently bound
    // to None (a param defaulted to None, a variable assigned None). An integer or
    // a concrete instance is never None.
    /// <summary>
    /// `A.B.C` (and deeper) read as ONE name, when the head is a class or a module rather than
    /// an instance. Returns null whenever the joined name is not a global the scan filed, so
    /// every access that resolves some other way keeps the path it has.
    /// </summary>
    /// <summary>
    /// The class of the object a slice is taken of, without lowering anything (#329).
    ///
    /// The subscript path is about to throw, so the target must not be visited: doing that
    /// emits instructions for a program that is being refused. The instance tables answer the
    /// question directly, under the same key spellings the rest of the compiler uses, including
    /// the underscore-joined one a module-level instance gets (`microcontroller.nvm`).
    /// </summary>
    private string? SliceTargetClass(Expression target)
    {
        string? name = target switch
        {
            VariableExpr v => v.Name,
            MemberAccessExpr { Object: VariableExpr mo } m => mo.Name + "_" + m.Member,
            _ => null,
        };
        if (name == null) return null;

        foreach (string? key in new[]
                 {
                     string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + name,
                     string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + name,
                     string.IsNullOrEmpty(currentModulePrefix) ? null : currentModulePrefix + name,
                     name,
                 })
        {
            if (key != null && instanceClasses.TryGetValue(key, out string? cls)) return cls;
        }
        return null;
    }

    private Val? TryDottedClassConstant(MemberAccessExpr expr)
    {
        var tail = new List<string> { expr.Member };
        Expression cur = expr.Object;
        while (cur is MemberAccessExpr inner)
        {
            tail.Add(inner.Member);
            cur = inner.Object;
        }

        // One hop is the ordinary case and is resolved below, unchanged.
        if (tail.Count < 2 || cur is not VariableExpr root) return null;

        // Only a class or a module, never an instance: a local whose flattened field names
        // happen to join to the same string must keep its own resolution.
        string head = root.Name;
        bool headIsModule = StillNamesAModule(head);
        if (!headIsModule && !classNames.Contains(head) && !IsImportedAlias(head)) return null;
        if (!headIsModule && LooksLikeLocalInstance(head)) return null;

        if (TryImportedAlias(head, out var realMod) && realMod != null)
            head = headIsModule ? realMod : realMod + "." + AliasOriginal(root.Name);

        tail.Reverse();
        string mangled = (head + "." + string.Join(".", tail)).Replace('.', '_');

        foreach (var key in new[] { mangled, currentModulePrefix + mangled })
        {
            if (key.Length == 0) continue;
            if (globals.TryGetValue(key, out var sym))
                return sym.IsMemoryAddress ? new MemoryAddress(sym.Value, sym.Type) : new Constant(sym.Value);
            if (constantVariables.TryGetValue(key, out int cv))
                return new Constant(cv, ResolveStrConstant(key));
            if (mutableGlobals.TryGetValue(key, out var mt))
                return new Variable(key, mt);
        }

        return null;
    }

    /// <summary>Whether a bare name stands for a value in the code being lowered, not a type.</summary>
    private bool LooksLikeLocalInstance(string name)
    {
        foreach (var key in new[]
                 {
                     string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + name,
                     string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + name,
                     name,
                 })
        {
            if (key == null) continue;
            if (instanceClasses.ContainsKey(key) || variableTypes.ContainsKey(key)
                || variableAliases.ContainsKey(key)) return true;
        }
        return false;
    }

    /// `getattr(module, "name"[, default])` where the first argument is an imported
    /// module and the second a string literal: resolved at compile time to the member
    /// access itself when the module exports the name, to the default expression when
    /// it does not. Null when the shape is not a module getattr (the reflection
    /// refusal handles those), or when a two-argument form names a member the module
    /// does not export (the caller reports the AttributeError).
    private Expression? TryResolveModuleGetattr(CallExpr expr)
    {
        if (expr.Callee is not VariableExpr { Name: "getattr" }
            || expr.Args.Count is not (2 or 3)
            || expr.Args[0] is not VariableExpr getattrObj
            || expr.Args[1] is not StringLiteral getattrMember
            || !modules.ContainsKey(getattrObj.Name))
            return null;

        string getattrMod = TryImportedAlias(getattrObj.Name, out var realGetattrMod)
                            && realGetattrMod != null
            ? realGetattrMod : getattrObj.Name;
        if (ExportedNames(getattrMod).Contains(getattrMember.Value))
            return new MemberAccessExpr(getattrObj, getattrMember.Value)
                { Line = expr.Line, Column = expr.Column, Length = expr.Length };
        return expr.Args.Count == 3 ? expr.Args[2] : null;
    }

    private bool IsNoneValued(Expression e)
    {
        if (e is NoneLiteral) return true;

        // `getattr(module, "x", None)` on a module that does not export "x" resolves
        // to its default -- a None the compiler already knows, the shape
        // `power = getattr(board, "NEOPIXEL", None)` has in neopixel.py.
        if (e is CallExpr getattrCall
            && TryResolveModuleGetattr(getattrCall) is { } getattrRes)
            return IsNoneValued(getattrRes);

        // `obj.field is None`: a field assigned None is tracked under its flattened name
        // (<base>_<field>) by EmitMemberAssign. Resolve the same name without emitting code.
        if (e is MemberAccessExpr ma && ma.Object is VariableExpr mo)
        {
            string b = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + mo.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + mo.Name : mo.Name);
            for (int d = 0; d < 20 && variableAliases.TryGetValue(b, out var a); d++) b = a;
            if (noneValuedNames.Contains(b + "_" + ma.Member)) return true;
            // The write flattens under the name the object itself resolved to, which
            // for a module-level instance is the bare global (d1_chip_select) — no
            // function prefix. Same bare-name fallback the VariableExpr arm below has.
            if (b != mo.Name)
            {
                string bare = mo.Name;
                for (int d = 0; d < 20 && variableAliases.TryGetValue(bare, out var a2); d++) bare = a2;
                if (noneValuedNames.Contains(bare + "_" + ma.Member)) return true;
            }
            return false;
        }

        if (e is not VariableExpr ve) return false;

        string q = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + ve.Name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + ve.Name : ve.Name);
        if (noneValuedNames.Contains(q)) return true;
        for (int d = 0; d < 20 && variableAliases.TryGetValue(q, out var a); d++)
        {
            q = a;
            if (noneValuedNames.Contains(q)) return true;
        }
        return noneValuedNames.Contains(ve.Name);
    }

    private bool IsBareRegisterName(Expression expr)
    {
        if (expr is not VariableExpr v) return false;
        string q = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + v.Name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + v.Name : v.Name);
        if (variableTypes.ContainsKey(q)) return false;
        return constantAddressVariables.ContainsKey(v.Name)
            || constantAddressVariables.ContainsKey(q)
            || IsMemoryAddressGlobal(v.Name)
            || IsMemoryAddressGlobal(currentModulePrefix + v.Name);
    }

    private void RejectBareRegisterRead(Expression expr)
    {
        if (!IsBareRegisterName(expr)) return;
        string name = ((VariableExpr)expr).Name;
        throw UserError(
            $"'{name}' names a register, not its contents; use {name}.value to read the whole " +
            $"register, or {name}[bit] for one bit", expr);
    }

    private void RejectBareRegisterOperands(BinaryExpr expr)
    {
        bool arithmetic = expr.Op is AstBinOp.Add or AstBinOp.Sub or AstBinOp.Mul
            or AstBinOp.Div or AstBinOp.FloorDiv or AstBinOp.Mod or AstBinOp.BitAnd
            or AstBinOp.BitOr or AstBinOp.BitXor or AstBinOp.LShift or AstBinOp.RShift;
        if (arithmetic)
        {
            RejectBareRegisterRead(expr.Left);
            RejectBareRegisterRead(expr.Right);
            return;
        }

        bool relational = expr.Op is AstBinOp.Equal or AstBinOp.NotEqual or AstBinOp.Less
            or AstBinOp.LessEq or AstBinOp.Greater or AstBinOp.GreaterEq;
        if (!relational) return;
        if (IsBareRegisterName(expr.Left) && IsBareRegisterName(expr.Right)) return;
        if (expr.Right is IntegerLiteral) RejectBareRegisterRead(expr.Left);
        if (expr.Left is IntegerLiteral) RejectBareRegisterRead(expr.Right);
    }

    private Val VisitBinary(BinaryExpr expr)
    {
        RejectBareRegisterOperands(expr);

        RefuseSequenceComparison(expr);

        // Capture and CLEAR any explicit-cast width hint up front: it applies to THIS op only,
        // so operands (visited below) and nested ops promote normally. `uint8(a + b)` then makes
        // the `+` an 8-bit op (wrap + 8-bit flags), the escape hatch from default promotion.
        DataType? widthHint = castWidthHint;
        castWidthHint = null;

        // RFC 0009: `v is None` on a live runtime optional reads the tag byte. The
        // fold below only knows compile-time None and would answer FALSE for a name
        // whose tag says otherwise at run time -- the silent miscompile this exists for.
        if (TryEmitOptionalNoneTest(expr) is { } noneTest)
            return noneTest;

        // RFC 0009 decision 7, second half: a live Optional operand on an op
        // CPython faults on None for lowers to a member dispatch whose None
        // leaf raises TypeError. Runs before the dunder paths so `inst + opt`
        // still reaches the dunder through a leaf, and before the literal-None
        // refusal so `r + None` dispatches on r's tag.
        if (TryEmitGuardedBinary(expr) is { } guarded)
            return guarded;

        // None comparisons resolve at compile time with real null semantics: an
        // integer or a concrete instance is never None; only a name bound to None
        // (or the None literal itself) is. This replaces the old None==-1 model,
        // which made `x == None` collide with a real value of -1 / 255 / 0xFFFF.
        bool leftNone = IsNoneValued(expr.Left);
        bool rightNone = IsNoneValued(expr.Right);
        if (leftNone || rightNone)
        {
            if (expr.Op is AstBinOp.Equal or AstBinOp.NotEqual or AstBinOp.Is or AstBinOp.IsNot)
            {
                bool isEq = expr.Op is AstBinOp.Equal or AstBinOp.Is;
                Expression otherExpr = leftNone ? expr.Right : expr.Left;
                // IsNoneValued reads the name tables, so a call or a property read it
                // cannot classify answered "not None" without the callee ever running:
                // `self.fraction is None` folded FALSE even when the getter's only
                // reachable return was `return None`, and the value arm then computed
                // on a result slot that never received one. Lower those operands and
                // read the result -- an expansion whose returns all produced NoneVal
                // is the None the source is testing for.
                bool otherIsNone = leftNone && rightNone
                    || (otherExpr is CallExpr or MemberAccessExpr
                        ? VisitExpression(otherExpr) is NoneVal { LiveCallResult: false }
                        : IsNoneValued(otherExpr));
                return new Constant(otherIsNone == isEq ? 1 : 0);
            }
            // A None LITERAL in arithmetic is a program error. A NAME bound to None can only
            // reach this in code that is already dead -- the `if x is None:` fold above has
            // taken its branch, and what is left lowers but never runs (`gain(3)`'s
            // `return a + b`). Treating the name like a literal would refuse the dead half.
            if (expr.Left is NoneLiteral || expr.Right is NoneLiteral)
                throw new TypeError(
                    "None supports only ==, !=, is and is not comparisons",
                    expr.Line > 0 ? expr.Line : lastLine, expr.Column);
        }

        string? dunder = BinaryOpDunder(expr.Op);

        // `self.lhs + self.rhs` / `self.lhs == self.rhs`: the receiver is a class-typed FIELD,
        // which neither the instance table nor the method table answers for under a bare name,
        // so the operator lowered numerically over the field's flattened slot. Written as the
        // method call it stands for, which is the path a field receiver already dispatches
        // through (`self.pin.read()`).
        if (dunder != null && expr.Left is MemberAccessExpr fieldLhs
            && FieldInstanceClass(fieldLhs) is { } fieldLhsCls
            && ClassDefinesMethod(ResolveMROMethod(fieldLhsCls, dunder), dunder))
            return VisitCall(new CallExpr(
                new MemberAccessExpr(fieldLhs, dunder),
                new List<Expression> { expr.Right }) { Line = expr.Line });

        if (dunder != null && expr.Left is VariableExpr lv)
        {
            string qname = BinaryDunderReceiver(lv.Name);
            if (instanceClasses.TryGetValue(qname, out var cls) && !string.IsNullOrEmpty(cls))
            {
                string funcKey = cls + "_" + dunder;
                if (inlineFunctions.ContainsKey(funcKey))
                {
                    Val lhs = VisitExpression(expr.Left);
                    Val rhs = VisitExpression(expr.Right);
                    return EmitDunderCall(qname, cls, funcKey, new List<Val> { rhs });
                }

                // The dunder may be OUTLINED rather than inline, in which case it is absent
                // from inlineFunctions but present in methodAstByName. Falling through here
                // dropped the overload and lowered the operator numerically over the instance
                // handle, which is never written: `x + y` answered 0 and `x == y` answered
                // true for distinct instances. Dispatch it as the method call it stands for,
                // the same way `obj(args)` resolves __call__.
                if (TryResolveInstanceMethodAst(lv.Name, dunder) != null)
                    return VisitCall(new CallExpr(
                        new MemberAccessExpr(lv, dunder),
                        new List<Expression> { expr.Right }) { Line = expr.Line });
            }
        }

        // Reflected dispatch: `2 + a`, where the INSTANCE is on the right. CPython tries the
        // reflected dunder when the left operand's type does not implement the operator, which
        // is exactly the state reached here. Without it the operator lowered numerically over
        // the instance handle, and that slot is never written, so `2 + a` answered 2 and every
        // reflected dunder in the program was dead code with no diagnostic (#168).
        //
        // Ordering matters and matches Python: the forward dunder above wins whenever the left
        // operand is an instance that has it, so nothing that already dispatched changes.
        // Comparisons are deliberately excluded, since their reflection is not a name prefix
        // (`__lt__` reflects to `__gt__`, not to `__rlt__`) and no such method exists to find.
        string? rDunder = ReflectedOpDunder(expr.Op);
        if (rDunder != null && expr.Right is VariableExpr rvRefl)
        {
            string rqname = BinaryDunderReceiver(rvRefl.Name);
            if (instanceClasses.TryGetValue(rqname, out var rcls) && !string.IsNullOrEmpty(rcls))
            {
                string rFuncKey = rcls + "_" + rDunder;
                if (inlineFunctions.ContainsKey(rFuncKey))
                {
                    Val other = VisitExpression(expr.Left);
                    return EmitDunderCall(rqname, rcls, rFuncKey, new List<Val> { other });
                }

                // Outlined reflected dunder, same fallback the forward path uses.
                if (TryResolveInstanceMethodAst(rvRefl.Name, rDunder) != null)
                    return VisitCall(new CallExpr(
                        new MemberAccessExpr(rvRefl, rDunder),
                        new List<Expression> { expr.Left }) { Line = expr.Line });
            }
        }

        // Neither operator dunder claimed this comparison and an instance is on at least one
        // side, so CPython's fallback decides it: identity for equality, TypeError for an
        // ordering.
        if (TryCompareInstanceOperands(expr) is { } instCmp) return instCmp;

        if (expr.Op == AstBinOp.In || expr.Op == AstBinOp.NotIn)
        {
            bool negate = expr.Op == AstBinOp.NotIn;

            // `x in range(a, b, s)` is two comparisons and, for a step past 1, a remainder
            // test -- not a container lookup. Rewritten before the left side is visited, so
            // it is evaluated exactly once by the rewrite (PyMCU#288).
            if (expr.Right is CallExpr { Callee: VariableExpr { Name: "range" } } rangeIn)
                return VisitExpression(RangeMembershipAst(expr.Left, rangeIn, negate, expr));

            // `v in g[y]` / `v in r` / `v in g` on a 2-D grid. On a row it is an
            // element test over a view that has no value; on the whole grid it
            // tests ROW membership, and there is no row object to compare.
            if (expr.Right is IndexExpr inIx && ResolveGridKey(inIx.Target) != null)
            {
                if (inIx.Index is SliceExpr)
                    throw UserError(
                        "a 2-D grid cannot be sliced -- g[a:b] would have to be a window " +
                        "of rows, and rows are views, not values. Write the loop.", inIx.Index);
                throw UserError(
                    "'in' on a row of a 2-D grid asks whether v is one of its elements, " +
                    "but a row is a view, not an iterable value -- test an element " +
                    "(v == g[y][x]) or write the loop", inIx);
            }
            if (expr.Right is VariableExpr inVe && ResolveRowRef(inVe) != null)
                throw UserError(
                    "'in' on a row of a 2-D grid asks whether v is one of its elements, " +
                    "but a row is a view, not an iterable value -- test an element " +
                    "(v == r[x]) or write the loop", inVe);
            if (ResolveGridKey(expr.Right) != null)
                throw UserError(
                    "'in' on a 2-D grid tests row membership, and a row is a view, not a " +
                    "value -- test elements (v == g[y][x]) or write the loop", expr.Right);

            Val lhs = VisitExpression(expr.Left);

            // `"rp2" in uname()` bound as a VALUE -- a condition the frontend folded
            // never reaches here, but an `x = "rp2" in uname()` does. The call has no
            // runtime object (evaluating it below would refuse), so the same table the
            // frontend consults answers the membership. Only when the needle is a
            // compile-time string; anything else falls through to the call, which
            // refuses the way a bare `u = uname()` does.
            if (expr.Right is CallExpr { Args.Count: 0 } unameInCall
                && IsUnameCallee(unameInCall.Callee)
                && IntrospectionTable.IsKnownStdlib(deviceConfig.Stdlib)
                && (StringTextOfVal(lhs) ?? TryGetCompileTimeText(expr.Left)) is { } unameNeedle)
            {
                var uIn = IntrospectionTable.GetUname(deviceConfig);
                bool inTuple = unameNeedle == uIn.Sysname || unameNeedle == uIn.Nodename
                               || unameNeedle == uIn.Release || unameNeedle == uIn.Version
                               || unameNeedle == uIn.Machine;
                return new Constant((negate ? !inTuple : inTuple) ? 1 : 0);
            }

            // `pin not in self.pin_mapping.analog_pins`: a field bound to a class OBJECT
            // reaches a compile-time tuple through the field's tag byte. Before the string
            // paths below try to evaluate the attribute as a value (there is none -- it is
            // a sequence), dispatch membership per candidate class.
            if (expr.Right is MemberAccessExpr coInOuter
                && TryClassObjectAttrIn(coInOuter, lhs, negate, expr.Left) is { } coInRes)
                return coInRes;

            // A call that RETURNS an instance with __contains__ (`"Linux" not in uname()`,
            // the Adafruit DHT spelling) used to skip the dunder path because that path
            // only asked about a VariableExpr. Evaluate the call once, then dispatch the
            // same way a name bound to the result already does (#466). A call that
            // RETURNS a compile-time string is the substring form (`"RP2350" in
            // uname().machine` is a member access, handled below).
            if (expr.Right is CallExpr)
            {
                Val rhsVal = VisitExpression(expr.Right);
                if (TryContainsDunder(rhsVal, lhs, negate, out var fromCall))
                    return fromCall;
                if (TryStringContainsVal(rhsVal, lhs, negate, out var fromCallStr))
                    return fromCallStr;
            }

            if (expr.Right is VariableExpr rv)
            {
                if (TryContainsDunder(VisitExpression(rv), lhs, negate, out var fromName))
                    return fromName;

                // Outlined __contains__ (`11 in b` on a real method, outline-dunders):
                // the dunder is not @inline, so TryContainsDunder declines. Dispatch
                // through the original VariableExpr -- a PreEvaluated receiver does
                // not resolve an outlined method AST.
                if (TryResolveInstanceMethodAst(rv.Name, "__contains__") != null)
                {
                    Val res2 = VisitCall(new CallExpr(
                        new MemberAccessExpr(rv, "__contains__"),
                        new List<Expression> { expr.Left }) { Line = expr.Line });
                    if (negate)
                    {
                        Temporary neg2 = MakeTemp();
                        Emit(new Binary(PyMCU.IR.BinaryOp.Equal, res2, new Constant(0), neg2));
                        return neg2;
                    }
                    return res2;
                }
            }

            // Compile-time substring: `"RP2350" in uname().machine`. Both sides must be
            // strings the compiler already holds; there is no runtime search (#466).
            // Prefer the AST text of a field (`u.machine`) -- VisitExpression of a
            // flattened field is often a Variable whose interned id is not enough.
            // A class/instance dict or set (`value in cls.string`) is membership of
            // keys, not a substring: skip this path so TryGetDictFor below owns it.
            // A compile-time SEQUENCE attribute (`Cls.pins`) is element membership the
            // same way -- evaluating it as a value here has nothing to find.
            if (expr.Right is MemberAccessExpr or StringLiteral
                && !(expr.Right is MemberAccessExpr
                     && (TryGetDictFor(expr.Right, out _) || TryGetSetFor(expr.Right, out _)
                         || ResolveConstSequenceExpr(expr.Right) != null)))
            {
                string? hay = TryGetCompileTimeText(expr.Right)
                              ?? StringTextOfVal(VisitExpression(expr.Right));
                string? ned = StringTextOfVal(lhs) ?? TryGetCompileTimeText(expr.Left);
                if (hay != null && ned != null)
                    return new Constant(negate ? (hay.Contains(ned) ? 0 : 1)
                                               : (hay.Contains(ned) ? 1 : 0));
            }

            // `"W" in byteorder`: the name holds a compile-time string, so this is a
            // substring test -- the same fold the MemberAccessExpr/StringLiteral path
            // above performs. A name bound to a sequence, set or dict keeps the element
            // path below (its membership tests elements, not substrings).
            if (expr.Right is VariableExpr strRight
                && !TryGetSetBinding(strRight.Name, out _) && !TryGetDictBinding(strRight.Name, out _)
                && ElementsOfNamedSequence(strRight.Name) == null
                && TryGetCompileTimeText(strRight) is { } hayVar)
            {
                string? nedVar = StringTextOfVal(lhs) ?? TryGetCompileTimeText(expr.Left);
                if (nedVar != null)
                    return new Constant(negate ? (hayVar.Contains(nedVar) ? 0 : 1)
                                               : (hayVar.Contains(nedVar) ? 1 : 0));
            }

            // The RHS may be a list `[...]`, tuple `(...)`, set `{...}` or dict literal
            // (membership tests the KEYS, as in Python), directly or bound to a name.
            // Normalize to the element list.
            List<Frontend.Expression> rhsElems = expr.Right switch
            {
                ListExpr rl => rl.Elements,
                Frontend.TupleExpr rt => rt.Elements,
                Frontend.SetExpr rs => rs.Elements,
                Frontend.DictExpr rd => rd.Entries.Select(en => en.Key).ToList(),
                VariableExpr rsv when TryGetSetBinding(rsv.Name, out var sb) => sb.Elements,
                VariableExpr rdv when TryGetDictBinding(rdv.Name, out var db)
                    => db.Entries.Select(en => en.Key).ToList(),
                // A name bound to a LIST, which is what the message already said was allowed:
                // only sets and dicts were actually resolved through their name, so
                // `x in data` was refused by a sentence recommending the spelling it refused.
                VariableExpr rlv when ElementsOfNamedSequence(rlv.Name) is { } listBound
                    => listBound,
                // The same three tables reached through a FIELD. `num not in self.digits` is
                // how a driver validates its argument, and it was refused by a sentence
                // listing the spellings it had just been given.
                MemberAccessExpr when TryGetDictFor(expr.Right, out var fd)
                    => fd.Entries.Select(en => en.Key).ToList(),
                MemberAccessExpr when TryGetSetFor(expr.Right, out var fs) => fs.Elements,
                MemberAccessExpr when ResolveConstSequenceExpr(expr.Right) is { } fseq => fseq,
                _ => throw UserError(
                    "'in' / 'not in' requires a list, tuple, set or dict literal (or a name " +
                    "bound to one) on the right-hand side", expr.Right)
            };
            return EmitConstSeqMembership(lhs, rhsElems, negate, expr.Left);
        }

        if (expr.Op == AstBinOp.Is || expr.Op == AstBinOp.IsNot)
        {
            Val lhs = VisitExpression(expr.Left);
            Val rhs = VisitExpression(expr.Right);
            PyMCU.IR.BinaryOp bop = expr.Op == AstBinOp.Is ? PyMCU.IR.BinaryOp.Equal : PyMCU.IR.BinaryOp.NotEqual;
            if (lhs is Constant c1 && rhs is Constant c2)
            {
                return new Constant(bop == PyMCU.IR.BinaryOp.Equal
                    ? (c1.Value == c2.Value ? 1 : 0)
                    : (c1.Value != c2.Value ? 1 : 0));
            }

            if (rhs is Constant cr && cr.Value == -1 && !string.IsNullOrEmpty(GetValClass(lhs)))
            {
                return new Constant(bop == PyMCU.IR.BinaryOp.Equal ? 0 : 1);
            }

            Temporary dst2 = MakeTemp(DataType.UINT8);
            Emit(new Binary(bop, lhs, rhs, dst2));
            return dst2;
        }

        if (expr.Op is AstBinOp.And or AstBinOp.Or && LiveOptionalTag(expr.Left) is { } abTag)
        {
            // RFC 0009 section 5: the pick between operands is the TAG first. `r or d`
            // selects d on a None tag without the payload's truthiness being consulted;
            // `r and d` keeps r -- tag included -- whenever r is falsy.
            bool isOr = expr.Op == AstBinOp.Or;
            Val optPayload = EvalOptionalCarry(expr.Left);
            bool rightMaybeNone = ExprMayBeOptional(expr.Right);
            // `or` with a concrete default is never None; `and` keeps the left tag on
            // the falsy path, so its result stays optional as long as the left is. A
            // left whose union has two or more real members is tag-dependent whichever
            // way the pick goes, so the result keeps the tag too.
            int leftReal = abTag.members.Count - (abTag.noneIdx >= 0 ? 1 : 0);
            bool resultOptional = !isOr || rightMaybeNone || leftReal > 1;
            // The result holds whichever operand runs, so it is sized for both -- a
            // wider right (`v or 500`) must not truncate to the payload width.
            var rightT = InferExprType(expr.Right);
            Temporary optResult = MakeTemp(rightT == DataType.UNKNOWN ? GetValType(optPayload)
                : DataTypeExtensions.GetPromotedType(GetValType(optPayload), rightT));
            string optEndLabel = MakeLabel();
            Variable? resTag = resultOptional ? TagStorageFor(optResult.Name) : null;
            Emit(new Copy(optPayload, optResult));
            if (resTag != null) Emit(new Copy(abTag.tag, resTag));
            EmitOptionalTruthJump(abTag.tag, optPayload, abTag.members, optEndLabel, isOr);
            Val optRight = VisitExpression(expr.Right);
            Emit(new Copy(optRight is NoneVal ? new Constant(0) : optRight, optResult));
            if (resTag != null)
            {
                var members = isOr
                    ? UnionMerge(UnionMembersOf(optPayload, expr.Left, null)
                            .Where(m => m != "None").ToList(),
                        UnionMembersOf(optRight, expr.Right, null))
                    : UnionMerge(UnionMembersOf(optPayload, expr.Left, null),
                        UnionMembersOf(optRight, expr.Right, null));
                Emit(new Copy(ArmTagFor(optRight, expr.Right, null, members,
                    optResult.Name), resTag));
                MarkOptional(optResult.Name, resTag, members);
            }
            Emit(new Label(optEndLabel));
            return optResult;
        }

        if (expr.Op == AstBinOp.And)
        {
            // Python `a and b` evaluates to the OPERAND, not a bool: falsy a -> a,
            // otherwise b. Short-circuits b. (`if a and b:` is unaffected since it
            // only tests truthiness; the difference shows in `x = a and b`.)
            // Deciding which operand to keep IS a truth test, so an instance operand goes
            // through its __bool__ here too.
            Val v1a = VisitExpression(LowerInstanceTruthiness(expr.Left));
            if (v1a is Constant c1a)
                return c1a.Value == 0 ? c1a : VisitExpression(expr.Right);

            Temporary result = MakeTemp(GetValType(v1a));
            string endLabel = MakeLabel();
            Emit(new Copy(v1a, result));                 // tentatively a
            Emit(new JumpIfZero(result, endLabel));      // a falsy -> keep a
            Val v2b = VisitExpression(expr.Right);
            Emit(new Copy(v2b, result));                 // a truthy -> b
            Emit(new Label(endLabel));
            return result;
        }

        if (expr.Op == AstBinOp.Or)
        {
            // Python `a or b`: truthy a -> a, otherwise b. Short-circuits b. Choosing between
            // them is a truth test, so an instance operand goes through its __bool__.
            Val v1a = VisitExpression(LowerInstanceTruthiness(expr.Left));
            if (v1a is Constant c1a)
                return c1a.Value != 0 ? c1a : VisitExpression(expr.Right);

            Temporary result = MakeTemp(GetValType(v1a));
            string endLabel = MakeLabel();
            Emit(new Copy(v1a, result));                 // tentatively a
            Emit(new JumpIfNotZero(result, endLabel));   // a truthy -> keep a
            Val v2b = VisitExpression(expr.Right);
            Emit(new Copy(v2b, result));                 // a falsy -> b
            Emit(new Label(endLabel));
            return result;
        }

        if (expr.Op == AstBinOp.Pow)
            return LowerPow(VisitExpression(expr.Left), VisitExpression(expr.Right),
                expr.Right, "** operator");

        // `s == "running"` where s holds one of several texts: interning gives equal texts the
        // same id, so the comparison IS the id comparison, decided at run time. Reading the id
        // is allowed here and only here (the fold below would answer a flat False instead).
        bool cmpAgainstStrLiteral = expr.Op is AstBinOp.Equal or AstBinOp.NotEqual
            && (expr.Left is StringLiteral || expr.Right is StringLiteral);
        if (cmpAgainstStrLiteral) multiStrHandleReads++;
        Val v1 = VisitExpression(expr.Left);
        // `a + b` on runtime heap lists is a concatenation, not a pointer add --
        // without this the two GC_REF operands fell to the numeric path and the
        // answer was the sum of two addresses. The check runs before the right
        // operand is evaluated because a Temporary left operand is not a GC
        // root: the concat emitter roots it before anything else can allocate.
        if (expr.Op == AstBinOp.Add && ListKeyOfVal(v1) is not null)
            return EmitRuntimeListConcat(v1, expr.Right, expr);
        Val v2 = VisitExpression(expr.Right);
        if (expr.Op == AstBinOp.Add && ListKeyOfVal(v2) is not null)
            throw new TypeError(
                "cannot concatenate a non-list value with a list; both operands of '+' " +
                "must be lists on this target", expr.Line > 0 ? expr.Line : lastLine, expr.Column);
        if (cmpAgainstStrLiteral) multiStrHandleReads--;

        // The operand is a run-time-decided string only where the read above was allowed:
        // anywhere else VisitVariable has already refused it by name.
        bool multiStrOperand = cmpAgainstStrLiteral
            && ((expr.Left is VariableExpr mlv && TryGetMultiStr(mlv.Name, out _, out _, out _))
                || (expr.Right is VariableExpr mrv && TryGetMultiStr(mrv.Name, out _, out _, out _)));

        // A string operand is one whose text the compiler can name at compile time: either the
        // expression is a literal, or it is a name/field already known to hold fixed text
        // (StaticStringOf -- the same AST-level lookup f-strings and .join() use for exactly
        // this), or -- for a nested fold like `"a" + "b" + "c"` -- the Val already computed
        // above carries its Text from a previous fold. Asking the VALUE alone was the old (and
        // broken) test: an interned id collides with an ordinary integer (`x * 256`), and a
        // one-character string's id IS its character code, so a lookup table can only answer
        // nonsense either way. Gating on the AST being a literal, as before, was narrower still:
        // a plain variable holding a string (`a = "hello"; b = "world"; a + b`) was never
        // recognised as a string at all and fell through to integer arithmetic on the interned
        // ids of "hello"/"world" (#438), and the same gate folded `x == "abc"` to False because
        // `x` (not a literal) was never seen as a string either.
        string? LeftText() => multiStrOperand ? null
            : StaticStringOf(expr.Left) ?? (v1 is Constant { Text: { } lt } ? lt : null);
        string? RightText() => multiStrOperand ? null
            : StaticStringOf(expr.Right) ?? (v2 is Constant { Text: { } rt } ? rt : null);
        string? leftText = LeftText();
        string? rightText = RightText();
        if (leftText != null || rightText != null)
        {
            bool bothStr = leftText != null && rightText != null;

            // Equality folds at compile time: interning gives identical strings the
            // same ID, and a string is never equal to a non-string. This keeps the
            // `if pin_name == "PB5"` / `__CHIP__ == "..."` dispatch idiom working.
            if (expr.Op is AstBinOp.Equal or AstBinOp.NotEqual)
            {
                bool equal = bothStr && leftText == rightText;
                bool isEq = expr.Op == AstBinOp.Equal;
                return new Constant(equal == isEq ? 1 : 0);
            }

            // Compile-time concatenation of two compile-time-known strings. The join carries
            // its own Text so a further `+ "c"` on the result folds through the Val branch
            // above instead of falling back to the id table.
            if (expr.Op == AstBinOp.Add && bothStr)
            {
                string joined = leftText! + rightText!;
                if (!stringLiteralIds.TryGetValue(joined, out int joinedId))
                {
                    joinedId = nextStringId++;
                    stringLiteralIds[joined] = joinedId;
                    stringIdToStr[joinedId] = joined;
                }
                return new Constant(joinedId, joined);
            }

            int errLine = expr.Line > 0 ? expr.Line : lastLine;
            if (expr.Op == AstBinOp.Add)
                throw new TypeError(
                    "cannot concatenate a string with a non-string value; both operands of '+' " +
                    "must be compile-time string literals (runtime string building is not supported)",
                    errLine, expr.Column);

            throw new TypeError(
                $"operator '{BinaryOpSymbol(expr.Op)}' is not supported on string values",
                errLine, expr.Column);
        }

        double? AsFloatCt(Val v)
        {
            if (v is FloatConstant fc) return fc.Value;
            if (v is Variable vv && floatConstantVariables.TryGetValue(vv.Name, out double f)) return f;
            if (v is Constant cv) return cv.Value;
            return null;
        }

        bool v1IsFloat = v1 is FloatConstant
            || (v1 is Variable vv1 && floatConstantVariables.ContainsKey(vv1.Name));
        bool v2IsFloat = v2 is FloatConstant
            || (v2 is Variable vv2 && floatConstantVariables.ContainsKey(vv2.Name));
        bool eitherFloat = v1IsFloat || v2IsFloat
            || GetValType(v1) == DataType.FLOAT || GetValType(v2) == DataType.FLOAT;
        if (eitherFloat)
        {
            // Bitwise and shift operators are undefined on floats (Python raises TypeError).
            // Without this guard the constant fold below hit its `_ => 0.0` default, silently
            // folding e.g. `1.5 & 2` to 0.0 and then dropping the whole assignment.
            if (expr.Op is AstBinOp.BitAnd or AstBinOp.BitOr or AstBinOp.BitXor
                or AstBinOp.LShift or AstBinOp.RShift)
                throw new TypeError(
                    $"unsupported operand type for {BinaryOpSymbol(expr.Op)}: 'float'",
                    expr.Line > 0 ? expr.Line : lastLine, expr.Column);

            double? f1 = AsFloatCt(v1);
            double? f2 = AsFloatCt(v2);

            // Dividing by a literal zero is an error whatever the dividend is, exactly as on
            // the integer path. The fold below used to answer 0.0 for it, so `p / 0.0` compiled
            // clean and put a plausible zero on the port.
            if (f2 is 0.0 && expr.Op is AstBinOp.Div or AstBinOp.FloorDiv or AstBinOp.Mod)
                throw new ValueError("float division by zero",
                    expr.Line > 0 ? expr.Line : lastLine, expr.Column);

            bool isCompare = expr.Op is AstBinOp.Equal or AstBinOp.NotEqual
                or AstBinOp.Less or AstBinOp.LessEq or AstBinOp.Greater or AstBinOp.GreaterEq;

            if (f1.HasValue && f2.HasValue)
            {
                // Compile-time fold: both operands are known constants. A comparison is a
                // bool, not a float -- it used to fall to the `_ => 0.0` arm and fold to
                // FloatConstant(0.0) whatever the operands were, so `0.0 <= value <= 1.0`
                // on a bound float parameter read as false and its `raise` fired on 0.5.
                if (isCompare)
                    return new Constant((expr.Op switch
                    {
                        AstBinOp.Equal => f1.Value == f2.Value,
                        AstBinOp.NotEqual => f1.Value != f2.Value,
                        AstBinOp.Less => f1.Value < f2.Value,
                        AstBinOp.LessEq => f1.Value <= f2.Value,
                        AstBinOp.Greater => f1.Value > f2.Value,
                        AstBinOp.GreaterEq => f1.Value >= f2.Value,
                        _ => false
                    }) ? 1 : 0);

                double res = expr.Op switch
                {
                    AstBinOp.Add => f1.Value + f2.Value,
                    AstBinOp.Sub => f1.Value - f2.Value,
                    AstBinOp.Mul => f1.Value * f2.Value,
                    AstBinOp.Div => f2.Value != 0.0 ? f1.Value / f2.Value : 0.0,
                    // Python float `//` floors the quotient toward -inf (7.0 // 2.0 == 3.0).
                    AstBinOp.FloorDiv => f2.Value != 0.0 ? Math.Floor(f1.Value / f2.Value) : 0.0,
                    AstBinOp.Mod => f1.Value % f2.Value,
                    _ => 0.0
                };
                return new FloatConstant(res);
            }

            // Runtime float operation: emit Binary with FLOAT destination.
            static BinaryOp MapOp(AstBinOp op) => op switch
            {
                AstBinOp.Add => BinaryOp.Add,
                AstBinOp.Sub => BinaryOp.Sub,
                AstBinOp.Mul => BinaryOp.Mul,
                AstBinOp.Div => BinaryOp.Div,
                // Keep FloorDiv distinct so the backend can floor the quotient (float `//`).
                AstBinOp.FloorDiv => BinaryOp.FloorDiv,
                AstBinOp.Mod => BinaryOp.Mod,
                AstBinOp.Equal => BinaryOp.Equal,
                AstBinOp.NotEqual => BinaryOp.NotEqual,
                AstBinOp.Less => BinaryOp.LessThan,
                AstBinOp.LessEq => BinaryOp.LessEqual,
                AstBinOp.Greater => BinaryOp.GreaterThan,
                AstBinOp.GreaterEq => BinaryOp.GreaterEqual,
                _ => throw new NotSupportedException($"Float op {op} not supported at runtime")
            };

            // Run-time float divide/modulo by zero raises ZeroDivisionError, matching Python and
            // matching what the INTEGER path has always done. Without this the division produced
            // an infinity, and print() renders an infinity as 0.0, so the one value a reader
            // would take as a legitimate result is what reached the port.
            //
            // The test is a float EQUALITY against zero rather than a test of the four bytes,
            // because negative zero has its sign bit set: `p / -0.0` raises in Python too, and a
            // bit test would let it through. Only a divisor the compiler cannot see pays for it.
            if (expr.Op is AstBinOp.Div or AstBinOp.FloorDiv or AstBinOp.Mod
                && AsFloatCt(v2) is null)
            {
                Temporary isZero = MakeTemp(DataType.UINT8);
                Emit(new Binary(BinaryOp.Equal, v2, new FloatConstant(0.0), isZero));
                string divOk = MakeLabel();
                Emit(new JumpIfZero(isZero, divOk));
                string? localCatchF = tryCatchStack.Count > 0 ? tryCatchStack[^1] : null;
                EmitPendingFinally(localCatchF != null ? tryFinallyFloor[^1] : 0);
                Emit(new SignalError(new Constant(6 /* ZeroDivisionError */), localCatchF));
                Emit(new Label(divOk));
            }

            Temporary floatDst = MakeTemp(isCompare ? DataType.UINT8 : DataType.FLOAT);
            Emit(new Binary(MapOp(expr.Op), v1, v2, floatDst));
            return floatDst;
        }

        // Reaching here means both operands are integers. Python 3's `/` is TRUE division and
        // always yields a float (5 / 2 == 2.5, even 4 / 2 == 2.0), while `//` is floor division.
        // Stay faithful: promote both operands to float and emit float division. This links the
        // floating-point routines into the firmware, so warn once per site that `//` is the
        // cheaper integer-division operator in case that is what the user meant.
        if (expr.Op == AstBinOp.Div)
        {
            int dline = expr.Line > 0 ? expr.Line : lastLine;
            if (warningNoticed.Add($"truediv:{dline}"))
                PyMCU.Common.Diagnostic.Warning(line: dline, code: "truediv-links-float", text: $"line {dline}: '/' is floating-point "
                    + "(true) division in Python and always yields a float; it links float "
                    + "routines into the firmware — use '//' for integer division if that is what you meant");

            Val ToFloatVal(Val x)
            {
                if (x is FloatConstant) return x;
                if (x is Constant ci) return new FloatConstant(ci.Value);
                Temporary ft = MakeTemp(DataType.FLOAT);
                Emit(new Copy(x, ft));
                return ft;
            }

            Val fa = ToFloatVal(v1);
            Val fb = ToFloatVal(v2);
            if (fa is FloatConstant fca && fb is FloatConstant fcb)
            {
                // Same rule as everywhere else: a literal zero divisor is the error, not 0.0.
                if (fcb.Value == 0.0)
                    throw new ValueError("division by zero", dline, expr.Column);
                return new FloatConstant(fca.Value / fcb.Value);
            }
            if (fb is not FloatConstant)
            {
                Temporary isZeroI = MakeTemp(DataType.UINT8);
                Emit(new Binary(BinaryOp.Equal, fb, new FloatConstant(0.0), isZeroI));
                string divOkI = MakeLabel();
                Emit(new JumpIfZero(isZeroI, divOkI));
                string? localCatchI = tryCatchStack.Count > 0 ? tryCatchStack[^1] : null;
                EmitPendingFinally(localCatchI != null ? tryFinallyFloor[^1] : 0);
                Emit(new SignalError(new Constant(6 /* ZeroDivisionError */), localCatchI));
                Emit(new Label(divOkI));
            }
            Temporary fdst = MakeTemp(DataType.FLOAT);
            Emit(new Binary(BinaryOp.Div, fa, fb, fdst));
            return fdst;
        }

        DataType t1 = GetValType(v1);
        DataType t2 = GetValType(v2);
        // A literal operand is type-agnostic (it defaults to uint8), so on a same-size op it
        // would wrongly win and drop the other operand's signedness: `int8(0) - int8(x)` became
        // uint8, making a later `< 0` test unsigned (abs() then returned the value unchanged).
        // Take the non-constant operand's type when exactly one side is a constant.
        bool lConst = v1 is Constant or FloatConstant;
        bool rConst = v2 is Constant or FloatConstant;
        DataType resType;
        if (t1.SizeOf() != t2.SizeOf())
            resType = t1.SizeOf() > t2.SizeOf() ? t1 : t2;   // the wider operand wins (e.g. 256 * u8 -> u16)
        else if (lConst && !rConst) resType = t2;            // same size: a literal is type-agnostic,
        else if (rConst && !lConst) resType = t1;            // so take the typed operand (keeps its sign)
        else resType = t1;                                   // both/neither constant: keep left (prior behaviour)

        // Python-fidelity: integer add/sub/mul/shift PROMOTES the result to the next wider type so
        // a same-width op never silently overflows (uint8+uint8 -> uint16 = 300, not 44; uint16*
        // uint16 -> uint32). The declared type is a STORAGE width; narrowing happens only at an
        // explicit store or cast. Capped at 32-bit (64-bit is impractical on AVR, wraps there).
        // Bitwise/compare/div/mod cannot overflow their width and are not promoted. The backend
        // widens narrower operands into the result width when loading them.
        // The promotion is by storage type, so it over-widens whenever the operands cannot
        // reach the type's limits. `hi * 256` with hi:uint8 tops out at 65280 — a uint16 —
        // yet promoting to uint32 cost AVR a four-byte frame spill to reload the low half.
        // Skip the promotion when the result provably FITS the unpromoted type: no value is
        // truncated, so semantics are unchanged, and nothing that was narrow gets widened.
        (long Min, long Max)? resRange = null;
        if (resType is not DataType.FLOAT
            && expr.Op is AstBinOp.Add or AstBinOp.Sub or AstBinOp.Mul or AstBinOp.LShift)
        {
            resRange = BinaryResultRange(expr.Op, ValRange(v1), ValRange(v2));
            var (tMin, tMax) = RangeOfType(resType);
            bool fits = resRange is (long rMin, long rMax) && rMin >= tMin && rMax <= tMax;
            // The wider type must also be able to be NEGATIVE when the result can be. Promoting
            // uint8 to uint16 for `-7 * a` (a: uint8) computed -49 as 65487, and the `// 7`
            // that followed answered 9355 on a clean build.
            bool negResult = resRange is (long nMin, _) && nMin < 0;
            if (!fits)
                resType = resType switch
                {
                    DataType.UINT8 => negResult ? DataType.INT16 : DataType.UINT16,
                    DataType.INT8 => DataType.INT16,
                    DataType.UINT16 => negResult ? DataType.INT32 : DataType.UINT32,
                    DataType.INT16 => DataType.INT32,
                    _ => resType,
                };
        }

        // An explicit cast around this op (`uint8(a + b)`) forces fixed-width: compute at the
        // cast's width, overriding promotion. Gives wraparound + the matching 8/16-bit flags.
        if (widthHint is DataType hint && hint is not DataType.FLOAT) resType = hint;

        Temporary dst = MakeTemp(resType);
        // Record the range so a consumer of this temp promotes on the real values rather
        // than on the storage type. Only when it fits the emitted type: an explicit cast
        // (widthHint) narrows on purpose, and the wrapped value is no longer bounded by it.
        if (resRange is (long dMin, long dMax))
        {
            var (fMin, fMax) = RangeOfType(resType);
            if (dMin >= fMin && dMax <= fMax) tempRanges[dst.Name] = (dMin, dMax);
        }

        // Dividing by a literal zero is an error whatever the dividend is. Python raises
        // ZeroDivisionError for `a // 0` too, and this used to reach the AVR division routine
        // with a zero divisor and hand back 255 on a clean build. The runtime check further
        // down only guards a divisor the compiler cannot see.
        if (v2 is Constant { Value: 0 } && expr.Op is AstBinOp.Div or AstBinOp.FloorDiv or AstBinOp.Mod)
            throw new ValueError("integer division or modulo by zero",
                expr.Line > 0 ? expr.Line : lastLine, expr.Column);

        if (v1 is Constant cA && v2 is Constant cB)
        {

            // A shift count outside 0..31 has no meaning for PyMCU's fixed-width ints and
            // would otherwise fold to a wrong value (C# masks the count to 5 bits, so
            // `1 << 99` silently becomes `1 << 3`).
            if (expr.Op is AstBinOp.LShift or AstBinOp.RShift && (cB.Value < 0 || cB.Value >= 32))
            {
                // A string reaching arithmetic through a variable carries no literal
                // for the check above to notice, so it arrives here as its interned
                // id -- and the report was a shift count nobody wrote. Passing a pin
                // name where a pin number belongs (`Pin("GP25")` on RP2040) lands
                // exactly here. The id could also be an ordinary integer, so this
                // only ever runs on the way to an error that was happening anyway.
                if (stringIdToStr.TryGetValue(cB.Value, out string? asText))
                    throw new TypeError(
                        $"cannot shift by the string \"{asText}\" -- a number is expected here. "
                        + "This usually means a name was passed where a number belongs "
                        + $"(for example a pin name instead of a pin number).",
                        expr.Line > 0 ? expr.Line : lastLine, expr.Column);

                throw new ValueError($"shift count {cB.Value} out of range (expected 0..31)",
                    expr.Line > 0 ? expr.Line : lastLine, expr.Column);
            }

            // int32 is the widest integer PyMCU has, so a constant that leaves its range has
            // no value to fold to. Computing it in 32 bits wrapped instead: `-2147483648 - 1`
            // became 2147483647 without a word, while the same overflow one width down
            // (`int16 = -32768 - 1`) was already a build error.
            Constant Fold(long result)
            {
                if (result < int.MinValue || result > int.MaxValue)
                    throw new ValueError(
                        $"the constant {result} does not fit in int32, the widest integer type "
                        + "(the operands are folded at compile time, so there is no width to "
                        + "carry it)", expr.Line > 0 ? expr.Line : lastLine, expr.Column);
                return new Constant((int)result);
            }

            switch (expr.Op)
            {
                case AstBinOp.Add: return Fold((long)cA.Value + cB.Value);
                case AstBinOp.Sub: return Fold((long)cA.Value - cB.Value);
                case AstBinOp.Equal: return new Constant(cA.Value == cB.Value ? 1 : 0);
                case AstBinOp.NotEqual: return new Constant(cA.Value != cB.Value ? 1 : 0);
                case AstBinOp.Mul: return Fold((long)cA.Value * cB.Value);
                // Divided in long, and NOT routed through Fold(). C# throws
                // OverflowException on int.MinValue / -1 and int.MinValue % -1, the two cases
                // whose true quotient is 2147483648; unhandled, it reached the user as
                // `InternalCompilerError: OverflowException` at line 1:1 (#223). int8 and
                // int16 never hit it, since 128 and 32768 fit the int they were computed in.
                //
                // These wrap rather than diagnosing, unlike Add/Sub/Mul above, because the
                // executed answer is the wrap and the narrower widths already fold to it:
                // int8 MIN // -1 folds to -128 and runs as -128, int16 to -32768, and int32
                // now folds to -2147483648, which is what an atmega328p produces for the same
                // expression with opaque operands. MIN % -1 is 0 at every width. Add, Sub and
                // Mul leave the range routinely and have no single representable answer, so
                // they keep diagnosing; a floored division of two in-range operands can only
                // leave it in this one case, and there the hardware has already answered.
                case AstBinOp.Div: return new Constant(unchecked((int)((long)cA.Value / cB.Value)));
                case AstBinOp.FloorDiv:
                    long q = (long)cA.Value / cB.Value;
                    if ((cA.Value ^ cB.Value) < 0 && q * cB.Value != cA.Value) q--;
                    return new Constant(unchecked((int)q));
                case AstBinOp.Mod:
                    // Python's % follows the sign of the divisor (floored), unlike C#'s
                    // truncated %. e.g. -7 % 3 == 2, not -1. Match Python at fold time.
                    long rem = (long)cA.Value % cB.Value;
                    if (rem != 0 && ((rem < 0) != (cB.Value < 0))) rem += cB.Value;
                    return new Constant(unchecked((int)rem));
                case AstBinOp.BitAnd: return new Constant(cA.Value & cB.Value);
                case AstBinOp.BitOr: return new Constant(cA.Value | cB.Value);
                case AstBinOp.LShift: return new Constant(cA.Value << cB.Value);
                case AstBinOp.RShift: return new Constant(cA.Value >> cB.Value);
                case AstBinOp.Less: return new Constant(cA.Value < cB.Value ? 1 : 0);
                case AstBinOp.LessEq: return new Constant(cA.Value <= cB.Value ? 1 : 0);
                case AstBinOp.Greater: return new Constant(cA.Value > cB.Value ? 1 : 0);
                case AstBinOp.GreaterEq: return new Constant(cA.Value >= cB.Value ? 1 : 0);
            }
        }

        // Fold compile-time ptr-register comparisons (e.g. `if pin_reg == PIND:`).
        // Both sides resolve to MemoryAddress when the ptr variable is in
        // constantAddressVariables and the RHS is a globals entry with IsMemoryAddress.
        // This allows the if/elif dispatch tree in pin_pulse_in to be fully DCE'd.
        if (v1 is MemoryAddress maL && v2 is MemoryAddress maR)
        {
            switch (expr.Op)
            {
                case AstBinOp.Equal:     return new Constant(maL.Address == maR.Address ? 1 : 0);
                case AstBinOp.NotEqual:  return new Constant(maL.Address != maR.Address ? 1 : 0);
                case AstBinOp.Less:      return new Constant(maL.Address <  maR.Address ? 1 : 0);
                case AstBinOp.LessEq:    return new Constant(maL.Address <= maR.Address ? 1 : 0);
                case AstBinOp.Greater:   return new Constant(maL.Address >  maR.Address ? 1 : 0);
                case AstBinOp.GreaterEq: return new Constant(maL.Address >= maR.Address ? 1 : 0);
                // Non-comparison ops on two ptrs fall through to normal Binary emit.
            }
        }

        // Runtime divide/modulo by zero raises ZeroDivisionError, matching Python (a constant
        // zero divisor is already a compile-time error above). The check guards only a runtime
        // divisor — a non-zero constant divisor pays nothing. SignalError delivers to the local
        // catch dispatcher inside a try, else propagates to the caller via the T-flag.
        if (expr.Op is AstBinOp.Div or AstBinOp.FloorDiv or AstBinOp.Mod && v2 is not Constant)
        {
            string divOk = MakeLabel();
            Emit(new JumpIfNotZero(v2, divOk));
            string? localCatch = tryCatchStack.Count > 0 ? tryCatchStack[^1] : null;
            EmitPendingFinally(localCatch != null ? tryFinallyFloor[^1] : 0);
            Emit(new SignalError(new Constant(6 /* ZeroDivisionError */), localCatch));
            Emit(new Label(divOk));
        }

        // A comparison answers about VALUES, so both sides have to be read in a type that can
        // hold both. `int8(100) > uint8(200)` compared the 200 as the -56 its bits spell in
        // int8 and said True; `int8(100) < 200` materialized the 200 the same way and said
        // False. Widening the operands here fixes every backend at once, because the widths
        // reach the backend already agreed.
        if (expr.Op is AstBinOp.Equal or AstBinOp.NotEqual or AstBinOp.Less
            or AstBinOp.LessEq or AstBinOp.Greater or AstBinOp.GreaterEq)
        {
            if (FoldComparisonByRange(expr.Op, v1, v2) is { } known)
                return new Constant(known ? 1 : 0);
            DataType cmp = ComparisonType(v1, v2);
            v1 = WidenForComparison(v1, cmp, left: true);
            v2 = WidenForComparison(v2, cmp);
        }

        Emit(new Binary(MapBinaryOp(expr.Op), v1, v2, dst));
        return dst;
    }

    /// <summary>
    /// The type both sides of a comparison must be read in: the narrowest one whose range
    /// covers the values each side can actually take. Returns VOID when the existing widths
    /// already agree on every value, which leaves the operands untouched.
    /// </summary>
    private DataType ComparisonType(Val v1, Val v2)
    {
        DataType t1 = GetValType(v1), t2 = GetValType(v2);
        if (t1 is DataType.FLOAT || t2 is DataType.FLOAT) return DataType.VOID;
        if (!IsIntegerType(t1) || !IsIntegerType(t2)) return DataType.VOID;
        if (t1 == t2) return DataType.VOID;

        // The backends compare at the width of the LEFT operand, so "one side fits the
        // other's type" was only enough when the wider side was the left one: `count < 300`
        // with count: uint8 compared against 44, and `x < n` with n: uint16 read n's low byte.
        // Any two different types now meet in the narrowest one that covers both, and
        // WidenForComparison puts the left side there.
        var (lo1, hi1) = ValRange(v1);
        var (lo2, hi2) = ValRange(v2);
        long lo = Math.Min(lo1, lo2), hi = Math.Max(hi1, hi2);

        foreach (var candidate in new[]
                 { DataType.INT8, DataType.UINT8, DataType.INT16, DataType.UINT16,
                   DataType.INT32, DataType.UINT32 })
        {
            var (cLo, cHi) = RangeOfType(candidate);
            if (lo >= cLo && hi <= cHi) return candidate;
        }
        return DataType.VOID;
    }

    // A comparison whose two sides cannot overlap has one answer for every run: `count < 300`
    // with count: uint8 is always true, `count == 300` never. Python says so, and the
    // alternative -- reading 300 at count's width as 44 -- was wrong code. Null when the
    // ranges overlap and the test is a real one.
    private bool? FoldComparisonByRange(Frontend.BinaryOp op, Val v1, Val v2)
    {
        if (!IsIntegerType(GetValType(v1)) || !IsIntegerType(GetValType(v2))) return null;
        if (v1 is not (Constant or Variable or Temporary) || v2 is not (Constant or Variable or Temporary)) return null;
        var (lo1, hi1) = ValRange(v1);
        var (lo2, hi2) = ValRange(v2);
        bool alwaysLess = hi1 < lo2, alwaysGreater = lo1 > hi2;
        if (!alwaysLess && !alwaysGreater) return null;
        return op switch
        {
            Frontend.BinaryOp.Less => alwaysLess,
            Frontend.BinaryOp.LessEq => alwaysLess,
            Frontend.BinaryOp.Greater => alwaysGreater,
            Frontend.BinaryOp.GreaterEq => alwaysGreater,
            Frontend.BinaryOp.Equal => false,
            Frontend.BinaryOp.NotEqual => true,
            _ => null,
        };
    }

    private static bool IsIntegerType(DataType t) => t is DataType.UINT8 or DataType.INT8
        or DataType.UINT16 or DataType.INT16 or DataType.UINT32 or DataType.INT32;

    private Val WidenForComparison(Val v, DataType to, bool left = false)
    {
        if (to is DataType.VOID || GetValType(v) == to) return v;
        // A literal on the RIGHT is materialised at the left side's width, which is the
        // covering one by now. On the LEFT it decides the width, so it is widened like a
        // variable: `5 < n` with n: uint16 compared n's low byte before this.
        if (v is Constant && !left) return v;

        Temporary wide = MakeTemp(to);
        Emit(new Copy(v, wide));
        return wide;
    }

    private Val VisitTernary(TernaryExpr expr)
    {
        // `1 if obj else 0` asks for the object's truth value exactly as `if obj:` does, and
        // only the statement forms were routed through the protocol. Here the raw handle was
        // tested instead, so an instance whose __bool__ says true came out false.
        // `Pin(dp_pin, Pin.OUT) if dp_pin else None` with dp_pin None: the true branch cannot
        // run, and lowering it refused the program for a constructor argument that only the
        // dead side ever supplies (PyMCU#334).
        // The protocol rewrite comes FIRST, as it does for an `if` (EmitOptimizedConditionalJump
        // lowers before it asks about None). In the other order a field holding an instance was
        // read as None -- a field whose own name never receives a value, because the class
        // collapsed onto the field below it -- and the conditional expression answered with its
        // false side without ever reaching the class: `1 if self._echo else 0` was 0 for an
        // object whose __len__ says otherwise (#385). A field that really is None has no class,
        // so the rewrite leaves it alone and the check below still decides it (#334).
        Expression truthCond = LowerInstanceTruthiness(expr.Condition);
        if (IsNoneValued(truthCond)) return VisitExpression(expr.FalseVal);

        // A bare optional as the condition is a tag test, not a payload read -- the
        // carry-eval keeps the section-8 refusal for everything inside the arms.
        Val cond = EvalOptionalCarry(truthCond);
        if (cond is Constant c)
        {
            if (c.Value != 0) return VisitExpression(expr.TrueVal);
            return VisitExpression(expr.FalseVal);
        }

        string falseLabel = MakeLabel();
        string endLabel = MakeLabel();

        // The result temp must be as wide as the WIDER of the two branches, not just the
        // true branch: `7 if c else wide` (true=uint8, false=uint16) typed the temp uint8
        // and truncated the 16-bit false value (500 -> 244). Visit both branches to learn
        // their real types, promote, then splice the true-branch copy into the true block
        // (the false branch is emitted between the true tail and the join).
        // RFC 0009: a bare optional as the condition tests its tag first; `r if r is
        // not None else d` narrows r on the true side only, and the result carries a
        // tag only when an arm can hand back None. The narrowing rides BranchState:
        // the all-agree join below drops whatever only one arm proved.
        var ternSnap = TakeBranchState();
        if (LiveOptionalTag(truthCond) is { } ternTag)
        {
            EmitOptionalTruthJump(ternTag.tag, cond, ternTag.members, falseLabel, false);
        }
        else
        {
            Emit(new JumpIfZero(cond, falseLabel));
        }
        ApplyOptionalCondEffect(truthCond, true);
        Val trueVal = VisitExpression(expr.TrueVal);
        int trueTail = currentInstructions.Count;   // where the true copy + jump belong
        var trueArmSnap = TakeBranchState();
        RestoreBranchState(ternSnap);
        ApplyOptionalCondEffect(truthCond, false);
        Emit(new Label(falseLabel));
        Val falseVal = VisitExpression(expr.FalseVal);
        var falseArmSnap = TakeBranchState();
        // The condition's narrowing belongs to its arm; past the expression the name
        // is whatever the arms agree on -- an arm runs under its own guard, so a
        // walrus or call in one cannot answer reads in the other either.
        var ternDisagreed = JoinBranchStates(
            new List<BranchState?> { trueArmSnap, falseArmSnap }, ternSnap, exhaustive: true);
        foreach (var key in ternDisagreed)
        {
            var candidates = new List<string?>();
            if (ternSnap.StrConstantVariables.TryGetValue(key, out var tBefore))
                candidates.Add(tBefore);
            foreach (var snap in new[] { trueArmSnap, falseArmSnap })
                if (snap.StrConstantVariables.TryGetValue(key, out var bv))
                    candidates.Add(bv);
            MarkMultiStr(key, candidates);
        }
        Temporary result = MakeTemp(
            DataTypeExtensions.GetPromotedType(GetValType(trueVal), GetValType(falseVal)));
        Emit(new Copy(falseVal, result));
        Emit(new Label(endLabel));

        var trueMembers = UnionMembersOf(trueVal, expr.TrueVal, trueArmSnap);
        var falseMembers = UnionMembersOf(falseVal, expr.FalseVal, falseArmSnap);
        var members = UnionMerge(trueMembers, falseMembers);
        Val? resultTag = null;
        // The result carries a tag when an arm can hand back a value the promoted
        // slot cannot represent: a None (no payload), or an arm that is itself a
        // live union (its payload width is tag-dependent). Two scalar arms promote
        // into each other and need no tag -- the same answer phase 1 gave.
        if (members.Contains("None") || trueMembers.Count > 1 || falseMembers.Count > 1)
        {
            resultTag = TagStorageFor(result.Name);
            Emit(new Copy(ArmTagFor(falseVal, expr.FalseVal, falseArmSnap, members,
                result.Name), resultTag));
            currentInstructions.Insert(trueTail,
                new Copy(ArmTagFor(trueVal, expr.TrueVal, trueArmSnap, members,
                    result.Name), resultTag));
            MarkOptional(result.Name, resultTag, members);
        }
        // Splice [Copy trueVal->result; Jump end] just after the true-branch body, ahead of
        // the false label. Insert in reverse so the first index stays valid.
        currentInstructions.Insert(trueTail, new Jump(endLabel));
        currentInstructions.Insert(trueTail, new Copy(trueVal, result));
        return result;
    }

    // pow() / ** : integer constant-fold, integer unroll, or libm powf (#463).
    //
    // A non-negative integer exponent still unrolls to multiply -- including a float
    // base, so `x ** 2` stays two muls and does not pull powf. A fractional exponent
    // (adafruit_tcs34725's `pow(x, 2.5)` gamma) cannot unroll; it is IEEE-754 single
    // powf, the same routine C would call.
    private Val LowerPow(Val bv, Val ev, ASTNode at, string form)
    {
        bool baseFloat = bv is FloatConstant || GetValType(bv) == DataType.FLOAT;

        static bool TryIntExponent(Val v, out int exp)
        {
            if (v is Constant c) { exp = c.Value; return true; }
            if (v is FloatConstant fc
                && fc.Value >= 0
                && fc.Value <= int.MaxValue
                && fc.Value == Math.Truncate(fc.Value))
            {
                exp = (int)fc.Value;
                return true;
            }
            exp = 0;
            return false;
        }

        if (TryIntExponent(ev, out int exp))
        {
            if (exp < 0)
                throw UserError(
                    $"{form}: negative exponent not supported (Python would return a float)", at);

            if (!baseFloat && bv is Constant cb)
            {
                int res = 1;
                for (int k = 0; k < exp; ++k) res *= cb.Value;
                return new Constant(res);
            }

            if (exp == 0) return baseFloat ? new FloatConstant(1.0) : new Constant(1);
            if (exp == 1) return bv;
            if (exp > 16)
                throw UserError(
                    $"{form}: exponent too large to unroll (max 16 for a runtime base); use a loop",
                    at);

            static DataType BumpTier(DataType t) => t switch
            {
                DataType.UINT8 => DataType.UINT16,
                DataType.INT8 => DataType.INT16,
                DataType.UINT16 => DataType.UINT32,
                DataType.INT16 => DataType.INT32,
                _ => t,
            };

            Val acc = bv;
            for (int k = 1; k < exp; ++k)
            {
                DataType mt = DataTypeExtensions.GetPromotedType(GetValType(acc), GetValType(bv));
                if (mt is not DataType.FLOAT) mt = BumpTier(mt);
                Temporary md = MakeTemp(mt);
                Emit(new Binary(MapBinaryOp(AstBinOp.Mul), acc, bv, md));
                acc = md;
            }
            return acc;
        }

        // A runtime integer exponent on an integer base stays refused: Python would
        // still be integer exponentiation, and silently routing it through powf would
        // change the type. A float base, or a non-integer constant exponent, is powf.
        bool expFloat = ev is FloatConstant || GetValType(ev) == DataType.FLOAT;
        if (!baseFloat && !expFloat)
            throw UserError($"{form}: the exponent must be a compile-time constant integer", at);

        double? AsCt(Val v)
        {
            if (v is FloatConstant fc) return fc.Value;
            if (v is Constant cv) return cv.Value;
            return null;
        }

        double? fb = AsCt(bv);
        double? fe = AsCt(ev);
        if (fb.HasValue && fe.HasValue)
            return new FloatConstant((float)Math.Pow(fb.Value, fe.Value));

        Val ToFloat(Val x)
        {
            if (x is FloatConstant) return x;
            if (x is Constant ci) return new FloatConstant(ci.Value);
            if (GetValType(x) == DataType.FLOAT) return x;
            Temporary ft = MakeTemp(DataType.FLOAT);
            Emit(new Copy(x, ft));
            return ft;
        }

        Temporary dst = MakeTemp(DataType.FLOAT);
        Emit(new Binary(BinaryOp.Pow, ToFloat(bv), ToFloat(ev), dst));
        return dst;
    }

    private Val VisitUnary(UnaryExpr expr)
    {
        // `not obj` is a truth test like `if obj:`, and only the statement forms went through
        // the protocol: the raw handle was negated instead, so `not x` answered true for an
        // instance whose __bool__ says true. The other unary operators take a number, not a
        // truth value, and are left alone.
        Expression unaryOperand =
            expr.Op == AstUnOp.Not ? LowerInstanceTruthiness(expr.Operand) : expr.Operand;

        // `not x` where x is bound to None: None is falsy, so the answer is always 1 --
        // the same decision EmitOptimizedConditionalJump makes for `if not x:`. Without
        // this the name read as its slot, which nothing ever wrote.
        if (expr.Op == AstUnOp.Not && IsNoneValued(unaryOperand)) return new Constant(1);

        // RFC 0009: `not r` on a live optional is a tag test first -- true when the tag
        // says None, else the payload's falseness decides. The payload is never read
        // for its value here, only tested.
        if (expr.Op == AstUnOp.Not && LiveOptionalTag(unaryOperand) is { } notTag)
        {
            Val notPayload = EvalOptionalCarry(unaryOperand);
            int realMembers = notTag.members.Count - (notTag.noneIdx >= 0 ? 1 : 0);
            if (notTag.noneIdx >= 0 && realMembers == 1)
            {
                // The phase-1 [X, None] shape: tag-None OR payload-zero, three ops.
                Temporary isNoneT = MakeTemp(DataType.UINT8);
                Emit(new Binary(BinaryOp.Equal, notTag.tag, new Constant(notTag.noneIdx), isNoneT));
                Temporary isZeroT = MakeTemp(DataType.UINT8);
                Emit(new Binary(BinaryOp.Equal, notPayload, new Constant(0), isZeroT));
                Temporary notResult = MakeTemp(DataType.UINT8);
                Emit(new Binary(BinaryOp.BitOr, isNoneT, isZeroT, notResult));
                return notResult;
            }
            // Multi-member union: falsy = tag-None or payload-as-member-zero, the
            // same dispatch EmitOptionalTruthJump emits, folded to a boolean temp.
            Temporary notRes = MakeTemp(DataType.UINT8);
            Emit(new Copy(new Constant(1), notRes));
            string notDone = MakeLabel();
            EmitOptionalTruthJump(notTag.tag, notPayload, notTag.members, notDone, false);
            Emit(new Copy(new Constant(0), notRes));
            Emit(new Label(notDone));
            return notRes;
        }
        // `-r`, `~r` and friends need the payload -- a live optional dispatches
        // on its tag and raises TypeError on the None member.
        if (TryEmitGuardedUnary(expr) is { } guardedUnary)
            return guardedUnary;
        if (expr.Op != AstUnOp.Not && unaryOperand is VariableExpr uv
            && OptionalKeyOf(uv.Name) is { } unKey && !narrowedOptionals.ContainsKey(unKey))
            throw UserError(
                $"'{uv.Name}' may be None here; narrow it first "
                + $"(`if {uv.Name} is not None:`).", expr);

        Val operand = VisitExpression(unaryOperand);

        string cls = GetValClass(operand);
        if (!string.IsNullOrEmpty(cls))
        {
            string? dunder = expr.Op == AstUnOp.Negate ? "__neg__" : (expr.Op == AstUnOp.BitNot ? "__invert__" : null);
            if (dunder != null)
            {
                string funcKey = cls + "_" + dunder;
                if (inlineFunctions.ContainsKey(funcKey))
                {
                    string selfName = operand is Variable v ? v.Name : (operand is Temporary t ? t.Name : "");
                    return EmitDunderCall(selfName, cls, funcKey, new List<Val>());
                }
            }
        }

        // Bitwise NOT is undefined on a float (Python raises TypeError). Without this guard
        // `~1.5` fell through to a Unary BitNot over a FloatConstant — a silent miscompile.
        if (expr.Op == AstUnOp.BitNot
            && (operand is FloatConstant
                || (operand is Variable fv && floatConstantVariables.ContainsKey(fv.Name))
                || GetValType(operand) == DataType.FLOAT))
            throw new TypeError("unsupported operand type for ~: 'float'",
                expr.Line > 0 ? expr.Line : lastLine, expr.Column);

        if (operand is Constant c)
        {
            switch (expr.Op)
            {
                case AstUnOp.Negate: return new Constant(-c.Value);
                case AstUnOp.Not: return new Constant(c.Value == 0 ? 1 : 0);
                case AstUnOp.BitNot: return new Constant(~c.Value);
            }
        }

        if (operand is FloatConstant fco)
        {
            switch (expr.Op)
            {
                case AstUnOp.Negate: return new FloatConstant(-fco.Value);
                case AstUnOp.Not: return new Constant(fco.Value == 0.0 ? 1 : 0);
            }
        }

        if (expr.Op == AstUnOp.Deref)
        {
            DataType derefElem = RuntimePtrElem(operand);
            Temporary res2 = MakeTemp(derefElem);
            Emit(new LoadIndirect(operand, res2, derefElem));
            return res2;
        }

        // `not x` always yields a 1-byte bool regardless of the operand's type --
        // minting the result temp with GetValType(operand) widens it to e.g. FLOAT
        // and a later conditional jump reads stale bytes past the stored bool.
        Temporary result = MakeTemp(
            expr.Op == AstUnOp.Not ? DataType.UINT8 : GetValType(operand));
        Emit(new Unary(MapUnaryOp(expr.Op), operand, result));
        return result;
    }

    private Val VisitYield(YieldExpr expr)
    {
        throw UserError(
            "'yield' is only supported in top-level plain functions (lowered to a " +
            "state-machine class) -- not inside @inline functions or class methods. " +
            "Move the generator to module level, or fill a fixed-size array instead.", expr);
    }

    // Resolves a variable name to the bytes/list/tuple literal bound to it as an
    // inline parameter, following the inline prefix and any variableAliases chain
    // (same resolution as the for-in path in Iteration.cs). Returns null if the
    // name is not a sequence-literal parameter.
    private Frontend.ListExpr? ResolveListLiteralParam(string name)
    {
        string? key = currentInlinePrefix + name;
        for (var depth = 0; depth < 20; depth++)
        {
            if (key != null && listLiteralParams.TryGetValue(key, out var bound)) return bound;
            if (key != null && variableAliases.TryGetValue(key, out var alias)) key = alias;
            else break;
        }
        return null;
    }

    // Dict/set literal bindings, looked up with the standard qualification order.
    private bool TryGetDictBinding(string name, out Frontend.DictExpr dict)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix)
            && dictLiteralBindings.TryGetValue(currentInlinePrefix + name, out dict!)) return true;
        if (!string.IsNullOrEmpty(currentFunction)
            && dictLiteralBindings.TryGetValue(currentFunction + "." + name, out dict!)) return true;
        // The scan files a module-level dict as `mod_<name>`; its init-function lowering may
        // re-register it as `mod___module_init.<name>`. Both are probed for the module(s) the
        // current context belongs to -- a bare name in `mod`'s code means `mod`'s global.
        foreach (var mp in OwningModulePrefixes())
        {
            if (dictLiteralBindings.TryGetValue(mp + name, out dict!)) return true;
            if (dictLiteralBindings.TryGetValue(mp + "__module_init." + name, out dict!)) return true;
        }
        return dictLiteralBindings.TryGetValue(name, out dict!);
    }

    private bool TryGetSetBinding(string name, out Frontend.SetExpr set)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix)
            && setLiteralBindings.TryGetValue(currentInlinePrefix + name, out set!)) return true;
        if (!string.IsNullOrEmpty(currentFunction)
            && setLiteralBindings.TryGetValue(currentFunction + "." + name, out set!)) return true;
        foreach (var mp in OwningModulePrefixes())
        {
            if (setLiteralBindings.TryGetValue(mp + name, out set!)) return true;
            if (setLiteralBindings.TryGetValue(mp + "__module_init." + name, out set!)) return true;
        }
        return setLiteralBindings.TryGetValue(name, out set!);
    }

    // Lower `d[k]` on a dict literal. Every entry must fold to constants (string keys and
    // values fold to their interned ids). A constant key folds the whole lookup; a runtime
    // key becomes a compare chain over the keys, raising KeyError on no match -- exactly
    // Python's semantics, riding the existing exception model.
    // `defaultExpr` is d.get()'s fallback: with it a missing key yields that value instead of
    // raising KeyError, at compile time for a constant key and as the compare chain's else for
    // a runtime one.
    private Val EmitDictLookup(Frontend.DictExpr d, Expression keyExpr, Expression? defaultExpr = null)
    {
        var entries = new List<(int[] Key, Val Value, bool StrKey, string? Text)>();
        foreach (var (kE, vE) in d.Entries)
        {
            Val vV = VisitExpression(vE);
            if (vV is not Constant && vV is not FloatConstant)
                throw UserError("dict literals are compile-time lookup tables: every key and " +
                                "value must be a compile-time constant", d);
            // A tuple key (`{(0, 1): x}`) is a constant key of several parts; each part must
            // fold, and a lookup matches when every part compares equal.
            if (kE is Frontend.TupleExpr tk)
            {
                var parts = new List<int>();
                foreach (var te in tk.Elements)
                {
                    if (VisitExpression(te) is not Constant tp)
                        throw UserError("dict literals are compile-time lookup tables: every key " +
                                        "and value must be a compile-time constant", d);
                    parts.Add(tp.Value);
                }
                entries.Add((parts.ToArray(), vV, false, null));
                continue;
            }
            Val kV = VisitExpression(kE);
            if (kV is not Constant kc)
                throw UserError("dict literals are compile-time lookup tables: every key and " +
                                "value must be a compile-time constant", d);
            entries.Add((new[] { kc.Value }, vV, kE is StringLiteral, kc.Text));
        }

        Val[] keyParts = keyExpr is Frontend.TupleExpr kt
            ? kt.Elements.Select(VisitExpression).ToArray()
            : new[] { VisitExpression(keyExpr) };
        if (keyParts.All(p => p is Constant))
        {
            var keyC = keyParts.Cast<Constant>().ToArray();
            // Match on the TEXT when both sides carry it, and only fall back to the numbers when
            // one of them does not. A one-character string has two encodings: as a literal it
            // folds to its character code, and through a name it resolves to its interned id.
            // Comparing the numbers read those as different strings, so `k = "a"` missed the
            // entry `"a"` -- `d[k]` raised KeyError naming 256, a number the program never wrote,
            // and `d.get(k, 1)` silently returned the default (PyMCU#215). A multi-character key
            // was unaffected because both encodings are the same interned id for it.
            //
            // Both spellings arrive here: `d[k]` and `d.get(k, default)` are one site, differing
            // only in whether defaultExpr is null, so this covers both.
            foreach (var e in entries)
            {
                if (e.Key.Length != keyC.Length) continue;
                bool hit = e.Key.Length == 1 && e.Text != null && keyC[0].Text != null
                    ? e.Text == keyC[0].Text
                    : e.Key.Zip(keyC, (ek, kc) => ek == kc.Value).All(b => b);
                if (hit) return e.Value;
            }

            if (defaultExpr != null) return VisitExpression(defaultExpr);

            // A miss the program HANDLES is not a compile error (#331). `try: d[k] except
            // KeyError:` is a program that works, and the compiler seeing the key became a
            // reason to refuse it as soon as reads of locals began to fold: `k = 7` then
            // `d[k]` inside a try went from raising at run time and being caught to not
            // compiling at all. Getting better at reading a program must not make a working
            // program stop building, so the raise is emitted where the handler can take it,
            // which is the same instruction the run-time key path emits for the same miss.
            if (tryCatchStack.Count > 0)
            {
                EmitPendingFinally(tryFinallyFloor[^1]);
                Emit(new SignalError(new Constant(4 /* KeyError */), tryCatchStack[^1]));
                return MakeTemp(DataType.UINT8);
            }

            string miss = keyC.Length == 1
                ? DescribeDictKey(keyExpr, keyC[0])
                : "(" + string.Join(", ", keyC.Select(c => c.Value.ToString())) + ")";
            throw UserError($"KeyError: {miss} is not a key of " +
                            "this dict literal (checked at compile time)", keyExpr);
        }

        // A ONE-CHARACTER string key IS a character code: the literal folds to it, which is
        // what already lets `ch in d` compare a run-time byte against these keys. Only a
        // MULTI-character key is different -- it resolves to an interned id, which a byte read
        // off a UART can never equal -- so only that one has to be constant (PyMCU#338).
        if (entries.Any(e => e.StrKey && (e.Text?.Length ?? 0) != 1))
            throw UserError(
                "a dict with multi-character string keys needs a compile-time constant key: "
                + "such a key is an interned id, and a run-time value is never one. "
                + "One-character keys are character codes and DO match a run-time byte.",
                keyExpr);
        if (entries.Count == 0)
            throw UserError("KeyError: lookup on an empty dict literal", d);

        // Result width from the value range. A float value (VEML7700's 0.25 / 0.125
        // gains) makes the whole lookup a float; mixed int/float is that too.
        bool anyFloat = entries.Any(e => e.Value is FloatConstant);
        DataType rt;
        if (anyFloat)
            rt = DataType.FLOAT;
        else
        {
            int min = entries.Min(e => ((Constant)e.Value).Value);
            int max = entries.Max(e => ((Constant)e.Value).Value);
            rt = min < 0
                ? (min >= short.MinValue && max <= short.MaxValue ? DataType.INT16 : DataType.INT32)
                : (max <= 0xFF ? DataType.UINT8 : max <= 0xFFFF ? DataType.UINT16 : DataType.UINT32);
        }

        Temporary result = MakeTemp(rt);
        string endL = MakeLabel();
        foreach (var e in entries)
        {
            string next = MakeLabel();
            // A part-wise key (tuple) matches when every part compares equal: a length
            // mismatch or one unequal part skips the entry.
            if (e.Key.Length == keyParts.Length)
            {
                for (int i = 0; i < e.Key.Length; i++)
                    Emit(new JumpIfNotEqual(keyParts[i], new Constant(e.Key[i]), next));
                Val stored = anyFloat && e.Value is Constant ic
                    ? new FloatConstant(ic.Value)
                    : e.Value;
                Emit(new Copy(stored, result));
                Emit(new Jump(endL));
            }
            Emit(new Label(next));
        }
        if (defaultExpr != null)
        {
            Emit(new Copy(VisitExpression(defaultExpr), result));
        }
        else
        {
            // No key matched: raise KeyError (caught by an enclosing try, else propagates).
            string? localCatch = tryCatchStack.Count > 0 ? tryCatchStack[^1] : null;
            EmitPendingFinally(localCatch != null ? tryFinallyFloor[^1] : 0);
            Emit(new SignalError(new Constant(4 /* KeyError */), localCatch));
        }
        Emit(new Label(endL));
        return result;
    }

    // Name the key the way the program wrote it. A name bound to a string reaches here as a
    // VariableExpr, so the old fall-through printed the folded number and the message read
    // "KeyError: 256", which is not anything the source says.
    private string DescribeDictKey(Expression keyExpr, Constant folded) => keyExpr switch
    {
        StringLiteral s => $"\"{s.Value}\"",
        _ when folded.Text != null => $"\"{folded.Text}\"",
        _ => folded.Value.ToString(),
    };


    // Reads one element of a table that lives in flash. A byte-wide element is a single
    // ArrayLoadFlash; a wider one is stored little-endian as bytes and reassembled here, so no
    // backend has to learn a new width.
    private Val EmitFlashArrayRead(string qualified, Val idxVal, int sz)
    {
                DataType felem = arrayElemTypes.TryGetValue(qualified, out var fe)
                    ? fe : DataType.UINT8;
                int fsize = felem.SizeOf();
                if (fsize <= 1)
                {
                    Temporary tmp = MakeTemp(felem);
                    Emit(new ArrayLoadFlash(qualified, idxVal, tmp));
                    return tmp;
                }

                // A table wider than a byte is stored little-endian as bytes, so the
                // element is read one byte at a time and reassembled here. ArrayLoadFlash
                // stays a byte load and no backend has to learn a new width, which is what
                // lets every target that reads a const[uint8[N]] table read a wide one.
                // A constant index folds to constant byte offsets, so the common case is
                // the same LPM sequence repeated, not index arithmetic.
                // The offset is a BYTE offset, so it outgrows eight bits before the element
                // count does: a uint16 table of 200 entries is 400 bytes, and index 128
                // scales to 256. Widen the offset temp once the table crosses that line, and
                // keep the narrow one for the tables that do not.
                DataType offType = sz * fsize > 256 ? DataType.UINT16 : DataType.UINT8;
                Val ByteOffset(int b)
                {
                    if (idxVal is Constant kc) return new Constant(kc.Value * fsize + b);
                    Temporary scaled = MakeTemp(offType);
                    Emit(new Binary(BinaryOp.Mul, idxVal, new Constant(fsize), scaled));
                    if (b == 0) return scaled;
                    Temporary off = MakeTemp(offType);
                    Emit(new Binary(BinaryOp.Add, scaled, new Constant(b), off));
                    return off;
                }

                // Built from the TOP byte down: each step shifts what is already there up
                // by eight and ORs the next byte in, so the two's-complement bit pattern of
                // a signed element comes out right without a sign-extension step.
                Temporary acc = MakeTemp(felem);
                Temporary hi = MakeTemp(DataType.UINT8);
                Emit(new ArrayLoadFlash(qualified, ByteOffset(fsize - 1), hi));
                Emit(new Copy(hi, acc));
                for (int b = fsize - 2; b >= 0; b--)
                {
                    Temporary shifted = MakeTemp(felem);
                    Emit(new Binary(BinaryOp.LShift, acc, new Constant(8), shifted));
                    Temporary lo = MakeTemp(DataType.UINT8);
                    Emit(new ArrayLoadFlash(qualified, ByteOffset(b), lo));
                    Temporary widened = MakeTemp(felem);
                    Emit(new Copy(lo, widened));
                    Temporary merged = MakeTemp(felem);
                    Emit(new Binary(BinaryOp.BitOr, shifted, widened, merged));
                    acc = merged;
                }
                return acc;
    }

    private Val VisitIndex(IndexExpr expr)
    {
        // RFC 0009: a live Optional as the subscript target or the index
        // dispatches on its tag -- the None member raises the TypeError CPython
        // raises for that side.
        if (TryEmitGuardedIndex(expr) is { } guardedIndex)
            return guardedIndex;

        // `g[y][x]` on a compile-time 2-D grid: the flat load at g[y*W + x],
        // the same arithmetic the hand-flattened spelling emits. A single index
        // `g[y]` names a ROW -- a view, not a value -- and only `g[y][x]`,
        // `len(g[y])` and `for x in g[y]` may use it.
        if (expr.Target is IndexExpr gIn && ResolveGridKey(gIn.Target) is { } nestGrid)
        {
            if (gIn.Index is SliceExpr)
                throw UserError(
                    "a 2-D grid cannot be sliced -- g[a:b] would have to be a window of " +
                    "rows, and rows are views, not values. Write the loop.", gIn.Index);
            if (gIn.Index is TupleExpr)
                throw UserError(TwoIndexSubscriptRefusal, gIn.Index);
            if (expr.Index is SliceExpr)
                throw UserError(
                    "a row of a 2-D grid cannot be sliced -- a slice would be a view object. " +
                    "Index an element (g[y][x]) or iterate the row (for x in g[y]).",
                    expr.Index);
            if (expr.Index is TupleExpr)
                throw UserError(TwoIndexSubscriptRefusal, expr.Index);
            return EmitGridElemLoad(nestGrid, gIn.Index, expr.Index);
        }

        // `r[x]` where `r` is bound to a row by `r = g[y]` or `for row in g`.
        if (expr.Target is VariableExpr rowVe && ResolveRowRef(rowVe) is { } rowRef)
        {
            if (expr.Index is SliceExpr)
                throw UserError(
                    "a row of a 2-D grid cannot be sliced -- a slice would be a view object. " +
                    "Index an element (r[x]) or iterate the row (for x in r).", expr.Index);
            if (expr.Index is TupleExpr)
                throw UserError(TwoIndexSubscriptRefusal, expr.Index);
            if (rowRef.Uses != null && !rowRef.Uses.Contains(expr))
                throw RowAliasNotAValue(expr);
            return EmitRowElemLoad(rowRef, expr.Index);
        }

        // `g[y]` / `g[a:b]` read bare: the row is a view with no value.
        if (ResolveGridKey(expr.Target) is { } singleGrid)
        {
            if (expr.Index is SliceExpr)
                throw UserError(
                    "a 2-D grid cannot be sliced -- g[a:b] would have to be a window of " +
                    "rows, and rows are views, not values. Write the loop.", expr.Index);
            if (expr.Index is TupleExpr)
                throw UserError(TwoIndexSubscriptRefusal, expr.Index);
            throw UserError(
                "g[y] names a row of a 2-D grid -- a view into the flat array, not a " +
                "list value. Index an element (g[y][x]), take its width (len(g[y])), " +
                "or iterate it (for x in g[y]); there is no row object to read, pass, " +
                "return or store.", expr);
        }

        // `sys.implementation.version[i]` (neopixel.py's `version[0] >= 7` feature-detect).
        // RFC 0007 folds this chain through CompileTimeEvaluator in `if`/`match`/`try`
        // conditions, but a condition the frontend cannot finish -- `version[0] >= 7 and
        // getattr(board, "NEOPIXEL", None) == pin` keeps its runtime half -- survives to
        // here, where the compat layer's sys.py carries no `version` field at all. Same
        // syntactic match, same table: the shim file's tuple is a placeholder for IDEs,
        // and the compiler substitutes the real answer (docs/rfcs/0007 sections 0.1, 4.2).
        if (expr.Target is MemberAccessExpr { Member: "version", Object: MemberAccessExpr { Member: "implementation" } verImplObj }
            && IsModuleAlias(verImplObj.Object, "sys", "usys"))
        {
            if (!IntrospectionTable.IsKnownStdlib(deviceConfig.Stdlib))
                throw UserError(
                    "'sys.implementation.version' is answered by the compat layer: this project "
                    + "has no CircuitPython/MicroPython stdlib layer selected (stdlib = [...] "
                    + "in pyproject.toml), so 'sys' is not the shim that carries it", expr);
            if (expr.Index is not IntegerLiteral versionIdx)
                throw UserError(
                    "'sys.implementation.version' is a compile-time (major, minor, micro) tuple -- "
                    + "index it with an integer literal (sys.implementation.version[0])", expr);
            var (vMajor, vMinor, vMicro) = IntrospectionTable.ImplementationVersion(deviceConfig);
            return new Constant(versionIdx.Value switch
            {
                0 => vMajor,
                1 => vMinor,
                2 => vMicro,
                _ => throw UserError(
                    "'sys.implementation.version' has 3 elements: [0] major, [1] minor, [2] micro",
                    expr),
            });
        }

        // `sys.version_info[i]` -- the Python language version upstream reports
        // ((3, 4, 0) on both flavors). Same shape as sys.implementation.version[i]:
        // only the indexed read folds; the bare tuple attribute cannot materialize
        // honestly under pymcuc and refuses through member resolution.
        if (expr.Target is MemberAccessExpr { Member: "version_info" } viTargetObj
            && IsModuleAlias(viTargetObj.Object, "sys", "usys"))
        {
            if (!IntrospectionTable.IsKnownStdlib(deviceConfig.Stdlib))
                throw UserError(
                    "'sys.version_info' is answered by the compat layer: this project "
                    + "has no CircuitPython/MicroPython stdlib layer selected (stdlib = [...] "
                    + "in pyproject.toml), so 'sys' is not the shim that carries it", expr);
            if (expr.Index is not IntegerLiteral viIdx)
                throw UserError(
                    "'sys.version_info' is a compile-time (major, minor, micro) tuple -- "
                    + "index it with an integer literal (sys.version_info[0])", expr);
            var (viMajor, viMinor, viMicro) = IntrospectionTable.SysVersionInfo(deviceConfig);
            return new Constant(viIdx.Value switch
            {
                0 => viMajor,
                1 => viMinor,
                2 => viMicro,
                _ => throw UserError(
                    "'sys.version_info' has 3 elements: [0] major, [1] minor, [2] micro",
                    expr),
            });
        }

        // docs/rfcs/0004-arena-allocator.md: `buf[i]` on an arena-allocated runtime-sized
        // bytearray -- see the matching write-side comment in Assign.cs EmitIndexAssign.
        if (expr.Target is VariableExpr arenaReadVe && TryResolveArenaBuffer(arenaReadVe.Name, out _))
        {
            string arenaMod = ResolveArenaModuleAlias(expr);
            return VisitExpression(new CallExpr(
                new MemberAccessExpr(new VariableExpr(arenaMod), "read8"),
                new List<Expression> { new BinaryExpr(arenaReadVe, AstBinOp.Add, expr.Index) }));
        }

        // PyMCU#418: the FIELD form, `self.buf[i]` / `d.buf[i]` -- see
        // TryResolveArenaBufferField and the matching write-side hook in Assign.cs
        // EmitIndexAssign. The field's value is read into an ordinary local first, for
        // the same reason the write side does: passed straight through as read8's
        // argument, an inline parameter bound to a member-access expression re-resolves
        // it INSIDE read8's own expansion, where _arena's resolution silently breaks
        // (PyMCU#415's symptom) even one level deep.
        if (expr.Target is MemberAccessExpr arenaReadMem
            && TryResolveArenaBufferField(arenaReadMem.Object, arenaReadMem.Member, out _))
        {
            string tempOff = $"__arena_field_off_{arenaFieldTempId++}";
            VisitStatement(new VarDecl(tempOff, "uint16", arenaReadMem) { Line = expr.Line });
            string arenaMod = ResolveArenaModuleAlias(expr);
            return VisitExpression(new CallExpr(
                new MemberAccessExpr(new VariableExpr(arenaMod), "read8"),
                new List<Expression> { new BinaryExpr(new VariableExpr(tempOff), AstBinOp.Add, expr.Index) }));
        }

        // `memoryview(buf)[k]`/`[a:b]`: a slice is a writable window of the
        // buffer (ssd1306's `memoryview(self.buffer)[1:]`). A single index
        // is the argument's own subscript (PyMCU#361).
        if (expr.Target is CallExpr { Callee: VariableExpr { Name: "memoryview" } } mvCall
            && mvCall.Args.Count == 1)
        {
            if (expr.Index is SliceExpr)
                return EmitMemoryviewSliceView(mvCall.Args[0], expr);
            return VisitIndex(new IndexExpr(mvCall.Args[0], expr.Index)
                { Line = expr.Line, Column = expr.Column });
        }

        // `struct.unpack(fmt, buf)[k]` / `struct.unpack_from(fmt, buf, off)[k]`. This is
        // the ONLY place the subscript and the call are visible together, and the pair is
        // the whole supported shape: indexed on the spot, so the tuple that CPython would
        // build never exists. A bare unpack() is refused in VisitCall, which cannot see
        // whether it was indexed.
        if (expr.Target is CallExpr unpackCall
            && (IsStructCall(unpackCall, "unpack_from") || IsStructCall(unpackCall, "unpack")))
            return EmitStructUnpackFromIndexed(unpackCall, expr.Index);

        // RFC 0008: `f.read(n)[k]` -- the read mints a view over the blob and the
        // subscript reads one byte of it, a constant fold while the position is
        // compile-time and an ArrayLoadFlash once a runtime seek moved it.
        if (expr.Target is CallExpr romReadCall && TryRomfsReadCall(romReadCall, out var romReadH))
            return VisitIndex(new IndexExpr(
                new VariableExpr(EmitRomfsReadView(romReadH, romReadCall)), expr.Index)
                { Line = expr.Line, Column = expr.Column, Length = expr.Length });

        // `os.stat("name")[6]` / `os.listdir(dir)[k]` on the embedded-file table: the
        // result is a compile-time sequence, so a constant subscript picks its element.
        if (expr.Target is CallExpr romSeqCall
            && (IsOsFsCall(romSeqCall, "stat") || IsOsFsCall(romSeqCall, "listdir")))
        {
            Val seqMark = IsOsFsCall(romSeqCall, "stat")
                ? EmitOsStat(romSeqCall) : EmitOsListdir(romSeqCall);
            var seqElems = constSequenceBindings[((Variable)seqMark).Name];
            int seqIdx;
            try { seqIdx = EvaluateConstantExpr(expr.Index); }
            catch (Common.CompilerError)
            {
                throw UserError(
                    "the index into a compile-time sequence must be a compile-time constant",
                    expr.Index);
            }
            if (seqIdx < 0 || seqIdx >= seqElems.Count)
                throw new IndexError($"index {seqIdx} out of range for {seqElems.Count} elements",
                    expr.Line > 0 ? expr.Line : lastLine, expr.Column);
            return VisitExpression(seqElems[seqIdx]);
        }

        // A name (or field) bound to a read view -- `hdr = f.read(2)`, then `hdr[k]`:
        // the view stays a compile-time window over the blob, never a copied buffer.
        if (expr.Target is VariableExpr or MemberAccessExpr
            && SequenceKeyOf(expr.Target) is { } romViewKey
            && romfsViews.TryGetValue(romViewKey, out var romView))
            return EmitRomfsViewIndex(romView, expr.Index);

        // `self.measurements[0]`: a @property is a call. Visiting the member first
        // used it as a scalar and refused "'measurements' returns 2 values". The
        // getter is the same f()[k] site as a written call (adafruit_sht4x).
        if (expr.Target is MemberAccessExpr propIx && IsPropertyGetterRead(propIx))
            expr = new IndexExpr(
                new CallExpr(propIx, new List<Expression>()) { Line = propIx.Line },
                expr.Index)
            { Line = expr.Line, Column = expr.Column, Length = expr.Length };

        // `f()[k]`: the subscript and the call are only visible together here.
        // The sentinel tells the expansion this site wants the tuple's slots, so
        // a multi-value return lands in them and the subscript picks element k --
        // `self._temperature_and_lux_dn40()[0]` in adafruit_tcs34725. A call
        // whose result is a buffer, an instance or a scalar keeps its old meaning.
        //
        // `d.pair[k]` is the same site when `pair` is a descriptor: __get__ is the
        // call, and a `return struct.unpack_from(...)` in it fills the slots just
        // the same (adafruit_register's Struct, read per-element).
        Expression? tupleSrc = expr.Target switch
        {
            CallExpr tc => tc,
            MemberAccessExpr tm when IsDescriptorMemberRead(tm) => tm,
            _ => null,
        };
        if (tupleSrc != null)
        {
            lastTupleResults.Clear();
            pendingTupleCount = -1;
            Val callResult = VisitExpression(tupleSrc);
            pendingTupleCount = 0;

            if (lastTupleResults.Count > 0)
            {
                Val idxVal = VisitExpression(expr.Index);
                if (idxVal is not Constant tc)
                    throw UserError("the index into a tuple return must be a compile-time "
                                    + "constant -- each element is its own slot", expr.Index);
                if (tc.Value < 0 || tc.Value >= lastTupleResults.Count)
                    throw new IndexError($"tuple index {tc.Value} out of range for "
                                         + $"{lastTupleResults.Count} elements",
                                         expr.Line > 0 ? expr.Line : lastLine, expr.Column);
                string elem = lastTupleResults[tc.Value];
                return new Variable(elem, variableTypes.TryGetValue(elem, out var et)
                    ? et
                    : constantVariables.TryGetValue(elem, out int ec)
                        ? WidestElemType(new List<int> { ec }) : DataType.UINT8);
            }

            // A returned buffer names the callee's fixed slot array, so the
            // subscript reads it the way a named array's is read. Through a
            // property the result arrives as the expansion's ResultTemp -- a
            // name aliased to the member array (`sr.gpio[0]` on
            // adafruit_74hc595 bit-tested the alias instead).
            string? retVarName = callResult switch
            {
                Variable rv => rv.Name,
                Temporary rt => rt.Name,
                _ => null,
            };
            if (retVarName != null
                && TryResolveArrayStorageKey(FollowAliases(retVarName), out var retKey)
                && arraySizes.TryGetValue(retKey, out int retSize))
            {
                Val idxVal = VisitExpression(expr.Index);
                DataType retElemDt = arrayElemTypes.TryGetValue(retKey, out var redt)
                    ? redt : DataType.UINT8;
                if (arraysWithVariableIndex.Contains(retKey)
                    || moduleSramArrays.Contains(retKey))
                {
                    Temporary retTmp = MakeTemp(retElemDt);
                    Emit(new ArrayLoad(retKey, idxVal, retTmp, retElemDt, retSize));
                    return retTmp;
                }
                if (idxVal is not Constant ci)
                    throw UnrolledArrayIndexError(retKey, expr.Target);
                if (ci.Value < 0 || ci.Value >= retSize)
                    throw new IndexError($"array index {ci.Value} out of range for size {retSize}",
                                         expr.Line > 0 ? expr.Line : lastLine, expr.Column);
                string elemSlot = retKey + "__" + ci.Value;
                return new Variable(elemSlot, variableTypes.TryGetValue(elemSlot, out var esd)
                    ? esd : retElemDt);
            }

            // An instance result is subscripted through its __getitem__, the same
            // path `v[1]` takes when v names the instance.
            if (callResult is Variable or Temporary
                && GetValClass(callResult) is { Length: > 0 } callCls
                && inlineFunctions.ContainsKey(callCls + "___getitem__"))
            {
                string callSelf = callResult is Variable cv ? cv.Name : ((Temporary)callResult).Name;
                if (expr.Index is TupleExpr keyTup)
                    return EmitDunderCall(callSelf, callCls, callCls + "___getitem__",
                        new List<Val> { new NoneVal() },
                        new Dictionary<int, ListExpr> { { 0, new ListExpr(keyTup.Elements) } });
                Val getIdx = VisitExpression(expr.Index);
                return EmitDunderCall(callSelf, callCls, callCls + "___getitem__",
                    new List<Val> { getIdx });
            }

            // A scalar result indexed was a bit test of the call's value -- the
            // meaning it had when the subscript fell through to the bit path.
            if (callResult is NoneVal)
                throw UserError("this call does not produce an indexable value", expr);
            Val bitIdx = VisitExpression(expr.Index);
            if (bitIdx is not Constant bc)
                throw UserError("Bit index must be constant for reading", expr.Index);
            Temporary bitDst = MakeTemp();
            Emit(new BitCheck(callResult, bc.Value, bitDst));
            return bitDst;
        }

        // d[k] on a dict-literal binding: a compile-time CLOSED lookup table. A constant
        // key folds to its value; a runtime key lowers to a compare chain that raises
        // KeyError when nothing matches. Must run before the string-subscript rejection
        // below (string keys are legal on dicts).
        if (expr.Target is VariableExpr dictVe && TryGetDictBinding(dictVe.Name, out var dictLit))
            return EmitDictLookup(dictLit, expr.Index);

        // `self.digits[num]`: the same lookup table reached through a field.
        if (expr.Target is MemberAccessExpr && TryGetDictFor(expr.Target, out var fieldDictLit))
            return EmitDictLookup(fieldDictLit, expr.Index);

        // `D[k][j]`: one element of a rectangular dict, both subscripts written together. The
        // row is never a value here, so this is the only place the pair is visible; taken apart
        // (`row = D[k]` then `row[j]`) it is the row-view path in the assignment. Without it the
        // inner lookup tried to evaluate a list in a value position and said so.
        if (expr.Target is IndexExpr rowChain && TryGetDictFor(rowChain.Target, out var chainDict)
            && DictKeyedRows(chainDict) is { } chainRows)
        {
            Val rowIdxVal = VisitExpression(rowChain.Index);
            Val colIdxVal = VisitExpression(expr.Index);
            int width = chainRows[0].Row.Count;
            string chainSource = rowChain.Target is MemberAccessExpr cm ? cm.Member
                               : (rowChain.Target is VariableExpr cv ? cv.Name : "table");
            string chainCache = SequenceKeyOf(rowChain.Target) ?? chainSource;

            if (rowIdxVal is Constant rc && colIdxVal is Constant cc)
            {
                int at = chainRows.FindIndex(r => r.Key == rc.Value);
                if (at < 0)
                    throw UserError($"KeyError: {DescribeDictKey(rowChain.Index, rc)} is not a key "
                                    + "of this dict literal (checked at compile time)", rowChain.Index);
                if (cc.Value < 0 || cc.Value >= width)
                    throw new IndexError($"array index {cc.Value} out of range for size {width}",
                                         expr.Line > 0 ? expr.Line : lastLine, expr.Column);
                return new Constant(chainRows[at].Row[cc.Value]);
            }

            if (MaterialiseDictRows("dictrows:" + chainCache, chainSource,
                                    chainRows.Select(r => r.Row).ToList()) is { } chainTable)
            {
                Val rowIndex = rowIdxVal is Constant
                    ? new Constant(chainRows.FindIndex(r => r.Key == ((Constant)rowIdxVal).Value))
                    : EmitDictRowIndex(chainRows, chainCache, chainSource, rowIdxVal);
                Temporary scaled = MakeTemp(DataType.UINT16);
                Emit(new Binary(BinaryOp.Mul, rowIndex, new Constant(width), scaled));
                Temporary sum = MakeTemp(DataType.UINT16);
                Emit(new Binary(BinaryOp.Add, scaled, colIdxVal, sum));
                return EmitFlashArrayRead(chainTable, sum, chainRows.Count * width);
            }
        }

        // `bins[b][0]` on a list[list[T]]: the inner subscript loads the inner
        // list's heap pointer (a GC_REF) out of the outer payload, and the outer
        // subscript then reads an element of THAT list. Without this branch the
        // outer index fell through to the register-bit path at the bottom and
        // read a bit of the pointer's low byte -- bins[0][0] answered 1 where
        // the bin held [562, 0] (adafruit_irremote's bin_data). The GC_REF temp
        // the inner load produces is registered as a list var so a bound name
        // (`inner = bins[b]`, `for kb in bins`) and `len()` resolve the same way.
        if (expr.Target is IndexExpr innerSub
            && innerSub.Target is VariableExpr innerVe
            && ResolveListVarQualified(innerVe.Name) is { Length: > 0 } innerListQ
            && listVarElemTypes[innerListQ] == DataType.GC_REF
            && listInnerElemTypes.TryGetValue(innerListQ, out var innerElemDt))
        {
            Val outerPtr = new Variable(innerListQ, DataType.GC_REF);
            Val innerIdxVal = VisitExpression(innerSub.Index);
            Temporary innerAddr = EmitElemAddr(outerPtr, innerIdxVal, DataType.GC_REF.SizeOf());
            Temporary innerRef = MakeTemp(DataType.GC_REF);
            Emit(new LoadIndirect(innerAddr, innerRef, DataType.GC_REF));
            listVarElemTypes[innerRef.Name] = innerElemDt;
            listInnerElemTypes.Remove(innerRef.Name);
            Val idxVal = VisitExpression(expr.Index);
            Temporary elemAddr = EmitElemAddr(innerRef, idxVal, innerElemDt.SizeOf());
            Temporary result = MakeTemp(innerElemDt);
            Emit(new LoadIndirect(elemAddr, result, innerElemDt));
            return result;
        }

        // `m[x, y]` on a target whose class cannot take the pair (#352). Refused HERE rather
        // than in the readers, which cannot know the class, and before the index is visited so
        // the generic "tuples are not supported as runtime values" refusal, which is true and
        // about a different program, never claims it. The caret goes on the Tuple, which both
        // front ends stamp at its first element.
        if (expr.Index is TupleExpr && !SubscriptTakesAPair(expr.Target, "__getitem__"))
            throw UserError(TwoIndexSubscriptRefusal, expr.Index);

        // A string subscript is a mistake — a single-char string would otherwise fold to
        // its code point and be used as a (wrong) integer index, e.g. a["k"] -> a[107].
        if (expr.Index is StringLiteral)
            throw UserError("array index must be an integer, not a string", expr.Index);

        if (expr.Index is SliceExpr sl)
        {
            // `stnum[:dot]`: a slice of a compile-time string is itself a compile-time
            // string -- the answer is text the compiler already holds, so it folds to a
            // new interned constant and no string object is ever built (adafruit_ht16k33's
            // _number pads and cuts stnum this way).
            if (StaticStringOf(expr.Target) is { } strSliceSrc)
            {
                if (!TrySliceStaticText(strSliceSrc, sl, out var strSliced))
                    throw UserError(
                        "a slice of a compile-time string needs compile-time bounds: the "
                        + "result's length is decided while compiling, and there is no "
                        + "allocator to size one at run time.", expr.Index);
                return InternConstString(strSliced);
            }

            // `self._buffer[0:2]`: a field bytearray is the same named array as `buf[0:2]`.
            // The slice path only accepted a VariableExpr, so adafruit_sht4x's
            // `temp_data = self._buffer[0:2]` was refused after measurements[0] compiled.
            if (expr.Target is MemberAccessExpr fieldSlice
                && ResolveMemberArrayName(fieldSlice) is string fieldArr)
                expr = new IndexExpr(
                    new VariableExpr(fieldArr) { Line = fieldSlice.Line },
                    expr.Index)
                { Line = expr.Line, Column = expr.Column, Length = expr.Length };

            if (expr.Target is VariableExpr srcVe)
            {
                // ResolveNameKey walks the same scopes every other lookup does -- an array
                // built inside an @inline expansion lives under the inline prefix, which a
                // currentFunction-only probe misses (PyMCU#361).
                string srcQ = ResolveNameKey(srcVe.Name);
                if (!arraySizes.ContainsKey(srcQ) && arraySizes.ContainsKey(srcVe.Name)) srcQ = srcVe.Name;
                if (arraySizes.TryGetValue(srcQ, out int srcSize))
                {
                    DataType elemDt = arrayElemTypes[srcQ];
                    // The bounds decide the materialized copy's size -- a compile-time
                    // question, so locals fold here exactly as the range unroller's trip
                    // count does (#326). `buf = self._buffer[i*17 : i*17 + 17]` inside a
                    // compile-time unrolled enumerate (adafruit_ht16k33's show()) is the
                    // demandant: `i` is the unroll's constant and the offset local it
                    // feeds sits in localConstantValues.
                    int start, stop, step;
                    {
                        bool savedFoldLocals = foldLocalConstants;
                        foldLocalConstants = true;
                        try
                        {
                            start = sl.Start != null ? EvaluateConstantExpr(sl.Start) : 0;
                            stop = sl.Stop != null ? EvaluateConstantExpr(sl.Stop) : srcSize;
                            step = sl.Step != null ? EvaluateConstantExpr(sl.Step) : 1;
                        }
                        finally { foldLocalConstants = savedFoldLocals; }
                    }
                    if (step == 0) throw UserError("Slice step cannot be zero", expr.Index);
                    if (start < 0) start += srcSize;
                    if (stop < 0) stop += srcSize;
                    start = Math.Max(0, Math.Min(start, srcSize));
                    stop = Math.Max(0, Math.Min(stop, srcSize));
                    int resultCount = 0;
                    for (int i = start; step > 0 ? i < stop : i > stop; i += step) ++resultCount;

                    // Inside an inline expansion the scratch array keeps its ordinary
                    // `__slice_{n}` spelling and joins canonicalTemps under the frame's
                    // "d{depth}_slice{k}" key, so every expansion of a
                    // `writeto(buf[i:i+n])` body shares ONE slice slot (allocator
                    // canonical-merge) instead of each site minting its own copy of
                    // the backing bytes.
                    string tmpName = "__slice_" + tempCounter++;
                    if (inlineStack.Count > 0 && currentInlinePrefix.Length > 0)
                        canonicalTemps[tmpName] = $"d{inlineDepth}_slice{inlineStack[^1].TempNext++}";
                    arraySizes[tmpName] = resultCount;
                    arrayElemTypes[tmpName] = elemDt;
                    variableTypes[tmpName] = elemDt;
                    bool srcSram = arraysWithVariableIndex.Contains(srcQ) || moduleSramArrays.Contains(srcQ);
                    // A slice of a bytearray IS a bytearray, and a bytearray is what a
                    // caller can take the address of -- `i2c_dev.write(buf[a:b])` in
                    // adafruit_ht16k33's show() marshals ArrayBase. The flat
                    // `tmp__0`, `tmp__1` element form has no base to take, so a slice of
                    // contiguous storage gets contiguous storage of its own.
                    if (srcSram) arraysWithVariableIndex.Add(tmpName);
                    int k = 0;
                    for (int i = start; step > 0 ? i < stop : i > stop; i += step, ++k)
                    {
                        Val srcVal;
                        if (srcSram)
                        {
                            Temporary tmp = MakeTemp(elemDt);
                            Emit(new ArrayLoad(srcQ, new Constant(i), tmp, elemDt, srcSize));
                            srcVal = tmp;
                        }
                        else
                        {
                            srcVal = new Variable(srcQ + "__" + i, elemDt);
                        }

                        if (srcSram)
                        {
                            Emit(new ArrayStore(tmpName, new Constant(k), srcVal, elemDt, resultCount));
                        }
                        else
                        {
                            string dstElem = tmpName + "__" + k;
                            variableTypes[dstElem] = elemDt;
                            Emit(new Copy(srcVal, new Variable(dstElem, elemDt)));
                        }
                    }

                    return new Variable(tmpName, elemDt);
                }

                // `pulses[1:end:2]` on a runtime heap list: a fresh list object holding
                // every step-th element of [start, stop). Unmodified adafruit_irremote
                // bins a captured frame this way in decode_bits.
                if (ResolveListVarQualified(srcVe.Name) is { Length: > 0 } sliceSrcKey)
                    return EmitRuntimeListSlice(sliceSrcKey, srcVe, sl, expr);
            }

            // An OBJECT with __getitem__ is not a fixed-size array, and telling its author about
            // fixed-size arrays describes neither the program they wrote nor the one they
            // should write (#329). `nvm[0:4]` is a sequence protocol, and what this target
            // cannot do is produce the new sequence a slice READ stands for -- there is nowhere
            // to put it. Writing through the slice works and is named, because the elements go
            // one at a time into storage that already exists.
            if (SliceTargetClass(expr.Target) is { } sliceCls
                && inlineFunctions.ContainsKey(sliceCls + "_" + "__getitem__"))
                throw UserError(
                    $"a slice READ of '{sliceCls}' is not supported: a slice makes a new sequence, "
                    + "and there is no storage to make it in. Read the elements one at a time "
                    + "(`obj[i]`), loop over them (`for v in obj:`), or copy them into a buffer "
                    + "you own. Writing through a slice (`obj[a:b] = ...`) is supported and "
                    + "unrolls to one __setitem__ per element.", expr.Target);

            throw UserError("Slice indexing is only supported on named fixed-size arrays", expr.Target);
        }

        // `self._pins[0]`: an element of a compile-time sequence of instances held in a FIELD.
        // The elements have no run-time storage -- each is its own flattened instance -- so the
        // subscript names one of them rather than loading from anywhere.
        if (expr.Target is MemberAccessExpr
            && TryResolveInstanceSequence(expr.Target, out string seqBase, out int seqCount))
        {
            Val seqIdxVal = VisitExpression(expr.Index);
            if (seqIdxVal is not Constant seqIdxConst)
                throw UserError(
                    $"'{FormatMemberTarget((MemberAccessExpr)expr.Target)}' holds {seqCount} "
                    + "compile-time instances, which have no run-time storage to index. Use a "
                    + "constant index, or walk them with `for p in "
                    + $"{FormatMemberTarget((MemberAccessExpr)expr.Target)}:`.", expr.Index);
            int seqIdx = seqIdxConst.Value;
            if (seqIdx < 0) seqIdx += seqCount;
            if (seqIdx < 0 || seqIdx >= seqCount)
                throw new IndexError(
                    $"array index {seqIdxConst.Value} out of range for size {seqCount}",
                    expr.Line > 0 ? expr.Line : lastLine, expr.Column);
            return new Variable(seqBase + "__" + seqIdx, DataType.UINT8);
        }

        // `self.pin_mapping.analog_pins[0]`: a class-object field's sequence attribute.
        // A constant index folds per candidate and the tag selects; a run-time index would
        // need every candidate's tuple in storage, which is refused.
        if (expr.Target is MemberAccessExpr coSubOuter
            && coSubOuter.Object is MemberAccessExpr coSubInner
            && ClassObjectFieldClasses(coSubInner) is { } coSubCands)
        {
            var subElems = new List<List<Frontend.Expression>>(coSubCands.Count);
            foreach (var cand in coSubCands)
            {
                if (!constSequenceBindings.TryGetValue(ClassAttrKey(cand, coSubOuter.Member),
                                                       out var se))
                    throw UserError(
                        $"field '{coSubInner.Member}' can hold class {ShortClassName(cand)}, which "
                        + $"has no compile-time sequence attribute '{coSubOuter.Member}' to index",
                        expr.Target);
                subElems.Add(se);
            }
            if (VisitExpression(expr.Index) is not Constant coIdxC)
                throw UserError(
                    $"indexing field '{coSubInner.Member}' attribute '{coSubOuter.Member}' at run "
                    + "time would need every candidate class's tuple in storage -- use a "
                    + "constant index, `in` or `.index()`", expr.Index);
            int coIdx = coIdxC.Value;
            if (coIdx < 0)
                throw UserError(
                    "a negative index into a class-object field's attribute cannot fold -- "
                    + "candidates' tuples differ in length, so there is no one answer",
                    expr.Index);
            var subVals = new List<Val>(coSubCands.Count);
            for (int k = 0; k < coSubCands.Count; k++)
            {
                if (coIdx >= subElems[k].Count)
                    throw UserError(
                        $"index {coIdx} is past the end of attribute '{coSubOuter.Member}' on "
                        + $"class {ShortClassName(coSubCands[k])} ({subElems[k].Count} elements) "
                        + $"-- field '{coSubInner.Member}' can hold it", expr.Index);
                subVals.Add(VisitExpression(subElems[k][coIdx]));
            }
            return EmitClassObjectSelect(coSubInner, coSubCands, subVals);
        }

        // `m.f[i]` where the field holds a heap list or tuple: the field slot
        // is a GC_REF to the object, so the subscript lowers exactly like a
        // named list's -- element at header offset 2 + i*elemSize. The field
        // answers under its flattened `<obj>_<member>` key when the store wrote
        // one; a single-field class collapses `m.f` onto the binding `m` itself
        // was given, so the name the access EVALUATES to is asked for too --
        // gated to a plain field of a resolved class so no property getter or
        // descriptor call is replayed by the probe.
        if (expr.Target is MemberAccessExpr { Object: VariableExpr lov } memList)
        {
            string memKey = ResolveNameKey(lov.Name) + "_" + memList.Member;
            Val? memPtr = null;
            DataType memElemDt = DataType.UNKNOWN;
            if (listVarElemTypes.TryGetValue(memKey, out var flatElemDt))
            {
                memPtr = new Variable(memKey, DataType.GC_REF);
                memElemDt = flatElemDt;
            }
            else if (InstanceClassOfName(lov.Name) is { } memCls
                     && classFieldLayout.TryGetValue(memCls, out var memLay)
                     && memLay.Any(f => f.Field == memList.Member)
                     && !IsPropertyGetterRead(memList) && !IsDescriptorMemberRead(memList)
                     && VisitExpression(expr.Target) is { } memVal)
            {
                string? memValName = memVal switch
                { Variable mv => mv.Name, Temporary mt => mt.Name, _ => null };
                if (memValName != null && listVarElemTypes.TryGetValue(memValName, out var mvElemDt))
                {
                    memPtr = memVal;
                    memKey = memValName;
                    memElemDt = mvElemDt;
                }
            }
            if (memPtr != null)
            {
                if (memElemDt == DataType.UNKNOWN)
                    throw UserError(
                        $"cannot infer the element type of '{memList.Member}' yet; "
                        + "its first append must precede reads, or declare it "
                        + "like `x: list[uint8] = []`", expr);
                Val memIdx = VisitExpression(expr.Index);
                Temporary memAddr = EmitElemAddr(memPtr, memIdx, memElemDt.SizeOf());
                Temporary memRes = MakeTemp(memElemDt);
                Emit(new LoadIndirect(memAddr, memRes, memElemDt));
                if (memElemDt == DataType.GC_REF
                    && listInnerElemTypes.TryGetValue(memKey, out var memInnerDt))
                    listVarElemTypes[memRes.Name] = memInnerDt;
                return memRes;
            }
        }

        // `self._levels[0]`: an element of a list of NUMBERS held in a field. The elements are
        // compile-time values, so a constant subscript folds to one of them.
        if (expr.Target is MemberAccessExpr constSeqMem
            && ResolveMemberArrayName(constSeqMem) is null
            && ResolveConstSequenceExpr(expr.Target) is { } constSeqElems)
        {
            Val cseqIdxVal = VisitExpression(expr.Index);
            // A table held in a field and read with a run-time index: the values are constants
            // and nothing writes them, so they go to flash here, at the subscript that needs it.
            if (cseqIdxVal is not Constant
                && ConstValuesOf(constSeqElems) is { } cseqValues
                && SequenceKeyOf(expr.Target) is { } cseqKey
                && TryMaterialiseConstTableFromValues(cseqKey, constSeqMem.Member, cseqValues)
                    is { } cseqTable)
                return EmitFlashArrayRead(cseqTable, cseqIdxVal, cseqValues.Count);
            if (cseqIdxVal is not Constant cseqIdxConst)
                throw UserError(
                    $"'{FormatMemberTarget(constSeqMem)}' holds {constSeqElems.Count} compile-time "
                    + "values with no storage behind them, so it cannot be indexed at run time. "
                    + $"Declare the field with its size (`{FormatMemberTarget(constSeqMem)}: "
                    + $"uint8[{constSeqElems.Count}] = [...]`) to get an array that is.", expr.Index);
            int cseqIdx = cseqIdxConst.Value;
            if (cseqIdx < 0) cseqIdx += constSeqElems.Count;
            if (cseqIdx < 0 || cseqIdx >= constSeqElems.Count)
                throw new IndexError(
                    $"array index {cseqIdxConst.Value} out of range for size {constSeqElems.Count}",
                    expr.Line > 0 ? expr.Line : lastLine, expr.Column);
            return VisitExpression(constSeqElems[cseqIdx]);
        }

        if (expr.Target is VariableExpr ve)
        {
            // Tuple/list/bytes literal bound to an inline parameter: fold a constant
            // subscript (param[0]) to the corresponding element expression. Mirrors
            // the for-in unroll path in Iteration.cs (same key + alias resolution);
            // enables e.g. NeoPixel.fill((r, g, b)) consumed as color[0..2].
            if (ResolveListLiteralParam(ve.Name) is ListExpr litArg)
            {
                int li;
                if (expr.Index is IntegerLiteral ilit) li = ilit.Value;
                else if (VisitExpression(expr.Index) is Constant clit) li = clit.Value;
                else if (ConstValuesOf(litArg.Elements) is { } litValues
                         && TryMaterialiseConstTableFromValues(
                                "param:" + ResolveNameKey(ve.Name), ve.Name, litValues)
                            is { } litTable)
                    return EmitFlashArrayRead(litTable, VisitExpression(expr.Index), litValues.Count);
                else throw UserError(
                    $"'{ve.Name}' holds {litArg.Elements.Count} compile-time values with no "
                    + "storage behind them, so it cannot be indexed at run time. Declare an "
                    + $"array and pass that (`table: uint8[{litArg.Elements.Count}] = [...]`), "
                    + $"or walk the values with `for v in {ve.Name}:`.", expr.Index);
                if (li < 0) li += litArg.Elements.Count;
                if (li < 0 || li >= litArg.Elements.Count)
                    throw UserError("Tuple/list parameter subscript index out of range", expr.Index);
                return VisitExpression(litArg.Elements[li]);
            }

            // `pins = [2, 3, 4]` then `pins[0]`: a name bound to an all-constant list keeps its
            // elements, and a CONSTANT subscript of it is one of them. It used to answer the
            // element VARIABLE instead, so the value could not feed a parameter declared
            // `const` -- `Pin(pins[0], Pin.OUT)` was refused while `for p in pins: Pin(p, ...)`
            // compiled, on the same list and the same parameter. Gated like the flash table of
            // PyMCU#317: a name the program writes keeps reading its storage.
            if (ResolveConstSequence(ve.Name) is { } nameSeq)
            {
                Val seqIdxVal = expr.Index is IntegerLiteral nlit0
                    ? new Constant(nlit0.Value)
                    : VisitExpression(expr.Index);
                // A for-loop target reads as written once under the broad count, but for a
                // compile-time-unrolled loop that write IS the binding being resolved here
                // (`for p in [(1, 2), ...]` binds p to each pair) -- discount it, or p[k]
                // falls through to the register-bit path and emits a bit check on a slot
                // nothing ever wrote.
                if (seqIdxVal is Constant seqIdxConst
                    && nameWriteCounts.GetValueOrDefault(ve.Name)
                        <= (forLoopVarNames.Contains(ve.Name) ? 1 : 0))
                {
                    int ni = seqIdxConst.Value;
                    if (ni < 0) ni += nameSeq.Count;
                    if (ni < 0 || ni >= nameSeq.Count)
                        throw new IndexError(
                            $"array index {ni} out of range for size {nameSeq.Count}",
                            expr.Line > 0 ? expr.Line : lastLine, expr.Column);
                    return VisitExpression(nameSeq[ni]);
                }

                // A run-time index into a compile-time sequence reached by name or through a
                // parameter -- `pulses[i]` in pulseio's send loop, where `pulses` is the
                // caller's `signal = [...]` bound through stacked @inline hops (#258). The
                // values are constants with no storage of their own, so the read goes to a
                // materialised flash table, exactly like the literal-parameter path above
                // and the module-list path below. The gate asks about real STORES into the
                // source of the sequence (the terminal of the alias chain): `signal` being
                // handed to send() counts as a write under the broad count, and only a
                // `signal[i] = v` can contradict a flash table that has no SRAM slot.
                if (seqIdxVal is not Constant && nameStoreCounts.GetValueOrDefault(ve.Name) == 0)
                {
                    string seqWritten = TerminalAliasOf(ve.Name);
                    int seqDot = seqWritten.LastIndexOf('.');
                    if (seqDot >= 0) seqWritten = seqWritten[(seqDot + 1)..];
                    if (ConstValuesOf(nameSeq) is { } seqValues
                        && TryMaterialiseConstTableFromValues("seq:" + seqWritten, seqWritten,
                                                              seqValues, storeOnly: true)
                            is { } seqTable)
                        return EmitFlashArrayRead(seqTable, seqIdxVal, seqValues.Count);
                }
            }

            // A name the current function binds -- a parameter or a local -- shadows every
            // module-scope spelling the fallbacks below probe. Without the guard a `const[str]`
            // parameter `s` inside uart_write_str resolved `s[i]` to `main.s` and the callee
            // streamed the caller's global instead of its argument (probe 070; the store
            // path's ResolveArrayVar has applied the same rule since #458/#460). The local
            // mechanisms further down (the alias follow and the inline-prefix probe) still
            // run: they resolve what the local is bound TO, not another scope's name.
            bool localShadowsModule = LocalScopeBinds(ve.Name);

            // A name already carrying its full storage key -- a buffer returned through
            // nested inline expansions (`inline3._read.inline4._read_register.result`,
            // which is what a struct.unpack field expression holds) -- is used verbatim:
            // prefixing it again produces a name nothing registered. A BARE name filed in
            // arraySizes is the module array, which the local binding just checked shadows.
            string qualified = (arraySizes.ContainsKey(ve.Name) || bytearrayParams.Contains(ve.Name))
                && !localShadowsModule
                ? ve.Name
                : (string.IsNullOrEmpty(currentFunction) ? ve.Name : currentFunction + "." + ve.Name);
            string localSpelling = qualified;
            // Same module-scope rule as the store path: the module array's canonical
            // spelling is the bare name ScanGlobals filed (PyMCU#460).
            if (!arraySizes.ContainsKey(qualified) && !localShadowsModule)
                qualified = ModuleScopeArrayName(qualified);

            // `x = f()` where f returned its local buffer binds `x` as an alias of the
            // callee's slot; follow the alias so the subscript reaches that storage. The
            // follow is adopted only when the endpoint really is array storage -- a scalar
            // alias (a list-returning call's result temp, say) must keep the original name,
            // which is the key its own element-type record is filed under.
            if (!arraySizes.ContainsKey(qualified) && !bytearrayParams.Contains(qualified)
                && variableAliases.ContainsKey(qualified)
                && TryResolveArrayStorageKey(FollowAliases(qualified), out var aliasedArr))
                qualified = aliasedArr;

            // Inside an inline expansion, the target may be an aliased bytearray parameter.
            if (!arraySizes.ContainsKey(qualified) && !bytearrayParams.Contains(qualified)
                && !string.IsNullOrEmpty(currentInlinePrefix))
            {
                string inlineQ = currentInlinePrefix + ve.Name;
                if (variableAliases.TryGetValue(inlineQ, out string? resolvedQ) && resolvedQ != null)
                    qualified = resolvedQ;
                else if (arraySizes.ContainsKey(inlineQ) || bytearrayParams.Contains(inlineQ))
                    qualified = inlineQ;
            }

            // The alias for an inline parameter bound to a MODULE-level array resolves to the
            // function-qualified name ("main.gbuf") while a module array is registered bare
            // ("gbuf"), so the lookup missed and the subscript fell through to the register
            // bit path. With a run-time index that failed as "Bit index must be constant";
            // with a constant index it compiled SILENTLY into a bit test of the array's
            // ADDRESS. Same normalization the qualified/bare fallback above does -- and the
            // same shadowing rule: a still-unresolved local name never strips to the module's.
            if (!arraySizes.ContainsKey(qualified) && !bytearrayParams.Contains(qualified)
                && !(localShadowsModule && qualified == localSpelling))
            {
                int lastDot = qualified.LastIndexOf('.');
                if (lastDot >= 0)
                {
                    string bare = qualified[(lastDot + 1)..];
                    if (arraySizes.ContainsKey(bare) || bytearrayParams.Contains(bare))
                        qualified = bare;
                }
            }

            // An alias can land on a class attribute's canonical name (`Sensor__BUFFER`)
            // while the storage is filed under the module init (`main.Sensor__BUFFER`).
            if (!arraySizes.ContainsKey(qualified) && !bytearrayParams.Contains(qualified)
                && !(localShadowsModule && qualified == localSpelling)
                && TryResolveArrayStorageKey(qualified, out var storedKey))
                qualified = storedKey;

            // A module-level list written without an annotation is filed under the synthesized
            // main that runs the module's statements, so neither `<fn>.<name>` nor the bare name
            // finds it from an ordinary function, and the subscript fell through to the register
            // bit path: a lookup table read from an ordinary function was told its bit index was
            // not constant, about a program with no register in it. The prescan has the values.
            if (!arraySizes.ContainsKey(qualified) && !bytearrayParams.Contains(qualified)
                && !listVarElemTypes.ContainsKey(qualified) && !listVarElemTypes.ContainsKey(ve.Name)
                && !localShadowsModule
                && ModuleConstListValues(ve.Name) is { } modValues)
            {
                Val modIdx = VisitExpression(expr.Index);
                int modIdxConst = modIdx is Constant mc ? mc.Value : -1;
                if (modIdx is Constant)
                {
                    if (modIdxConst < 0) modIdxConst += modValues.Count;
                    if (modIdxConst < 0 || modIdxConst >= modValues.Count)
                        throw new IndexError(
                            $"array index {(modIdx as Constant)!.Value} out of range for size {modValues.Count}",
                            expr.Line > 0 ? expr.Line : lastLine, expr.Column);
                    return new Constant(modValues[modIdxConst]);
                }
                if (TryMaterialiseConstTableFromValues("module:" + ve.Name, ve.Name, modValues)
                        is { } modTable)
                    return EmitFlashArrayRead(modTable, modIdx, modValues.Count);
            }

                        // Bytearray parameter: the value stored is a pointer; use indirect indexed load.
            if (bytearrayParams.Contains(qualified))
            {
                Val idxVal = VisitExpression(expr.Index);
                Temporary tmp = MakeTemp(DataType.UINT8);
                Emit(new BytearrayLoad(qualified, idxVal, tmp));
                return tmp;
            }

            // list[T] indexing: x[i] → load element from GC heap list at offset 2 + i*elemSize
            {
                // ResolveNameKey walks the inline-prefix/function/module candidates and
                // follows the alias a bound parameter carries, so `xs[i]` inside an
                // expansion reads the caller's list, not a same-named dead slot.
                string resolvedList = ResolveNameKey(ve.Name);
                string listQ = listVarElemTypes.ContainsKey(resolvedList) ? resolvedList
                             : listVarElemTypes.ContainsKey(qualified) ? qualified
                             : !localShadowsModule && listVarElemTypes.ContainsKey(ve.Name) ? ve.Name
                             : "";
                if (!string.IsNullOrEmpty(listQ))
                {
                    DataType elemDt = listVarElemTypes[listQ];
                    // A promoted `x = []` has no element type until an append
                    // teaches it; a read before then would compute a 0-width
                    // element address. The program's own fix is the append
                    // order or an annotation.
                    if (elemDt == DataType.UNKNOWN)
                        throw UserError(
                            $"cannot infer the element type of '{ve.Name}' yet; " +
                            "its first append must precede reads, or declare it " +
                            "like `x: list[uint8] = []`", expr);
                    Val listPtr = new Variable(listQ, DataType.GC_REF);
                    Val idxVal = VisitExpression(expr.Index);
                    Temporary elemAddr = EmitElemAddr(listPtr, idxVal, elemDt.SizeOf());
                    Temporary result = MakeTemp(elemDt);
                    Emit(new LoadIndirect(elemAddr, result, elemDt));
                    // `inner = bins[b]` on a list[list[T]]: the temp IS an inner list,
                    // so the name that receives it resolves len()/[i]/append the same
                    // way the declared spelling would.
                    if (elemDt == DataType.GC_REF
                        && listInnerElemTypes.TryGetValue(listQ, out var subElemDt))
                        listVarElemTypes[result.Name] = subElemDt;
                    return result;
                }
            }

            if (arraySizes.TryGetValue(qualified, out int sz))
            {
                // Evaluate the index once and normalize a negative compile-time index
                // (Python a[-1] -> a[len-1]) before any load path sees it; a runtime
                // index is left as-is (negative runtime indexing is not supported).
                Val idxVal = VisitExpression(expr.Index);
                if (idxVal is Constant negc && negc.Value < 0)
                {
                    int adj = negc.Value + sz;
                    if (adj < 0)
                        throw new IndexError(
                            $"array index {negc.Value} out of range for size {sz}",
                            expr.Line > 0 ? expr.Line : lastLine, expr.Column);
                    idxVal = new Constant(adj);
                }

                // Bounds-check a compile-time positive index for every array kind. The
                // fixed-array path below already does this, but the flash and SRAM paths
                // emitted an out-of-bounds load with no diagnostic (reading past the array).
                if (idxVal is Constant cidx && (cidx.Value < 0 || cidx.Value >= sz))
                    throw new IndexError(
                        $"array index {cidx.Value} out of range for size {sz}",
                        expr.Line > 0 ? expr.Line : lastLine, expr.Column);

                // `DIGITS[digit]` with `digit` known only at run time, on a lookup table
                // written as a plain list. The elements are constants and nothing writes them,
                // so the table goes to flash here, at the first subscript that needs it -- a
                // table only ever indexed with a constant still costs nothing.
                if (idxVal is not Constant
                    && !flashArrays.Contains(qualified)
                    && !arraysWithVariableIndex.Contains(qualified)
                    && !moduleSramArrays.Contains(qualified)
                    && TryMaterialiseConstTable(qualified) is { } ctTable)
                    qualified = ctTable;

                if (flashArrays.Contains(qualified))
                    return EmitFlashArrayRead(qualified, idxVal, sz);

                if (arraysWithVariableIndex.Contains(qualified) || moduleSramArrays.Contains(qualified)
                    || arrayViewBase.ContainsKey(qualified))
                {
                    RemapArrayAccess(qualified, idxVal, out var loadName, out var loadIdx,
                        out var loadSize, out var loadDt);
                    Temporary tmp = MakeTemp(loadDt);
                    Emit(new ArrayLoad(loadName, loadIdx, tmp, loadDt, loadSize));
                    return tmp;
                }
                else
                {
                    if (idxVal is not Constant cc2)
                        throw UnrolledArrayIndexError(qualified, expr.Target);
                    int elemIdx = cc2.Value;
                    if (elemIdx < 0 || elemIdx >= sz)
                        throw new IndexError(
                            $"array index {elemIdx} out of range for size {sz}",
                            expr.Line > 0 ? expr.Line : lastLine, expr.Column);
                    string elemName = qualified + "__" + elemIdx;
                    // The slot's own type wins over the array's uniform element type: a
                    // struct.unpack sequence stores int8/int16 fields next to unsigned ones,
                    // and the uniform type would read a signed field's bits as unsigned.
                    return new Variable(elemName,
                        variableTypes.TryGetValue(elemName, out var elemVarDt)
                            ? elemVarDt : arrayElemTypes[qualified]);
                }
            }
        }

        // Instance-member array load: self._buf[i] (i runtime), where self._buf
        // was declared as a per-instance SRAM framebuffer.
        if (expr.Target is MemberAccessExpr memLoad
            && ResolveMemberArrayName(memLoad) is string flatLoad)
        {
            Val idxVal = VisitExpression(expr.Index);
            RemapArrayAccess(flatLoad, idxVal, out var loadName, out var loadIdx,
                out var loadSize, out var loadDt);
            Temporary tmp = MakeTemp(loadDt);
            Emit(new ArrayLoad(loadName, loadIdx, tmp, loadDt, loadSize));
            return tmp;
        }

        // One evaluation of the target feeds both the __getitem__ probe here and the
        // register-bit tail below -- a CallExpr target (`self._read_register(reg, 1)[0]`,
        // adafruit_bmp280's register read) runs an inline expansion whose I2C traffic must
        // happen exactly once, and re-evaluating it would send every transaction twice.
        Val tgtVal = VisitExpression(expr.Target);
        {
            string cls = GetValClass(tgtVal);
            if (!string.IsNullOrEmpty(cls))
            {
                string funcKey = cls + "_" + "__getitem__";
                if (inlineFunctions.ContainsKey(funcKey))
                {
                    string selfName = tgtVal is Variable v ? v.Name : (tgtVal is Temporary t ? t.Name : "");
                    // `m[x, y]` (#352): the pair is bound to `key` as a compile-time sequence,
                    // which is the same mechanism `pixels[i] = (r, g, b)` already uses for the
                    // VALUE parameter. The dunder then unpacks it with `x, y = key` or reads
                    // `key[0]`, and neither needs a tuple to exist at run time.
                    if (expr.Index is TupleExpr keyTup)
                        return EmitDunderCall(selfName, cls, funcKey, new List<Val> { new NoneVal() },
                            new Dictionary<int, ListExpr> { { 0, new ListExpr(keyTup.Elements) } });
                    Val idxVal = VisitExpression(expr.Index);
                    return EmitDunderCall(selfName, cls, funcKey, new List<Val> { idxVal });
                }

                // Outlined __getitem__: absent from inlineFunctions, present in
                // methodAstByName. Without this the subscript fell through to the
                // register bit-index path and reported "Bit index must be constant".
                if (expr.Target is VariableExpr gv
                    && TryResolveInstanceMethodAst(gv.Name, "__getitem__") != null)
                    return VisitCall(new CallExpr(
                        new MemberAccessExpr(gv, "__getitem__"),
                        new List<Expression> { expr.Index }) { Line = expr.Line });
            }
        }

        if (expr.Target is VariableExpr ve2)
        {
            string localName = string.IsNullOrEmpty(currentInlinePrefix)
                ? (string.IsNullOrEmpty(currentFunction) ? ve2.Name : currentFunction + "." + ve2.Name)
                : currentInlinePrefix + ve2.Name;

            // Runtime flash-string pointer parameter (const[str] on a non-@inline function):
            // s[i] reads one byte from flash at (s + i) via LPM. Works for both literal and
            // runtime indices since the base is only known at runtime.
            if (flashStrPtrVars.Contains(localName))
            {
                Val idxVal = VisitExpression(expr.Index);
                Temporary tmp = MakeTemp(DataType.UINT8);
                Emit(new FlashLoadPtr(new Variable(localName, FlashPtrType), idxVal, tmp));
                return tmp;
            }

            string? strVal = ResolveStrConstant(localName);
            if (strVal != null)
            {
                if (expr.Index is IntegerLiteral ic)
                {
                    if (ic.Value < 0 || ic.Value >= strVal.Length)
                        throw UserError("String subscript index out of range", expr.Index);
                    return new Constant((int)strVal[ic.Value]);
                }

                // Runtime index on a const[str]: intern string as flash data and
                // emit ArrayLoadFlash so the loop can iterate byte by byte.
                string flashName = InternStringAsFlash(strVal);
                Val idxVal = VisitExpression(expr.Index);
                Temporary tmp = MakeTemp(DataType.UINT8);
                Emit(new ArrayLoadFlash(flashName, idxVal, tmp));
                return tmp;
            }
        }

        // Everything below treats a subscript as a REGISTER BIT access, which is what it is for
        // (`PORTB[3]`). A class instance reaching here has no __getitem__ (that path returned
        // above), and reading its bits is never what the author meant. Left alone it produced
        // three different wrong answers depending on the index: bits 0..7 are legal, so `a[0]`
        // built clean and answered from an unassigned slot; `a[9]` overflowed the 8-bit immediate
        // and only avr-as noticed; a run-time index hit "Bit index must be constant for reading"
        // in a program with no registers in it (#171).
        //
        // Narrow on purpose: only a name bound to an instance of a class that HAS a declared
        // field layout, so registers, pointers, bytearrays and every numeric target keep the bit
        // path untouched.
        if (expr.Target is VariableExpr subVe
            && InstanceClassOfName(subVe.Name) is { } subCls
            && classFieldLayout.ContainsKey(subCls))
            throw UserError(
                $"'{subCls}' does not define __getitem__, so '{subVe.Name}[...]' has no meaning. " +
                "Give the class a __getitem__ method (and a __len__ with a constant return if you " +
                "also want to iterate it), or subscript a fixed array instead. A subscript on a " +
                "non-class value reads a register bit, which is not what an instance holds.", subVe);

        // Same defect as the guard above, with a set binding instead of an instance, and it
        // reached the same place: `s = {70, 7}` then `s[1]` emitted `bchk main.s, 1` against a
        // name nothing ever assigns, because a set literal binds a compile-time table and no
        // storage. It built clean and tested a bit of an undefined slot. CPython raises
        // TypeError, a set not being subscriptable in any Python (#208).
        //
        // A DICT binding is deliberately not caught here: `d[key]` is supported and lowers to
        // the constant lookup, so it must keep reaching the path below.
        if (expr.Target is VariableExpr setVe && TryGetSetBinding(setVe.Name, out _))
            throw UserError(
                $"'{setVe.Name}' is a compile-time set literal (read-only membership table), and a " +
                "set is not subscriptable: there is no order for an index to mean. Supported: " +
                $"x in {setVe.Name}, len({setVe.Name}). For a collection you index, use a " +
                "fixed-size list or a bytearray.", setVe);

        // The target already evaluated once above (tgtVal): evaluating again here would
        // replay a call's side effects -- `_read_register(reg, 1)[0]` sent its I2C
        // register-pointer write and read a second time before the subscript ran.
        Val target = tgtVal;
        Val indexVal2 = VisitExpression(expr.Index);

        // `obj.field.prop[i]` where prop is a getter on the field's class
        // returning the object's buffer (`self._shift_register.gpio[k]` on
        // adafruit_74hc595): IsPropertyGetterRead only rewrites a VariableExpr
        // receiver, so this arrives as the expansion's ResultTemp aliased to
        // the member array -- an element load, not a bit test of the alias.
        {
            string? idxTgtName = target is Variable iv ? iv.Name
                : target is Temporary it ? it.Name : null;
            if (idxTgtName != null
                && TryResolveArrayStorageKey(FollowAliases(idxTgtName), out var idxArr)
                && arraySizes.TryGetValue(idxArr, out int idxArrSize))
            {
                DataType idxElem = arrayElemTypes.TryGetValue(idxArr, out var iedt)
                    ? iedt : DataType.UINT8;
                Temporary idxTmp = MakeTemp(idxElem);
                Emit(new ArrayLoad(idxArr, indexVal2, idxTmp, idxElem, idxArrSize));
                return idxTmp;
            }
        }

        Val ResolveAddr(Val val)
        {
            string? name = val is Temporary t ? t.Name : (val is Variable vv ? vv.Name : null);
            if (name != null && constantAddressVariables.TryGetValue(name, out int addr))
                return new MemoryAddress(addr, DataType.UINT16);
            return val;
        }

        target = ResolveAddr(target);

        int bit = 0;
        if (indexVal2 is Constant c) bit = c.Value;
        else
        {
            bool TryConst(string name)
            {
                if (constantVariables.TryGetValue(name, out int cv))
                {
                    bit = cv;
                    return true;
                }

                return false;
            }

            bool resolved = false;
            if (indexVal2 is Temporary t) resolved = TryConst(t.Name);
            else if (indexVal2 is Variable v) resolved = TryConst(v.Name);
            if (!resolved) throw UserError("Bit index must be constant for reading", expr.Index);
        }

        Temporary dst = MakeTemp();
        Emit(new BitCheck(target, bit, dst));
        return dst;
    }

    /// <summary>
    /// `src[start:stop:step]` on a runtime heap list: a fresh GC object holding every
    /// step-th element of the clamped range. Unmodified adafruit_irremote bins a
    /// captured frame with `pulses[1:pulses_end:2]` in decode_bits.
    ///
    /// Bounds are evaluated in signed arithmetic and clamped to [0, len] the way
    /// CPython clamps them: a negative value adds len first, and anything past len
    /// pins at len. The step is a positive compile-time integer -- a computed or
    /// negative step has no heap-cheap lowering and reports honestly. The copy is
    /// an exact fit (capacity = count) and keeps the source's element type, so
    /// reads, len() and append on the result resolve as they did on the source.
    /// </summary>
    private Val EmitRuntimeListSlice(string srcKey, VariableExpr srcVe, SliceExpr sl, IndexExpr expr)
    {
        DataType elemDt = listVarElemTypes[srcKey];
        if (elemDt == DataType.UNKNOWN)
            throw UserError(
                $"cannot infer the element type of '{srcVe.Name}' yet; append an element " +
                "first or declare it like `x: list[uint8] = []`", expr);
        int elemSize = elemDt.SizeOf();

        int step = 1;
        if (sl.Step != null)
        {
            if (!TryFoldConstElement(sl.Step, out step))
                throw UserError(
                    "a slice on a list needs a step the compiler can see (`x[a:b:2]`); " +
                    "a computed step has no lowering on this target", sl.Step);
            if (step <= 0)
                throw UserError(
                    "a slice on a list needs a positive step; a reversed slice (`x[::-1]`) " +
                    "has no lowering on this target", sl.Step);
        }

        Variable srcVar = new Variable(srcKey, DataType.GC_REF);
        Temporary lenTmp = MakeTemp(DataType.UINT8);
        Emit(new LoadIndirect(srcVar, lenTmp));

        // One bound, evaluated then clamped into [0, len] in signed arithmetic.
        Temporary ClampBound(Expression? boundExpr, Val defaultVal)
        {
            Val raw;
            // `s[1:opt:2]` where opt is `int | None` at run time (`pulses_end = -1`
            // else `pulses_end = None` in decode_bits): a None bound is the open
            // end, the payload otherwise -- select on the tag instead of reading
            // the payload unconditionally.
            if (boundExpr is VariableExpr boundVar
                && LiveOptionalTag(boundVar) is { } boundTag)
            {
                Temporary sel = MakeTemp(DataType.INT32);
                Emit(new Copy(defaultVal, sel));
                string selDone = MakeLabel();
                Emit(new JumpIfEqual(boundTag.tag, new Constant(boundTag.noneIdx), selDone));
                Emit(new Copy(EvalOptionalCarry(boundVar), sel));
                Emit(new Label(selDone));
                raw = sel;
            }
            else
            {
                raw = boundExpr == null ? defaultVal : VisitExpression(boundExpr);
            }
            Temporary t = MakeTemp(DataType.INT32);
            Emit(new Copy(raw, t));
            string pastNeg = MakeLabel();
            Emit(new JumpIfGreaterOrEqual(t, new Constant(0), pastNeg));
            Emit(new AugAssign(BinaryOp.Add, t, lenTmp));
            Emit(new Label(pastNeg));
            string pastFloor = MakeLabel();
            Emit(new JumpIfGreaterOrEqual(t, new Constant(0), pastFloor));
            Emit(new Copy(new Constant(0), t));
            Emit(new Label(pastFloor));
            string pastCap = MakeLabel();
            Emit(new JumpIfLessOrEqual(t, lenTmp, pastCap));
            Emit(new Copy(lenTmp, t));
            Emit(new Label(pastCap));
            return t;
        }

        Temporary lo = ClampBound(sl.Start, new Constant(0));
        Temporary hi = ClampBound(sl.Stop, lenTmp);

        // count = hi <= lo ? 0 : (hi - lo + step - 1) / step   (ceil division)
        Temporary span = MakeTemp(DataType.INT32);
        Emit(new Binary(BinaryOp.Sub, hi, lo, span));
        Temporary count32 = MakeTemp(DataType.INT32);
        Emit(new Copy(new Constant(0), count32));
        string countedLabel = MakeLabel();
        Emit(new JumpIfLessOrEqual(span, new Constant(0), countedLabel));
        Temporary spanPlus = MakeTemp(DataType.INT32);
        Emit(new Binary(BinaryOp.Add, span, new Constant(step - 1), spanPlus));
        Emit(new Binary(BinaryOp.Div, spanPlus, new Constant(step), count32));
        Emit(new Label(countedLabel));

        Temporary countBytes = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, count32, new Constant(elemSize), countBytes));
        Temporary allocSize = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, countBytes, new Constant(2), allocSize));

        // GcAlloc may collect and relocate the source; the base pointer below is
        // re-derived from the (possibly updated) variable afterwards. A null
        // return is real heap exhaustion -- refuse rather than write the header
        // through SRAM[0] (the register file).
        Temporary dstPtr = MakeTemp(DataType.GC_REF);
        Emit(new GcAlloc(allocSize, dstPtr, elemDt == DataType.GC_REF));
        string okLabel = MakeLabel();
        Emit(new JumpIfNotZero(dstPtr with { Type = DataType.UINT16 }, okLabel));
        EnterRuntimeBranch($"slicing '{srcVe.Name}'");
        try
        {
            VisitRaise(new RaiseStmt("MemoryError",
                "list slice ran out of heap on this target"));
        }
        finally { LeaveRuntimeBranch(); }
        Emit(new Label(okLabel));

        EmitListStore(dstPtr, 0, count32);
        EmitListStore(dstPtr, 1, count32);

        Temporary srcIdx = MakeTemp(DataType.UINT16);
        Emit(new Copy(lo, srcIdx));
        Temporary dstIdx = MakeTemp(DataType.UINT16);
        Emit(new Copy(new Constant(0), dstIdx));
        Temporary count16 = MakeTemp(DataType.UINT16);
        Emit(new Copy(count32, count16));

        string sliceLoopLabel = MakeLabel();
        string sliceLoopEnd = MakeLabel();
        Emit(new Label(sliceLoopLabel));
        Temporary sliceDone = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.GreaterEqual, dstIdx, count16, sliceDone));
        Emit(new JumpIfNotZero(sliceDone, sliceLoopEnd));
        Temporary srcOff = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, srcIdx, new Constant(elemSize), srcOff));
        Temporary srcAddr = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, srcVar with { Type = DataType.UINT16 }, srcOff, srcAddr));
        Emit(new AugAssign(BinaryOp.Add, srcAddr, new Constant(2)));
        Temporary dstOff = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, dstIdx, new Constant(elemSize), dstOff));
        Temporary dstAddr = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, dstPtr with { Type = DataType.UINT16 }, dstOff, dstAddr));
        Emit(new AugAssign(BinaryOp.Add, dstAddr, new Constant(2)));
        Temporary elemTmp = MakeTemp(elemDt);
        Emit(new LoadIndirect(srcAddr, elemTmp, elemDt));
        Emit(new StoreIndirect(elemTmp, dstAddr, elemDt));
        Emit(new AugAssign(BinaryOp.Add, srcIdx, new Constant(step)));
        Emit(new AugAssign(BinaryOp.Add, dstIdx, new Constant(1)));
        Emit(new Jump(sliceLoopLabel));
        Emit(new Label(sliceLoopEnd));

        listVarElemTypes[dstPtr.Name] = elemDt;
        if (listInnerElemTypes.TryGetValue(srcKey, out var innerElem))
            listInnerElemTypes[dstPtr.Name] = innerElem;
        return dstPtr;
    }

    /// The listVarElemTypes key a Val names, when it names a runtime heap list.
    /// A Variable read comes back already qualified; a Temporary from a slice or
    /// copy-ctor is registered under its own name. Null for anything else.
    private string? ListKeyOfVal(Val v)
    {
        if (v is not Variable vv) return null;
        if (listVarElemTypes.ContainsKey(vv.Name)) return vv.Name;
        return ResolveListVarQualified(vv.Name) is { Length: > 0 } k ? k : null;
    }

    /// A runtime list lives in a named Variable slot or it is not a GC root: a
    /// Temporary holding a fresh slice/copy survives only until the next
    /// allocation relocates the heap under it. Rebind those to a generated name
    /// (the same shape MaterializeSequenceLiteral gives a literal) and answer
    /// the name's key; a Variable that is already a slot needs no copy.
    private string RootListOperand(Val v, string key)
    {
        string? resolved = ListKeyOfVal(v);
        if (v is Variable vv && !vv.Name.StartsWith("tmp_") && resolved != null)
            return resolved;
        variableTypes[key] = DataType.GC_REF;
        listVarElemTypes[key] = listVarElemTypes[resolved!];
        if (resolved != null && listInnerElemTypes.TryGetValue(resolved, out var srcInner))
            listInnerElemTypes[key] = srcInner;
        Emit(new Copy(v, new Variable(key, DataType.GC_REF)));
        return key;
    }

    /// `a + b` on two runtime heap lists: CPython allocates a fresh list that
    /// holds both payloads. Lowered the way the slice copy is -- a header of
    /// len/cap, then a counted loop per source. The operands are bound to names
    /// BEFORE the result allocates, because a Temporary is not a GC root and the
    /// GcAlloc below can compact the heap under it.
    private Variable EmitRuntimeListConcat(Val leftVal, Expression rightExpr, Expression at)
    {
        string aKey = RootListOperand(leftVal, QualifyHelperName("__cat_" + labelCounter++));
        DataType elemDt = listVarElemTypes[aKey];
        DataType innerDt = listInnerElemTypes.TryGetValue(aKey, out var inner) ? inner : DataType.UNKNOWN;

        Val rightVal = VisitExpression(rightExpr);
        string? rightKey = ListKeyOfVal(rightVal);
        if (rightKey == null)
            throw new TypeError(
                "cannot concatenate a list with a non-list value; both operands of '+' " +
                "must be lists on this target", at.Line > 0 ? at.Line : lastLine, at.Column);
        string bKey = RootListOperand(rightVal, QualifyHelperName("__cat_" + labelCounter++));
        if (listVarElemTypes[bKey] != elemDt)
            throw UserError(
                "the two lists do not agree on an element type -- PyMCU lists are "
                + "homogeneous, so a concat cannot mix them", at);

        int elemSize = elemDt.SizeOf();
        Variable aVar = new Variable(aKey, DataType.GC_REF);
        Variable bVar = new Variable(bKey, DataType.GC_REF);
        Temporary lenA = MakeTemp(DataType.UINT8);
        Emit(new LoadIndirect(aVar, lenA));
        Temporary lenB = MakeTemp(DataType.UINT8);
        Emit(new LoadIndirect(bVar, lenB));
        Temporary total = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, lenA, lenB, total));

        Temporary totalBytes = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, total, new Constant(elemSize), totalBytes));
        Temporary allocSize = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, totalBytes, new Constant(2), allocSize));

        string dstKey = QualifyHelperName("__cat_" + labelCounter++);
        var dstVar = new Variable(dstKey, DataType.GC_REF);
        Temporary dstPtr = MakeTemp(DataType.GC_REF);
        Emit(new GcAlloc(allocSize, dstPtr, elemDt == DataType.GC_REF));
        string catOk = MakeLabel();
        Emit(new JumpIfNotZero(dstPtr with { Type = DataType.UINT16 }, catOk));
        EnterRuntimeBranch("concatenating two lists");
        try
        {
            VisitRaise(new RaiseStmt("MemoryError",
                "list concatenation ran out of heap on this target"));
        }
        finally { LeaveRuntimeBranch(); }
        Emit(new Label(catOk));
        variableTypes[dstKey] = DataType.GC_REF;
        listVarElemTypes[dstKey] = elemDt;
        if (innerDt != DataType.UNKNOWN) listInnerElemTypes[dstKey] = innerDt;
        Emit(new Copy(dstPtr, dstVar));

        EmitListStore(dstVar, 0, total);
        EmitListStore(dstVar, 1, total);

        // for i in 0..len(src): dst[2 + (base + i)*sz] = src[2 + i*sz]
        void CopyPayload(Variable src, Val baseIdx)
        {
            Temporary idx = MakeTemp(DataType.UINT16);
            Emit(new Copy(new Constant(0), idx));
            Temporary srcLen = MakeTemp(DataType.UINT8);
            Emit(new LoadIndirect(src, srcLen));
            string loopTop = MakeLabel();
            string loopEnd = MakeLabel();
            Emit(new Label(loopTop));
            Temporary done = MakeTemp(DataType.UINT8);
            Emit(new Binary(BinaryOp.GreaterEqual, idx, srcLen, done));
            Emit(new JumpIfNotZero(done, loopEnd));
            Temporary srcOff = MakeTemp(DataType.UINT16);
            Emit(new Binary(BinaryOp.Mul, idx, new Constant(elemSize), srcOff));
            Temporary srcAddr = MakeTemp(DataType.UINT16);
            Emit(new Binary(BinaryOp.Add, src with { Type = DataType.UINT16 }, srcOff, srcAddr));
            Emit(new AugAssign(BinaryOp.Add, srcAddr, new Constant(2)));
            Temporary dstIdx = MakeTemp(DataType.UINT16);
            Emit(new Binary(BinaryOp.Add, idx, baseIdx, dstIdx));
            Temporary dstOff = MakeTemp(DataType.UINT16);
            Emit(new Binary(BinaryOp.Mul, dstIdx, new Constant(elemSize), dstOff));
            Temporary dstAddr = MakeTemp(DataType.UINT16);
            Emit(new Binary(BinaryOp.Add, dstVar with { Type = DataType.UINT16 }, dstOff, dstAddr));
            Emit(new AugAssign(BinaryOp.Add, dstAddr, new Constant(2)));
            Temporary elemTmp = MakeTemp(elemDt);
            Emit(new LoadIndirect(srcAddr, elemTmp, elemDt));
            Emit(new StoreIndirect(elemTmp, dstAddr, elemDt));
            Emit(new AugAssign(BinaryOp.Add, idx, new Constant(1)));
            Emit(new Jump(loopTop));
            Emit(new Label(loopEnd));
        }

        CopyPayload(aVar, new Constant(0));
        CopyPayload(bVar, lenA);
        return dstVar;
    }

    private string QualifyHelperName(string name)
    {
        string key = !string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix + name
            : !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name
            : name;
        ObserveResolution("QualifyHelperName", name, key);
        return key;
    }

    /// `[e for v in xs if cond]` where `xs` is a runtime heap list: the result
    /// is a fresh heap list. Capacity is the source length -- the filter can
    /// only shrink it, so no grow path is needed. The loop variable is a named
    /// slot (a Temporary would not be a GC root and `b[1]` reads need a real
    /// list name to resolve against). Returns the result's Variable, or null
    /// when the iterable is not a runtime list and the caller keeps its own
    /// fallback.
    private Variable? TryEmitRuntimeListComp(ListCompExpr lc)
    {
        if (lc.Iterable2 != null || !string.IsNullOrEmpty(lc.Var2Name)) return null;
        if (ComprehensionElementIsInstance(lc)) return null;

        // Decide whether the iterable can produce a runtime list BEFORE
        // evaluating it, so a non-list iterable keeps the caller's fallback --
        // and its diagnostic -- rather than emitting half a loop body first.
        bool couldBeList =
            lc.Iterable is VariableExpr iv && ResolveListVarQualified(iv.Name) is { Length: > 0 }
            || lc.Iterable is BinaryExpr { Op: AstBinOp.Add };
        if (!couldBeList) return null;

        Val srcVal = VisitExpression(lc.Iterable);
        string? srcKey = ListKeyOfVal(srcVal);
        if (srcKey == null) return null;
        srcKey = RootListOperand(srcVal, QualifyHelperName("__csrc_" + labelCounter++));
        DataType srcElem = listVarElemTypes[srcKey];
        DataType srcInner = listInnerElemTypes.TryGetValue(srcKey, out var si) ? si : DataType.UNKNOWN;
        Variable srcVar = new Variable(srcKey, DataType.GC_REF);

        // The loop variable: a named slot under the name the element and filter
        // read -- qualified the same way a `for` target is, so `b[1]` resolves
        // the inner type through it.
        string varKey = QualifyHelperName(lc.VarName);
        variableTypes[varKey] = srcElem;
        var lcVar = new Variable(varKey, srcElem);
        if (srcElem == DataType.GC_REF)
        {
            listVarElemTypes[varKey] = srcInner;
            if (srcInner == DataType.GC_REF)
                throw UserError(
                    "a comprehension over a list nested deeper than list[list[T]] has no "
                    + "element type for the innermost lists; bind the levels to names of "
                    + "their own first", lc);
        }

        DataType resElem = InferExprType(lc.Element);
        if (resElem == DataType.UNKNOWN || resElem == DataType.VOID) resElem = srcElem;
        int elemSize = resElem.SizeOf();

        // cap = len(src): the filter can only keep fewer.
        Temporary srcLen = MakeTemp(DataType.UINT8);
        Emit(new LoadIndirect(srcVar, srcLen));
        Temporary capBytes = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, srcLen, new Constant(elemSize), capBytes));
        Temporary allocSize = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, capBytes, new Constant(2), allocSize));

        string resKey = QualifyHelperName("__compres_" + labelCounter++);
        var resVar = new Variable(resKey, DataType.GC_REF);
        Temporary resPtr = MakeTemp(DataType.GC_REF);
        Emit(new GcAlloc(allocSize, resPtr, resElem == DataType.GC_REF));
        string resOk = MakeLabel();
        Emit(new JumpIfNotZero(resPtr with { Type = DataType.UINT16 }, resOk));
        EnterRuntimeBranch("materializing a list comprehension");
        try
        {
            VisitRaise(new RaiseStmt("MemoryError",
                "list comprehension ran out of heap on this target"));
        }
        finally { LeaveRuntimeBranch(); }
        Emit(new Label(resOk));
        variableTypes[resKey] = DataType.GC_REF;
        listVarElemTypes[resKey] = resElem;
        if (resElem == DataType.GC_REF) listInnerElemTypes[resKey] = srcInner;
        Emit(new Copy(resPtr, resVar));
        EmitListStore(resVar, 1, srcLen);

        Temporary outIdx = MakeTemp(DataType.UINT16);
        Emit(new Copy(new Constant(0), outIdx));
        Temporary idx = MakeTemp(DataType.UINT16);
        Emit(new Copy(new Constant(0), idx));

        string loopTop = MakeLabel();
        string loopEnd = MakeLabel();
        Emit(new Label(loopTop));
        Temporary done = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.GreaterEqual, idx, srcLen, done));
        Emit(new JumpIfNotZero(done, loopEnd));

        // v = src[idx]
        Temporary srcAddr = EmitElemAddr(srcVar, idx, srcElem.SizeOf());
        Temporary elemTmp = MakeTemp(srcElem);
        Emit(new LoadIndirect(srcAddr, elemTmp, srcElem));
        Emit(new Copy(elemTmp, lcVar));

        // The filter decides at run time -- a failed check skips the store.
        string nextIter = MakeLabel();
        if (lc.Filter != null)
        {
            Val cond = VisitExpression(lc.Filter);
            Emit(new JumpIfZero(cond, nextIter));
        }

        // res[outIdx] = e; outIdx++
        Val mapped = VisitExpression(lc.Element);
        Temporary dstAddr = EmitElemAddr(resVar, outIdx, elemSize);
        Emit(new StoreIndirect(mapped, dstAddr, resElem));
        Emit(new AugAssign(BinaryOp.Add, outIdx, new Constant(1)));

        Emit(new Label(nextIter));
        Emit(new AugAssign(BinaryOp.Add, idx, new Constant(1)));
        Emit(new Jump(loopTop));
        Emit(new Label(loopEnd));

        // len = the number of stores that ran; cap was the source length.
        EmitListStore(resVar, 0, outIdx);
        return resVar;
    }

    /// `[e] * n` with a count that only exists at run time: a fresh heap list
    /// of n copies of the single literal element. The compile-time repeat path
    /// answers the fixed-count form first, so what reaches here is exactly the
    /// case a fixed array cannot hold. Returns null when the expression is not
    /// a single-element `list * count` at all.
    private Variable? TryEmitRuntimeListRepeat(BinaryExpr mul)
    {
        ListExpr? lit = mul.Left as ListExpr;
        Expression countExpr = mul.Right;
        if (lit == null)
        {
            lit = mul.Right as ListExpr;
            countExpr = mul.Left;
        }
        if (lit == null || lit.Elements.Count != 1) return null;
        // A compile-time count belongs to the fixed-array repeat path; reaching
        // here means TryRepeatCount already declined it, so evaluate it for real.
        if (TryRepeatCount(countExpr, out _)) return null;

        Val count = VisitExpression(countExpr);
        DataType elemDt = InferExprType(lit.Elements[0]);
        if (elemDt is DataType.UNKNOWN or DataType.VOID) elemDt = DataType.UINT8;
        if (elemDt == DataType.GC_REF)
            throw UserError(
                "a `[x] * n` repeat of a list element aliases one object n times -- "
                + "PyMCU has no per-element copy to offer; build the list in a loop", mul);
        int elemSize = elemDt.SizeOf();

        // The element evaluates once -- `[f()] * n` calls f once, per Python.
        Val elemVal = VisitExpression(lit.Elements[0]);

        Temporary lenBytes = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, count, new Constant(elemSize), lenBytes));
        Temporary allocSize = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, lenBytes, new Constant(2), allocSize));

        string resKey = QualifyHelperName("__rep_" + labelCounter++);
        var resVar = new Variable(resKey, DataType.GC_REF);
        Temporary resPtr = MakeTemp(DataType.GC_REF);
        Emit(new GcAlloc(allocSize, resPtr, false));
        string repOk = MakeLabel();
        Emit(new JumpIfNotZero(resPtr with { Type = DataType.UINT16 }, repOk));
        EnterRuntimeBranch("materializing a [x] * n list");
        try
        {
            VisitRaise(new RaiseStmt("MemoryError",
                "[x] * n list ran out of heap on this target"));
        }
        finally { LeaveRuntimeBranch(); }
        Emit(new Label(repOk));
        variableTypes[resKey] = DataType.GC_REF;
        listVarElemTypes[resKey] = elemDt;
        Emit(new Copy(resPtr, resVar));

        EmitListStore(resVar, 0, count);
        EmitListStore(resVar, 1, count);

        Temporary idx = MakeTemp(DataType.UINT16);
        Emit(new Copy(new Constant(0), idx));
        string repTop = MakeLabel();
        string repEnd = MakeLabel();
        Emit(new Label(repTop));
        Temporary repDone = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.GreaterEqual, idx, count, repDone));
        Emit(new JumpIfNotZero(repDone, repEnd));
        Temporary dstAddr = EmitElemAddr(resVar, idx, elemSize);
        Emit(new StoreIndirect(elemVal, dstAddr, elemDt));
        Emit(new AugAssign(BinaryOp.Add, idx, new Constant(1)));
        Emit(new Jump(repTop));
        Emit(new Label(repEnd));
        return resVar;
    }

    // The element type a literal asks for when none is declared: the widest type
    // its scalar elements infer to, GC_REF when the elements are themselves
    // literals. A mixed literal has no one element type -- a PyMCU list is
    // homogeneous, so `[[1], 2]` refuses rather than silently picking one.
    private DataType InferLiteralElemType(List<Expression> elements, Expression at)
    {
        DataType elemDt = DataType.UNKNOWN;
        foreach (var e in elements)
        {
            DataType et = e is ListExpr or TupleExpr ? DataType.GC_REF : InferExprType(e);
            if (et == DataType.GC_REF || elemDt == DataType.GC_REF)
            {
                if (elemDt != DataType.UNKNOWN && elemDt != DataType.GC_REF
                    || et != DataType.GC_REF && et != DataType.UNKNOWN)
                    throw UserError(
                        "a list literal that mixes list and scalar elements has no one "
                        + "element type -- PyMCU lists are homogeneous; bind the inner "
                        + "lists to names of their own first", at);
                elemDt = DataType.GC_REF;
                continue;
            }
            if (et is DataType.UNKNOWN or DataType.VOID)
                throw UserError(
                    "cannot infer the element type of this list literal; declare it "
                    + "instead: `x: list[uint8] = [...]`", e);
            elemDt = elemDt == DataType.UNKNOWN ? et : DataTypeExtensions.GetPromotedType(elemDt, et);
        }
        return elemDt == DataType.UNKNOWN ? DataType.UINT8 : elemDt;
    }

    // `x = [[v, 0]]` / `xs.append([v, 1])`: a literal that must be a run-time
    // heap object. The object is gc_alloc'd exact-fit (count == capacity), its
    // payload ref-flagged when the elements are themselves lists, and -- before
    // any element that materializes a list of its own -- bound to a generated
    // NAME: a Temporary is not a GC root, so the name is what keeps the object
    // findable when a nested literal's allocation compacts the heap.
    private Variable MaterializeSequenceLiteral(List<Expression> elements, DataType? declaredElem,
                                                Expression at)
    {
        DataType elemDt = declaredElem ?? InferLiteralElemType(elements, at);

        DataType innerDt = DataType.UNKNOWN;
        if (elemDt == DataType.GC_REF)
        {
            var innerDts = new List<DataType>();
            foreach (var e in elements)
            {
                // The outer's inner element type is each element-list's OWN
                // element type: a literal infers it, a bound list variable
                // already carries it, and anything else is the mixed-literal
                // refusal InferLiteralElemType promises.
                DataType dt = e switch
                {
                    ListExpr il => InferLiteralElemType(il.Elements, e),
                    TupleExpr it => InferLiteralElemType(it.Elements, e),
                    VariableExpr ev when ResolveListVarQualified(ev.Name) is { Length: > 0 } evk
                        => listVarElemTypes[evk],
                    _ => throw UserError(
                        "a list literal that mixes list and scalar elements has no one "
                        + "element type -- PyMCU lists are homogeneous; bind the inner "
                        + "lists to names of their own first", at),
                };
                if (!innerDts.Contains(dt)) innerDts.Add(dt);
            }
            if (innerDts.Count > 1)
                throw UserError(
                    "the inner literals do not agree on an element type -- PyMCU lists "
                    + "are homogeneous; declare the inner element type with `list[T]` "
                    + "instead", at);
            innerDt = innerDts.Count > 0 ? innerDts[0] : DataType.UINT8;
            if (innerDt == DataType.GC_REF)
                throw UserError(
                    "a list literal nested deeper than list[list[T]] has no inferred "
                    + "element type for the innermost lists; bind it to a name and "
                    + "declare the levels separately", at);
        }

        int elemSize = elemDt.SizeOf();
        Temporary tmpPtr = MakeTemp(DataType.GC_REF);
        Emit(new GcAlloc(new Constant(2 + elements.Count * elemSize), tmpPtr,
                         elemDt == DataType.GC_REF));

        // The allocation answers 0 on exhaustion: a header store through it is
        // SRAM[0] -- raise the way list() and append do.
        string litOk = MakeLabel();
        Emit(new JumpIfNotZero(tmpPtr with { Type = DataType.UINT16 }, litOk));
        EnterRuntimeBranch("materializing a list literal");
        try
        {
            VisitRaise(new RaiseStmt("MemoryError",
                "list literal ran out of heap on this target"));
        }
        finally { LeaveRuntimeBranch(); }
        Emit(new Label(litOk));

        string litName = "__lit_" + labelCounter++;
        string litKey = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + litName
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + litName : litName);
        var litVar = new Variable(litKey, DataType.GC_REF);
        variableTypes[litKey] = DataType.GC_REF;
        listVarElemTypes[litKey] = elemDt;
        if (innerDt != DataType.UNKNOWN)
            listInnerElemTypes[litKey] = innerDt;
        Emit(new Copy(tmpPtr, litVar));

        EmitListStore(litVar, 0, new Constant(elements.Count));
        EmitListStore(litVar, 1, new Constant(elements.Count));

        for (int k = 0; k < elements.Count; k++)
        {
            Val ev = elements[k] switch
            {
                ListExpr innerList => MaterializeSequenceLiteral(innerList.Elements, innerDt, elements[k]),
                TupleExpr innerTuple => MaterializeSequenceLiteral(innerTuple.Elements, innerDt, elements[k]),
                _ => VisitExpression(elements[k]),
            };
            EmitListStore(litVar, 2 + k * elemSize, ev, elemDt);
        }

        return litVar;
    }

    // A just-loaded slot field whose declared type is itself a class: re-tag the loaded value
    // with that (concrete) class so a following `.field`/`.method()` resolves after the ZCA
    // collapse (the field stores only the scalar). Only class-typed fields (fieldClasses) ->
    // narrow, no effect on the scalar fields that make up the vast majority.
    private Val TagSlotFieldClass(Val loaded, string? cls, string member)
    {
        if (loaded is Temporary t && cls != null
            && fieldClasses.TryGetValue(cls + "|" + member, out var fc)
            && ResolveConcreteClass(fc) is { } cc)
        {
            instanceClasses[t.Name] = cc;
            if (classFieldLayout.TryGetValue(cc, out var l) && l.Count == 1)
                factoryHandleInstances.Add(t.Name);
        }
        return loaded;
    }

    // The class a plain name is an instance of, or null when the name is not an instance.
    // Pure lookup: it emits no IR, so it is safe to ask before deciding how to lower an access.
    //
    // "Pure" has to include not throwing. Fourteen call sites ask this question speculatively,
    // to choose how to lower something, and ResolveBinding answers an unknown name by throwing
    // the user-facing "name is not defined". That error escaped the probe and became the
    // reported diagnostic, which had two consequences: `no_such_func(1)` was reported as a name
    // read rather than as the call it plainly is, never reaching the message written for that
    // case, and it arrived from a helper that has only a bare string, so it carried no column
    // and could not be pointed at. ProbeBinding answers null for a name that is merely unknown,
    // so the caller reaches its own diagnostic with the node still in hand -- while a module
    // guard's refusal still throws, because that one is an answer worth keeping.
    private string? InstanceClassOfName(string recvName)
    {
        // A parameter or local of this scope, even when the bare name is also a
        // module alias. `import adafruit_framebuf as framebuf` then
        // `def set_pixel(framebuf, ...): ... framebuf.stride` is the instance
        // (adafruit_ssd1306 / MVLSBFormat). ProbeBinding of the bare name can
        // miss the prefixed binding and leave this null, which then sent the
        // member onto the module path as `adafruit_framebuf_stride`.
        foreach (string? key in new[]
                 {
                     string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + recvName,
                     string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + recvName,
                 })
        {
            if (key == null) continue;
            string n = key;
            for (int depth = 0; depth < 20; depth++)
            {
                if (instanceClasses.TryGetValue(n, out var scoped) && scoped != null)
                    return scoped;
                if (!variableAliases.TryGetValue(n, out var next)
                    || next == null || Temporary.IsScratchName(next)) break;
                n = next;
            }
        }

        if (ProbeBinding(recvName) is not Variable rv) return null;
        string name = rv.Name;
        for (int depth = 0; depth < 20; depth++)
        {
            if (!variableAliases.TryGetValue(name, out var next)
                || next == null || Temporary.IsScratchName(next)) break;
            name = next;
        }
        return instanceClasses.TryGetValue(name, out var cls) ? cls : null;
    }

    /// <summary>
    /// True when <paramref name="name"/> still names a module at this point of
    /// lowering. An assignment that rebinds the name to an instance shadows the
    /// alias, matching CPython (#467). Member writes already resolved the
    /// instance; reads and calls consulted the module table first and never
    /// saw the binding -- <c>from adafruit_motor import servo</c> then
    /// <c>servo = servo.Servo(pwm)</c> then <c>print(servo.fraction)</c>.
    /// </summary>
    private bool StillNamesAModule(string name) =>
        modules.ContainsKey(name) && InstanceClassOfName(name) == null;

    /// <summary>
    /// True when a member access on <paramref name="name"/> is still a
    /// module-level name, including the constructor in
    /// <c>servo = servo.Servo(pwm)</c>. VisitAssign tags the target as the
    /// instance before the RHS runs, so <see cref="StillNamesAModule"/> is
    /// already false there; the member is the class on the module (#467).
    ///
    /// A module-level singleton (<c>time = _TimeAlarmModule()</c> in alarm.py) is
    /// also a module member: it is filed under the mangled key, not as a class.
    /// <c>import time</c> against that name can file the ALARM module itself as
    /// an instance (#381), and without this check <c>alarm.time.TimeAlarm</c>
    /// was refused as "object has no attribute 'time'" -- the instance path,
    /// which has no such field. A rebound alias whose member is NOT on the
    /// module (<c>servo.fraction</c> after <c>servo = servo.Servo()</c>) still
    /// returns false, so the instance path keeps the field.
    /// </summary>
    private bool NamesAModuleMember(string name, string member)
    {
        if (!modules.ContainsKey(name)) return false;
        if (InstanceClassOfName(name) == null) return true;
        string realMod = TryImportedAlias(name, out var rm) && rm != null ? rm : name;
        string mangled = realMod.Replace('.', '_') + "_" + member;
        return inlineFunctions.ContainsKey(mangled + "___init__")
            || overloadedFunctions.Contains(mangled + "___init__")
            || classFieldLayout.ContainsKey(mangled)
            || instanceClasses.ContainsKey(mangled)
            || mutableGlobals.ContainsKey(mangled)
            || globals.ContainsKey(mangled)
            || functionReturnTypes.ContainsKey(mangled)
            || inlineFunctions.ContainsKey(mangled);
    }

    /// <summary>
    /// The class a lowered value is an instance of, or null when it is not one.
    /// Used by argument binding to substitute a typing-only parameter annotation
    /// for the argument's real class (#419).
    /// </summary>
    private string? InstanceClassOfVal(Val v)
    {
        // Walk the same alias chain GetValClass does: a constructor in value
        // position (`Lcd(mcp.get_pin(1), ...)`) returns a Variable/`__cN` that
        // aliases the instance, and a one-hop lookup on the Temporary missed it.
        string cls = GetValClass(v);
        return string.IsNullOrEmpty(cls) ? null : cls;
    }

    /// <summary>
    /// <c>x in container</c> when the container is an instance that defines <c>__contains__</c>.
    /// Used for a name AND for a call that returns one (<c>"Linux" not in uname()</c>, #466).
    /// </summary>
    private bool TryContainsDunder(Val container, Val lhs, bool negate, out Val result)
    {
        result = null!;
        // Walk the alias chain: an @inline factory's result temp aliases the
        // constructed instance and is not itself a key of instanceClasses (#466).
        string cls = GetValClass(container);
        string? key = ResolveClassCarryingName(container);
        if (string.IsNullOrEmpty(cls) || key == null || !ClassDefinesMethod(cls, "__contains__"))
            return false;
        string funcKey = cls + "_" + "__contains__";
        Val res;
        if (inlineFunctions.ContainsKey(funcKey))
            res = EmitDunderCall(key, cls, funcKey, new List<Val> { lhs });
        else if (TryResolveInstanceMethodAst(key, "__contains__") != null)
            res = VisitCall(new CallExpr(
                new MemberAccessExpr(new PreEvaluatedExpr(container, null), "__contains__"),
                new List<Expression> { new PreEvaluatedExpr(lhs, null) }));
        else
            return false;
        if (negate)
        {
            Temporary neg = MakeTemp();
            Emit(new Binary(PyMCU.IR.BinaryOp.Equal, res, new Constant(0), neg));
            result = neg;
            return true;
        }
        result = res;
        return true;
    }

    /// <summary>
    /// <c>needle in haystack</c> when both are compile-time strings: Python substring
    /// membership, folded to 0/1. Used for <c>"RP2350" in uname().machine</c> (#466).
    /// </summary>
    private bool TryStringContainsVal(Val haystack, Val needle, bool negate, out Val result)
    {
        result = null!;
        string? hay = StringTextOfVal(haystack);
        string? ned = StringTextOfVal(needle);
        if (hay == null || ned == null) return false;
        bool hit = hay.Contains(ned);
        result = new Constant(negate ? (hit ? 0 : 1) : (hit ? 1 : 0));
        return true;
    }

    private string? StringTextOfVal(Val v) => v switch
    {
        Constant { Text: { } t } => t,
        Constant sc when stringIdToStr.TryGetValue(sc.Value, out var s) => s,
        Variable vr => ResolveStrConstant(vr.Name),
        Temporary tmp => ResolveStrConstant(tmp.Name),
        _ => null
    };

    /// <summary>
    /// The name at the end of this name's alias chain, when that name is a known instance.
    ///
    /// `with C(...) as v:` binds v by writing `variableAliases[v] = <manager>` and nothing else:
    /// there is no instance registered under v, because v IS the manager. Reading a field
    /// through v works, because `InstanceClassOfName` walks that chain. A METHOD call did not
    /// walk it, looked v up in `instanceClasses`, found nothing, and degraded into a free
    /// function named `v_method` -- which is what `with digitalio.DigitalInOut(...) as pin:`
    /// then `pin.switch_to_output(True)` was refused as (#305).
    ///
    /// Returns the name rather than the class, because the receiver of the method has to become
    /// the manager: binding `self` to v would look for fields under a name that has none.
    /// </summary>
    private string? AliasedInstanceName(string startName)
    {
        string name = startName;
        for (int depth = 0; depth < 20; depth++)
        {
            if (!variableAliases.TryGetValue(name, out var next) || next == null) break;
            // A scratch name is a dead end for the chase UNLESS it carries a class --
            // a boxed field read (`with self._device:`) lowers to a slot-load temp
            // that TagSlotFieldClass tags with the field's class, and stopping there
            // would strand the manager's `__enter__` dispatch.
            if (Temporary.IsScratchName(next) && !instanceClasses.ContainsKey(next)) break;
            name = next;
            if (instanceClasses.ContainsKey(name)) return name;
        }
        return null;
    }

    /// <summary>
    /// The flattened instance NAME behind an expression whose Val is nameless: an
    /// object-typed field, kept out of the scalar slot layout, whose members live
    /// under `<anchor>_<field>` names. A coroutine's `self.a = Acc(s)` is the shape
    /// that needs it -- `self` inside a method inlined onto that field aliases to
    /// the anchor (`main.__c1_a`), the anchor evaluates to no scalar, and the
    /// field's own members are still written and read under its name.
    ///
    /// Answers only when the resolved name carries a class in `instanceClasses`,
    /// so a name that is merely unresolved keeps answering null instead of being
    /// mistaken for an object.
    /// </summary>
    private string? AnchorNameOf(Expression expr)
    {
        switch (expr)
        {
            case VariableExpr ve:
                foreach (var cand in new[]
                         {
                             currentInlinePrefix + ve.Name,
                             string.IsNullOrEmpty(currentFunction) ? ve.Name : currentFunction + "." + ve.Name,
                             ve.Name,
                         })
                {
                    string cur = cand;
                    for (int d = 0; d < 20 && variableAliases.TryGetValue(cur, out var nx); d++)
                    {
                        if (nx == null || nx.StartsWith("tmp_", StringComparison.Ordinal)) break;
                        cur = nx;
                    }
                    if (instanceClasses.ContainsKey(cur)) return cur;
                }
                return null;

            case MemberAccessExpr ma:
                if (AnchorNameOf(ma.Object) is { } outer)
                {
                    string flat = outer + "_" + ma.Member;
                    if (instanceClasses.ContainsKey(flat)) return flat;
                    // The write that registers <outer>_<member> can sit behind a branch
                    // boundary the join already closed: a coroutine's `self.a = Acc(s)`
                    // runs in one state arm while `self.a.add(1)` lowers in the next, and
                    // instanceClasses is arm-scoped. The field's class is still recorded
                    // on the owner's class at scan time -- recover it from there.
                    if (instanceClasses.TryGetValue(outer, out var outerCls) && outerCls != null
                        && fieldClasses.TryGetValue(outerCls + "|" + ma.Member, out var fCls)
                        && fCls != null)
                    {
                        instanceClasses[flat] = fCls;
                        return flat;
                    }
                }
                return null;

            default:
                return null;
        }
    }

    // True when <member> could legitimately be reached through this receiver.
    //
    // When the receiver's class is known, that class and its bases answer, and `receiverClass` is
    // set so the caller can name it. When it is not known -- an unbound name, a temporary, a
    // module-level object the scan never keyed -- the program-wide superset answers instead and
    // `receiverClass` stays null, so this can only ever refuse a receiver it positively resolved.
    //
    // Assignment-based on purpose. classFieldLayout would be the obvious source and is the wrong
    // one: it omits array fields and never learns fields assigned inside a `match`, so gating a
    // READ on it would reject valid code. See the note on assignedMemberNamesByClass in State.cs.
    private bool MemberReachableFromReceiver(
        string? baseName, string member, out string? receiverClass, out string receiverMembers)
    {
        receiverClass = null;
        receiverMembers = "";
        if (baseName != null
            && ReceiverClassThroughAliases(baseName) is { } cls && !string.IsNullOrEmpty(cls))
        {
            var seen = new SortedSet<string>(StringComparer.Ordinal);
            bool known = false;
            string? cur = cls;
            for (int depth = 0; cur != null && depth < 20; depth++)
            {
                if (assignedMemberNamesByClass.TryGetValue(cur, out var own))
                {
                    known = true;
                    if (own.Contains(member)) return true;
                    foreach (var m in own) seen.Add(m);
                }
                cur = classBasePrefixes.TryGetValue(cur, out var bp) && !string.IsNullOrEmpty(bp)
                    ? (bp.EndsWith("_") ? bp[..^1] : bp)
                    : null;
            }
            // Only refuse BY NAME on a class we actually learned something about. A class the
            // collector never keyed says nothing about its members, and treating silence as "no
            // fields" would turn every read on it into an error.
            //
            // A tempting further step is to name the class whenever classDirectMethods has an
            // entry for it (an entry every class definition gets, even an empty one) rather than
            // only when `known`. Tried and reverted (#441): a NESTED class's owner key in
            // assignedMemberNamesByClass does not always match the key classDirectMethods and
            // the receiver resolver use for it, a pre-existing gap unrelated to this issue, and
            // the wider check turned that gap into a false refusal on real CircuitPython-layer
            // code (alarm.TimeAlarm._deadline_ms, genuinely assigned in __init__, reported as
            // assigned nowhere). `known` alone is the narrower, already-correct check.
            if (known)
            {
                receiverClass = cls;
                receiverMembers = seen.Count > 0 ? string.Join(", ", seen) : "(none)";
                return false;
            }
        }
        return assignedMemberNames.Contains(member);
    }

    /// <summary>
    /// The class of a receiver, resolved through the alias chain to the ARGUMENT it came from
    /// (#318).
    ///
    /// Asking `instanceClasses` about the name directly is what made this unreliable inside a
    /// constructor: the receiver bindings are still being established there, so a PARAMETER
    /// answers with the class under construction rather than with its own. `machine.ADC`'s
    /// `__init__(self, pin: Pin)` reads `pin._name`, the lookup said `machine_ADC`, and a valid
    /// read was refused -- which is why the undefined-attribute check was scoped out of
    /// `__init__` entirely, and why `ADC(an_adc)` was then accepted and lowered against a slot
    /// nothing writes.
    ///
    /// The LAST answer along the chain is the right one, because the chain ends at the caller's
    /// value. `ADC(Pin("PC0"))` ends at the Pin, which has `_name`; `Wrap(ADC(Pin("PC0")))`
    /// ends at the ADC, which does not, and that is the program that used to compile into a
    /// 39-instruction comparison of a pin name against a byte of BSS.
    /// </summary>
    private string? ReceiverClassThroughAliases(string baseName)
    {
        string? answer = null;
        string cur = baseName;
        for (int depth = 0; depth < 20; depth++)
        {
            if (instanceClasses.TryGetValue(cur, out var c) && !string.IsNullOrEmpty(c)) answer = c;
            if (!variableAliases.TryGetValue(cur, out var next) || string.IsNullOrEmpty(next)) break;
            cur = next;
        }
        return answer;
    }

    /// <summary>
    /// True when `&lt;name&gt;.&lt;member&gt;` reads a FIELD OF A BOXED INSTANCE, so the slot
    /// read in <see cref="VisitMemberAccess"/> owns it and nothing before that branch may
    /// claim it.
    ///
    /// The test is the slot branch's own -- the instance has a slot, the instance has a class,
    /// and the class lays that member out at a byte offset -- so the guard and the branch it
    /// guards cannot disagree about which reads belong to the slot.
    ///
    /// What differs is only HOW THE NAME IS SPELLED. The slot branch runs after the object has
    /// been visited, so it holds the qualified name; the guard runs on the name as written. The
    /// four candidates below are the four spellings <c>SlotInstanceKey</c> can have produced
    /// when the instance was constructed, and each is walked through the alias chain the way
    /// the branch walks its own.
    /// </summary>
    private bool NamesABoxedField(string name, string member)
    {
        if (slotInstances.Count == 0) return false;

        foreach (string candidate in new[]
                 {
                     name,
                     currentInlinePrefix + name,
                     string.IsNullOrEmpty(currentFunction) ? name : currentFunction + "." + name,
                     currentModulePrefix + name,
                 })
        {
            string cur = candidate;
            for (int depth = 0; depth < 20; depth++)
            {
                if (slotInstances.ContainsKey(cur)
                    && instanceClasses.TryGetValue(cur, out var cls)
                    && TryGetSlotFieldOffset(cls, member, out _, out _))
                    return true;
                if (!variableAliases.TryGetValue(cur, out var next) || string.IsNullOrEmpty(next))
                    break;
                cur = next;
            }
        }
        return false;
    }

    /// <summary>
    /// The value of a CLASS-level attribute, reached through an instance of the class rather
    /// than through the class name (PyMCU#268).
    ///
    /// `Cls.ATTR` has always worked and `inst.ATTR` never did, on a program CPython runs. The
    /// instance read builds the flattened name `inst_ATTR`, which nothing registers, and the
    /// class's own namespace was never consulted -- so a class attribute was reachable by one
    /// spelling and reported as "has no attribute" by the other.
    ///
    /// This is the class-name lookup, asked once per class along the MRO, so the answer is the
    /// same value by either spelling: the folded constant for an attribute the scan could
    /// evaluate, the slot for one it could not, and the qualified name for one holding an
    /// instance (whose fields live under `&lt;name&gt;_&lt;field&gt;`, which is what the
    /// sub-prefix scan recognises).
    ///
    /// Bases are walked because class attributes do not inherit on their own: inheritance
    /// copies methods and leaves `globals` and `mutableGlobals` keyed under the base's own
    /// prefix, so `Sub.LIMIT` declared on `Base` is only ever found by asking `Base`.
    /// </summary>
    private Val? ClassAttributeThroughReceiver(string? baseName, string member)
    {
        if (!TryFindClassAttribute(baseName, member, out _, out var fullName)) return null;

        // A class-level ARRAY attribute (`_BUFFER = bytearray(8)` in the class body,
        // CircuitPython's shared scratch-buffer idiom, PyMCU#442) also registers a scalar
        // placeholder under `fullName` -- its bytearray initializer does not fold to a
        // constant. The real storage lives under the module-init-qualified spelling that
        // TryResolveArrayStorageKey normalizes to, and that is the name a whole-attribute
        // read must answer with: `f(self._BUFFER)` hands the callee an alias to the array,
        // not a copy of the placeholder.
        if (TryResolveArrayStorageKey(fullName, out var arrStorage))
            return new Variable(arrStorage,
                arrayElemTypes.TryGetValue(arrStorage, out var arrElem) ? arrElem : DataType.UINT8);

        if (globals.TryGetValue(fullName, out var sym))
            return sym.IsMemoryAddress
                ? new MemoryAddress(sym.Value, sym.Type)
                : new Constant(sym.Value);
        if (mutableGlobals.TryGetValue(fullName, out var t))
            return new Variable(fullName, t);
        // An attribute holding an instance has no value of its own: its fields were constructed
        // flattened under `<fullName>_<field>`, and the qualified name is what the method
        // dispatch and the field reads both key on.
        return new Variable(fullName, DataType.UINT8);
    }

    /// <summary>
    /// The class that declares a class-level attribute called <paramref name="member"/>, found
    /// by walking the receiver's class and its bases, and the key its value, slot or flattened
    /// fields are registered under.
    /// </summary>
    private bool TryFindClassAttribute(
        string? baseName, string member, out string declaringClass, out string fullName)
    {
        declaringClass = "";
        fullName = "";
        if (baseName == null || ReceiverClassThroughAliases(baseName) is not { } cls
            || string.IsNullOrEmpty(cls))
            return false;

        string? cur = cls;
        for (int depth = 0; cur != null && depth < 20; depth++)
        {
            // Two spellings, because a class reaches here two ways. A class in the file being
            // compiled is named simply and `classModuleMap` carries its module's prefix; a class
            // from an imported module arrives already mangled, prefix included, and is not in
            // that map at all. Asking only the first found every class attribute in a test and
            // none in a library.
            foreach (var key in new[]
            {
                classModuleMap.TryGetValue(cur, out var modPfx) ? modPfx + cur + "_" + member : null,
                cur + "_" + member,
            })
            {
                if (key == null) continue;
                if (globals.ContainsKey(key) || mutableGlobals.ContainsKey(key)
                    || instanceClasses.ContainsKey(key)
                    || dictLiteralBindings.ContainsKey(key)
                    || setLiteralBindings.ContainsKey(key)
                    || globals.Keys.Any(k => k.StartsWith(key + "_", StringComparison.Ordinal)))
                {
                    declaringClass = cur;
                    fullName = key;
                    return true;
                }
            }
            cur = BaseClassOf(cur);
        }
        return false;
    }

    /// <summary>
    /// The name a source-level receiver is keyed under, tried under the inline expansion's
    /// prefix, under the current function, and bare -- the first spelling that resolves to a
    /// class. `self` inside a method is never the bare name, so a descriptor written as
    /// `self.attr = v` in the driver's own constructor was missed by a bare lookup while
    /// `dev.attr = v` in main was found.
    /// </summary>
    private string? ReceiverNameForLookup(VariableExpr recv)
    {
        foreach (var cand in new[]
        {
            string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + recv.Name,
            string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + recv.Name,
            recv.Name,
        })
        {
            if (cand != null && ReceiverClassThroughAliases(cand) is { } c && !string.IsNullOrEmpty(c))
                return cand;
        }
        return null;
    }

    /// <summary>The class a class inherits from, or null. The prefix carries a trailing '_'.</summary>
    private string? BaseClassOf(string cls)
        => classBasePrefixes.TryGetValue(cls, out var bp) && !string.IsNullOrEmpty(bp)
            ? (bp.EndsWith("_", StringComparison.Ordinal) ? bp[..^1] : bp)
            : null;

    /// <summary>
    /// The source spelling of a class key, so the descriptor rewrite can name
    /// <c>type(inst)</c> as a VariableExpr that ResolveBinding knows.
    ///
    /// TryFindClassAttribute returns the class KEY. A class in the file being
    /// compiled is already that spelling (<c>Dev</c>); an imported class arrives
    /// mangled (<c>adafruit_ina219_INA219</c>). classNames and classModuleMap are
    /// keyed by the bare name, so <c>self.raw_bus_voltage</c> inside a method of
    /// the imported class was reported as "name 'adafruit_ina219_INA219' is not
    /// defined" -- the second argument of the synthesized <c>__get__</c> is
    /// <c>type(inst)</c>.
    /// </summary>
    private string ClassNameForDescriptorRewrite(string owner)
    {
        if (classNames.Contains(owner)) return owner;
        foreach (var (bare, pfx) in classModuleMap)
        {
            if (string.Equals((pfx ?? "") + bare, owner, StringComparison.Ordinal))
                return bare;
        }
        return owner;
    }

    /// <summary>
    /// The descriptor protocol (PyMCU#360): `inst.attr`, where `attr` is a class attribute whose
    /// class defines `__get__`, IS `type(inst).attr.__get__(inst, type(inst))`.
    ///
    /// The rewrite produces that spelling and lowers it, rather than reimplementing the call:
    /// the explicit form already compiles, so the implicit one cannot diverge from it. That is
    /// also why this is asked before the receiver is evaluated -- the rewrite evaluates the
    /// receiver itself, and one visited twice is emitted twice.
    ///
    /// A class attribute whose class defines no `__get__` is not a descriptor and keeps its
    /// #268 meaning: it is the object, and a method call on it reaches that object's method.
    /// </summary>
    private Val? TryDescriptorRead(string? baseName, Val receiver, MemberAccessExpr expr)
    {
        if (!TryFindClassAttribute(baseName, expr.Member, out var owner, out var fullName))
            return null;
        if (!instanceClasses.TryGetValue(fullName, out var attrCls)
            || !ClassDefinesMethod(attrCls, "__get__"))
            return null;

        string clsName = ClassNameForDescriptorRewrite(owner);
        var attr = new MemberAccessExpr(new VariableExpr(clsName) { Line = expr.Line }, expr.Member)
            { Line = expr.Line };
        return VisitCall(new CallExpr(
            new MemberAccessExpr(attr, "__get__") { Line = expr.Line },
            new List<Expression>
            {
                // The receiver has already been lowered on the way here; handing the expression
                // over again would emit it a second time.
                new PreEvaluatedExpr(receiver, null) { Line = expr.Line },
                new VariableExpr(clsName) { Line = expr.Line },
            })
            { Line = expr.Line });
    }

    /// <summary>
    /// True when <paramref name="expr"/> reads a class attribute whose class defines
    /// <c>__get__</c> -- the shape <c>TryDescriptorRead</c> rewrites. The check is the
    /// same, and separate, because a subscript site (<c>d.pair[k]</c>) needs the answer
    /// BEFORE it commits to the tuple-return evaluation, while TryDescriptorRead decides
    /// inside it.
    /// </summary>
    private bool IsDescriptorMemberRead(MemberAccessExpr expr)
    {
        if (expr.Object is not VariableExpr recv
            || ReceiverNameForLookup(recv) is not { } baseName)
            return false;
        return TryFindClassAttribute(baseName, expr.Member, out _, out var fullName)
            && instanceClasses.TryGetValue(fullName, out var attrCls)
            && ClassDefinesMethod(attrCls, "__get__");
    }

    /// <summary>
    /// The write half of the descriptor protocol (PyMCU#360): `inst.attr = v`, where `attr` is a
    /// class attribute whose class defines `__set__`, IS `type(inst).attr.__set__(inst, v)`.
    /// True when the assignment was rewritten and lowered, so the caller must not lower it again.
    /// </summary>
    private bool TryDescriptorWrite(string? baseName, Val receiver, MemberAccessExpr target, Val value)
    {
        if (!TryFindClassAttribute(baseName, target.Member, out var owner, out var fullName))
            return false;
        if (!instanceClasses.TryGetValue(fullName, out var attrCls)
            || !ClassDefinesMethod(attrCls, "__set__"))
            return false;

        string clsName = ClassNameForDescriptorRewrite(owner);
        var attr = new MemberAccessExpr(new VariableExpr(clsName) { Line = target.Line }, target.Member)
            { Line = target.Line };
        VisitCall(new CallExpr(
            new MemberAccessExpr(attr, "__set__") { Line = target.Line },
            new List<Expression>
            {
                // Both the receiver and the value are already lowered by the time the assignment
                // reaches here; handing either expression over again would emit it twice.
                new PreEvaluatedExpr(receiver, null) { Line = target.Line },
                new PreEvaluatedExpr(value, null) { Line = target.Line },
            }) { Line = target.Line });
        return true;
    }

    /// <summary>
    /// The same rewrite for a value that has no Val form: <c>inst.attr = (a, b)</c>,
    /// where <c>attr</c> is a class attribute whose class defines <c>__set__</c>
    /// (adafruit_register's <c>Struct</c>). A tuple is not a scalar, so the expression
    /// itself is handed to the call, which binds it to the <c>value</c> parameter as the
    /// sequence a tuple argument always becomes. Asked before the field-array path, which
    /// would otherwise swallow the write into storage the descriptor protocol never reads.
    /// </summary>
    private bool TryDescriptorSeqWrite(MemberAccessExpr target, Expression value)
    {
        var objVal = VisitExpression(target.Object);
        string? baseName = objVal is Variable v ? v.Name : (objVal is Temporary t ? t.Name : null);
        while (baseName != null && variableAliases.TryGetValue(baseName, out var alias)) baseName = alias;
        if (!TryFindClassAttribute(baseName, target.Member, out var owner, out var fullName)
            || !instanceClasses.TryGetValue(fullName, out var attrCls)
            || !ClassDefinesMethod(attrCls, "__set__"))
            return false;

        string clsName = ClassNameForDescriptorRewrite(owner);
        var attr = new MemberAccessExpr(new VariableExpr(clsName) { Line = target.Line }, target.Member)
            { Line = target.Line };
        VisitCall(new CallExpr(
            new MemberAccessExpr(attr, "__set__") { Line = target.Line },
            new List<Expression>
            {
                // The receiver was already lowered on the way here; handing the expression
                // over again would emit it a second time. The value is the tuple as written:
                // the __set__ expansion binds it to `value` as a sequence parameter.
                new PreEvaluatedExpr(objVal, null) { Line = target.Line },
                value,
            }) { Line = target.Line });
        return true;
    }

    // True when this reads a @property getter on a known instance: the receiver is a plain
    // name bound to a class that registers <member> as a getter. The MRO walk makes a getter
    // declared on a base class reachable from a subclass instance (`lcd.columns`).
    private bool IsPropertyGetterRead(MemberAccessExpr expr)
        => expr.Object is VariableExpr recv
           && InstanceClassOfName(recv.Name) is { } cls
           && ResolveMROPropertyClass(cls, expr.Member) is { } propCls
           && propertyGetters.Contains(propCls + "." + expr.Member);

    // The AST of `<instance>.<member>`, resolved through the MRO. Null when the name is not
    // an instance or its class has no such method.
    private FunctionDef? TryResolveInstanceMethodAst(string objName, string member)
    {
        if (InstanceClassOfName(objName) is not { } cls) return null;
        string sym = ResolveMROMethod(cls, member) + "_" + member;
        if (methodAstByName.TryGetValue(sym, out var fd)) return fd;
        return inlineFunctions.TryGetValue(sym, out var fd2) ? fd2 : null;
    }

    /// <summary>
    /// The compare-chain half of `x in seq` / `x not in seq`, given the element list. Folds
    /// to a constant when the left side and every element are compile-time-known; otherwise
    /// emits the short-circuit compare chain and returns its bool temp.
    /// </summary>
    private Val EmitConstSeqMembership(Val lhs, List<Frontend.Expression> rhsElems,
                                       bool negate, Frontend.Expression lhsExpr)
    {
        if (rhsElems.Count == 0) return new Constant(negate ? 1 : 0);

        var elems = new List<Val>();
        bool allConst = true;
        if (lhs is Constant lc)
        {
            foreach (var e in rhsElems)
            {
                Val ev = VisitExpression(e);
                if (ev is Constant ec)
                {
                    // Two Constants standing for STRINGS match on their text, not their
                    // value: the same one-character string is its character code in
                    // expression position and an interned id through a name, so
                    // `c in ("a", "b")` compared two spellings of "a" and answered false
                    // (#211). Both sides or neither, so a pair without text is compared
                    // exactly as before.
                    bool hit = lc.Text != null && ec.Text != null
                        ? lc.Text == ec.Text
                        : lc.Value == ec.Value;
                    if (hit) return new Constant(negate ? 0 : 1);
                }
                else allConst = false;

                elems.Add(ev);
            }

            if (allConst) return new Constant(negate ? 1 : 0);
        }
        else if (TryGetCompileTimeText(lhsExpr) is { } lhsStrText)
        {
            // `ORDER in {neopixel.RGB, neopixel.GRB}` -- a name bound to a
            // compile-time string reads back as a Variable over the interned
            // id, so the Constant fold above never saw it and emitted a
            // run-time compare chain for a question the compiler can answer
            // (wheel()'s conditional tuple return then refused on it). The
            // text is what membership compares: a str matches a str element
            // iff the texts match, and never matches a non-str constant --
            // drop both from the chain and let only genuinely run-time
            // elements keep a compare.
            foreach (var e in rhsElems)
            {
                Val ev = VisitExpression(e);
                string? eText = (ev as Constant)?.Text ?? TryGetCompileTimeText(e);
                if (eText != null)
                {
                    if (eText == lhsStrText) return new Constant(negate ? 0 : 1);
                    continue;   // a different compile-time string: never equal
                }
                if (ev is Constant) continue;   // a non-str constant: never equal
                elems.Add(ev);                  // run-time value: keep the compare
            }
            if (elems.Count == 0) return new Constant(negate ? 1 : 0);
        }
        else
        {
            foreach (var e in rhsElems) elems.Add(VisitExpression(e));
        }

        Temporary result = MakeTemp(DataType.UINT8);
        if (negate)
        {
            Temporary cmp = MakeTemp(DataType.UINT8);
            Emit(new Binary(PyMCU.IR.BinaryOp.NotEqual, lhs, elems[0], cmp));
            Emit(new Copy(cmp, result));
            for (int i = 1; i < elems.Count; ++i)
            {
                Temporary ci = MakeTemp(DataType.UINT8);
                Emit(new Binary(PyMCU.IR.BinaryOp.NotEqual, lhs, elems[i], ci));
                string endLbl = MakeLabel();
                Emit(new JumpIfZero(result, endLbl));
                Emit(new Copy(ci, result));
                Emit(new Label(endLbl));
            }
        }
        else
        {
            Temporary cmp = MakeTemp(DataType.UINT8);
            Emit(new Binary(PyMCU.IR.BinaryOp.Equal, lhs, elems[0], cmp));
            Emit(new Copy(cmp, result));
            for (int i = 1; i < elems.Count; ++i)
            {
                Temporary ci = MakeTemp(DataType.UINT8);
                Emit(new Binary(PyMCU.IR.BinaryOp.Equal, lhs, elems[i], ci));
                string endLbl = MakeLabel();
                Emit(new JumpIfNotZero(result, endLbl));
                Emit(new Copy(ci, result));
                Emit(new Label(endLbl));
            }
        }

        return result;
    }

    /// <summary>
    /// `pin not in self.pin_mapping.analog_pins`: the field is a class OBJECT, so the
    /// attribute is a compile-time tuple on every class it can hold and the field's tag
    /// byte picks which one applies at run time. One candidate folds with no tag at all.
    /// Returns null when <paramref name="outer"/> is not a class-object-field attribute.
    /// </summary>
    private Val? TryClassObjectAttrIn(MemberAccessExpr outer, Val lhs, bool negate,
                                      Frontend.Expression lhsExpr)
    {
        if (outer.Object is not MemberAccessExpr inner) return null;
        if (ClassObjectFieldClasses(inner) is not { } cands) return null;
        var perClass = new List<List<Frontend.Expression>>(cands.Count);
        foreach (var cand in cands)
        {
            if (!constSequenceBindings.TryGetValue(ClassAttrKey(cand, outer.Member), out var e))
                throw UserError(
                    $"field '{inner.Member}' can hold class {ShortClassName(cand)}, which has no "
                    + $"compile-time sequence attribute '{outer.Member}' to test membership in", outer);
            perClass.Add(e);
        }
        if (cands.Count == 1)
            return EmitConstSeqMembership(lhs, perClass[0], negate, lhsExpr);
        Val tag = VisitExpression(inner);
        Temporary acc = MakeTemp(DataType.UINT8);
        Emit(new Copy(new Constant(0), acc));
        for (int k = 0; k < cands.Count; k++)
        {
            Val mk = EmitConstSeqMembership(lhs, perClass[k], negate, lhsExpr);
            // acc |= (tag == k) && mk -- a folded mk contributes its arm's compare or nothing.
            if (mk is Constant cmk)
            {
                if (cmk.Value == 0) continue;
                Temporary condOnly = MakeTemp(DataType.UINT8);
                Emit(new Binary(PyMCU.IR.BinaryOp.Equal, tag, new Constant(k), condOnly));
                Emit(new Binary(PyMCU.IR.BinaryOp.BitOr, acc, condOnly, acc));
                continue;
            }
            Temporary ck = MakeTemp(DataType.UINT8);
            Emit(new Binary(PyMCU.IR.BinaryOp.Equal, tag, new Constant(k), ck));
            Temporary both = MakeTemp(DataType.UINT8);
            Emit(new Binary(PyMCU.IR.BinaryOp.BitAnd, ck, mk, both));
            Emit(new Binary(PyMCU.IR.BinaryOp.BitOr, acc, both, acc));
        }
        return acc;
    }

    /// <summary>
    /// Select between per-candidate values on the field's tag byte: `vals[k]` is what the
    /// expression means when the field holds `cands[k]`. The last arm is the fall-through --
    /// the tag is one of the candidates by construction.
    /// </summary>
    private Val EmitClassObjectSelect(MemberAccessExpr fieldAccess, List<string> cands,
                                      List<Val> vals)
    {
        if (cands.Count == 1) return vals[0];
        Val tag = VisitExpression(fieldAccess);
        DataType dt = DataType.UINT8;
        foreach (var v in vals)
        {
            if (v is Constant cv && cv.Value > 255) dt = DataType.UINT16;
            if (v is Variable { Type: DataType.UINT16 or DataType.UINT32 }
                     or Temporary { Type: DataType.UINT16 or DataType.UINT32 })
                dt = DataType.UINT16;
        }
        Temporary result = MakeTemp(dt);
        string done = MakeLabel();
        for (int k = 0; k < cands.Count; k++)
        {
            string? next = k < cands.Count - 1 ? MakeLabel() : null;
            if (next != null) Emit(new JumpIfNotEqual(tag, new Constant(k), next));
            Emit(new Copy(vals[k], result));
            if (next != null) { Emit(new Jump(done)); Emit(new Label(next)); }
        }
        Emit(new Label(done));
        return result;
    }

    /// <summary>
    /// `self.pin_mapping.pwm_width` as a VALUE: the per-candidate attribute, selected on the
    /// tag byte when more than one class can be bound. A sequence attribute in this position
    /// and an attribute missing on a candidate are both located refusals -- the field's tag
    /// cannot carry a tuple, and a class without the attribute is what CPython would raise on.
    /// </summary>
    private Val ClassObjectAttrValue(MemberAccessExpr expr, MemberAccessExpr inner,
                                     List<string> cands)
    {
        var vals = new List<Val>(cands.Count);
        foreach (var cand in cands)
        {
            string akey = ClassAttrKey(cand, expr.Member);
            if (globals.TryGetValue(akey, out var g))
                vals.Add(new Constant(g.Value));
            else if (mutableGlobals.TryGetValue(akey, out var mgt))
                vals.Add(new Variable(akey, mgt));
            else if (constSequenceBindings.ContainsKey(akey))
                throw UserError(
                    $"attribute '{expr.Member}' of field '{inner.Member}' is a compile-time "
                    + $"sequence on class {ShortClassName(cand)} -- use `x in`, `.index()` or a "
                    + "subscript to read it; the field's tag cannot carry a tuple", expr);
            else
                throw UserError(
                    $"field '{inner.Member}' can hold class {ShortClassName(cand)}, which has "
                    + $"no attribute '{expr.Member}'", expr);
        }
        if (vals[0] is Constant first && vals.All(v => v is Constant c && c.Value == first.Value))
            return first;
        return EmitClassObjectSelect(inner, cands, vals);
    }

    /// A candidate class's name for diagnostics: strip the module prefix a mangled key
    /// carries, keep a bare name (which may itself contain underscores) whole.
    private string ShortClassName(string clsKey)
    {
        if (classNames.Contains(clsKey)) return clsKey;
        foreach (var (bare, pfx) in classModuleMap)
            if (string.Equals((pfx ?? "") + bare, clsKey, StringComparison.Ordinal))
                return bare;
        int i = clsKey.LastIndexOf('_');
        return i < 0 ? clsKey : clsKey[(i + 1)..];
    }

    /// <summary>
    /// Whether <paramref name="e"/> is a bare name denoting one of <paramref name="mods"/> --
    /// the literal module name or an alias bound to it by `import &lt;mod&gt; as &lt;name&gt;`
    /// (importedAliases maps the used name to the real module). `import usys as s` then
    /// `s.platform` must fold to the same table `usys.platform` does.
    /// </summary>
    private bool IsModuleAlias(Expression? e, params string[] mods) =>
        e is VariableExpr { Name: var n }
        && (mods.Contains(n) || (importedAliases.TryGetValue(n, out var real) && mods.Contains(real)));

    /// <summary>The callee shapes `uname()` / `os.uname()` / `uos.uname()` take.</summary>
    private bool IsUnameCallee(Expression callee) => callee switch
    {
        VariableExpr { Name: "uname" } => true,
        MemberAccessExpr { Member: "uname" } mem => IsModuleAlias(mem.Object, "os", "uos"),
        _ => false,
    };

    private Val VisitMemberAccess(MemberAccessExpr expr)
    {
        // `cls.string` inside a @classmethod: cls is the receiver class.
        if (expr.Object is VariableExpr clsVe && ClassmethodClsOf(clsVe.Name) is { } mappedCls)
            expr = new MemberAccessExpr(new VariableExpr(mappedCls), expr.Member)
                { Line = expr.Line, Column = expr.Column, Length = expr.Length };

        // `__CHIP__.name` / `.arch` / `.board` are compile-time strings (DeviceConfig),
        // the same facts CompileTimeEvaluator already folds in `if` / `match`. Using
        // them as a VALUE (`uname_result(..., __CHIP__.name)`, #466) used to be
        // refused as "object has no attribute 'name'" because the IR path treated
        // `__CHIP__` as an ordinary instance.
        if (expr.Object is VariableExpr { Name: "__CHIP__" }
            && ChipFactString(expr.Member) is { } chipFact)
            return InternedStringConstant(chipFact);

        // RFC 0007: `sys.platform`, `sys.implementation.name` and `uname().<field>`
        // (in their `usys`/`uos` spellings too) are compile-time facts the compat
        // layer's sys.py/os.py only declares for IDEs -- the values in those files
        // are placeholders. CompileTimeEvaluator already substitutes the table in
        // `if`/`match`/`try` conditions; a read anywhere else (`p = sys.platform`)
        // used to reach the placeholder, so the same program answered the same
        // question two different ways. Substitute the table here too, the way
        // `sys.implementation.version[i]` is already substituted below. Guarded by
        // IsKnownStdlib: with no compat layer declared, `sys`/`os` are whatever the
        // project made them (pymcu.os's uname() really does run inline) and the
        // member must resolve normally, not to a table meant for another module.
        if (IntrospectionTable.IsKnownStdlib(deviceConfig.Stdlib))
        {
            if (expr is MemberAccessExpr { Member: "name", Object: MemberAccessExpr { Member: "implementation" } implObj }
                && IsModuleAlias(implObj.Object, "sys", "usys"))
                return InternedStringConstant(IntrospectionTable.ImplementationName(deviceConfig));

            if (expr is MemberAccessExpr { Member: "platform" }
                && IsModuleAlias(expr.Object, "sys", "usys"))
                return InternedStringConstant(IntrospectionTable.SysPlatform(deviceConfig));

            // `sys.implementation.version` bare: the (major, minor, micro) tuple has no
            // runtime object, so a read that is not `version[i]` cannot be answered --
            // index it (this is the member-access half of the IndexExpr rule below).
            if (expr is MemberAccessExpr { Member: "version", Object: MemberAccessExpr { Member: "implementation" } verObj }
                && IsModuleAlias(verObj.Object, "sys", "usys"))
                throw UserError(
                    "'sys.implementation.version' is a compile-time (major, minor, micro) tuple -- "
                    + "index it with an integer literal (sys.implementation.version[0])", expr);

            if (expr is { Object: CallExpr { Args.Count: 0 } unameCall, Member: var unameField }
                && IsUnameCallee(unameCall.Callee))
            {
                var u = IntrospectionTable.GetUname(deviceConfig);
                return unameField switch
                {
                    "sysname" => InternedStringConstant(u.Sysname),
                    "nodename" => InternedStringConstant(u.Nodename),
                    "release" => InternedStringConstant(u.Release),
                    "version" => InternedStringConstant(u.Version),
                    "machine" => InternedStringConstant(u.Machine),
                    _ => throw UserError(
                        $"'uname()' has no field '{unameField}' -- the fields are sysname, " +
                        "nodename, release, version, machine", expr),
                };
            }
        }

        // A constant two class names deep: `Outer.Inner.A`, where one level works. The access
        // was resolved one hop at a time, so `Outer.Inner` was asked for as an attribute of
        // `Outer` and refused with "object has no attribute 'Inner'" -- a sentence about the
        // hop, not about the name. The scan files a nested class's constants under the joined
        // prefix, so the whole dotted path IS the name (#319). It is how CircuitPython spells
        // the UART parity: `busio.UART.Parity.ODD`, which adds a module hop in front.
        if (TryDottedClassConstant(expr) is { } dottedConst) return dottedConst;

        // `self.pin_mapping.pwm_width`: the field holds a class OBJECT, so the attribute is
        // the one each candidate class declares. A sequence attribute has no scalar value --
        // `in` / `.index()` / a subscript handle it; reaching for it as a value is refused
        // here, where the refusal can name the field and the class.
        if (expr.Object is MemberAccessExpr coInner
            && ClassObjectFieldClasses(coInner) is { } coCands)
            return ClassObjectAttrValue(expr, coInner, coCands);

        // `e.__cause__` / `e.__context__` (#434). The chain is not recorded, because
        // `raise X() from Y` compiles as `raise X()` -- there is nothing for either
        // attribute to point at. Named here, ahead of the generic "not defined"
        // fallback, so the diagnostic says which attribute and why.
        if (expr.Object is VariableExpr causeVe
            && (expr.Member is "__cause__" or "__context__")
            && TryGetExceptionBinding(causeVe.Name, out _))
        {
            throw UserError(
                $"'{causeVe.Name}.{expr.Member}' is not kept: PyMCU does not record an "
                + "exception chain, so there is nothing for either attribute to point at",
                expr);
        }

        // A single-field instance handed back by a factory IS its one field: the call returns
        // the field's value in a register and the name is bound to that (RFC 0001 Model B
        // handle). A method call on it already knew that; a direct field READ did not, and
        // resolved to a per-field name nobody ever wrote, so `o.a` came back as zero while
        // `o.g()` answered correctly on the very next line.
        if (expr.Object is VariableExpr handleVe && HandleFieldRead(handleVe.Name, expr.Member) is { } handleVal)
            return handleVal;

        // RFC 0001 Model B (SRAM slot): inside a slot method, `self.<field>` reads from the
        // instance slot via the `self` pointer at the field's byte offset. Guard with an empty
        // inline prefix: when ANOTHER method is inlined into this outlined slot method (e.g.
        // machine.Pin.mode inside a user slot method), its `self` is a DIFFERENT instance, not
        // currentFunction's slot self -- using currentFunction's offsets here would read the
        // wrong field's class and (for a same-named method) recurse forever.
        // The method whose `self` is in scope is the INNERMOST inline frame (its CalleeName),
        // falling back to the outlined function being compiled -- NOT currentFunction, which is the
        // outer outline when another method is inlined into it. This is what makes self.<field>
        // resolution frame-aware: `self` inside an inlined machine.Pin.mode is machine.Pin's self,
        // not the user slot method's, so it reads machine.Pin's _pin (hal.Pin), not the user
        // field -- without it, a same-named method (mode->mode) recurses forever.
        string frameMethod = inlineStack.Count > 0 && !string.IsNullOrEmpty(inlineStack[^1].CalleeName)
            ? inlineStack[^1].CalleeName : currentFunction;
        // The slot read uses currentFunction (unchanged), with ONE narrow exception: when a NON-slot
        // method like machine.Pin.mode is inlined into an outlined slot method, its `self` shadows
        // the outline's, so reading the outline's same-named field here would be wrong (and for a
        // same-named method, recurse forever). Detect exactly that case -- the innermost inline frame
        // is a different method that is itself NOT a slot method -- and skip, letting self.<field>
        // fall to the frame-aware Model-A recovery. Every existing path (outline body, inlined slot
        // method) is untouched.
        bool innerNonSlotShadow = frameMethod != currentFunction
            && !slotMethodFieldOffsets.ContainsKey(frameMethod);
        if (expr.Object is VariableExpr selfVe && selfVe.Name == "self" && !innerNonSlotShadow
            && slotMethodFieldOffsets.TryGetValue(currentFunction, out var fieldOffs)
            && fieldOffs.TryGetValue(expr.Member, out int fieldOff))
        {
            // RFC 0009 Model B: a union field's slot record carries the tag byte
            // next to the payload -- the read returns a tagged val.
            var mthCls = methodInstanceTypes.GetValueOrDefault(currentFunction);
            if (mthCls != null && IsUnionField(mthCls, expr.Member, out var mthDecl))
            {
                var mPayTy = mthDecl != null ? UnionPayloadType(mthDecl) : DataType.FLOAT;
                return EmitSlotUnionFieldLoad(currentFunction + ".self", true, fieldOff, mPayTy,
                    fieldOff + mPayTy.SizeOf(), mthCls, expr.Member, 0);
            }
            return TagSlotFieldClass(
                EmitSlotFieldLoad(currentFunction + ".self", true, fieldOff,
                    SlotMethodFieldType(currentFunction, expr.Member), 0),
                mthCls, expr.Member);
        }

        // RFC 0001 Model B (Class[N]): a direct field read on an instance-array element,
        // `arr[i].x`. Compute the element field address and load through it. Without this the
        // member access fell through to a flattened name and read 0.
        if (expr.Object is IndexExpr iaIdxRead
            && TryInstanceArrayFieldAddr(iaIdxRead, expr.Member, out var iaFieldTy,
                out var iaCls) is { } iaAddr)
        {
            // A union field's record is payload + trailing tag byte.
            if (iaCls != null && IsUnionField(iaCls, expr.Member, out _))
                return EmitSlotUnionFieldLoadAddr(iaAddr, iaFieldTy, iaCls, expr.Member);
            Temporary iaLoaded = MakeTemp(iaFieldTy);
            Emit(new LoadIndirect(iaAddr, iaLoaded));
            return iaLoaded;
        }

        // A field of a BOXED instance is answered by the slot branch below, never by the
        // module-attribute mangling that follows.
        //
        // That mangling joins a base and a member with an underscore to reach a module's
        // global (`machine.mem8` -> `machine_mem8`), and instance-field flattening spells its
        // names the same way, so `c._t` mangled to `c__t` -- which `mutableGlobals` always
        // carries a placeholder for, whether or not the class was later boxed into an SRAM
        // slot. The branch matched and RETURNED, about two hundred lines before the slot read
        // that knows better could run.
        //
        // The flattened name is written when at least one field is initialised from something
        // other than a bare constructor parameter, because that shape takes the materialising
        // path through `__init__`. When EVERY field comes straight from a parameter, the fast
        // construction path writes the slot and only the slot, so the flattened name is never
        // written and the read answers 0. Two classes of the same shape in one program, one
        // right and one wrong, which is what made it hard to see (#409).
        //
        // Guarded here rather than by gating the mangling on `varExpr` naming a module: the
        // test is the slot branch's own, so the two cannot disagree about which reads belong
        // to it.
        if (expr.Object is VariableExpr slotVe && NamesABoxedField(slotVe.Name, expr.Member))
        {
            // Fall through to the slot read below.
        }
        else if (expr.Object is VariableExpr varExpr
                 && (InstanceClassOfName(varExpr.Name) == null
                     ? !LocalScopeBinds(varExpr.Name)
                     : NamesAModuleMember(varExpr.Name, expr.Member)))
        {
            // Resolve a module alias (import machine as m) to the real module name so
            // `m.Pin` / `m.Pin.OUT` mangle to machine_Pin..., not the unknown m_Pin.
            // A parameter of the same name as the alias is the parameter when we
            // already know its class (`framebuf.stride` inside set_pixel). A name
            // we have not typed yet still takes the module path unless the current
            // scope binds it -- otherwise `thing = thing.Thing()` (#467) would skip
            // the constructor. Skipped when the name has been rebound to an
            // instance AND the member is not on the module: `from adafruit_motor
            // import servo` then `servo = servo.Servo(pwm)` then
            // `print(servo.fraction)` is a field read. A module-level singleton
            // (`alarm.time`) stays a module member even if `import time` filed
            // `alarm` as an instance (#381).
            //
            // `.Replace('.', '_')`: a SUBMODULE import (`import adafruit_mcp3xxx.mcp3008 as
            // MCP`) resolves realModName to the full dotted path, and every OTHER module-name
            // mangling in the compiler (the class-construction factory case, VisitCall's own
            // module-member lookup) converts its dots to underscores before appending the
            // member -- this one did not, so `MCP.P0` (a plain module-level int constant in
            // mcp3008.py) mangled to the literal "adafruit_mcp3xxx.mcp3008_P0", a name that
            // still has a dot in it and matches nothing `globals` was ever keyed under
            // (registered as "adafruit_mcp3xxx_mcp3008_P0", the module's own scan prefix).
            string moduleBase = modules.ContainsKey(varExpr.Name)
                && TryImportedAlias(varExpr.Name, out var realModName) && realModName != null
                ? realModName.Replace('.', '_') : varExpr.Name;
            string mangledName = moduleBase + "_" + expr.Member;

            // A bare name inside a body binds in the DEFINING module's namespace
            // first: machine.py's own `Pin.IN` is machine_Pin_IN, however the
            // caller spelled the import. The flat globals below are the entry
            // module's ("" prefix), importedAliases answers for the CALLER's
            // `from machine import Pin`, and classModuleMap keeps whichever
            // module scanned the bare class name last (Pin, I2C, SPI, UART all
            // exist in HAL, compat and board modules). Under `import machine`
            // every fallback sent `Pin.IN` inside machine.Pin.__init__ to the
            // HAL's Pin, which answers 1, and inverted every translated mode.
            string ownPfx = OwningModulePrefix();
            if (ownPfx.Length > 0 && !modules.ContainsKey(varExpr.Name))
            {
                string ownMangled = ownPfx + varExpr.Name + "_" + expr.Member;
                if (globals.TryGetValue(ownMangled, out var sym0))
                {
                    if (sym0.IsMemoryAddress) return new MemoryAddress(sym0.Value, sym0.Type);
                    return new Constant(sym0.Value);
                }

                if (mutableGlobals.TryGetValue(ownMangled, out var t0))
                    return new Variable(ownMangled, t0);
            }

            // A module's own `from other import name` re-export -- the compat shims'
            // `from .sys import maxsize` inside usys.py: the name binds to the DEFINING
            // module in the re-exporting module's own import table, so no `usys_maxsize`
            // global ever exists. Chase it the same way ResolveReExport chases a
            // facade: `usys.maxsize` resolves to `sys_maxsize`.
            if (!globals.ContainsKey(mangledName) && !mutableGlobals.ContainsKey(mangledName)
                && modules.ContainsKey(varExpr.Name)
                && perModuleImportedAliases.TryGetValue(moduleBase + "_", out var reExports)
                && reExports.TryGetValue(expr.Member, out var reExportedMod) && reExportedMod != null)
            {
                // The member can BE a submodule (`alarm.time` where the package's
                // `from . import time` binds `time` to module `alarm.time`): its
                // spelling is the module's own mangled name -- appending the member
                // again produced `alarm_time_time` and the placeholder below then
                // named the wrong variable.
                bool memberIsSubmodule = modules.ContainsKey(reExportedMod)
                    && reExportedMod.EndsWith("." + expr.Member, StringComparison.Ordinal);
                mangledName = memberIsSubmodule
                    ? reExportedMod.Replace('.', '_')
                    : reExportedMod.Replace('.', '_') + "_" + expr.Member;
            }

            if (globals.TryGetValue(mangledName, out var sym))
            {
                if (sym.IsMemoryAddress) return new MemoryAddress(sym.Value, sym.Type);
                return new Constant(sym.Value);
            }

            if (mutableGlobals.TryGetValue(mangledName, out var type))
            {
                // A module-level string constant (`__version__ = "0.0.0+auto.0"`) lives
                // in strConstantVariables, never in the slot -- answer the text or the
                // read streams the 0-initialised byte as a number. A global bound to
                // DIFFERENT texts by the module's functions is runtime-dispatched --
                // folding one of its texts here would hide that.
                if (!multiStrVariables.ContainsKey(mangledName)
                    && strConstantVariables.TryGetValue(mangledName, out var modStrText))
                    return InternedStringConstant(modStrText);

                // `mod.state` where the module's own functions bind it to different texts: the
                // slot holds an interned id, not a number the program means (see VisitVariable).
                if (multiStrHandleReads == 0 && !strConstantVariables.ContainsKey(mangledName)
                    && multiStrVariables.TryGetValue(mangledName, out var modStrValues))
                    throw MultiStrUseError(varExpr.Name + "." + expr.Member, modStrValues, expr);

                return new Variable(mangledName, type);
            }

            if (modules.ContainsKey(varExpr.Name))
            {
                if (functionParams.ContainsKey(mangledName) || functionReturnTypes.ContainsKey(mangledName))
                {
                    return new Variable(mangledName, DataType.UINT8);
                }

                string classPrefix = mangledName + "_";
                foreach (var key in globals.Keys)
                {
                    if (key.StartsWith(classPrefix)) return new Variable(mangledName, DataType.UINT8);
                }

                // A module-level singleton (`alarm.time = _TimeAlarmModule()`) is an
                // instance, not a global constant. Without this the member was unknown
                // even after NamesAModuleMember admitted the hop (#381).
                if (instanceClasses.ContainsKey(mangledName)
                    || classFieldLayout.ContainsKey(mangledName)
                    || inlineFunctions.ContainsKey(mangledName + "___init__")
                    || overloadedFunctions.Contains(mangledName + "___init__"))
                    return new Variable(mangledName, DataType.UINT8);

                // `alarm.time` where `time` is a submodule file of package `alarm`:
                // the import registered the dotted module under its full name, not
                // as a member symbol of `alarm`. The placeholder keeps
                // `alarm.time.TimeAlarm` resolving to alarm_time_TimeAlarm.
                if (modules.ContainsKey(varExpr.Name + "." + expr.Member))
                    return new Variable(mangledName, DataType.UINT8);

                throw UserError("Unknown module member: " + mangledName, expr);
            }

            if (functionParams.ContainsKey(mangledName) || functionReturnTypes.ContainsKey(mangledName))
            {
                return new Variable(mangledName, DataType.UINT8);
            }

            if (TryImportedAlias(varExpr.Name, out var modName))
            {
                var originalName = AliasOriginal(varExpr.Name);
                var modPrefix = modName?.Replace('.', '_');
                var modMangled = modPrefix + "_" + expr.Member;
                if (globals.TryGetValue(modMangled, out var sym2))
                {
                    if (sym2.IsMemoryAddress) return new MemoryAddress(sym2.Value, sym2.Type);
                    return new Constant(sym2.Value);
                }

                string classMangled = modPrefix + "_" + originalName + "_" + expr.Member;
                if (globals.TryGetValue(classMangled, out var sym3))
                {
                    if (sym3.IsMemoryAddress) return new MemoryAddress(sym3.Value, sym3.Type);
                    return new Constant(sym3.Value);
                }
            }

            if (classModuleMap.TryGetValue(varExpr.Name, out var modPfx))
            {
                string fullName = modPfx + varExpr.Name + "_" + expr.Member;
                if (globals.TryGetValue(fullName, out var sym4))
                {
                    if (sym4.IsMemoryAddress) return new MemoryAddress(sym4.Value, sym4.Type);
                    return new Constant(sym4.Value);
                }

                if (mutableGlobals.TryGetValue(fullName, out var t2)) return new Variable(fullName, t2);
                string subPrefix = fullName + "_";
                foreach (var key in globals.Keys)
                {
                    if (key.StartsWith(subPrefix)) return new Variable(fullName, DataType.UINT8);
                }
            }
        }

        // `.value` is the pointer read (`p.value` on a ptr[T]), but it is also an ordinary
        // property name -- digitalio.DigitalInOut.value is THE CircuitPython idiom. Let a
        // registered getter win: without this the pointer path ran on a plain instance,
        // found no address to load from and handed back the instance itself, so `led.value`
        // silently read the pin id instead of the pin, and `led.value = not led.value`
        // never toggled anything.
        if (expr.Member == "value" && propertyGetters.Count > 0 && IsPropertyGetterRead(expr))
            return VisitCall(new CallExpr(expr, new List<Expression>()));

        if (expr.Member == "value" && !IsKnownInstanceField(expr.Object, "value"))
        {
            Val obj = VisitExpression(expr.Object);

            // Runtime pointer (ptr(<runtime addr>), e.g. ptr(BASE + x)): read the value at
            // the held address with a LoadIndirect rather than via a compile-time address.
            string? rpn = obj switch { Variable rpv => rpv.Name, Temporary rpt => rpt.Name, _ => null };
            if (rpn != null && runtimePtrVars.TryGetValue(rpn, out var rpElem))
            {
                // Prefer the annotated variable's element width over the bare ptr()
                // temp's UINT8 default (mirrors the .value write path in Assign.cs).
                string? declName = expr.Object is VariableExpr dvo ? dvo.Name : null;
                if (declName != null)
                {
                    foreach (var k in new[]
                    {
                        string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + declName,
                        string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + declName,
                        declName,
                    })
                    {
                        if (k != null && runtimePtrVars.TryGetValue(k, out var declElem))
                        {
                            rpElem = declElem;
                            break;
                        }
                    }
                }
                Temporary ld = MakeTemp(rpElem);
                Emit(new LoadIndirect(obj, ld, rpElem));
                return ld;
            }

            DataType varType = DataType.UINT8;
            if (obj is Variable v)
            {
                // Resolve local ptr[T] compile-time constant address variable
                if (constantAddressVariables.TryGetValue(v.Name, out int ptrAddr))
                {
                    DataType elemType = DataType.UINT8;
                    if (variableTypes.TryGetValue(v.Name, out var et)) elemType = et;
                    return new MemoryAddress(ptrAddr, elemType);
                }
                if (variableTypes.TryGetValue(v.Name, out var vt)) varType = vt;
                obj = v with { Type = varType };
            }

            return obj;
        }

        var objVal = VisitExpression(expr.Object);
        var baseName = objVal is Variable vv ? vv.Name : (objVal is Temporary tt ? tt.Name : "");

        // (see NamesABoxedField, used by the guard above)

        // string_constant.name → the string itself (e.g. cs="PB2" → cs.name == "PB2")
        // This supports passing a bare pin-name string where a Pin typed param is expected.
        if (string.IsNullOrEmpty(baseName) && objVal is Constant strConst &&
            expr.Member == "name" && stringIdToStr.ContainsKey(strConst.Value))
        {
            return strConst;
        }

        if (string.IsNullOrEmpty(baseName))
        {
            // Inline single-field method: `self.<field>` where self folded to its scalar value
            // (e.g. a constant pin) so the evaluated value has no name. If self's class -- set by
            // the force-inline binding -- is a single-field class whose only field is this member,
            // then self.<field> IS self: yield self's value, re-tagged with the field's class when
            // the field is itself a class, else returned as the bare scalar.
            if (expr.Object is VariableExpr selfVe2)
            {
                string selfKey = currentInlinePrefix + selfVe2.Name;
                while (variableAliases.TryGetValue(selfKey, out var a2)
                       && !(a2 != null && Temporary.IsScratchName(a2))) selfKey = a2!;
                if (instanceClasses.TryGetValue(selfKey, out var selfCls2) && selfCls2 != null
                    && classFieldLayout.TryGetValue(selfCls2, out var lay2) && lay2.Count == 1
                    && lay2[0].Field == expr.Member)
                {
                    if (fieldClasses.TryGetValue(selfCls2 + "|" + expr.Member, out var fc2)
                        && ResolveConcreteClass(fc2) is { } cc2)
                    {
                        var t2 = MakeTemp(DataType.UINT8);
                        Emit(new Copy(objVal, t2));
                        instanceClasses[t2.Name] = cc2;
                        if (classFieldLayout.TryGetValue(cc2, out var l3) && l3.Count == 1)
                            factoryHandleInstances.Add(t2.Name);
                        return t2;
                    }
                    return objVal;   // scalar single field: self IS the value
                }
            }
            // An object-typed field is the other nameless receiver: `self.a.x` where `a`
            // holds an instance -- the anchor has no scalar, but its name is the prefix
            // the member's own storage flattens under.
            if (AnchorNameOf(expr.Object) is { } anchorName)
                baseName = anchorName;
            else
                throw UserError("Unknown member access: " + expr.Member, expr);
        }
        while (baseName != null && variableAliases.TryGetValue(baseName, out var next))
        {
            if (next != null && Temporary.IsScratchName(next)) break;
            baseName = next;
        }

        // `obj.prop = None` marked this member None-valued at the write: a read answers
        // the mark. Expanding the getter anyway would hand back the declared-type result
        // slot, which a `Copy(NoneVal)` leaves holding whatever was there before -- the
        // disabled `s.angle` read back as 180.0 of residue. Properties only: a marked
        // FIELD (`self._font = None` in __init__) still reads as its storage -- member
        // access off it (`self._font.font_name`) resolves through the field's class.
        if (baseName != null && noneValuedNames.Contains(baseName + "_" + expr.Member)
            && instanceClasses.TryGetValue(baseName, out var markedCls) && markedCls != null
            && ResolveMROPropertyClass(markedCls, expr.Member) is { } markedPropCls
            && propertyGetters.Contains(markedPropCls + "." + expr.Member))
            return new NoneVal();

        // @property getter: a bare `obj.prop` read where `prop` is a registered getter on the
        // instance's class is desugared into a call to the getter method. Without this it would
        // fall through to a non-existent flattened `<base>_<prop>` data field and read 0.
        // The MRO walk reaches a getter inherited from a base class.
        if (baseName != null && propertyGetters.Count > 0
            && instanceClasses.TryGetValue(baseName, out var getterCls) && getterCls != null
            && ResolveMROPropertyClass(getterCls, expr.Member) is { } getterPropCls
            && propertyGetters.Contains(getterPropCls + "." + expr.Member))
        {
            return VisitCall(new CallExpr(expr, new List<Expression>()));
        }

        // RFC 0001 Model B (SRAM slot): a direct field read on a slot instance OUTSIDE a method
        // (`p.x` where p is a multi-field ZCA) must load from the instance slot. Without this it
        // fell through to a flattened `p_x` variable that no store ever wrote -- a 0 read, or an
        // undefined-symbol link error once the dead var was DCE'd.
        if (baseName != null && slotInstances.TryGetValue(baseName, out var slotArrR)
            && instanceClasses.TryGetValue(baseName, out var slotClsR)
            && TryGetSlotFieldLayout(slotClsR, expr.Member, out int slotOffR, out DataType slotTyR,
                out int slotTagR))
        {
            // The slot is a direct SRAM array here (not a pointer as inside a method), so use a
            // byte-offset ArrayLoad -- matching EmitSlotConstruction's ArrayStore. (A BytearrayLoad
            // would dereference main.p__slot as a pointer and read 0.) Multi-byte fields assemble
            // from consecutive bytes. A union field also loads its tag byte, so the val the read
            // returns carries the member choice.
            int slotTotR = arraySizes.TryGetValue(slotArrR, out var tszR) ? tszR : 0;
            if (slotTagR >= 0)
                return EmitSlotUnionFieldLoad(slotArrR, false, slotOffR, slotTyR, slotTagR,
                    slotClsR, expr.Member, slotTotR);
            return TagSlotFieldClass(
                EmitSlotFieldLoad(slotArrR, false, slotOffR, slotTyR, slotTotR),
                slotClsR, expr.Member);
        }

        var flattenedName = baseName + "_" + expr.Member;

        if (constantVariables.TryGetValue(flattenedName, out int cv)) return new Constant(cv);
        if (constantAddressVariables.TryGetValue(flattenedName, out int ca))
            return new MemoryAddress(ca,
                variableTypes.TryGetValue(flattenedName, out var caDt) ? caDt : DataType.UINT16);

        // Nested single-field ZCA field access: `obj.theOnlyField` where obj is a known single-
        // field class whose only field is itself a class (machine.Pin._pin -> hal.Pin). The
        // instance collapsed to a scalar so `obj.field` IS obj; re-tag it with the nested class.
        // Guarded tightly: only a single-field class with a CLASS-typed field, and only when the
        // flattened var carries no class and is no real global -- i.e. only when the normal path
        // would otherwise fail (so the construction-time `X__pin` case is left untouched).
        if (baseName != null
            && instanceClasses.TryGetValue(baseName, out var sfCls) && sfCls != null
            && classFieldLayout.TryGetValue(sfCls, out var sfLay) && sfLay.Count == 1
            && sfLay[0].Field == expr.Member
            && fieldClasses.TryGetValue(sfCls + "|" + expr.Member, out var sfNestedRaw)
            && ResolveConcreteClass(sfNestedRaw) is { } sfNested
            && !instanceClasses.ContainsKey(flattenedName)
            && !globals.ContainsKey(flattenedName))
        {
            var sfTy = objVal switch { Variable sv => sv.Type, Temporary st => st.Type, _ => DataType.UINT8 };
            var sfTmp = MakeTemp(sfTy);
            Emit(new Copy(objVal, sfTmp));
            instanceClasses[sfTmp.Name] = sfNested;
            // The nested class is itself single-field (its instance IS this scalar), so mark the
            // temp a handle instance: a Model-A method call on it (pulse_in) then passes the value
            // directly as the field arg instead of re-visiting (temp)._field, which has no home.
            if (classFieldLayout.TryGetValue(sfNested, out var nestLay) && nestLay.Count == 1)
                factoryHandleInstances.Add(sfTmp.Name);
            return sfTmp;
        }

        // Member access on a plain numeric scalar (e.g. `x.foo` where x: uint8) is invalid:
        // .value/.name are handled above, ZCA instance fields resolve through instanceClasses,
        // pointers through constantAddressVariables/runtimePtrVars — so a numeric-typed base
        // here means the member would fabricate an undefined `<base>_<member>` that reads as 0.
        if (!globals.ContainsKey(flattenedName)
            && variableTypes.TryGetValue(baseName, out var baseTy)
            && baseTy is DataType.UINT8 or DataType.INT8 or DataType.UINT16 or DataType.INT16
                      or DataType.UINT32 or DataType.INT32
            && !instanceClasses.ContainsKey(baseName)
            && !constantAddressVariables.ContainsKey(baseName)
            && !runtimePtrVars.ContainsKey(baseName))
        {
            // Model-A single-field method recovery: when a method touches only one field of self,
            // self is outlined as that field's scalar. The source `self.<field>` here IS self, so
            // re-tag it with the field's (nested) class -- ONLY in this otherwise-error path, so a
            // class-typed field (the DHT's machine.Pin _pin) survives without disturbing any
            // working resolution. currentFunction is "<class>__<method>".
            // self.<field> where <field> is a real field of frameMethod's class, but self has
            // collapsed to a scalar (a Model-A method passes only the field(s) it uses). frameMethod
            // -- not currentFunction -- gives the class whose self is in scope, so this stays correct
            // for a method inlined into another method (the recursion / wrong-class bug).
            if (expr.Object is VariableExpr svF && svF.Name == "self"
                && methodInstanceTypes.TryGetValue(frameMethod, out var ownerCls)
                && classFieldLayout.TryGetValue(ownerCls, out var ownerLay)
                && ownerLay.Any(f => f.Field == expr.Member))
            {
                // Class-typed field: re-tag self with the (concrete) field class so the following
                // .method()/.field resolves -- the nested-ZCA dispatch (machine.Pin._pin -> hal.Pin).
                if (fieldClasses.TryGetValue(ownerCls + "|" + expr.Member, out var ofcRaw)
                    && ResolveConcreteClass(ofcRaw) is { } ofc)
                {
                    var oTy = objVal switch { Variable ov => ov.Type, Temporary ot => ot.Type, _ => DataType.UINT8 };
                    var oTmp = MakeTemp(oTy);
                    Emit(new Copy(objVal, oTmp));
                    instanceClasses[oTmp.Name] = ofc;
                    if (classFieldLayout.TryGetValue(ofc, out var ol) && ol.Count == 1)
                        factoryHandleInstances.Add(oTmp.Name);
                    return oTmp;
                }
                // A bare scalar field IS self only when the class has exactly one field (the collapsed
                // handle, e.g. hal.Pin._pin -- the pin number). For a multi-field class the field lives
                // at an offset and `self` alone is not it -- fall through to the error rather than
                // returning the wrong scalar (this is what keeps the inheritance chains correct).
                if (ownerLay.Count == 1 && ownerLay[0].Field == expr.Member)
                    return objVal;
            }
            throw UserError($"'{expr.Member}' is not a member of a numeric value", expr);
        }

        if (!globals.TryGetValue(flattenedName, out var sym5))
        {
            // Undefined attribute (a typo). This fallback is the last resort: every legitimate
            // resolution (module member, .value/.name, ZCA fields, pointers, numeric-scalar
            // guard) has already returned or thrown above, so reaching here fabricates an
            // undefined `<base>_<member>` Variable read as 0 -- in practice an INDETERMINATE
            // read, since nothing writes the slot and the allocator hands out a register with no
            // writer. Gated to real chip targets (skip PIO and the empty-config unit compiles),
            // like the undefined-function check.
            //
            // Asked of the RECEIVER'S class when that can be resolved, and only of the
            // program-wide superset when it cannot (#276). The superset alone accepted any name
            // that any class anywhere assigned, so whether this line was an error or silent wrong
            // code depended on what else happened to be linked into the same firmware.
            // Scoped OUT of __init__, exactly as the write-path check in Assign.cs is, and for a
            // reason the corpus supplied rather than one I reasoned to: inside a constructor the
            // receiver bindings are still being established, so `instanceClasses` for a name there
            // can answer with the class under construction instead of the name's own class. The
            // MicroPython layer's `machine.ADC.__init__` reads `pin._name` on a `Pin` PARAMETER and
            // the lookup returned `machine_ADC`, which turned a valid read into a refusal -- the one
            // failure mode worse than #276 itself. IsInsideInit() sees through @inline expansion
            // chains, which is what that case needs, since the constructor is inlined into main.
            //
            // The underlying receiver-resolution weakness is NOT fixed here, only avoided, and it
            // is worth its own issue: a wrong answer from instanceClasses is a hazard for anything
            // that trusts it, not just for this check.
            // No longer scoped out of __init__ (#318). The exclusion existed because the
            // receiver's class could not be resolved reliably there, and the resolution is
            // fixed above rather than avoided: a read of a field the receiver's class does not
            // have is refused inside a constructor exactly as it is everywhere else. Accepting
            // it is what let `ADC(an_adc)` lower a HAL table into a run-time comparison chain
            // whose subject nothing writes, and degrade the refusal in its default arm to a
            // warning -- so a pin with no channel behind it read channel 0 instead of being
            // refused.
            // A class attribute whose class defines __get__ is a DESCRIPTOR, and reading it is
            // calling that method rather than taking the attribute's value (#360). Asked first,
            // so a descriptor is never handed back as the object it is stored as.
            if (TryDescriptorRead(baseName, objVal, expr) is { } descriptorVal)
                return descriptorVal;

            // A CLASS-level attribute is a name the instance has and the flattened key does not
            // carry, so it is asked for before the read is called undefined (#268). `Cls.ATTR`
            // already answered; this is the same answer through an instance of Cls.
            if (ClassAttributeThroughReceiver(baseName, expr.Member) is { } classAttr)
                return classAttr;

            if (deviceConfig.Arch.Length > 0 && !deviceConfig.Arch.Contains("pio")
                && !IsKnownMethodName(expr.Member)
                && !MemberReachableFromReceiver(baseName, expr.Member, out var recvCls, out var recvMembers))
                throw UserError(
                    recvCls == null
                        ? $"object has no attribute '{expr.Member}' (typo, or a field never assigned)"
                        : $"'{recvCls}' object has no attribute '{expr.Member}' -- the same AttributeError "
                          + "CPython, MicroPython and CircuitPython would raise here at run time, since "
                          + $"nothing in '{recvCls}' or its bases ever assigns it; PyMCU lays instances "
                          + "out at compile time, so it catches this before the program ever runs instead "
                          + "of after. Assign it in some method to make it a field, or correct the "
                          + $"spelling. Assigned members: {recvMembers}",
                    expr);

            // RFC 0009 phase 3: a field that can hold BOTH None and a scalar is a
            // tagged union -- payload in `flattenedName`, member index in its
            // `<flat>$tag` sibling. EnsureUnionField mints the bookkeeping on the
            // field's first touch; from here the read is an ordinary payload read
            // and the tag-carry paths (`x = obj.field`, `return self.f`, an
            // `is None`/isinstance test) find it through the flat name exactly as
            // a tagged local's.
            if (baseName != null
                && UnionOwnerClass(baseName) is { } ufCls
                && EnsureUnionField(flattenedName, ufCls, expr.Member, out _))
            {
                var uft = variableTypes.TryGetValue(flattenedName, out var udt)
                    ? udt : DataType.UINT8;
                if (moduleInstanceMutableFields.Contains(flattenedName)
                    || UnionFieldIsModuleStorage(flattenedName))
                    mutableGlobals[flattenedName] = uft;
                return new Variable(flattenedName, uft);
            }

            // A field of a module-level instance read inside a FUNCTION needs a real
            // global: its store ran in main, so as a plain local the optimizer
            // dead-stored it once main's own reads folded, and the function's reader
            // loaded a slot nothing wrote. A read in main needs nothing -- the same
            // main-scope store that wrote it stays live for the read. Registering the
            // global here is enough: Globals are collected after all functions lower,
            // and the store targets the same flattened name. Fields that fold never
            // reach this point, so a compile-time value keeps its zero-cost read.
            //
            // This must outrank the variableTypes shortcut below: that map now also
            // carries WIDENED fields (a uint16 _duty a setter joined past its uint8
            // init, #488), and answering from it alone hands back a Variable that was
            // never given a home -- the field reads as a name nothing allocates.
            if (baseName != null && topLevelInstanceTargets.Contains(baseName)
                && instanceClasses.ContainsKey(baseName)
                && !string.IsNullOrEmpty(currentFunction) && currentFunction != "main")
            {
                var gft = variableTypes.TryGetValue(flattenedName, out var gvt)
                    ? gvt : FlattenedFieldType(baseName, expr.Member);
                mutableGlobals[flattenedName] = gft;
                return new Variable(flattenedName, gft);
            }

            // A field promoted to a runtime home (e.g. a write-back-mutated ZCA field) carries
            // its declared width in variableTypes; read it at that width so a uint16/uint32
            // field isn't truncated to a byte.
            if (variableTypes.TryGetValue(flattenedName, out var ft))
                return new Variable(flattenedName, ft);

            return new Variable(flattenedName, DataType.UINT8);
        }
        if (sym5.IsMemoryAddress) return new MemoryAddress(sym5.Value, sym5.Type);
        return new Constant(sym5.Value);

    }

    // True if any class defines a method with this name (used to exclude method references
    // from the undefined-attribute check, e.g. a bare `obj.method` used as a value).
    private bool IsKnownMethodName(string member)
    {
        foreach (var methods in classDirectMethods.Values)
            if (methods.Contains(member)) return true;
        return false;
    }

    private string? ChipFactString(string member) => member switch
    {
        "name" or "chip" => string.IsNullOrEmpty(deviceConfig.Chip)
            ? deviceConfig.TargetChip
            : deviceConfig.Chip,
        "arch" => deviceConfig.Arch,
        "board" => deviceConfig.Board ?? "",
        _ => null
    };

    private Constant InternedStringConstant(string text)
    {
        if (text.Length == 1) return new Constant((int)text[0], text);
        if (!stringLiteralIds.ContainsKey(text))
        {
            stringLiteralIds[text] = nextStringId;
            stringIdToStr[nextStringId] = text;
            nextStringId++;
        }
        return new Constant(stringLiteralIds[text], text);
    }

    /// <summary>
    /// The value of `<paramref name="name"/>.<paramref name="member"/>` when name is bound to
    /// a factory handle whose class has exactly one field and member IS that field. Null in
    /// every other case, which leaves the ordinary member resolution alone.
    /// </summary>
    private Val? HandleFieldRead(string name, string member)
    {
        foreach (var key in new[]
                 {
                     !string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix + name : null,
                     !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : null,
                     name,
                 })
        {
            if (key == null || !factoryHandleInstances.Contains(key)) continue;
            if (!instanceClasses.TryGetValue(key, out var cls) || cls == null) continue;
            if (!classFieldLayout.TryGetValue(cls, out var layout)) continue;
            if (layout.Count != 1 || layout[0].Field != member) continue;

            DataType dt = DataTypeExtensions.StringToDataType(layout[0].Type);
            return new Variable(key, variableTypes.TryGetValue(key, out var vt) ? vt : dt);
        }
        return null;
    }

}
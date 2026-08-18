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
using PyMCU.Frontend;
using PyMCU.IR;
using AstBinOp = PyMCU.Frontend.BinaryOp;
using AstUnOp = PyMCU.Frontend.UnaryOp;

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    private Val VisitExpression(Expression expr)
    {
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
                "implemented yet. Drive a future's poll() from a cooperative loop for now.");

        if (expr is Frontend.DictExpr or Frontend.SetExpr)
            throw UserError(
                "dict/set literals are compile-time lookup tables: bind one to a name " +
                "(`d = {...}`) and use `d[k]` / `x in d` / `len(d)`; they have no runtime " +
                "value in other positions (no heap on bare metal).");

        if (expr is NoneLiteral) return new NoneVal();

        if (expr is StringLiteral str)
        {
            if (str.Value.Length == 1) return new Constant((int)str.Value[0]);

            if (!stringLiteralIds.ContainsKey(str.Value))
            {
                stringLiteralIds[str.Value] = nextStringId;
                stringIdToStr[nextStringId] = str.Value;
                nextStringId++;
            }

            return new Constant(stringLiteralIds[str.Value]);
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
            return vr;
        }

        if (expr is LambdaExpr lam) return VisitLambdaExpr(lam);

        if (expr is FloatLiteral floatLit)
            return new FloatConstant(floatLit.Value);

        if (expr is TupleExpr)
            throw UserError(
                "tuples are not supported as runtime values -- use a fixed list " +
                "([a, b, c]) for indexable storage, or unpack directly (x, y = f())");

        if (expr is ListCompExpr)
            throw UserError(
                "list comprehensions with a filter (if) are not supported -- the array " +
                "length must be a compile-time constant. Build the list with an explicit " +
                "loop, or drop the filter (plain [f(i) for i in range(N)] works)");

        throw UserError($"IR Generation: Unknown Expression type: {expr.GetType().Name}");
    }

    private Val VisitLambdaExpr(LambdaExpr expr)
    {
        string key = "__lambda_" + lambdaCounter++;
        lambdaFunctionsMap[key] = expr;
        pendingLambdaKey = key;
        return new Constant(0);
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

        currentInlinePrefix = newPrefix;
        currentModulePrefix = className + "_";
        inlineDepth = newDepth;
        inlineStack.Add(new InlineContext { ExitLabel = exitLabel, ResultTemp = result, CalleeName = funcKey });

        VisitBlock(func.Body);
        Emit(new Label(exitLabel));
        inlineStack.RemoveAt(inlineStack.Count - 1);

        inlineDepth = savedDepth;
        currentInlinePrefix = savedPrefix;
        currentModulePrefix = savedMod;

        if (result != null) return result;
        return new Constant(0);
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

    private Val VisitFStringExpr(FStringExpr expr)
    {
        string result = "";
        foreach (var part in expr.Parts)
        {
            if (!part.IsExpr) result += part.Text;
            else
            {
                Val val = VisitExpression(part.Expr!);
                if (val is Constant c)
                {
                    if (stringIdToStr.TryGetValue(c.Value, out var s)) result += s;
                    else result += c.Value.ToString();
                }
                else throw new TypeError(
                    "f-string interpolates a runtime value in a position PyMCU does not " +
                    "support. Supported: streaming (print(f\"...\"), uart.write_str/println" +
                    "(f\"...\")) and assignment to a variable (s = f\"...\" builds the string " +
                    "into a fixed buffer). Assign the f-string to a name first, then use " +
                    "that name here.",
                    expr.Line > 0 ? expr.Line : lastLine, 1);
            }
        }

        if (!stringLiteralIds.ContainsKey(result))
        {
            stringLiteralIds[result] = nextStringId;
            stringIdToStr[nextStringId] = result;
            nextStringId++;
        }

        return new Constant(stringLiteralIds[result]);
    }

    private static Val VisitLiteral(IntegerLiteral expr) => new Constant(expr.Value);

    private Val VisitVariable(VariableExpr expr) => ResolveBinding(expr.Name);

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

    private string? BinaryOpDunder(AstBinOp op)
    {
        return op switch
        {
            AstBinOp.Add => "__add__",
            AstBinOp.Sub => "__sub__",
            AstBinOp.Mul => "__mul__",
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

    // True when an expression is None: the None literal, or a name currently bound
    // to None (a param defaulted to None, a variable assigned None). An integer or
    // a concrete instance is never None.
    private bool IsNoneValued(Expression e)
    {
        if (e is NoneLiteral) return true;

        // `obj.field is None`: a field assigned None is tracked under its flattened name
        // (<base>_<field>) by EmitMemberAssign. Resolve the same name without emitting code.
        if (e is MemberAccessExpr ma && ma.Object is VariableExpr mo)
        {
            string b = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + mo.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + mo.Name : mo.Name);
            for (int d = 0; d < 20 && variableAliases.TryGetValue(b, out var a); d++) b = a;
            return noneValuedNames.Contains(b + "_" + ma.Member);
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

    private Val VisitBinary(BinaryExpr expr)
    {
        // Capture and CLEAR any explicit-cast width hint up front: it applies to THIS op only,
        // so operands (visited below) and nested ops promote normally. `uint8(a + b)` then makes
        // the `+` an 8-bit op (wrap + 8-bit flags), the escape hatch from default promotion.
        DataType? widthHint = castWidthHint;
        castWidthHint = null;

        // None comparisons resolve at compile time with real null semantics: an
        // integer or a concrete instance is never None; only a name bound to None
        // (or the None literal itself) is. This replaces the old None==-1 model,
        // which made `x == None` collide with a real value of -1 / 255 / 0xFFFF.
        bool leftNone = expr.Left is NoneLiteral;
        bool rightNone = expr.Right is NoneLiteral;
        if (leftNone || rightNone)
        {
            if (expr.Op is AstBinOp.Equal or AstBinOp.NotEqual or AstBinOp.Is or AstBinOp.IsNot)
            {
                bool isEq = expr.Op is AstBinOp.Equal or AstBinOp.Is;
                bool otherIsNone = leftNone && rightNone
                    || IsNoneValued(leftNone ? expr.Right : expr.Left);
                return new Constant(otherIsNone == isEq ? 1 : 0);
            }
            throw new TypeError(
                "None supports only ==, !=, is and is not comparisons",
                expr.Line > 0 ? expr.Line : lastLine, 1);
        }

        string? dunder = BinaryOpDunder(expr.Op);
        if (dunder != null && expr.Left is VariableExpr lv)
        {
            string qname = string.IsNullOrEmpty(currentInlinePrefix)
                ? (string.IsNullOrEmpty(currentFunction) ? lv.Name : currentFunction + "." + lv.Name)
                : currentInlinePrefix + lv.Name;
            if (instanceClasses.TryGetValue(qname, out var cls) && !string.IsNullOrEmpty(cls))
            {
                string funcKey = cls + "_" + dunder;
                if (inlineFunctions.ContainsKey(funcKey))
                {
                    Val lhs = VisitExpression(expr.Left);
                    Val rhs = VisitExpression(expr.Right);
                    return EmitDunderCall(qname, cls, funcKey, new List<Val> { rhs });
                }
            }
        }

        if (expr.Op == AstBinOp.In || expr.Op == AstBinOp.NotIn)
        {
            bool negate = expr.Op == AstBinOp.NotIn;
            Val lhs = VisitExpression(expr.Left);

            if (expr.Right is VariableExpr rv)
            {
                string qname = string.IsNullOrEmpty(currentInlinePrefix)
                    ? (string.IsNullOrEmpty(currentFunction) ? rv.Name : currentFunction + "." + rv.Name)
                    : currentInlinePrefix + rv.Name;
                if (instanceClasses.TryGetValue(qname, out var cls) && !string.IsNullOrEmpty(cls))
                {
                    string funcKey = cls + "_" + "__contains__";
                    if (inlineFunctions.ContainsKey(funcKey))
                    {
                        Val res = EmitDunderCall(qname, cls, funcKey, new List<Val> { lhs });
                        if (negate)
                        {
                            Temporary neg = MakeTemp();
                            Emit(new Binary(PyMCU.IR.BinaryOp.Equal, res, new Constant(0), neg));
                            return neg;
                        }

                        return res;
                    }
                }
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
                _ => throw UserError(
                    "'in' / 'not in' requires a list, tuple, set or dict literal (or a name " +
                    "bound to one) on the right-hand side")
            };
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
                        if (lc.Value == ec.Value) return new Constant(negate ? 0 : 1);
                    }
                    else allConst = false;

                    elems.Add(ev);
                }

                if (allConst) return new Constant(negate ? 1 : 0);
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

        if (expr.Op == AstBinOp.And)
        {
            // Python `a and b` evaluates to the OPERAND, not a bool: falsy a -> a,
            // otherwise b. Short-circuits b. (`if a and b:` is unaffected since it
            // only tests truthiness; the difference shows in `x = a and b`.)
            Val v1a = VisitExpression(expr.Left);
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
            // Python `a or b`: truthy a -> a, otherwise b. Short-circuits b.
            Val v1a = VisitExpression(expr.Left);
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
        {
            Val bv = VisitExpression(expr.Left);
            Val ev = VisitExpression(expr.Right);
            if (ev is not Constant ce)
                throw UserError("** operator: the exponent must be a compile-time constant integer");
            int exp = ce.Value;
            if (exp < 0)
                throw UserError("** operator: negative exponent not supported (Python would return a float)");

            // Both operands constant: fold the whole power at compile time (table sizes, masks...).
            if (bv is Constant cb)
            {
                int res = 1;
                for (int k = 0; k < exp; ++k) res *= cb.Value;
                return new Constant(res);
            }

            // Runtime base with a constant exponent: lower to repeated multiplication so the common
            // idiom (s ** 2, x ** 3) works. Python-faithful — the base is evaluated exactly once and
            // each multiply promotes to the next wider type, so the result never silently overflows
            // the base's width. Large exponents are rejected rather than emitting a huge unrolled
            // chain (use an explicit loop); the realistic faithful cases are small.
            if (exp == 0) return new Constant(1);
            if (exp == 1) return bv;
            if (exp > 16)
                throw UserError("** operator: exponent too large to unroll (max 16 for a runtime base); use a loop");

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

        Val v1 = VisitExpression(expr.Left);
        Val v2 = VisitExpression(expr.Right);

        // String literals are interned as integer IDs (>= 256); see VisitExpression.
        // Plain arithmetic on those IDs is meaningless — '+' would add the IDs and
        // silently emit garbage. We only treat an operand as a string when there is
        // a real string literal in the source expression: an interned ID can collide
        // with an ordinary integer (e.g. `x * 256`), so the value alone is not enough.
        // This still covers the real cases ("a" + "b", s + "x", name == "PB5").
        bool HasStringLiteral = (expr.Left is StringLiteral sll && sll.Value.Length != 1)
                             || (expr.Right is StringLiteral srl && srl.Value.Length != 1);
        bool IsStringId(Val v) => v is Constant sc && stringIdToStr.ContainsKey(sc.Value);
        if (HasStringLiteral && (IsStringId(v1) || IsStringId(v2)))
        {
            bool bothStr = IsStringId(v1) && IsStringId(v2);

            // Equality folds at compile time: interning gives identical strings the
            // same ID, and a string is never equal to a non-string. This keeps the
            // `if pin_name == "PB5"` / `__CHIP__ == "..."` dispatch idiom working.
            if (expr.Op is AstBinOp.Equal or AstBinOp.NotEqual)
            {
                bool equal = bothStr && ((Constant)v1).Value == ((Constant)v2).Value;
                bool isEq = expr.Op == AstBinOp.Equal;
                return new Constant(equal == isEq ? 1 : 0);
            }

            // Compile-time concatenation of two string literals.
            if (expr.Op == AstBinOp.Add && bothStr)
            {
                string joined = stringIdToStr[((Constant)v1).Value] + stringIdToStr[((Constant)v2).Value];
                if (!stringLiteralIds.TryGetValue(joined, out int joinedId))
                {
                    joinedId = nextStringId++;
                    stringLiteralIds[joined] = joinedId;
                    stringIdToStr[joinedId] = joined;
                }
                return new Constant(joinedId);
            }

            int errLine = expr.Line > 0 ? expr.Line : lastLine;
            if (expr.Op == AstBinOp.Add)
                throw new TypeError(
                    "cannot concatenate a string with a non-string value; both operands of '+' " +
                    "must be compile-time string literals (runtime string building is not supported)",
                    errLine, 1);

            throw new TypeError(
                $"operator '{BinaryOpSymbol(expr.Op)}' is not supported on string values",
                errLine, 1);
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
                    expr.Line > 0 ? expr.Line : lastLine, 1);

            double? f1 = AsFloatCt(v1);
            double? f2 = AsFloatCt(v2);
            if (f1.HasValue && f2.HasValue)
            {
                // Compile-time fold: both operands are known constants.
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
            bool isCompare = expr.Op is AstBinOp.Equal or AstBinOp.NotEqual
                or AstBinOp.Less or AstBinOp.LessEq or AstBinOp.Greater or AstBinOp.GreaterEq;
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
                Console.Error.WriteLine($"[pymcuc] warning: line {dline}: '/' is floating-point "
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
                return new FloatConstant(fcb.Value != 0.0 ? fca.Value / fcb.Value : 0.0);
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
        if (resType is not DataType.FLOAT
            && expr.Op is AstBinOp.Add or AstBinOp.Sub or AstBinOp.Mul or AstBinOp.LShift)
            resType = resType switch
            {
                DataType.UINT8 => DataType.UINT16,
                DataType.INT8 => DataType.INT16,
                DataType.UINT16 => DataType.UINT32,
                DataType.INT16 => DataType.INT32,
                _ => resType,
            };

        // An explicit cast around this op (`uint8(a + b)`) forces fixed-width: compute at the
        // cast's width, overriding promotion. Gives wraparound + the matching 8/16-bit flags.
        if (widthHint is DataType hint && hint is not DataType.FLOAT) resType = hint;

        Temporary dst = MakeTemp(resType);
        if (v1 is Constant cA && v2 is Constant cB)
        {
            // Division/modulo by a constant zero is a compile-time error (Python raises
            // ZeroDivisionError). Guard before folding so we report a clean diagnostic
            // instead of leaking a C# DivideByZeroException as an InternalCompilerError.
            if (cB.Value == 0 && expr.Op is AstBinOp.Div or AstBinOp.FloorDiv or AstBinOp.Mod)
                throw new ValueError("integer division or modulo by zero",
                    expr.Line > 0 ? expr.Line : lastLine, 1);

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
                        expr.Line > 0 ? expr.Line : lastLine, 1);

                throw new ValueError($"shift count {cB.Value} out of range (expected 0..31)",
                    expr.Line > 0 ? expr.Line : lastLine, 1);
            }

            switch (expr.Op)
            {
                case AstBinOp.Add: return new Constant(cA.Value + cB.Value);
                case AstBinOp.Sub: return new Constant(cA.Value - cB.Value);
                case AstBinOp.Equal: return new Constant(cA.Value == cB.Value ? 1 : 0);
                case AstBinOp.NotEqual: return new Constant(cA.Value != cB.Value ? 1 : 0);
                case AstBinOp.Mul: return new Constant(cA.Value * cB.Value);
                case AstBinOp.Div: return new Constant(cA.Value / cB.Value);
                case AstBinOp.FloorDiv:
                    int q = cA.Value / cB.Value;
                    if ((cA.Value ^ cB.Value) < 0 && q * cB.Value != cA.Value) q--;
                    return new Constant(q);
                case AstBinOp.Mod:
                    // Python's % follows the sign of the divisor (floored), unlike C#'s
                    // truncated %. e.g. -7 % 3 == 2, not -1. Match Python at fold time.
                    int rem = cA.Value % cB.Value;
                    if (rem != 0 && ((rem ^ cB.Value) < 0)) rem += cB.Value;
                    return new Constant(rem);
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
            Emit(new SignalError(new Constant(6 /* ZeroDivisionError */), localCatch));
            Emit(new Label(divOk));
        }

        Emit(new Binary(MapBinaryOp(expr.Op), v1, v2, dst));
        return dst;
    }

    private Val VisitTernary(TernaryExpr expr)
    {
        Val cond = VisitExpression(expr.Condition);
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
        Emit(new JumpIfZero(cond, falseLabel));
        Val trueVal = VisitExpression(expr.TrueVal);
        int trueTail = currentInstructions.Count;   // where the true copy + jump belong
        Emit(new Label(falseLabel));
        Val falseVal = VisitExpression(expr.FalseVal);
        Temporary result = MakeTemp(
            DataTypeExtensions.GetPromotedType(GetValType(trueVal), GetValType(falseVal)));
        Emit(new Copy(falseVal, result));
        Emit(new Label(endLabel));
        // Splice [Copy trueVal->result; Jump end] just after the true-branch body, ahead of
        // the false label. Insert in reverse so the first index stays valid.
        currentInstructions.Insert(trueTail, new Jump(endLabel));
        currentInstructions.Insert(trueTail, new Copy(trueVal, result));
        return result;
    }

    private Val VisitUnary(UnaryExpr expr)
    {
        Val operand = VisitExpression(expr.Operand);

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
                expr.Line > 0 ? expr.Line : lastLine, 1);

        if (operand is Constant c)
        {
            switch (expr.Op)
            {
                case AstUnOp.Negate: return new Constant(-c.Value);
                case AstUnOp.Not: return new Constant(c.Value == 0 ? 1 : 0);
                case AstUnOp.BitNot: return new Constant(~c.Value);
            }
        }

        if (expr.Op == AstUnOp.Deref)
        {
            DataType derefElem = RuntimePtrElem(operand);
            Temporary res2 = MakeTemp(derefElem);
            Emit(new LoadIndirect(operand, res2, derefElem));
            return res2;
        }

        Temporary result = MakeTemp(GetValType(operand));
        Emit(new Unary(MapUnaryOp(expr.Op), operand, result));
        return result;
    }

    private Val VisitYield(YieldExpr expr)
    {
        throw UserError(
            "'yield' is only supported in top-level plain functions (lowered to a " +
            "state-machine class) -- not inside @inline functions or class methods. " +
            "Move the generator to module level, or fill a fixed-size array instead.");
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
        return dictLiteralBindings.TryGetValue(name, out dict!);
    }

    private bool TryGetSetBinding(string name, out Frontend.SetExpr set)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix)
            && setLiteralBindings.TryGetValue(currentInlinePrefix + name, out set!)) return true;
        if (!string.IsNullOrEmpty(currentFunction)
            && setLiteralBindings.TryGetValue(currentFunction + "." + name, out set!)) return true;
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
        var entries = new List<(int Key, int Value, bool StrKey)>();
        foreach (var (kE, vE) in d.Entries)
        {
            Val kV = VisitExpression(kE);
            Val vV = VisitExpression(vE);
            if (kV is not Constant kc || vV is not Constant vc)
                throw UserError("dict literals are compile-time lookup tables: every key and " +
                                "value must be a compile-time constant");
            entries.Add((kc.Value, vc.Value, kE is StringLiteral));
        }

        Val keyVal = VisitExpression(keyExpr);
        if (keyVal is Constant keyC)
        {
            foreach (var e in entries)
                if (e.Key == keyC.Value) return new Constant(e.Value);
            if (defaultExpr != null) return VisitExpression(defaultExpr);
            throw UserError($"KeyError: {DescribeDictKey(keyExpr, keyC.Value)} is not a key of " +
                            "this dict literal (checked at compile time)");
        }

        if (entries.Any(e => e.StrKey))
            throw UserError("a dict with string keys needs a compile-time constant key; " +
                            "runtime keys can only match integer keys");
        if (entries.Count == 0)
            throw UserError("KeyError: lookup on an empty dict literal");

        // Result width from the value range.
        int min = entries.Min(e => e.Value), max = entries.Max(e => e.Value);
        DataType rt = min < 0
            ? (min >= short.MinValue && max <= short.MaxValue ? DataType.INT16 : DataType.INT32)
            : (max <= 0xFF ? DataType.UINT8 : max <= 0xFFFF ? DataType.UINT16 : DataType.UINT32);

        Temporary result = MakeTemp(rt);
        string endL = MakeLabel();
        foreach (var e in entries)
        {
            string next = MakeLabel();
            Emit(new JumpIfNotEqual(keyVal, new Constant(e.Key), next));
            Emit(new Copy(new Constant(e.Value), result));
            Emit(new Jump(endL));
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
            Emit(new SignalError(new Constant(4 /* KeyError */), localCatch));
        }
        Emit(new Label(endL));
        return result;
    }

    private string DescribeDictKey(Expression keyExpr, int folded) => keyExpr switch
    {
        StringLiteral s => $"\"{s.Value}\"",
        _ => folded.ToString(),
    };

    private Val VisitIndex(IndexExpr expr)
    {
        // d[k] on a dict-literal binding: a compile-time CLOSED lookup table. A constant
        // key folds to its value; a runtime key lowers to a compare chain that raises
        // KeyError when nothing matches. Must run before the string-subscript rejection
        // below (string keys are legal on dicts).
        if (expr.Target is VariableExpr dictVe && TryGetDictBinding(dictVe.Name, out var dictLit))
            return EmitDictLookup(dictLit, expr.Index);

        // A string subscript is a mistake — a single-char string would otherwise fold to
        // its code point and be used as a (wrong) integer index, e.g. a["k"] -> a[107].
        if (expr.Index is StringLiteral)
            throw UserError("array index must be an integer, not a string");

        if (expr.Index is SliceExpr sl)
        {
            if (expr.Target is VariableExpr srcVe)
            {
                string srcQ = string.IsNullOrEmpty(currentFunction) ? srcVe.Name : currentFunction + "." + srcVe.Name;
                if (!arraySizes.ContainsKey(srcQ) && arraySizes.ContainsKey(srcVe.Name)) srcQ = srcVe.Name;
                if (arraySizes.TryGetValue(srcQ, out int srcSize))
                {
                    DataType elemDt = arrayElemTypes[srcQ];
                    int start = sl.Start != null ? EvaluateConstantExpr(sl.Start) : 0;
                    int stop = sl.Stop != null ? EvaluateConstantExpr(sl.Stop) : srcSize;
                    int step = sl.Step != null ? EvaluateConstantExpr(sl.Step) : 1;
                    if (step == 0) throw UserError("Slice step cannot be zero");
                    if (start < 0) start += srcSize;
                    if (stop < 0) stop += srcSize;
                    start = Math.Max(0, Math.Min(start, srcSize));
                    stop = Math.Max(0, Math.Min(stop, srcSize));
                    int resultCount = 0;
                    for (int i = start; step > 0 ? i < stop : i > stop; i += step) ++resultCount;

                    string tmpName = "__slice_" + tempCounter++;
                    arraySizes[tmpName] = resultCount;
                    arrayElemTypes[tmpName] = elemDt;
                    bool srcSram = arraysWithVariableIndex.Contains(srcQ) || moduleSramArrays.Contains(srcQ);
                    int k = 0;
                    for (int i = start; step > 0 ? i < stop : i > stop; i += step, ++k)
                    {
                        string dstElem = tmpName + "__" + k;
                        variableTypes[dstElem] = elemDt;
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

                        Emit(new Copy(srcVal, new Variable(dstElem, elemDt)));
                    }

                    return new Variable(tmpName, elemDt);
                }
            }

            throw UserError("Slice indexing is only supported on named fixed-size arrays");
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
                else throw UserError("Tuple/list parameter subscript must be a compile-time constant");
                if (li < 0) li += litArg.Elements.Count;
                if (li < 0 || li >= litArg.Elements.Count)
                    throw UserError("Tuple/list parameter subscript index out of range");
                return VisitExpression(litArg.Elements[li]);
            }

            string qualified = string.IsNullOrEmpty(currentFunction) ? ve.Name : currentFunction + "." + ve.Name;
            if (!arraySizes.ContainsKey(qualified) && arraySizes.ContainsKey(ve.Name)) qualified = ve.Name;

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
                string listQ = listVarElemTypes.ContainsKey(qualified) ? qualified
                             : listVarElemTypes.ContainsKey(ve.Name) ? ve.Name
                             : "";
                if (!string.IsNullOrEmpty(listQ))
                {
                    DataType elemDt = listVarElemTypes[listQ];
                    Val listPtr = new Variable(listQ, DataType.GC_REF);
                    Val idxVal = VisitExpression(expr.Index);
                    Temporary elemAddr = EmitElemAddr(listPtr, idxVal, elemDt.SizeOf());
                    Temporary result = MakeTemp(elemDt);
                    Emit(new LoadIndirect(elemAddr, result, elemDt));
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
                            expr.Line > 0 ? expr.Line : lastLine, 1);
                    idxVal = new Constant(adj);
                }

                // Bounds-check a compile-time positive index for every array kind. The
                // fixed-array path below already does this, but the flash and SRAM paths
                // emitted an out-of-bounds load with no diagnostic (reading past the array).
                if (idxVal is Constant cidx && (cidx.Value < 0 || cidx.Value >= sz))
                    throw new IndexError(
                        $"array index {cidx.Value} out of range for size {sz}",
                        expr.Line > 0 ? expr.Line : lastLine, 1);

                if (flashArrays.Contains(qualified))
                {
                    Temporary tmp = MakeTemp(DataType.UINT8);
                    Emit(new ArrayLoadFlash(qualified, idxVal, tmp));
                    return tmp;
                }

                if (arraysWithVariableIndex.Contains(qualified) || moduleSramArrays.Contains(qualified))
                {
                    Temporary tmp = MakeTemp(arrayElemTypes[qualified]);
                    Emit(new ArrayLoad(qualified, idxVal, tmp, arrayElemTypes[qualified], sz));
                    return tmp;
                }
                else
                {
                    if (idxVal is not Constant cc2)
                        throw UserError("Array subscript must be a compile-time constant");
                    int elemIdx = cc2.Value;
                    if (elemIdx < 0 || elemIdx >= sz)
                        throw new IndexError(
                            $"array index {elemIdx} out of range for size {sz}",
                            expr.Line > 0 ? expr.Line : lastLine, 1);
                    string elemName = qualified + "__" + elemIdx;
                    return new Variable(elemName, arrayElemTypes[qualified]);
                }
            }
        }

        // Instance-member array load: self._buf[i] (i runtime), where self._buf
        // was declared as a per-instance SRAM framebuffer.
        if (expr.Target is MemberAccessExpr memLoad
            && ResolveMemberArrayName(memLoad) is string flatLoad)
        {
            Val idxVal = VisitExpression(expr.Index);
            Temporary tmp = MakeTemp(arrayElemTypes[flatLoad]);
            Emit(new ArrayLoad(flatLoad, idxVal, tmp, arrayElemTypes[flatLoad], arraySizes[flatLoad]));
            return tmp;
        }

        {
            Val tgtVal = VisitExpression(expr.Target);
            string cls = GetValClass(tgtVal);
            if (!string.IsNullOrEmpty(cls))
            {
                string funcKey = cls + "_" + "__getitem__";
                if (inlineFunctions.ContainsKey(funcKey))
                {
                    string selfName = tgtVal is Variable v ? v.Name : (tgtVal is Temporary t ? t.Name : "");
                    Val idxVal = VisitExpression(expr.Index);
                    return EmitDunderCall(selfName, cls, funcKey, new List<Val> { idxVal });
                }
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
                        throw UserError("String subscript index out of range");
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

        Val target = VisitExpression(expr.Target);
        Val indexVal2 = VisitExpression(expr.Index);

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
            if (!resolved) throw UserError("Bit index must be constant for reading");
        }

        Temporary dst = MakeTemp();
        Emit(new BitCheck(target, bit, dst));
        return dst;
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
    private string? InstanceClassOfName(string recvName)
    {
        if (ResolveBinding(recvName) is not Variable rv) return null;
        string name = rv.Name;
        for (int depth = 0; depth < 20; depth++)
        {
            if (!variableAliases.TryGetValue(name, out var next)
                || next == null || next.StartsWith("tmp_")) break;
            name = next;
        }
        return instanceClasses.TryGetValue(name, out var cls) ? cls : null;
    }

    // True when this reads a @property getter on a known instance: the receiver is a plain
    // name bound to a class that registers <member> as a getter.
    private bool IsPropertyGetterRead(MemberAccessExpr expr)
        => expr.Object is VariableExpr recv
           && InstanceClassOfName(recv.Name) is { } cls
           && propertyGetters.Contains(cls + "." + expr.Member);

    // The AST of `<instance>.<member>`, resolved through the MRO. Null when the name is not
    // an instance or its class has no such method.
    private FunctionDef? TryResolveInstanceMethodAst(string objName, string member)
    {
        if (InstanceClassOfName(objName) is not { } cls) return null;
        string sym = ResolveMROMethod(cls, member) + "_" + member;
        if (methodAstByName.TryGetValue(sym, out var fd)) return fd;
        return inlineFunctions.TryGetValue(sym, out var fd2) ? fd2 : null;
    }

    private Val VisitMemberAccess(MemberAccessExpr expr)
    {
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
            return TagSlotFieldClass(
                EmitSlotFieldLoad(currentFunction + ".self", true, fieldOff,
                    SlotMethodFieldType(currentFunction, expr.Member), 0),
                methodInstanceTypes.GetValueOrDefault(currentFunction), expr.Member);
        }

        // RFC 0001 Model B (Class[N]): a direct field read on an instance-array element,
        // `arr[i].x`. Compute the element field address and load through it. Without this the
        // member access fell through to a flattened name and read 0.
        if (expr.Object is IndexExpr iaIdxRead
            && TryInstanceArrayFieldAddr(iaIdxRead, expr.Member, out var iaFieldTy) is { } iaAddr)
        {
            Temporary iaLoaded = MakeTemp(iaFieldTy);
            Emit(new LoadIndirect(iaAddr, iaLoaded));
            return iaLoaded;
        }

        if (expr.Object is VariableExpr varExpr)
        {
            // Resolve a module alias (import machine as m) to the real module name so
            // `m.Pin` / `m.Pin.OUT` mangle to machine_Pin..., not the unknown m_Pin.
            string moduleBase = modules.ContainsKey(varExpr.Name)
                && importedAliases.TryGetValue(varExpr.Name, out var realModName) && realModName != null
                ? realModName : varExpr.Name;
            string mangledName = moduleBase + "_" + expr.Member;

            if (globals.TryGetValue(mangledName, out var sym))
            {
                if (sym.IsMemoryAddress) return new MemoryAddress(sym.Value, sym.Type);
                return new Constant(sym.Value);
            }

            if (mutableGlobals.TryGetValue(mangledName, out var type))
            {
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

                throw UserError("Unknown module member: " + mangledName);
            }

            if (functionParams.ContainsKey(mangledName) || functionReturnTypes.ContainsKey(mangledName))
            {
                return new Variable(mangledName, DataType.UINT8);
            }

            if (importedAliases.TryGetValue(varExpr.Name, out var modName))
            {
                var originalName = aliasToOriginal.TryGetValue(varExpr.Name, out var orig) ? orig : varExpr.Name;
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

        if (expr.Member == "value")
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
                       && !(a2 != null && a2.StartsWith("tmp_"))) selfKey = a2!;
                if (instanceClasses.TryGetValue(selfKey, out var selfCls2) && selfCls2 != null
                    && classFieldLayout.TryGetValue(selfCls2, out var lay2) && lay2.Count == 1
                    && lay2[0].Field == expr.Member)
                {
                    if (fieldClasses.TryGetValue(selfCls2 + "|" + expr.Member, out var fc2)
                        && ResolveConcreteClass(fc2) is { } cc2)
                    {
                        var t2 = new Temporary($"tmp_{tempCounter++}", DataType.UINT8);
                        Emit(new Copy(objVal, t2));
                        instanceClasses[t2.Name] = cc2;
                        if (classFieldLayout.TryGetValue(cc2, out var l3) && l3.Count == 1)
                            factoryHandleInstances.Add(t2.Name);
                        return t2;
                    }
                    return objVal;   // scalar single field: self IS the value
                }
            }
            throw UserError("Unknown member access: " + expr.Member);
        }
        while (baseName != null && variableAliases.TryGetValue(baseName, out var next))
        {
            if (next != null && next.StartsWith("tmp_")) break;
            baseName = next;
        }

        // @property getter: a bare `obj.prop` read where `prop` is a registered getter on the
        // instance's class is desugared into a call to the getter method. Without this it would
        // fall through to a non-existent flattened `<base>_<prop>` data field and read 0.
        if (baseName != null && propertyGetters.Count > 0
            && instanceClasses.TryGetValue(baseName, out var getterCls)
            && propertyGetters.Contains(getterCls + "." + expr.Member))
        {
            return VisitCall(new CallExpr(expr, new List<Expression>()));
        }

        // RFC 0001 Model B (SRAM slot): a direct field read on a slot instance OUTSIDE a method
        // (`p.x` where p is a multi-field ZCA) must load from the instance slot. Without this it
        // fell through to a flattened `p_x` variable that no store ever wrote -- a 0 read, or an
        // undefined-symbol link error once the dead var was DCE'd.
        if (baseName != null && slotInstances.TryGetValue(baseName, out var slotArrR)
            && instanceClasses.TryGetValue(baseName, out var slotClsR)
            && TryGetSlotFieldOffset(slotClsR, expr.Member, out int slotOffR, out DataType slotTyR))
        {
            // The slot is a direct SRAM array here (not a pointer as inside a method), so use a
            // byte-offset ArrayLoad -- matching EmitSlotConstruction's ArrayStore. (A BytearrayLoad
            // would dereference main.p__slot as a pointer and read 0.) Multi-byte fields assemble
            // from consecutive bytes.
            int slotTotR = arraySizes.TryGetValue(slotArrR, out var tszR) ? tszR : 0;
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
            var sfTmp = new Temporary($"tmp_{tempCounter++}", sfTy);
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
                    var oTmp = new Temporary($"tmp_{tempCounter++}", oTy);
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
            throw UserError($"'{expr.Member}' is not a member of a numeric value");
        }

        if (!globals.TryGetValue(flattenedName, out var sym5))
        {
            // Undefined attribute (a typo). This fallback is the last resort: every legitimate
            // resolution (module member, .value/.name, ZCA fields, pointers, numeric-scalar
            // guard) has already returned or thrown above, so reaching here fabricates an
            // undefined `<base>_<member>` Variable read as 0. It is a genuine typo when the
            // member is assigned NOWHERE in the program (assignedMemberNames is the superset of
            // every class's fields — a real field is always written in some __init__/method)
            // and is not a method or property getter. Gated to real chip targets (skip PIO and
            // the empty-config unit compiles), like the undefined-function check.
            if (deviceConfig.Arch.Length > 0 && !deviceConfig.Arch.Contains("pio")
                && !assignedMemberNames.Contains(expr.Member)
                && !IsKnownMethodName(expr.Member))
                throw UserError($"object has no attribute '{expr.Member}' (typo, or a field never assigned)");

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
}
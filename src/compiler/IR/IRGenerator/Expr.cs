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
                    "f-string interpolates a runtime value, which PyMCU does not support: " +
                    "building a string at runtime needs a dynamic formatter/buffer that is " +
                    "not generated on bare-metal targets. Use uart.write_str(\"...\") and " +
                    "uart.write(value) / uart.write_hex(value) separately, or keep all " +
                    "interpolated values compile-time constant.",
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

            // The RHS may be a list `[...]` or a tuple `(...)` literal — both are valid in
            // Python (`x in (1, 2, 3)`). Normalize to the element list.
            List<Frontend.Expression> rhsElems = expr.Right switch
            {
                ListExpr rl => rl.Elements,
                Frontend.TupleExpr rt => rt.Elements,
                _ => throw UserError("'in' / 'not in' requires a list or tuple literal on the right-hand side")
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
            if (!(bv is Constant cb) || !(ev is Constant ce))
                throw UserError("** operator requires compile-time constant operands");
            int @base = cb.Value;
            int exp = ce.Value;
            if (exp < 0) throw UserError("** operator: negative exponent not supported");
            int res = 1;
            for (int k = 0; k < exp; ++k) res *= @base;
            return new Constant(res);
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
                    AstBinOp.Div or AstBinOp.FloorDiv => f2.Value != 0.0 ? f1.Value / f2.Value : 0.0,
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
                AstBinOp.Div or AstBinOp.FloorDiv => BinaryOp.Div,
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

        DataType t1 = GetValType(v1);
        DataType t2 = GetValType(v2);
        DataType resType = t1.SizeOf() >= t2.SizeOf() ? t1 : t2;

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
                throw new ValueError($"shift count {cB.Value} out of range (expected 0..31)",
                    expr.Line > 0 ? expr.Line : lastLine, 1);

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

        Emit(new JumpIfZero(cond, falseLabel));
        Val trueVal = VisitExpression(expr.TrueVal);
        Temporary result = MakeTemp(GetValType(trueVal));
        Emit(new Copy(trueVal, result));
        Emit(new Jump(endLabel));
        Emit(new Label(falseLabel));
        Val falseVal = VisitExpression(expr.FalseVal);
        Emit(new Copy(falseVal, result));
        Emit(new Label(endLabel));
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
            Temporary res2 = MakeTemp(DataType.UINT8);
            Emit(new LoadIndirect(operand, res2));
            return res2;
        }

        Temporary result = MakeTemp(GetValType(operand));
        Emit(new Unary(MapUnaryOp(expr.Op), operand, result));
        return result;
    }

    private Val VisitYield(YieldExpr expr)
    {
        throw UserError("Yield not yet implemented");
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

    private Val VisitIndex(IndexExpr expr)
    {
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
                    Emit(new LoadIndirect(elemAddr, result));
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
                Emit(new FlashLoadPtr(new Variable(localName, DataType.UINT16), idxVal, tmp));
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

    private Val VisitMemberAccess(MemberAccessExpr expr)
    {
        // RFC 0001 Model B (SRAM slot): inside a slot method, `self.<field>` reads from the
        // instance slot via the `self` pointer at the field's byte offset.
        if (expr.Object is VariableExpr selfVe && selfVe.Name == "self"
            && slotMethodFieldOffsets.TryGetValue(currentFunction, out var fieldOffs)
            && fieldOffs.TryGetValue(expr.Member, out int fieldOff))
        {
            Temporary loaded = MakeTemp(DataType.UINT8);
            Emit(new BytearrayLoad(currentFunction + ".self", new Constant(fieldOff), loaded));
            return loaded;
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

        if (expr.Member == "value")
        {
            Val obj = VisitExpression(expr.Object);

            // Runtime pointer (ptr(<runtime addr>), e.g. ptr(BASE + x)): read the value at
            // the held address with a LoadIndirect rather than via a compile-time address.
            string? rpn = obj switch { Variable rpv => rpv.Name, Temporary rpt => rpt.Name, _ => null };
            if (rpn != null && runtimePtrVars.TryGetValue(rpn, out var rpElem))
            {
                Temporary ld = MakeTemp(rpElem);
                Emit(new LoadIndirect(obj, ld));
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

        if (string.IsNullOrEmpty(baseName)) throw UserError("Unknown member access: " + expr.Member);
        while (baseName != null && variableAliases.TryGetValue(baseName, out var next))
        {
            if (next != null && next.StartsWith("tmp_")) break;
            baseName = next;
        }

        var flattenedName = baseName + "_" + expr.Member;

        if (constantVariables.TryGetValue(flattenedName, out int cv)) return new Constant(cv);
        if (constantAddressVariables.TryGetValue(flattenedName, out int ca))
            return new MemoryAddress(ca, DataType.UINT16);

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
            throw UserError($"'{expr.Member}' is not a member of a numeric value");

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
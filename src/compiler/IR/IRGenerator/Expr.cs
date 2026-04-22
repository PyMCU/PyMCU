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

        if (expr is NoneExpr) return new Constant(-1);

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
            string key = string.IsNullOrEmpty(currentInlinePrefix)
                ? walrus.VarName
                : currentInlinePrefix + walrus.VarName;
            DataType dt = DataType.UINT8;
            if (variableTypes.TryGetValue(key, out var t)) dt = t;
            var vr = new Variable(key, dt);
            Emit(new Copy(rhs, vr));
            return vr;
        }

        if (expr is LambdaExpr lam) return VisitLambdaExpr(lam);

        if (expr is FloatLiteral floatLit)
            return new FloatConstant(floatLit.Value);

        throw new Exception("IR Generation: Unknown Expression type");
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

    private Val EmitDunderCall(string selfQname, string className, string funcKey, List<Val> extraArgs)
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
                else throw new Exception("f-string interpolation requires a compile-time constant expression");
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

    private Val VisitBinary(BinaryExpr expr)
    {
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

            if (!(expr.Right is ListExpr rlist))
                throw new Exception("'in' / 'not in' requires a list literal on the right-hand side");
            if (rlist.Elements.Count == 0) return new Constant(negate ? 1 : 0);

            var elems = new List<Val>();
            bool allConst = true;
            if (lhs is Constant lc)
            {
                foreach (var e in rlist.Elements)
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
                foreach (var e in rlist.Elements) elems.Add(VisitExpression(e));
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
            Val v1a = VisitExpression(expr.Left);
            if (v1a is Constant c1a)
            {
                if (c1a.Value == 0) return new Constant(0);
                Val v2a = VisitExpression(expr.Right);
                if (v2a is Constant c2a) return new Constant(c2a.Value != 0 ? 1 : 0);
                Temporary r = MakeTemp(DataType.UINT8);
                Emit(new Binary(PyMCU.IR.BinaryOp.NotEqual, v2a, new Constant(0), r));
                return r;
            }

            string falseLabel = MakeLabel();
            string endLabel = MakeLabel();
            Temporary result = MakeTemp(DataType.UINT8);
            Emit(new JumpIfZero(v1a, falseLabel));
            Val v2b = VisitExpression(expr.Right);
            Emit(new Binary(PyMCU.IR.BinaryOp.NotEqual, v2b, new Constant(0), result));
            Emit(new Jump(endLabel));
            Emit(new Label(falseLabel));
            Emit(new Copy(new Constant(0), result));
            Emit(new Label(endLabel));
            return result;
        }

        if (expr.Op == AstBinOp.Or)
        {
            Val v1a = VisitExpression(expr.Left);
            if (v1a is Constant c1a)
            {
                if (c1a.Value != 0) return new Constant(1);
                Val v2a = VisitExpression(expr.Right);
                if (v2a is Constant c2a) return new Constant(c2a.Value != 0 ? 1 : 0);
                Temporary r = MakeTemp(DataType.UINT8);
                Emit(new Binary(PyMCU.IR.BinaryOp.NotEqual, v2a, new Constant(0), r));
                return r;
            }

            string trueLabel = MakeLabel();
            string endLabel = MakeLabel();
            Temporary result = MakeTemp(DataType.UINT8);
            Emit(new JumpIfNotZero(v1a, trueLabel));
            Val v2b = VisitExpression(expr.Right);
            Emit(new Binary(PyMCU.IR.BinaryOp.NotEqual, v2b, new Constant(0), result));
            Emit(new Jump(endLabel));
            Emit(new Label(trueLabel));
            Emit(new Copy(new Constant(1), result));
            Emit(new Label(endLabel));
            return result;
        }

        if (expr.Op == AstBinOp.Pow)
        {
            Val bv = VisitExpression(expr.Left);
            Val ev = VisitExpression(expr.Right);
            if (!(bv is Constant cb) || !(ev is Constant ce))
                throw new Exception("** operator requires compile-time constant operands");
            int @base = cb.Value;
            int exp = ce.Value;
            if (exp < 0) throw new Exception("** operator: negative exponent not supported");
            int res = 1;
            for (int k = 0; k < exp; ++k) res *= @base;
            return new Constant(res);
        }

        Val v1 = VisitExpression(expr.Left);
        Val v2 = VisitExpression(expr.Right);

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

        DataType GetValType(Val v)
        {
            if (v is FloatConstant) return DataType.FLOAT;
            if (v is Variable varV) return varV.Type;
            if (v is Temporary tmp) return tmp.Type;
            if (v is Constant c)
            {
                if (c.Value > 255 || c.Value < -128) return DataType.UINT16;
                return DataType.UINT8;
            }

            return DataType.UINT8;
        }

        DataType t1 = GetValType(v1);
        DataType t2 = GetValType(v2);
        DataType resType = t1.SizeOf() >= t2.SizeOf() ? t1 : t2;

        Temporary dst = MakeTemp(resType);
        if (v1 is Constant cA && v2 is Constant cB)
        {
            switch (expr.Op)
            {
                case AstBinOp.Add: return new Constant(cA.Value + cB.Value);
                case AstBinOp.Sub: return new Constant(cA.Value - cB.Value);
                case AstBinOp.Equal: return new Constant(cA.Value == cB.Value ? 1 : 0);
                case AstBinOp.NotEqual: return new Constant(cA.Value != cB.Value ? 1 : 0);
                case AstBinOp.BitAnd: return new Constant(cA.Value & cB.Value);
                case AstBinOp.BitOr: return new Constant(cA.Value | cB.Value);
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
        Temporary result = MakeTemp(DataType.UINT8);
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

        Temporary result = MakeTemp(DataType.UINT8);
        Emit(new Unary(MapUnaryOp(expr.Op), operand, result));
        return result;
    }

    private Val VisitYield(YieldExpr expr)
    {
        throw new Exception("Yield not yet implemented");
    }

    private Val VisitIndex(IndexExpr expr)
    {
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
                    if (step == 0) throw new Exception("Slice step cannot be zero");
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

            throw new Exception("Slice indexing is only supported on named fixed-size arrays");
        }

        if (expr.Target is VariableExpr ve)
        {
            string qualified = string.IsNullOrEmpty(currentFunction) ? ve.Name : currentFunction + "." + ve.Name;
            if (!arraySizes.ContainsKey(qualified) && arraySizes.ContainsKey(ve.Name)) qualified = ve.Name;
            if (arraySizes.TryGetValue(qualified, out int sz))
            {
                if (flashArrays.Contains(qualified))
                {
                    Val idxVal = VisitExpression(expr.Index);
                    Temporary tmp = MakeTemp(DataType.UINT8);
                    Emit(new ArrayLoadFlash(qualified, idxVal, tmp));
                    return tmp;
                }

                if (arraysWithVariableIndex.Contains(qualified) || moduleSramArrays.Contains(qualified))
                {
                    Val idxVal = VisitExpression(expr.Index);
                    Temporary tmp = MakeTemp(arrayElemTypes[qualified]);
                    Emit(new ArrayLoad(qualified, idxVal, tmp, arrayElemTypes[qualified], sz));
                    return tmp;
                }
                else
                {
                    if (!(expr.Index is IntegerLiteral c2))
                        throw new Exception("Array subscript must be a compile-time constant");
                    string elemName = qualified + "__" + c2.Value;
                    return new Variable(elemName, arrayElemTypes[qualified]);
                }
            }
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
            string? strVal = ResolveStrConstant(localName);
            if (strVal != null)
            {
                if (expr.Index is IntegerLiteral ic)
                {
                    if (ic.Value < 0 || ic.Value >= strVal.Length)
                        throw new Exception("String subscript index out of range");
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
            if (!resolved) throw new Exception("Bit index must be constant for reading");
        }

        Temporary dst = MakeTemp();
        Emit(new BitCheck(target, bit, dst));
        return dst;
    }

    private Val VisitMemberAccess(MemberAccessExpr expr)
    {
        if (expr.Object is VariableExpr varExpr)
        {
            string mangledName = varExpr.Name + "_" + expr.Member;

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

                throw new Exception("Unknown module member: " + mangledName);
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

        if (string.IsNullOrEmpty(baseName)) throw new Exception("Unknown member access: " + expr.Member);
        while (baseName != null && variableAliases.TryGetValue(baseName, out var next))
        {
            if (next != null && next.StartsWith("tmp_")) break;
            baseName = next;
        }

        var flattenedName = baseName + "_" + expr.Member;

        if (constantVariables.TryGetValue(flattenedName, out int cv)) return new Constant(cv);
        if (constantAddressVariables.TryGetValue(flattenedName, out int ca))
            return new MemoryAddress(ca, DataType.UINT16);
        if (!globals.TryGetValue(flattenedName, out var sym5)) return new Variable(flattenedName, DataType.UINT8);
        if (sym5.IsMemoryAddress) return new MemoryAddress(sym5.Value, sym5.Type);
        return new Constant(sym5.Value);

    }
}
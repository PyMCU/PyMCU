using PyMCU.Frontend;
using AstUnOp = PyMCU.Frontend.UnaryOp;

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    private int EmitOptimizedConditionalJump(Expression cond, string targetLabel, bool jumpIfTrue = false)
    {
        int? ResolveInt(Expression expr)
        {
            if (expr is IntegerLiteral num) return num.Value;
            if (expr is VariableExpr v && globals.TryGetValue(v.Name, out var sym) && !sym.IsMemoryAddress)
                return sym.Value;
            return null;
        }

        if (cond is BinaryExpr binExpr)
        {
            if (binExpr.Op == Frontend.BinaryOp.And || binExpr.Op == Frontend.BinaryOp.Or)
            {
                bool isAnd = binExpr.Op == Frontend.BinaryOp.And;

                void EmitSub(Expression sub, string label, bool ifTrue)
                {
                    int r = EmitOptimizedConditionalJump(sub, label, ifTrue);
                    if (r != 0) return;
                    Val v = VisitExpression(sub);
                    if (v is Constant c)
                    {
                        bool cval = c.Value != 0;
                        if (cval == ifTrue) Emit(new Jump(label));
                        return;
                    }

                    if (ifTrue) Emit(new JumpIfNotZero(v, label));
                    else Emit(new JumpIfZero(v, label));
                }

                if ((!jumpIfTrue && isAnd) || (jumpIfTrue && !isAnd))
                {
                    EmitSub(binExpr.Left, targetLabel, jumpIfTrue);
                    EmitSub(binExpr.Right, targetLabel, jumpIfTrue);
                }
                else
                {
                    string skipLabel = MakeLabel();
                    EmitSub(binExpr.Left, skipLabel, !jumpIfTrue);
                    EmitSub(binExpr.Right, targetLabel, jumpIfTrue);
                    Emit(new Label(skipLabel));
                }

                return 1;
            }

            if (binExpr.Op == Frontend.BinaryOp.In || binExpr.Op == Frontend.BinaryOp.NotIn ||
                binExpr.Op == Frontend.BinaryOp.Is || binExpr.Op == Frontend.BinaryOp.IsNot)
                return 0;

            Val v1 = VisitExpression(binExpr.Left);
            Val v2 = VisitExpression(binExpr.Right);

            if (v1 is Constant c1 && v2 is Constant c2)
            {
                bool condResult = false;
                switch (binExpr.Op)
                {
                    case Frontend.BinaryOp.Equal: condResult = c1.Value == c2.Value; break;
                    case Frontend.BinaryOp.NotEqual: condResult = c1.Value != c2.Value; break;
                    case Frontend.BinaryOp.Less: condResult = c1.Value < c2.Value; break;
                    case Frontend.BinaryOp.LessEq: condResult = c1.Value <= c2.Value; break;
                    case Frontend.BinaryOp.Greater: condResult = c1.Value > c2.Value; break;
                    case Frontend.BinaryOp.GreaterEq: condResult = c1.Value >= c2.Value; break;
                }

                if (jumpIfTrue)
                {
                    if (condResult) Emit(new Jump(targetLabel));
                }
                else
                {
                    if (!condResult) Emit(new Jump(targetLabel));
                }

                return condResult ? 1 : -1;
            }

            switch (binExpr.Op)
            {
                case Frontend.BinaryOp.Equal:
                    if (jumpIfTrue) Emit(new JumpIfEqual(v1, v2, targetLabel));
                    else Emit(new JumpIfNotEqual(v1, v2, targetLabel));
                    return 1;
                case Frontend.BinaryOp.NotEqual:
                    if (jumpIfTrue) Emit(new JumpIfNotEqual(v1, v2, targetLabel));
                    else Emit(new JumpIfEqual(v1, v2, targetLabel));
                    return 1;
                case Frontend.BinaryOp.Less:
                    if (jumpIfTrue) Emit(new JumpIfLessThan(v1, v2, targetLabel));
                    else Emit(new JumpIfGreaterOrEqual(v1, v2, targetLabel));
                    return 1;
                case Frontend.BinaryOp.LessEq:
                    if (jumpIfTrue) Emit(new JumpIfLessOrEqual(v1, v2, targetLabel));
                    else Emit(new JumpIfGreaterThan(v1, v2, targetLabel));
                    return 1;
                case Frontend.BinaryOp.Greater:
                    if (jumpIfTrue) Emit(new JumpIfGreaterThan(v1, v2, targetLabel));
                    else Emit(new JumpIfLessOrEqual(v1, v2, targetLabel));
                    return 1;
                case Frontend.BinaryOp.GreaterEq:
                    if (jumpIfTrue) Emit(new JumpIfGreaterOrEqual(v1, v2, targetLabel));
                    else Emit(new JumpIfLessThan(v1, v2, targetLabel));
                    return 1;
            }
        }

        if (cond is BinaryExpr binExpr2 &&
            (binExpr2.Op == Frontend.BinaryOp.Equal || binExpr2.Op == Frontend.BinaryOp.NotEqual))
        {
            var indexExpr = binExpr2.Left as IndexExpr;
            var rhsExpr = binExpr2.Right;
            if (indexExpr == null)
            {
                indexExpr = binExpr2.Right as IndexExpr;
                rhsExpr = binExpr2.Left;
            }

            if (indexExpr != null)
            {
                bool targetIsArray = false;
                if (indexExpr.Target is VariableExpr ve)
                {
                    string q = string.IsNullOrEmpty(currentFunction) ? ve.Name : currentFunction + "." + ve.Name;
                    targetIsArray = arraySizes.ContainsKey(q);
                }

                if (!targetIsArray)
                {
                    var bitVal = ResolveInt(indexExpr.Index);
                    var targetVal = ResolveInt(rhsExpr);

                    if (bitVal.HasValue && targetVal.HasValue)
                    {
                        Val addr = VisitExpression(indexExpr.Target);
                        int bit = bitVal.Value;
                        int target = targetVal.Value;

                        bool invert = binExpr2.Op == Frontend.BinaryOp.NotEqual;
                        if (invert) target = target == 0 ? 1 : 0;

                        if (target == 0)
                        {
                            if (jumpIfTrue) Emit(new JumpIfBitClear(addr, bit, targetLabel));
                            else Emit(new JumpIfBitSet(addr, bit, targetLabel));
                            return 1;
                        }
                        else if (target == 1)
                        {
                            if (jumpIfTrue) Emit(new JumpIfBitSet(addr, bit, targetLabel));
                            else Emit(new JumpIfBitClear(addr, bit, targetLabel));
                            return 1;
                        }
                    }
                }
            }
        }

        if (cond is UnaryExpr unExpr && unExpr.Op == AstUnOp.Not)
        {
            if (unExpr.Operand is IndexExpr idx)
            {
                bool targetIsArray = false;
                if (idx.Target is VariableExpr ve)
                {
                    string q = string.IsNullOrEmpty(currentFunction) ? ve.Name : currentFunction + "." + ve.Name;
                    targetIsArray = arraySizes.ContainsKey(q);
                }

                if (!targetIsArray)
                {
                    var bitVal = ResolveInt(idx.Index);
                    if (bitVal.HasValue)
                    {
                        Val addr = VisitExpression(idx.Target);
                        int bit = bitVal.Value;

                        if (jumpIfTrue) Emit(new JumpIfBitClear(addr, bit, targetLabel));
                        else Emit(new JumpIfBitSet(addr, bit, targetLabel));
                        return 1;
                    }
                }
            }
        }

        if (cond is IndexExpr idx2)
        {
            bool targetIsArray = false;
            if (idx2.Target is VariableExpr ve)
            {
                string q = string.IsNullOrEmpty(currentFunction) ? ve.Name : currentFunction + "." + ve.Name;
                targetIsArray = arraySizes.ContainsKey(q);
            }

            if (!targetIsArray)
            {
                var bitVal = ResolveInt(idx2.Index);
                if (bitVal.HasValue)
                {
                    Val addr = VisitExpression(idx2.Target);
                    int bit = bitVal.Value;

                    if (jumpIfTrue) Emit(new JumpIfBitSet(addr, bit, targetLabel));
                    else Emit(new JumpIfBitClear(addr, bit, targetLabel));
                    return 1;
                }
            }
        }

        return 0;
    }

    private void VisitIf(IfStmt stmt)
    {
        string endLabel = MakeLabel();
        string nextLabel = (stmt.ElifBranches.Count == 0 && stmt.ElseBranch == null) ? endLabel : MakeLabel();

        int optResult = EmitOptimizedConditionalJump(stmt.Condition, nextLabel, false);
        bool skipThen = false;

        if (optResult == -1) skipThen = true;
        else if (optResult == 0)
        {
            Val condVal = VisitExpression(stmt.Condition);
            if (condVal is Constant c)
            {
                if (c.Value == 0)
                {
                    skipThen = true;
                    if (stmt.ElifBranches.Count == 0 && stmt.ElseBranch == null)
                    {
                        Emit(new Label(endLabel));
                        return;
                    }

                    Emit(new Jump(nextLabel));
                }
                else
                {
                    VisitStatement(stmt.ThenBranch);
                    Emit(new Label(endLabel));
                    return;
                }
            }
            else
            {
                Emit(new JumpIfZero(condVal, nextLabel));
            }
        }

        var snapBefore = new Dictionary<string, string>(strConstantVariables);
        var branchSnaps = new List<Dictionary<string, string>>();
        bool hasElse = stmt.ElseBranch != null;

        if (!skipThen)
        {
            VisitStatement(stmt.ThenBranch);
            if (stmt.ElifBranches.Count > 0 || stmt.ElseBranch != null)
                Emit(new Jump(endLabel));
            branchSnaps.Add(new Dictionary<string, string>(strConstantVariables));
            strConstantVariables = new Dictionary<string, string>(snapBefore);
        }

        for (int i = 0; i < stmt.ElifBranches.Count; ++i)
        {
            Emit(new Label(nextLabel));
            bool isLastElif = i == stmt.ElifBranches.Count - 1;
            nextLabel = (isLastElif && stmt.ElseBranch == null) ? endLabel : MakeLabel();

            var elifCond = stmt.ElifBranches[i].Condition;
            var elifBlock = stmt.ElifBranches[i].Body;

            int elifOpt = EmitOptimizedConditionalJump(elifCond, nextLabel, false);
            bool skipElif = false;

            if (elifOpt == -1) skipElif = true;
            else if (elifOpt == 0)
            {
                Val elifVal = VisitExpression(elifCond);
                if (elifVal is Constant c)
                {
                    if (c.Value == 0)
                    {
                        skipElif = true;
                        Emit(new Jump(nextLabel));
                    }
                }
                else
                {
                    Emit(new JumpIfZero(elifVal, nextLabel));
                }
            }

            if (!skipElif)
            {
                VisitStatement(elifBlock);
                if (!isLastElif || stmt.ElseBranch != null) Emit(new Jump(endLabel));
                branchSnaps.Add(new Dictionary<string, string>(strConstantVariables));
                strConstantVariables = new Dictionary<string, string>(snapBefore);
            }
        }

        if (stmt.ElseBranch != null)
        {
            Emit(new Label(nextLabel));
            VisitStatement(stmt.ElseBranch);
            branchSnaps.Add(new Dictionary<string, string>(strConstantVariables));
            strConstantVariables = new Dictionary<string, string>(snapBefore);
        }

        Emit(new Label(endLabel));

        if (branchSnaps.Count <= 0) return;
        var changedKeys = new HashSet<string>();
        foreach (var kvp in branchSnaps.SelectMany(snap => snap))
        {
            if (!snapBefore.TryGetValue(kvp.Key, out var oldV) || oldV != kvp.Value)
                changedKeys.Add(kvp.Key);
        }

        foreach (var key in changedKeys)
        {
            var allAgree = true;
            var agreedVal = "";
            var first = true;
            foreach (var snap in branchSnaps)
            {
                if (!snap.TryGetValue(key, out var v))
                {
                    allAgree = false;
                    break;
                }

                if (first)
                {
                    agreedVal = v;
                    first = false;
                }
                else if (v != agreedVal)
                {
                    allAgree = false;
                    break;
                }
            }

            if (allAgree && !first && hasElse)
            {
                strConstantVariables[key] = agreedVal;
            }
        }
    }

    private void VisitMatch(MatchStmt stmt)
    {
        Val targetVal = VisitExpression(stmt.Target);
        bool ctAlreadyMatched = false;
        string endLabel = MakeLabel();

        foreach (var branch in stmt.Branches)
        {
            string nextCaseLabel = MakeLabel();

            if (branch.Pattern != null)
            {
                if (branch.Pattern is ListExpr seq)
                {
                    string arrName = "";
                    if (targetVal is Variable v) arrName = v.Name;
                    else throw new Exception("match/case sequence pattern: subject must be an array variable");

                    int patSize = seq.Elements.Count;
                    if (arraySizes.TryGetValue(arrName, out int size) && size != patSize)
                    {
                        Emit(new Jump(nextCaseLabel));
                        Emit(new Label(nextCaseLabel));
                        continue;
                    }

                    bool useSram = arraysWithVariableIndex.Contains(arrName) || moduleSramArrays.Contains(arrName);
                    DataType elemDt = arrayElemTypes.TryGetValue(arrName, out var dt) ? dt : DataType.UINT8;

                    var captures = new List<(int Idx, string Name)>();
                    for (int i = 0; i < patSize; ++i)
                    {
                        Expression elem = seq.Elements[i];
                        Val elemVal;
                        if (useSram)
                        {
                            Temporary tmp = MakeTemp(elemDt);
                            Emit(new ArrayLoad(arrName, new Constant(i), tmp, elemDt, patSize));
                            elemVal = tmp;
                        }
                        else
                        {
                            elemVal = new Variable(arrName + "__" + i, elemDt);
                        }

                        if (elem is VariableExpr ve)
                        {
                            string qname = string.IsNullOrEmpty(currentFunction)
                                ? ve.Name
                                : currentFunction + "." + ve.Name;
                            captures.Add((i, qname));
                        }
                        else
                        {
                            Val patVal = VisitExpression(elem);
                            Temporary cmp = MakeTemp();
                            Emit(new Binary(PyMCU.IR.BinaryOp.Equal, elemVal, patVal, cmp));
                            Emit(new JumpIfZero(cmp, nextCaseLabel));
                        }
                    }

                    if (branch.Guard != null)
                    {
                        Val g = VisitExpression(branch.Guard);
                        Emit(new JumpIfZero(g, nextCaseLabel));
                    }

                    foreach (var cap in captures)
                    {
                        Val src = useSram ? (Val)MakeTemp(elemDt) : new Variable(arrName + "__" + cap.Idx, elemDt);
                        if (useSram) Emit(new ArrayLoad(arrName, new Constant(cap.Idx), src, elemDt, patSize));
                        Emit(new Copy(src, new Variable(cap.Name, elemDt)));
                        variableTypes[cap.Name] = elemDt;
                    }

                    if (!string.IsNullOrEmpty(branch.CaptureName))
                    {
                        string qname = string.IsNullOrEmpty(currentFunction)
                            ? branch.CaptureName
                            : currentFunction + "." + branch.CaptureName;
                        Emit(new Copy(targetVal, new Variable(qname, elemDt)));
                        variableTypes[qname] = elemDt;
                    }

                    if (branch.Body != null) VisitBlock((Block)branch.Body);
                    Emit(new Jump(endLabel));
                    Emit(new Label(nextCaseLabel));
                    continue;
                }

                var alts = new List<Expression>();

                void Flatten(Expression e)
                {
                    if (e is BinaryExpr bin && bin.Op == Frontend.BinaryOp.BitOr) // AST BitOr for match alternation
                    {
                        Flatten(bin.Left);
                        Flatten(bin.Right);
                        return;
                    }

                    alts.Add(e);
                }

                Flatten(branch.Pattern);

                var altVals = alts.Select(VisitExpression).ToList<Val>();
                bool allAltsConst = targetVal is Constant;
                if (allAltsConst)
                {
                    foreach (var v in altVals)
                        if (!(v is Constant))
                        {
                            allAltsConst = false;
                            break;
                        }
                }

                bool skipBody = false;
                if (allAltsConst)
                {
                    bool anyMatch = false;
                    var ct = targetVal as Constant;
                    foreach (var v in altVals)
                    {
                        if (((Constant)v).Value == ct!.Value)
                        {
                            anyMatch = true;
                            break;
                        }
                    }

                    if (!anyMatch)
                    {
                        Emit(new Jump(nextCaseLabel));
                        skipBody = true;
                    }
                    else
                    {
                        ctAlreadyMatched = true;
                    }
                }
                else if (alts.Count == 1)
                {
                    Temporary cmpRes = MakeTemp();
                    Emit(new Binary(PyMCU.IR.BinaryOp.Equal, targetVal, altVals[0], cmpRes));
                    Emit(new JumpIfZero(cmpRes, nextCaseLabel));
                }
                else
                {
                    string matchLabel = MakeLabel();
                    foreach (var altVal in altVals)
                    {
                        Temporary cmp = MakeTemp();
                        Emit(new Binary(PyMCU.IR.BinaryOp.Equal, targetVal, altVal, cmp));
                        Emit(new JumpIfNotZero(cmp, matchLabel));
                    }

                    Emit(new Jump(nextCaseLabel));
                    Emit(new Label(matchLabel));
                }

                if (!skipBody)
                {
                    if (!string.IsNullOrEmpty(branch.CaptureName))
                    {
                        string qname = string.IsNullOrEmpty(currentFunction)
                            ? branch.CaptureName
                            : currentFunction + "." + branch.CaptureName;
                        DataType dt = targetVal is Variable v2
                            ? v2.Type
                            : (targetVal is Temporary t2 ? t2.Type : DataType.UINT8);
                        Emit(new Copy(targetVal, new Variable(qname, dt)));
                        variableTypes[qname] = dt;
                    }

                    if (branch.Guard != null)
                    {
                        Val g = VisitExpression(branch.Guard);
                        Emit(new JumpIfZero(g, nextCaseLabel));
                    }

                    if (branch.Body != null) VisitBlock((Block)branch.Body);
                    Emit(new Jump(endLabel));
                }
            }
            else
            {
                if (!ctAlreadyMatched)
                {
                    if (!string.IsNullOrEmpty(branch.CaptureName))
                    {
                        string qname = string.IsNullOrEmpty(currentFunction)
                            ? branch.CaptureName
                            : currentFunction + "." + branch.CaptureName;
                        DataType dt = targetVal is Variable v2
                            ? v2.Type
                            : (targetVal is Temporary t2 ? t2.Type : DataType.UINT8);
                        Emit(new Copy(targetVal, new Variable(qname, dt)));
                        variableTypes[qname] = dt;
                    }

                    if (branch.Guard != null)
                    {
                        Val g = VisitExpression(branch.Guard);
                        Emit(new JumpIfZero(g, nextCaseLabel));
                    }

                    if (branch.Body != null) VisitBlock((Block)branch.Body);
                    Emit(new Jump(endLabel));
                }
            }

            Emit(new Label(nextCaseLabel));
        }

        Emit(new Label(endLabel));
    }

    private void VisitWhile(WhileStmt stmt)
    {
        string startLabel = MakeLabel();
        string endLabel = MakeLabel();
        loopStack.Add(new LoopLabels { ContinueLabel = startLabel, BreakLabel = endLabel });

        Emit(new Label(startLabel));

        int whileOpt = EmitOptimizedConditionalJump(stmt.Condition, endLabel, false);
        if (whileOpt == -1)
        {
            Emit(new Label(endLabel));
            loopStack.RemoveAt(loopStack.Count - 1);
            return;
        }

        if (whileOpt == 0)
        {
            Val condVal = VisitExpression(stmt.Condition);
            if (condVal is Constant c)
            {
                if (c.Value == 0) Emit(new Jump(endLabel));
            }
            else
            {
                Emit(new JumpIfZero(condVal, endLabel));
            }
        }

        VisitStatement(stmt.Body);
        Emit(new Jump(startLabel));
        Emit(new Label(endLabel));
        loopStack.RemoveAt(loopStack.Count - 1);
    }

    private void VisitBreak(BreakStmt stmt)
    {
        if (loopStack.Count == 0) throw new Exception("Break statement outside of loop");
        Emit(new Jump(Enumerable.Last<LoopLabels>(loopStack).BreakLabel));
    }

    private void VisitContinue(ContinueStmt stmt)
    {
        if (loopStack.Count == 0) throw new Exception("Continue statement outside of loop");
        Emit(new Jump(Enumerable.Last<LoopLabels>(loopStack).ContinueLabel));
    }
}
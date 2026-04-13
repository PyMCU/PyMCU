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
    public int EvaluateConstantExpr(Expression expr)
    {
        if (expr is IntegerLiteral num) return num.Value;

        if (expr is StringLiteral str)
        {
            if (!stringLiteralIds.ContainsKey(str.Value))
            {
                stringLiteralIds[str.Value] = nextStringId;
                stringIdToStr[nextStringId] = str.Value;
                nextStringId++;
            }

            return stringLiteralIds[str.Value];
        }

        if (expr is CallExpr call)
        {
            if (call.Callee is VariableExpr varExpr)
            {
                if (((varExpr.Name == "ptr" && intrinsicNames.Contains("ptr")) || varExpr.Name == "PIORegister") &&
                    call.Args.Count == 1)
                {
                    return EvaluateConstantExpr(call.Args[0]);
                }

                if (varExpr.Name == "const" && intrinsicNames.Contains("const") && call.Args.Count == 1)
                {
                    return EvaluateConstantExpr(call.Args[0]);
                }
            }
        }

        if (expr is VariableExpr varE)
        {
            string lookup = currentModulePrefix + varE.Name;
            if (globals.TryGetValue(lookup, out var globalSym))
            {
                if (!globalSym.IsMemoryAddress) return globalSym.Value;
            }

            foreach (var modName in modules.Keys)
            {
                string modKey = modName + "_" + varE.Name;
                if (globals.TryGetValue(modKey, out var modSym))
                {
                    if (!modSym.IsMemoryAddress) return modSym.Value;
                }
            }
        }

        throw new Exception("Not a constant expression");
    }

    private Function VisitFunction(FunctionDef funcNode)
    {
        var irFunc = new Function();
        string fullName = currentModulePrefix + funcNode.Name;
        irFunc.Name = fullName;
        currentFunction = fullName;

        irFunc.IsInline = funcNode.IsInline;
        irFunc.IsInterrupt = funcNode.IsInterrupt;
        irFunc.InterruptVector = funcNode.InterruptVector;

        currentFunctionGlobals.Clear();
        currentInstructions.Clear();
        loopStack.Clear();
        lastLine = -1;

        foreach (var param in funcNode.Params)
        {
            irFunc.Params.Add(currentFunction + "." + param.Name);
        }

        arraysWithVariableIndex.Clear();
        ScanForVariableIndexedArrays(funcNode.Body.Statements, fullName + ".");

        VisitBlock(funcNode.Body);

        if (currentInstructions.Count == 0 || !(currentInstructions.Last() is Return))
        {
            Emit(new Return(new NoneVal()));
        }

        irFunc.Body = new List<Instruction>(currentInstructions);
        arraysWithVariableIndex.Clear();
        return irFunc;
    }

    private void VisitBlock(Block block)
    {
        foreach (var stmt in block.Statements)
        {
            VisitStatement(stmt);
        }
    }

    private void VisitStatement(Statement stmt)
    {
        if (stmt.Line > 0 && inlineDepth == 0)
        {
            currentStmtLine = stmt.Line;
        }

        if (stmt.Line > 0 && stmt.Line != lastLine)
        {
            var linesPtr = sourceLines;
            if (!string.IsNullOrEmpty(currentModulePrefix))
            {
                string modKey = currentModulePrefix.Substring(0, currentModulePrefix.Length - 1);
                if (moduleSourceLines.TryGetValue(modKey, out var lines))
                {
                    linesPtr = lines;
                }
            }

            if (stmt.Line <= linesPtr.Count)
            {
                Emit(new DebugLine(stmt.Line, linesPtr[stmt.Line - 1], currentSourceFile));
                lastLine = stmt.Line;
            }
        }

        if (stmt is ImportStmt imp)
        {
            if (inlineDepth > 0)
            {
                foreach (var sym in imp.Symbols)
                {
                    string key = imp.Aliases.ContainsKey(sym) ? imp.Aliases[sym] : sym;
                    importedAliases[key] = imp.ModuleName;
                    if (imp.Aliases.ContainsKey(sym))
                        aliasToOriginal[key] = sym;
                }

                if (imp.Symbols.Count == 0)
                {
                    string modKey = string.IsNullOrEmpty(imp.ModuleAlias) ? imp.ModuleName : imp.ModuleAlias;
                    modules[modKey] = new ModuleScope();
                }
            }

            return;
        }

        if (stmt is Block block)
        {
            VisitBlock(block);
            return;
        }

        if (stmt is ReturnStmt ret)
        {
            VisitReturn(ret);
            return;
        }

        if (stmt is IfStmt ifStmt)
        {
            VisitIf(ifStmt);
            return;
        }

        if (stmt is MatchStmt matchStmt)
        {
            VisitMatch(matchStmt);
            return;
        }

        if (stmt is WhileStmt whileStmt)
        {
            VisitWhile(whileStmt);
            return;
        }

        if (stmt is ForStmt forStmt)
        {
            VisitFor(forStmt);
            return;
        }

        if (stmt is BreakStmt breakStmt)
        {
            VisitBreak(breakStmt);
            return;
        }

        if (stmt is ContinueStmt continueStmt)
        {
            VisitContinue(continueStmt);
            return;
        }

        if (stmt is WithStmt withStmt)
        {
            VisitWith(withStmt);
            return;
        }

        if (stmt is AssertStmt assertStmt)
        {
            VisitAssert(assertStmt);
            return;
        }

        if (stmt is AssignStmt assign)
        {
            VisitAssign(assign);
            return;
        }

        if (stmt is AugAssignStmt augAssign)
        {
            VisitAugAssign(augAssign);
            return;
        }

        if (stmt is VarDecl decl)
        {
            VisitVarDecl(decl);
            return;
        }

        if (stmt is AnnAssign annAssign)
        {
            VisitAnnAssign(annAssign);
            return;
        }

        if (stmt is ExprStmt exprStmt)
        {
            VisitExprStmt(exprStmt);
            return;
        }

        if (stmt is TupleUnpackStmt tupleUnpack)
        {
            VisitTupleUnpack(tupleUnpack);
            return;
        }

        if (stmt is NonlocalStmt nonloc)
        {
            VisitNonlocal(nonloc);
            return;
        }

        if (stmt is FunctionDef funcDef)
        {
            if (!funcDef.IsInline) throw new Exception($"Nested function '{funcDef.Name}' must be @inline");
            inlineFunctions[funcDef.Name] = funcDef;
            functionReturnTypes[funcDef.Name] = funcDef.ReturnType;
            var @params = new List<string>();
            var paramTypes = new List<DataType>();
            foreach (var p in funcDef.Params)
            {
                @params.Add(p.Name);
                paramTypes.Add(DataTypeExtensions.StringToDataType(p.Type));
            }

            functionParams[funcDef.Name] = @params;
            functionParamTypes[funcDef.Name] = paramTypes;
            return;
        }

        if (stmt is GlobalStmt global)
        {
            VisitGlobal(global);
        }
        else if (stmt is ClassDef cls)
        {
            VisitClassDef(cls);
        }
        else if (stmt is PassStmt)
        {
            return;
        }
        else if (stmt is RaiseStmt raiseStmt)
        {
            throw new Exception($"{raiseStmt.ErrorType}: {raiseStmt.Message}");
        }
        else
        {
            throw new Exception($"IR Generation: Unknown Statement type: {stmt.GetType().Name}");
        }
    }

    private void VisitReturn(ReturnStmt stmt)
    {
        if (stmt.Value != null && inlineStack.Count > 0 && inlineStack.Last().ResultVars.Count > 0)
        {
            if (stmt.Value is TupleExpr tup)
            {
                var ctx = inlineStack.Last();
                if (tup.Elements.Count != ctx.ResultVars.Count)
                {
                    throw new Exception($"Tuple return size mismatch: expected {ctx.ResultVars.Count} elements");
                }

                for (int k = 0; k < tup.Elements.Count; ++k)
                {
                    Val elemVal = VisitExpression(tup.Elements[k]);
                    DataType dt = DataType.UINT8;
                    Emit(new Copy(elemVal, new Variable(ctx.ResultVars[k], dt)));
                    if (elemVal is Constant c)
                        constantVariables[ctx.ResultVars[k]] = c.Value;
                }

                Emit(new Jump(ctx.ExitLabel));
                return;
            }
        }

        Val val = new NoneVal();
        if (stmt.Value != null)
        {
            val = VisitExpression(stmt.Value);
        }

        if (inlineStack.Count > 0)
        {
            var ctx = inlineStack.Last();
            if (ctx.ResultTemp != null)
            {
                if (val is MemoryAddress m)
                {
                    bool returnsPtr = false;
                    if (functionReturnTypes.TryGetValue(ctx.CalleeName, out var rt))
                    {
                        if (rt != null) returnsPtr = rt.StartsWith("ptr") || (rt.Contains("ptr") && rt.Contains("["));
                    }

                    if (returnsPtr)
                    {
                        if (!ctx.ResultAssigned)
                        {
                            constantAddressVariables[ctx.ResultTemp.Name] = m.Address;
                            ctx.ResultAssigned = true;
                        }

                        Emit(new Jump(ctx.ExitLabel));
                        return;
                    }
                }

                Emit(new Copy(val, ctx.ResultTemp));
                ctx.ResultAssigned = true;

                if (val is Constant c)
                {
                    constantVariables[ctx.ResultTemp.Name] = c.Value;
                }
                else if (val is Variable v)
                {
                    variableAliases[ctx.ResultTemp.Name] = v.Name;
                }
                else if (val is Temporary t)
                {
                    variableAliases[ctx.ResultTemp.Name] = t.Name;
                }
            }

            Emit(new Jump(ctx.ExitLabel));
        }
        else
        {
            Emit(new Return(val));
        }
    }
}
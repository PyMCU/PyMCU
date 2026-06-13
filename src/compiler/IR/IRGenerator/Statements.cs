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

        // Fold constant arithmetic so register definitions can use a readable
        // `BASE + offset` form, e.g. `UART0_DR: ptr[uint32] = ptr(UART0_BASE + 0x00)`.
        // Without this, only literal-address registers (the AVR `ptr(0x23)` style)
        // resolved to a MemoryAddress; expression-based addresses (RP2040 / CH32V
        // peripheral maps) fell through to the catch in ScanGlobals and were
        // mis-registered as mutable SRAM globals.
        if (expr is BinaryExpr bin)
        {
            int l = EvaluateConstantExpr(bin.Left);
            int r = EvaluateConstantExpr(bin.Right);
            switch (bin.Op)
            {
                case PyMCU.Frontend.BinaryOp.Add:    return l + r;
                case PyMCU.Frontend.BinaryOp.Sub:    return l - r;
                case PyMCU.Frontend.BinaryOp.Mul:    return l * r;
                case PyMCU.Frontend.BinaryOp.Div:    return l / r;
                case PyMCU.Frontend.BinaryOp.FloorDiv: return l / r;
                case PyMCU.Frontend.BinaryOp.Mod:    return l % r;
                case PyMCU.Frontend.BinaryOp.BitAnd: return l & r;
                case PyMCU.Frontend.BinaryOp.BitOr:  return l | r;
                case PyMCU.Frontend.BinaryOp.BitXor: return l ^ r;
                case PyMCU.Frontend.BinaryOp.LShift: return l << r;
                case PyMCU.Frontend.BinaryOp.RShift: return l >> r;
            }
        }

        if (expr is UnaryExpr un)
        {
            int v = EvaluateConstantExpr(un.Operand);
            switch (un.Op)
            {
                case PyMCU.Frontend.UnaryOp.Negate: return -v;
                case PyMCU.Frontend.UnaryOp.BitNot: return ~v;
            }
        }

        throw UserError("Not a constant expression");
    }

    private Function VisitFunction(FunctionDef funcNode)
    {
        var irFunc = new Function();
        string fullName = currentModulePrefix + funcNode.Name;
        irFunc.Name = fullName;
        irFunc.OriginalName = funcNode.Name;
        currentFunction = fullName;

        irFunc.IsInline = funcNode.IsInline;
        irFunc.IsInterrupt = funcNode.IsInterrupt;
        irFunc.IsNaked = funcNode.IsNaked;
        irFunc.InterruptVector = funcNode.InterruptVector;
        irFunc.ReturnType = DataTypeExtensions.StringToDataType(funcNode.ReturnType);
        // RFC 0001 Model B: a factory declared `-> C` (single-field ZCA) actually returns
        // the packed field scalar, so the IR return type is the field type, not the class.
        if (zcaFactoryClasses.TryGetValue(funcNode.ReturnType, out var handleFieldType))
            irFunc.ReturnType = DataTypeExtensions.StringToDataType(handleFieldType);

        currentFunctionGlobals.Clear();
        currentInstructions.Clear();
        loopStack.Clear();
        lastLine = -1;

        // RFC 0001 Model B (sret): a factory `-> C` for a MULTI-field (slot) ZCA gets a hidden
        // leading `__self` pointer param. The caller allocates the slot and passes its address;
        // the body stores fields through it and returns it (see VisitReturn). R24:R25 = __self.
        if (slotClasses.Contains(funcNode.ReturnType))
        {
            string selfParam = currentFunction + ".__self";
            irFunc.Params.Add(selfParam);
            bytearrayParams.Add(selfParam);
            variableTypes[selfParam] = DataType.UINT16;
            irFunc.ReturnType = DataType.UINT16; // returns the slot pointer
        }

        foreach (var param in funcNode.Params)
        {
            string qualifiedParam = currentFunction + "." + param.Name;
            irFunc.Params.Add(qualifiedParam);
            DataType paramDt = DataTypeExtensions.StringToDataType(param.Type);
            if (param.Type == "bytearray")
                bytearrayParams.Add(qualifiedParam);
            // A const[str] parameter of a non-@inline function is received by reference as
            // a 16-bit flash byte-pointer (callers pass a FlashStrAddr); s[i] in the body
            // lowers to FlashLoadPtr. (@inline functions still bind the literal at compile
            // time via strConstantVariables, so this only applies to real subroutines.)
            if (param.Type == "const[str]" && !funcNode.IsInline)
            {
                paramDt = DataType.UINT16;
                flashStrPtrVars.Add(qualifiedParam);
            }
            variableTypes[qualifiedParam] = paramDt;
        }

        arraysWithVariableIndex.Clear();
        ScanForVariableIndexedArrays(funcNode.Body.Statements, fullName + ".");

        VisitBlock(funcNode.Body);

        if (currentInstructions.Count == 0 || !(currentInstructions.Last() is Return))
        {
            Emit(new Return(new NoneVal()));
        }

        // Collect unique GC_REF named locals; inject GcRoot at prologue and GcUnroot before each Return.
        // Only track named Variables (not Temporaries): gc_alloc returns a Temporary that is immediately
        // Copy'd to a named Variable, and that Variable is what needs shadow-stack tracking.
        // Temporaries may live only in registers (_tmpRegLayout) and have no SRAM slot for GetGcRefSramAddr.
        var gcRefs = new List<Val>();
        var gcRefNames = new HashSet<string>();
        foreach (var instr in currentInstructions)
        {
            if (instr is Copy cp && cp.Dst is Variable vd && vd.Type == DataType.GC_REF)
            {
                if (gcRefNames.Add(vd.Name)) gcRefs.Add(vd);
            }
        }

        if (gcRefs.Count > 0)
        {
            var annotated = new List<Instruction>();
            // Prologue: push each GC_REF local onto the shadow stack.
            foreach (var gcRef in gcRefs)
                annotated.Add(new GcRoot(gcRef));
            // Body: insert GcUnroot before every Return.
            foreach (var instr in currentInstructions)
            {
                if (instr is Return)
                    foreach (var gcRef in gcRefs)
                        annotated.Add(new GcUnroot(gcRef));
                annotated.Add(instr);
            }
            currentInstructions = annotated;
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
                Emit(new DebugLine(stmt.Line, linesPtr[stmt.Line - 1], currentSourceFile, !string.IsNullOrEmpty(currentInlinePrefix)));
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
            if (!funcDef.IsInline) throw UserError($"Nested function '{funcDef.Name}' must be @inline");
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
            VisitRaise(raiseStmt);
        }
        else if (stmt is TryStmt tryStmt)
        {
            VisitTry(tryStmt);
        }
        else
        {
            throw UserError($"IR Generation: Unknown Statement type: {stmt.GetType().Name}");
        }
    }

    private void VisitReturn(ReturnStmt stmt)
    {
        // Returning a (multi-char) string from a function declared to return an integer is
        // a type confusion — the string folds to its flash id and would be returned as that
        // numeric id. (A single-char string is its code point, which is a valid integer.)
        if (stmt.Value is StringLiteral retStr && retStr.Value.Length != 1
            && functionReturnTypes.TryGetValue(currentFunction, out var retRt) && retRt != null
            && retRt is "uint8" or "int8" or "uint16" or "int16" or "uint32" or "int32")
            throw UserError(
                $"cannot return a string from a function declared to return {retRt}");

        if (stmt.Value != null && inlineStack.Count > 0 && inlineStack.Last().ResultVars.Count > 0)
        {
            if (stmt.Value is TupleExpr tup)
            {
                var ctx = inlineStack.Last();
                if (tup.Elements.Count != ctx.ResultVars.Count)
                {
                    throw UserError($"Tuple return size mismatch: expected {ctx.ResultVars.Count} elements");
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

        // RFC 0001 Model B: a non-@inline factory `def make() -> C: return C(args)` where
        // C is a single-field ZCA. The instance has no runtime struct, so return the packed
        // field as a scalar (the "handle"). The field value is the ctor arg that initializes
        // it. The use site tracks `x = make()` as a handle instance (see Assign.cs).
        if (inlineStack.Count == 0 && stmt.Value is CallExpr facCall
            && facCall.Callee is VariableExpr facCallee
            && functionReturnTypes.TryGetValue(currentFunction, out var curRt) && curRt != null)
        {
            string facCls = ResolveCallee(facCallee.Name);
            if (curRt == facCls && zcaFactoryClasses.ContainsKey(facCls)
                && classFieldLayout.TryGetValue(facCls, out var facLayout) && facLayout.Count == 1)
            {
                int argIdx = 0;
                string srcParam = facLayout[0].SourceParam;
                if (!string.IsNullOrEmpty(srcParam)
                    && functionParams.TryGetValue(facCls + "___init__", out var initParams))
                {
                    int pIdx = initParams.IndexOf(srcParam);
                    if (pIdx >= 1) argIdx = pIdx - 1; // drop implicit self
                }

                Val handleVal = argIdx < facCall.Args.Count
                    ? VisitExpression(facCall.Args[argIdx])
                    : new Constant(0);
                Emit(new Return(handleVal));
                return;
            }

            // Multi-field (slot) ZCA factory: store each field into the caller-allocated slot
            // via the hidden __self pointer, then return that pointer (sret).
            if (curRt == facCls && slotClasses.Contains(facCls)
                && classFieldLayout.TryGetValue(facCls, out var slotLayout))
            {
                string selfPtr = currentFunction + ".__self";
                functionParams.TryGetValue(facCls + "___init__", out var slotInit);
                int off = 0;
                foreach (var (field, type, srcParam) in slotLayout)
                {
                    int argIdx = 0;
                    if (slotInit != null && !string.IsNullOrEmpty(srcParam))
                    {
                        int pIdx = slotInit.IndexOf(srcParam);
                        if (pIdx >= 1) argIdx = pIdx - 1;
                    }

                    Val v = argIdx < facCall.Args.Count
                        ? VisitExpression(facCall.Args[argIdx])
                        : new Constant(0);
                    Emit(new BytearrayStore(selfPtr, new Constant(off), v));
                    off += DataTypeExtensions.StringToDataType(type).SizeOf();
                }

                Emit(new Return(new Variable(selfPtr, DataType.UINT16)));
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

                // Guard: when a live return path already set the result via a Variable or
                // Temporary (runtime value), a subsequent dead-code `return constant` must NOT
                // overwrite the alias — that would fold the runtime result to the sentinel
                // constant (e.g. 255 for a uint8 default-arg guard) on every later use.
                //
                // Rule: Constant returns win only when they are FIRST (first-return-wins for
                // constants). Variable/Temporary returns always update regardless of order —
                // this preserves the `return -1; ... return result` pattern where a runtime
                // return must clear a stale constant set by an earlier const return.
                bool wasAlreadyAssigned = ctx.ResultAssigned;
                Emit(new Copy(val, ctx.ResultTemp));
                ctx.ResultAssigned = true;

                if (val is Constant c)
                {
                    if (!wasAlreadyAssigned)
                    {
                        // First return wins for constants — subsequent const dead-code paths skipped.
                        constantVariables[ctx.ResultTemp.Name] = c.Value;
                        // If the constant is a string ID, also register it as a string constant
                        // so downstream code can resolve it via ResolveStrConstant.
                        if (stringIdToStr.TryGetValue(c.Value, out string? sv))
                            strConstantVariables[ctx.ResultTemp.Name] = sv;
                    }
                }
                else if (val is Variable v)
                {
                    // A non-constant return path clears any constant tracked from a prior
                    // return path (e.g. `return -1` followed by `return result`).  Without
                    // this, the stale constant propagates through the alias chain and causes
                    // the comparison to be constant-folded at IR-generation time.
                    constantVariables.Remove(ctx.ResultTemp.Name);
                    variableAliases[ctx.ResultTemp.Name] = v.Name;
                    // Carry string-constant metadata through the alias
                    if (strConstantVariables.TryGetValue(v.Name, out string? vsv))
                        strConstantVariables[ctx.ResultTemp.Name] = vsv;
                }
                else if (val is Temporary t)
                {
                    constantVariables.Remove(ctx.ResultTemp.Name);
                    variableAliases[ctx.ResultTemp.Name] = t.Name;
                    // Carry string-constant metadata through the alias
                    if (strConstantVariables.TryGetValue(t.Name, out string? tsv))
                        strConstantVariables[ctx.ResultTemp.Name] = tsv;
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
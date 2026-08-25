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
            // Compile-time constants from loop unrolling / inline expansion (e.g. `i`
            // in `for i in range(n)` or an enumerate index). This lets address
            // expressions like `ptr(BASE + 4 * i)` fold to a constant MemoryAddress
            // inside an unrolled loop, instead of degrading to a runtime pointer.
            if (constantVariables.TryGetValue(currentInlinePrefix + varE.Name, out int cvip)) return cvip;
            if (!string.IsNullOrEmpty(currentFunction) &&
                constantVariables.TryGetValue(currentFunction + "." + varE.Name, out int cvf)) return cvf;
            if (constantVariables.TryGetValue(varE.Name, out int cvb)) return cvb;

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

    /// <summary>
    /// Records, for the function about to be lowered, the narrowest type that holds every
    /// integer literal assigned to each unannotated local. A name is recorded ONLY when the
    /// compiler can see all of its assignments and all of them are plain integer literals:
    /// one augmented assignment, one call, one loop variable or one unpack target and the name
    /// is dropped, because then the width depends on something this scan does not evaluate.
    /// </summary>
    private void CollectLiteralOnlyLocalWidths(FunctionDef funcNode)
    {
        literalOnlyLocalWidths = CollectLiteralOnlyWidths(
            new List<Statement> { funcNode.Body }, funcNode.Params.Select(p => p.Name));
    }

    /// <summary>
    /// Shared by the local and module-level scans: walks <paramref name="body"/> and returns the
    /// narrowest type that holds every integer literal assigned to each name, for the names whose
    /// assignments are ALL plain literals. Names in <paramref name="preDropped"/> never qualify.
    /// </summary>
    private static Dictionary<string, DataType> CollectLiteralOnlyWidths(
        IEnumerable<Statement> body, IEnumerable<string> preDropped)
    {
        var result = new Dictionary<string, DataType>();
        var bounds = new Dictionary<string, (long Min, long Max)>();
        var dropped = new HashSet<string>();

        foreach (var p in preDropped) dropped.Add(p);

        void Bind(string name, Expression? value)
        {
            // `-5` parses as a negation over a literal, and it is as much a literal as `5`.
            if (value is UnaryExpr { Op: PyMCU.Frontend.UnaryOp.Negate, Operand: IntegerLiteral neg })
                value = new IntegerLiteral(-neg.Value);

            if (value is IntegerLiteral lit && !dropped.Contains(name))
            {
                if (bounds.TryGetValue(name, out var b))
                    bounds[name] = (Math.Min(b.Min, lit.Value), Math.Max(b.Max, lit.Value));
                else
                    bounds[name] = (lit.Value, lit.Value);
                return;
            }

            dropped.Add(name);
        }

        void Walk(Statement? st)
        {
            switch (st)
            {
                case null: return;
                case Block b: foreach (var s2 in b.Statements) Walk(s2); return;
                case AssignStmt a when a.Target is VariableExpr av: Bind(av.Name, a.Value); return;
                case AnnAssign an: dropped.Add(an.Target); return;
                // An unannotated module-level `b = 5` parses as a VarDecl with an empty type,
                // which is a literal binding like any other; only a written annotation means
                // the user has already chosen the width.
                case VarDecl vd when string.IsNullOrEmpty(vd.VarType): Bind(vd.Name, vd.Init); return;
                case VarDecl vd: dropped.Add(vd.Name); return;
                case AugAssignStmt aug when aug.Target is VariableExpr augv: dropped.Add(augv.Name); return;
                case TupleUnpackStmt tu: foreach (var t in tu.Targets) dropped.Add(t); return;
                case ForStmt f:
                    dropped.Add(f.VarName);
                    if (!string.IsNullOrEmpty(f.Var2Name)) dropped.Add(f.Var2Name);
                    Walk(f.Body);
                    return;
                case WhileStmt w: Walk(w.Body); return;
                case WithStmt wi:
                    if (!string.IsNullOrEmpty(wi.AsName)) dropped.Add(wi.AsName);
                    Walk(wi.Body);
                    return;
                case IfStmt i:
                    Walk(i.ThenBranch);
                    foreach (var (_, body) in i.ElifBranches) Walk(body);
                    Walk(i.ElseBranch);
                    return;
                case MatchStmt m:
                    foreach (var br in m.Branches)
                    {
                        if (!string.IsNullOrEmpty(br.CaptureName)) dropped.Add(br.CaptureName);
                        Walk(br.Body);
                    }
                    return;
                case TryStmt t2:
                    foreach (var s2 in t2.Body) Walk(s2);
                    foreach (var (_, handler) in t2.Handlers) foreach (var s2 in handler) Walk(s2);
                    if (t2.ElseBody != null) foreach (var s2 in t2.ElseBody) Walk(s2);
                    if (t2.Finally != null) foreach (var s2 in t2.Finally) Walk(s2);
                    return;
                case FunctionDef nested: Walk(nested.Body); return;
                default: return;
            }
        }

        foreach (var st in body) Walk(st);

        foreach (var kv in bounds)
        {
            if (dropped.Contains(kv.Key)) continue;
            result[kv.Key] = NarrowestTypeFor(kv.Value.Min, kv.Value.Max);
        }

        return result;
    }

    /// <summary>
    /// Every name a function assigns to, whatever the shape of the assignment. The module-level
    /// scan uses it to DISQUALIFY: a global written from inside a function is not literal-only,
    /// and telling a `global x` write apart from a same-named local is not worth the risk here --
    /// a name dropped by coincidence keeps the width it had.
    /// </summary>
    private static void CollectAssignedNames(Statement? st, HashSet<string> into)
    {
        switch (st)
        {
            case null: return;
            case Block b: foreach (var s2 in b.Statements) CollectAssignedNames(s2, into); return;
            case AssignStmt a when a.Target is VariableExpr av: into.Add(av.Name); return;
            case AnnAssign an: into.Add(an.Target); return;
            case VarDecl vd: into.Add(vd.Name); return;
            case AugAssignStmt aug when aug.Target is VariableExpr augv: into.Add(augv.Name); return;
            case TupleUnpackStmt tu: foreach (var t in tu.Targets) into.Add(t); return;
            case ForStmt f:
                into.Add(f.VarName);
                if (!string.IsNullOrEmpty(f.Var2Name)) into.Add(f.Var2Name);
                CollectAssignedNames(f.Body, into);
                return;
            case WhileStmt w: CollectAssignedNames(w.Body, into); return;
            case WithStmt wi:
                if (!string.IsNullOrEmpty(wi.AsName)) into.Add(wi.AsName);
                CollectAssignedNames(wi.Body, into);
                return;
            case IfStmt i:
                CollectAssignedNames(i.ThenBranch, into);
                foreach (var (_, bodyStmt) in i.ElifBranches) CollectAssignedNames(bodyStmt, into);
                CollectAssignedNames(i.ElseBranch, into);
                return;
            case MatchStmt m:
                foreach (var br in m.Branches)
                {
                    if (!string.IsNullOrEmpty(br.CaptureName)) into.Add(br.CaptureName);
                    CollectAssignedNames(br.Body, into);
                }
                return;
            case TryStmt t2:
                foreach (var s2 in t2.Body) CollectAssignedNames(s2, into);
                foreach (var (_, handler) in t2.Handlers) foreach (var s2 in handler) CollectAssignedNames(s2, into);
                if (t2.ElseBody != null) foreach (var s2 in t2.ElseBody) CollectAssignedNames(s2, into);
                if (t2.Finally != null) foreach (var s2 in t2.Finally) CollectAssignedNames(s2, into);
                return;
            case FunctionDef nested: CollectAssignedNames(nested.Body, into); return;
            default: return;
        }
    }

    /// <summary>The narrowest integer type that holds the whole closed range [min, max].</summary>
    private static DataType NarrowestTypeFor(long min, long max)
    {
        if (min >= 0)
        {
            if (max <= byte.MaxValue) return DataType.UINT8;
            if (max <= ushort.MaxValue) return DataType.UINT16;
            return DataType.UINT32;
        }

        if (min >= sbyte.MinValue && max <= sbyte.MaxValue) return DataType.INT8;
        if (min >= short.MinValue && max <= short.MaxValue) return DataType.INT16;
        return DataType.INT32;
    }

    private Function VisitFunction(FunctionDef funcNode)
    {
        if (funcNode.IsAsync)
            throw UserError(
                $"async def '{funcNode.Name}': the coroutine-to-state-machine lowering is not " +
                "implemented yet (the syntax parses; the transform is the next step). For now, " +
                "write the future as a small class with a poll() method and drive it from a " +
                "cooperative loop -- the zero-cost pattern async lowers to (see the RTOS example).");

        // A multi-value return is lowered only through the @inline expansion path, where the
        // caller's unpack targets become the result slots. A real subroutine has one return
        // register, so the annotation cannot be honoured -- reject it at the definition rather
        // than at whichever `return (a, b)` happens to be reached first.
        if (TupleType.IsTupleType(funcNode.ReturnType))
        {
            currentStmtLine = funcNode.Line;
            throw UserError(
                $"'{funcNode.Name}' is declared to return {TupleType.Describe(funcNode.ReturnType)}: " +
                "returning multiple values is only supported from an @inline function " +
                "(the caller's unpack targets receive them); mark the function @inline or " +
                "return a single value");
        }

        var irFunc = new Function();
        string fullName = currentModulePrefix + funcNode.Name;
        irFunc.Name = fullName;
        irFunc.OriginalName = funcNode.Name;
        currentFunction = fullName;

        irFunc.IsInline = funcNode.IsInline;
        irFunc.IsInterrupt = funcNode.IsInterrupt;
        irFunc.IsNaked = funcNode.IsNaked;
        irFunc.IsExportC = funcNode.IsExportC;
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

        // Width of the unannotated locals whose every assignment is an integer literal, so
        // `n = 200` costs what `n: uint8 = 200` costs. Collected BEFORE the body is visited:
        // the first store used to fix the type at int32 with no idea what the rest of the
        // function did, and pulled in the 32-bit decimal writer -- 756 bytes on atmega328p,
        // 37% of an attiny2313's flash, for a choice the user did not know they were making.
        CollectLiteralOnlyLocalWidths(funcNode);

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
                paramDt = FlashPtrType;   // 16-bit on AVR/PIC, 32-bit on ARM/RISC-V
                flashStrPtrVars.Add(qualifiedParam);
            }

            // Set by the scan (Scan.cs) for an unannotated parameter the body subscripts: it is a
            // pass an array by its base address (ArrayBase), so the pointer was arriving all
            // along and only the callee's reading of `buf[i]` was wrong: with nothing saying
            // otherwise, a subscript fell through to the REGISTER BIT path. A run-time index
            // then failed as "Bit index must be constant for reading", which names no buffer
            // and no parameter and describes an operation the program does not contain; a
            // constant index was worse, compiling silently into a bit test of the address.
            //
            // Reading it as a byte pointer is what the `bytearray` annotation does, so the
            // annotated and unannotated spellings now agree. Inferring nothing (an annotated
            // parameter, an @inline function whose body is expanded at the call site with the
            // argument bound directly, a parameter never subscripted) leaves every other path
            // exactly as it was.
            if (param.Type.Length == 0 && bytearrayParams.Contains(qualifiedParam))
                paramDt = DataTypeExtensions.PointerWidth >= 4 ? DataType.UINT32 : DataType.UINT16;

            variableTypes[qualifiedParam] = paramDt;
        }

        arraysWithVariableIndex.Clear();
        ScanForVariableIndexedArrays(funcNode.Body.Statements, fullName + ".");

        VisitBlock(funcNode.Body);

        // A function that promises a value must produce one on every path. Falling off the end
        // emitted `ret` with nothing in the return register, and the caller printed whatever
        // it happened to hold -- on a clean build, with the missing `return` in plain sight.
        // Python answers None here, which PyMCU has no room for in a typed return.
        // Both the source and what was emitted have to agree that the end is reachable. The
        // HAL is full of if/elif dispatch over compile-time constants where the source falls
        // through on paper and the folded body cannot: judging on the source alone rejected
        // 464 programs that are perfectly well formed.
        bool emittedFallsThrough = currentInstructions.Count == 0
                                   || currentInstructions[^1] is not Return;
        if (funcNode.ReturnType is not (null or "" or "void" or "None")
            && !funcNode.IsExtern && !funcNode.IsNaked
            && emittedFallsThrough
            && !FunctionHasYield(funcNode)
            && !FunctionUsesInlineAsm(funcNode)
            && !AlwaysReturns(funcNode.Body))
            throw UserError(
                $"'{funcNode.Name}' is declared to return {funcNode.ReturnType}, but it can reach "
                + "the end of its body without a return. Python would answer None there, which a "
                + $"{funcNode.ReturnType} has no room for -- add a return on the remaining path.");

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
        //
        // The return belongs to whatever function is being lowered right now, which during an
        // expansion is the INLINE one, not the caller. Reading currentFunction meant an
        // `@inline ... -> str` returning "PB5" inside a `-> uint8` function was reported as
        // "cannot return a string from a function declared to return uint8": a mismatch that
        // exists in neither function, on a line the user cannot act on. When the owner's
        // declared type is unknown, say nothing -- a wrong answer is worse than none.
        string returnOwner = inlineStack.Count > 0 ? inlineStack[^1].CalleeName : currentFunction;
        if (stmt.Value is StringLiteral retStr && retStr.Value.Length != 1
            && functionReturnTypes.TryGetValue(returnOwner, out var retRt) && retRt != null
            && retRt is "uint8" or "int8" or "uint16" or "int16" or "uint32" or "int32")
            throw UserError(
                $"cannot return a string from a function declared to return {retRt}");

        // A `return` escaping a try-with-finally must run the pending finally block(s) first
        // (Python semantics). Evaluate the value, materialize it so the finally can't change it,
        // run the finallies, then return. Handles the common non-inline, non-constructor return;
        // the specialized inline/factory return shapes below are a rare combination with finally.
        bool ctorReturn = stmt.Value is CallExpr ccr && ccr.Callee is VariableExpr ccrv
                          && classNames.Contains(ResolveCallee(ccrv.Name));
        if (finallyStack.Count > 0 && inlineStack.Count == 0 && !ctorReturn)
        {
            Val rfv = stmt.Value != null ? VisitExpression(stmt.Value) : new NoneVal();
            if (rfv is not (Constant or NoneVal or FloatConstant))
            {
                Temporary rt = MakeTemp(GetValType(rfv));
                Emit(new Copy(rfv, rt));
                rfv = rt;
            }
            EmitPendingFinally();
            Emit(new Return(rfv));
            return;
        }

        if (stmt.Value != null && inlineStack.Count > 0 && inlineStack.Last().ResultVars.Count > 0)
        {
            if (stmt.Value is TupleExpr tup)
            {
                var ctx = inlineStack.Last();
                var declared = functionReturnTypes.TryGetValue(ctx.CalleeName, out var ctxRt)
                    ? TupleType.ElementTypes(ctxRt) : new List<string>();

                if (declared.Count > 0 && tup.Elements.Count != declared.Count)
                    throw UserError(
                        $"'{ctx.CalleeName}' is declared to return {declared.Count} values " +
                        $"{TupleType.Describe(ctxRt!)}, but this return has {tup.Elements.Count}");

                if (tup.Elements.Count != ctx.ResultVars.Count)
                {
                    throw UserError($"Tuple return size mismatch: expected {ctx.ResultVars.Count} elements");
                }

                for (int k = 0; k < tup.Elements.Count; ++k)
                {
                    Val elemVal = VisitExpression(tup.Elements[k]);
                    // The result slots carry the annotated element widths when the callee
                    // declared them (see EmitInlineFunctionCall); uint8 otherwise.
                    DataType dt = variableTypes.TryGetValue(ctx.ResultVars[k], out var slotDt)
                        ? slotDt : DataType.UINT8;
                    Emit(new Copy(elemVal, new Variable(ctx.ResultVars[k], dt)));
                    if (elemVal is Constant c)
                        constantVariables[ctx.ResultVars[k]] = c.Value;
                }

                Emit(new Jump(ctx.ExitLabel));
                return;
            }

            // Declared multi-value but returning a single expression: the caller has N unpack
            // targets and only one value would reach them.
            if (functionReturnTypes.TryGetValue(inlineStack.Last().CalleeName, out var singleRt)
                && TupleType.IsTupleType(singleRt))
                throw UserError(
                    $"'{inlineStack.Last().CalleeName}' is declared to return " +
                    $"{TupleType.Describe(singleRt!)}, but this return has a single value");
        }

        // A multi-value (tuple) return is only lowered through the @inline expansion path
        // above (the caller's unpack targets become the ResultVars). From a regular
        // subroutine there are no per-call result slots, so it cannot be supported; report it
        // clearly instead of letting the TupleExpr reach VisitExpression as an unknown node.
        if (stmt.Value is TupleExpr)
            throw UserError(
                "returning multiple values is only supported from an @inline function " +
                "(the caller's unpack targets receive them); mark the function @inline or " +
                "return a single value");

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
                    EmitSlotFieldStore(selfPtr, true, off,
                        DataTypeExtensions.StringToDataType(type), v, 0);
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
                            // Carry the pointed-to width across the inline return too.
                            // Without it a `ptr[T]` returned from an @inline selector
                            // loses T, and a later `.value` access is sized by whatever
                            // the temporary happened to be typed as -- a 16-bit store
                            // to a 32-bit peripheral register on a 32-bit target.
                            variableTypes[ctx.ResultTemp.Name] = m.Type;
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

                // A return already visited at this expansion's own branch depth ends control
                // flow: everything after it is dead code and must not change what the result
                // is tracked as.
                bool afterUnconditionalReturn = ctx.ResultReturnedUnconditionally;

                if (val is Constant c && !afterUnconditionalReturn)
                {
                    if (!wasAlreadyAssigned)
                    {
                        // First return wins for constants — subsequent const dead-code paths skipped.
                        ctx.ResultConst = c.Value;
                        constantVariables[ctx.ResultTemp.Name] = c.Value;
                        // If the constant is a string ID, also register it as a string constant
                        // so downstream code can resolve it via ResolveStrConstant.
                        if (stringIdToStr.TryGetValue(c.Value, out string? sv))
                            strConstantVariables[ctx.ResultTemp.Name] = sv;
                    }
                    else if (ctx.ResultConst is int prevConst && prevConst != c.Value)
                    {
                        // A second REACHABLE return yields a different constant, so the callee
                        // picks its result at run time and the result is not a compile-time
                        // constant at all. Only arms the compiler actually walks get here, and
                        // only while no earlier return has already ended control flow.
                        //
                        // Leaving the first constant in place made every consumer that folds a
                        // constant RHS (a ZCA field store, an @inline argument) drop the store
                        // and read the first `return` on every path (PWM.set_freq always chose
                        // prescaler 1).
                        ctx.ResultConst = null;
                        constantVariables.Remove(ctx.ResultTemp.Name);
                        strConstantVariables.Remove(ctx.ResultTemp.Name);
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

                // No run-time condition opened inside this expansion guards this return, so it
                // ends the body: mark it, and later returns are treated as the dead code they are.
                if (_runtimeBranchDepth <= ctx.EntryBranchDepth)
                    ctx.ResultReturnedUnconditionally = true;
            }

            Emit(new Jump(ctx.ExitLabel));
        }
        else
        {
            Emit(new Return(val));
        }
    }

    /// <summary>
    /// True when control cannot reach the end of <paramref name="st"/>: every path leaves
    /// through a return or a raise. Deliberately conservative -- an unrecognized shape counts
    /// as falling through, so the check never invents a diagnostic for a program it does not
    /// understand.
    /// </summary>
    private static bool AlwaysReturns(Statement? st)
    {
        switch (st)
        {
            case null: return false;
            case ReturnStmt: return true;
            case RaiseStmt: return true;
            case Block b: return b.Statements.Any(AlwaysReturns);
            case IfStmt i:
                if (i.ElseBranch == null) return false;
                if (!AlwaysReturns(i.ThenBranch)) return false;
                foreach (var (_, br) in i.ElifBranches)
                    if (!AlwaysReturns(br)) return false;
                return AlwaysReturns(i.ElseBranch);
            case WhileStmt w:
                // `while True:` with no way out is an endless loop: the end of the body is
                // never reached, so neither is the end of the function.
                return w.Condition is BooleanLiteral { Value: true }
                       or IntegerLiteral { Value: not 0 }
                       && !LoopBodyHasBreakOrContinue(w.Body);
            case TryStmt t:
                if (t.Finally != null && t.Finally.Any(AlwaysReturns)) return true;
                if (!t.Body.Any(AlwaysReturns)) return false;
                foreach (var (_, h) in t.Handlers)
                    if (!h.Any(AlwaysReturns)) return false;
                return true;
            case WithStmt wi: return AlwaysReturns(wi.Body);
            case MatchStmt m:
                // Only a match with a catch-all (`case _:`) covers every value; without one
                // there is a path that matches nothing and falls out the bottom. An unguarded
                // capture is a catch-all too (`case _ as x:`).
                bool hasCatchAll = m.Branches.Any(
                    br => br.Guard == null && (br.Pattern == null || !string.IsNullOrEmpty(br.CaptureName)));
                return hasCatchAll && m.Branches.All(br => AlwaysReturns(br.Body));
            default: return false;
        }
    }

    /// <summary>True when the function body contains a yield, making it a generator.</summary>
    private static bool FunctionHasYield(FunctionDef func)
    {
        bool found = false;
        void E(Expression? e)
        {
            if (found || e == null) return;
            switch (e)
            {
                case YieldExpr: found = true; return;
                case CallExpr c: E(c.Callee); foreach (var a in c.Args) E(a); return;
                case BinaryExpr b: E(b.Left); E(b.Right); return;
                case UnaryExpr u: E(u.Operand); return;
                case MemberAccessExpr m: E(m.Object); return;
                case IndexExpr ix: E(ix.Target); E(ix.Index); return;
                case TernaryExpr t: E(t.Condition); E(t.TrueVal); E(t.FalseVal); return;
            }
        }
        void S(Statement? st)
        {
            if (found || st == null) return;
            switch (st)
            {
                case Block b: foreach (var cs in b.Statements) S(cs); return;
                case ExprStmt es: E(es.Expr); return;
                case AssignStmt a: E(a.Value); return;
                case AugAssignStmt aug: E(aug.Value); return;
                case AnnAssign an: E(an.Value); return;
                case VarDecl vd: E(vd.Init); return;
                case ReturnStmt r: E(r.Value); return;
                case IfStmt i:
                    S(i.ThenBranch);
                    foreach (var (_, br) in i.ElifBranches) S(br);
                    S(i.ElseBranch);
                    return;
                case WhileStmt w: S(w.Body); return;
                case ForStmt f: S(f.Body); return;
                case WithStmt wi: S(wi.Body); return;
                case TryStmt t:
                    foreach (var cs in t.Body) S(cs);
                    foreach (var (_, h) in t.Handlers) foreach (var cs in h) S(cs);
                    if (t.ElseBody != null) foreach (var cs in t.ElseBody) S(cs);
                    if (t.Finally != null) foreach (var cs in t.Finally) S(cs);
                    return;
                case MatchStmt m: foreach (var br in m.Branches) S(br.Body); return;
            }
        }
        S(func.Body);
        return found;
    }


    /// <summary>
    /// True when the body contains an `asm(...)`. Such a function returns through the assembly
    /// it wrote -- the HAL's pulse-in helpers are nothing but `asm` lines ending in RET -- so
    /// there is no Python return for the fall-through check to find, and none is missing.
    /// </summary>
    private static bool FunctionUsesInlineAsm(FunctionDef func)
    {
        bool found = false;
        void S(Statement? st)
        {
            if (found || st == null) return;
            switch (st)
            {
                case Block b: foreach (var cs in b.Statements) S(cs); return;
                case ExprStmt { Expr: CallExpr { Callee: VariableExpr { Name: "asm" } } }: found = true; return;
                case IfStmt i:
                    S(i.ThenBranch);
                    foreach (var (_, br) in i.ElifBranches) S(br);
                    S(i.ElseBranch);
                    return;
                case WhileStmt w: S(w.Body); return;
                case ForStmt f: S(f.Body); return;
                case WithStmt wi: S(wi.Body); return;
                case TryStmt t:
                    foreach (var cs in t.Body) S(cs);
                    foreach (var (_, h) in t.Handlers) foreach (var cs in h) S(cs);
                    if (t.ElseBody != null) foreach (var cs in t.ElseBody) S(cs);
                    if (t.Finally != null) foreach (var cs in t.Finally) S(cs);
                    return;
                case MatchStmt m: foreach (var br in m.Branches) S(br.Body); return;
            }
        }
        S(func.Body);
        return found;
    }

}
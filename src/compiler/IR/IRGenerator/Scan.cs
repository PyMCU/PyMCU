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
    // Registers a module-level fixed buffer for `name = bytearray(N)` or
    // `bytearray([...])`. N may be an integer literal or any compile-time constant
    // expression already registered by this scan (e.g. `WINDOW = 8` earlier in the
    // module). Silently ignores anything that is not a bytearray(...) call — the
    // caller passes every candidate initializer through here.
    private void TryRegisterModuleBytearray(string name, Expression? initializer)
    {
        if (initializer is not CallExpr call || call.Callee is not VariableExpr callee
            || callee.Name != "bytearray" || call.Args.Count == 0) return;

        int count = call.Args[0] switch
        {
            ListExpr le => le.Elements.Count,
            var e when TryEvalElemConst(e, out int n) => n,
            _ => 0,
        };
        if (count <= 0) return;

        arraySizes[name] = count;
        arrayElemTypes[name] = DataType.UINT8;
        moduleSramArrays.Add(name);
    }

    // Module-level names that are WRITTEN beyond their initializer: a second top-level
    // assignment, any assignment nested in a loop/branch/try, an augmented assignment,
    // or a function that declares them `global`. Such a name is a mutable variable whose
    // initializer merely happens to be constant -- NOT a constant alias. Folding it as a
    // constant silently deleted every later write and folded every read (a state machine
    // with named states, `state: uint8 = IDLE`, never left state 0).
    private static HashSet<string> CollectModuleReassignedNames(ProgramNode ast)
    {
        var counts = new Dictionary<string, int>();
        void Bump(string n, int by = 1) =>
            counts[n] = (counts.TryGetValue(n, out var c) ? c : 0) + by;

        void Walk(Statement? s)
        {
            switch (s)
            {
                case null: return;
                case AssignStmt { Target: VariableExpr v }: Bump(v.Name); return;
                case AnnAssign aa: Bump(aa.Target); return;
                case VarDecl vd: Bump(vd.Name); return;
                // aug-assign presupposes an existing binding: always a REassignment.
                case AugAssignStmt { Target: VariableExpr av }: Bump(av.Name, 2); return;
                case Block b: foreach (var st in b.Statements) Walk(st); return;
                case IfStmt f:
                    Walk(f.ThenBranch);
                    Walk(f.ElseBranch);
                    foreach (var e in f.ElifBranches) Walk(e.Item2);
                    return;
                case WhileStmt w: Walk(w.Body); return;
                case ForStmt fo: Walk(fo.Body); return;
                case MatchStmt m: foreach (var br in m.Branches) Walk(br.Body); return;
                case TryStmt t:
                    foreach (var st in t.Body) Walk(st);
                    foreach (var (_, h) in t.Handlers) foreach (var st in h) Walk(st);
                    if (t.Finally != null) foreach (var st in t.Finally) Walk(st);
                    if (t.ElseBody != null) foreach (var st in t.ElseBody) Walk(st);
                    return;
            }
        }

        foreach (var s in ast.GlobalStatements) Walk(s);
        var result = new HashSet<string>(
            counts.Where(kv => kv.Value > 1).Select(kv => kv.Key));

        // `global x` inside a function marks x as mutated from function scope.
        void WalkGlobals(Statement? s)
        {
            switch (s)
            {
                case null: return;
                case GlobalStmt g: foreach (var n in g.Names) result.Add(n); return;
                case Block b: foreach (var st in b.Statements) WalkGlobals(st); return;
                case IfStmt f:
                    WalkGlobals(f.ThenBranch);
                    WalkGlobals(f.ElseBranch);
                    foreach (var e in f.ElifBranches) WalkGlobals(e.Item2);
                    return;
                case WhileStmt w: WalkGlobals(w.Body); return;
                case ForStmt fo: WalkGlobals(fo.Body); return;
                case TryStmt t:
                    foreach (var st in t.Body) WalkGlobals(st);
                    foreach (var (_, h) in t.Handlers) foreach (var st in h) WalkGlobals(st);
                    if (t.Finally != null) foreach (var st in t.Finally) WalkGlobals(st);
                    return;
            }
        }
        foreach (var fn in ast.Functions) WalkGlobals(fn.Body);

        return result;
    }

    private void ScanGlobals(ProgramNode ast, ModuleScope? scope = null)
    {
        var reassigned = CollectModuleReassignedNames(ast);

        // Collect every member name used as an assignment target anywhere in this module
        // (recursing into class methods and nested blocks). This forms the superset of all
        // class fields used to flag a read of an undefined instance attribute.
        foreach (var stmt in ast.GlobalStatements)
        {
            CollectAssignedMemberNames(stmt);
            CollectBoolNames(stmt);
        }
        foreach (var fn in ast.Functions) CollectBoolNames(fn);

        foreach (var stmt in ast.GlobalStatements)
        {
            string name = "";
            string type = "";
            Expression? initializer = null;

            // A bare module-level `raise CompileError(...)` in an imported module is an
            // arch/chip guard whose enclosing if/match was folded away. Record it so a use
            // of any symbol from this module reports the guard's message (see
            // EmitRegularFunctionCall); aborting here would be too eager — the module may
            // be pulled in transitively (hal/__init__.py) without its symbols being used.
            if (stmt is RaiseStmt guard && guard.ErrorType == "CompileError"
                && !string.IsNullOrEmpty(currentModulePrefix)
                && !moduleGuardErrors.ContainsKey(currentModulePrefix))
            {
                moduleGuardErrors[currentModulePrefix] = (
                    guard.Message.Length > 0 ? guard.Message : "module not supported on this target",
                    currentSourceFile, guard.Line);
                continue;
            }

            if (stmt is VarDecl varDecl)
            {
                name = varDecl.Name;
                type = varDecl.VarType;
                initializer = varDecl.Init;

                if (type == "bytearray" && initializer != null)
                    TryRegisterModuleBytearray(name, initializer);

                if ((type == "str" || type == "const[str]") && initializer is StringLiteral vdStr)
                    strConstantVariables[currentModulePrefix + name] = vdStr.Value;
            }
            else if (stmt is AssignStmt assign)
            {
                if (assign.Target is VariableExpr varExpr)
                {
                    name = varExpr.Name;
                    initializer = assign.Value;

                    // Unannotated module-level `name = bytearray(...)` (MicroPython idiom):
                    // register the fixed buffer just like the annotated form.
                    TryRegisterModuleBytearray(name, initializer);
                }
            }
            else if (stmt is AnnAssign annAssign)
            {
                name = annAssign.Target;
                type = annAssign.Annotation;
                initializer = annAssign.Value;

                // A `const[...]` annotation marks the name immutable; record it so a later
                // assignment to it is rejected (see VisitAssign's reassignment guard).
                if (type.StartsWith("const[") && type.EndsWith("]"))
                    declaredConstants.Add(name);

                // A module-level string constant (`str` or `const[str]`). Register its
                // compile-time value under the module-global key so ResolveStrConstant resolves
                // it for subscripting (S[i]), len(S) and iteration — previously these silently
                // dropped because ScanGlobals (which owns module globals) never recorded it.
                // (Done before the const[...] dispatch below, which would otherwise consume
                // const[str] as a malformed flash array.)
                if ((type == "str" || type == "const[str]") && initializer is StringLiteral strLit)
                    strConstantVariables[currentModulePrefix + name] = strLit.Value;

                // Detect const[uint8[N]] flash array annotation.
                if (type.StartsWith("const[") && type.EndsWith("]"))
                {
                    string constInner = type.Substring(6, type.Length - 7); // strip "const[" and "]"
                    int ciB = constInner.IndexOf('[');
                    int ciC = constInner.LastIndexOf(']');
                    if (ciB != -1 && ciC == constInner.Length - 1 && ciC > ciB + 1)
                    {
                        string ciNum = constInner.Substring(ciB + 1, ciC - ciB - 1);
                        if (!string.IsNullOrEmpty(ciNum) && ciNum.All(char.IsDigit))
                        {
                            int count = int.Parse(ciNum);
                            DataType elemDt = DataTypeExtensions.StringToDataType(constInner.Substring(0, ciB));
                            if (elemDt == DataType.UINT8)
                            {
                                arraySizes[name] = count;
                                arrayElemTypes[name] = elemDt;
                                flashArrays.Add(name);

                                // Collect FlashData so Generate() can inject it into the
                                // main function body; ScanGlobals runs before VisitFunction.
                                var bytes = new List<int>(Enumerable.Repeat(0, count));
                                if (initializer is ListExpr le)
                                {
                                    for (int k = 0; k < Math.Min(count, le.Elements.Count); k++)
                                        if (TryEvalElemConst(le.Elements[k], out int v))
                                            bytes[k] = v;
                                }
                                pendingFlashData.Add(new FlashData(name, bytes));
                            }
                        }
                    }
                }
                else if (type.StartsWith("list[") && type.EndsWith("]"))
                {
                    string elemTypeName = type.Substring(5, type.Length - 6);
                    DataType elemDt = DataTypeExtensions.StringToDataType(elemTypeName);
                    listVarElemTypes[name] = elemDt;
                }
                else
                {
                    int bracket = type.IndexOf('[');
                    int close = type.LastIndexOf(']');
                    if (bracket != -1 && close != -1 && close == type.Length - 1 && close > bracket + 1)
                    {
                        string inner = type.Substring(bracket + 1, close - bracket - 1);
                        if (!string.IsNullOrEmpty(inner) && inner.All(char.IsDigit))
                        {
                            int count = int.Parse(inner);
                            DataType elemDt = DataTypeExtensions.StringToDataType(type.Substring(0, bracket));
                            arraySizes[name] = count;
                            arrayElemTypes[name] = elemDt;
                            moduleSramArrays.Add(name);
                        }
                    }
                }
            }
            else if (stmt is ClassDef classDef)
            {
                var oldPrefix = currentModulePrefix;
                currentModulePrefix += classDef.Name + "_";
                classModuleMap[classDef.Name] = oldPrefix;

                var isEnum = classDef.Bases.Contains("Enum") || classDef.Bases.Contains("IntEnum");

                if (classDef.Body is Block block)
                {
                    foreach (var innerStmt in block.Statements)
                    {
                        var innerName = "";
                        var innerType = "";
                        Expression? innerInit = null;

                        switch (innerStmt)
                        {
                            case VarDecl vDecl:
                                innerName = vDecl.Name;
                                innerType = vDecl.VarType;
                                innerInit = vDecl.Init;
                                break;
                            case AssignStmt { Target: VariableExpr iVar } iAssign:
                                innerName = iVar.Name;
                                innerInit = iAssign.Value;
                                break;
                            case AnnAssign iAnnAssign:
                                innerName = iAnnAssign.Target;
                                innerType = iAnnAssign.Annotation;
                                innerInit = iAnnAssign.Value;
                                break;
                        }

                        if (string.IsNullOrEmpty(innerName) || innerInit == null) continue;
                        try
                        {
                            var val = EvaluateConstantExpr(innerInit);
                            var isAllUpper = innerName.All(c => !char.IsLower(c));

                            if (isAllUpper || isEnum)
                            {
                                globals[currentModulePrefix + innerName] = new SymbolInfo
                                    { IsMemoryAddress = false, Value = val };
                            }
                            else
                            {
                                mutableGlobals[currentModulePrefix + innerName] =
                                    DataTypeExtensions.StringToDataType(innerType);
                            }
                        }
                        catch
                        {
                            if (!isEnum)
                            {
                                mutableGlobals[currentModulePrefix + innerName] =
                                    DataTypeExtensions.StringToDataType(innerType);
                            }
                        }
                    }
                }

                currentModulePrefix = oldPrefix;
            }

            if (!string.IsNullOrEmpty(name) && initializer != null)
            {
                // Dict/set literals bind their AST as a compile-time lookup table -- no
                // storage, no constant fold (EvaluateConstantExpr would throw and the
                // fallback would mis-register them as 1-byte mutable globals).
                if (initializer is DictExpr dictInit)
                {
                    dictLiteralBindings[currentModulePrefix + name] = dictInit;
                    continue;
                }
                if (initializer is SetExpr setInit)
                {
                    setLiteralBindings[currentModulePrefix + name] = setInit;
                    continue;
                }

                try
                {
                    // `x = SOME_CONST` registers x as a constant ALIAS -- but only when x
                    // is never written again: a mutable variable whose INITIALIZER is a
                    // named constant (state: uint8 = IDLE, then state = HEAT in the loop)
                    // must stay a runtime global, or every later write silently vanishes.
                    if (initializer is VariableExpr varExprInit && !reassigned.Contains(name))
                    {
                        SymbolInfo? sourceInfo = null;
                        string lookupLocal = currentModulePrefix + varExprInit.Name;

                        if (globals.TryGetValue(lookupLocal, out var localSym))
                        {
                            sourceInfo = localSym;
                        }
                        else
                        {
                            foreach (var modName in modules.Keys)
                            {
                                string modKey = modName + "_" + varExprInit.Name;
                                if (globals.TryGetValue(modKey, out var modSym))
                                {
                                    sourceInfo = modSym;
                                    break;
                                }
                            }
                        }

                        if (sourceInfo.HasValue)
                        {
                            globals[currentModulePrefix + name] = sourceInfo.Value;
                            continue;
                        }
                    }

                    int val = EvaluateConstantExpr(initializer);
                    bool isMemoryAddress = false;

                    if (initializer is CallExpr callInit && callInit.Callee is VariableExpr cVar)
                    {
                        if ((cVar.Name == "ptr" && intrinsicNames.Contains("ptr")) || cVar.Name == "PIORegister")
                        {
                            isMemoryAddress = true;
                        }
                    }

                    if (!string.IsNullOrEmpty(type) && (type.Contains("ptr") || type.Contains("PIORegister")))
                    {
                        isMemoryAddress = true;
                    }

                    if (isMemoryAddress)
                    {
                        var info = new SymbolInfo
                            { IsMemoryAddress = true, Value = val, Type = DataTypeExtensions.StringToDataType(type) };
                        globals[currentModulePrefix + name] = info;
                        if (scope != null) scope.Globals[name] = info;
                    }
                    else
                    {
                        bool isAllUpper = name.All(c => !char.IsLower(c));
                        if (isAllUpper)
                        {
                            var info = new SymbolInfo { IsMemoryAddress = false, Value = val };
                            globals[currentModulePrefix + name] = info;
                            if (scope != null) scope.Globals[name] = info;
                        }
                        else
                        {
                            DataType t = DataTypeExtensions.StringToDataType(type);
                            mutableGlobals[currentModulePrefix + name] = t;
                            if (scope != null) scope.MutableGlobals[name] = t;
                        }
                    }
                }
                catch
                {
                    // SRAM arrays (already in moduleSramArrays) must not be added to
                    // mutableGlobals here — their size is determined by ArrayStore/ArrayLoad
                    // instructions in the StackAllocator, which uses count * elemSize.
                    // Adding them with StringToDataType("T[N]") → UNKNOWN (1 byte) would
                    // under-allocate SRAM and cause layout corruption.
                    if (moduleSramArrays.Contains(name)) continue;
                    DataType t = DataTypeExtensions.StringToDataType(type);
                    mutableGlobals[currentModulePrefix + name] = t;
                    if (scope != null) scope.MutableGlobals[name] = t;
                    if (string.IsNullOrEmpty(type))
                        widenableGlobals.Add(currentModulePrefix + name);
                }

                // Track module-level singleton instances (e.g. `mem8 = _Mem8()`) so
                // that subscript and method dispatch via GetValClass works when these
                // singletons are imported by user code.
                if (initializer is CallExpr ctorCallInst
                    && ctorCallInst.Callee is VariableExpr ctorVarInst)
                {
                    string fullKey = currentModulePrefix + name;
                    if (classModuleMap.TryGetValue(ctorVarInst.Name, out var classMod) && classMod != null)
                    {
                        instanceClasses[fullKey] = classMod + ctorVarInst.Name;
                    }
                    else
                    {
                        // The class was imported through a facade re-export (e.g. wifi.py does
                        // `from pymcu.hal.wifi import CYW43`, itself re-exported from the concrete
                        // module). Its concrete module isn't in classModuleMap yet (scan order),
                        // so record the import-resolved name; the dispatch maps it to the concrete
                        // class via ResolveConcreteClass once every class is scanned.
                        string rc = ResolveCallee(ctorVarInst.Name);
                        if (rc.Contains('_') && rc != ctorVarInst.Name && !intrinsicNames.Contains(ctorVarInst.Name))
                            instanceClasses[fullKey] = rc;
                    }
                }
            }
        }

        NarrowLiteralOnlyGlobals(ast);
    }

    /// <summary>
    /// Gives an unannotated module-level integer the width that holds every literal assigned to
    /// it. Without this the FIRST store fixed the width and every later value was truncated into
    /// it, in silence: `b = 5` then `b = 300` stored 44, and printed 44 on the board. Module
    /// level is the MicroPython and CircuitPython shape -- those programs have no def main() at
    /// all -- so it was the default spelling for anyone arriving from either port.
    /// </summary>
    private void NarrowLiteralOnlyGlobals(ProgramNode ast)
    {
        // A global written from inside a function is not literal-only: those assignments are not
        // in this scan's reach, so the name keeps whatever width it had.
        var assignedInFunctions = new HashSet<string>();
        foreach (var fn in ast.Functions) CollectAssignedNames(fn.Body, assignedInFunctions);

        var widths = CollectLiteralOnlyWidths(ast.GlobalStatements, assignedInFunctions);
        foreach (var kv in widths)
        {
            string key = currentModulePrefix + kv.Key;
            // Only names this module actually holds as a mutable global. A name carrying a
            // written annotation never reaches here: the scan above drops it.
            if (!mutableGlobals.ContainsKey(key)) continue;
            mutableGlobals[key] = kv.Value;
            widenableGlobals.Remove(key);
        }
    }

    /// <summary>
    /// Mark every field of a MODULE-LEVEL instance that some function assigns to, so it is
    /// never tracked as a compile-time constant.
    ///
    /// Functions are lowered in an order the program does not control, so a read in one
    /// function could be folded against the constructor's value while the write that changes
    /// it lives in a function lowered later: `obj = Box(0)` at module level, `obj.n = 77` in
    /// setup(), and main() printing obj.n answered 0, with the store dead and eliminated
    /// because nothing read the name. Writing and reading in the SAME function worked, which
    /// is what made it look like an ISR problem rather than an ordering one.
    ///
    /// Only a field assigned OUTSIDE the constructor is marked. A Pin's `_bit` is written once
    /// in __init__ and read as a compile-time constant forever after, and must stay that way.
    /// </summary>
    private void MarkModuleInstanceMutableFields(ProgramNode ast)
    {
        // topLevelInstanceTargets is the set the entry file already records for exactly this
        // shape, `name = Ctor(...)` at module level, and it is filled before the class scan,
        // which classFieldLayout is not.
        var moduleInstances = topLevelInstanceTargets;
        if (moduleInstances.Count == 0) return;

        void Walk(Statement? st)
        {
            switch (st)
            {
                case null: return;
                case Block b: foreach (var cs in b.Statements) Walk(cs); return;
                case AssignStmt { Target: MemberAccessExpr { Object: VariableExpr ov } ma }
                    when moduleInstances.Contains(ov.Name):
                    foreach (var key in new[] { currentModulePrefix + ov.Name + "_" + ma.Member,
                                                ov.Name + "_" + ma.Member })
                    {
                        killedConstants.Add(key);
                        moduleInstanceMutableFields.Add(key);
                    }
                    return;
                case AugAssignStmt { Target: MemberAccessExpr { Object: VariableExpr ov2 } ma2 }
                    when moduleInstances.Contains(ov2.Name):
                    foreach (var key in new[] { currentModulePrefix + ov2.Name + "_" + ma2.Member,
                                                ov2.Name + "_" + ma2.Member })
                    {
                        killedConstants.Add(key);
                        moduleInstanceMutableFields.Add(key);
                    }
                    return;
                case IfStmt i:
                    Walk(i.ThenBranch);
                    foreach (var (_, br) in i.ElifBranches) Walk(br);
                    Walk(i.ElseBranch);
                    return;
                case WhileStmt w: Walk(w.Body); return;
                case ForStmt f: Walk(f.Body); return;
                case WithStmt wi: Walk(wi.Body); return;
                case MatchStmt m: foreach (var br in m.Branches) Walk(br.Body); return;
                case TryStmt t:
                    foreach (var cs in t.Body) Walk(cs);
                    foreach (var (_, h) in t.Handlers) foreach (var cs in h) Walk(cs);
                    if (t.ElseBody != null) foreach (var cs in t.ElseBody) Walk(cs);
                    if (t.Finally != null) foreach (var cs in t.Finally) Walk(cs);
                    return;
            }
        }

        foreach (var func in ast.Functions) Walk(func.Body);
    }

    private void ScanFunctions(ProgramNode ast, ModuleScope? scope = null)
    {
        MarkModuleInstanceMutableFields(ast);

        foreach (var func in ast.Functions)
        {
            string fullName = currentModulePrefix + func.Name;

            if (func.IsAsync)
                throw UserError(
                    $"async def '{func.Name}': the coroutine-to-state-machine lowering is not " +
                    "implemented yet. The syntax parses (this is the foundation); the transform " +
                    "is the next step. For now write the future as a small class with a poll() " +
                    "method driven from a cooperative loop -- the zero-cost pattern async lowers to.");

            // @asm_pio / @rp2.asm_pio: the body is PIO assembly, not CPU code.
            // Assemble it now and register it; never lower it as a function.
            if (func.IsPioProgram)
            {
                try
                {
                    pioPrograms[fullName] = PyMCU.Frontend.Pio.PioAssembler.Assemble(func);
                    if (currentModulePrefix == "" || currentModulePrefix == null)
                        pioPrograms[func.Name] = pioPrograms[fullName];
                }
                catch (PyMCU.Frontend.Pio.PioAsmException ex)
                {
                    throw UserError($"in PIO program '{func.Name}': {ex.Message}");
                }
                continue;
            }

            functionReturnTypes[fullName] = func.ReturnType;
            var @params = new List<string>();
            var paramTypes = new List<DataType>();
            foreach (var p in func.Params)
            {
                // A fixed-array parameter type (`uint8[4]`) is not a real subroutine ABI: it has
                // no scalar register form and was silently misread as a ZCA handler param, so the
                // body was never compiled and the call failed only at link time. Reject it with a
                // pointer to the supported idiom (an array argument is passed by reference as a
                // `bytearray`), instead of emitting a dangling `undefined reference`.
                if (IsFixedArrayParamType(p.Type))
                    throw UserError(
                        $"parameter '{p.Name}' of '{func.Name}' has a fixed-array type '{p.Type}'; " +
                        "pass an array to a function as a 'bytearray' (by reference), " +
                        $"e.g. `def {func.Name}({p.Name}: bytearray, ...)`");
                @params.Add(p.Name);
                paramTypes.Add(DataTypeExtensions.StringToDataType(p.Type));
            }

            functionParams[fullName] = @params;
            functionParamTypes[fullName] = paramTypes;
            functionParamDefaults[fullName] = func.Params.Select(p => p.DefaultValue).ToList();
            functionModulePrefix[fullName] = currentModulePrefix ?? "";

            if (scope != null)
            {
                scope.FunctionReturnTypes[func.Name] = func.ReturnType;
                scope.FunctionParams[func.Name] = @params;
            }

            if (func.IsExtern)
            {
                externFunctionMap[fullName] = func.ExternSymbol;
            }
            else if (func.IsInline)
            {
                RegisterInlineFunction(func, fullName, scope);
            }
            else
            {
                // If the first parameter has an unknown (class) type, this is a ZCA-parameterized
                // handler (e.g. def on_irq(pin: Pin)). Store for on-demand synthesis; do NOT
                // add to functionsToCompile because the body references ZCA fields that are
                // only known at the call site.
                var listParam = func.Params.FirstOrDefault(p => p.Type.StartsWith("list["));
                if (listParam != null)
                    throw UserError(
                        $"function '{func.Name}': parameter '{listParam.Name}: {listParam.Type}' -- " +
                        "list parameters are not supported (the function would be silently " +
                        "dropped and fail at link time). Use 'bytearray' for byte buffers, or " +
                        "mark the function @inline so the list resolves at the call site");
                // A parameter annotated with a class type carries a ZCA instance, which has no
                // subroutine ABI: the fields live in the caller's frame, so the body only has
                // meaning expanded at the call site. That is what @inline already does for the
                // same shape, so register these the same way and let the call site expand them.
                // Without it the two positions failed differently and both badly: a class in the
                // first parameter was kept only for on-demand ISR synthesis and never emitted
                // (`undefined reference` from the linker), and a class in any other position was
                // lowered as an ordinary function whose field reads were never bound, so it
                // silently computed on whatever the RAM held.
                bool hasZcaFirstParam = func.Params.Count > 0 && IsZcaHandlerParamType(func.Params[0].Type);
                bool hasZcaParam = func.Params.Any(p => IsZcaInstanceParamType(p.Type));

                // The first-position form is also the ISR handler shape (`def on_irq(pin: Pin)`),
                // which is synthesized separately from the AST when the handler is registered.
                if (hasZcaFirstParam)
                    zcaHandlerAstNodes[fullName] = (func, currentModulePrefix ?? "");

                if (hasZcaParam)
                {
                    RegisterInlineFunction(func, fullName, scope);
                }
                else if (!hasZcaFirstParam)
                {
                    functionsToCompile.Add(new FunctionEntry
                        { Prefix = currentModulePrefix, Func = func, SourceFile = currentSourceFile });
                }
                // An UNANNOTATED first parameter lands in neither: it is the decorator shape the
                // stdlib uses (`def inline(f): return f`), which has never been lowered and must
                // not start being lowered here.
            }
        }

        foreach (var stmt in ast.GlobalStatements)
        {
            if (stmt is ClassDef classDef)
            {
                bool isEnum = classDef.Bases.Contains("Enum") || classDef.Bases.Contains("IntEnum");
                if (isEnum) continue;

                bool isException = classDef.Bases.Any(b =>
                    b is "Exception" or "BaseException" || constantVariables.ContainsKey(b) && exceptionNames.Contains(b));
                if (isException)
                {
                    constantVariables[classDef.Name] = nextUserExceptionCode++;
                    exceptionNames.Add(classDef.Name);
                    continue;
                }

                if (classDef.Body != null)
                {
                    // Multiple inheritance is not supported (the ZCA model assumes a single base
                    // for layout + dispatch). Reject it clearly instead of later failing with an
                    // opaque "undefined function 'C_foo'" when a second base's method is called.
                    var realBases = classDef.Bases
                        .Where(b => b is not ("Enum" or "IntEnum" or "object")).ToList();
                    if (realBases.Count > 1)
                        throw UserError(
                            $"class '{classDef.Name}' uses multiple inheritance " +
                            $"({string.Join(", ", realBases)}), which PyMCU does not support; " +
                            "use composition (hold an instance as a field) or a single base class");

                    classNames.Add(classDef.Name);
                    if (classDef.IsValue) valueClasses.Add(classDef.Name);
                    var oldPrefix = currentModulePrefix;
                    var classPrefix = currentModulePrefix + classDef.Name + "_";
                    currentModulePrefix = classPrefix;

                    // Ensure an entry exists in classDirectMethods even for empty classes.
                    string classKey = classPrefix.Substring(0, classPrefix.Length - 1);
                    if (!classDirectMethods.ContainsKey(classKey))
                        classDirectMethods[classKey] = new HashSet<string>();

                    if (classDef.Body is Block block)
                    {
                        // RFC 0001: derive the field layout once per class. A class with a
                        // single primitive field is eligible to be returned by value from a
                        // non-@inline factory (Model B register-packed handle).
                        var clsLayout = DeriveFieldLayout(block);
                        // A subclass with no __init__ of its own inherits the base's fields, so an
                        // OVERRIDDEN method can resolve them (`self.a` otherwise errors "not a
                        // member"). Inherit the layout ONLY when the base is a slot class: that is
                        // the case where the override would be outlined and needs the field layout.
                        // Plain virtual/@inline HAL classes (base NOT in slotClasses) keep an empty
                        // layout so their construction model is unchanged (inheriting it there
                        // wrongly promotes the subclass to a slot and crashes codegen).
                        if (clsLayout.Count == 0 && !InitCallsSuperInit(block))
                            foreach (var baseName in classDef.Bases)
                            {
                                if (baseName is "Enum" or "IntEnum") continue;
                                // Inherit the base's field layout so an inherited/overridden method
                                // can resolve `self.<field>`. Allow it for a real ZCA data class --
                                // a slot (>= 2 fields) OR a single-field data class (zcaFactoryClasses)
                                // -- but NOT for a virtual/@inline HAL class (neither), whose multi-
                                // field layout would wrongly promote the subclass to a slot (A66).
                                bool IsDataClass(string k) =>
                                    slotClasses.Contains(k) || zcaFactoryClasses.ContainsKey(k);
                                string bk = oldPrefix + baseName;
                                if (classFieldLayout.TryGetValue(bk, out var bl) && bl.Count > 0
                                    && IsDataClass(bk))
                                {
                                    clsLayout = bl;
                                    break;
                                }

                                if (classFieldLayout.TryGetValue(baseName, out var bl2) && bl2.Count > 0
                                    && IsDataClass(baseName))
                                {
                                    clsLayout = bl2;
                                    break;
                                }
                            }

                        // A subclass __init__ that calls super().__init__() also owns the base's
                        // fields (the base ctor sets them on the same self). Merge the base layout
                        // AHEAD of the subclass's own fields (base ctor runs first), so an outlined
                        // method on the subclass receives every field and `self.<inherited>` resolves
                        // instead of erroring "not a member". Runs even when the subclass adds no own
                        // field (a super-only __init__, common at intermediate/leaf levels of a deep
                        // chain) -- merged becomes the full base layout, which keeps the chain
                        // propagating through any number of levels (L0->L1->...->Ln).
                        if (InitCallsSuperInit(block))
                            foreach (var baseName in classDef.Bases)
                            {
                                if (baseName is "Enum" or "IntEnum") continue;
                                List<(string Field, string Type, string SourceParam)>? baseLayout = null;
                                if (classFieldLayout.TryGetValue(oldPrefix + baseName, out var blm) && blm.Count > 0)
                                    baseLayout = blm;
                                else if (classFieldLayout.TryGetValue(baseName, out var blm2) && blm2.Count > 0)
                                    baseLayout = blm2;
                                if (baseLayout == null) continue;

                                var ownFields = new HashSet<string>(clsLayout.Select(f => f.Field));
                                var merged = new List<(string Field, string Type, string SourceParam)>();
                                foreach (var bf in baseLayout)
                                    if (!ownFields.Contains(bf.Field)) merged.Add(bf);
                                merged.AddRange(clsLayout);
                                clsLayout = merged;
                                break;
                            }

                        classFieldLayout[classKey] = clsLayout;
                        // Record any field whose type is itself a class, so a member read can
                        // recover the nested class identity a single-field ZCA loses on collapse.
                        // (1) param-typed fields (`self.x = pin` where pin: SomeClass): the layout
                        //     already carries the class name as the field type.
                        foreach (var (fld, ty, _) in clsLayout)
                        {
                            if (string.IsNullOrEmpty(ty) || IsScalarTypeName(ty)) continue;
                            fieldClasses[classKey + "|" + fld] = ResolveCallee(ty);
                        }
                        // (2) constructor-assigned fields (`self.x = SomeClass(...)`): the layout
                        //     records these as a scalar (the class collapses), so recover the class
                        //     from the __init__ RHS. Resolved here in the defining module's scope.
                        foreach (var s0 in block.Statements)
                            if (s0 is FunctionDef fdI && fdI.Name == "__init__")
                                foreach (var st in fdI.Body.Statements)
                                    if (st is AssignStmt asg2 && asg2.Target is MemberAccessExpr m2
                                        && m2.Object is VariableExpr sv2 && sv2.Name == "self"
                                        && asg2.Value is CallExpr ce2 && ce2.Callee is VariableExpr cv2
                                        && !IsScalarTypeName(cv2.Name)
                                        && !fieldClasses.ContainsKey(classKey + "|" + m2.Member))
                                        fieldClasses[classKey + "|" + m2.Member] = ResolveCallee(cv2.Name);
                        if (InitCallsSuperInit(block)) classInitCallsSuper.Add(classKey);
                        // Note: slotClasses (>= 2 fields) is marked only when an @outline method
                        // is actually present (below), so plain @inline HAL classes with multiple
                        // fields keep their normal virtual-construction path. zcaFactoryClasses is
                        // safe to mark eagerly: it is only consulted in factory-return contexts,
                        // never in direct construction.
                        if (clsLayout.Count == 1)
                            zcaFactoryClasses[classKey] = clsLayout[0].Type;

                        // This class body's methods by name, so the field-mutation analysis can
                        // follow a self.<method>() call into the method it names.
                        var classMethods = new Dictionary<string, FunctionDef>();
                        foreach (var s1 in block.Statements)
                            if (s1 is FunctionDef mf) classMethods[mf.Name] = mf;

                        foreach (var inner in block.Statements)
                        {
                            if (inner is FunctionDef func)
                            {
                                // Register as directly-defined BEFORE the inheritance copy so
                                // ResolveMROMethod can distinguish "defined here" from "inherited".
                                classDirectMethods[classKey].Add(func.Name);

                                // A dunder PyMCU never calls compiles quietly and then fails at
                                // whichever use site the reader reaches first, in a different
                                // shape each time (print(v), str(v) and f"{v}" gave three
                                // different messages, none of them mentioning __str__). Say it
                                // where the method is written.
                                if (func.Name is "__str__" or "__repr__" or "__format__"
                                    or "__iter__" or "__next__")
                                {
                                    string why = func.Name is "__iter__" or "__next__"
                                        ? "PyMCU does not run the iterator protocol; iterate the "
                                          + "underlying array, or give the class a method you call by name"
                                        : "PyMCU has no run-time string formatting; print the fields "
                                          + "explicitly, or give the class a method you call by name";
                                    Console.Error.WriteLine(
                                        $"[pymcuc] warning: line {func.Line}: '{classDef.Name}.{func.Name}' is "
                                        + $"defined but never called -- {why}.");
                                }

                                string fullName = currentModulePrefix + func.Name;
                                functionReturnTypes[fullName] = func.ReturnType;
                                var @params = new List<string>();
                                var paramTypes = new List<DataType>();
                                foreach (var p in func.Params)
                                {
                                    @params.Add(p.Name);
                                    paramTypes.Add(DataTypeExtensions.StringToDataType(p.Type));
                                }

                                functionParams[fullName] = @params;
                                functionParamTypes[fullName] = paramTypes;
                                // Methods need their defaults recorded too. Only top-level
                                // functions were, so an outlined method called with an argument
                                // omitted got nothing for that parameter and its body read zero
                                // instead of the declared default.
                                functionParamDefaults[fullName] =
                                    func.Params.Select(p => p.DefaultValue).ToList();

                                if (func.IsPropertyGetter)
                                {
                                    // Record the getter so a bare `obj.<prop>` read is desugared
                                    // into a getter call. @property forces IsInline, so the inline
                                    // registration in the branch below still runs.
                                    string getterClass = classPrefix.Substring(0, classPrefix.Length - 1);
                                    propertyGetters.Add(getterClass + "." + func.Name);
                                }

                                if (func.IsPropertySetter)
                                {
                                    string setterKey = fullName + "___setter";
                                    inlineFunctions[setterKey] = func;
                                    string className = classPrefix.Substring(0, classPrefix.Length - 1);
                                    propertySetters[className + "." + func.PropertyName] = setterKey;
                                }
                                else if (func.IsOutline && func.Name != "__init__")
                                {
                                    // RFC 0001: explicit @outline -- compile this method ONCE as a
                                    // shared subroutine (Model A field-params, or Model B SRAM slot
                                    // for >= 2 fields). After F4 this is redundant with the default
                                    // for outline-safe methods; kept as an explicit request.
                                    //
                                    // @outline on __init__ is not handled here at all: a constructor
                                    // establishes the instance and cannot be shared, and taking it out
                                    // of the ordinary path left `A(s)` unable to find the class, which
                                    // was then reported as the class having no __init__ -- on a file
                                    // whose next line defines one. It takes the undecorated path.
                                    //
                                    // The safety check applies here too. @outline asks for a shared
                                    // body wherever one is possible; it cannot make an unshareable
                                    // one shareable. A body that reaches THROUGH a field
                                    // (`self.inner.get()`) has no standalone form: outlining it
                                    // anyway mangled the call into `self_inner_get` and failed the
                                    // build over a method the program may never call.
                                    if (IsOutlineSafe(func, clsLayout))
                                        RegisterOutlinedMethod(func, classKey, clsLayout, fullName, classMethods);
                                    else
                                        instanceMethodDefs[fullName] = func;
                                }
                                else if (func.IsInline)
                                {
                                    // Once a name is overloaded its bare key is vacated, so a later
                                    // same-named overload must register under its suffixed key — never
                                    // re-occupy the bare key (a TryAdd there would succeed and hide the
                                    // overload from suffix-based resolution; e.g. Pin's 3rd const[str]
                                    // __init__ landing on the bare key and never being found).
                                    if (overloadedFunctions.Contains(fullName))
                                    {
                                        inlineFunctions[fullName + "___" + BuildOverloadSuffix(func.Params)] = func;
                                    }
                                    else if (!inlineFunctions.TryAdd(fullName, func))
                                    {
                                        var existing = inlineFunctions[fullName];
                                        if (existing?.Params != null)
                                        {
                                            var existingSfx = BuildOverloadSuffix(existing.Params);
                                            inlineFunctions[fullName + "___" + existingSfx] = existing;
                                        }

                                        inlineFunctions.Remove(fullName);
                                        overloadedFunctions.Add(fullName);
                                        inlineFunctions[fullName + "___" + BuildOverloadSuffix(func.Params)] = func;
                                    }
                                }
                                else
                                {
                                    // Overloads are a property of @inline methods: the registration
                                    // that keeps them apart by parameter suffix lives in that branch
                                    // only. An undecorated method is outlined by default and has no
                                    // such registration, so a second definition of the same name used
                                    // to REPLACE the first without a word -- and every caller of the
                                    // shape that disappeared failed at its own call site, naming a
                                    // mangled symbol. Say it here, where both definitions are visible.
                                    if (classDirectMethods[classKey].Contains(func.Name)
                                        && !func.IsInline
                                        && (instanceMethodDefs.ContainsKey(fullName)
                                            || inlineFunctions.ContainsKey(fullName)
                                            || outlinedMethods.Contains(fullName)))
                                        throw UserError(
                                            $"class '{classDef.Name}' defines '{func.Name}' more than "
                                            + "once, and overloads are only supported on @inline "
                                            + "methods (an undecorated method is compiled once as a "
                                            + "shared subroutine, which one name cannot address twice)."
                                            + $" Mark every '{func.Name}' @inline to overload by "
                                            + "parameter types, or give them different names.");

                                    // RFC 0001 F4: an undecorated method is OUTLINED BY DEFAULT when
                                    // it is outline-safe (touches self only as self.<field>). This is
                                    // why @inline now means something: without it, a representable
                                    // method is shared, not silently force-inlined per instance.
                                    var defLayout = clsLayout;
                                    if (IsOutlineSafe(func, defLayout))
                                    {
                                        // A single-field mutator that ALSO has explicit returns
                                        // cannot use write-back-via-return (one return slot can't
                                        // carry both a value and the field). Force-inline it so
                                        // self.field aliasing persists the mutation. Registered in
                                        // inlineFunctions ONLY (never functionsToCompile) so it is
                                        // expanded per call site, not compiled standalone (which
                                        // would treat self as numeric and fail).
                                        if (defLayout.Count == 1
                                            && MethodMutatesField(func, defLayout[0].Field, classMethods)
                                            && MethodHasReturnStmt(func))
                                        {
                                            inlineFunctions[fullName] = func;
                                            instanceMethodDefs[fullName] = func;
                                        }
                                        else
                                        {
                                            RegisterOutlinedMethod(func, classKey, defLayout, fullName, classMethods);
                                        }
                                    }
                                    else
                                    {
                                        // Not representable as a shared body (uses self.method(),
                                        // passes self, non-derivable field, or an unhandled construct):
                                        // force-inline is the only way to give it a runtime form, so
                                        // it is registered for expansion ONLY. Compiling it standalone
                                        // as well would bind `self` to nothing -- a body reading
                                        // `self._pin.value()` mangled the field to a call on
                                        // `self__pin`, and the whole program failed to build over a
                                        // method the program may never even call.
                                        instanceMethodDefs[fullName] = func;
                                    }
                                }

                                if (!func.IsPropertySetter)
                                {
                                    methodInstanceTypes[fullName] =
                                        currentModulePrefix.Substring(0, currentModulePrefix.Length - 1);
                                    // Keep every instance method's AST reachable by symbol so a
                                    // super().<method>() can inline-expand the base body even when
                                    // the base method is outlined (not in inlineFunctions).
                                    methodAstByName[fullName] = func;
                                    if (MethodCallsSelfMethod(func)) methodsWithSelfCall.Add(fullName);
                                }
                            }
                            else if (inner is ClassDef nestedClass)
                            {
                                // Nested class (class defined inside another class), e.g.
                                // CircuitPython's alarm.time.TimeAlarm. Register its methods
                                // under the nested prefix so it is ZCA-constructible like a
                                // top-level class (VisitCall finds <prefix>___init__).
                                ScanNestedClassMembers(nestedClass, classPrefix);
                            }
                        }
                    }

                    foreach (var baseName in classDef.Bases)
                    {
                        string basePrefix = oldPrefix + baseName + "_";

                        string ResolveBase()
                        {
                            if (!string.IsNullOrEmpty(oldPrefix))
                            {
                                foreach (var k in inlineFunctions.Keys)
                                {
                                    if (k.StartsWith(basePrefix)) return basePrefix;
                                }
                            }

                            string bare = baseName + "_";
                            foreach (var k in inlineFunctions.Keys)
                            {
                                if (k.StartsWith(bare)) return bare;
                            }

                            return basePrefix;
                        }

                        string resolvedBasePrefix = ResolveBase();
                        string childClassName = classPrefix.Substring(0, classPrefix.Length - 1);
                        classBasePrefixes[childClassName] = resolvedBasePrefix;

                        // Register child → parent edge in the class-children graph.
                        string parentKey = resolvedBasePrefix.EndsWith("_")
                            ? resolvedBasePrefix[..^1] : resolvedBasePrefix;
                        if (!classChildren.TryGetValue(parentKey, out var childSet))
                            classChildren[parentKey] = childSet = new HashSet<string>();
                        childSet.Add(childClassName);

                        var toInherit = new List<KeyValuePair<string, FunctionDef>>();
                        foreach (var kvp in inlineFunctions)
                        {
                            if (kvp.Key.StartsWith(resolvedBasePrefix))
                            {
                                string methodSuffix = kvp.Key.Substring(resolvedBasePrefix.Length);
                                string childKey = classPrefix + methodSuffix;
                                if (!inlineFunctions.ContainsKey(childKey))
                                {
                                    if (kvp.Value != null)
                                        toInherit.Add(new KeyValuePair<string, FunctionDef>(childKey, kvp.Value));
                                }
                            }
                        }

                        foreach (var (childKey, value) in toInherit)
                        {
                            inlineFunctions[childKey] = value;
                            string srcKey = resolvedBasePrefix + childKey[classPrefix.Length..];

                            if (functionParams.TryGetValue(srcKey, out var p)) functionParams[childKey] = p;
                            if (functionParamTypes.TryGetValue(srcKey, out var pt)) functionParamTypes[childKey] = pt;
                            if (functionReturnTypes.TryGetValue(srcKey, out var rt)) functionReturnTypes[childKey] = rt;

                            methodInstanceTypes[childKey] = classPrefix.Substring(0, classPrefix.Length - 1);
                        }
                    }

                    currentModulePrefix = oldPrefix;
                }
            }
        }
    }

    // RFC 0001 Model A: derives the ordered runtime-field layout of a ZCA class
    // from its __init__ body. Each `self.<field> = <expr>` becomes a (field, type)
    // entry; the type is taken from the matching __init__ parameter when the RHS is
    // that parameter, else defaults to uint8. Used to synthesize the leading params
    // of an @outline method.
    // Recursively walk a statement (into class bodies, methods and nested blocks) and record
    // every member name used as an assignment target. Defensive about statement types so it
    // never UNDER-collects (a missed write would risk a false "no attribute" error).
    private void CollectAssignedMemberNames(Statement? s)
    {
        switch (s)
        {
            case null: return;
            case Block b: foreach (var st in b.Statements) CollectAssignedMemberNames(st); return;
            case ClassDef cd: CollectAssignedMemberNames(cd.Body); return;
            case FunctionDef fd: CollectAssignedMemberNames(fd.Body); return;
            case IfStmt iff:
                CollectAssignedMemberNames(iff.ThenBranch);
                foreach (var br in iff.ElifBranches) CollectAssignedMemberNames(br.Body);
                CollectAssignedMemberNames(iff.ElseBranch);
                return;
            case WhileStmt w: CollectAssignedMemberNames(w.Body); return;
            case ForStmt f:
                // `for self.x in ...` (a member loop target) also writes the member.
                if (f.VarName.Contains('.')) assignedMemberNames.Add(f.VarName[(f.VarName.LastIndexOf('.') + 1)..]);
                CollectAssignedMemberNames(f.Body);
                return;
            case WithStmt wi: CollectAssignedMemberNames(wi.Body); return;
            case MatchStmt m: foreach (var br in m.Branches) CollectAssignedMemberNames(br.Body); return;
            case TryStmt t:
                foreach (var st in t.Body) CollectAssignedMemberNames(st);
                foreach (var (_, h) in t.Handlers) foreach (var st in h) CollectAssignedMemberNames(st);
                if (t.Finally != null) foreach (var st in t.Finally) CollectAssignedMemberNames(st);
                return;
            case AssignStmt a: RecordMemberAssignTarget(a.Target); return;
            case AugAssignStmt ag: RecordMemberAssignTarget(ag.Target); return;
            case AnnAssign an:
                // AnnAssign.Target is a (possibly dotted) name string, e.g. "self._buf".
                int dot = an.Target.LastIndexOf('.');
                if (dot >= 0) assignedMemberNames.Add(an.Target[(dot + 1)..]);
                return;
        }
    }

    // Walk a statement and classify every plain-name binding as bool or not-bool (see the
    // boolNames/nonBoolNames comment in State.cs). Only a True/False literal binds a bool;
    // everything else -- a comparison (an integer in PyMCU), a loop variable, a parameter --
    // vetoes the name for the whole program.
    private void CollectBoolNames(Statement? s)
    {
        switch (s)
        {
            case null: return;
            case Block b: foreach (var st in b.Statements) CollectBoolNames(st); return;
            case ClassDef cd: CollectBoolNames(cd.Body); return;
            case FunctionDef fd:
                foreach (var p in fd.Params) nonBoolNames.Add(p.Name);
                CollectBoolNames(fd.Body);
                return;
            case IfStmt iff:
                CollectBoolNames(iff.ThenBranch);
                foreach (var br in iff.ElifBranches) CollectBoolNames(br.Body);
                CollectBoolNames(iff.ElseBranch);
                return;
            case WhileStmt w: CollectBoolNames(w.Body); return;
            case ForStmt f:
                nonBoolNames.Add(f.VarName);
                if (!string.IsNullOrEmpty(f.Var2Name)) nonBoolNames.Add(f.Var2Name);
                CollectBoolNames(f.Body);
                return;
            case WithStmt wi: CollectBoolNames(wi.Body); return;
            case MatchStmt m: foreach (var br in m.Branches) CollectBoolNames(br.Body); return;
            case TryStmt t:
                foreach (var st in t.Body) CollectBoolNames(st);
                foreach (var (_, h) in t.Handlers) foreach (var st in h) CollectBoolNames(st);
                if (t.ElseBody != null) foreach (var st in t.ElseBody) CollectBoolNames(st);
                if (t.Finally != null) foreach (var st in t.Finally) CollectBoolNames(st);
                return;
            case AssignStmt { Target: VariableExpr av } a: NoteBoolBinding(av.Name, a.Value); return;
            case AssignStmt { Target: TupleExpr tup }:
                foreach (var e in tup.Elements)
                    if (e is VariableExpr tv) nonBoolNames.Add(tv.Name);
                return;
            case AugAssignStmt { Target: VariableExpr gv }: nonBoolNames.Add(gv.Name); return;
            case VarDecl vd: NoteBoolBinding(vd.Name, vd.Init); return;
            case AnnAssign an when !an.Target.Contains('.'): NoteBoolBinding(an.Target, an.Value); return;
        }
    }

    private void NoteBoolBinding(string name, Expression? value)
    {
        if (value is BooleanLiteral) boolNames.Add(name);
        else nonBoolNames.Add(name);
    }

    // True when `name` is bound to True/False everywhere in the program, so interpolating it
    // must print Python's words rather than the underlying byte.
    private bool IsBoolName(string name) => boolNames.Contains(name) && !nonBoolNames.Contains(name);

    private void RecordMemberAssignTarget(Expression target)
    {
        switch (target)
        {
            case MemberAccessExpr ma: assignedMemberNames.Add(ma.Member); break;
            case IndexExpr { Target: MemberAccessExpr ma2 }: assignedMemberNames.Add(ma2.Member); break;
            case TupleExpr tup: foreach (var e in tup.Elements) RecordMemberAssignTarget(e); break;
        }
    }

    // True when the method body calls a sibling method on self (self.<m>(...)). Such a method,
    // if outlined, binds the self-call statically to its defining class; called on a subclass
    // instance it must be force-inlined instead so the call dispatches to the concrete override.
    private static bool MethodCallsSelfMethod(FunctionDef method)
    {
        bool found = false;
        void E(Expression? e)
        {
            if (found || e == null) return;
            switch (e)
            {
                case CallExpr { Callee: MemberAccessExpr { Object: VariableExpr { Name: "self" } } }:
                    found = true; return;
                case CallExpr c: E(c.Callee); foreach (var a in c.Args) E(a); return;
                case MemberAccessExpr ma: E(ma.Object); return;
                case BinaryExpr b: E(b.Left); E(b.Right); return;
                case UnaryExpr u: E(u.Operand); return;
                case TernaryExpr t: E(t.Condition); E(t.TrueVal); E(t.FalseVal); return;
                case IndexExpr ix: E(ix.Target); E(ix.Index); return;
                case KeywordArgExpr kw: E(kw.Value); return;
                case TupleExpr tu: foreach (var el in tu.Elements) E(el); return;
                case ListExpr le: foreach (var el in le.Elements) E(el); return;
            }
        }
        void S(Statement? s)
        {
            if (found || s == null) return;
            switch (s)
            {
                case Block bl: foreach (var cs in bl.Statements) S(cs); return;
                case VarDecl vd: E(vd.Init); return;
                case AnnAssign a: E(a.Value); return;
                case AssignStmt asg: E(asg.Value); return;
                case AugAssignStmt aug: E(aug.Value); return;
                case ReturnStmt r: E(r.Value); return;
                case ExprStmt ex: E(ex.Expr); return;
                case IfStmt iff:
                    E(iff.Condition); S(iff.ThenBranch);
                    foreach (var br in iff.ElifBranches) { E(br.Condition); S(br.Body); }
                    S(iff.ElseBranch); return;
                case WhileStmt wh: E(wh.Condition); S(wh.Body); return;
                case ForStmt fr: S(fr.Body); return;
            }
        }
        foreach (var st in method.Body.Statements) S(st);
        return found;
    }

    // True when the class's own __init__ delegates to its base via super().__init__(...).
    // Such a subclass gains the base's fields (set by the base ctor) in addition to its own,
    // so its slot layout must merge the base fields ahead of its own.
    private static bool InitCallsSuperInit(Block classBody)
    {
        FunctionDef? init = null;
        foreach (var s in classBody.Statements)
            if (s is FunctionDef f && f.Name == "__init__") { init = f; break; }
        if (init == null) return false;
        foreach (var st in init.Body.Statements)
        {
            if (st is ExprStmt { Expr: CallExpr { Callee: MemberAccessExpr {
                    Member: "__init__", Object: CallExpr { Callee: VariableExpr { Name: "super" } } } } })
                return true;
        }
        return false;
    }

    private static readonly HashSet<string> ScalarTypeNames = new()
    {
        "uint8", "int8", "uint16", "int16", "uint32", "int32", "uint64", "int64",
        "int", "bool", "float", "void", "None", "str", "bytes", "bytearray",
        "const", "Callable", "gc_ref", "char",
    };

    // True for primitive/built-in type names (and any bracketed form like const[..]/ptr[..]/T[N]).
    // A field type that is NOT one of these is a class name -- tracked in fieldClasses.
    private static bool IsScalarTypeName(string ty)
    {
        if (string.IsNullOrEmpty(ty)) return true;
        if (ty.Contains('[')) return true;
        return ScalarTypeNames.Contains(ty);
    }

    private List<(string Field, string Type, string SourceParam)> DeriveFieldLayout(Block classBody)
    {
        var layout = new List<(string, string, string)>();
        var seen = new HashSet<string>();

        FunctionDef? init = null;
        foreach (var s in classBody.Statements)
            if (s is FunctionDef f && f.Name == "__init__") { init = f; break; }
        if (init == null) return layout;

        var paramTypes = new Dictionary<string, string>();
        foreach (var p in init.Params) paramTypes[p.Name] = p.Type;

        foreach (var s in init.Body.Statements)
        {
            string? field = null;
            Expression? rhs = null;
            string? annotatedType = null;
            if (s is AssignStmt asg && asg.Target is MemberAccessExpr ma
                && ma.Object is VariableExpr sv && sv.Name == "self")
            {
                field = ma.Member;
                rhs = asg.Value;
                annotatedType = asg.AnnotatedType;
            }

            if (field == null || !seen.Add(field)) continue;

            // SourceParam: the __init__ param that directly initializes the field
            // (RHS is a bare parameter), else "" -- needed for factory return lowering.
            string type = "uint8";
            string srcParam = "";
            // An explicit `self.x: T = ...` annotation wins -- the field gets its
            // declared width (otherwise a uint32 field would default to uint8 and
            // truncate, e.g. a timer deadline or a 1<<24 bit mask).
            if (annotatedType != null)
            {
                type = annotatedType.StartsWith("const[") && annotatedType.EndsWith("]")
                    ? annotatedType.Substring(6, annotatedType.Length - 7)
                    : annotatedType;
            }
            if (rhs is VariableExpr rv && paramTypes.TryGetValue(rv.Name, out var pt))
            {
                srcParam = rv.Name;
                if (annotatedType == null)
                    type = pt.StartsWith("const[") && pt.EndsWith("]")
                        ? pt.Substring(6, pt.Length - 7) // const[uint8] -> uint8
                        : pt;
            }
            layout.Add((field, type, srcParam));
        }

        return layout;
    }

    // RFC 0001 F1-F3: register a method as an outlined shared subroutine. >= 2 fields ->
    // Model B SRAM slot (self pointer + BytearrayLoad offsets); else Model A (one param per
    // field). Used by both the explicit @outline branch and the F4 default (an outline-safe
    // undecorated method).
    private void RegisterOutlinedMethod(FunctionDef func, string classKey,
        List<(string Field, string Type, string SourceParam)> layout, string fullName,
        IReadOnlyDictionary<string, FunctionDef>? siblings = null)
    {
        var synthParams = new List<Param>();
        if (layout.Count >= 2) slotClasses.Add(classKey);
        // A single-field method that BOTH writes its field and returns a value cannot be
        // Model A: the field travels by value and the one return slot already carries the
        // returned expression, so the write has nowhere to come back through and is lost.
        // `@outline def bump(self) -> uint8: self.a = self.a + 1; return self.a` returned the
        // right number and left the instance holding the old one. Give the class a slot so the
        // body writes through a pointer instead.
        else if (siblings != null
                 && siblings.Values.Any(m => MethodHasReturnStmt(m)
                        && layout.Any(f => MethodMutatesField(m, f.Field, siblings))))
            slotClasses.Add(classKey);
        if (slotClasses.Contains(classKey))
        {
            synthParams.Add(new Param("self", "bytearray"));
            var offsets = new Dictionary<string, int>();
            int off = 0;
            foreach (var (fld, ty, _) in layout)
            {
                offsets[fld] = off;
                off += DataTypeExtensions.StringToDataType(ty).SizeOf();
            }
            slotMethods.Add(fullName);
            slotMethodFieldOffsets[fullName] = offsets;
        }
        else
        {
            foreach (var (fld, ty, _) in layout)
                synthParams.Add(new Param("self_" + fld, ty));
        }
        for (int pi = 1; pi < func.Params.Count; ++pi)
            synthParams.Add(func.Params[pi]);

        // RFC 0001 (write-back): a single-field (Model A) method that mutates its field but
        // never returns a value loses the mutation, because the field is passed BY VALUE.
        // Rewrite the shared body to RETURN the (updated) field and record the field so the
        // call site copies it back to the instance. The caller routes mutators that have
        // explicit returns to force-inline instead, so here the body always falls through:
        // appending a single `return self.<field>` is sufficient.
        Block body = func.Body;
        string returnType = func.ReturnType;
        if (!slotClasses.Contains(classKey) && layout.Count == 1
            && MethodMutatesField(func, layout[0].Field, siblings) && !MethodHasReturnStmt(func))
        {
            var (field, ftype, _) = layout[0];
            body = new Block();
            body.Statements.AddRange(func.Body.Statements);
            body.Statements.Add(new ReturnStmt(new MemberAccessExpr(new VariableExpr("self"), field)));
            returnType = ftype;
            outlineWriteBack[fullName] = (field, DataTypeExtensions.StringToDataType(ftype));
            if (!zcaWriteBackFields.TryGetValue(classKey, out var wf))
                zcaWriteBackFields[classKey] = wf = new HashSet<string>();
            wf.Add(field);
        }

        var synth = new FunctionDef(func.Name, synthParams, returnType, body, isInline: false);
        functionsToCompile.Add(new FunctionEntry
            { Prefix = currentModulePrefix, Func = synth, SourceFile = currentSourceFile });

        outlinedMethods.Add(fullName);
        outlineFieldLayout[fullName] = layout;
        functionReturnTypes[fullName] = returnType;
        functionParams[fullName] = synthParams.Select(p => p.Name).ToList();
        // Aligned with synthParams (the leading self_<field> ones included), so the call site
        // can index them by position when an argument is omitted.
        functionParamDefaults[fullName] = synthParams.Select(p => p.DefaultValue).ToList();
        functionParamTypes[fullName] = synthParams.Select(p => DataTypeExtensions.StringToDataType(p.Type)).ToList();
    }

    // RFC 0001 F4: is a method safe to outline (compile once, share) instead of force-inline?
    // Safe iff its body touches `self` only as `self.<field>` where <field> is a derivable data
    // field -- never `self.<method>()` and never bare `self` (passed as a value). Any unrecognized
    // node makes it UNSAFE: outlining must be provably correct, otherwise we keep the existing
    // force-inline behavior (zero regression). Methods with user params besides self stay safe
    // (those params become trailing params of the shared body).
    private bool IsOutlineSafe(FunctionDef method,
        List<(string Field, string Type, string SourceParam)> layout)
    {
        if (layout.Count == 0) return false;

        // A field whose type is not a scalar is another ZCA instance (e.g. a Pin
        // stored as `self.pin`). An outlined body shares one copy across instances
        // by passing each field as a runtime parameter, but a ZCA field is
        // compile-time per-instance (a Pin is just its const pin name, no runtime
        // value) — it cannot be passed as a parameter. Such methods must stay
        // force-inlined so `self.pin.<method>()` resolves at each call site.
        var scalarTypes = new HashSet<string>
            { "uint8", "int8", "uint16", "int16", "uint32", "int32", "float", "bool" };
        if (layout.Any(f => !scalarTypes.Contains(f.Type))) return false;

        // A PARAMETER that is another instance cannot be passed either, for the same reason a
        // ZCA field cannot: the instance is compile-time per-instance, not a runtime value a
        // shared body can receive. `def read(self, o: C) -> uint8: return self.n + o.n` was
        // outlined anyway, `self` arrived and `o` did not, and the method answered with the
        // other operand missing -- 7 where 8 was right. Free functions with an instance
        // parameter were already routed to expansion (#71, #72); methods were left out.
        for (int pi = 1; pi < method.Params.Count; ++pi)
        {
            string pt = method.Params[pi].Type;
            if (string.IsNullOrEmpty(pt) || scalarTypes.Contains(pt)) continue;
            if (classFieldLayout.ContainsKey(pt) || classFieldLayout.ContainsKey(ResolveCallee(pt)))
                return false;
        }

        var fields = new HashSet<string>(layout.Select(f => f.Field));
        bool safe = true;

        void E(Expression? e)
        {
            if (!safe || e == null) return;
            switch (e)
            {
                case MemberAccessExpr ma when ma.Object is VariableExpr sv && sv.Name == "self":
                    if (!fields.Contains(ma.Member)) safe = false; // self.method() or non-field
                    return; // do NOT descend into the `self` leaf -- it is a field access
                // `self.<field>.<anything>` -- a member reached THROUGH a field, so the field is
                // another instance, not the scalar an outlined body would take as a parameter.
                // The layout types such a field uint8 when it is constructed in __init__, so the
                // scalar check above cannot catch it; without this the body compiled standalone
                // and `self._pin.value()` mangled into a call on the field's own name.
                case MemberAccessExpr { Object: MemberAccessExpr { Object: VariableExpr { Name: "self" } } }:
                    safe = false; return;
                case MemberAccessExpr ma2: E(ma2.Object); return;
                case VariableExpr ve: if (ve.Name == "self") safe = false; return; // bare self
                case BinaryExpr b: E(b.Left); E(b.Right); return;
                case UnaryExpr u: E(u.Operand); return;
                // self.method(args): a sibling-method call. Allowed in an outlined body —
                // it lowers to a call that forwards this method's own self (field params
                // or slot pointer). Validate only the args, not the self.<method> callee.
                // LIMITATION: the outlined body is compiled once with `self` bound to the
                // DEFINING class, so this self-call binds statically to that class's version.
                // If the sibling is overridden in a subclass, virtual dispatch does NOT happen
                // (Shape.total() calling self.unit() always runs Shape.unit). See the codegen
                // backlog (virtual-dispatch-via-outlined-self-call) -- fixing it needs a
                // post-scan, override-aware force-inline of just the virtual cases, preserving
                // shared outlining for non-overridden sibling calls.
                case CallExpr { Callee: MemberAccessExpr { Object: VariableExpr { Name: "self" } } } selfCall:
                    foreach (var a in selfCall.Args) E(a);
                    return;
                case CallExpr c: E(c.Callee); foreach (var a in c.Args) E(a); return;
                case KeywordArgExpr kw: E(kw.Value); return;
                case IndexExpr ix: E(ix.Target); E(ix.Index); return;
                case TernaryExpr t: E(t.Condition); E(t.TrueVal); E(t.FalseVal); return;
                case TupleExpr tu: foreach (var el in tu.Elements) E(el); return;
                case ListExpr le: foreach (var el in le.Elements) E(el); return;
                case IntegerLiteral: case FloatLiteral: case BooleanLiteral:
                case StringLiteral: return;
                default: safe = false; return; // conservative: unknown node -> not outline-safe
            }
        }

        void S(Statement? s)
        {
            if (!safe || s == null) return;
            switch (s)
            {
                case Block bl: foreach (var cs in bl.Statements) S(cs); return;
                case VarDecl vd: E(vd.Init); return; // typed local decl: `x: T = expr`
                case AnnAssign a: E(a.Value); return;
                case AssignStmt asg: E(asg.Target); E(asg.Value); return;
                case AugAssignStmt aug: E(aug.Target); E(aug.Value); return;
                case ReturnStmt r: E(r.Value); return;
                case ExprStmt ex: E(ex.Expr); return;
                case IfStmt iff:
                    E(iff.Condition); S(iff.ThenBranch);
                    foreach (var br in iff.ElifBranches) { E(br.Condition); S(br.Body); }
                    S(iff.ElseBranch);
                    return;
                case WhileStmt wh: E(wh.Condition); S(wh.Body); return;
                case BreakStmt: case ContinueStmt: case PassStmt: return;
                default: safe = false; return; // conservative
            }
        }

        foreach (var st in method.Body.Statements) S(st);
        return safe;
    }

    // True if the method assigns `self.<field>` anywhere (a plain or augmented assignment).
    // Such a method mutates instance state; if its single field is passed by value it loses
    // the mutation unless we write it back (see RegisterOutlinedMethod).
    // A fixed-array parameter type like `uint8[4]` / `int16[10]`: a scalar element type followed
    // by a bracketed size. `const[...]`, `ptr[...]`, `list[...]`, `bytearray` and class names are
    // NOT this shape. Such a type is valid for a local/field but not for a by-value parameter.
    private static bool IsFixedArrayParamType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        int lb = type.IndexOf('[');
        if (lb <= 0 || !type.EndsWith("]")) return false;
        string elem = type.Substring(0, lb);
        if (elem is not ("uint8" or "int8" or "uint16" or "int16" or "uint32" or "int32"
                         or "float" or "bool")) return false;
        string inner = type.Substring(lb + 1, type.Length - lb - 2);
        return inner.Length > 0 && inner.All(char.IsDigit);
    }

    /// <summary>Same question as MethodMutatesField, for callers outside the class scan.</summary>
    private static bool MethodMutatesFieldPublic(FunctionDef method, string field)
        => MethodMutatesField(method, field);

    // The mutation may also be indirect: `bump()` writing nothing itself but calling
    // `self.inc()`, which does. The field travels by value, so an indirect mutator needs the
    // same write-back as a direct one -- without it the sibling updated a copy and the
    // increment vanished. `siblings` maps the enclosing class body's method names to their
    // ASTs; a self-call naming a method that is not there (inherited, or defined elsewhere)
    // counts as mutating, since assuming otherwise would silently drop a write.
    private static bool MethodMutatesField(FunctionDef method, string field,
        IReadOnlyDictionary<string, FunctionDef>? siblings = null)
    {
        static bool IsSelfField(Expression e, string fld) =>
            e is MemberAccessExpr ma && ma.Member == fld
            && ma.Object is VariableExpr sv && sv.Name == "self";

        var visiting = new HashSet<string>();
        bool Walk(FunctionDef fn)
        {
            bool found = false;

            void E(Expression? e)
            {
                if (found || e == null) return;
                switch (e)
                {
                    case CallExpr { Callee: MemberAccessExpr { Object: VariableExpr { Name: "self" } } sc } selfCall:
                        foreach (var a in selfCall.Args) E(a);
                        if (found) return;
                        if (siblings == null || !siblings.TryGetValue(sc.Member, out var sib))
                        {
                            found = true;   // unknown sibling: assume it writes the field
                            return;
                        }
                        if (visiting.Add(sc.Member))
                        {
                            found = Walk(sib);
                            visiting.Remove(sc.Member);
                        }
                        return;
                    case CallExpr c: E(c.Callee); foreach (var a in c.Args) E(a); return;
                    case MemberAccessExpr ma: E(ma.Object); return;
                    case BinaryExpr b: E(b.Left); E(b.Right); return;
                    case UnaryExpr u: E(u.Operand); return;
                    case KeywordArgExpr kw: E(kw.Value); return;
                    case IndexExpr ix: E(ix.Target); E(ix.Index); return;
                    case TernaryExpr t: E(t.Condition); E(t.TrueVal); E(t.FalseVal); return;
                    case TupleExpr tu: foreach (var el in tu.Elements) E(el); return;
                    case ListExpr le: foreach (var el in le.Elements) E(el); return;
                }
            }

            void S(Statement? s)
            {
                if (found || s == null) return;
                switch (s)
                {
                    case Block bl: foreach (var cs in bl.Statements) S(cs); break;
                    case AssignStmt asg when IsSelfField(asg.Target, field): found = true; break;
                    case AugAssignStmt aug when IsSelfField(aug.Target, field): found = true; break;
                    case AssignStmt asg2: E(asg2.Value); break;
                    case AugAssignStmt aug2: E(aug2.Value); break;
                    case VarDecl vd: E(vd.Init); break;
                    case AnnAssign an: E(an.Value); break;
                    case ReturnStmt r: E(r.Value); break;
                    case ExprStmt ex: E(ex.Expr); break;
                    case IfStmt iff:
                        E(iff.Condition);
                        S(iff.ThenBranch);
                        foreach (var br in iff.ElifBranches) { E(br.Condition); S(br.Body); }
                        S(iff.ElseBranch);
                        break;
                    case WhileStmt wh: E(wh.Condition); S(wh.Body); break;
                    case ForStmt fr: S(fr.Body); break;
                }
            }

            foreach (var st in fn.Body.Statements) S(st);
            return found;
        }

        return Walk(method);
    }

    // True if the method contains any return statement (value-returning or bare). Write-back
    // via return is applied only to fall-through void mutators; a mutator with explicit
    // returns can't carry both a value and the field in one return slot, so it is force-inlined.
    private static bool MethodHasReturnStmt(FunctionDef method)
    {
        bool found = false;
        void S(Statement? s)
        {
            if (found || s == null) return;
            switch (s)
            {
                case Block bl: foreach (var cs in bl.Statements) S(cs); break;
                case ReturnStmt: found = true; break;
                case IfStmt iff:
                    S(iff.ThenBranch);
                    foreach (var br in iff.ElifBranches) S(br.Body);
                    S(iff.ElseBranch);
                    break;
                case WhileStmt wh: S(wh.Body); break;
                case ForStmt fr: S(fr.Body); break;
            }
        }
        foreach (var st in method.Body.Statements) S(st);
        return found;
    }

    // Registers a nested class (a class defined in the body of another class) so it
    // can be constructed with zero-cost ZCA inlining just like a top-level class.
    // Mirrors the per-method registration done for top-level classes, prefixing
    // symbols with the enclosing class path. Recurses for further nesting. Bases
    // (inheritance) on nested classes are not handled here -- none use it today.
    /// <summary>
    /// True when a parameter type is not one the backend can pass in registers: the shape an
    /// ISR handler takes (`def on_irq(pin: Pin)`). An UNANNOTATED parameter counts here, which
    /// is what keeps decorator-style stdlib helpers (`def inline(f)`) out of normal lowering.
    /// </summary>
    private static bool IsZcaHandlerParamType(string type)
        => DataTypeExtensions.StringToDataType(type) == DataType.UNKNOWN
           && type != "bytearray"
           && !type.StartsWith("ptr")
           && type != "const[str]" && type != "str";

    /// <summary>
    /// True when a parameter annotation NAMES a class, so the argument is a ZCA instance.
    /// Narrower than <see cref="IsZcaHandlerParamType"/> on purpose: an unannotated parameter
    /// is not an instance, and force-inlining those would swallow the stdlib's decorator
    /// helpers (`def inline(f)`, `def used(f)`) whose bodies must never be expanded.
    /// </summary>
    private static bool IsZcaInstanceParamType(string type)
        => !string.IsNullOrEmpty(type) && IsZcaHandlerParamType(type);

    /// <summary>
    /// Registers a function for call-site expansion (what `@inline` means). Shared by the
    /// explicit `@inline` decorator and by functions that take a class instance, which have
    /// no other lowering.
    /// </summary>
    private void RegisterInlineFunction(FunctionDef func, string fullName, ModuleScope? scope)
    {
        // `|| overloadedFunctions.Contains` so that once a name is overloaded (its
        // bare key removed below), a later same-named overload registers under its
        // suffixed key instead of re-occupying the vacated bare key, where it would
        // be invisible to suffix-based overload resolution.
        if (inlineFunctions.ContainsKey(fullName) || overloadedFunctions.Contains(fullName))
        {
            if (!overloadedFunctions.Contains(fullName))
            {
                var existing = inlineFunctions[fullName];
                string existingSfx = BuildOverloadSuffix(existing.Params);
                inlineFunctions[fullName + "___" + existingSfx] = existing;
                inlineFunctions.Remove(fullName);
                overloadedFunctions.Add(fullName);
            }

            string newSfx = BuildOverloadSuffix(func.Params);
            inlineFunctions[fullName + "___" + newSfx] = func;
        }
        else
        {
            inlineFunctions[fullName] = func;
            if (scope != null) scope.InlineFunctions[func.Name] = func;
        }
    }

    private void ScanNestedClassMembers(ClassDef nested, string outerPrefix)
    {
        if (nested.Bases.Contains("Enum") || nested.Bases.Contains("IntEnum")) return;
        if (nested.Body is not Block block) return;

        classNames.Add(nested.Name);
        if (nested.IsValue) valueClasses.Add(nested.Name);

        var oldPrefix = currentModulePrefix;
        var classPrefix = outerPrefix + nested.Name + "_";
        currentModulePrefix = classPrefix;

        string classKey = classPrefix.Substring(0, classPrefix.Length - 1);
        if (!classDirectMethods.ContainsKey(classKey))
            classDirectMethods[classKey] = new HashSet<string>();

        foreach (var inner in block.Statements)
        {
            if (inner is FunctionDef func)
            {
                classDirectMethods[classKey].Add(func.Name);

                string fullName = currentModulePrefix + func.Name;
                functionReturnTypes[fullName] = func.ReturnType;
                var @params = new List<string>();
                var paramTypes = new List<DataType>();
                foreach (var p in func.Params)
                {
                    @params.Add(p.Name);
                    paramTypes.Add(DataTypeExtensions.StringToDataType(p.Type));
                }

                functionParams[fullName] = @params;
                functionParamTypes[fullName] = paramTypes;

                if (func.IsPropertySetter)
                {
                    string setterKey = fullName + "___setter";
                    inlineFunctions[setterKey] = func;
                    propertySetters[classKey + "." + func.PropertyName] = setterKey;
                }
                else if (func.IsInline)
                {
                    // See the top-level class path: once overloaded, register under the
                    // suffixed key rather than re-occupying the vacated bare key.
                    if (overloadedFunctions.Contains(fullName))
                    {
                        inlineFunctions[fullName + "___" + BuildOverloadSuffix(func.Params)] = func;
                    }
                    else if (!inlineFunctions.TryAdd(fullName, func))
                    {
                        var existing = inlineFunctions[fullName];
                        if (existing?.Params != null)
                        {
                            var existingSfx = BuildOverloadSuffix(existing.Params);
                            inlineFunctions[fullName + "___" + existingSfx] = existing;
                        }

                        inlineFunctions.Remove(fullName);
                        overloadedFunctions.Add(fullName);
                        inlineFunctions[fullName + "___" + BuildOverloadSuffix(func.Params)] = func;
                    }
                }
                else
                {
                    functionsToCompile.Add(new FunctionEntry
                        { Prefix = currentModulePrefix, Func = func, SourceFile = currentSourceFile });
                    instanceMethodDefs[fullName] = func;
                }

                if (!func.IsPropertySetter)
                {
                    methodInstanceTypes[fullName] =
                        currentModulePrefix.Substring(0, currentModulePrefix.Length - 1);
                }
            }
            else if (inner is ClassDef deeper)
            {
                ScanNestedClassMembers(deeper, classPrefix);
            }
        }

        currentModulePrefix = oldPrefix;
    }

    // Returns the set of parameter names (excluding "self") that are accessed with a
    // variable (non-constant) index inside the given inline function body, including
    // parameters that are forwarded to nested inline function calls whose own parameters
    // are also variable-indexed.
    private HashSet<string> GetInlineVarIndexedParams(FunctionDef func, HashSet<FunctionDef> visiting)
    {
        if (!visiting.Add(func)) return new HashSet<string>(); // cycle guard

        var paramNames = new HashSet<string>(func.Params.Select(p => p.Name));
        var result = new HashSet<string>();

        // bytearray-typed parameters always require SRAM — they are indexable buffers passed
        // by pointer.  Marking them here avoids having to follow the full inline call chain to
        // find a variable-index subscript.
        foreach (var p in func.Params)
            if (p.Type == "bytearray") result.Add(p.Name);

        void ScanIExpr(Expression? expr)
        {
            if (expr == null) return;
            if (expr is IndexExpr idx && idx.Target is VariableExpr ve && !(idx.Index is IntegerLiteral))
            {
                if (paramNames.Contains(ve.Name))
                    result.Add(ve.Name);
            }

            if (expr is CallExpr ic)
            {
                ScanIExpr(ic.Callee);
                foreach (var a in ic.Args) ScanIExpr(a);

                // Resolve direct calls (non-method) to inline functions and propagate.
                string nestedKey = "";
                if (ic.Callee is VariableExpr icVe)
                    nestedKey = ResolveCallee(icVe.Name);

                if (!string.IsNullOrEmpty(nestedKey) && inlineFunctions.TryGetValue(nestedKey, out var nested))
                {
                    var nestedVarIdx = GetInlineVarIndexedParams(nested, visiting);
                    if (nestedVarIdx.Count > 0)
                    {
                        int argPos = 0;
                        foreach (var np in nested.Params)
                        {
                            if (np.Name == "self") continue;
                            if (nestedVarIdx.Contains(np.Name) && argPos < ic.Args.Count)
                            {
                                if (ic.Args[argPos] is VariableExpr argVe && paramNames.Contains(argVe.Name))
                                    result.Add(argVe.Name);
                            }
                            argPos++;
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(nestedKey) && overloadedFunctions.Contains(nestedKey))
                {
                    // Overloaded direct call: union results from all variants.
                    foreach (var kv in inlineFunctions)
                    {
                        if (!kv.Key.StartsWith(nestedKey + "___")) continue;
                        var oVarIdx = GetInlineVarIndexedParams(kv.Value, visiting);
                        if (oVarIdx.Count == 0) continue;
                        int oArgPos = 0;
                        foreach (var np in kv.Value.Params)
                        {
                            if (np.Name == "self") continue;
                            if (oVarIdx.Contains(np.Name) && oArgPos < ic.Args.Count)
                            {
                                if (ic.Args[oArgPos] is VariableExpr argVe && paramNames.Contains(argVe.Name))
                                    result.Add(argVe.Name);
                            }
                            oArgPos++;
                        }
                    }
                }
            }

            if (expr is BinaryExpr ib) { ScanIExpr(ib.Left); ScanIExpr(ib.Right); }
            if (expr is UnaryExpr iu) ScanIExpr(iu.Operand);
            if (expr is MemberAccessExpr ima) ScanIExpr(ima.Object);
        }

        void ScanIStmt(Statement? s)
        {
            if (s == null) return;
            if (s is AssignStmt ia) { ScanIExpr(ia.Target); ScanIExpr(ia.Value); }
            else if (s is AnnAssign ia2) ScanIExpr(ia2.Value);
            else if (s is ReturnStmt ir) ScanIExpr(ir.Value);
            else if (s is ExprStmt ie) ScanIExpr(ie.Expr);
            else if (s is IfStmt iif)
            {
                ScanIExpr(iif.Condition);
                ScanIStmt(iif.ThenBranch);
                foreach (var b in iif.ElifBranches) { ScanIExpr(b.Condition); ScanIStmt(b.Body); }
                ScanIStmt(iif.ElseBranch);
            }
            else if (s is WhileStmt iwh) { ScanIExpr(iwh.Condition); ScanIStmt(iwh.Body); }
            else if (s is Block ib2) foreach (var cs in ib2.Statements) ScanIStmt(cs);
            else if (s is AugAssignStmt iaug) { ScanIExpr(iaug.Target); ScanIExpr(iaug.Value); }
        }

        foreach (var s in func.Body.Statements) ScanIStmt(s);
        visiting.Remove(func);
        return result;
    }

    private void ScanForVariableIndexedArrays(List<Statement> stmts, string prefix)
    {
        var localArrays = new HashSet<string>();

        // Pre-scan: collect local variable → class name for constructor calls, so we can
        // resolve method calls to inline functions without needing instanceClasses (which
        // is not yet populated at this point in compilation).
        var localVarTypes = new Dictionary<string, string>();

        void CollectArrayDecls(Statement? stmt)
        {
            if (stmt == null) return;
            if (stmt is AnnAssign ann)
            {
                if (ann.Annotation.StartsWith("list[") && ann.Annotation.EndsWith("]"))
                {
                    string elemTypeName = ann.Annotation.Substring(5, ann.Annotation.Length - 6);
                    DataType elemDt = DataTypeExtensions.StringToDataType(elemTypeName);
                    listVarElemTypes[prefix + ann.Target] = elemDt;
                    // list variables are NOT fixed-size arrays; do not add to localArrays
                }
                else
                {
                    int br = ann.Annotation.IndexOf('[');
                    int cl = ann.Annotation.LastIndexOf(']');
                    if (br != -1 && cl != -1)
                    {
                        string inner = ann.Annotation.Substring(br + 1, cl - br - 1);
                        if (!string.IsNullOrEmpty(inner) && inner.All(char.IsDigit))
                            localArrays.Add(prefix + ann.Target);
                    }

                    if (ann.Annotation == "bytearray")
                        localArrays.Add(prefix + ann.Target);
                }
            }
            else if (stmt is VarDecl vd)
            {
                if (vd.VarType == "bytearray")
                    localArrays.Add(prefix + vd.Name);
            }
            else if (stmt is Block block)
            {
                foreach (var s in block.Statements) CollectArrayDecls(s);
            }
            else if (stmt is IfStmt ifStmt)
            {
                CollectArrayDecls(ifStmt.ThenBranch);
                foreach (var branch in ifStmt.ElifBranches) CollectArrayDecls(branch.Body);
                CollectArrayDecls(ifStmt.ElseBranch);
            }
            else if (stmt is WhileStmt wh)
            {
                CollectArrayDecls(wh.Body);
            }
        }

        foreach (var s in stmts) CollectArrayDecls(s);

        // Pre-scan statements to collect variable → class name from constructor calls.
        void CollectLocalTypes(Statement? stmt)
        {
            if (stmt == null) return;

            void TryRecordType(string varName, Expression? value)
            {
                if (value is not CallExpr ctorCall) return;
                string cls = "";
                if (ctorCall.Callee is VariableExpr cv)
                    cls = ResolveCallee(cv.Name);
                else if (ctorCall.Callee is MemberAccessExpr cm && cm.Object is VariableExpr modVe
                         && modules.ContainsKey(modVe.Name))
                    cls = modVe.Name.Replace('.', '_') + "_" + cm.Member;
                if (!string.IsNullOrEmpty(cls) &&
                    (inlineFunctions.ContainsKey(cls + "___init__") ||
                     overloadedFunctions.Contains(cls + "___init__")))
                    localVarTypes[prefix + varName] = cls;
            }

            if (stmt is AssignStmt asn && asn.Target is VariableExpr asv)
                TryRecordType(asv.Name, asn.Value);
            else if (stmt is AnnAssign aan && aan.Value != null)
                TryRecordType(aan.Target, aan.Value);
            else if (stmt is Block blk)
                foreach (var s in blk.Statements) CollectLocalTypes(s);
            else if (stmt is IfStmt ifs)
            {
                CollectLocalTypes(ifs.ThenBranch);
                foreach (var b in ifs.ElifBranches) CollectLocalTypes(b.Body);
                CollectLocalTypes(ifs.ElseBranch);
            }
            else if (stmt is WhileStmt whs) CollectLocalTypes(whs.Body);
        }

        foreach (var s in stmts) CollectLocalTypes(s);

        void ScanExpr(Expression? expr)
        {
            if (expr == null) return;
            if (expr is IndexExpr idx)
            {
                if (idx.Target is VariableExpr ve)
                {
                    string q = prefix + ve.Name;
                    if (localArrays.Contains(q) && !(idx.Index is IntegerLiteral))
                    {
                        arraysWithVariableIndex.Add(q);
                    }
                }

                ScanExpr(idx.Target);
                ScanExpr(idx.Index);
            }
            else if (expr is CallExpr call)
            {
                ScanExpr(call.Callee);
                foreach (var arg in call.Args) ScanExpr(arg);

                // Propagate variable-index array info from inline function parameters
                // to the actual arguments at this call site.
                FunctionDef? resolvedFunc = null;
                string overloadedKey = "";   // non-empty when the call targets an overloaded inline

                if (call.Callee is MemberAccessExpr memAcc && memAcc.Object is VariableExpr objVe)
                {
                    // Method call: resolve object type via pre-collected localVarTypes.
                    string objKey = prefix + objVe.Name;
                    if (localVarTypes.TryGetValue(objKey, out string cls))
                    {
                        string methodKey = cls + "_" + memAcc.Member;
                        if (!inlineFunctions.TryGetValue(methodKey, out resolvedFunc) &&
                            overloadedFunctions.Contains(methodKey))
                            overloadedKey = methodKey;
                    }
                }
                else if (call.Callee is VariableExpr callVe)
                {
                    // Direct function call.
                    string resolvedCallee = ResolveCallee(callVe.Name);
                    if (!inlineFunctions.TryGetValue(resolvedCallee, out resolvedFunc) &&
                        overloadedFunctions.Contains(resolvedCallee))
                        overloadedKey = resolvedCallee;

                    // For non-inline functions: if an argument is a local array passed to a
                    // bytearray parameter (UINT16 pointer type), mark it as needing SRAM storage
                    // so it is allocated contiguously and not constant-folded away.
                    if (resolvedFunc == null && string.IsNullOrEmpty(overloadedKey) &&
                        functionParamTypes.TryGetValue(resolvedCallee, out var calleeParamTypes))
                    {
                        for (int ai = 0; ai < call.Args.Count && ai < calleeParamTypes.Count; ai++)
                        {
                            if (calleeParamTypes[ai] == DataType.UINT16 && call.Args[ai] is VariableExpr argVe2)
                            {
                                string actualName = prefix + argVe2.Name;
                                if (localArrays.Contains(actualName))
                                    arraysWithVariableIndex.Add(actualName);
                            }
                        }
                    }
                }

                // Single non-overloaded inline function.
                if (resolvedFunc != null)
                {
                    var varIdxParams = GetInlineVarIndexedParams(resolvedFunc, new HashSet<FunctionDef>());
                    if (varIdxParams.Count > 0)
                    {
                        int argPos = 0;
                        foreach (var param in resolvedFunc.Params)
                        {
                            if (param.Name == "self") continue;
                            if (varIdxParams.Contains(param.Name) && argPos < call.Args.Count)
                            {
                                if (call.Args[argPos] is VariableExpr argVe)
                                {
                                    string actualName = prefix + argVe.Name;
                                    if (localArrays.Contains(actualName))
                                        arraysWithVariableIndex.Add(actualName);
                                }
                            }
                            argPos++;
                        }
                    }
                }

                // Overloaded inline functions: union var-index info across ALL variants whose
                // non-self parameter count matches the call argument count.  We cannot pick a
                // single overload without full type inference, so we take the conservative
                // (false-positive-safe) approach of marking an argument if ANY overload
                // indicates that position needs SRAM.
                if (!string.IsNullOrEmpty(overloadedKey))
                {
                    int argCount = call.Args.Count;
                    foreach (var kv in inlineFunctions)
                    {
                        if (!kv.Key.StartsWith(overloadedKey + "___")) continue;
                        if (kv.Value.Params.Count(p => p.Name != "self") != argCount) continue;
                        var varIdxP = GetInlineVarIndexedParams(kv.Value, new HashSet<FunctionDef>());
                        if (varIdxP.Count == 0) continue;
                        int ap = 0;
                        foreach (var param in kv.Value.Params)
                        {
                            if (param.Name == "self") continue;
                            if (varIdxP.Contains(param.Name) && ap < call.Args.Count)
                            {
                                if (call.Args[ap] is VariableExpr argVe)
                                {
                                    string actualName = prefix + argVe.Name;
                                    if (localArrays.Contains(actualName))
                                        arraysWithVariableIndex.Add(actualName);
                                }
                            }
                            ap++;
                        }
                    }
                }
            }
            else if (expr is BinaryExpr bin)
            {
                ScanExpr(bin.Left);
                ScanExpr(bin.Right);
            }
            else if (expr is UnaryExpr un)
            {
                ScanExpr(un.Operand);
            }
        }

        void ScanStmt(Statement? stmt)
        {
            if (stmt == null) return;
            if (stmt is AssignStmt assign)
            {
                ScanExpr(assign.Target);
                ScanExpr(assign.Value);
            }
            else if (stmt is AnnAssign ann)
            {
                ScanExpr(ann.Value);
            }
            else if (stmt is ReturnStmt ret)
            {
                ScanExpr(ret.Value);
            }
            else if (stmt is ExprStmt exprStmt)
            {
                ScanExpr(exprStmt.Expr);
            }
            else if (stmt is IfStmt ifStmt)
            {
                ScanExpr(ifStmt.Condition);
                ScanStmt(ifStmt.ThenBranch);
                foreach (var branch in ifStmt.ElifBranches)
                {
                    ScanExpr(branch.Condition);
                    ScanStmt(branch.Body);
                }

                ScanStmt(ifStmt.ElseBranch);
            }
            else if (stmt is WhileStmt wh)
            {
                ScanExpr(wh.Condition);
                ScanStmt(wh.Body);
            }
            else if (stmt is Block block)
            {
                foreach (var s in block.Statements) ScanStmt(s);
            }
            else if (stmt is AugAssignStmt aug)
            {
                ScanExpr(aug.Target);
                ScanExpr(aug.Value);
            }
            else if (stmt is VarDecl vd)
            {
                // `v: T = arr[idx]` declares v with a runtime-indexed read in its initializer.
                // Without scanning it, arr is never marked variable-indexed and the read demands
                // a constant subscript -- yet `v: T = 0; v = arr[idx]` works. Scan the init.
                ScanExpr(vd.Init);
            }
            else if (stmt is ForStmt fr)
            {
                ScanExpr(fr.RangeStart); ScanExpr(fr.RangeStop); ScanExpr(fr.RangeStep);
                ScanExpr(fr.Iterable);
                ScanStmt(fr.Body);
            }
        }

        foreach (var s in stmts) ScanStmt(s);
    }
}
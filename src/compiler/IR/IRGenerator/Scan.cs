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
    private void ScanGlobals(ProgramNode ast, ModuleScope? scope = null)
    {
        foreach (var stmt in ast.GlobalStatements)
        {
            string name = "";
            string type = "";
            Expression? initializer = null;

            if (stmt is VarDecl varDecl)
            {
                name = varDecl.Name;
                type = varDecl.VarType;
                initializer = varDecl.Init;

                if (type == "bytearray" && initializer != null)
                {
                    if (initializer is CallExpr call && call.Callee is VariableExpr callee &&
                        callee.Name == "bytearray" && call.Args.Count > 0)
                    {
                        int count = 0;
                        if (call.Args[0] is IntegerLiteral il) count = il.Value;
                        if (count > 0)
                        {
                            arraySizes[name] = count;
                            arrayElemTypes[name] = DataType.UINT8;
                            moduleSramArrays.Add(name);
                        }
                    }
                }
            }
            else if (stmt is AssignStmt assign)
            {
                if (assign.Target is VariableExpr varExpr)
                {
                    name = varExpr.Name;
                    initializer = assign.Value;
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
                                        if (le.Elements[k] is IntegerLiteral il)
                                            bytes[k] = il.Value;
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
                try
                {
                    if (initializer is VariableExpr varExprInit)
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
                }

                // Track module-level singleton instances (e.g. `mem8 = _Mem8()`) so
                // that subscript and method dispatch via GetValClass works when these
                // singletons are imported by user code.
                if (initializer is CallExpr ctorCallInst
                    && ctorCallInst.Callee is VariableExpr ctorVarInst
                    && classModuleMap.TryGetValue(ctorVarInst.Name, out var classMod)
                    && classMod != null)
                {
                    string fullKey = currentModulePrefix + name;
                    string fullClassName = classMod + ctorVarInst.Name;
                    instanceClasses[fullKey] = fullClassName;
                }
            }
        }
    }

    private void ScanFunctions(ProgramNode ast, ModuleScope? scope = null)
    {
        foreach (var func in ast.Functions)
        {
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
            else
            {
                // If the first parameter has an unknown (class) type, this is a ZCA-parameterized
                // handler (e.g. def on_irq(pin: Pin)). Store for on-demand synthesis; do NOT
                // add to functionsToCompile because the body references ZCA fields that are
                // only known at the call site.
                bool hasZcaFirstParam = func.Params.Count > 0 &&
                    DataTypeExtensions.StringToDataType(func.Params[0].Type) == DataType.UNKNOWN &&
                    func.Params[0].Type != "bytearray" &&
                    !func.Params[0].Type.StartsWith("ptr") &&
                    func.Params[0].Type != "const[str]" && func.Params[0].Type != "str";
                if (hasZcaFirstParam)
                {
                    zcaHandlerAstNodes[fullName] = (func, currentModulePrefix ?? "");
                }
                else
                {
                    functionsToCompile.Add(new FunctionEntry
                        { Prefix = currentModulePrefix, Func = func, SourceFile = currentSourceFile });
                }
            }
        }

        foreach (var stmt in ast.GlobalStatements)
        {
            if (stmt is ClassDef classDef)
            {
                bool isEnum = classDef.Bases.Contains("Enum") || classDef.Bases.Contains("IntEnum");
                if (isEnum) continue;

                if (classDef.Body != null)
                {
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
                        classFieldLayout[classKey] = clsLayout;
                        // Note: slotClasses (>= 2 fields) is marked only when an @outline method
                        // is actually present (below), so plain @inline HAL classes with multiple
                        // fields keep their normal virtual-construction path. zcaFactoryClasses is
                        // safe to mark eagerly: it is only consulted in factory-return contexts,
                        // never in direct construction.
                        if (clsLayout.Count == 1)
                            zcaFactoryClasses[classKey] = clsLayout[0].Type;

                        foreach (var inner in block.Statements)
                        {
                            if (inner is FunctionDef func)
                            {
                                // Register as directly-defined BEFORE the inheritance copy so
                                // ResolveMROMethod can distinguish "defined here" from "inherited".
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
                                    string className = classPrefix.Substring(0, classPrefix.Length - 1);
                                    propertySetters[className + "." + func.PropertyName] = setterKey;
                                }
                                else if (func.IsOutline)
                                {
                                    // RFC 0001: explicit @outline -- compile this method ONCE as a
                                    // shared subroutine (Model A field-params, or Model B SRAM slot
                                    // for >= 2 fields). After F4 this is redundant with the default
                                    // for outline-safe methods; kept as an explicit request.
                                    RegisterOutlinedMethod(func, classKey, DeriveFieldLayout(block), fullName);
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
                                    // RFC 0001 F4: an undecorated method is OUTLINED BY DEFAULT when
                                    // it is outline-safe (touches self only as self.<field>). This is
                                    // why @inline now means something: without it, a representable
                                    // method is shared, not silently force-inlined per instance.
                                    var defLayout = DeriveFieldLayout(block);
                                    if (IsOutlineSafe(func, defLayout))
                                    {
                                        RegisterOutlinedMethod(func, classKey, defLayout, fullName);
                                    }
                                    else
                                    {
                                        // Not representable as a shared body (uses self.method(),
                                        // passes self, non-derivable field, or an unhandled construct):
                                        // force-inline is the only way to give it a runtime form.
                                        functionsToCompile.Add(new FunctionEntry
                                            { Prefix = currentModulePrefix, Func = func, SourceFile = currentSourceFile });
                                        instanceMethodDefs[fullName] = func;
                                    }
                                }

                                if (!func.IsPropertySetter)
                                {
                                    methodInstanceTypes[fullName] =
                                        currentModulePrefix.Substring(0, currentModulePrefix.Length - 1);
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
            if (s is AssignStmt asg && asg.Target is MemberAccessExpr ma
                && ma.Object is VariableExpr sv && sv.Name == "self")
            {
                field = ma.Member;
                rhs = asg.Value;
            }

            if (field == null || !seen.Add(field)) continue;

            // SourceParam: the __init__ param that directly initializes the field
            // (RHS is a bare parameter), else "" -- needed for factory return lowering.
            string type = "uint8";
            string srcParam = "";
            if (rhs is VariableExpr rv && paramTypes.TryGetValue(rv.Name, out var pt))
            {
                srcParam = rv.Name;
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
        List<(string Field, string Type, string SourceParam)> layout, string fullName)
    {
        var synthParams = new List<Param>();
        if (layout.Count >= 2) slotClasses.Add(classKey);
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

        var synth = new FunctionDef(func.Name, synthParams, func.ReturnType, func.Body, isInline: false);
        functionsToCompile.Add(new FunctionEntry
            { Prefix = currentModulePrefix, Func = synth, SourceFile = currentSourceFile });

        outlinedMethods.Add(fullName);
        outlineFieldLayout[fullName] = layout;
        functionParams[fullName] = synthParams.Select(p => p.Name).ToList();
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
                case MemberAccessExpr ma2: E(ma2.Object); return;
                case VariableExpr ve: if (ve.Name == "self") safe = false; return; // bare self
                case BinaryExpr b: E(b.Left); E(b.Right); return;
                case UnaryExpr u: E(u.Operand); return;
                // self.method(args): a sibling-method call. Allowed in an outlined body —
                // it lowers to a call that forwards this method's own self (field params
                // or slot pointer). Validate only the args, not the self.<method> callee.
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

    // Registers a nested class (a class defined in the body of another class) so it
    // can be constructed with zero-cost ZCA inlining just like a top-level class.
    // Mirrors the per-method registration done for top-level classes, prefixing
    // symbols with the enclosing class path. Recurses for further nesting. Bases
    // (inheritance) on nested classes are not handled here -- none use it today.
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
        }

        foreach (var s in stmts) ScanStmt(s);
    }
}
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
                    DataType t = DataTypeExtensions.StringToDataType(type);
                    mutableGlobals[currentModulePrefix + name] = t;
                    if (scope != null) scope.MutableGlobals[name] = t;
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
                if (inlineFunctions.ContainsKey(fullName))
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
                functionsToCompile.Add(new FunctionEntry
                    { Prefix = currentModulePrefix, Func = func, SourceFile = currentSourceFile });
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
                    var oldPrefix = currentModulePrefix;
                    var classPrefix = currentModulePrefix + classDef.Name + "_";
                    currentModulePrefix = classPrefix;

                    if (classDef.Body is Block block)
                    {
                        foreach (var inner in block.Statements)
                        {
                            if (inner is FunctionDef func)
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

                                if (func.IsPropertySetter)
                                {
                                    string setterKey = fullName + "___setter";
                                    inlineFunctions[setterKey] = func;
                                    string className = classPrefix.Substring(0, classPrefix.Length - 1);
                                    propertySetters[className + "." + func.PropertyName] = setterKey;
                                }
                                else if (func.IsInline)
                                {
                                    if (!inlineFunctions.TryAdd(fullName, func))
                                    {
                                        if (!overloadedFunctions.Contains(fullName))
                                        {
                                            var existing = inlineFunctions[fullName];
                                            if (existing?.Params != null)
                                            {
                                                var existingSfx = BuildOverloadSuffix(existing.Params);
                                                inlineFunctions[fullName + "___" + existingSfx] = existing;
                                            }

                                            inlineFunctions.Remove(fullName);
                                            overloadedFunctions.Add(fullName);
                                        }

                                        string newSfx = BuildOverloadSuffix(func.Params);
                                        inlineFunctions[fullName + "___" + newSfx] = func;
                                    }
                                }
                                else
                                {
                                    functionsToCompile.Add(new FunctionEntry
                                        { Prefix = currentModulePrefix, Func = func, SourceFile = currentSourceFile });
                                }

                                if (!func.IsPropertySetter)
                                {
                                    methodInstanceTypes[fullName] =
                                        currentModulePrefix.Substring(0, currentModulePrefix.Length - 1);
                                }
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

    private void ScanForVariableIndexedArrays(List<Statement> stmts, string prefix)
    {
        var localArrays = new HashSet<string>();

        void CollectArrayDecls(Statement? stmt)
        {
            if (stmt == null) return;
            if (stmt is AnnAssign ann)
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
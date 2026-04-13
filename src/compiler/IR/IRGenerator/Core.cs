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
    // Maps AST BinaryOp to IR BinaryOp (only for ops that have IR equivalents)
    private static BinaryOp MapBinaryOp(Frontend.BinaryOp op) => op switch
    {
        Frontend.BinaryOp.Add => BinaryOp.Add,
        Frontend.BinaryOp.Sub => BinaryOp.Sub,
        Frontend.BinaryOp.Mul => BinaryOp.Mul,
        Frontend.BinaryOp.Div => BinaryOp.Div,
        Frontend.BinaryOp.FloorDiv => BinaryOp.FloorDiv,
        Frontend.BinaryOp.Mod => BinaryOp.Mod,
        Frontend.BinaryOp.Equal => BinaryOp.Equal,
        Frontend.BinaryOp.NotEqual => BinaryOp.NotEqual,
        Frontend.BinaryOp.Less => BinaryOp.LessThan,
        Frontend.BinaryOp.Greater => BinaryOp.GreaterThan,
        Frontend.BinaryOp.LessEq => BinaryOp.LessEqual,
        Frontend.BinaryOp.GreaterEq => BinaryOp.GreaterEqual,
        Frontend.BinaryOp.BitAnd => BinaryOp.BitAnd,
        Frontend.BinaryOp.BitOr => BinaryOp.BitOr,
        Frontend.BinaryOp.BitXor => BinaryOp.BitXor,
        Frontend.BinaryOp.LShift => BinaryOp.LShift,
        Frontend.BinaryOp.RShift => BinaryOp.RShift,
        _ => throw new Exception($"BinaryOp {op} has no IR equivalent"),
    };

    // Maps AST UnaryOp to IR UnaryOp
    private static UnaryOp MapUnaryOp(Frontend.UnaryOp op) => op switch
    {
        Frontend.UnaryOp.Negate => UnaryOp.Neg,
        Frontend.UnaryOp.Not => UnaryOp.Not,
        Frontend.UnaryOp.BitNot => UnaryOp.BitNot,
        _ => throw new Exception($"UnaryOp {op} has no IR equivalent"),
    };

    // Maps AST AugOp to IR BinaryOp
    private static BinaryOp MapAugOp(AugOp op) => op switch
    {
        AugOp.Add => BinaryOp.Add,
        AugOp.Sub => BinaryOp.Sub,
        AugOp.Mul => BinaryOp.Mul,
        AugOp.Div => BinaryOp.Div,
        AugOp.FloorDiv => BinaryOp.FloorDiv,
        AugOp.Mod => BinaryOp.Mod,
        AugOp.BitAnd => BinaryOp.BitAnd,
        AugOp.BitOr => BinaryOp.BitOr,
        AugOp.BitXor => BinaryOp.BitXor,
        AugOp.LShift => BinaryOp.LShift,
        AugOp.RShift => BinaryOp.RShift,
        _ => throw new Exception($"AugOp {op} has no IR equivalent"),
    };

    private bool IsConstType(string type)
    {
        return type == "const" || (type.StartsWith("const[") && type.EndsWith("]"));
    }

    private Temporary MakeTemp(DataType type = DataType.UINT8)
    {
        return new Temporary($"tmp_{tempCounter++}", type);
    }

    private static string DataTypeToSuffixStr(DataType dt)
    {
        return dt switch
        {
            DataType.UINT8 => "uint8",
            DataType.UINT16 => "uint16",
            DataType.UINT32 => "uint32",
            DataType.INT8 => "int8",
            DataType.INT16 => "int16",
            DataType.INT32 => "int32",
            _ => "uint8",
        };
    }

    public static string BuildOverloadSuffix(List<Param> parameters)
    {
        string suffix = "";
        bool first = true;
        foreach (var p in parameters)
        {
            if (p.Name == "self") continue;
            if (!first) suffix += "_";
            first = false;
            suffix += string.IsNullOrEmpty(p.Type) ? "uint8" : p.Type;
        }

        return string.IsNullOrEmpty(suffix) ? "void" : suffix;
    }

    private DataType InferExprType(Expression expr)
    {
        switch (expr)
        {
            case BooleanLiteral or IntegerLiteral:
                break;
            case VariableExpr varExpr:
            {
                var key = currentInlinePrefix + varExpr.Name;
                for (var i = 0; i < 20; ++i)
                {
                    if (variableTypes.TryGetValue(key, out var type)) return type;
                    if (variableAliases.TryGetValue(key, out var alias))
                        key = alias;
                    else
                        break;
                }

                break;
            }
            case BinaryExpr bin:
            {
                var lt = InferExprType(bin.Left);
                var rt = InferExprType(bin.Right);
                return (DataType)Math.Max((int)lt, (int)rt);
            }
        }

        return DataType.UINT8;
    }

    private string MakeLabel()
    {
        return $"L_{labelCounter++}";
    }

    private void Emit(Instruction inst)
    {
        currentInstructions.Add(inst);
    }

    public ProgramIR Generate(
        ProgramNode mainAst,
        Dictionary<string, ProgramNode> importedModules,
        DeviceConfig config,
        List<string>? sourceLines = null,
        Dictionary<string, List<string>>? moduleSourceLines = null)
    {
        this.deviceConfig = config;
        this.sourceLines = sourceLines ?? new List<string>();
        this.moduleSourceLines = moduleSourceLines ?? new Dictionary<string, List<string>>();
        this.lastLine = -1;
        this.currentSourceFile = "";

        var irProgram = new ProgramIR();
        globals.Clear();
        mutableGlobals.Clear();
        functionReturnTypes.Clear();
        functionParams.Clear();
        inlineFunctions.Clear();
        modules.Clear();
        functionsToCompile.Clear();
        intrinsicNames.Clear();
        pendingIsrRegistrations.Clear();
        externFunctionMap.Clear();

        intrinsicNames.Add("uart_send_string");
        intrinsicNames.Add("uart_send_string_ln");
        foreach (var t in new[] { "uint8", "uint16", "uint32", "int8", "int16", "int32" })
            intrinsicNames.Add(t);
        intrinsicNames.Add("print");
        intrinsicNames.Add("sleep_ms");
        intrinsicNames.Add("sleep_us");
        intrinsicNames.Add("len");
        intrinsicNames.Add("sum");
        intrinsicNames.Add("any");
        intrinsicNames.Add("all");
        intrinsicNames.Add("hex");
        intrinsicNames.Add("bin");
        intrinsicNames.Add("str");
        intrinsicNames.Add("pow");
        intrinsicNames.Add("zip");
        intrinsicNames.Add("reversed");
        intrinsicNames.Add("divmod");

        if (config.Frequency > 0)
        {
            constantVariables["__FREQ__"] = (int)config.Frequency;
            constantVariables["__FREQUENCY__"] = (int)config.Frequency;
        }

        foreach (var imp in mainAst.Imports)
        {
            if (imp.ModuleName == "pymcu.types")
            {
                intrinsicNames.Add("ptr");
                intrinsicNames.Add("const");
                intrinsicNames.Add("device_info");
                intrinsicNames.Add("inline");
                intrinsicNames.Add("interrupt");
                intrinsicNames.Add("asm");
                intrinsicNames.Add("compile_isr");
            }

            if (imp.Symbols.Count == 0)
            {
                string modKey = string.IsNullOrEmpty(imp.ModuleAlias) ? imp.ModuleName : imp.ModuleAlias;
                modules[modKey] = new ModuleScope();
            }

            foreach (var sym in imp.Symbols)
            {
                string key = imp.Aliases.ContainsKey(sym) ? imp.Aliases[sym] : sym;
                importedAliases[key] = imp.ModuleName;
                if (imp.Aliases.ContainsKey(sym))
                    aliasToOriginal[key] = sym;
            }
        }

        foreach (var kvp in importedModules)
        {
            var modName = kvp.Key;
            var modAst = kvp.Value;
            foreach (var imp in modAst.Imports)
            {
                if (imp.ModuleName == "pymcu.types")
                {
                    intrinsicNames.Add("ptr");
                    intrinsicNames.Add("const");
                    intrinsicNames.Add("device_info");
                    intrinsicNames.Add("inline");
                    intrinsicNames.Add("interrupt");
                    intrinsicNames.Add("asm");
                    intrinsicNames.Add("compile_isr");
                }

                foreach (var sym in imp.Symbols)
                {
                    string key = imp.Aliases.ContainsKey(sym) ? imp.Aliases[sym] : sym;
                    // Don't overwrite aliases established by the main file — sub-module
                    // imports use the same flat dictionary and would otherwise shadow the
                    // user's own `from machine import Pin` with a stdlib-internal
                    // `from pymcu.hal.gpio import Pin` that lives in e.g. hal/__init__.py.
                    if (!importedAliases.ContainsKey(key))
                    {
                        importedAliases[key] = imp.ModuleName;
                        if (imp.Aliases.ContainsKey(sym))
                            aliasToOriginal[key] = sym;
                    }
                }
            }
        }

        foreach (var kvp in importedModules)
        {
            var modName = kvp.Key;
            var modAst = kvp.Value;
            modules[modName] = new ModuleScope();
            currentModulePrefix = modName.Replace('.', '_') + "_";
            int dotPos = modName.LastIndexOf('.');
            currentSourceFile = (dotPos != -1 ? modName.Substring(dotPos + 1) : modName) + ".py";
            ScanGlobals(modAst, modules[modName]);
            ScanFunctions(modAst, modules[modName]);
        }

        foreach (var kvp in importedModules)
        {
            var modName = kvp.Key;
            var modAst = kvp.Value;
            var scope = modules[modName];
            foreach (var imp in modAst.Imports)
            {
                if (modules.TryGetValue(imp.ModuleName, out var srcScope))
                {
                    foreach (var sym in imp.Symbols)
                    {
                        if (srcScope.Globals.TryGetValue(sym, out var globalSym))
                        {
                            scope.Globals[sym] = globalSym;
                        }
                        else if (srcScope.MutableGlobals.TryGetValue(sym, out var mutGlobalType))
                        {
                            scope.MutableGlobals[sym] = mutGlobalType;
                        }
                    }
                }
            }
        }

        currentModulePrefix = "";
        currentSourceFile = "main.py";
        ScanGlobals(mainAst);
        ScanFunctions(mainAst);

        foreach (var imp in mainAst.Imports)
        {
            if (modules.TryGetValue(imp.ModuleName, out var srcScope))
            {
                foreach (var sym in imp.Symbols)
                {
                    if (srcScope.Globals.TryGetValue(sym, out var globalSym))
                    {
                        globals[sym] = globalSym;
                    }
                    else if (srcScope.MutableGlobals.TryGetValue(sym, out var mutGlobalType))
                    {
                        mutableGlobals[sym] = mutGlobalType;
                    }
                }
            }
        }

        foreach (var kvp in importedModules)
        {
            var modName = kvp.Key;
            var modAst = kvp.Value;
            string dstPrefix = modName.Replace('.', '_') + "_";

            foreach (var imp in modAst.Imports)
            {
                if (!modules.ContainsKey(imp.ModuleName)) continue;

                string srcPrefix = imp.ModuleName.Replace('.', '_') + "_";

                foreach (var sym in imp.Symbols)
                {
                    if (imp.Aliases.ContainsKey(sym)) continue;

                    string srcClassPrefix = srcPrefix + sym + "_";
                    string dstClassPrefix = dstPrefix + sym + "_";

                    var inlineAdds = new List<KeyValuePair<string, FunctionDef>>();
                    foreach (var funcKvp in inlineFunctions)
                    {
                        if (funcKvp.Key.StartsWith(srcClassPrefix))
                        {
                            string suffix = funcKvp.Key.Substring(srcClassPrefix.Length);
                            inlineAdds.Add(
                                new KeyValuePair<string, FunctionDef>(dstClassPrefix + suffix, funcKvp.Value));
                        }
                    }

                    foreach (var add in inlineAdds)
                    {
                        string newKey = add.Key;
                        string srcKey = srcClassPrefix + newKey.Substring(dstClassPrefix.Length);
                        inlineFunctions[newKey] = add.Value;

                        if (functionParams.TryGetValue(srcKey, out var p)) functionParams[newKey] = p;
                        if (functionReturnTypes.TryGetValue(srcKey, out var rt)) functionReturnTypes[newKey] = rt;
                        if (functionParamTypes.TryGetValue(srcKey, out var pt)) functionParamTypes[newKey] = pt;
                        if (methodInstanceTypes.TryGetValue(srcKey, out var mit)) methodInstanceTypes[newKey] = mit;
                    }

                    foreach (var globKvp in Enumerable.ToList<KeyValuePair<string, SymbolInfo>>(globals))
                    {
                        if (globKvp.Key.StartsWith(srcClassPrefix))
                        {
                            string suffix = globKvp.Key.Substring(srcClassPrefix.Length);
                            globals[dstClassPrefix + suffix] = globKvp.Value;
                        }
                    }
                }
            }
        }

        foreach (var entry in functionsToCompile)
        {
            currentModulePrefix = entry.Prefix;
            currentSourceFile = entry.SourceFile;
            if (!entry.Func.IsInline)
            {
                irProgram.Functions.Add(VisitFunction(entry.Func));
            }
        }

        foreach (var kvp in pendingIsrRegistrations)
        {
            string bareName = kvp.Key;
            int vec = kvp.Value;
            bool found = false;
            foreach (var fn in irProgram.Functions)
            {
                if (fn.Name == bareName ||
                    (fn.Name.Length > bareName.Length &&
                     fn.Name[fn.Name.Length - bareName.Length - 1] == '_' &&
                     fn.Name.EndsWith(bareName)))
                {
                    fn.IsInterrupt = true;
                    fn.InterruptVector = vec;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new Exception(
                    $"compile_isr(): function '{bareName}' not found. Ensure the handler is a top-level function defined in the same translation unit.");
            }
        }

        pendingIsrRegistrations.Clear();

        foreach (var kvp in mutableGlobals)
        {
            irProgram.Globals.Add(new Variable(kvp.Key, kvp.Value));
        }

        var seenExtern = new HashSet<string>();
        foreach (var kvp in externFunctionMap)
        {
            if (seenExtern.Add(kvp.Value))
            {
                irProgram.ExternSymbols.Add(kvp.Value);
            }
        }

        loopStack.Clear();
        externFunctionMap.Clear();
        return irProgram;
    }

    private Val ResolveBinding(string name)
    {
        if (globals.TryGetValue(name, out var symInfo))
        {
            if (symInfo.IsMemoryAddress)
                return new MemoryAddress(symInfo.Value, symInfo.Type);
            return new Constant(symInfo.Value);
        }

        if (mutableGlobals.ContainsKey(name))
        {
            if (!string.IsNullOrEmpty(currentFunction))
            {
                if (currentFunctionGlobals.Contains(name))
                {
                    return new Variable(name, mutableGlobals[name]);
                }

                string localName = currentFunction + "." + name;
                if (constantVariables.TryGetValue(localName, out int localVal))
                {
                    return new Constant(localVal);
                }
            }

            string moduleGlobal = currentModulePrefix + name;
            if (mutableGlobals.TryGetValue(moduleGlobal, out var modType))
            {
                return new Variable(moduleGlobal, modType);
            }

            if (constantVariables.TryGetValue(moduleGlobal, out int modVal))
            {
                return new Constant(modVal);
            }

            if (mutableGlobals.TryGetValue(name, out var bareType))
            {
                return new Variable(name, bareType);
            }

            if (constantVariables.TryGetValue(name, out int bareVal))
            {
                return new Constant(bareVal);
            }
        }

        if (!string.IsNullOrEmpty(currentInlinePrefix))
        {
            string inlineName = currentInlinePrefix + name;
            if (constantVariables.TryGetValue(inlineName, out int inlineVal))
            {
                return new Constant(inlineVal);
            }

            if (constantAddressVariables.TryGetValue(inlineName, out int inlineAddr))
            {
                DataType inlineDt = DataType.UINT8;
                if (variableTypes.TryGetValue(inlineName, out var inlineType))
                    inlineDt = inlineType;
                else if (variableTypes.TryGetValue(name, out var globalType))
                    inlineDt = globalType;
                return new MemoryAddress(inlineAddr, inlineDt);
            }
        }

        if (!string.IsNullOrEmpty(currentFunction) && string.IsNullOrEmpty(currentInlinePrefix))
        {
            string localName = currentFunction + "." + name;
            if (constantVariables.TryGetValue(localName, out int localVal))
            {
                return new Constant(localVal);
            }
        }

        string mg = currentModulePrefix + name;
        if (constantVariables.TryGetValue(mg, out int mgVal))
            return new Constant(mgVal);
        if (constantVariables.TryGetValue(name, out int nameVal))
            return new Constant(nameVal);

        foreach (var mod in modules)
        {
            string mangledMod = mod.Key.Replace('.', '_');
            string modKey = mangledMod + "_" + name;
            if (globals.TryGetValue(modKey, out var modSym))
            {
                if (modSym.IsMemoryAddress)
                    return new MemoryAddress(modSym.Value, modSym.Type);
                return new Constant(modSym.Value);
            }

            if (mutableGlobals.TryGetValue(modKey, out var modMutType))
            {
                return new Variable(modKey, modMutType);
            }
        }

        string finalLocalName = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + name
            : currentFunction + "." + name;

        if (constantVariables.TryGetValue(finalLocalName, out int finVal))
        {
            return new Constant(finVal);
        }

        string? strVal = ResolveStrConstant(finalLocalName);
        if (strVal != null)
        {
            if (stringLiteralIds.TryGetValue(strVal, out int strId))
            {
                return new Constant(strId);
            }

            int newId = nextStringId++;
            stringLiteralIds[strVal] = newId;
            stringIdToStr[newId] = strVal;
            return new Constant(newId);
        }

        DataType type = DataType.UINT8;
        if (variableTypes.TryGetValue(finalLocalName, out var dt))
            type = dt;

        int dotCount = finalLocalName.Count(c => c == '.');
        if (dotCount >= 2)
        {
            string resolved = finalLocalName;
            string lastNonTemp = finalLocalName;
            for (int depth = 0; depth < 20; depth++)
            {
                if (!variableAliases.TryGetValue(resolved, out string next)) break;
                if (next.StartsWith("tmp_"))
                {
                    if (constantVariables.TryGetValue(next, out int tmpVal)) return new Constant(tmpVal);
                    if (constantAddressVariables.TryGetValue(next, out int tmpAddr))
                        return new MemoryAddress(tmpAddr, DataType.UINT16);
                    break;
                }

                resolved = next;
                lastNonTemp = resolved;
            }

            if (lastNonTemp != finalLocalName)
            {
                if (constantVariables.TryGetValue(lastNonTemp, out int lstVal)) return new Constant(lstVal);
                if (constantAddressVariables.TryGetValue(lastNonTemp, out int lstAddr))
                    return new MemoryAddress(lstAddr, DataType.UINT16);
                DataType resolvedType = DataType.UINT8;
                if (variableTypes.TryGetValue(lastNonTemp, out var lastDt)) resolvedType = lastDt;
                return new Variable(lastNonTemp, resolvedType);
            }
        }

        return new Variable(finalLocalName, type);
    }

    private string? ResolveStrConstant(string name)
    {
        var key = name;
        for (var depth = 0; depth < 20; depth++)
        {
            if (key != null && strConstantVariables.TryGetValue(key, out var val)) return val;
            if (key != null && variableAliases.TryGetValue(key, out var alias)) key = alias;
            else break;
        }

        return null;
    }

    private string ResolveCallee(string name)
    {
        int dotPos = name.IndexOf('.');
        if (dotPos != -1)
        {
            string mod = name.Substring(0, dotPos);
            string func = name.Substring(dotPos + 1);
            return mod + "_" + func;
        }

        if (intrinsicNames.Contains(name)) return name;

        if (importedAliases.TryGetValue(name, out var modName))
        {
            var mangledMod = modName?.Replace('.', '_');
            var original = aliasToOriginal.GetValueOrDefault(name, name);
            return mangledMod + "_" + original;
        }


        var prefixTry = currentModulePrefix;
        while (!string.IsNullOrEmpty(prefixTry))
        {
            var candidate = prefixTry + name;
            if (inlineFunctions.ContainsKey(candidate) || functionParams.ContainsKey(candidate))
            {
                return candidate;
            }

            if (prefixTry.Length < 2) break;
            int lastSep = prefixTry.LastIndexOf('_', prefixTry.Length - 2);
            if (lastSep == -1) break;
            prefixTry = prefixTry.Substring(0, lastSep + 1);
        }

        return name;
    }
}
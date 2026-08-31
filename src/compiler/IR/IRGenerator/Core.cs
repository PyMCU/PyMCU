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
using PyMCU.Common.Models;
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
            // Without this a FLOAT argument spelled itself "uint8", so `f(2.5)` could never
            // exact-match `f(x: float)` and every float call fell through to the arity
            // fallback (PyMCU#182).
            DataType.FLOAT => "float",
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
            case FloatLiteral:
                return DataType.FLOAT;
            case BooleanLiteral:
                break;
            case IntegerLiteral il:
                if (il.Value < short.MinValue) return DataType.INT32;
                if (il.Value < sbyte.MinValue) return DataType.INT16;
                if (il.Value < 0) return DataType.INT8;
                if (il.Value <= byte.MaxValue) break;
                if (il.Value <= ushort.MaxValue) return DataType.UINT16;
                return DataType.UINT32;
            case VariableExpr varExpr:
            {
                // Try the same qualifications the read side uses: the inline prefix, then the
                // enclosing function, then the bare name. Only the first was tried, so a plain
                // function LOCAL was never found -- it is registered as `main.x`, not `x` -- and
                // every such argument silently inferred UINT8. `x: float` then spelled itself
                // "uint8" for overload selection, so `math.floor(x)` could not exact-match
                // `floor(x: float)` and fell through to the arity fallback (PyMCU#182).
                foreach (var start in new[]
                {
                    string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + varExpr.Name,
                    string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + varExpr.Name,
                    varExpr.Name,
                })
                {
                    if (start == null) continue;
                    var key = start;
                    for (var i = 0; i < 20; ++i)
                    {
                        if (variableTypes.TryGetValue(key, out var type)) return type;
                        if (variableAliases.TryGetValue(key, out var alias))
                            key = alias;
                        else
                            break;
                    }
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
        // Value-tracking aliases (a = b for plain scalars) are only valid straight-line:
        // at a control-flow join the aliased copy may not have executed on every path, so
        // following it would read (or index by!) the wrong variable. Clear them at labels,
        // exactly like PropagateCopies clears var-consts. Structural aliases (self, params,
        // nonlocal) are not value-tracking and survive.
        if (inst is Label && valueTrackingAliases.Count > 0)
        {
            foreach (var k in valueTrackingAliases) variableAliases.Remove(k);
            valueTrackingAliases.Clear();
        }
        currentInstructions.Add(inst);
    }

    private void PropagateCtState(string src, string dst)
    {
        if (instanceClasses.TryGetValue(src, out var cls))
        {
            instanceClasses[dst] = cls;
            virtualInstances.Add(dst);
        }

        // Copy every descendant key. Nested instance fields are flattened two ways depending
        // on the access path: dotted ("inst._pin") and underscore-joined ("inst__pin", from
        // self._pin field access). Both must follow the instance to its new binding (e.g. a
        // for-in loop variable) or a nested method like self._pin.mode() loses its class and
        // degrades to an undefined CALL.
        void CopyDescendants<T>(Dictionary<string, T> map, string sep)
        {
            string sp = src + sep, dp = dst + sep;
            foreach (var kv in map.Where(kv => kv.Key.StartsWith(sp, StringComparison.Ordinal)).ToList())
                map[dp + kv.Key[sp.Length..]] = kv.Value;
        }

        foreach (var sep in new[] { ".", "_" })
        {
            CopyDescendants(constantVariables, sep);
            CopyDescendants(strConstantVariables, sep);
            CopyDescendants(floatConstantVariables, sep);
            CopyDescendants(constantAddressVariables, sep);
            CopyDescendants(instanceClasses, sep);
            // A field holding a RUN-TIME value has no constant to copy: it is an alias onto
            // the variable that holds it. Leaving those behind bound the loop variable to a
            // field name nothing ever wrote, so `for o in objs: o.g()` read zero.
            CopyDescendants(variableAliases, sep);
        }
    }

    /// <summary>
    /// Bind <paramref name="dst"/> to the instance at <paramref name="src"/> for one unrolled
    /// iteration: carry the compile-time state across, then COPY the fields that live in a
    /// run-time variable. Those have nothing in the compile-time maps to carry, so without the
    /// copies the loop variable named fields that nothing had ever written and every read came
    /// back zero.
    /// </summary>
    private void BindInstanceForIteration(string src, string dst)
    {
        PropagateCtState(src, dst);

        if (!instanceClasses.TryGetValue(src, out var cls) || cls == null) return;
        if (!classFieldLayout.TryGetValue(cls, out var layout)) return;

        foreach (var (field, type, _) in layout)
        {
            string from = src + "_" + field, to = dst + "_" + field;
            if (constantVariables.ContainsKey(to) || variableAliases.ContainsKey(to)) continue;
            if (!classFieldLayout.ContainsKey(type)) // a nested instance is carried, not copied
            {
                DataType dt = DataTypeExtensions.StringToDataType(type);
                variableTypes[to] = dt;
                Emit(new Copy(new Variable(from, dt), new Variable(to, dt)));
            }
        }
    }

    private void CleanCtState(string dst)
    {
        instanceClasses.Remove(dst);

        void RemoveDescendants<T>(Dictionary<string, T> map, string sep)
        {
            string dp = dst + sep;
            foreach (var k in map.Keys.Where(k => k.StartsWith(dp, StringComparison.Ordinal)).ToList())
                map.Remove(k);
        }

        foreach (var sep in new[] { ".", "_" })
        {
            RemoveDescendants(constantVariables, sep);
            RemoveDescendants(strConstantVariables, sep);
            RemoveDescendants(floatConstantVariables, sep);
            RemoveDescendants(variableAliases, sep);
            RemoveDescendants(constantAddressVariables, sep);
            RemoveDescendants(instanceClasses, sep);
        }
    }

    // Carry the chip file's declared geometry into the IR, which is the only channel
    // a backend has to it. DeviceConfig spells "not declared" as 0 for historical
    // reasons; DeviceGeometry spells it as null, so a backend that needs a number it
    // was never given fails the build instead of compiling for a chip with no flash.
    private static DeviceGeometry GeometryOf(DeviceConfig config) => new()
    {
        Chip       = !string.IsNullOrEmpty(config.Chip) ? config.Chip : config.TargetChip,
        RamSize    = config.RamSize    > 0 ? config.RamSize    : null,
        FlashSize  = config.FlashSize  > 0 ? config.FlashSize  : null,
        EepromSize = config.EepromSize > 0 ? config.EepromSize : null,
    };

    /// Names the file an error was raised IN when the error did not name one itself.
    ///
    /// `UserError` attaches `File`; the typed classes (`ValueError`, `TypeError`, ...) are
    /// constructed directly and none of them does, so their line and column came from the AST
    /// node, which belongs to whichever module the code is in, while the renderer fell back to
    /// the entry file. Two halves from two files, which is the pair #227 fixed for the
    /// `UserError` path and this one never had. Issue #230.
    ///
    /// Here rather than at each of the 27 sites: `currentSourcePath` is a field, so unwinding
    /// does not restore it, and at this catch it still holds the value it had at the throw. One
    /// place covers every direct throw inside IR generation, including one added later.
    ///
    /// `LocationIsFinal` is the opt-out, and it has to be a property rather than the exception
    /// class: the deliberate site in `VisitRaise` throws `ArchitectureError`, which is also one
    /// of the compiler-generated classes, so nothing about the type distinguishes them.
    ///
    /// Not covered, and not reachable from here: the three in `Optimizer.cs` and the two in
    /// `CanFailAnalyzer.cs`, which run in later phases.
    public ProgramIR Generate(
        ProgramNode mainAst,
        Dictionary<string, ProgramNode> importedModules,
        DeviceConfig config,
        List<string>? sourceLines = null,
        Dictionary<string, List<string>>? moduleSourceLines = null,
        HashSet<string>? projectModules = null,
        Dictionary<string, string>? modulePaths = null)
    {
        try
        {
            return GenerateCore(mainAst, importedModules, config, sourceLines,
                                moduleSourceLines, projectModules, modulePaths);
        }
        catch (PyMCU.Common.CompilerError e)
            when (!e.LocationIsFinal && e.File == null && !string.IsNullOrEmpty(currentSourcePath))
        {
            throw new PyMCU.Common.CompilerError(
                e.TypeName, e.Message, e.Line, e.Column, e.Length) { File = currentSourcePath };
        }
    }

    private ProgramIR GenerateCore(
        ProgramNode mainAst,
        Dictionary<string, ProgramNode> importedModules,
        DeviceConfig config,
        List<string>? sourceLines = null,
        Dictionary<string, List<string>>? moduleSourceLines = null,
        HashSet<string>? projectModules = null,
        Dictionary<string, string>? modulePaths = null)
    {
        // Assign the PARAMETER, not only the field: inside this method the parameter shadows
        // the field, so every later use of the bare name saw the caller's null.
        projectModules ??= new HashSet<string>();
        this.projectModules = projectModules;
        this.deviceConfig = config;
        this.sourceLines = sourceLines ?? new List<string>();
        this.moduleSourceLines = moduleSourceLines ?? new Dictionary<string, List<string>>();
        this.modulePaths = modulePaths ?? new Dictionary<string, string>();

        // Join the two maps the caller already provides, so a debug listing can be looked up
        // by the path a compiled function carries rather than by a module name reconstructed
        // from its mangled prefix. The reconstruction could never match a dotted name, and a
        // miss was silent: it fell back to the entry file and printed that file's text against
        // the module's line numbers (issue #179).
        this.sourceLinesByPath = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var kv in this.moduleSourceLines)
            if (this.modulePaths.TryGetValue(kv.Key, out var modPath) && modPath.Length > 0)
                this.sourceLinesByPath[modPath] = kv.Value;
        this.lastLine = -1;
        this.currentSourceFile = "";
        this.currentSourcePath = "";

        var irProgram = new ProgramIR { Device = GeometryOf(config) };
        globals.Clear();
        mutableGlobals.Clear();
        functionReturnTypes.Clear();
        functionParams.Clear();
        inlineFunctions.Clear();
        modules.Clear();
        functionsToCompile.Clear();
        intrinsicNames.Clear();
        pendingIsrRegistrations.Clear();
        pendingIsrOrigins.Clear();
        pendingZcaIsrBindings.Clear();
        zcaHandlerAstNodes.Clear();
        pendingZcaSynthFunctions.Clear();
        externFunctionMap.Clear();
        pendingFlashData.Clear();

        foreach (var t in new[] { "uint8", "uint16", "uint32", "int8", "int16", "int32", "int" })
            intrinsicNames.Add(t);
        intrinsicNames.Add("print");
        intrinsicNames.Add("input");
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
        intrinsicNames.Add("bitcast");
        intrinsicNames.Add("gc_alloc");

        if (config.Frequency > 0)
        {
            constantVariables["__FREQ__"] = (int)config.Frequency;
            constantVariables["__FREQUENCY__"] = (int)config.Frequency;
        }

        // Desugar `async def` coroutines into ZCA state-machine classes before any
        // scanning, so the rest of the pipeline sees ordinary classes.
        PyMCU.Frontend.AsyncTransform.TransformProgram(mainAst);
        foreach (var m in importedModules.Values)
            PyMCU.Frontend.AsyncTransform.TransformProgram(m);

        // Fill unannotated params/returns of outlined functions from call-site evidence
        // (safe integer-widening join) BEFORE scanning, so an unannotated helper no longer
        // silently defaults to uint8 and truncates wider arguments.
        PyMCU.Frontend.TypeInference.InferProgram(mainAst, importedModules.Values);

        // Shared with the import check, which has to know these resolve with or without an
        // import naming them (PyMCU.Common.BuiltinExceptionNames).
        foreach (var exn in PyMCU.Common.BuiltinExceptionNames.Codes)
            constantVariables[exn.Key] = exn.Value;

        foreach (var imp in mainAst.Imports)
        {
            if (imp.ModuleName == "pymcu.types")
            {
                intrinsicNames.Add("ptr");
                intrinsicNames.Add("const");
                intrinsicNames.Add("device_info");
                intrinsicNames.Add("inline");
                intrinsicNames.Add("naked");
                intrinsicNames.Add("interrupt");
                intrinsicNames.Add("asm");
                intrinsicNames.Add("compile_isr");
                intrinsicNames.Add("_set_irq_zca_arg");
                intrinsicNames.Add("funcref");
            }

            if (imp.WasStarImport)
                starImports.Add((imp.ModuleName, new List<string>(imp.Symbols)));

            if (imp.Symbols.Count == 0)
            {
                string modKey = string.IsNullOrEmpty(imp.ModuleAlias) ? imp.ModuleName : imp.ModuleAlias;
                modules[modKey] = new ModuleScope();
                // Map the used name (alias or real) to the real module so member/method
                // resolution mangles `t.sleep_ms` (import time as t) to time_sleep_ms.
                importedAliases[modKey] = imp.ModuleName;
            }

            foreach (var sym in imp.Symbols)
            {
                string key = imp.Aliases.ContainsKey(sym) ? imp.Aliases[sym] : sym;
                // Re-export chase: `from pymcu.hal import Pin` where hal/__init__ itself
                // does `from pymcu.hal.gpio import Pin` must bind Pin to the DEFINING
                // module -- mangling against the facade produced an undefined
                // pymcu_hal_Pin. A module that defines the symbol itself ends the chase.
                importedAliases[key] = ResolveReExport(importedModules, imp.ModuleName, sym);
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
                    intrinsicNames.Add("naked");
                    intrinsicNames.Add("interrupt");
                    intrinsicNames.Add("asm");
                    intrinsicNames.Add("compile_isr");
                    intrinsicNames.Add("_set_irq_zca_arg");
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

                // `import x as y` inside an imported module (no symbols). The entry loop
                // above registers these only for the entry file; a module imported
                // transitively (main imports B, B does `import A as a`) also needs its
                // alias mapped so `a.func()` mangles to A_func, not a_func.
                if (imp.Symbols.Count == 0)
                {
                    string modKey = string.IsNullOrEmpty(imp.ModuleAlias) ? imp.ModuleName : imp.ModuleAlias;
                    if (!importedAliases.ContainsKey(modKey))
                        importedAliases[modKey] = imp.ModuleName;
                    if (!modules.ContainsKey(modKey))
                        modules[modKey] = modules.TryGetValue(imp.ModuleName, out var realScope)
                            ? realScope : new ModuleScope();
                }
            }
        }

        // Track which AST objects have already been scanned so that the same
        // physical module file loaded under two different qualified names
        // (e.g. "time" via `import time` AND "pymcu.time" via
        // `from pymcu.time import …`) is only scanned once.  For the alias
        // name we still need all inline functions to be accessible under the
        // alias prefix (e.g. pymcu_time_delay_ms) so we copy them after the
        // canonical scan rather than running ScanFunctions a second time.
        var astToCanonicalPrefix = new Dictionary<ProgramNode, string>(ReferenceEqualityComparer.Instance);

        foreach (var kvp in importedModules)
        {
            var modName = kvp.Key;
            var modAst = kvp.Value;
            string modPrefix = modName.Replace('.', '_') + "_";

            if (astToCanonicalPrefix.TryGetValue(modAst, out var canonicalPrefix))
            {
                // Same AST already fully scanned under canonicalPrefix.
                // Share the same scope object so symbol-propagation loops
                // that look up modules[modName] still find the right entries.
                if (modules.TryGetValue(canonicalPrefix.Substring(0, canonicalPrefix.Length - 1), out var sharedScope))
                    modules[modName] = sharedScope;
                else
                    modules[modName] = new ModuleScope();

                // Propagate inline functions from canonical prefix to alias prefix
                // so callee resolution via importedAliases works for both names.
                var inlineAdds = new List<KeyValuePair<string, FunctionDef?>>();
                foreach (var fn in inlineFunctions)
                {
                    if (!fn.Key.StartsWith(canonicalPrefix)) continue;
                    string aliasKey = modPrefix + fn.Key.Substring(canonicalPrefix.Length);
                    if (!inlineFunctions.ContainsKey(aliasKey))
                        inlineAdds.Add(new KeyValuePair<string, FunctionDef?>(aliasKey, fn.Value));
                }
                foreach (var add in inlineAdds)
                {
                    inlineFunctions[add.Key] = add.Value;
                    string srcKey = canonicalPrefix + add.Key.Substring(modPrefix.Length);
                    if (functionParams.TryGetValue(srcKey, out var p)) functionParams.TryAdd(add.Key, p);
                    if (functionReturnTypes.TryGetValue(srcKey, out var rt)) functionReturnTypes.TryAdd(add.Key, rt);
                    if (functionParamTypes.TryGetValue(srcKey, out var pt)) functionParamTypes.TryAdd(add.Key, pt);
                }
                // Propagate globals under the alias prefix too.
                var globAdds = new List<KeyValuePair<string, SymbolInfo>>();
                foreach (var g in globals)
                {
                    if (!g.Key.StartsWith(canonicalPrefix)) continue;
                    string aliasKey = modPrefix + g.Key.Substring(canonicalPrefix.Length);
                    if (!globals.ContainsKey(aliasKey))
                        globAdds.Add(new KeyValuePair<string, SymbolInfo>(aliasKey, g.Value));
                }
                foreach (var add in globAdds)
                    globals[add.Key] = add.Value;

                continue;
            }

            astToCanonicalPrefix[modAst] = modPrefix;
            modules[modName] = new ModuleScope();
            currentModulePrefix = modPrefix;
            int dotPos = modName.LastIndexOf('.');
            currentSourceFile = (dotPos != -1 ? modName.Substring(dotPos + 1) : modName) + ".py";
            currentSourcePath = PathOfModule(modName);
            ScanGlobals(modAst, modules[modName]);
            ScanFunctions(modAst, modules[modName]);
            RefuseCodegenDecoratorsOnExpandedFunctions(modAst);
        }

        foreach (var kvp in importedModules)
        {
            var modName = kvp.Key;
            var modAst = kvp.Value;
            if (!modules.TryGetValue(modName, out var scope))
            {
                // Module was not scanned in the first pass - this can happen if
                // importedModules was modified after the first foreach.
                // Create the scope now to avoid KeyNotFoundException.
                scope = new ModuleScope();
                modules[modName] = scope;
                currentModulePrefix = modName.Replace('.', '_') + "_";
                int dotPos2 = modName.LastIndexOf('.');
                currentSourceFile = (dotPos2 != -1 ? modName.Substring(dotPos2 + 1) : modName) + ".py";
                currentSourcePath = PathOfModule(modName);
                ScanGlobals(modAst, scope);
                ScanFunctions(modAst, scope);
                RefuseCodegenDecoratorsOnExpandedFunctions(modAst);
            }
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

        // Propagate instanceClasses for `from X import Y` imports so that
        // GetValClass can find the ZCA class when user code uses imported
        // singletons via subscript (e.g. `from machine import mem8; mem8[addr]`).
        foreach (var imp in mainAst.Imports)
        {
            string modPrefix = imp.ModuleName.Replace('.', '_') + "_";
            foreach (var sym in imp.Symbols)
            {
                string key = imp.Aliases.ContainsKey(sym) ? imp.Aliases[sym] : sym;
                string importedKey = modPrefix + sym;
                if (instanceClasses.TryGetValue(importedKey, out var importedClass))
                    instanceClasses[key] = importedClass;
            }
        }

        currentModulePrefix = "";
        currentSourceFile = "main.py";
        // The entry file is what diagnostics are reported against by default, so it carries no
        // path of its own: an empty path means "the file the compiler was invoked on".
        currentSourcePath = "";

        // Record entry-file module-level `name = Ctor(...)` targets: their construction
        // is injected into main as module init, but later references resolve them as
        // module globals — the instance tracking must use the module key (SlotInstanceKey).
        foreach (var s in mainAst.GlobalStatements)
            if (s is AssignStmt { Target: VariableExpr tlTv, Value: CallExpr })
                topLevelInstanceTargets.Add(tlTv.Name);

        ScanGlobals(mainAst);
        ScanFunctions(mainAst);
        RefuseCodegenDecoratorsOnExpandedFunctions(mainAst);

        // Synthesize a `main` function from top-level executable statements when the
        // user has not written an explicit `def main():`.  This allows MicroPython-
        // and CircuitPython-style scripts that have no entry-point wrapper.
        bool hasExplicitMain = mainAst.Functions.Any(f => f.Name == "main");
        if (!hasExplicitMain)
        {
            var executableStmts = mainAst.GlobalStatements
                .Where(s => !IsTopLevelPureDeclaration(s))
                .ToList();

            if (executableStmts.Count > 0)
            {
                var syntheticBlock = new Block();
                foreach (var s in executableStmts)
                    syntheticBlock.Statements.Add(s);

                var syntheticMain = new FunctionDef("main", new List<Param>(), "None", syntheticBlock);
                functionsToCompile.Insert(0,
                    new FunctionEntry { Prefix = "", Func = syntheticMain, SourceFile = "main.py", SourcePath = "" });
                functionReturnTypes["main"] = "None";
                functionParams["main"] = new List<string>();
                functionParamTypes["main"] = new List<DataType>();

                // A top-level script is an entry point too. Running an imported module's own
                // module level was wired to the explicit `def main():` branch only, so the
                // MicroPython and CircuitPython shape -- the one with no entry-point wrapper,
                // and the shape #117 was reported in -- still read every module-level value of
                // its imported modules as zero, with nothing to say so.
                EmitImportedModuleInit(syntheticMain, importedModules, astToCanonicalPrefix);
            }
        }
        else
        {
            // Explicit `def main()`. Module-level executable statements run at STARTUP,
            // before main()'s body -- mirroring Python, where the module body executes
            // before the entry point. (Previously they were rejected or silently dropped,
            // so a Pin/UART/sensor constructed at module scope never configured its
            // hardware -- only one constructed *inside* main did.)
            var mainFuncDef = mainAst.Functions.FirstOrDefault(f => f.Name == "main");
            if (mainFuncDef != null)
            {
                // Collect module-level statements with a runtime effect, in source order.
                // Pure declarations (imports, class defs, const/already-folded globals) are
                // skipped. A VarDecl initializer for a mutable global becomes an AnnAssign so
                // the global is actually written -- zero inits included: the AVR backend may
                // give a mutable global a register home, which BSS zeroing never touches, and
                // AVR registers power up undefined (the emulator zeroes them, real silicon
                // does not). Everything else -- AnnAssign SRAM arrays, plain constructions like
                // `led = Pin(...)`, bare calls, control flow -- runs as written.
                var moduleInit = new List<Statement>();
                foreach (var s in mainAst.GlobalStatements)
                {
                    if (IsTopLevelPureDeclaration(s)) continue;

                    // `if __name__ == "__main__": main()` -- the guard is true here (the entry
                    // file IS __main__, so the condition already folded away), and its body
                    // calls the entry point PyMCU calls itself. Inserting that call into main's
                    // own body made the cycle detector report `main -> main`, a recursion the
                    // user never wrote, for the most universal idiom in Python. The call is
                    // redundant, not wrong: drop it and let the entry point run once.
                    if (IsEntryPointSelfCall(s)) continue;

                    if (s is VarDecl d)
                    {
                        if (d.Init != null
                            && mutableGlobals.ContainsKey(d.Name)
                            && !globals.ContainsKey(d.Name))
                            moduleInit.Add(new AnnAssign(d.Name, d.VarType, d.Init));
                        continue;
                    }
                    moduleInit.Add(s);
                }

                // Insert AFTER the build's auto-injected `_pymcu_*` preamble (clock_init,
                // millis_init, stdout) so module-level peripheral setup sees the final clocks
                // and stdout, but BEFORE the user's own main body.
                var body = mainFuncDef.Body.Statements;
                int at = 0;
                while (at < body.Count && IsInjectedPreamble(body[at])) at++;
                for (int i = moduleInit.Count - 1; i >= 0; i--)
                    body.Insert(at, moduleInit[i]);

                // An import runs before the file that imports it, so this goes in AFTER the
                // entry module's own init and therefore ends up ahead of it.
                EmitImportedModuleInit(mainFuncDef, importedModules, astToCanonicalPrefix);
            }
        }

        foreach (var imp in mainAst.Imports)
        {
            if (modules.TryGetValue(imp.ModuleName, out var srcScope))
            {
                foreach (var sym in imp.Symbols)
                {
                    // A symbol that is already a known compile-time constant (e.g. the
                    // builtin exception codes ValueError=1, …, predefined regardless of
                    // import) must NOT be re-imported as a data global — doing so shadows
                    // the constant with an undefined symbol and breaks `raise ValueError`.
                    if (constantVariables.ContainsKey(sym)) continue;
                    if (srcScope.Globals.TryGetValue(sym, out var globalSym))
                    {
                        globals[sym] = globalSym;
                    }
                    else if (srcScope.MutableGlobals.ContainsKey(sym))
                    {
                        // Deliberately NOT `mutableGlobals[sym] = type`. The name already has
                        // storage, under the defining module's own key, and giving it a second
                        // one here split the variable in two: the module initializer wrote the
                        // declared value into `<mod>_<sym>` while the module's own functions and
                        // this file both wrote and read the bare name, so the declared value was
                        // in the firmware and nothing could reach it. ResolveBinding resolves
                        // this name through importedAliases to the one slot that exists.
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

                    // Re-export a plain @inline function under the facade name. The
                    // class-method loop below only matches "prefix_sym_<method>" keys, so
                    // the exact function key "prefix_sym" (e.g. millis) would otherwise be
                    // left unmapped and its call site would emit an unresolved CALL.
                    string srcExact = srcPrefix + sym;
                    string dstExact = dstPrefix + sym;
                    if (inlineFunctions.TryGetValue(srcExact, out var exactFn)
                        && !inlineFunctions.ContainsKey(dstExact))
                    {
                        inlineFunctions[dstExact] = exactFn;
                        if (functionParams.TryGetValue(srcExact, out var ep)) functionParams[dstExact] = ep;
                        if (functionReturnTypes.TryGetValue(srcExact, out var ert)) functionReturnTypes[dstExact] = ert;
                        if (functionParamTypes.TryGetValue(srcExact, out var ept)) functionParamTypes[dstExact] = ept;
                        if (methodInstanceTypes.TryGetValue(srcExact, out var emit)) methodInstanceTypes[dstExact] = emit;
                        // Keep the DEFINING module so inlining the re-exported function
                        // still resolves its internal helper calls in the original module.
                        if (functionModulePrefix.TryGetValue(srcExact, out var emp)) functionModulePrefix[dstExact] = emp;
                    }

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

                    // Carry the OVERLOAD REGISTRY across the re-export as well. Once a name is
                    // overloaded its bare key is deliberately vacated in inlineFunctions so that
                    // suffix resolution can work, so copying inlineFunctions alone gives the
                    // facade the suffixed keys and nothing that records the name as overloaded.
                    // Constructor resolution asks exactly that question, so an overloaded
                    // __init__ reached through a facade was found under neither the bare key nor
                    // the overload set, and the call site reported the class as not exported --
                    // naming, as the near miss, the name it had just refused.
                    var ovlAdds = new List<string>();
                    foreach (var ovl in overloadedFunctions)
                    {
                        if (ovl == srcExact)
                            ovlAdds.Add(dstExact);
                        else if (ovl.StartsWith(srcClassPrefix))
                            ovlAdds.Add(dstClassPrefix + ovl.Substring(srcClassPrefix.Length));
                    }
                    foreach (var add in ovlAdds)
                        overloadedFunctions.Add(add);

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

        // Runs here, not during the scan: it needs the class layouts and the instance-to-class
        // map, and both are only complete once every module has been scanned.
        currentModulePrefix = "";
        MarkModuleInstanceFields(mainAst);

        // Every imported module too, under its own prefix. The stdlib is deliberately out of
        // scope here, for the same reason EmitImportedModuleInit leaves it out: its modules are
        // written knowing that only the entry file's top level runs.
        foreach (var modKvp in importedModules)
        {
            if (!astToCanonicalPrefix.TryGetValue(modKvp.Value, out var markPrefix)) continue;
            if (!projectModules.Contains(modKvp.Key)) continue;
            currentModulePrefix = markPrefix;
            MarkModuleInstanceFields(modKvp.Value);
        }
        currentModulePrefix = "";

        ForceInlineClassReturningFactories();

        // A synthesized `__module_init` is LOWERED before the rest, then put back where it was.
        //
        // Lowering the module level is what BINDS a module-level instance's fields: a Pin's
        // port, direction register and bit are compile-time constants established by the
        // construction. EmitImportedModuleInit appends the init to the end of the list, so a
        // function of that same module was lowered first and read the fields as run-time
        // values instead -- `led = Pin("PB5", Pin.OUT)` at a module's top level still failed
        // from a function of that module, now on the field read rather than on the missing
        // construction.
        //
        // Conditional because lowering order is observable: it advances the shared label,
        // temporary and string-literal counters, so hoisting unconditionally renumbered every
        // program in the corpus (and shifted interned string ids) for a binding only a module
        // with an init needs. A program without one lowers exactly as it always did.
        //
        // Only the ORDER OF LOWERING changes. Emission order is restored below.
        var lowered = new Function?[functionsToCompile.Count];
        var lowerOrder = new List<int>(functionsToCompile.Count);
        var initFirst = new List<int>();

        // The ENTRY file has no `__module_init` of its own: its module level is injected into
        // main's body, so lowering MAIN is what binds an object built there. Any other function
        // of the entry file was lowered first and read the instance's fields as run-time values,
        // which the backend reported as a bit index through a runtime pointer, at a line the
        // file does not have. Same binding, same remedy, same scope: only a file that builds an
        // instance at its module level moves, so every other program lowers as it always did.
        var mainFirst = new List<int>();
        bool hoistEntryMain = EntryModuleLevelBuildsInstance(mainAst);
        if (hoistEntryMain)
            Logger.Verbose("IRGen", "entry file builds an instance at module level; lowering main first");

        for (int i = 0; i < functionsToCompile.Count; i++)
        {
            if (functionsToCompile[i].Func.Name == "__module_init") initFirst.Add(i);
            else if (hoistEntryMain && string.IsNullOrEmpty(functionsToCompile[i].Prefix)
                     && functionsToCompile[i].Func.Name == "main") mainFirst.Add(i);
            else lowerOrder.Add(i);
        }
        lowerOrder.InsertRange(0, mainFirst);
        lowerOrder.InsertRange(0, initFirst);

        foreach (int i in lowerOrder)
        {
            var entry = functionsToCompile[i];
            currentModulePrefix = entry.Prefix;
            currentSourceFile = entry.SourceFile;
            currentSourcePath = entry.SourcePath;
            if (!entry.Func.IsInline)
            {
                lowered[i] = VisitFunction(entry.Func);
            }
        }

        foreach (var fn in lowered)
            if (fn != null)
                irProgram.Functions.Add(fn);

        // Inject FlashData instructions (global const[uint8[N]] arrays) into the
        // main function body so the backend emits .byte tables in flash.
        if (pendingFlashData.Count > 0)
        {
            var mainFunc = irProgram.Functions.FirstOrDefault(f => f.Name == "main");
            if (mainFunc != null)
            {
                mainFunc.Body.InsertRange(0, pendingFlashData);
            }
        }

        foreach (var sf in pendingZcaSynthFunctions)
            irProgram.Functions.Add(sf);
        pendingZcaSynthFunctions.Clear();

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
                pendingIsrOrigins.TryGetValue(bareName, out var origin);
                bool fromModule = !string.IsNullOrEmpty(origin.Module);
                string where = string.IsNullOrEmpty(origin.Function)
                    ? ""
                    : $" inside '{origin.Function}'";
                string at = fromModule && origin.Line > 0
                    ? $" The call is at line {origin.Line} of the module that defines it, not of " +
                      "the file being compiled."
                    : "";
                throw new PyMCU.Common.CompilerError("CompileError",
                    $"compile_isr(){where} could not resolve '{bareName}' to a function. The " +
                    "handler must be a compile-time function reference: either a top-level " +
                    "function in this translation unit, or a parameter that folds to one. A " +
                    "function that calls compile_isr() with a handler parameter must be " +
                    $"@inline -- without it the parameter stays a run-time value and no " +
                    $"function can be resolved.{at}",
                    fromModule || origin.Line <= 0 ? 1 : origin.Line, 1);
            }
        }

        pendingIsrRegistrations.Clear();
        pendingIsrOrigins.Clear();

        foreach (var kvp in mutableGlobals)
        {
            irProgram.Globals.Add(new Variable(kvp.Key, kvp.Value));
        }

        // Module-level SRAM arrays must be allocated as globals so the overlay
        // algorithm never aliases them with function-local arrays across sibling calls.
        foreach (var name in moduleSramArrays)
        {
            int count = arraySizes.TryGetValue(name, out int c) ? c : 1;
            DataType elemType = arrayElemTypes.TryGetValue(name, out DataType dt) ? dt : DataType.UINT8;
            irProgram.GlobalArrays[name] = count * elemType.SizeOf();
        }

        var seenExtern = new HashSet<string>();
        foreach (var kvp in externFunctionMap)
        {
            if (seenExtern.Add(kvp.Value))
            {
                irProgram.ExternSymbols.Add(kvp.Value);
                // Carry the DECLARED widths across to the backend: an @extern function has no
                // body, so it never reaches irProgram.Functions, and without this the call site
                // sizes each argument by the width of the value instead of the parameter --
                // f(5) to a uint16_t loaded one byte and left the high one undefined.
                irProgram.ExternSignatures.Add(new ExternSignature
                {
                    Symbol = kvp.Value,
                    ParamTypes = functionParamTypes.TryGetValue(kvp.Key, out var extParamTypes)
                        ? new List<DataType>(extParamTypes)
                        : new List<DataType>(),
                    ReturnType = functionReturnTypes.TryGetValue(kvp.Key, out var extRet)
                        ? DataTypeExtensions.StringToDataType(extRet ?? "void")
                        : DataType.VOID,
                });
            }
        }

        foreach (var sym in exnExterns)
            if (seenExtern.Add(sym))
                irProgram.ExternSymbols.Add(sym);

        // If any try/raise was emitted, allocate the global 2-byte active-jmpbuf pointer.
        if (exnExterns.Count > 0)
        {
            irProgram.Globals.Add(new Variable("__pymcu_active_jmpbuf", DataType.UINT16));
        }

        loopStack.Clear();
        externFunctionMap.Clear();
        exnExterns.Clear();

        if (_pendingFlashData.Count > 0)
        {
            var mainFunc = irProgram.Functions.FirstOrDefault(f => f.Name == "main");
            if (mainFunc != null) mainFunc.Body.InsertRange(0, _pendingFlashData);
            _pendingFlashData.Clear();
        }

        // Two functions compiled under the same name (e.g. `def f()` twice without an
        // overload-distinguishing signature) would otherwise crash a downstream
        // ToDictionary(f => f.Name) with a raw "An item with the same key has already been
        // added" reported as an InternalCompilerError. Report it as a clean diagnostic.
        var dupFn = irProgram.Functions
            .GroupBy(f => f.Name)
            .FirstOrDefault(g => g.Count() > 1);
        if (dupFn != null)
            throw new PyMCU.Common.CompilerError("CompileError",
                $"duplicate function definition: '{dupFn.Key}' is defined more than once " +
                "(give the overloads different parameter types, or rename one)", 1, 1);

        // Propagate class hierarchy so the Optimizer devirt pass and AvrCodeGen
        // can work without access to IRGenerator-internal state.
        irProgram.ClassChildren = new Dictionary<string, HashSet<string>>(
            classChildren.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value)));
        irProgram.ClassDirectMethods = new Dictionary<string, HashSet<string>>(
            classDirectMethods.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value)));

        // An outlined body reachable from two contexts that can interrupt each other is not
        // reentrant, and its statics are shared between them. Checked on the finished IR so an
        // @inline body, already expanded into its callers, cannot be flagged.
        CheckReentrancy(irProgram);

        return irProgram;
    }

    /// <summary>
    /// The tail of the "not defined" message when a star import is in scope. "never imported"
    /// reads as a contradiction of the import the reader can see, so name the star and what it
    /// did bring in. A leading underscore is the usual reason a name is missing: it is private
    /// by convention and a star never binds one, in CPython either.
    /// </summary>
    private string StarImportHint(string name)
    {
        if (starImports.Count == 0) return "";

        var parts = new List<string>();
        foreach (var (module, names) in starImports)
        {
            string brought = names.Count == 0
                ? "nothing: it exports no public top-level name"
                : string.Join(", ", names.Take(8)) + (names.Count > 8 ? ", ..." : "");
            parts.Add($"'from {module} import *' brings in {brought}");
        }

        string why = name.StartsWith('_')
            ? $". A name starting with '_' is private and no star import binds it, here or in "
              + $"CPython; write `from <its module> import {name}` if that is what you meant"
            : "";

        return $". {string.Join("; ", parts)}{why}";
    }

    private bool InlineScopeShadows(string name)
    {
        if (string.IsNullOrEmpty(currentInlinePrefix)) return false;
        string k = currentInlinePrefix + name;
        return constantVariables.ContainsKey(k) || strConstantVariables.ContainsKey(k)
            || floatConstantVariables.ContainsKey(k) || variableAliases.ContainsKey(k)
            || constantAddressVariables.ContainsKey(k) || variableTypes.ContainsKey(k);
    }

    /// <summary>
    /// A module-level `main()` with no arguments: the call the runtime already makes. Written
    /// by hand or left by the `if __name__ == "__main__":` guard, it means the same thing.
    /// </summary>
    private static bool IsEntryPointSelfCall(Statement s)
        => s is ExprStmt { Expr: CallExpr { Callee: VariableExpr { Name: "main" } } call }
           && call.Args.Count == 0;

    /// <summary>
    /// The diagnostic for indexing an unrolled array with a run-time value. The subscript is
    /// not the problem -- the same subscript on a declared array compiles -- so a message about
    /// the subscript sends the reader off trying to make the INDEX constant, which defeats the
    /// buffer. Name the array and the annotation that makes it indexable.
    /// </summary>
    /// <param name="at">
    /// The ARRAY the message names, not the whole subscript and not the index. `xs[i]` has
    /// three candidates and the message is about `xs`: it asks the reader to declare a type
    /// for the array, so a caret under `i` would send them to make the index constant, which
    /// is the reading the message was written to prevent.
    /// </param>
    private Exception UnrolledArrayIndexError(string qualified, ASTNode? at = null)
    {
        int dot = qualified.LastIndexOf('.');
        string shown = dot >= 0 ? qualified[(dot + 1)..] : qualified;
        string elem = arrayElemTypes.TryGetValue(qualified, out var et) && et != DataType.UNKNOWN
            ? et.ToString().ToLowerInvariant()
            : "uint8";
        int size = arraySizes.TryGetValue(qualified, out var sz) ? sz : 0;
        string example = size > 0 ? $"{shown}: {elem}[{size}] = [...]" : $"{shown}: {elem}[N] = [...]";
        return UserError(
            $"'{shown}' has no declared array type, so it lives as separate variables and can "
            + "only be indexed with a constant. Declare it as an array to index it at run time, "
            + $"e.g. `{example}`", at);
    }

    /// <param name="at">
    /// The expression node the name was read from, when the caller has it. Only used to locate
    /// the "not defined" diagnostic: a name is a token, so it has a column, and the caret
    /// belongs under the name rather than under the first character of the statement. Callers
    /// that hold no node pass none and get a statement-level location with no caret.
    /// </param>
    private Val ResolveBinding(string name, PyMCU.Frontend.ASTNode? at = null)
        => ResolveBindingCore(name, at, probe: false)!;

    /// Resolves <paramref name="name"/> for a caller that is ASKING rather than lowering, and
    /// answers null instead of throwing when the name is simply not defined.
    ///
    /// The distinction that matters is between "I do not know this name" and "this name is
    /// refused". A module-level `raise CompileError(...)` guard that folded away -- the HAL
    /// saying this part has no hardware UART, say -- is a definite answer with a written reason,
    /// and it keeps throwing even here: swallowing it would replace one good sentence with the
    /// generic "call to undefined function 'uart_init'". Only the plain never-defined case is
    /// downgraded to null, because that is the one where the asker has a better message of its
    /// own to reach.
    private Val? ProbeBinding(string name) => ResolveBindingCore(name, null, probe: true);

    private Val? ResolveBindingCore(string name, PyMCU.Frontend.ASTNode? at, bool probe)
    {
        if (globals.TryGetValue(name, out var symInfo))
        {
            if (symInfo.IsMemoryAddress)
                return new MemoryAddress(symInfo.Value, symInfo.Type);
            return new Constant(symInfo.Value);
        }

        // Loop variable bound to a function reference (zip() over a function list).
        if (loopFunctionAliases.TryGetValue(name, out string? fnAliasName))
            return new FunctionRef(fnAliasName);

        // Inside an @inline expansion a bound parameter or local shadows any module
        // global of the same name: `uart.write('hello')` inlines write(data=...) and a
        // user-level `data = 5` global must not hijack the body's reads of `data`
        // (the const[str] binding then went unseen and the call hard-errored).
        if (mutableGlobals.ContainsKey(name) && !InlineScopeShadows(name))
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

                // A PARAMETER of a plain (non-@inline) function shadows a module global
                // of the same name -- Python scoping. Without this, a user-level
                // `start_low_ms = 250` hijacked every read of dht_read's start_low_ms
                // parameter and the driver held its start pulse for 250 ms. Only
                // parameters can collide here: assigning a global-named local inside a
                // non-main function is already a NameError, and `main` IS the module's
                // top level, where the qualified name and the global are one binding.
                if (currentFunction != "main"
                    && variableTypes.TryGetValue(localName, out var localDt))
                {
                    return new Variable(localName, localDt);
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

        // Resolve bare names that refer to mutable globals in the current module
        // (e.g. `_num_tasks` inside rtos.py functions, where the IR key is `rtos__num_tasks`).
        if (!string.IsNullOrEmpty(currentModulePrefix))
        {
            string modKey2 = currentModulePrefix + name;
            string localFnKey = string.IsNullOrEmpty(currentFunction) ? "" : currentFunction + "." + name;
            bool hasLocalDecl = !string.IsNullOrEmpty(localFnKey) &&
                                (variableTypes.ContainsKey(localFnKey) || constantVariables.ContainsKey(localFnKey));
            if (!hasLocalDecl && mutableGlobals.TryGetValue(modKey2, out var modType2))
                return new Variable(modKey2, modType2);
        }

        // `from module import sym` where sym is a mutable global (e.g. `from machine import mem8`)
        if (importedAliases.TryGetValue(name, out var importedAliasMod))
        {
            var origName = aliasToOriginal.TryGetValue(name, out var origAlias) ? origAlias : name;
            string importedPrefix = importedAliasMod.Replace('.', '_') + "_";
            string importedKey = importedPrefix + origName;
            if (mutableGlobals.TryGetValue(importedKey, out var importedAliasType))
                return new Variable(importedKey, importedAliasType);
        }

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
            // A name bound to a one-character string is in BOTH maps: the numeric one holds its
            // character code and the string one holds its text. The numeric lookup wins here, so
            // without carrying the text the read arrives downstream as a bare number.
            return new Constant(finVal, ResolveStrConstant(finalLocalName));
        }

        string? strVal = ResolveStrConstant(finalLocalName);
        if (strVal != null)
        {
            // The text rides along with the id. It matters most for a ONE-character string,
            // whose id here is an interned number while the same literal in expression position
            // is its character code -- two encodings of one string, which a comparison by value
            // reads as unequal.
            if (stringLiteralIds.TryGetValue(strVal, out int strId))
            {
                return new Constant(strId, strVal);
            }

            int newId = nextStringId++;
            stringLiteralIds[strVal] = newId;
            stringIdToStr[newId] = strVal;
            return new Constant(newId, strVal);
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
                        return new MemoryAddress(tmpAddr,
                            variableTypes.TryGetValue(next, out var tmpDt) ? tmpDt : DataType.UINT16);
                    break;
                }

                resolved = next;
                lastNonTemp = resolved;
            }

            if (lastNonTemp != finalLocalName)
            {
                if (constantVariables.TryGetValue(lastNonTemp, out int lstVal)) return new Constant(lstVal);
                if (constantAddressVariables.TryGetValue(lastNonTemp, out int lstAddr))
                    return new MemoryAddress(lstAddr,
                        variableTypes.TryGetValue(lastNonTemp, out var lstDt) ? lstDt : DataType.UINT16);
                DataType resolvedType = DataType.UINT8;
                if (variableTypes.TryGetValue(lastNonTemp, out var lastDt)) resolvedType = lastDt;
                else if (mutableGlobals.TryGetValue(lastNonTemp, out var lastGlobalDt)) resolvedType = lastGlobalDt;
                return new Variable(lastNonTemp, resolvedType);
            }
        }

        // Closure capture: a nested @inline function may read a variable from the ENCLOSING
        // function (e.g. `return x + base`, where base is a local of the caller). Such a free
        // variable is not bound under this inline prefix, and the earlier enclosing-scope lookup
        // only runs when no inline prefix is active — so without this it fell through to an
        // unbound (zero) local, silently dropping the capture. Resolve it to the enclosing
        // function's binding when the inline-qualified name is genuinely unknown.
        if (!string.IsNullOrEmpty(currentInlinePrefix) && !variableTypes.ContainsKey(finalLocalName))
        {
            // Candidate enclosing scopes, innermost first: each enclosing inline expansion's
            // prefix (so a capture from an enclosing @inline like `outer` resolves), then the
            // enclosing plain function. The topmost stack entry is THIS expansion — skip it.
            for (int si = inlineStack.Count - 2; si >= 0; --si)
            {
                string p = inlineStack[si].Prefix;
                if (string.IsNullOrEmpty(p)) continue;
                string enc = p + name;
                if (enc == finalLocalName) continue;
                if (constantVariables.TryGetValue(enc, out int ec)) return new Constant(ec);
                if (constantAddressVariables.TryGetValue(enc, out int ea2))
                    return new MemoryAddress(ea2, variableTypes.TryGetValue(enc, out var ead) ? ead : DataType.UINT16);
                if (variableTypes.TryGetValue(enc, out var et)) return new Variable(enc, et);
            }

            if (!string.IsNullOrEmpty(currentFunction))
            {
                string enclosing = currentFunction + "." + name;
                if (enclosing != finalLocalName)
                {
                    if (constantVariables.TryGetValue(enclosing, out int encConst)) return new Constant(encConst);
                    if (constantAddressVariables.TryGetValue(enclosing, out int encAddr))
                        return new MemoryAddress(encAddr,
                            variableTypes.TryGetValue(enclosing, out var encAddrDt) ? encAddrDt : DataType.UINT16);
                    if (variableTypes.TryGetValue(enclosing, out var encType)) return new Variable(enclosing, encType);
                }
            }
        }

        // Nothing above resolved the name, and two very different situations reach this line.
        // One is a binding this generator holds under some other key: an inline handler's
        // parameter, a type-annotated instance, a tuple result slot, a runtime-bounded slice.
        // The other is a name the program never defines -- a typo, which used to become a read
        // of a slot nobody ever wrote, so the firmware shipped with whatever the RAM held and
        // no diagnostic said a word. Invent the local only for the first.
        if (!IsNameKnownSomewhere(finalLocalName, name))
        {
            // The name may be missing because its defining module REFUSED this target: a
            // module-level `raise CompileError(...)` guard whose enclosing if/match folded
            // away, so none of the module's symbols exist. The call path has reported that
            // properly for a while (see EmitRegularFunctionCall); a plain name read did not,
            // and answered "name 'uart_init' is not defined" for an internal helper the user
            // never wrote, on a chip whose HAL had said in one sentence why it cannot be built.
            foreach (var g in moduleGuardErrors.OrderByDescending(kv => kv.Key.Length))
                if (finalLocalName.StartsWith(g.Key, StringComparison.Ordinal)
                    || currentModulePrefix.StartsWith(g.Key, StringComparison.Ordinal))
                    // `at` and not LocationIsFinal. The message names ANOTHER file, which is
                    // where the guard is, but the position this diagnostic reports is the READ
                    // that failed, and that read is in the file being lowered. Two different
                    // things: the caret says where to look in the program in front of you, the
                    // sentence says where the refusal came from.
                    //
                    // UNVERIFIED, and said out loud rather than left to look tested. Every
                    // program written to reach this reached the CALL path instead, which is
                    // located already (EmitRegularFunctionCall) and reports the same sentence
                    // from a different site -- so a test built on one of them would have passed
                    // without this line and claimed to cover it. What is left is a plain name
                    // read of a symbol from a module its own guard refused, which the comment
                    // above describes as arising from an internal HAL helper rather than from
                    // anything a user writes. The node costs nothing and cannot be worse than
                    // the fallback; it is simply not pinned.
                    throw UserError($"{g.Value.Msg} (module guard at {g.Value.File}:{g.Value.Line})", at);

            // A probing caller gets null and reaches its own, more specific diagnostic. Note
            // this sits AFTER the module-guard check above, which throws for everyone.
            if (probe) return null;

            throw UserError(
                $"name '{name}' is not defined -- it is read here but never assigned, " +
                "imported, or received as a parameter" + StarImportHint(name), at);
        }

        return new Variable(finalLocalName, type);
    }

    /// <summary>
    /// True when some table in this generator knows <paramref name="qualified"/> or the bare
    /// <paramref name="name"/>, under any of the prefixes a binding can be filed by. Used at the
    /// end of <see cref="ResolveBinding"/> to tell "bound through another path" apart from
    /// "never defined": the question is deliberately asked of every table rather than of
    /// variableTypes alone, because the paths that legitimately reach the fallback file their
    /// bindings elsewhere (aliases, instance classes, arrays, literal params).
    /// </summary>
    private bool IsNameKnownSomewhere(string qualified, string name)
    {
        // Compiler-generated names (temporaries, anonymous constructor targets, inline result
        // slots) are never user-written, so they can never be a typo.
        if (name.StartsWith("tmp_") || name.StartsWith("__c") || name.StartsWith("__slc")
            || name.StartsWith("__unpack") || name.StartsWith("_irq_synth_"))
            return true;

        // `self` is bound by the expansion machinery, not by any statement in the source.
        if (name == "self") return true;

        // A Python builtin: whatever is wrong with the call, the diagnostic that names the
        // builtin (VisitCall, via UnsupportedBuiltins) is better than "not defined" -- a builtin
        // is in scope everywhere, so it was never going to be assigned or imported. The full
        // builtins namespace is used, not a hand-kept subset, so `sorted` and `oct` reach the
        // same diagnostic `round` and `isinstance` do.
        if (PythonBuiltins.Contains(name)) return true;

        var keys = new List<string> { qualified, name };
        if (!string.IsNullOrEmpty(currentFunction)) keys.Add(currentFunction + "." + name);
        if (!string.IsNullOrEmpty(currentModulePrefix)) keys.Add(currentModulePrefix + name);
        if (!string.IsNullOrEmpty(currentInlinePrefix)) keys.Add(currentInlinePrefix + name);
        foreach (var frame in inlineStack)
            if (!string.IsNullOrEmpty(frame.Prefix)) keys.Add(frame.Prefix + name);

        foreach (var key in keys)
        {
            if (variableTypes.ContainsKey(key)) return true;
            if (constantVariables.ContainsKey(key)) return true;
            if (constantAddressVariables.ContainsKey(key)) return true;
            if (strConstantVariables.ContainsKey(key)) return true;
            if (mutableGlobals.ContainsKey(key)) return true;
            if (globals.ContainsKey(key)) return true;
            if (variableAliases.ContainsKey(key)) return true;
            if (instanceClasses.ContainsKey(key)) return true;
            if (listLiteralParams.ContainsKey(key)) return true;
            if (dictLiteralBindings.ContainsKey(key)) return true;
            if (setLiteralBindings.ContainsKey(key)) return true;
            if (runtimeStrVars.ContainsKey(key)) return true;
            if (funcrefReturnTypes.ContainsKey(key)) return true;
            if (loopFunctionAliases.ContainsKey(key)) return true;
            if (noneValuedNames.Contains(key)) return true;
            if (declaredConstants.Contains(key)) return true;
            if (bytearrayParams.Contains(key)) return true;
            if (arraysWithVariableIndex.Contains(key)) return true;
            if (moduleSramArrays.Contains(key)) return true;
            if (boundNames.Contains(key)) return true;
        }

        // A name that denotes something other than a variable: a class, a function, an import.
        if (classNames.Contains(name) || importedAliases.ContainsKey(name)
            || aliasToOriginal.ContainsKey(name) || inlineFunctions.ContainsKey(name)
            || functionParams.ContainsKey(name) || functionReturnTypes.ContainsKey(name)
            || externFunctionMap.ContainsKey(name))
            return true;

        // Filed under a module prefix by whichever module declared it.
        foreach (var mod in modules)
        {
            string modKey = mod.Key.Replace('.', '_') + "_" + name;
            if (globals.ContainsKey(modKey) || mutableGlobals.ContainsKey(modKey)
                || variableTypes.ContainsKey(modKey) || constantVariables.ContainsKey(modKey))
                return true;
        }

        return false;
    }

    // Resolve a variable name used as an asm() constraint operand.
    // Unlike ResolveBinding, this always returns a Variable (never a Constant)
    // so that the backend can load and then store back the modified value.
    private Val ResolveAsmOperand(string name)
    {
        string localName = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : name);

        DataType type = DataType.UINT8;
        if (variableTypes.TryGetValue(localName, out var dt))
            type = dt;
        else if (variableTypes.TryGetValue(name, out var dt2))
            type = dt2;

        return new Variable(localName, type);
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

        // Fall back to the module-global / bare-name forms, mirroring how integer globals
        // resolve (ResolveBinding): a qualified `localName` like "main.S" should still find a
        // module-level string `S` registered by ScanGlobals as `currentModulePrefix + "S"`.
        string bare = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        if (strConstantVariables.TryGetValue(currentModulePrefix + bare, out var mv)) return mv;
        if (strConstantVariables.TryGetValue(bare, out var bv)) return bv;

        // `from m import banner`: the text is filed under the DEFINING module's key, which is
        // the only storage the name has (see the import loop in Generate). Reading it under
        // the bare name alone answered with no text at all, and print wrote an empty line.
        if (ImportedGlobalKey(bare) is { } importedKey
            && strConstantVariables.TryGetValue(importedKey, out var iv)) return iv;

        return null;
    }

    /// <summary>
    /// The defining module's key for a name this file imported with `from m import name`, or
    /// null when the name was not imported that way. The name has no storage of its own: it
    /// stands for the variable that lives in `m`.
    /// </summary>
    private string? ImportedGlobalKey(string name)
    {
        if (!importedAliases.TryGetValue(name, out var mod) || mod == null) return null;
        string original = aliasToOriginal.TryGetValue(name, out var orig) ? orig : name;
        return mod.Replace('.', '_') + "_" + original;
    }

    // --- Strings whose value is decided at run time (issue #145) -------------------------
    //
    // A str is a compile-time value in PyMCU: there is no string type, only an interned id
    // that the writers turn back into flash bytes. When two paths bind the SAME name to
    // different texts, no single text is right at a later read -- and folding one of them is
    // how `s = "idle"; if x: s = "running"; print(s)` printed "idle" on every path.
    //
    // What lives at run time is the id, in a 16-bit variable. It is stored at each binding
    // site (only for the names CollectMultiStrNames flagged: one binding still folds), and a
    // read that needs the TEXT dispatches over the ids the name can hold.

    /// <summary>The storage key a str binding of <paramref name="name"/> is filed under.</summary>
    private string StrBindingKey(string name)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix)) return currentInlinePrefix + name;
        if (!string.IsNullOrEmpty(currentFunction))
        {
            if (currentFunctionGlobals.Contains(name) && mutableGlobals.ContainsKey(currentModulePrefix + name))
                return currentModulePrefix + name;
            if (multiStrCandidates.ContainsKey(currentFunction + "." + name))
                return currentFunction + "." + name;
            if (mutableGlobals.ContainsKey(currentModulePrefix + name)) return currentModulePrefix + name;
            return currentFunction + "." + name;
        }
        return currentModulePrefix + name;
    }

    /// <summary>
    /// The candidate texts of a name whose string value is decided at run time, if it is one.
    /// <paramref name="materialized"/> says whether the id is actually stored at every binding
    /// site: only then can a read dispatch on it -- otherwise the slot holds whatever the RAM
    /// held, and the read has to be refused instead.
    /// </summary>
    private bool TryGetMultiStr(string name, out string key, out List<string> values, out bool materialized)
    {
        foreach (var k in MultiStrKeys(name))
        {
            // An unconditional rebind (`s = "third"` outside any branch) gives the name a
            // single value again, and that value is the right one to fold from there on.
            if (strConstantVariables.ContainsKey(k)) break;

            if (multiStrVariables.TryGetValue(k, out var vals))
            {
                key = k;
                values = vals;
                materialized = multiStrCandidates.ContainsKey(k);
                return true;
            }
        }

        key = "";
        values = new List<string>();
        materialized = false;
        return false;
    }

    /// <summary>Every key a str binding of <paramref name="name"/> could be filed under.</summary>
    private IEnumerable<string> MultiStrKeys(string name)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix)) yield return currentInlinePrefix + name;
        if (!string.IsNullOrEmpty(currentFunction)) yield return currentFunction + "." + name;
        if (!string.IsNullOrEmpty(currentModulePrefix)) yield return currentModulePrefix + name;
        yield return name;
        // `from m import state`: the slot belongs to m, and so does what is recorded about it.
        if (ImportedGlobalKey(name) is { } importedKey) yield return importedKey;
    }

    /// <summary>
    /// Records that <paramref name="key"/> holds one of <paramref name="values"/> at run time,
    /// and stops it being a compile-time string. Marking is one-way: once two paths disagree,
    /// no later single value makes the earlier read right again.
    /// </summary>
    private void MarkMultiStr(string key, IEnumerable<string?> values)
    {
        if (!multiStrVariables.TryGetValue(key, out var known))
            multiStrVariables[key] = known = new List<string>();
        foreach (var v in values)
            if (v != null && !known.Contains(v)) known.Add(v);
        strConstantVariables.Remove(key);
        if (known.Count > 0) variableTypes[key] = DataType.UINT16;
        if (mutableGlobals.ContainsKey(key)) mutableGlobals[key] = DataType.UINT16;
    }

    /// <summary>
    /// The 16-bit slot a str binding must be stored into, or null when the name is bound to
    /// one text only (then the fold is always right and nothing is stored). Emitting the store
    /// does not commit to reading it: while the name still has a single compile-time value
    /// every read folds and the store is dead, which the optimizer removes.
    /// </summary>
    private Val? MultiStrStoreTarget(string name)
    {
        string key = StrBindingKey(name);
        if (!multiStrCandidates.ContainsKey(key)) return null;
        variableTypes[key] = DataType.UINT16;
        if (mutableGlobals.ContainsKey(key)) mutableGlobals[key] = DataType.UINT16;
        return new Variable(key, DataType.UINT16);
    }

    /// <summary>
    /// The refusal for a use of a run-time-decided string that PyMCU cannot lower. It names the
    /// texts the name can hold, because the whole difficulty is that the reader of the source
    /// sees several and the compiler used to pick one of them in silence.
    /// </summary>
    /// <param name="at">
    /// The READ the compiler could not lower, which is the position the reader has to change.
    /// The assignments that gave the name its several texts are elsewhere and are already
    /// named in the message by their texts.
    /// </param>
    private Exception MultiStrUseError(string name, List<string> values, ASTNode? at = null)
    {
        string shown = values.Count switch
        {
            0 => "",
            1 => $"\"{values[0]}\"",
            _ => string.Join(" or ", values.Select(v => $"\"{v}\"")),
        };
        return UserError(
            $"'{name}' has no single compile-time value here: it is {shown} depending on the "
            + "path taken. A PyMCU string is a compile-time value, so a name that holds "
            + "different texts on different paths can only be printed (print / write_str / "
            + "println) or compared with a literal (== / !=).", at);
    }

    /// <summary>The interned id a string literal is lowered to (see VisitExpression).</summary>
    private int StringIdOf(string text)
    {
        if (text.Length == 1) return text[0];
        if (stringLiteralIds.TryGetValue(text, out int id)) return id;
        id = nextStringId++;
        stringLiteralIds[text] = id;
        stringIdToStr[id] = text;
        return id;
    }

    // Interns a compile-time string value as a null-terminated FlashData entry,
    // returning the flash array name.  Reuses an existing entry if the same string
    // was interned before.  Registers the entry in flashArrays / arraySizes /
    // arrayElemTypes so that ArrayLoadFlash works for runtime-indexed access.
    private int _flashStrCounter;
    private readonly Dictionary<string, string> _flashStrCache = new();
    private readonly List<FlashData> _pendingFlashData = new();

    private string InternStringAsFlash(string value)
    {
        if (_flashStrCache.TryGetValue(value, out var existing))
            return existing;

        var name = $"__cstr_{_flashStrCounter++}";
        var bytes = System.Text.Encoding.ASCII.GetBytes(value)
            .Select(b => (int)b)
            .Append(0) // null terminator
            .ToList();

        _pendingFlashData.Add(new FlashData(name, bytes));
        flashArrays.Add(name);
        arraySizes[name] = bytes.Count;
        arrayElemTypes[name] = DataType.UINT8;

        _flashStrCache[value] = name;
        return name;
    }

    // A field's recorded class may be a HAL dispatch facade key (e.g. "pymcu_hal_gpio_Pin")
    // that re-exports the concrete chip class ("pymcu_hal_<chip>_gpio_Pin"). Return the concrete
    // classFieldLayout key: the facade itself if it has a layout, else the UNIQUE layout key that
    // matches the facade with a chip segment inserted (same "pymcu_hal_" prefix + same tail).
    // Null when unknown or ambiguous, so the caller leaves resolution untouched.
    private string? ResolveConcreteClass(string cls)
    {
        if (string.IsNullOrEmpty(cls)) return null;
        if (classFieldLayout.ContainsKey(cls)) return cls;
        const string halPfx = "pymcu_hal_";
        if (cls.StartsWith(halPfx))
        {
            // HAL facade "..._gpio_Pin" -> the unique concrete "..._<chip>_gpio_Pin".
            string tail = "_" + cls.Substring(halPfx.Length);
            string? f = UniqueClassEndingWith(cls, tail, halPfx);
            if (f != null) return f;
        }
        // Generic facade re-export (e.g. `from facade import Foo` where facade re-exported
        // it from concrete): the class isn't itself defined, so resolve to the UNIQUE
        // concrete class sharing its final symbol (the part after the last '_').
        int us = cls.LastIndexOf('_');
        if (us <= 0) return null;
        return UniqueClassEndingWith(cls, cls.Substring(us), null);
    }

    private string? UniqueClassEndingWith(string exclude, string suffix, string? requirePrefix)
    {
        string? found = null;
        foreach (var k in classFieldLayout.Keys)
            if (k != exclude && k.EndsWith(suffix) && (requirePrefix == null || k.StartsWith(requirePrefix)))
            {
                if (found != null) return null;   // ambiguous -> give up
                found = k;
            }
        return found;
    }

    /// <summary>
    /// True when the entry file builds a CLASS INSTANCE at its module level (`led = Pin(...)`,
    /// annotated or not). Only those files need main lowered before their other functions, and
    /// asking the question this narrowly is what keeps every other program on its exact current
    /// path: lowering order advances the shared label, temporary and string-literal counters,
    /// so a hoist nobody needs renumbers a program for nothing.
    /// </summary>
    private bool EntryModuleLevelBuildsInstance(ProgramNode mainAst)
    {
        foreach (var stmt in mainAst.GlobalStatements)
        {
            Expression? value = stmt switch
            {
                AssignStmt { Target: VariableExpr } a => a.Value,
                VarDecl vd => vd.Init,
                AnnAssign an => an.Value,
                _ => null,
            };

            if (value is not CallExpr call) continue;
            string cls = call.Callee switch
            {
                VariableExpr cv => ResolveCallee(cv.Name),
                MemberAccessExpr { Object: VariableExpr mo } ma when modules.ContainsKey(mo.Name)
                    => (importedAliases.TryGetValue(mo.Name, out var real) && real != null ? real : mo.Name)
                       .Replace('.', '_') + "_" + ma.Member,
                _ => "",
            };

            if (string.IsNullOrEmpty(cls)) continue;
            if (inlineFunctions.ContainsKey(cls + "___init__")
                || overloadedFunctions.Contains(cls + "___init__")) return true;
        }

        return false;
    }

    private string ResolveCallee(string name)
    {
        int dotPos = name.IndexOf('.');
        if (dotPos != -1)
        {
            string mod = name.Substring(0, dotPos);
            string func = name.Substring(dotPos + 1);
            // `import pymcu.hal.console as c` then `c.print(1)`: print is a builtin this
            // compiler lowers itself, and the module qualifier only says where the name came
            // from. Without this the call went looking for `c_print`, a symbol nothing emits.
            int lastDot = name.LastIndexOf('.');
            string qualifier = name[..lastDot];
            string member = name[(lastDot + 1)..];
            if (intrinsicNames.Contains(member)
                && (importedAliases.ContainsKey(qualifier) || modules.ContainsKey(qualifier)))
                return member;
            return mod + "_" + func;
        }

        if (intrinsicNames.Contains(name)) return name;

        if (importedAliases.TryGetValue(name, out var modName))
        {
            var mangledMod = modName?.Replace('.', '_');
            var original = aliasToOriginal.GetValueOrDefault(name, name);
            // `from pymcu.hal.console import print as p`: the alias renames a builtin, so the
            // call must reach the builtin. Mangling it to `pymcu_hal_console_print` named a
            // function that is never emitted, and the error blamed the module rather than
            // saying the alias had been dropped.
            if (intrinsicNames.Contains(original)) return original;
            return mangledMod + "_" + original;
        }


        var prefixTry = currentModulePrefix;
        while (!string.IsNullOrEmpty(prefixTry))
        {
            var candidate = prefixTry + name;
            // Classes are looked up here too, not just functions: a class defined in an
            // imported module is registered under that module's prefix, and without this
            // `C(5)` resolved to the bare `C`, found no `C___init__`, and was reported as a
            // class with no __init__ on a file that defines one -- from the importing file AND
            // from a plain function inside the module that defines the class.
            if (inlineFunctions.ContainsKey(candidate) || functionParams.ContainsKey(candidate)
                || classFieldLayout.ContainsKey(candidate) || classDirectMethods.ContainsKey(candidate))
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

    // Returns true for top-level statements that are purely declarative and have no
    // runtime effect when visited inside a function body.  Such statements are either
    // already handled at scan time (globals, constants, class / function definitions)
    // or are no-ops at the IR level (imports, class definitions).
    //
    // Used by the synthesized-main logic to decide which GlobalStatements to include
    // in the generated `main` body.
    // Follows a re-export chain to the module that actually DEFINES `symbol`. A module
    // defines it when it contains a class, function or module-level assignment of that
    // name; otherwise, if the module re-imports the symbol, the chase continues there.
    // Bounded to keep import cycles from looping.
    private static string ResolveReExport(Dictionary<string, ProgramNode> importedModules,
        string moduleName, string symbol, int depth = 0)
    {
        if (depth > 8 || !importedModules.TryGetValue(moduleName, out var mAst))
            return moduleName;

        bool definedHere =
            mAst.Functions.Any(f => f.Name == symbol)
            || mAst.GlobalStatements.Any(s =>
                s is ClassDef cd && cd.Name == symbol
                || s is AssignStmt { Target: VariableExpr tv } && tv.Name == symbol
                || s is AnnAssign aa && aa.Target == symbol
                || s is VarDecl vd && vd.Name == symbol);
        if (definedHere) return moduleName;

        // `from Y import S` re-exports S; `from Y import S as T` re-exports T, not S.
        foreach (var mi in mAst.Imports)
            if (mi.Symbols.Contains(symbol) && !mi.Aliases.ContainsKey(symbol))
                return ResolveReExport(importedModules, mi.ModuleName, symbol, depth + 1);

        return moduleName;
    }

    private bool IsTopLevelPureDeclaration(Statement s)
    {
        // Imports and class definitions are scanned before IR generation and have no
        // runtime representation in a function body.
        if (s is ImportStmt || s is ClassDef) return true;

        // Dict/set literal bindings are compile-time lookup tables (registered during
        // the scan) -- no runtime initialization exists.
        if (s is AssignStmt { Value: DictExpr or SetExpr, Target: VariableExpr }) return true;
        if (s is VarDecl { Init: DictExpr or SetExpr }) return true;

        if (s is AnnAssign ann)
        {
            // const[T[N]] flash arrays are already injected via pendingFlashData —
            // including them again would emit a duplicate FlashData instruction.
            if (ann.Annotation.StartsWith("const[") && ann.Annotation.EndsWith("]"))
            {
                string inner = ann.Annotation.Substring(6, ann.Annotation.Length - 7);
                if (inner.Contains('[')) return true; // const[uint8[N]] — flash array
            }

            // If ScanGlobals resolved this name as a compile-time constant (e.g.
            // `MY_VAL: const[uint8] = 42` or an all-caps assignment), there is no
            // runtime initializer to emit.
            if (globals.ContainsKey(ann.Target)) return true;

            return false;
        }

        return false;
    }

    // Returns true for the build's auto-injected startup preamble statements -- the
    // `_pymcu_*` calls (clock_init / millis_init / stdout) and the print_str `pass`.
    // Module-level init is inserted AFTER this run so peripheral setup at module scope
    // sees the final clocks and stdout, but before the user's own main body.
    private static bool IsInjectedPreamble(Statement s) =>
        s is PassStmt
        || (s is ExprStmt es && es.Expr is CallExpr ce
            && ce.Callee is VariableExpr ve && ve.Name.StartsWith("_pymcu_"));
    /// <summary>
    /// Run the module-level statements of every IMPORTED module before main's own body.
    ///
    /// Only the entry file's module level was executed, so an imported module's state started
    /// at zero however it was written: `n: uint16 = 5` in cfg.py, or `c = C(5)` at module
    /// level, both arrived as zero. The storage and the writes were real -- a counter in an
    /// imported module counted 0, 1, 2 instead of 5, 6, 7 -- only the initial value was lost,
    /// which is why it compiles, runs, and is wrong by a constant.
    ///
    /// Each module's statements become a synthesized `__module_init` compiled under that
    /// module's own prefix, so its names resolve exactly as the rest of that module does, and
    /// main calls them in import order before anything else the user wrote.
    /// </summary>
    private void EmitImportedModuleInit(FunctionDef? mainFuncDef,
                                        Dictionary<string, ProgramNode> importedModules,
                                        Dictionary<ProgramNode, string> astToCanonicalPrefix)
    {
        if (mainFuncDef == null || importedModules.Count == 0) return;

        var calls = new List<Statement>();
        foreach (var kvp in importedModules)
        {
            var modAst = kvp.Value;
            if (!astToCanonicalPrefix.TryGetValue(modAst, out var prefix)) continue;

            // Only the USER's own modules. An installed distribution (the pymcu stdlib, and
            // the compat layers that provide `machine`, `board` and `busio`) is written knowing
            // that only the entry file's module level runs: several guard their top level on
            // the target chip, and running that as a function reaches code the import machinery
            // never intended to compile. Measured, not assumed: doing it for all of them turns
            // 129 tests red, `machine`'s own `mem8 = _Mem8()` first. Extending it to the
            // installed layers is a separate question with its own measurements.
            if (!projectModules.Contains(kvp.Key)) continue;

            var body = new Block();
            foreach (var st in modAst.GlobalStatements)
            {
                if (IsTopLevelPureDeclaration(st)) continue;
                if (st is VarDecl d)
                {
                    // A declaration with an initializer is the whole point here: without the
                    // rewrite the value never reaches the global it declares.
                    if (d.Init != null && mutableGlobals.ContainsKey(prefix + d.Name)
                        && !globals.ContainsKey(prefix + d.Name))
                        body.Statements.Add(new AnnAssign(d.Name, d.VarType, d.Init));
                    continue;
                }
                body.Statements.Add(st);
            }

            if (body.Statements.Count == 0) continue;

            // The synthesized function IS the module level, so every name it assigns is a
            // module global by definition. Without saying so it hits the ordinary rule and
            // reports "'c' is a module-level global; to assign it inside
            // 'counter___module_init' add a 'global c'" -- naming a function nobody wrote.
            var globalNames = new List<string>();
            foreach (var st in body.Statements)
            {
                string? target = st switch
                {
                    AnnAssign aa => aa.Target,
                    AssignStmt { Target: VariableExpr tv } => tv.Name,
                    _ => null,
                };
                if (target != null && !globalNames.Contains(target)) globalNames.Add(target);
            }
            if (globalNames.Count > 0)
                body.Statements.Insert(0, new GlobalStmt(globalNames));

            string initName = prefix + "__module_init";
            var initFn = new FunctionDef("__module_init", new List<Param>(), "None", body);
            functionsToCompile.Add(new FunctionEntry
                { Prefix = prefix, Func = initFn, SourceFile = kvp.Key + ".py", SourcePath = PathOfModule(kvp.Key) });
            functionReturnTypes[initName] = "None";
            calls.Add(new ExprStmt(new CallExpr(new VariableExpr(initName), new List<Expression>())));
        }

        if (calls.Count == 0) return;

        // After the build's auto-injected preamble, before the entry module's own init and
        // before the user's body: an import runs before the file that imports it.
        var mainBody = mainFuncDef.Body.Statements;
        int at = 0;
        while (at < mainBody.Count && IsInjectedPreamble(mainBody[at])) at++;
        for (int i = calls.Count - 1; i >= 0; i--) mainBody.Insert(at, calls[i]);
    }


    /// <summary>
    /// A function whose declared return type is a CLASS with no representable handle has no
    /// standalone form, so it must be expanded at its call sites.
    ///
    /// A single-field class travels back in a register (RFC 0001 Model B) and a slot class
    /// travels as a pointer to its SRAM slot. Everything else, which is every multi-field HAL
    /// class, has neither: the call returned a scalar, the name it was assigned to never
    /// learned its class, and the next method call on it became a call to a symbol nobody
    /// defines. `def create_out(i) -> Pin` followed by `led.value(1)` reported
    /// "call to undefined function 'led_value'", naming a function the program never wrote.
    ///
    /// Registered in inlineFunctions and REMOVED from functionsToCompile, the same shape
    /// force-inlining already takes for a single-field mutator that also returns a value.
    ///
    /// This runs after every module has been scanned rather than at registration time, because
    /// whether a class is a slot class or a factory class is decided while scanning classes,
    /// in the same pass that registers functions: at registration the answer is not known yet.
    /// </summary>
    private void ForceInlineClassReturningFactories()
    {
        var moved = new List<FunctionEntry>();
        foreach (var entry in functionsToCompile)
        {
            var rt = entry.Func.ReturnType;
            if (string.IsNullOrEmpty(rt) || rt == "None") continue;

            string? classKey = ResolveClassKey(rt, entry.Prefix ?? "");
            if (classKey == null) continue;
            if (slotClasses.Contains(classKey) || zcaFactoryClasses.ContainsKey(classKey)) continue;

            string fullName = (entry.Prefix ?? "") + entry.Func.Name;
            if (inlineFunctions.ContainsKey(fullName)) continue;
            inlineFunctions[fullName] = entry.Func;
            moved.Add(entry);
        }
        foreach (var m in moved) functionsToCompile.Remove(m);
    }

    /// <summary>
    /// The class a declared type name refers to, or null when it names no class. Tries the name
    /// as written, then under the defining module's prefix, then any known class whose key ends
    /// in it: a return type is spelled the way the user imported it, not the way it is mangled.
    /// </summary>
    private string? ResolveClassKey(string typeName, string prefix)
    {
        if (classFieldLayout.ContainsKey(typeName) || classDirectMethods.ContainsKey(typeName))
            return typeName;

        string prefixed = prefix + typeName;
        if (classFieldLayout.ContainsKey(prefixed) || classDirectMethods.ContainsKey(prefixed))
            return prefixed;

        string suffix = "_" + typeName;
        foreach (var k in classDirectMethods.Keys)
            if (k.EndsWith(suffix, StringComparison.Ordinal)) return k;
        foreach (var k in classFieldLayout.Keys)
            if (k.EndsWith(suffix, StringComparison.Ordinal)) return k;
        return null;
    }

}
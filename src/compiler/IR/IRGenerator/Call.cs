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
using PyMCU.IR;

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    private Val VisitCall(CallExpr expr)
    {
        if (TryEmitSuperMethodCall(expr) is { } superResult) return superResult;

        string callee = "";
        if (expr.Callee is VariableExpr varE)
        {
            callee = ResolveCallee(varE.Name);
        }
        else if (expr.Callee is MemberAccessExpr memC)
        {
            bool resolvedAsModule = false;

            // RFC 0001 Model B (Class[N]): arr[i].method() dispatch.
            if (TryEmitInstanceArrayMethodCall(expr, memC) is { } iaResult) return iaResult;

            // self.method(args) inside an outlined method: call the sibling outlined method.
            if (TryEmitSelfOutlinedMethodCall(expr, memC) is { } selfResult) return selfResult;

            if (memC.Object is VariableExpr ve)
            {
                if (modules.ContainsKey(ve.Name))
                {
                    // Mangle with the real module name, not the alias: `import time as t`
                    // registers modules["t"] but compiles functions as time_sleep_ms.
                    string realMod = importedAliases.TryGetValue(ve.Name, out var rm) && rm != null ? rm : ve.Name;
                    string mangledMod = realMod.Replace('.', '_');
                    callee = mangledMod + "_" + memC.Member;
                    resolvedAsModule = true;
                }
                else if (classNames.Contains(ve.Name))
                {
                    callee = currentModulePrefix + ve.Name + "_" + memC.Member;
                    resolvedAsModule = true;
                }
                else if (ve.Name == "int")
                {
                    callee = "int_" + memC.Member;
                    resolvedAsModule = true;
                }
            }

            if (!resolvedAsModule)
            {
                Val objVal = VisitExpression(memC.Object);
                if (objVal is Variable vObj)
                {
                    // list[T] method dispatch
                    if (listVarElemTypes.ContainsKey(vObj.Name))
                    {
                        switch (memC.Member)
                        {
                            case "append" when expr.Args.Count == 1:
                                return EmitListAppend(vObj, expr.Args[0]);
                            default:
                                throw UserError($"list.{memC.Member}(): method not supported");
                        }
                    }

                    if (instanceClasses.TryGetValue(vObj.Name, out string clsC))
                    {
                        // Walk MRO: find the class that actually defines the method so that
                        // inherited non-inline methods (e.g. DHTBase._read_byte called on a
                        // DHT11 instance) resolve to the correct label instead of the
                        // non-existent <ConcreteClass>_<method> symbol.
                        string definingClass = ResolveMROMethod(clsC!, memC.Member);
                        callee = definingClass + "_" + memC.Member;

                        // RFC 0001 Model A: an @outline method is a shared subroutine, not
                        // inlined. Pass the instance's runtime field values as leading args
                        // (self_<field>), then the user args, and emit a real Call. One body,
                        // N call sites -- no per-instance bloat.
                        if (outlinedMethods.Contains(callee))
                        {
                            var oArgs = new List<Val>();
                            string instName = objVal is Variable iv ? iv.Name : "";
                            if (slotMethods.Contains(callee)
                                && slotInstances.TryGetValue(instName, out var slotName))
                            {
                                // Model B (SRAM slot): pass the slot base address as `self`;
                                // the body reads fields via BytearrayLoad at offsets.
                                oArgs.Add(new ArrayBase(slotName));
                            }
                            else
                            {
                                // Model B handle instance (from a factory): the instance IS its
                                // single packed field, so pass the variable itself as the field arg.
                                // Model A direct instance: read each field from <inst>_<field>.
                                bool isHandle = !string.IsNullOrEmpty(instName)
                                                && factoryHandleInstances.Contains(instName);
                                foreach (var (fld, _, _) in outlineFieldLayout[callee])
                                    oArgs.Add(isHandle
                                        ? VisitExpression(memC.Object)
                                        : VisitExpression(new MemberAccessExpr(memC.Object, fld)));
                            }
                            foreach (var a in expr.Args)
                            {
                                Val av = VisitExpression(a);
                                if (av is FloatConstant fc) av = new Constant((int)Math.Round(fc.Value));
                                oArgs.Add(av);
                            }

                            bool rVoid = !functionReturnTypes.TryGetValue(callee, out var rt)
                                         || rt == "void" || rt == "None";
                            if (rVoid)
                            {
                                Emit(new Call(callee, oArgs, new NoneVal()));
                                return new NoneVal();
                            }

                            Temporary oDst = MakeTemp(DataTypeExtensions.StringToDataType(
                                functionReturnTypes[callee]));
                            Emit(new Call(callee, oArgs, oDst));
                            return oDst;
                        }

                        // ZCA force-inline: if the resolved callee is a non-inline instance
                        // method, add its AST to inlineFunctions on-demand so the standard
                        // inline expansion runs.  ZCA field aliasing requires inline expansion;
                        // without it, `self._field` accesses inside the method would reference
                        // the wrong stack frame.
                        if (!inlineFunctions.ContainsKey(callee)
                            && instanceMethodDefs.TryGetValue(callee, out var implDef))
                        {
                            inlineFunctions[callee] = implDef;
                        }

                        // Phase 3 gate: emit VirtualCall only when static dispatch cannot be
                        // proven safe.  In the current ZCA model instanceClasses always holds
                        // the exact concrete type (Rule 2), so IsVirtualDispatch always returns
                        // false and we always take the direct-call path above.
                        // (IsVirtualDispatch kept here for future polymorphic-variable support.)
                    }
                    else
                    {
                        // A list mutation method on something that is not a typed list:
                        // the usual cause is an untyped `[]`, which has no runtime list to
                        // mutate (it would emit a call to a nonexistent <var>_append symbol
                        // and fail at link). Surface it clearly. These method names are never
                        // valid on a non-list value, so this never flags a real symbol.
                        if (memC.Member is "append" or "pop" or "insert" or "remove" or "extend" or "clear")
                            throw new NameError(
                                $"'.{memC.Member}()' requires a typed list; an untyped '[]' has no " +
                                "runtime list. Declare it like `x: list[uint8] = []`, or use a " +
                                "fixed-size array `x: uint8[N]`.",
                                expr.Line > 0 ? expr.Line : lastLine, 1);
                        callee = vObj.Name + "_" + memC.Member;
                    }
                }
                else if (objVal is MemoryAddress addr)
                {
                    callee = $"MemoryAddress_{addr.Address}_{memC.Member}";
                }
                else
                {
                    // Reached when the receiver of a value-returning method is itself a
                    // ZCA field (e.g. `self.pin.pulse_in()` — a Pin stored in a field of
                    // another ZCA). The chained access resolves to a temporary, which the
                    // current model can't dispatch through. Name the member to make the
                    // limitation actionable instead of opaque.
                    throw UserError(
                        $"calling .{memC.Member}() on a nested member access is not yet supported " +
                        "(a ZCA field that is itself a ZCA, like self.pin.pulse_in()); " +
                        "void methods on such a field work, but value-returning ones do not yet");
                }
            }
        }
        else if (expr.Callee is IndexExpr { Target: VariableExpr idxArrVe0 } idxCallee0)
        {
            return EmitCallableArrayCall(expr, idxCallee0, idxArrVe0);
        }
        else
        {
            throw UserError("Indirect calls not yet supported");
        }

        // Lambda call: a variable bound to a lambda expands the lambda body in place.
        if (TryEmitLambdaCall(expr, callee) is { } lambdaResult) return lambdaResult;

        // Indirect call via FUNCREF-typed variable (function pointer via funcref() intrinsic).
        // After the lambda check so lambdas take priority; before all intrinsics/inline expansion.
        if (TryEmitFuncrefVariableCall(expr) is { } funcrefResult) return funcrefResult;

        // Indirect call via Callable[N] array: _tasks[i]()
        // Note: this path is unreachable now since the IndexExpr case above handles
        // it and returns early. Kept here as dead code guard in case of future refactoring.

        if (inlineFunctions.ContainsKey(callee + "___init__") || overloadedFunctions.Contains(callee + "___init__"))
        {
            callee += "___init__";
        }

        callee = ResolveOverloadedCallee(callee, expr);

        {
            bool isSleepMs = callee == "sleep_ms" || callee == "time_sleep_ms" || callee == "pymcu_time_sleep_ms" || callee == "delay_ms" || callee == "time_delay_ms" || callee == "pymcu_time_delay_ms";
            bool isSleepUs = callee == "sleep_us" || callee == "time_sleep_us" || callee == "pymcu_time_sleep_us" || callee == "delay_us" || callee == "time_delay_us" || callee == "pymcu_time_delay_us";
            if (isSleepMs || isSleepUs)
            {
                string targetSuffix = isSleepMs ? "delay_ms" : "delay_us";
                string candidate = "pymcu_time_" + targetSuffix;
                if (!inlineFunctions.ContainsKey(candidate))
                {
                    candidate = "";
                    foreach (var fnName in inlineFunctions.Keys)
                    {
                        if (fnName.EndsWith(targetSuffix))
                        {
                            candidate = fnName;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(candidate)) callee = candidate;
            }
        }

        if (callee == "len") return EmitLenBuiltin(expr);
        if (callee == "int_from_bytes") return EmitIntFromBytesBuiltin(expr);
        if (callee == "abs") return EmitAbsBuiltin(expr);
        if (callee == "min") return EmitMinBuiltin(expr);
        if (callee == "max") return EmitMaxBuiltin(expr);
        if (callee == "ord") return EmitOrdBuiltin(expr);
        if (callee == "chr") return EmitChrBuiltin(expr);

        if (callee == "sum") return EmitSumBuiltin(expr);

        if (callee == "any") return EmitAnyBuiltin(expr);
        if (callee == "all") return EmitAllBuiltin(expr);

        if (callee == "hex") return EmitHexBuiltin(expr);
        if (callee == "bin") return EmitBinBuiltin(expr);
        if (callee == "str") return EmitStrBuiltin(expr);
        if (callee == "pow") return EmitPowBuiltin(expr);

        if (callee == "divmod") return EmitDivmodBuiltin(expr);
        if (CastTypes.ContainsKey(callee)) return EmitNumericCastBuiltin(expr, callee);
        if (callee == "bitcast") return EmitBitcastBuiltin(expr);
        if (callee == "gc_alloc") return EmitGcAllocBuiltin(expr);
        if (callee == "asm") return EmitAsmBuiltin(expr);

        if (callee == "print") return EmitPrintBuiltin(expr);

        if (callee == "ptr" && intrinsicNames.Contains("ptr"))
        {
            if (expr.Args.Count != 1) throw UserError("ptr() expects exactly one argument");
            // Evaluate the argument as a compile-time ADDRESS: a literal, a const, or a
            // register/MMIO base combined with constant +/- offsets, e.g. ptr(PORTB + 6).
            // A bare register contributes its address here (not its dereferenced value).
            if (TryEvalConstAddress(expr.Args[0]) is int addr)
                return new MemoryAddress(addr, DataType.UINT8);

            // A string/f-string is not an address (it would otherwise be accepted as its
            // flash string-id, silently aiming the pointer at a garbage address).
            if (expr.Args[0] is StringLiteral or FStringExpr)
                throw UserError("ptr() argument must be a numeric address, not a string");

            // Runtime address, e.g. ptr(BASE + x) with a non-constant offset. Materialize
            // the 16-bit address into a temp and mark it a runtime pointer; a subsequent
            // `.value` read/write lowers to Load/StoreIndirect through the held address.
            Val addrVal = VisitExpression(expr.Args[0]);
            Temporary ptrTmp = MakeTemp(DataType.UINT16);
            Emit(new Copy(addrVal, ptrTmp));
            runtimePtrVars[ptrTmp.Name] = DataType.UINT8;
            return ptrTmp;
        }

        if (callee == "ptr" && !intrinsicNames.Contains("ptr"))
        {
            Console.Error.WriteLine(
                "[Warning] 'ptr' is not recognized as an intrinsic. Did you forget to import from pymcu.types?");
            return new Constant(0);
        }

        if (callee == "const" && intrinsicNames.Contains("const"))
        {
            if (expr.Args.Count != 1) throw UserError("const() expects exactly one argument");
            Val argVal = VisitExpression(expr.Args[0]);
            if (argVal is Constant) return argVal;
            throw UserError("const() argument must be a compile-time constant expression");
        }

        if ((callee == "funcref" || callee == "pymcu_types_funcref") && intrinsicNames.Contains("funcref"))
            return EmitFuncrefIntrinsic(expr);

        if (callee == "_set_irq_zca_arg" && intrinsicNames.Contains("_set_irq_zca_arg"))
            return EmitSetIrqZcaArgIntrinsic(expr);

        if (callee == "compile_isr" && intrinsicNames.Contains("compile_isr"))
            return EmitCompileIsrIntrinsic(expr);

        if (externFunctionMap.TryGetValue(callee, out string cSym))
            return EmitExternCall(expr, callee, cSym);

        if (inlineFunctions.TryGetValue(callee, out var func)) return EmitInlineFunctionCall(expr, callee, func);

        return EmitRegularFunctionCall(expr, callee);
    }

    // Resolve keyword arguments in a call to a regular (non-@inline) function into a flat
    // positional argument list (Python-style binding). Positional args fill leading params;
    // keyword args bind by parameter name; any gap before the last supplied arg is filled
    // from the parameter default. Returns the original list unchanged when there are no
    // keyword args. Reports unknown/duplicate/missing keyword bindings as clean user errors
    // (the inline path already does this; previously a kwarg to a real subroutine reached
    // VisitExpression and surfaced the cryptic "Unknown Expression type: KeywordArgExpr").
    private List<Expression> ReorderCallArgs(List<Expression> args, string callee)
    {
        if (!args.Any(a => a is KeywordArgExpr)) return args;

        // Look up the callee's parameter names, trying the module-mangled form too
        // (a dotted "mod.fn" is stored as "mod_fn").
        List<string>? paramNames = null;
        if (!functionParams.TryGetValue(callee, out paramNames))
        {
            int dot = callee.IndexOf('.');
            if (dot != -1)
                functionParams.TryGetValue(
                    callee.Substring(0, dot) + "_" + callee.Substring(dot + 1), out paramNames);
        }
        string shown = callee.Contains('.') ? callee[(callee.LastIndexOf('.') + 1)..] : callee;
        if (paramNames == null)
            throw UserError($"keyword arguments are not supported in call to '{shown}'");

        var positional = new List<Expression>();
        var byName = new Dictionary<string, Expression>();
        foreach (var a in args)
        {
            if (a is KeywordArgExpr kw)
            {
                if (!paramNames.Contains(kw.Key))
                    throw UserError($"unknown keyword argument '{kw.Key}' in call to '{shown}'");
                if (!byName.TryAdd(kw.Key, kw.Value))
                    throw UserError($"keyword argument '{kw.Key}' repeated in call to '{shown}'");
            }
            else positional.Add(a);
        }

        // Highest parameter index that receives an explicit value.
        int lastIdx = positional.Count - 1;
        for (int i = 0; i < paramNames.Count; i++)
            if (byName.ContainsKey(paramNames[i])) lastIdx = Math.Max(lastIdx, i);

        functionParamDefaults.TryGetValue(callee, out var defaults);
        var ordered = new List<Expression>();
        for (int i = 0; i <= lastIdx; i++)
        {
            if (i < positional.Count)
            {
                if (byName.ContainsKey(paramNames[i]))
                    throw UserError($"multiple values for argument '{paramNames[i]}' in call to '{shown}'");
                ordered.Add(positional[i]);
            }
            else if (byName.TryGetValue(paramNames[i], out var kwVal))
            {
                ordered.Add(kwVal);
            }
            else
            {
                var def = defaults != null && i < defaults.Count ? defaults[i] : null;
                if (def == null)
                    throw UserError($"missing argument '{paramNames[i]}' in call to '{shown}'");
                ordered.Add(def);
            }
        }
        return ordered;
    }

    // Emit a call to a known non-@inline function (a real subroutine): build the arg
    // list (flash-string-by-ref, array base addresses), mangle module-dotted names,
    // fill defaulted params and copy each arg into the callee's param slot, then Call.
    private Val EmitRegularFunctionCall(CallExpr expr, string callee)
    {
        // A call that resolved to no known function (not inline, extern, a builtin or an
        // intrinsic — those return earlier) is a typo or a missing import. Report it now
        // instead of emitting a Call to an undefined symbol that fails much later with a
        // cryptic linker "undefined reference". `__`-prefixed runtime helpers are exempt.
        // Gated to real chip targets (skip PIO, whose mnemonics like pull/push compile as
        // calls resolved by the PIO backend, and the empty-config compiles used in tests).
        bool checkUndefined = deviceConfig.Arch.Length > 0 && !deviceConfig.Arch.Contains("pio");
        if (checkUndefined
            && !functionParams.ContainsKey(callee)
            && !functionReturnTypes.ContainsKey(callee)
            && !inlineFunctions.ContainsKey(callee)
            && !externFunctionMap.ContainsKey(callee)
            && !callee.StartsWith("__"))
        {
            string shown = callee.Contains('.') ? callee[(callee.LastIndexOf('.') + 1)..] : callee;
            throw UserError($"call to undefined function '{shown}' (typo, or a missing import?)");
        }

        bool calleeIsKnownFunc = functionParams.ContainsKey(callee);
        // Resolve any keyword arguments into positional order before evaluating them.
        var callArgs = ReorderCallArgs(expr.Args, callee);
        var argValuesL = new List<Val>();
        foreach (var arg in callArgs)
        {
            // const[str] argument to a non-@inline function: intern the string and pass its
            // flash address by reference (FlashStrAddr). The callee walks it with FlashLoadPtr,
            // so the byte-loop lives in a single shared subroutine instead of being inlined at
            // every call site. (Inline callees bind the literal via strConstantVariables and
            // never reach this path.)
            if (calleeIsKnownFunc)
            {
                string? argStr = arg switch
                {
                    StringLiteral sl => sl.Value,
                    VariableExpr ve => ResolveStrConstant((!string.IsNullOrEmpty(currentInlinePrefix)
                        ? currentInlinePrefix
                        : currentFunction + ".") + ve.Name),
                    _ => null
                };
                if (argStr != null)
                {
                    argValuesL.Add(new FlashStrAddr(InternStringAsFlash(argStr)));
                    continue;
                }
            }

            // If the argument is a bare variable name that refers to a local array,
            // pass its base address rather than trying to load it as a scalar.
            if (arg is VariableExpr argVe)
            {
                string argQualified = (!string.IsNullOrEmpty(currentInlinePrefix)
                    ? currentInlinePrefix
                    : currentFunction + ".") + argVe.Name;
                if (!arraySizes.ContainsKey(argQualified))
                {
                    // Fall back to unqualified / module-level name
                    string altQ = currentModulePrefix + argVe.Name;
                    if (arraySizes.ContainsKey(altQ)) argQualified = altQ;
                    else if (arraySizes.ContainsKey(argVe.Name)) argQualified = argVe.Name;
                }
                if (arraySizes.ContainsKey(argQualified))
                {
                    argValuesL.Add(new ArrayBase(argQualified));
                    continue;
                }
            }
            argValuesL.Add(VisitExpression(arg));
        }

        int dotPos2 = callee.IndexOf('.');
        if (dotPos2 != -1)
        {
            string mod = callee.Substring(0, dotPos2);
            if (modules.ContainsKey(mod))
            {
                callee = callee.Substring(0, dotPos2) + "_" + callee.Substring(dotPos2 + 1);
            }
        }

        if (functionParams.TryGetValue(callee, out var paramNames))
        {
            if (callArgs.Count > paramNames.Count)
                throw UserError(
                    $"Function '{callee}' expects {paramNames.Count} arguments, but {callArgs.Count} were provided");
            // Fill omitted trailing arguments from the parameter defaults (Python-style),
            // so defaults work for real subroutines, not only @inline functions.
            if (argValuesL.Count < paramNames.Count)
            {
                functionParamDefaults.TryGetValue(callee, out var defaults);
                for (int i = argValuesL.Count; i < paramNames.Count; ++i)
                {
                    var def = defaults != null && i < defaults.Count ? defaults[i] : null;
                    if (def is null)
                        throw UserError(
                            $"Function '{callee}' expects {paramNames.Count} arguments, but {callArgs.Count} were provided");
                    argValuesL.Add(VisitExpression(def));
                }
            }
            var paramTypes = functionParamTypes.TryGetValue(callee, out var pt) ? pt : new List<DataType>();
            for (int i = 0; i < argValuesL.Count; ++i)
            {
                string paramVarName = callee + "." + paramNames[i];
                DataType ptype = i < paramTypes.Count ? paramTypes[i] : DataType.UINT8;
                Val argVal = argValuesL[i];

                // A flash-string-by-reference argument is a 16-bit flash address, regardless
                // of how the const[str] param's nominal type folds.
                if (argVal is FlashStrAddr) ptype = DataType.UINT16;

                // Auto-wrap: if a Callable (FUNCREF) parameter receives a bare function name
                // (which resolves as a UINT8 Variable rather than a FunctionRef), create the
                // FunctionRef so DCE treats the function as reachable and the backend emits
                // the correct lo8/hi8 address load rather than a SRAM load.
                if (ptype == DataType.FUNCREF && argVal is Variable argVar && argVar.Type != DataType.FUNCREF)
                {
                    string rawName = argVar.Name.Contains('.')
                        ? argVar.Name.Substring(argVar.Name.LastIndexOf('.') + 1)
                        : argVar.Name;
                    string resolvedFn = ResolveCallee(rawName);
                    if (functionParams.ContainsKey(resolvedFn) || functionReturnTypes.ContainsKey(resolvedFn))
                    {
                        argVal = new FunctionRef(resolvedFn);
                        // Update the arg list so the CALL instruction also passes the FunctionRef.
                        // Without this the backend would emit a 1-byte UINT8 load into R24 and
                        // leave R25 (the hi byte of the word address) undefined.
                        argValuesL[i] = argVal;
                    }
                }

                Emit(new Copy(argVal, new Variable(paramVarName, ptype)));
            }
        }

        bool returnsVoidEnd = functionReturnTypes.TryGetValue(callee, out string? rType) && (rType == "void" || rType == "None");

        if (returnsVoidEnd)
        {
            Emit(new Call(callee, argValuesL, new NoneVal()));
            return new NoneVal();
        }

        Temporary dstC = MakeTemp();
        Emit(new Call(callee, argValuesL, dstC));
        return dstC;
    }

    // Expand a known @inline function/ZCA method call in place: bind positional,
    // keyword and defaulted args into a fresh inline frame, alias self for instance
    // methods, run the body, and yield the (possibly tuple) result. The big ZCA
    // call-expansion core; always returns (result / constructed instance / None).
    private Val EmitInlineFunctionCall(CallExpr expr, string callee, FunctionDef? func)
    {
        // Recursion guard: if this callee is already being expanded further up the
        // chain, inlining it again would never terminate and overflow the compiler
        // stack (SIGSEGV). PyMCU has no call frame for inlined/ZCA methods, so this
        // recursion is unsupported — report it clearly instead of crashing.
        if (!activeInlineExpansions.Add(callee))
        {
            string rn = func?.Name ?? callee;
            throw new RecursionError(
                $"function '{rn}' is recursive; PyMCU has no call frame for inlined " +
                "or ZCA methods, so recursion is not supported — rewrite it as a loop",
                currentStmtLine > 0 ? currentStmtLine : 1, 1);
        }

        // @warning("..."): print the author-supplied note (once per function)
        // when a call to this function is expanded. Informational only -- it
        // does NOT abort compilation, so flagged-but-usable features (soft-float,
        // reduced bare-metal behaviour) still build.
        if (func != null && !string.IsNullOrEmpty(func.WarningMessage) && warningNoticed.Add(func.Name))
        {
            Console.Error.WriteLine($"[pymcuc] warning: {func.WarningMessage}");
        }

        var exitLabel = MakeLabel();
        var newDepth = inlineDepth + 1;
        var newPrefix = $"inline{newDepth}.{func?.Name}.";

        Temporary? result = null;
        var tupleResultNames = new List<string>();

        if (pendingTupleCount > 0)
        {
            string bBase = string.IsNullOrEmpty(currentFunction) ? "main" : currentFunction;
            for (int k = 0; k < pendingTupleCount; ++k)
            {
                tupleResultNames.Add($"{bBase}.iret_{newDepth}_{k}");
            }
        }
        else if (func.ReturnType != "void" && func.ReturnType != "None")
        {
            result = MakeTemp(DataTypeExtensions.StringToDataType(func.ReturnType));
        }

        var argValues = new List<Val>();

        bool isConstructor = callee.EndsWith("___init__") || callee.Contains("___init____");
        int paramOffset = 0;

        if (!isConstructor)
        {
            if (expr.Callee is MemberAccessExpr mem2)
            {
                Val objVal = VisitExpression(mem2.Object);
                if (objVal is Variable v2 && instanceClasses.ContainsKey(v2.Name))
                {
                    string selfName = newPrefix + "self";
                    variableAliases[selfName] = v2.Name;
                    instanceClasses[selfName] = instanceClasses[v2.Name];
                    paramOffset = 1;
                }
            }
        }
        else paramOffset = 1;

        var kwArgValues = new Dictionary<string, Val>();
        var rawKwStrArgs = new Dictionary<string, string?>();
        var rawStrArgs = new List<StringLiteral?>();
        var rawListArgs = new List<ListExpr?>();

        foreach (var arg in expr.Args)
        {
            if (arg is KeywordArgExpr kw)
            {
                string savedOuterPct = pendingConstructorTarget;
                pendingConstructorTarget = "";
                kwArgValues[kw.Key] = VisitExpression(kw.Value);
                if (kw.Value is StringLiteral s) rawKwStrArgs[kw.Key] = s.Value;
                // Always restore: inner ctor targets (anonymous __cN) must not
                // overwrite the outer assignment target (e.g. "main.spi").
                pendingConstructorTarget = savedOuterPct;
            }
            else
            {
                rawStrArgs.Add(arg as StringLiteral);
                // A bytes/list literal (b"Hi", [1,2,3]) or a tuple literal
                // ((r,g,b)) is a fixed sequence: normalise both to a ListExpr
                // and bind it to the inline parameter so the callee can consume
                // it via `for x in param` (unrolled) or `param[const]` indexing.
                ListExpr? seqLit = arg as ListExpr
                    ?? (arg is TupleExpr tple ? new ListExpr(tple.Elements) : null);
                rawListArgs.Add(seqLit);
                if (seqLit != null)
                {
                    // Visiting the literal as an expression is unsupported; the raw
                    // AST is bound below. Push a placeholder to keep argValues
                    // index-aligned with the parameter list.
                    argValues.Add(new NoneVal());
                }
                else
                {
                    string savedOuterPct = pendingConstructorTarget;
                    pendingConstructorTarget = "";
                    argValues.Add(VisitExpression(arg));
                    // Always restore: same reason as kwarg case above.
                    pendingConstructorTarget = savedOuterPct;
                }
            }
        }

        bool isForceInlined = func != null && !func.IsInline;
        // @inline expansions are bracketed with a *tagged* marker so the
        // generic parameterized-outlining pass (Optimizer) can collapse
        // repeated copies that differ only in folded constants. The tag is
        // stripped before IR is handed to any backend, so codegen is
        // unaffected and the non-@inline markers above keep their meaning.
        bool isInlineMethod = func != null && func.IsInline;
        if (isForceInlined)
            Emit(new InlineExpansionMarker(callee, false));
        else if (isInlineMethod)
            Emit(new InlineExpansionMarker(Optimizer.InlineMarkerTag + callee, false));

        inlineDepth++;
        string savedPrefix = currentInlinePrefix;
        currentInlinePrefix = newPrefix;

        var savedModulePrefix = currentModulePrefix;
        // Resolve the body's calls in the module where the function was DEFINED,
        // not where its (possibly re-exported) callee name lives. A facade like
        // pymcu.hal.tone re-exports tone_start from pymcu.hal.avr.tone; deriving
        // the prefix from the re-exported callee would look for tone_start's
        // internal helper (_tone_ocr) in the wrong module and emit an unresolved
        // call. functionModulePrefix preserves the defining module across re-export.
        if (functionModulePrefix.TryGetValue(callee, out var definingPrefix))
        {
            currentModulePrefix = definingPrefix;
        }
        else if (func.Name.Length < callee.Length)
        {
            currentModulePrefix = callee.Substring(0, callee.Length - func.Name.Length);
        }

        if (methodInstanceTypes.TryGetValue(callee, out var mit))
        {
            instanceClasses[newPrefix + "self"] = mit;
        }

        string? ctorSubexprSynth = null;
        if (isConstructor)
        {
            var selfName = newPrefix + "self";
            var initPos = callee.IndexOf("___init____", StringComparison.Ordinal);
            var classPrefix =
                initPos != -1 ? callee[..initPos] : callee[..^9];
            string target;
            if (!string.IsNullOrEmpty(pendingConstructorTarget))
            {
                target = pendingConstructorTarget;
                pendingConstructorTarget = "";
            }
            else
            {
                string bBase = string.IsNullOrEmpty(currentFunction) ? "main" : currentFunction;
                target = bBase + ".__c" + (++ctorAnonId);
                ctorSubexprSynth = target;
            }

            variableAliases[selfName] = target;
            instanceClasses[selfName] = classPrefix;
            instanceClasses[target] = classPrefix;
            virtualInstances.Add(target);
        }

        inlineStack.Add(new InlineContext
            { ExitLabel = exitLabel, ResultTemp = result, ResultVars = tupleResultNames, CalleeName = callee });

        var boundParams = new HashSet<int>();

        for (int i = 0; i < argValues.Count; ++i)
        {
            int paramIdx = i + paramOffset;
            if (paramIdx >= func.Params.Count) break;
            string paramName = currentInlinePrefix + func.Params[paramIdx].Name;
            boundParams.Add(paramIdx);

            if (i < rawListArgs.Count && rawListArgs[i] != null)
            {
                // Bytes/list/tuple literal bound to this parameter: record the raw AST
                // so `for x in param` unrolls it and `param[const]` folds. Clear any
                // stale scalar bindings.
                listLiteralParams[paramName] = rawListArgs[i]!;
                constantVariables.Remove(paramName);
                strConstantVariables.Remove(paramName);
                variableAliases.Remove(paramName);
                continue;
            }
            listLiteralParams.Remove(paramName);

            if (argValues[i] is FloatConstant fcArg)
            {
                var fcPType = func.Params[paramIdx].Type;
                bool fcIsInt = fcPType is "uint8" or "uint16" or "uint32" or "int8" or "int16" or "int32" or "int";
                if (fcIsInt)
                {
                    constantVariables[paramName] = (int)fcArg.Value;
                    floatConstantVariables.Remove(paramName);
                }
                else
                {
                    floatConstantVariables[paramName] = fcArg.Value;
                    constantVariables.Remove(paramName);
                }
                strConstantVariables.Remove(paramName);
                variableAliases.Remove(paramName);
                variableTypes[paramName] = DataTypeExtensions.StringToDataType(fcPType);
                continue;
            }

            if (argValues[i] is Variable vArg)
            {
                if (func.Params[paramIdx].Type == "const[str]")
                {
                    string? strVal = ResolveStrConstant(vArg.Name);
                    if (strVal != null)
                    {
                        strConstantVariables[paramName] = strVal;
                        constantVariables.Remove(paramName);
                        variableAliases.Remove(paramName);
                        continue;
                    }
                }

                if (floatConstantVariables.TryGetValue(vArg.Name, out double fv))
                {
                    var fvPType = func.Params[paramIdx].Type;
                    bool fvIsInt = fvPType is "uint8" or "uint16" or "uint32" or "int8" or "int16" or "int32" or "int";
                    if (fvIsInt)
                    {
                        constantVariables[paramName] = (int)fv;
                        floatConstantVariables.Remove(paramName);
                    }
                    else
                    {
                        floatConstantVariables[paramName] = fv;
                        constantVariables.Remove(paramName);
                    }
                    strConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                    variableTypes[paramName] = DataTypeExtensions.StringToDataType(fvPType);
                    continue;
                }

                variableAliases[paramName] = vArg.Name;
                constantVariables.Remove(paramName);
                strConstantVariables.Remove(paramName);
                variableTypes[paramName] = DataTypeExtensions.StringToDataType(func.Params[paramIdx].Type);
                continue;
            }

            if (argValues[i] is Temporary tArg)
            {
                // A Temporary can carry a compile-time string or numeric constant
                // when it is the result of a DCE'd @inline function (e.g.,
                // _arduino_pin_name(13) → "PB5").  Without this block the value
                // would fall through to the runtime Copy, losing the constant.
                string? tStr = ResolveStrConstant(tArg.Name);
                if (tStr == null && constantVariables.TryGetValue(tArg.Name, out int tId))
                    stringIdToStr.TryGetValue(tId, out tStr);
                if (tStr != null)
                {
                    strConstantVariables[paramName] = tStr;
                    constantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                    continue;
                }
                if (constantVariables.TryGetValue(tArg.Name, out int tNum))
                {
                    constantVariables[paramName] = tNum;
                    strConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                    continue;
                }
                // Non-constant Temporary: fall through to runtime Copy
            }

            if (IsConstType(func.Params[paramIdx].Type))
            {
                if (func.Params[paramIdx].Type == "const[str]")
                {
                    // The inline-prefix param key can be reused across call sites (two
                    // Pin.__init__ overloads at the same depth share inlineN.__init__.pin_id),
                    // so a prior site may have left a stale numeric/alias binding here.
                    // Clear the complementary maps so only this string value is live --
                    // otherwise a later `self._name = pin_id` reads the stale int first.
                    constantVariables.Remove(paramName);
                    floatConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);

                    if (i < rawStrArgs.Count && rawStrArgs[i] != null)
                    {
                        strConstantVariables[paramName] = rawStrArgs[i]!.Value;
                        continue;
                    }

                    if (argValues[i] is Variable vArg2 && ResolveStrConstant(vArg2.Name) is string sv2)
                    {
                        strConstantVariables[paramName] = sv2;
                        continue;
                    }

                    if (argValues[i] is Constant cArg && stringIdToStr.TryGetValue(cArg.Value, out string sv3))
                    {
                        strConstantVariables[paramName] = sv3;
                        continue;
                    }

                    throw UserError(
                        $"Parameter '{func.Params[paramIdx].Name}' is declared as const[str] and requires a compile-time string constant value");
                }

                if (!(argValues[i] is Constant cArg2))
                    throw UserError(
                        $"Parameter '{func.Params[paramIdx].Name}' is declared as const and requires a compile-time constant value");
                constantVariables[paramName] = cArg2.Value;
                strConstantVariables.Remove(paramName);
                floatConstantVariables.Remove(paramName);
                variableAliases.Remove(paramName);
                continue;
            }
            if (argValues[i] is Constant cArg3)
            {
                constantVariables[paramName] = cArg3.Value;
                strConstantVariables.Remove(paramName);
                floatConstantVariables.Remove(paramName);
                variableAliases.Remove(paramName);
                continue;
            }
            if (argValues[i] is MemoryAddress mArg)
            {
                constantAddressVariables[paramName] = mArg.Address;
                constantAddressVariables.Remove(paramName + "_type");
                constantVariables.Remove(paramName);
                strConstantVariables.Remove(paramName);
                variableAliases.Remove(paramName);
                continue;
            }

            constantVariables.Remove(paramName);
            strConstantVariables.Remove(paramName);
            variableAliases.Remove(paramName);
            DataType paramType = DataTypeExtensions.StringToDataType(func.Params[paramIdx].Type);
            variableTypes[paramName] = paramType;
            Emit(new Copy(argValues[i], new Variable(paramName, paramType)));
        }

        foreach (var kvp in kwArgValues)
        {
            bool found = false;
            for (int pi = paramOffset; pi < func.Params.Count; ++pi)
            {
                if (func.Params[pi].Name == kvp.Key)
                {
                    string paramName = currentInlinePrefix + func.Params[pi].Name;
                    boundParams.Add(pi);
                    found = true;

                    if (kvp.Value is Variable vkw) variableAliases[paramName] = vkw.Name;

                    if (IsConstType(func.Params[pi].Type))
                    {
                        if (func.Params[pi].Type == "const[str]")
                        {
                            if (rawKwStrArgs.TryGetValue(kvp.Key, out var skw))
                                strConstantVariables[paramName] = skw;
                            else if (kvp.Value is Variable vkw2 && ResolveStrConstant(vkw2.Name) is { } svkw)
                                strConstantVariables[paramName] = svkw;
                            else
                                throw UserError(
                                    $"Parameter '{func.Params[pi].Name}' is declared as const[str] and requires a compile-time string constant value");
                        }
                        else
                        {
                            if (!(kvp.Value is Constant ckw))
                                throw UserError(
                                    $"Parameter '{func.Params[pi].Name}' is declared as const and requires a compile-time constant value");
                            constantVariables[paramName] = ckw.Value;
                        }
                    }
                    else if (kvp.Value is Constant ckw2)
                    {
                        constantVariables[paramName] = ckw2.Value;
                    }
                    else
                    {
                        constantVariables.Remove(paramName);
                        strConstantVariables.Remove(paramName);
                        DataType paramType = DataTypeExtensions.StringToDataType(func.Params[pi].Type);
                        variableTypes[paramName] = paramType;
                        if (kvp.Value is Variable)
                        {
                            // Variable arg (including ZCA instances): preserve the alias
                            // set above and skip the Copy, same as positional arg handling.
                        }
                        else
                        {
                            variableAliases.Remove(paramName);
                            Emit(new Copy(kvp.Value, new Variable(paramName, paramType)));
                        }
                    }

                    break;
                }
            }

            if (!found) throw UserError($"Unknown keyword argument '{kvp.Key}' in call to {callee}");
        }

        for (int i = paramOffset; i < func.Params.Count; ++i)
        {
            if (boundParams.Contains(i)) continue;
            if (func.Params[i].DefaultValue != null)
            {
                string paramName = currentInlinePrefix + func.Params[i].Name;
                // A parameter defaulting to None (e.g. `cs: Pin = None`) is bound as
                // None, not as a value: track it so `cs is None` folds correctly and
                // emit no Copy (None has no runtime representation for a reference).
                if (func.Params[i].DefaultValue is NoneLiteral)
                {
                    noneValuedNames.Add(paramName);
                    continue;
                }
                Val defaultVal = VisitExpression(func.Params[i].DefaultValue!);

                if (IsConstType(func.Params[i].Type))
                {
                    if (func.Params[i].Type == "const[str]")
                    {
                        if (defaultVal is Variable vdf && ResolveStrConstant(vdf.Name) is string svdf)
                        {
                            strConstantVariables[paramName] = svdf;
                            continue;
                        }
                    }

                    if (!(defaultVal is Constant cdf))
                        throw UserError(
                            $"Default value for const parameter '{func.Params[i].Name}' must be a compile-time constant");
                    constantVariables[paramName] = cdf.Value;
                    continue;
                }

                if (defaultVal is Constant cdf2) constantVariables[paramName] = cdf2.Value;
                else
                {
                    DataType paramType = DataTypeExtensions.StringToDataType(func.Params[i].Type);
                    Emit(new Copy(defaultVal, new Variable(paramName, paramType)));
                }
            }
        }

        int savedLastLine = lastLine;
        lastLine = -1;
        try
        {
            VisitBlock(func.Body);
        }
        catch (CompilerError)
        {
            throw;
        }
        catch (Exception ex)
        {
            int callLine = currentStmtLine > 0 ? currentStmtLine : 1;
            throw new CompilerError("CompileError", ex.Message, callLine, 1);
        }

        lastLine = savedLastLine;

        Emit(new Label(exitLabel));

        if (isForceInlined)
            Emit(new InlineExpansionMarker(callee, true));
        else if (isInlineMethod)
            Emit(new InlineExpansionMarker(Optimizer.InlineMarkerTag + callee, true));

        if (Enumerable.Last<InlineContext>(inlineStack).ResultVars.Count > 0)
            lastTupleResults = new List<string>(Enumerable.Last<InlineContext>(inlineStack).ResultVars);
        inlineStack.RemoveAt(inlineStack.Count - 1);
        activeInlineExpansions.Remove(callee);

        currentInlinePrefix = savedPrefix;
        currentModulePrefix = savedModulePrefix;
        inlineDepth--;

        if (result != null) return result;
        if (ctorSubexprSynth != null) return new Variable(ctorSubexprSynth);
        return new NoneVal();
    }

    // Resolve an overloaded call to its concrete mangled name: build a type suffix
    // from the positional arg types, prefer the exact match, else a default-aware
    // arity fallback. Returns callee unchanged when it is not overloaded.
    private string ResolveOverloadedCallee(string callee, CallExpr expr)
    {
        if (overloadedFunctions.Contains(callee))
        {
            string ShortClassName(string fullKey)
            {
                foreach (var cn in classNames)
                {
                    if (fullKey == cn) return cn;
                    if (fullKey.Length > cn.Length && fullKey[fullKey.Length - cn.Length - 1] == '_' &&
                        fullKey.EndsWith((string)cn)) return cn;
                }

                return fullKey;
            }

            string ArgTypeSuffix(Expression arg)
            {
                if (arg is StringLiteral) return "str";
                if (arg is VariableExpr v)
                {
                    string key = currentInlinePrefix + v.Name;
                    for (int depth = 0; depth < 20; depth++)
                    {
                        if (instanceClasses.TryGetValue(key, out string ic)) return ShortClassName(ic);
                        if (strConstantVariables.ContainsKey(key)) return "str";
                        if (variableAliases.TryGetValue(key, out string ak)) key = ak;
                        else break;
                    }

                    // SRAM arrays (variable-indexed) are passed as buffer pointers — use
                    // "bytearray" so overloads that accept bytearray parameters are selected.
                    // Try all three qualified forms since the set may use different prefixes.
                    string qKey = !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + v.Name : v.Name;
                    if (arraysWithVariableIndex.Contains(key) || arraysWithVariableIndex.Contains(qKey) ||
                        arraysWithVariableIndex.Contains(v.Name) ||
                        moduleSramArrays.Contains(key) || moduleSramArrays.Contains(qKey) ||
                        bytearrayParams.Contains(key) || bytearrayParams.Contains(qKey))
                        return "bytearray";
                }

                return IRGenerator.DataTypeToSuffixStr(InferExprType(arg));
            }

            string suffix = "";
            bool first = true;
            foreach (var arg in expr.Args)
            {
                if (arg is KeywordArgExpr) continue;
                if (!first) suffix += "_";
                first = false;
                suffix += ArgTypeSuffix(arg);
            }

            if (string.IsNullOrEmpty(suffix)) suffix = "void";

            var mangled = callee + "___" + suffix;
            if (inlineFunctions.ContainsKey(mangled)) callee = mangled;
            else
            {
                var argCount = expr.Args.Count(a => a is not KeywordArgExpr);

                // 1) Existing behaviour: first overload whose parameter count equals the
                //    number of supplied positional arguments exactly.
                string? pick = null;
                foreach (var kvp in inlineFunctions)
                {
                    if (!kvp.Key.StartsWith(callee + "___")) continue;
                    if (kvp.Value.Params.Count(p => p.Name != "self") == argCount) { pick = kvp.Key; break; }
                }

                // 2) Default-aware fallback. When no exact-arity overload exists — e.g. a
                //    one-arg Pin(14) against overloads whose trailing params have defaults —
                //    accept an overload that defaults the missing trailing params, and prefer
                //    the one whose leading parameter types match the argument types so the
                //    int literal selects const[uint8], not the const[str] overload. Falls
                //    back to the first arity-compatible overload. This only runs for calls
                //    that previously resolved to nothing, so existing resolutions (AVR and
                //    ARM alike) are unchanged.
                if (pick is null)
                {
                    static string NormType(string t)
                        => t.StartsWith("const[") && t.EndsWith("]") ? t[6..^1] : t;

                    string? typed = null, anyArity = null;
                    foreach (var kvp in inlineFunctions)
                    {
                        if (!kvp.Key.StartsWith(callee + "___")) continue;
                        var ps = kvp.Value.Params.Where(p => p.Name != "self").ToList();
                        if (argCount > ps.Count) continue;

                        bool restDefaulted = true;
                        for (int pi = argCount; pi < ps.Count; pi++)
                            if (ps[pi].DefaultValue is null) { restDefaulted = false; break; }
                        if (!restDefaulted) continue;

                        anyArity ??= kvp.Key;
                        string lead = string.Join("_", ps.Take(argCount).Select(p => NormType(p.Type)));
                        if (argCount == 0 || lead == suffix) { typed = kvp.Key; break; }
                    }
                    pick = typed ?? anyArity;
                }

                if (pick != null) callee = pick;
            }
        }
        return callee;
    }

    // super().method(args): expand the resolved base-class @inline method body in place,
    // aliasing self and binding the args into the new inline frame (single-inheritance
    // ZCA super-call). Returns NoneVal when handled; null to fall through to normal call
    // resolution (not a super call, or the base method is not an inline function).
    private Val? TryEmitSuperMethodCall(CallExpr expr)
    {
        if (expr.Callee is not MemberAccessExpr mem) return null;
        if (mem.Object is not CallExpr { Callee: VariableExpr { Name: "super" } }) return null;

        string childClass = string.IsNullOrEmpty(currentModulePrefix)
            ? ""
            : currentModulePrefix.Substring(0, currentModulePrefix.Length - 1);
        if (!classBasePrefixes.TryGetValue(childClass, out var basePrefix)) return null;

        var calleeSuper = basePrefix + mem.Member;
        if (!inlineFunctions.TryGetValue(calleeSuper, out var funcSuper)) return null;

        var exitLabel = MakeLabel();
        var newDepth = inlineDepth + 1;
        var newPrefix = $"inline{newDepth}_{funcSuper.Name}_";

        var selfAlias = currentInlinePrefix + "self";
        if (variableAliases.TryGetValue(selfAlias, out var vAlias))
            variableAliases[newPrefix + "self"] = vAlias;
        else if (!string.IsNullOrEmpty(pendingConstructorTarget))
            variableAliases[newPrefix + "self"] = pendingConstructorTarget;

        var paramIdx = 0;
        foreach (var p in funcSuper.Params)
        {
            if (p.Name == "self") continue;
            if (paramIdx >= expr.Args.Count) continue;
            var argVal = VisitExpression(expr.Args[paramIdx]);
            var paramKey = newPrefix + p.Name;
            if (argVal is Variable vArg)
            {
                variableAliases[paramKey] = vArg.Name;
            }
            else
            {
                var paramVar = new Variable(paramKey, DataType.UINT8);
                Emit(new Copy(argVal, paramVar));
            }

            paramIdx++;
        }

        var savedPrefix = currentInlinePrefix;
        var savedMod = currentModulePrefix;
        var savedDepth = inlineDepth;

        currentInlinePrefix = newPrefix;
        currentModulePrefix = basePrefix;
        inlineDepth = newDepth;
        inlineStack.Add(new InlineContext { ExitLabel = exitLabel });

        VisitBlock(funcSuper.Body);
        Emit(new Label(exitLabel));
        inlineStack.RemoveAt(inlineStack.Count - 1);

        currentInlinePrefix = savedPrefix;
        currentModulePrefix = savedMod;
        inlineDepth = savedDepth;
        return new NoneVal();
    }

    // RFC 0001 Model B (Class[N]): `arr[i].method(args)` — compute the element address
    // (base + i*stride) and call the shared slot method with it as the self pointer.
    // Returns the call result when handled; null when the receiver is not an instance
    // array (fall through to normal member-call resolution).
    private Val? TryEmitInstanceArrayMethodCall(CallExpr expr, MemberAccessExpr memC)
    {
        if (memC.Object is not IndexExpr { Target: VariableExpr iaArr } iaIdx) return null;

        string iaQ = !string.IsNullOrEmpty(currentFunction)
            ? currentFunction + "." + iaArr.Name : iaArr.Name;
        if (!instanceArrayClass.ContainsKey(iaQ) && instanceArrayClass.ContainsKey(iaArr.Name))
            iaQ = iaArr.Name;
        if (!instanceArrayClass.TryGetValue(iaQ, out var iaCls)) return null;

        string iaMethod = ResolveMROMethod(iaCls, memC.Member) + "_" + memC.Member;
        int stride = instanceArrayStride[iaQ];

        Val idxV = VisitExpression(iaIdx.Index);
        Temporary baseT = MakeTemp(DataType.UINT16);
        Emit(new Copy(new ArrayBase(iaQ), baseT));        // load slot-array base addr
        Temporary scaled = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, idxV, new Constant(stride), scaled));
        Temporary elemAddr = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, baseT, scaled, elemAddr)); // base + i*stride

        var iaArgs = new List<Val> { elemAddr };
        foreach (var a in expr.Args) iaArgs.Add(VisitExpression(a));

        bool iaVoid = !functionReturnTypes.TryGetValue(iaMethod, out var iaRt)
                      || iaRt == "void" || iaRt == "None";
        if (iaVoid)
        {
            Emit(new Call(iaMethod, iaArgs, new NoneVal()));
            return new NoneVal();
        }
        Temporary iaDst = MakeTemp(DataTypeExtensions.StringToDataType(functionReturnTypes[iaMethod]));
        Emit(new Call(iaMethod, iaArgs, iaDst));
        return iaDst;
    }

    // `self.method(args)` inside an outlined method: call the sibling outlined method,
    // forwarding this method's own self — the slot pointer (Model B) or the field params
    // (Model A). Keeps the call a shared subroutine instead of force-inlining the whole
    // containing method at each call site. Returns the call result when handled; null to
    // fall through (not a self-call, the containing method is not outlined, or the target
    // is not itself outlined).
    private Val? TryEmitSelfOutlinedMethodCall(CallExpr expr, MemberAccessExpr memC)
    {
        if (memC.Object is not VariableExpr { Name: "self" }) return null;
        if (!outlinedMethods.Contains(currentFunction)) return null;
        if (!methodInstanceTypes.TryGetValue(currentFunction, out var selfCls)) return null;

        string target = ResolveMROMethod(selfCls, memC.Member) + "_" + memC.Member;
        if (!outlinedMethods.Contains(target)) return null;

        var fwdArgs = new List<Val>();
        if (slotMethods.Contains(currentFunction))
            fwdArgs.Add(new Variable(currentFunction + ".self", DataType.UINT16));
        else
            foreach (var (fld, ty, _) in outlineFieldLayout[currentFunction])
                fwdArgs.Add(new Variable(currentFunction + ".self_" + fld,
                    DataTypeExtensions.StringToDataType(ty)));
        foreach (var a in expr.Args) fwdArgs.Add(VisitExpression(a));

        bool tVoid = !functionReturnTypes.TryGetValue(target, out var tRt)
                     || tRt == "void" || tRt == "None";
        if (tVoid) { Emit(new Call(target, fwdArgs, new NoneVal())); return new NoneVal(); }
        Temporary tDst = MakeTemp(DataTypeExtensions.StringToDataType(functionReturnTypes[target]));
        Emit(new Call(target, fwdArgs, tDst));
        return tDst;
    }

    // A variable bound to a lambda: expand the lambda body in place with the args bound
    // into a fresh inline frame. Returns the lambda's result when handled; null when the
    // callee is not a lambda variable (fall through).
    private Val? TryEmitLambdaCall(CallExpr expr, string callee)
    {
        string qcallee = "";
        if (expr.Callee is VariableExpr ve2)
        {
            if (!string.IsNullOrEmpty(currentInlinePrefix)) qcallee = currentInlinePrefix + ve2.Name;
            else if (!string.IsNullOrEmpty(currentFunction)) qcallee = currentFunction + "." + ve2.Name;
            else qcallee = ve2.Name;
        }

        string lambdaKey = "";
        if (!string.IsNullOrEmpty(qcallee) && lambdaVariableNames.TryGetValue(qcallee, out string lk1))
            lambdaKey = lk1;
        else if (lambdaVariableNames.TryGetValue(callee, out string lk2)) lambdaKey = lk2;

        if (string.IsNullOrEmpty(lambdaKey) || !lambdaFunctionsMap.TryGetValue(lambdaKey, out var lam))
            return null;

        string pfx = "__lam" + lambdaCounter++ + "_";
        for (int i = 0; i < lam.Params.Count && i < expr.Args.Count; ++i)
        {
            string paramKey = pfx + lam.Params[i].Name;
            Val argVal = VisitExpression(expr.Args[i]);
            DataType dt = DataTypeExtensions.StringToDataType(lam.Params[i].Type);
            if (argVal is Constant c) constantVariables[paramKey] = c.Value;
            else
            {
                Emit(new Copy(argVal, new Variable(paramKey, dt)));
                variableTypes[paramKey] = dt;
            }
        }

        string savedInline = currentInlinePrefix;
        currentInlinePrefix = pfx;
        Val resultL = VisitExpression(lam.Body);
        currentInlinePrefix = savedInline;

        foreach (var p in lam.Params)
        {
            string pk = pfx + p.Name;
            constantVariables.Remove(pk);
            variableTypes.Remove(pk);
        }

        return resultL;
    }

    // Indirect call through a FUNCREF-typed variable (a function pointer from funcref()).
    // Returns the call result when the callee variable is a funcref; null otherwise.
    private Val? TryEmitFuncrefVariableCall(CallExpr expr)
    {
        if (expr.Callee is not VariableExpr fvExpr) return null;

        // Build qualified key matching Assign.cs (currentFunction + "." + name when not inline)
        string fvKey = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + fvExpr.Name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + fvExpr.Name : fvExpr.Name);
        for (int d = 0; d < 20; ++d)
            if (variableAliases.TryGetValue(fvKey, out string nx)) fvKey = nx;
            else break;
        if (!variableTypes.TryGetValue(fvKey, out DataType fvType) || fvType != DataType.FUNCREF)
            return null;

        var indArgs = new List<Val>();
        foreach (var a in expr.Args)
            indArgs.Add(VisitExpression(a));
        Temporary indDst = MakeTemp();
        Emit(new IndirectCall(new Variable(fvKey, DataType.FUNCREF), indArgs, indDst));
        return indDst;
    }

    // Callable[N] array call: `_tasks[i]()` — load the function address from SRAM and ICALL.
    // Always handles the call (returns the result) or throws if the array is not Callable.
    private Val EmitCallableArrayCall(CallExpr expr, IndexExpr idxCallee, VariableExpr idxArr)
    {
        string arrKey = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + idxArr.Name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + idxArr.Name : idxArr.Name);
        if (!arraySizes.ContainsKey(arrKey) && arraySizes.ContainsKey(idxArr.Name))
            arrKey = idxArr.Name;
        if (arraySizes.TryGetValue(arrKey, out int arrSz)
            && arrayElemTypes.TryGetValue(arrKey, out DataType arrElemDt)
            && arrElemDt == DataType.FUNCREF)
        {
            Val idxVal = VisitExpression(idxCallee.Index);
            Temporary tmpFn = MakeTemp(DataType.FUNCREF);
            Emit(new ArrayLoad(arrKey, idxVal, tmpFn, DataType.FUNCREF, arrSz));
            var indArgs = new List<Val>();
            foreach (var a in expr.Args)
                indArgs.Add(VisitExpression(a));
            Val indDst = new NoneVal();
            Emit(new IndirectCall(tmpFn, indArgs, indDst));
            return indDst;
        }
        throw UserError($"Callable array '{idxArr.Name}' not found or element type is not Callable");
    }

    // ── Built-in functions (each handled when callee matches; always returns or throws) ──

    // len(x): compile-time constant for fixed-size arrays / list literals; runtime header
    // load for list[T]; __len__ dunder for ZCA instances.
    private Val EmitLenBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("len() expects exactly one argument");
        if (expr.Args[0] is ListExpr le2) return new Constant(le2.Elements.Count);
        if (expr.Args[0] is VariableExpr vLen)
        {
            if (!string.IsNullOrEmpty(currentInlinePrefix) &&
                arraySizes.TryGetValue(currentInlinePrefix + vLen.Name, out int s1)) return new Constant(s1);
            if (!string.IsNullOrEmpty(currentFunction) &&
                arraySizes.TryGetValue(currentFunction + "." + vLen.Name, out int s2)) return new Constant(s2);
            if (arraySizes.TryGetValue(vLen.Name, out int s3)) return new Constant(s3);

            // Follow variableAliases to resolve through @inline parameter bindings
            // (e.g. len(buf) inside write(buf: bytearray) where buf aliases main.out_buf).
            string lenKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + vLen.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + vLen.Name : vLen.Name);
            string lenResolved = lenKey;
            for (int depth = 0; depth < 20; depth++)
            {
                if (!variableAliases.TryGetValue(lenResolved, out string lenNext)) break;
                lenResolved = lenNext;
                if (arraySizes.TryGetValue(lenResolved, out int sAlias)) return new Constant(sAlias);
            }
        }

        Val argVal = VisitExpression(expr.Args[0]);
        string cls = GetValClass(argVal);
        if (!string.IsNullOrEmpty(cls))
        {
            string funcKey = cls + "_" + "__len__";
            if (inlineFunctions.ContainsKey(funcKey))
            {
                string selfName = argVal is Variable v ? v.Name : (argVal is Temporary t ? t.Name : "");
                return EmitDunderCall(selfName, cls, funcKey, new List<Val>());
            }
        }

        // Handle list[T] variable: len(x) → load length from offset 0 of heap header
        if (expr.Args[0] is VariableExpr vListLen)
        {
            string listQual = ResolveListVarQualified(vListLen.Name);
            if (!string.IsNullOrEmpty(listQual))
            {
                Val listPtr = new Variable(listQual, DataType.GC_REF);
                return EmitListLoad(listPtr, 0, DataType.UINT8);
            }
        }

        throw UserError("len() argument must be a fixed-size array or list literal");
    }

    // int.from_bytes(bytes, endian): assemble a uint16 from a two-byte literal/list.
    private Val EmitIntFromBytesBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 2)
            throw UserError("int.from_bytes() expects exactly two arguments (bytes, endian)");
        bool littleEndian = true;
        if (expr.Args[1] is StringLiteral estr)
        {
            if (estr.Value == "big") littleEndian = false;
            else if (estr.Value != "little")
                throw UserError("int.from_bytes() endian must be 'little' or 'big'");
        }
        else throw UserError("int.from_bytes() endian argument must be a string literal");

        if (expr.Args[0] is ListExpr le)
        {
            if (le.Elements.Count < 2) throw UserError("int.from_bytes() requires at least 2 bytes");
            Val b0 = VisitExpression(le.Elements[0]);
            Val b1 = VisitExpression(le.Elements[1]);

            if (b0 is Constant c0 && b1 is Constant c1)
            {
                int val = littleEndian
                    ? ((c1.Value & 0xFF) << 8) | (c0.Value & 0xFF)
                    : ((c0.Value & 0xFF) << 8) | (c1.Value & 0xFF);
                return new Constant(val);
            }

            Val loVal = littleEndian ? b0 : b1;
            Val hiVal = littleEndian ? b1 : b0;
            Temporary hiShifted = MakeTemp(DataType.UINT16);
            Temporary resT = MakeTemp(DataType.UINT16);
            Emit(new Binary(BinaryOp.LShift, hiVal, new Constant(8), hiShifted));
            Emit(new Binary(BinaryOp.BitOr, hiShifted, loVal, resT));
            return resT;
        }

        throw UserError("int.from_bytes() first argument must be a bytes literal b\"...\" or list [lo, hi]");
    }

    // abs(x): compile-time fold for constants, else a branchless-ish negate-if-negative.
    private Val EmitAbsBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("abs() expects exactly one argument");
        var v = VisitExpression(expr.Args[0]);
        if (v is Constant c) return new Constant(c.Value < 0 ? -c.Value : c.Value);
        var negLabel = MakeLabel();
        var endLabel = MakeLabel();
        var result = MakeTemp();
        var negv = MakeTemp();
        Emit(new Binary(BinaryOp.LessThan, v, new Constant(0), negv));
        Emit(new JumpIfNotZero(negv, negLabel));
        Emit(new Copy(v, result));
        Emit(new Jump(endLabel));
        Emit(new Label(negLabel));
        Temporary negResult = MakeTemp();
        Emit(new Binary(BinaryOp.Sub, new Constant(0), v, negResult));
        Emit(new Copy(negResult, result));
        Emit(new Label(endLabel));
        return result;
    }

    // min(a, b): compile-time fold for constants, else compare-and-select.
    private Val EmitMinBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 2) throw UserError("min() expects exactly two arguments");
        Val a = VisitExpression(expr.Args[0]);
        Val b = VisitExpression(expr.Args[1]);
        if (a is Constant ca && b is Constant cb) return new Constant(ca.Value < cb.Value ? ca.Value : cb.Value);
        string elseLabel = MakeLabel();
        string endLabel = MakeLabel();
        Temporary result = MakeTemp();
        Temporary cmp = MakeTemp();
        Emit(new Binary(BinaryOp.LessThan, a, b, cmp));
        Emit(new JumpIfZero(cmp, elseLabel));
        Emit(new Copy(a, result));
        Emit(new Jump(endLabel));
        Emit(new Label(elseLabel));
        Emit(new Copy(b, result));
        Emit(new Label(endLabel));
        return result;
    }

    // max(a, b): compile-time fold for constants, else compare-and-select.
    private Val EmitMaxBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 2) throw UserError("max() expects exactly two arguments");
        var a = VisitExpression(expr.Args[0]);
        var b = VisitExpression(expr.Args[1]);
        if (a is Constant ca && b is Constant cb) return new Constant(ca.Value > cb.Value ? ca.Value : cb.Value);
        var elseLabel = MakeLabel();
        var endLabel = MakeLabel();
        var result = MakeTemp();
        var cmp = MakeTemp();
        Emit(new Binary(BinaryOp.GreaterThan, a, b, cmp));
        Emit(new JumpIfZero(cmp, elseLabel));
        Emit(new Copy(a, result));
        Emit(new Jump(endLabel));
        Emit(new Label(elseLabel));
        Emit(new Copy(b, result));
        Emit(new Label(endLabel));
        return result;
    }

    // ord(c): single-character string literal -> its code point; otherwise pass through.
    private Val EmitOrdBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("ord() expects exactly one argument");
        if (expr.Args[0] is StringLiteral sl)
        {
            if (sl.Value.Length != 1) throw UserError("ord() argument must be a single character");
            return new Constant((int)sl.Value[0]);
        }

        return VisitExpression(expr.Args[0]);
    }

    // chr(n): a byte value treated as a character; pass the value through unchanged.
    private Val EmitChrBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("chr() expects exactly one argument");
        return VisitExpression(expr.Args[0]);
    }

    // sum(seq): fold a list literal or sum a fixed-size array's unrolled elements.
    private Val EmitSumBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("sum() expects exactly one argument");
        switch (expr.Args[0])
        {
            case ListExpr { Elements.Count: 0 }:
                return new Constant(0);
            case ListExpr le:
            {
                var acc = VisitExpression(le.Elements[0]);
                for (var i = 1; i < le.Elements.Count; ++i)
                {
                    var v = VisitExpression(le.Elements[i]);
                    if (acc is Constant ca && v is Constant cv)
                    {
                        acc = new Constant(ca.Value + cv.Value);
                        continue;
                    }

                    var t = MakeTemp();
                    Emit(new Binary(BinaryOp.Add, acc, v, t));
                    acc = t;
                }

                return acc;
            }
            case VariableExpr sumVar:
            {
                int arrSize = -1;
                string arrBase = "";
                if (!string.IsNullOrEmpty(currentInlinePrefix))
                {
                    string key = currentInlinePrefix + sumVar.Name;
                    if (arraySizes.TryGetValue(key, out int s))
                    {
                        arrSize = s;
                        arrBase = key;
                    }
                }

                if (arrSize < 0 && !string.IsNullOrEmpty(currentFunction))
                {
                    string key = currentFunction + "." + sumVar.Name;
                    if (arraySizes.TryGetValue(key, out int s))
                    {
                        arrSize = s;
                        arrBase = key;
                    }
                }

                if (arrSize < 0 && arraySizes.TryGetValue(sumVar.Name, out int s2))
                {
                    arrSize = s2;
                    arrBase = sumVar.Name;
                }

                if (arrSize <= 0) throw UserError("sum() requires a list literal or fixed-size array");

                Val acc = new Variable(arrBase + "__0", DataType.UINT8);
                for (int i = 1; i < arrSize; ++i)
                {
                    Val vi = new Variable(arrBase + "__" + i, DataType.UINT8);
                    Temporary t = MakeTemp();
                    Emit(new Binary(BinaryOp.Add, acc, vi, t));
                    acc = t;
                }

                return acc;
            }
            default:
                throw UserError("sum() requires a list literal or fixed-size array");
        }
    }

    // any(list-literal): compile-time fold when all elements are constant, else an
    // OR-reduction over the elements.
    private Val EmitAnyBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("any() expects exactly one argument");
        if (!(expr.Args[0] is ListExpr le)) throw UserError("any() requires a list literal argument");
        if (le.Elements.Count == 0) return new Constant(0);
        bool allConst = true;
        foreach (var e in le.Elements)
        {
            Val v = VisitExpression(e);
            if (v is Constant c)
            {
                if (c.Value != 0) return new Constant(1);
            }
            else allConst = false;
        }

        if (allConst) return new Constant(0);
        Temporary result = MakeTemp();
        Emit(new Copy(new Constant(0), result));
        foreach (var e in le.Elements)
        {
            Val v = VisitExpression(e);
            Temporary cmp = MakeTemp();
            Emit(new Binary(BinaryOp.NotEqual, v, new Constant(0), cmp));
            string endLbl = MakeLabel();
            Emit(new JumpIfNotZero(result, endLbl));
            Emit(new Copy(cmp, result));
            Emit(new Label(endLbl));
        }

        return result;
    }

    // all(list-literal): compile-time fold when all elements are constant, else an
    // AND-reduction over the elements.
    private Val EmitAllBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("all() expects exactly one argument");
        if (!(expr.Args[0] is ListExpr le)) throw UserError("all() requires a list literal argument");
        if (le.Elements.Count == 0) return new Constant(1);
        bool allConst = true;
        foreach (var e in le.Elements)
        {
            Val v = VisitExpression(e);
            if (v is Constant c)
            {
                if (c.Value == 0) return new Constant(0);
            }
            else allConst = false;
        }

        if (allConst) return new Constant(1);
        Temporary result = MakeTemp();
        Emit(new Copy(new Constant(1), result));
        foreach (var e in le.Elements)
        {
            Val v = VisitExpression(e);
            Temporary cmp = MakeTemp();
            Emit(new Binary(BinaryOp.NotEqual, v, new Constant(0), cmp));
            string endLbl = MakeLabel();
            Emit(new JumpIfZero(result, endLbl));
            Emit(new Copy(cmp, result));
            Emit(new Label(endLbl));
        }

        return result;
    }

    // hex(const): intern "0x…" as a flash string literal, return its id (compile-time only).
    private Val EmitHexBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("hex() expects exactly one argument");
        Val v = VisitExpression(expr.Args[0]);
        if (!(v is Constant c)) throw UserError("hex() argument must be a compile-time constant integer");
        string hexstr = "0x" + c.Value.ToString("x");
        if (!stringLiteralIds.ContainsKey(hexstr))
        {
            stringLiteralIds[hexstr] = nextStringId;
            stringIdToStr[nextStringId] = hexstr;
            nextStringId++;
        }

        return new Constant(stringLiteralIds[hexstr]);
    }

    // bin(const): intern "0b…" as a flash string literal, return its id (compile-time only).
    private Val EmitBinBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("bin() expects exactly one argument");
        Val v = VisitExpression(expr.Args[0]);
        if (!(v is Constant c)) throw UserError("bin() argument must be a compile-time constant integer");
        string binstr = "0b" + Convert.ToString(c.Value, 2);
        if (!stringLiteralIds.ContainsKey(binstr))
        {
            stringLiteralIds[binstr] = nextStringId;
            stringIdToStr[nextStringId] = binstr;
            nextStringId++;
        }

        return new Constant(stringLiteralIds[binstr]);
    }

    // str(const): intern the decimal form as a flash string literal (compile-time only).
    private Val EmitStrBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("str() expects exactly one argument");
        Val v = VisitExpression(expr.Args[0]);
        if (!(v is Constant c)) throw UserError("str() argument must be a compile-time constant integer");
        string decstr = c.Value.ToString();
        if (!stringLiteralIds.ContainsKey(decstr))
        {
            stringLiteralIds[decstr] = nextStringId;
            stringIdToStr[nextStringId] = decstr;
            nextStringId++;
        }

        return new Constant(stringLiteralIds[decstr]);
    }

    // pow(base, exp): compile-time integer exponentiation (both args must be constant).
    private Val EmitPowBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 2) throw UserError("pow() expects exactly two arguments");
        Val bv = VisitExpression(expr.Args[0]);
        Val ev = VisitExpression(expr.Args[1]);
        if (!(bv is Constant cb) || !(ev is Constant ce))
            throw UserError("pow() arguments must be compile-time constant integers");
        int @base = cb.Value;
        int exp = ce.Value;
        if (exp < 0) throw UserError("pow() negative exponent not supported");
        int res = 1;
        for (int k = 0; k < exp; ++k) res *= @base;
        return new Constant(res);
    }

    // Numeric-cast builtins: uint8/uint16/uint32/int8/int16/int32/int.
    private static readonly Dictionary<string, DataType> CastTypes = new()
    {
        { "uint8", DataType.UINT8 }, { "uint16", DataType.UINT16 }, { "uint32", DataType.UINT32 },
        { "int8", DataType.INT8 }, { "int16", DataType.INT16 }, { "int32", DataType.INT32 },
        { "int", DataType.INT16 }
    };

    // divmod(a, b): wider-operand result width; constant-folds, emits a fused
    // FloorDiv+Mod pair for the 2-tuple target, else just the quotient.
    private Val EmitDivmodBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 2) throw UserError("divmod() expects exactly two arguments");
        Val aVal = VisitExpression(expr.Args[0]);
        Val bVal = VisitExpression(expr.Args[1]);
        // Result width follows the wider operand, so divmod(uint16, ...) divides at
        // 16-bit width and stores 16-bit results rather than truncating to 8 bits.
        // Read the type off the resolved Vals (Variable/Temporary carry it; a constant
        // is sized by its value) -- InferExprType keys on the unqualified name and
        // misses prefixed locals.
        static DataType ValType(Val v) => v switch
        {
            Variable x => x.Type,
            Temporary x => x.Type,
            Constant c => c.Value < 0 ? DataType.INT16
                          : c.Value <= 0xFF ? DataType.UINT8
                          : c.Value <= 0xFFFF ? DataType.UINT16 : DataType.UINT32,
            _ => DataType.UINT8,
        };
        DataType ta = ValType(aVal), tb = ValType(bVal);
        DataType rt = ta.SizeOf() >= tb.SizeOf() ? ta : tb;
        if (rt == DataType.UNKNOWN || rt.SizeOf() == 0) rt = DataType.UINT8;

        if (aVal is Constant ca && bVal is Constant cb)
        {
            if (cb.Value == 0) throw UserError("divmod(): division by zero");
            int q = ca.Value / cb.Value;
            int r = ca.Value % cb.Value;
            if (pendingTupleCount == 2)
            {
                string bBase = string.IsNullOrEmpty(currentFunction) ? "main" : currentFunction;
                string qn = bBase + ".divmod_q" + tempCounter;
                string rn = bBase + ".divmod_r" + (tempCounter + 1);
                tempCounter += 2;
                Emit(new Copy(new Constant(q), new Variable(qn, rt)));
                Emit(new Copy(new Constant(r), new Variable(rn, rt)));
                lastTupleResults = new List<string> { qn, rn };
                return new NoneVal();
            }

            return new Constant(q);
        }

        if (pendingTupleCount == 2)
        {
            string bBase = string.IsNullOrEmpty(currentFunction) ? "main" : currentFunction;
            string qn = bBase + ".divmod_q" + tempCounter;
            string rn = bBase + ".divmod_r" + (tempCounter + 1);
            tempCounter += 2;
            var qvar = new Variable(qn, rt);
            var rvar = new Variable(rn, rt);

            // Emit the quotient and remainder as the same FloorDiv/Mod the // and %
            // operators produce, adjacent and sharing operands, so the AVR backend's
            // divmod fusion folds the pair into a single division call.
            Emit(new Binary(BinaryOp.FloorDiv, aVal, bVal, qvar));
            Emit(new Binary(BinaryOp.Mod, aVal, bVal, rvar));
            lastTupleResults = new List<string> { qn, rn };
            return new NoneVal();
        }

        Temporary qTmp = MakeTemp(rt);
        Emit(new Binary(BinaryOp.FloorDiv, aVal, bVal, qTmp));
        return qTmp;
    }

    // uint8(x)/int16(x)/… numeric cast: constant- and float-constant-fold, else a
    // width-changing Copy. `callee` is guaranteed to be a key of CastTypes.
    private Val EmitNumericCastBuiltin(CallExpr expr, string callee)
    {
        DataType dstType = CastTypes[callee];
        if (expr.Args.Count != 1) throw UserError(callee + "() expects exactly one argument");
        Val v = VisitExpression(expr.Args[0]);
        if (v is Constant c)
        {
            int val = c.Value;
            switch (dstType)
            {
                case DataType.UINT8: val = (byte)val; break;
                case DataType.UINT16: val = (ushort)val; break;
                case DataType.INT8: val = (sbyte)val; break;
                case DataType.INT16: val = (short)val; break;
            }

            return new Constant(val);
        }

        // Float constant to integer cast: fold at compile time (e.g. uint16(0.5 * 1000) -> 500).
        if (v is FloatConstant fc && dstType != DataType.FLOAT)
        {
            int val = (int)fc.Value;
            switch (dstType)
            {
                case DataType.UINT8: val = (byte)val; break;
                case DataType.UINT16: val = (ushort)val; break;
                case DataType.INT8: val = (sbyte)val; break;
                case DataType.INT16: val = (short)val; break;
            }
            return new Constant(val);
        }

        Temporary dst = MakeTemp(dstType);
        Emit(new Copy(v, dst));
        return dst;
    }

    // bitcast(type, value): reinterpret bits between float and integer widths.
    private Val EmitBitcastBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 2) throw UserError("bitcast() expects exactly two arguments: bitcast(type, value)");
        string typeName = (expr.Args[0] as VariableExpr)?.Name
            ?? throw UserError("bitcast() first argument must be a type name");
        DataType bcDstType;
        if (typeName == "float")
            bcDstType = DataType.FLOAT;
        else if (!CastTypes.TryGetValue(typeName, out bcDstType))
            throw UserError($"bitcast(): unknown type '{typeName}'");

        Val srcVal = VisitExpression(expr.Args[1]);

        // Compile-time constant folding
        if (bcDstType == DataType.UINT32 && srcVal is FloatConstant fcBc)
        {
            uint bits = BitConverter.SingleToUInt32Bits((float)fcBc.Value);
            return new Constant((int)bits);
        }
        if (bcDstType == DataType.FLOAT && srcVal is Constant cBc)
            return new FloatConstant(BitConverter.Int32BitsToSingle(cBc.Value));

        Temporary bcDst = MakeTemp(bcDstType);
        Emit(new Bitcast(srcVal, bcDst));
        return bcDst;
    }

    // gc_alloc(size): allocate from the bounded GC heap, returning a GC_REF.
    private Val EmitGcAllocBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("gc_alloc() expects exactly one argument: gc_alloc(size)");
        Val sizeVal = VisitExpression(expr.Args[0]);
        Temporary gcDst = MakeTemp(DataType.GC_REF);
        Emit(new GcAlloc(sizeVal, gcDst));
        return gcDst;
    }

    // asm("code" [, op0, op1, …]): emit inline assembly, optionally with %N constraint
    // operands (resolved to Variables so the backend can load/store them).
    private Val EmitAsmBuiltin(CallExpr expr)
    {
        // asm("code")                  — bare inline assembly (no constraints)
        // asm("code", op0, op1, ...)   — assembly with %N register constraints
        if (expr.Args.Count < 1) throw UserError("asm() requires at least one string argument");

        string? code = null;
        if (expr.Args[0] is StringLiteral str2)
            code = str2.Value;
        else if (expr.Args[0] is FStringExpr fstr2)
        {
            var resolved = VisitFStringExpr(fstr2);
            if (resolved is Constant c2 && stringIdToStr.TryGetValue(c2.Value, out var s2))
                code = s2;
            else
                throw UserError("asm() f-string did not resolve to a string constant");
        }
        else if (expr.Args[0] is VariableExpr ve2)
            throw UserError($"asm() argument must be a string literal, got variable '{ve2.Name}'");
        else
            throw UserError("asm() argument must be a compile-time string literal");

        if (code == null) return new NoneVal();

        if (expr.Args.Count == 1)
        {
            Emit(new InlineAsm(code));
        }
        else
        {
            // Collect constraint operands (%0, %1, …).
            // Operands must resolve to Variables (not Constants) so that
            // the backend can both load the current value and store back
            // the modified result after the inline assembly executes.
            var operands = new List<Val>();
            for (int i = 1; i < expr.Args.Count; i++)
            {
                if (expr.Args[i] is VariableExpr ve)
                {
                    operands.Add(ResolveAsmOperand(ve.Name));
                }
                else
                {
                    operands.Add(VisitExpression(expr.Args[i]));
                }
            }
            Emit(new InlineAsm(code, operands));
        }
        return new NoneVal();
    }

    // print(*args, sep=" ", end="\n"): write each argument via the resolved string/
    // decimal/float UART helpers, separated by sep and terminated by end.
    private Val EmitPrintBuiltin(CallExpr expr)
    {
        string endStr = "\n";
        string sepStr = " ";
        var posArgs = new List<Expression>();
        foreach (var arg in expr.Args)
        {
            if (arg is KeywordArgExpr kw)
            {
                if (kw.Key == "end" || kw.Key == "sep")
                {
                    if (kw.Value is StringLiteral lit)
                    {
                        if (kw.Key == "end") endStr = lit.Value;
                        else sepStr = lit.Value;
                    }
                    else throw UserError($"print() '{kw.Key}' must be a compile-time string literal");
                }
            }
            else posArgs.Add(arg);
        }

        // Resolve the string-output function.  Prefer the arch-dispatched
        // console.print_str injected by the build driver; fall back to
        // uart_write_str for projects that initialise UART manually.
        string writeStrFn = ResolveCallee("print_str");
        if (writeStrFn == "print_str")
        {
            writeStrFn = ResolveCallee("uart_write_str");
            if (writeStrFn == "uart_write_str")
            {
                foreach (var fnName in inlineFunctions.Keys)
                {
                    if (fnName.EndsWith("_print_str") || fnName.EndsWith("_uart_write_str"))
                    {
                        writeStrFn = fnName;
                        break;
                    }
                }
            }
        }

        void EmitStr(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            var synthCall = new CallExpr(
                new VariableExpr(writeStrFn),
                new List<Expression> { new StringLiteral(s) });
            VisitCall(synthCall);
        }

        // Integer output: uart_write_decimal_u8 is non-inline and works with direct Emit.
        // print_u8 from console.py is @inline and requires VisitCall; deferred to a
        // future refactor of EmitPrintArg to use VisitCall for all write functions.
        string decimalWriteFn = ResolveCallee("uart_write_decimal_u8");
        if (decimalWriteFn == "uart_write_decimal_u8")
        {
            string decSuffix = "uart_write_decimal_u8";
            foreach (var fnName in functionReturnTypes.Keys)
            {
                if (fnName.EndsWith(decSuffix))
                {
                    decimalWriteFn = fnName;
                    break;
                }
            }
        }

        // Float output: same rationale — use the non-inline uart function.
        string floatWriteFn = ResolveCallee("uart_write_float");
        if (floatWriteFn == "uart_write_float")
        {
            foreach (var fnName in functionReturnTypes.Keys)
            {
                if (fnName.EndsWith("uart_write_float"))
                {
                    floatWriteFn = fnName;
                    break;
                }
            }
        }

        void EmitPrintArg(Expression arg)
        {
            if (arg is StringLiteral lit)
            {
                var synthCall = new CallExpr(
                    new VariableExpr(writeStrFn),
                    new List<Expression> { lit });
                VisitCall(synthCall);
                return;
            }

            if (arg is VariableExpr v)
            {
                string key = currentInlinePrefix + v.Name;
                string? strVal = ResolveStrConstant(key);
                if (strVal != null)
                {
                    var synthCall = new CallExpr(
                        new VariableExpr(writeStrFn),
                        new List<Expression> { new StringLiteral(strVal) });
                    VisitCall(synthCall);
                    return;
                }
            }

            Val val = VisitExpression(arg);
            bool isFloat = val is FloatConstant ||
                           (val is Variable vf && vf.Type == DataType.FLOAT) ||
                           (val is Temporary tf && tf.Type == DataType.FLOAT);
            if (isFloat)
            {
                Temporary ftmp = MakeTemp(DataType.FLOAT);
                Emit(new Copy(val, ftmp));
                Emit(new Call(floatWriteFn, new List<Val> { ftmp }, ftmp));
                return;
            }
            // Select the decimal formatter by the value's width/signedness so a
            // uint16/uint32 argument is not silently truncated to 8 bits.
            DataType argType = val switch
            {
                Variable v2 => v2.Type,
                Temporary t2 => t2.Type,
                Constant cc => cc.Value < 0 ? DataType.INT16
                             : cc.Value <= 0xFF ? DataType.UINT8
                             : cc.Value <= 0xFFFF ? DataType.UINT16 : DataType.UINT32,
                _ => DataType.UINT8,
            };
            (string decBase, DataType tmpType) = argType switch
            {
                DataType.UINT16 => ("uart_write_decimal_u16", DataType.UINT16),
                DataType.INT16 => ("uart_write_decimal_i16", DataType.INT16),
                DataType.UINT32 => ("uart_write_decimal_u32", DataType.UINT32),
                DataType.INT32 => ("uart_write_decimal_i16", DataType.INT16),
                _ => ("uart_write_decimal_u8", DataType.UINT8),
            };
            string decFn = ResolveCallee(decBase);
            if (decFn == decBase)
                foreach (var fnName in functionReturnTypes.Keys)
                    if (fnName.EndsWith(decBase, StringComparison.Ordinal)) { decFn = fnName; break; }

            Temporary tmp = MakeTemp(tmpType);
            Emit(new Copy(val, tmp));
            Emit(new Call(decFn, new List<Val> { tmp }, tmp));
        }

        if (posArgs.Count == 0)
        {
            EmitStr(endStr);
            return new NoneVal();
        }

        for (int i = 0; i < posArgs.Count; ++i)
        {
            if (i > 0) EmitStr(sepStr);
            EmitPrintArg(posArgs[i]);
        }

        EmitStr(endStr);
        return new NoneVal();
    }

    // funcref(fn): resolve a function name (through any alias chain) to a FunctionRef
    // value (a function pointer usable in Callable[N] arrays / indirect calls).
    private Val EmitFuncrefIntrinsic(CallExpr expr)
    {
        if (expr.Args.Count != 1)
            throw UserError("funcref() expects exactly one argument: a function name");
        if (expr.Args[0] is not VariableExpr fnRefExpr)
            throw UserError("funcref() argument must be a function name identifier");

        // Resolve alias chain (same as compile_isr) to find the canonical function name.
        string key = currentInlinePrefix + fnRefExpr.Name;
        for (int d = 0; d < 20; ++d)
            if (variableAliases.TryGetValue(key, out string nx)) key = nx;
            else break;

        string resolvedName = key;
        int lastDot = resolvedName.LastIndexOf('.');
        if (lastDot >= 0) resolvedName = resolvedName.Substring(lastDot + 1);
        string fnName = ResolveCallee(resolvedName);

        return new FunctionRef(fnName);
    }

    // _set_irq_zca_arg(handler, zca_instance): record the ZCA variable to bind to the
    // handler's first parameter when its ISR wrapper is synthesized (see compile_isr).
    private Val EmitSetIrqZcaArgIntrinsic(CallExpr expr)
    {
        if (expr.Args.Count == 2)
        {
            // Resolve handler name (same alias-chase as compile_isr arg0)
            string hKey = "";
            if (expr.Args[0] is VariableExpr v0)
            {
                hKey = currentInlinePrefix + v0.Name;
                for (int d = 0; d < 20; d++)
                    if (variableAliases.TryGetValue(hKey, out string? nk) && nk != null) hKey = nk; else break;
                int ld = hKey.LastIndexOf('.');
                if (ld >= 0) hKey = hKey[(ld + 1)..];
                hKey = ResolveCallee(hKey);
            }
            // Resolve ZCA instance to its root variable key (follow alias chain)
            Val zcaVal = VisitExpression(expr.Args[1]);
            string zcaKey = "";
            if (zcaVal is Variable vz) zcaKey = vz.Name;
            else if (zcaVal is Temporary tz) zcaKey = tz.Name;
            for (int d = 0; d < 20; d++)
                if (variableAliases.TryGetValue(zcaKey, out string? nz) && nz != null) zcaKey = nz; else break;

            if (!string.IsNullOrEmpty(hKey) && !string.IsNullOrEmpty(zcaKey))
                pendingZcaIsrBindings[hKey] = zcaKey;
        }
        return new NoneVal();
    }

    // compile_isr(handler, vector): register handler at the interrupt vector (or a
    // synthesized ZCA wrapper when a _set_irq_zca_arg binding was recorded).
    private Val EmitCompileIsrIntrinsic(CallExpr expr)
    {
        if (expr.Args.Count != 2)
            throw UserError("compile_isr() expects exactly 2 arguments: compile_isr(handler, vector)");
        Val vecVal = VisitExpression(expr.Args[1]);
        int vector = 0;
        if (vecVal is Constant c) vector = c.Value;
        else throw UserError("compile_isr() second argument (vector) must be a compile-time constant");

        string handlerFuncName = "";
        bool handlerProvided = false;

        if (expr.Args[0] is VariableExpr v)
        {
            string key = currentInlinePrefix + v.Name;
            if (constantVariables.TryGetValue(key, out int cv) && cv == 0) return new NoneVal();

            for (int depth = 0; depth < 20; ++depth)
            {
                if (variableAliases.TryGetValue(key, out string next)) key = next;
                else break;
            }

            // When compile_isr() is called inside an inlined function, the
            // handler parameter is an alias chain (e.g. handler -> main.int0_isr).
            // After alias resolution above, `key` holds the resolved name which
            // may be scope-qualified (e.g. "main.int0_isr" for a function defined
            // at top-level in main.py).  Extract the bare function name (after
            // the last dot) and resolve it via ResolveCallee so it gets the
            // correct module-qualified IR name.
            string resolvedName = key;
            int lastDot = resolvedName.LastIndexOf('.');
            if (lastDot >= 0)
                resolvedName = resolvedName.Substring(lastDot + 1);
            handlerFuncName = ResolveCallee(resolvedName);
            handlerProvided = !string.IsNullOrEmpty(handlerFuncName);
        }
        else
        {
            Val arg0 = VisitExpression(expr.Args[0]);
            if (arg0 is Constant c0 && c0.Value == 0) return new NoneVal();
            throw UserError("compile_isr() first argument must be a function reference or 0");
        }

        if (!handlerProvided) return new NoneVal();

        // ZCA ISR synthesis: if a ZCA binding was registered via _set_irq_zca_arg,
        // synthesize a parameterless wrapper that inline-expands handler with the
        // ZCA constants bound. The wrapper is what gets registered at the vector.
        if (pendingZcaIsrBindings.TryGetValue(handlerFuncName, out string? zcaRootKey) &&
            !string.IsNullOrEmpty(zcaRootKey))
        {
            pendingZcaIsrBindings.Remove(handlerFuncName);
            string synthName = SynthesizeZcaIsrWrapper(handlerFuncName, zcaRootKey);
            if (!string.IsNullOrEmpty(synthName))
            {
                pendingIsrRegistrations[synthName] = vector;
                return new NoneVal();
            }
            // Synthesis returned empty -- fall through to original name (will fail if ZCA param)
        }

        pendingIsrRegistrations[handlerFuncName] = vector;
        return new NoneVal();
    }

    // Call into a C extern function (@extern): coerce float args to ints per the C ABI
    // and emit a direct Call to the resolved C symbol.
    private Val EmitExternCall(CallExpr expr, string callee, string cSym)
    {
        var extArgs = new List<Val>();
        foreach (var arg in expr.Args)
        {
            Val av = VisitExpression(arg);
            if (av is FloatConstant avFc)
                av = new Constant((int)Math.Round(avFc.Value));
            else if (av is Variable v && floatConstantVariables.TryGetValue(v.Name, out double fv))
                av = new Constant((int)Math.Round(fv));
            extArgs.Add(av);
        }

        bool returnsVoid = !functionReturnTypes.ContainsKey(callee) || functionReturnTypes[callee] == "void" ||
                           functionReturnTypes[callee] == "None";
        if (returnsVoid)
        {
            Emit(new Call(cSym, extArgs, new NoneVal()));
            return new NoneVal();
        }

        Temporary extDst = MakeTemp(DataTypeExtensions.StringToDataType(functionReturnTypes[callee]));
        Emit(new Call(cSym, extArgs, extDst));
        return extDst;
    }

    // Evaluate an expression as a compile-time address for ptr(...): an integer literal,
    // a const or register/MMIO name, or those combined with constant +/- offsets — e.g.
    // ptr(0x100 + 6), ptr(BASE + 6), ptr(PORTB + 6). A bare register name contributes its
    // ADDRESS (via MemoryAddress.Address), NOT its dereferenced runtime value, so this must
    // resolve operands itself rather than going through arithmetic VisitBinary (which would
    // emit a runtime read of the register). Returns null if the expression is not a
    // compile-time address. Resolving a bare name/literal here emits no IR.
    private int? TryEvalConstAddress(Expression e)
    {
        switch (e)
        {
            case IntegerLiteral il:
                return il.Value;
            case BinaryExpr be when be.Op is PyMCU.Frontend.BinaryOp.Add or PyMCU.Frontend.BinaryOp.Sub:
            {
                if (TryEvalConstAddress(be.Left) is not int l) return null;
                if (TryEvalConstAddress(be.Right) is not int r) return null;
                return be.Op == PyMCU.Frontend.BinaryOp.Add ? l + r : l - r;
            }
            case VariableExpr:
                // A const folds to a Constant; a register/MMIO symbol resolves to a
                // MemoryAddress. Name resolution is side-effect free (no IR emitted).
                return VisitExpression(e) switch
                {
                    Constant c => c.Value,
                    MemoryAddress m => m.Address,
                    _ => null,
                };
            default:
                return null;
        }
    }

    // Resolves a short variable name to its fully qualified list variable name,
    // or returns "" if the variable is not a list[T].
    private string ResolveListVarQualified(string name)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix))
        {
            string k = currentInlinePrefix + name;
            if (listVarElemTypes.ContainsKey(k)) return k;
        }
        if (!string.IsNullOrEmpty(currentFunction))
        {
            string k = currentFunction + "." + name;
            if (listVarElemTypes.ContainsKey(k)) return k;
        }
        if (listVarElemTypes.ContainsKey(name)) return name;
        return "";
    }

    // Computes basePtr + 2 + index * elemSize as a UINT16 address Temporary.
    private Temporary EmitElemAddr(Val basePtr, Val index, int elemSize)
    {
        Val ptrU16 = basePtr is Temporary t ? t with { Type = DataType.UINT16 }
                   : basePtr is Variable v ? v with { Type = DataType.UINT16 }
                   : basePtr;

        Temporary finalAddr = MakeTemp(DataType.UINT16);
        if (elemSize == 1)
        {
            Temporary idxPlusTwo = MakeTemp(DataType.UINT16);
            Emit(new Binary(BinaryOp.Add, index, new Constant(2), idxPlusTwo));
            Emit(new Binary(BinaryOp.Add, ptrU16, idxPlusTwo, finalAddr));
        }
        else
        {
            Temporary scaled = MakeTemp(DataType.UINT16);
            Emit(new Binary(BinaryOp.Mul, index, new Constant(elemSize), scaled));
            Temporary scaledPlusTwo = MakeTemp(DataType.UINT16);
            Emit(new Binary(BinaryOp.Add, scaled, new Constant(2), scaledPlusTwo));
            Emit(new Binary(BinaryOp.Add, ptrU16, scaledPlusTwo, finalAddr));
        }
        return finalAddr;
    }

    // Emits IR for list.append(val). Handles fast path (len < cap) and slow path (realloc).
    private Val EmitListAppend(Variable listVar, Expression valExpr)
    {
        DataType elemDt = listVarElemTypes[listVar.Name];
        int elemSize = elemDt.SizeOf();

        // Load current length (offset 0) and capacity (offset 1)
        Temporary tmpLen = MakeTemp(DataType.UINT8);
        Emit(new LoadIndirect(listVar, tmpLen));
        Temporary tmpCap = EmitListLoad(listVar, 1, DataType.UINT8);

        string fastLabel = MakeLabel();

        // if len < cap: skip realloc
        Temporary ltCap = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.LessThan, tmpLen, tmpCap, ltCap));
        Emit(new JumpIfNotZero(ltCap, fastLabel));

        // === SLOW PATH: realloc to double capacity ===

        // new_cap = cap * 2
        Temporary newCap = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.Mul, tmpCap, new Constant(2), newCap));

        // new_alloc_size = 2 + new_cap * elemSize
        Temporary newCapScaled = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, newCap, new Constant(elemSize), newCapScaled));
        Temporary newAllocSize = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, newCapScaled, new Constant(2), newAllocSize));

        // Save old pointer, allocate new buffer
        Temporary oldPtr = MakeTemp(DataType.GC_REF);
        Emit(new Copy(listVar, oldPtr));
        Temporary newPtr = MakeTemp(DataType.GC_REF);
        Emit(new GcAlloc(newAllocSize, newPtr));

        // Write new header
        EmitListStore(newPtr, 0, tmpLen);
        EmitListStore(newPtr, 1, newCap);

        // Copy existing elements byte-by-byte
        // Compute base pointers outside the loop
        Val oldPtrU16 = oldPtr with { Type = DataType.UINT16 };
        Val newPtrU16 = newPtr with { Type = DataType.UINT16 };
        Temporary totalBytes = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, tmpLen, new Constant(elemSize), totalBytes));
        Temporary oldBase = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, oldPtrU16, new Constant(2), oldBase));
        Temporary newBase = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, newPtrU16, new Constant(2), newBase));

        Temporary byteOff = MakeTemp(DataType.UINT16);
        Emit(new Copy(new Constant(0), byteOff));
        string copyLoopLabel = MakeLabel();
        string copyLoopEnd = MakeLabel();
        Emit(new Label(copyLoopLabel));
        Temporary cmpDone = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.GreaterEqual, byteOff, totalBytes, cmpDone));
        Emit(new JumpIfNotZero(cmpDone, copyLoopEnd));
        Temporary srcAddr = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, oldBase, byteOff, srcAddr));
        Temporary dstAddr = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, newBase, byteOff, dstAddr));
        Temporary byteTmp = MakeTemp(DataType.UINT8);
        Emit(new LoadIndirect(srcAddr, byteTmp));
        Emit(new StoreIndirect(byteTmp, dstAddr));
        Emit(new AugAssign(BinaryOp.Add, byteOff, new Constant(1)));
        Emit(new Jump(copyLoopLabel));
        Emit(new Label(copyLoopEnd));

        // Update listVar → newPtr; shadow stack slot already tracks SRAM addr of listVar
        Emit(new Copy(newPtr, listVar));

        // === FAST PATH: write element at offset 2 + len * elemSize ===
        Emit(new Label(fastLabel));

        Val elemVal = VisitExpression(valExpr);
        Temporary appendAddr = EmitElemAddr(listVar, tmpLen, elemSize);
        Emit(new StoreIndirect(elemVal, appendAddr));

        // length += 1
        Temporary newLen = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.Add, tmpLen, new Constant(1), newLen));
        Emit(new StoreIndirect(newLen, listVar));

        return new NoneVal();
    }

    // -------------------------------------------------------------------------
    // Class hierarchy helpers — MRO resolution and virtual-dispatch gate
    // -------------------------------------------------------------------------

    /// <summary>
    /// Walk the MRO chain starting at <paramref name="cls"/> (no trailing underscore)
    /// and return the first class that directly defines <paramref name="methodName"/>.
    /// Falls back to <paramref name="cls"/> if no defining class is found (safe: linker
    /// will catch a truly-missing symbol).
    /// </summary>
    private string ResolveMROMethod(string cls, string methodName)
    {
        string? current = cls;
        while (current != null)
        {
            if (classDirectMethods.TryGetValue(current, out var dm) && dm.Contains(methodName))
                return current;

            if (classBasePrefixes.TryGetValue(current, out var parentPrefix)
                && !string.IsNullOrEmpty(parentPrefix))
            {
                // parentPrefix has a trailing underscore — strip it for the next lookup.
                current = parentPrefix!.EndsWith("_") ? parentPrefix[..^1] : parentPrefix;
            }
            else
                break;
        }
        return cls;
    }

    /// <summary>
    /// Returns <c>true</c> when a call to <paramref name="methodName"/> on an object of
    /// declared type <paramref name="cls"/> cannot be devirtualized statically.
    ///
    /// Devirtualization is safe (returns false) when ANY of:
    ///   Rule 1 — <paramref name="cls"/> is a leaf class (no known subclasses).
    ///   Rule 3 — no subclass in the entire subtree overrides the method.
    ///
    /// Rule 2 (exact type from instanceClasses) is enforced by the call site: we only
    /// reach this helper when instanceClasses already holds the concrete type, so Rule 2
    /// always applies and this method always returns false for the current ZCA model.
    /// The implementation is kept general for future polymorphic variable support.
    /// </summary>
    private bool IsVirtualDispatch(string cls, string methodName)
    {
        // Rule 1: leaf class.
        if (!classChildren.TryGetValue(cls, out var children) || children.Count == 0)
            return false;

        // Rule 3: no subclass overrides the method.
        if (IsMethodNeverOverridden(cls, methodName))
            return false;

        return true;
    }

    private bool IsMethodNeverOverridden(string cls, string methodName)
    {
        if (!classChildren.TryGetValue(cls, out var children)) return true;
        foreach (var child in children)
        {
            if (classDirectMethods.TryGetValue(child, out var dm) && dm.Contains(methodName))
                return false;
            if (!IsMethodNeverOverridden(child, methodName))
                return false;
        }
        return true;
    }

    // Synthesizes a parameterless ISR wrapper that inline-expands handlerFuncName
    // with the ZCA variable zcaRootKey bound to the handler's first parameter.
    // The synthesized Function is added to pendingZcaSynthFunctions and its name returned.
    private string SynthesizeZcaIsrWrapper(string handlerFuncName, string zcaRootKey)
    {
        if (!zcaHandlerAstNodes.TryGetValue(handlerFuncName, out var entry)) return "";

        var funcDef = entry.Func;
        if (funcDef.Params.Count == 0) return "";

        // Build a collision-free synthesis name
        string baseName = handlerFuncName.Replace('.', '_');
        string zcaSuffix = zcaRootKey.Replace('.', '_');
        string synthName = "_irq_synth_" + baseName + "_" + zcaSuffix;

        // Save compilation state
        var savedInstructions  = currentInstructions;
        var savedFunction      = currentFunction;
        var savedModulePrefix  = currentModulePrefix;
        var savedInlinePrefix  = currentInlinePrefix;
        int savedInlineDepth   = inlineDepth;
        var savedLoopStack     = loopStack;
        var savedInlineStack   = inlineStack;
        int savedLastLine      = lastLine;
        var savedFunctionGlobals = currentFunctionGlobals;

        // Set up fresh compilation context for the wrapper
        currentInstructions   = new List<Instruction>();
        currentFunction       = synthName;
        currentModulePrefix   = entry.Prefix;
        currentInlinePrefix   = synthName + "_s_";
        inlineDepth           = 0;
        loopStack             = new List<LoopLabels>();
        inlineStack           = new List<InlineContext>();
        lastLine              = -1;
        currentFunctionGlobals = new HashSet<string>();

        // Bind handler's first parameter to the ZCA root variable
        string paramName = currentInlinePrefix + funcDef.Params[0].Name;
        variableAliases[paramName] = zcaRootKey;
        if (instanceClasses.TryGetValue(zcaRootKey, out string? cls) && cls != null)
            instanceClasses[paramName] = cls;

        // Propagate ZCA sub-fields (constantVariables, strConstantVariables, instanceClasses)
        string zcaFieldPfx = zcaRootKey + ".";
        foreach (var kv in constantVariables
            .Where(kv => kv.Key.StartsWith(zcaFieldPfx)).ToList())
            constantVariables[paramName + "." + kv.Key[zcaFieldPfx.Length..]] = kv.Value;
        foreach (var kv in strConstantVariables
            .Where(kv => kv.Key.StartsWith(zcaFieldPfx)).ToList())
            strConstantVariables[paramName + "." + kv.Key[zcaFieldPfx.Length..]] = kv.Value;
        foreach (var kv in instanceClasses
            .Where(kv => kv.Key.StartsWith(zcaFieldPfx)).ToList())
            instanceClasses[paramName + "." + kv.Key[zcaFieldPfx.Length..]] = kv.Value;

        // Compile the handler body with ZCA constants in scope
        VisitBlock(funcDef.Body);
        if (currentInstructions.Count == 0 || currentInstructions[^1] is not Return)
            Emit(new Return(new NoneVal()));

        // Build the IR function object
        var wrapperFunc = new Function
        {
            Name         = synthName,
            OriginalName = funcDef.Name,
            ReturnType   = DataType.VOID,
            Body         = new List<Instruction>(currentInstructions),
        };
        pendingZcaSynthFunctions.Add(wrapperFunc);

        // Restore compilation state
        currentInstructions    = savedInstructions;
        currentFunction        = savedFunction;
        currentModulePrefix    = savedModulePrefix;
        currentInlinePrefix    = savedInlinePrefix;
        inlineDepth            = savedInlineDepth;
        loopStack              = savedLoopStack;
        inlineStack            = savedInlineStack;
        lastLine               = savedLastLine;
        currentFunctionGlobals = savedFunctionGlobals;

        return synthName;
    }
}
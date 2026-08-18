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
    // Intercepts `rp2.StateMachine(sm_id, prog, freq=..., set_base=..., ...)` where
    // `prog` names an @asm_pio program. MVP: PIO0 + state-machine 0 only (sm_id 0).
    // The whole setup is emitted as constant MMIO stores (Copy -> MemoryAddress):
    // the PIO block registers (INSTR_MEM + SM config) are chip-independent
    // (PIO0 @ 0x50200000 on both RP2040 and RP2350); only the RESETS-ungate and the
    // pin FUNCSEL are chip-specific. The state machine then runs autonomously.
    private Val? TryEmitPioStateMachine(CallExpr expr)
    {
        // Callee must be `StateMachine` (bare or `<mod>.StateMachine`).
        string? name = expr.Callee switch
        {
            VariableExpr v => v.Name,
            MemberAccessExpr m => m.Member,
            _ => null,
        };
        if (name != "StateMachine") return null;

        // Positional: (sm_id, prog). prog must be a known @asm_pio program.
        var pos = expr.Args.Where(a => a is not KeywordArgExpr).ToList();
        if (pos.Count < 2 || pos[1] is not VariableExpr progVar) return null;
        if (!(pioPrograms.TryGetValue(progVar.Name, out var prog)
              || pioPrograms.TryGetValue(currentModulePrefix + progVar.Name, out prog)))
            return null;

        int smId = TryConstInt(pos[0]) ?? 0;
        if (smId != 0)
            throw UserError("PIO StateMachine MVP supports state machine 0 only (sm_id=0)");

        // Keyword config (all compile-time constants).
        int Kw(string key, int dflt)
        {
            foreach (var a in expr.Args)
                if (a is KeywordArgExpr kw && kw.Key == key)
                    return TryConstInt(kw.Value) ?? dflt;
            return dflt;
        }
        int freq      = Kw("freq", 0);
        int setBase   = Kw("set_base", -1);
        int outBase   = Kw("out_base", -1);
        int sideBase  = Kw("sideset_base", -1);
        int inBase    = Kw("in_base", -1);

        var c = prog.Config;

        // ── Chip-specific: RESETS ungate PIO0 + per-pin FUNCSEL = PIO0 (=6) ──
        bool rp2350 = (deviceConfig.TargetChip ?? "").ToLowerInvariant() == "rp2350";
        int resetsClr = rp2350 ? 0x40023000 : 0x4000F000;     // RESETS_RESET atomic-clear alias
        int resetPio0 = rp2350 ? 11 : 10;
        int ioBank0   = rp2350 ? 0x40028000 : 0x40014000;
        int padsBank0 = rp2350 ? 0x40038000 : 0x4001C000;
        const int funcselPio0 = 6;

        void Store(int addr, int value) =>
            Emit(new Copy(new Constant(value), new MemoryAddress(addr, DataType.UINT32)));

        // Ungate PIO0.
        Store(resetsClr, 1 << resetPio0);

        // Route the consumed pins to PIO0 (and de-isolate the pad on RP2350).
        void RoutePins(int @base, int count)
        {
            if (@base < 0) return;
            for (int k = 0; k < count; k++)
            {
                if (rp2350) Store(padsBank0 + 4 + 4 * (@base + k), 1 << 6);   // IE on, ISO off
                Store(ioBank0 + 8 * (@base + k) + 4, funcselPio0);
            }
        }
        RoutePins(setBase, c.SetInitCount > 0 ? c.SetInitCount : 1);
        RoutePins(outBase, c.OutInitCount);
        RoutePins(sideBase, c.SideSetInitCount);

        // ── Chip-independent PIO0 block (base 0x50200000) ──
        const int pio0 = 0x50200000;
        const int instrMem = pio0 + 0x048;
        const int sm0 = pio0 + 0x0C8;

        // Load the assembled program into instruction memory.
        for (int i = 0; i < prog.Words.Length; i++)
            Store(instrMem + 4 * i, prog.Words[i]);

        // SM0 CLKDIV (INT[31:16], FRAC[15:8]); default to /1 when freq is 0.
        int sysclk = deviceConfig.Frequency > 0 ? (int)deviceConfig.Frequency : 125_000_000;
        int divInt = (freq > 0) ? sysclk / freq : 1;
        if (divInt < 1) divInt = 1;
        if (divInt > 0xFFFF) divInt = 0xFFFF;
        Store(sm0 + 0x00, divInt << 16);

        // SM0 EXECCTRL: WRAP_BOTTOM[11:7], WRAP_TOP[16:12], SIDE_PINDIR[29], SIDE_EN[30].
        int execctrl = (prog.Wrap << 12) | (prog.WrapTarget << 7)
                     | (c.SideSetPinDir ? 1 << 29 : 0) | (c.SideSetOpt ? 1 << 30 : 0);
        Store(sm0 + 0x04, execctrl);

        // SM0 SHIFTCTRL: AUTOPUSH[16], AUTOPULL[17], IN/OUT_SHIFTDIR[18/19], thresholds.
        int pushT = c.PushThreshold >= 32 ? 0 : c.PushThreshold;
        int pullT = c.PullThreshold >= 32 ? 0 : c.PullThreshold;
        int shiftctrl = (c.AutoPush ? 1 << 16 : 0) | (c.AutoPull ? 1 << 17 : 0)
                      | ((int)c.InShiftDir << 18) | ((int)c.OutShiftDir << 19)
                      | (pushT << 20) | (pullT << 25);
        Store(sm0 + 0x08, shiftctrl);

        // SM0 PINCTRL: bases + counts.
        int setCount = c.SetInitCount > 0 ? c.SetInitCount : (setBase >= 0 ? 1 : 0);
        int pinctrl = ((setBase < 0 ? 0 : setBase) << 5) | (setCount << 26)
                    | (outBase < 0 ? 0 : outBase) | (c.OutInitCount << 20)
                    | ((sideBase < 0 ? 0 : sideBase) << 10) | (c.SideSetInitCount << 29)
                    | ((inBase < 0 ? 0 : inBase) << 15);
        Store(sm0 + 0x14, pinctrl);

        // Enable state machine 0 (CTRL.SM_ENABLE bit 0). The SM now runs.
        Store(pio0 + 0x000, 1);

        return new Constant(0);
    }

    private int? TryConstInt(Expression e)
    {
        try { return EvaluateConstantExpr(e); }
        catch { return null; }
    }

    private Val VisitCall(CallExpr expr)
    {
        if (TryEmitPioStateMachine(expr) is { } pioResult) return pioResult;
        if (TryEmitSuperMethodCall(expr) is { } superResult) return superResult;

        // uart.write_str(f"...") / uart.println(f"...") with a runtime f-string: lower it to direct
        // stream writes instead of letting it reach the const[str] parameter (which would reject the
        // runtime interpolation). Same generic lowering as print().
        if (TryEmitStreamMethodFString(expr) is { } streamResult) return streamResult;
        if (TryEmitLcdMethodFString(expr) is { } lcdResult) return lcdResult;
        if (TryEmitDictMethod(expr) is { } dictResult) return dictResult;

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
                // A nested member access (obj.field.method()) yields a Temporary. If it carries a
                // class -- a ZCA field re-tagged with its nested class, like the DHT's
                // machine.Pin._pin -- dispatch the method on it exactly like a named instance by
                // treating the temp as a Variable. Without a class it falls through unchanged.
                if (objVal is Temporary tObj && instanceClasses.ContainsKey(tObj.Name))
                    objVal = new Variable(tObj.Name, tObj.Type);
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

                    // A nested ZCA field instance reached as a flattened global (e.g. a module-level
                    // `sensor._pin`, resolved to the name "sensor__pin") carries no class of its own.
                    // Recover it from the parent instance's class + the field's declared class so the
                    // method dispatches to <FieldClass>_<method>, not the nonexistent <name>_<method>.
                    if (!instanceClasses.TryGetValue(vObj.Name, out string clsC))
                    {
                        for (int si = 1; si < vObj.Name.Length - 1 && clsC == null; si++)
                        {
                            if (vObj.Name[si] != '_') continue;
                            string parent = vObj.Name.Substring(0, si);
                            string field = vObj.Name.Substring(si + 1);
                            if (!instanceClasses.TryGetValue(parent, out var pcls) || pcls == null)
                                continue;
                            // The field may be declared in a base class (e.g. DHTBase._pin reached
                            // on a DHT11 instance), so walk the MRO for its declared class.
                            for (string? anc = pcls; anc != null && clsC == null; )
                            {
                                if (fieldClasses.TryGetValue(anc + "|" + field, out var nfc)
                                    && ResolveConcreteClass(nfc) is { } ncc)
                                    clsC = ncc;
                                else if (classBasePrefixes.TryGetValue(anc, out var pp) && !string.IsNullOrEmpty(pp))
                                    anc = pp!.EndsWith("_") ? pp[..^1] : pp;
                                else break;
                            }
                        }
                        // Record it so the later self-binding (which re-resolves the receiver) also
                        // sees the class and binds self -- otherwise the @inline expansion drops the
                        // real first argument ("missing required argument").
                        if (clsC != null) instanceClasses[vObj.Name] = clsC;
                    }
                    if (clsC != null)
                    {
                        // A module-level singleton imported through a facade re-export carries the
                        // facade class name (e.g. "pymcu_hal_wifi_CYW43", which isn't itself defined);
                        // map it to the concrete class so the method resolves.
                        if (!classFieldLayout.ContainsKey(clsC) && ResolveConcreteClass(clsC) is { } concreteC)
                        {
                            clsC = concreteC;
                            instanceClasses[vObj.Name] = clsC;
                        }
                        // Walk MRO: find the class that actually defines the method so that
                        // inherited non-inline methods (e.g. DHTBase._read_byte called on a
                        // DHT11 instance) resolve to the correct label instead of the
                        // non-existent <ConcreteClass>_<method> symbol.
                        string definingClass = ResolveMROMethod(clsC!, memC.Member);
                        callee = definingClass + "_" + memC.Member;

                        // Virtual dispatch: an inherited method (defined in a base, reached on a
                        // subclass instance) that calls self.<m>() must be force-inlined, not run
                        // as the shared outlined body. The shared body bound self to the DEFINING
                        // class, so its self-call resolved statically (Shape.total() always ran
                        // Shape.unit, never the Square.unit override). Force-inlining rebinds self
                        // to the concrete instance so the inner call dispatches to the override.
                        // Same-class calls (clsC == definingClass) keep the shared outlined body.
                        bool needsVirtualInline = clsC != definingClass
                            && methodsWithSelfCall.Contains(callee);
                        if (needsVirtualInline && !inlineFunctions.ContainsKey(callee)
                            && methodAstByName.TryGetValue(callee, out var virtImpl))
                            inlineFunctions[callee] = virtImpl;

                        // RFC 0001 Model A: an @outline method is a shared subroutine, not
                        // inlined. Pass the instance's runtime field values as leading args
                        // (self_<field>), then the user args, and emit a real Call. One body,
                        // N call sites -- no per-instance bloat.
                        if (outlinedMethods.Contains(callee) && !needsVirtualInline)
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

                            // RFC 0001 (write-back): a single-field void mutator returns its
                            // (updated) field. The Python expression is still void, so emit the
                            // call into a temp, copy that temp back to the instance field, and
                            // yield None. The field's runtime home was promoted at construction
                            // (zcaWriteBackFields), so later reads -- including the next loop
                            // iteration -- pick up the new value.
                            if (outlineWriteBack.TryGetValue(callee, out var wb)
                                && !slotMethods.Contains(callee)
                                && !factoryHandleInstances.Contains(instName))
                            {
                                string fieldBase = instName;
                                while (fieldBase != null
                                       && variableAliases.TryGetValue(fieldBase, out var fa)) fieldBase = fa;
                                string fieldVar = fieldBase + "_" + wb.Field;

                                Temporary wDst = MakeTemp(wb.Type);
                                Emit(new Call(callee, oArgs, wDst));
                                Emit(new Copy(wDst, new Variable(fieldVar, wb.Type)));
                                constantVariables.Remove(fieldVar);
                                killedConstants.Add(fieldVar);
                                variableTypes[fieldVar] = wb.Type;
                                return new NoneVal();
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
                                expr.Line > 0 ? expr.Line : lastLine, 1);                        callee = vObj.Name + "_" + memC.Member;
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
            // the address (at the chip's native pointer width -- 32-bit on Cortex-M /
            // RISC-V, 16-bit on AVR) into a temp and mark it a runtime pointer; a
            // subsequent `.value` read/write lowers to Load/StoreIndirect.
            Val addrVal = VisitExpression(expr.Args[0]);
            DataType ptrTmpType = DataTypeExtensions.PointerWidth >= 4 ? DataType.UINT32 : DataType.UINT16;
            Temporary ptrTmp = MakeTemp(ptrTmpType);
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

            // The name failed to resolve because its defining module refused this target:
            // its module-level `raise CompileError(...)` guard survived if/match folding,
            // so none of its symbols were imported. Report the module author's message at
            // this use site instead of a misleading "undefined function".
            foreach (var g in moduleGuardErrors.OrderByDescending(kv => kv.Key.Length))
                if (callee.StartsWith(g.Key, StringComparison.Ordinal))
                    throw UserError($"{g.Value.Msg} (module guard at {g.Value.File}:{g.Value.Line})");

            // A known class invoked but with no __init__ to construct it — Python would use a
            // default constructor, which PyMCU does not synthesize. Be specific.
            if (classNames.Contains(callee) || classNames.Contains(shown))
                throw UserError(
                    $"class '{shown}' cannot be constructed: it has no __init__ method (PyMCU does " +
                    "not synthesize a default constructor — add `def __init__(self): ...`)");

            // `obj(args)` where obj is an instance whose class defines __call__: Python's
            // callable-object protocol. Dispatch it as the method call it stands for.
            if (expr.Callee is VariableExpr callableVe
                && TryResolveInstanceMethodAst(callableVe.Name, "__call__") != null)
                return VisitCall(new CallExpr(
                    new MemberAccessExpr(callableVe, "__call__"), expr.Args) { Line = expr.Line });

            // A known variable used as if it were callable (`x(3)` where x is a value).
            if (expr.Callee is VariableExpr cv)
            {
                string vq = !string.IsNullOrEmpty(currentInlinePrefix)
                    ? currentInlinePrefix + cv.Name
                    : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + cv.Name : cv.Name);
                if (variableTypes.ContainsKey(vq) || variableTypes.ContainsKey(cv.Name)
                    || mutableGlobals.ContainsKey(currentModulePrefix + cv.Name) || mutableGlobals.ContainsKey(cv.Name))
                    throw UserError($"'{shown}' is not callable (it is a value, not a function)");
            }

            // Reflection builtins: name the real reason instead of "undefined function".
            if (shown is "getattr" or "setattr" or "hasattr" or "delattr" or "eval" or "exec" or "vars" or "dir" or "globals" or "locals")
                throw UserError($"'{shown}' is runtime reflection, which PyMCU does not support " +
                                "(attributes are resolved at compile time); access the attribute directly, " +
                                "or dispatch on an explicit type-tag field");

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
                if (argVal is FlashStrAddr) ptype = FlashPtrType;

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

                // Coerce a scalar argument to the callee's DECLARED param width. The Call
                // instruction marshals by each arg Val's own type, so a wider temp (e.g. a
                // `pos + 1` inferred u32 passed to a uint16 param) shifts every later argument
                // out of its register slot on AVR -- the callee then reads garbage (this
                // silently dropped the buf pointer in strfmt._fs_i32 -> _fs_u32). Narrow (or
                // widen, with sign-correct Copy) into a temp of the param's type first.
                if (i < paramTypes.Count
                    && IsScalarIntType(ptype) && argVal is Variable or Temporary
                    && IsScalarIntType(GetValType(argVal))
                    && GetValType(argVal).SizeOf() != ptype.SizeOf())
                {
                    var coerced = MakeTemp(ptype);
                    Emit(new Copy(argVal, coerced));
                    argValuesL[i] = coerced;
                    argVal = coerced;
                }
                // Same for CONSTANT args wider params: a Constant's natural width is its
                // magnitude (65535 -> UINT16), so the backend would marshal fewer bytes
                // than the callee reads -- the high bytes arrive as register garbage
                // (surfaced by a uint32 param receiving a 16-bit-looking literal).
                else if (i < paramTypes.Count && IsScalarIntType(ptype) && argVal is Constant argC
                         && GetValType(argVal).SizeOf() < ptype.SizeOf())
                {
                    var coerced = MakeTemp(ptype);
                    Emit(new Copy(argC, coerced));
                    argValuesL[i] = coerced;
                    argVal = coerced;
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

        // Type the result temp with the callee's declared return type. Defaulting to uint8 lost
        // the signedness/width of a direct call result (e.g. `print(neg_of(x))` where neg_of
        // returns int8 picked the unsigned formatter, showing 251 instead of -5).
        DataType retDt = rType != null && rType.Length > 0
            ? DataTypeExtensions.StringToDataType(rType) : DataType.UINT8;
        Temporary dstC = MakeTemp(retDt);
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

        // `-> (T1, T2)` / `-> tuple[T1, T2]`: the arity is part of the signature, so a call
        // that unpacks a different number of targets is a mismatch worth naming here -- the
        // generic "Expected N tuple results, got M" fires far from the declaration.
        var declaredTupleElems = TupleType.ElementTypes(func?.ReturnType);
        if (declaredTupleElems.Count > 0)
        {
            string declared = TupleType.Describe(func!.ReturnType);
            if (pendingTupleCount == 0)
                throw UserError(
                    $"'{func.Name}' returns {declaredTupleElems.Count} values {declared}; " +
                    $"unpack them into {declaredTupleElems.Count} targets");
            if (pendingTupleCount != declaredTupleElems.Count)
                throw UserError(
                    $"'{func.Name}' is declared to return {declaredTupleElems.Count} values " +
                    $"{declared}, but {pendingTupleCount} unpack target(s) were given");
        }

        if (pendingTupleCount > 0)
        {
            string bBase = string.IsNullOrEmpty(currentFunction) ? "main" : currentFunction;
            for (int k = 0; k < pendingTupleCount; ++k)
            {
                string slot = $"{bBase}.iret_{newDepth}_{k}";
                tupleResultNames.Add(slot);
                // The annotated element type widens the result slot; without an annotation the
                // slot stays uint8, as it has always been. Slot names repeat across expansions
                // at the same depth, so an unannotated callee must clear a widened predecessor.
                if (k < declaredTupleElems.Count)
                    variableTypes[slot] = DataTypeExtensions.StringToDataType(declaredTupleElems[k]);
                else
                    variableTypes.Remove(slot);
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
                // The receiver may be a Temporary, not just a Variable -- e.g. a nested ZCA field
                // re-tagged with its class (machine.Pin._pin). Bind self off either, so a method
                // call on such a value still gets self (otherwise the @inline expansion reports
                // "missing required argument 'self'").
                string? recvName = objVal is Variable v2 ? v2.Name
                                 : (objVal is Temporary t2 ? t2.Name : null);
                if (recvName != null && instanceClasses.ContainsKey(recvName))
                {
                    string selfName = newPrefix + "self";
                    variableAliases[selfName] = recvName;
                    instanceClasses[selfName] = instanceClasses[recvName]!;
                    paramOffset = 1;
                }
            }
        }
        else paramOffset = 1;

        var kwArgValues = new Dictionary<string, Val>();
        var rawKwStrArgs = new Dictionary<string, string?>();
        var rawStrArgs = new List<StringLiteral?>();
        var rawListArgs = new List<ListExpr?>();

        foreach (var rawArg in expr.Args)
        {
            var arg = rawArg;
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
                arg = HoistSlotCtorArg(arg);
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

        // Register the callee's runtime-indexed local arrays so they are allocated as SRAM (not
        // register element-vars). An inlined fixed array is qualified with the enclosing function
        // (currentFunction), same as the load site, so scan under that prefix. The per-function
        // prescan only sees the caller's own body, never an inlined callee's locals, so without
        // this a runtime-indexed local array inside an @inline hit "subscript must be constant".
        if (func != null)
            ScanForVariableIndexedArrays(func.Body.Statements,
                string.IsNullOrEmpty(currentFunction) ? "" : currentFunction + ".");

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
            { ExitLabel = exitLabel, ResultTemp = result, ResultVars = tupleResultNames, CalleeName = callee,
              Prefix = newPrefix });

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
                // A Temporary that is a tagged class instance (e.g. a single-field ZCA field
                // re-tagged with its nested class) must carry that class onto the @inline param,
                // so the callee's `param.field`/`param.method()` resolves -- the param is bound by
                // a runtime Copy below (its own var), and alias-following stops at tmp_ names, so
                // the class would otherwise be lost.
                if (instanceClasses.TryGetValue(tArg.Name, out var tCls) && tCls != null)
                    instanceClasses[paramName] = tCls;
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
            else
            {
                // Required parameter with no argument and no default. Python raises TypeError;
                // reject clearly instead of leaving it uninitialised (read as 0) -- e.g. P(5)
                // for __init__(self, x, y) silently set y = 0.
                int initPos2 = callee.IndexOf("___init__", StringComparison.Ordinal);
                string what = initPos2 > 0
                    ? $"constructor of '{callee[..initPos2]}'"
                    : $"'{func.Name}'";
                throw UserError(
                    $"missing required argument '{func.Params[i].Name}' in call to {what} " +
                    $"(expects {func.Params.Count - paramOffset} argument(s))");
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

    // `f(Cls(...))` where Cls is boxed into an SRAM slot (RFC 0001 Model B): build the
    // instance into a named slot first and pass that name, which is exactly what
    // `x = Cls(...); f(x)` does. As an anonymous subexpression the constructor takes the
    // flattened (Model A) path instead, so the instance has no slot and an @outline method
    // compiled with the self-pointer ABI reads its fields from the wrong place -- how
    // `asyncio.gather(fast(), slow())` used to fail, since a coroutine state machine is
    // always a multi-field (slot) class.
    private Expression HoistSlotCtorArg(Expression arg)
    {
        if (arg is not CallExpr ctor || ctor.Callee is not VariableExpr ctorName) return arg;
        if (!slotClasses.Contains(ResolveCallee(ctorName.Name))) return arg;
        string bound = "__zca" + (++ctorAnonId);
        VisitAssign(new AssignStmt(new VariableExpr(bound), arg));
        return new VariableExpr(bound);
    }

    // `d.get(key, default)` on a dict-literal binding. The dict is a compile-time closed
    // table, so this is the d[key] lowering with the miss handed the default instead of a
    // KeyError. Returns null when the receiver is not a dict literal, so a real method call
    // on an instance is unaffected -- without this the call mangled into an undefined `d_get`.
    private Val? TryEmitDictMethod(CallExpr expr)
    {
        if (expr.Callee is not MemberAccessExpr { Object: VariableExpr dv } dm) return null;
        if (!TryGetDictBinding(dv.Name, out var dict)) return null;

        if (dm.Member != "get")
            throw UserError(
                $"dict literals are compile-time lookup tables: '{dm.Member}()' is not available. " +
                "Supported: d[key], key in d, len(d), d.get(key, default). For a mutable dict use " +
                "pymcu.collections.FixedDict(capacity).");

        if (expr.Args.Count != 2)
            throw UserError(
                "d.get(key, default) needs the default spelled out: PyMCU has no None value to " +
                "return for a missing key.");

        return EmitDictLookup(dict, expr.Args[0], expr.Args[1]);
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

                // The registered key spells parameter types RAW (BuildOverloadSuffix), while the
                // suffix built above spells them normalized -- so for any `const[...]` parameter
                // the exact lookup can never hit, and every such call lands here. Normalizing the
                // declared type is what lets the steps below compare like with like.
                static string NormType(string t)
                    => string.IsNullOrEmpty(t) ? "uint8"
                     : t.StartsWith("const[") && t.EndsWith("]") ? t[6..^1] : t;

                // 1) Exact arity, and the parameter types must MATCH the argument types. Arity
                //    alone used to be enough, which handed the call to whichever overload was
                //    declared first: Pin("RA4", Pin.OUT) picked the const[uint8] overload and
                //    died advising the caller to pass a port name -- which is what it had passed.
                string? pick = null;
                string? arityOnly = null;
                foreach (var kvp in inlineFunctions)
                {
                    if (!kvp.Key.StartsWith(callee + "___")) continue;
                    var ps = kvp.Value.Params.Where(p => p.Name != "self").ToList();
                    if (ps.Count != argCount) continue;
                    arityOnly ??= kvp.Key;
                    if (argCount == 0
                        || string.Join("_", ps.Select(p => NormType(p.Type))) == suffix)
                    {
                        pick = kvp.Key;
                        break;
                    }
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

                // Nothing matched on types: keep the old arity-only choice rather than failing to
                // resolve at all, so a call whose argument types we cannot name still compiles.
                pick ??= arityOnly;

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

        // When an OUTLINED method body is compiled standalone, currentModulePrefix is not the
        // class prefix, so derive the child class from the method's recorded instance type first
        // (this is what makes super().<method>() resolve inside an outlined override). Fall back
        // to currentModulePrefix for the inline construction path (super().__init__()).
        string childClass = methodInstanceTypes.TryGetValue(currentFunction, out var mitChild)
            ? mitChild
            : (string.IsNullOrEmpty(currentModulePrefix)
                ? ""
                : currentModulePrefix.Substring(0, currentModulePrefix.Length - 1));
        if (!classBasePrefixes.TryGetValue(childClass, out var basePrefix)) return null;

        var calleeSuper = basePrefix + mem.Member;
        // The base method may be @inline (in inlineFunctions) OR a default-outlined method
        // (only its AST is in instanceMethodDefs). Either way, expand its BODY in place with
        // self aliased to the current instance -- this sidesteps the outlined-call ABI and
        // works whether the OVERRIDING method is itself outlined or force-inlined. Before,
        // only an @inline base method resolved; a non-inline one fell through to an undefined
        // 'super' (super().<method>() only worked for __init__).
        if (!inlineFunctions.TryGetValue(calleeSuper, out var funcSuper)
            && !instanceMethodDefs.TryGetValue(calleeSuper, out funcSuper)
            && !methodAstByName.TryGetValue(calleeSuper, out funcSuper))
            return null;

        var exitLabel = MakeLabel();
        var newDepth = inlineDepth + 1;
        var newPrefix = $"inline{newDepth}_{funcSuper.Name}_";

        var selfAlias = currentInlinePrefix + "self";
        if (variableAliases.TryGetValue(selfAlias, out var vAlias))
            variableAliases[newPrefix + "self"] = vAlias;
        else if (!string.IsNullOrEmpty(pendingConstructorTarget))
            variableAliases[newPrefix + "self"] = pendingConstructorTarget;
        // Propagate the concrete instance type so the base body's self.<field> resolves.
        if (instanceClasses.TryGetValue(selfAlias, out var selfClsSuper) && selfClsSuper != null)
            instanceClasses[newPrefix + "self"] = selfClsSuper;

        var paramIdx = 0;
        foreach (var p in funcSuper.Params)
        {
            if (p.Name == "self") continue;
            if (paramIdx >= expr.Args.Count) continue;
            var argVal = VisitExpression(expr.Args[paramIdx]);
            var paramKey = newPrefix + p.Name;
            constantVariables.Remove(paramKey);
            variableAliases.Remove(paramKey);
            if (argVal is Constant cArg)
            {
                constantVariables[paramKey] = cArg.Value;
            }
            else
            {
                // Materialize the value into the param's own var (do NOT merely alias a
                // Variable arg). When the base __init__ field is later consumed by an outlined
                // (Model-A) method call, the field is read by literal name -- a compile-time
                // alias is never written, so it read 0 and a forwarded super().__init__(v, ...)
                // dropped the runtime `v` (constants survived, variables became 0).
                var paramVar = new Variable(paramKey,
                    DataTypeExtensions.StringToDataType(p.Type));
                Emit(new Copy(argVal, paramVar));
                variableTypes[paramKey] = DataTypeExtensions.StringToDataType(p.Type);
            }

            paramIdx++;
        }

        // A value-returning super method needs a result temp; the base body's `return`
        // copies into it via the inline frame's ResultTemp. Without this the value was lost.
        Temporary? superResult = null;
        if (funcSuper.ReturnType != "void" && funcSuper.ReturnType != "None")
            superResult = MakeTemp(DataTypeExtensions.StringToDataType(funcSuper.ReturnType));

        var savedPrefix = currentInlinePrefix;
        var savedMod = currentModulePrefix;
        var savedDepth = inlineDepth;

        currentInlinePrefix = newPrefix;
        currentModulePrefix = basePrefix;
        inlineDepth = newDepth;
        inlineStack.Add(new InlineContext { ExitLabel = exitLabel, ResultTemp = superResult });

        VisitBlock(funcSuper.Body);
        Emit(new Label(exitLabel));
        inlineStack.RemoveAt(inlineStack.Count - 1);

        currentInlinePrefix = savedPrefix;
        currentModulePrefix = savedMod;
        inlineDepth = savedDepth;
        return superResult ?? (Val)new NoneVal();
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

        // RFC 0001 (write-back), sibling case: the callee is a mutator that returns its
        // updated field because Model A passes the field BY VALUE. This method's own copy
        // lives in its field parameter, so the returned value has to land there -- otherwise
        // the sibling mutates a copy that dies with the call and the write is lost. (Model B
        // needs nothing: both share the slot the self pointer names.)
        if (!slotMethods.Contains(currentFunction)
            && outlineWriteBack.TryGetValue(target, out var swb))
        {
            Temporary swDst = MakeTemp(swb.Type);
            Emit(new Call(target, fwdArgs, swDst));
            Emit(new Copy(swDst, new Variable(currentFunction + ".self_" + swb.Field, swb.Type)));
            return new NoneVal();
        }

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
        // Type the result temp to the pointee's return width; without this a uint16/int16
        // return read only its low byte (the default uint8 temp).
        DataType retTy = funcrefReturnTypes.TryGetValue(fvKey, out var rt) ? rt : DataType.UINT8;
        Temporary indDst = MakeTemp(retTy);
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
        if (expr.Args[0] is TupleExpr te2) return new Constant(te2.Elements.Count);
        if (expr.Args[0] is Frontend.DictExpr de2) return new Constant(de2.Entries.Count);
        if (expr.Args[0] is Frontend.SetExpr se2) return new Constant(se2.Elements.Count);
        // A compile-time string constant (literal or a str / const[str] variable) has a
        // statically known length.
        if (expr.Args[0] is StringLiteral slLen) return new Constant(slLen.Value.Length);
        if (expr.Args[0] is VariableExpr vLen)
        {
            // A runtime string (f-string-as-value buffer): its length is the tracked write
            // position, not the buffer capacity that arraySizes would report below.
            if (TryGetRuntimeStr(vLen.Name, out var rsLen))
                return VisitExpression(new VariableExpr(rsLen.LenVar));

            // Dict/set literal bindings have a compile-time size.
            if (TryGetDictBinding(vLen.Name, out var dLen)) return new Constant(dLen.Entries.Count);
            if (TryGetSetBinding(vLen.Name, out var sLen)) return new Constant(sLen.Elements.Count);

            // An @inline parameter bound to a list/tuple literal argument has a
            // statically known length (e.g. len(prog) inside a HAL helper).
            if (ResolveListLiteralParam(vLen.Name) is ListExpr boundLen)
                return new Constant(boundLen.Elements.Count);
            if (!string.IsNullOrEmpty(currentInlinePrefix) &&
                arraySizes.TryGetValue(currentInlinePrefix + vLen.Name, out int s1)) return new Constant(s1);
            if (!string.IsNullOrEmpty(currentFunction) &&
                arraySizes.TryGetValue(currentFunction + "." + vLen.Name, out int s2)) return new Constant(s2);
            if (arraySizes.TryGetValue(vLen.Name, out int s3)) return new Constant(s3);

            string lenStrKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + vLen.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + vLen.Name : vLen.Name);
            if (ResolveStrConstant(lenStrKey) is string svLen) return new Constant(svLen.Length);

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
        if (expr.Args[0] is StringLiteral or FStringExpr)
            throw UserError("abs() argument must be numeric, not a string");
        var v = VisitExpression(expr.Args[0]);
        if (v is Constant c) return new Constant(c.Value < 0 ? -c.Value : c.Value);
        // The result carries the operand's width/signedness. A bare uint8 temp here
        // truncated abs() of any int16/int32 value (e.g. abs(-500) -> 244).
        DataType absType = GetValType(v);
        var negLabel = MakeLabel();
        var endLabel = MakeLabel();
        var result = MakeTemp(absType);
        var negv = MakeTemp();
        Emit(new Binary(BinaryOp.LessThan, v, new Constant(0), negv));
        Emit(new JumpIfNotZero(negv, negLabel));
        Emit(new Copy(v, result));
        Emit(new Jump(endLabel));
        Emit(new Label(negLabel));
        Temporary negResult = MakeTemp(absType);
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
        // Result holds whichever operand wins, so it must be at least as wide as the
        // wider operand; a bare uint8 temp truncated min/max of int16/int32 values.
        string elseLabel = MakeLabel();
        string endLabel = MakeLabel();
        Temporary result = MakeTemp(DataTypeExtensions.GetPromotedType(GetValType(a), GetValType(b)));
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
        // Result holds whichever operand wins, so it must be at least as wide as the
        // wider operand; a bare uint8 temp truncated min/max of int16/int32 values.
        var elseLabel = MakeLabel();
        var endLabel = MakeLabel();
        var result = MakeTemp(DataTypeExtensions.GetPromotedType(GetValType(a), GetValType(b)));
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
        Val v = VisitExpression(expr.Args[0]);
        // A char on an 8-bit target is a single byte; a compile-time argument outside 0..255
        // would otherwise pass through as a too-large Constant and be silently truncated.
        if (v is Constant c && (c.Value < 0 || c.Value > 255))
            throw new ValueError($"chr() arg not in range(256): {c.Value}",
                expr.Line > 0 ? expr.Line : lastLine, 1);
        return v;
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

                    var t = MakeTemp(DataTypeExtensions.GetPromotedType(GetValType(acc), GetValType(v)));
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

                // Read each element at the array's real element type; a hardcoded UINT8 here
                // truncated every element of a uint16/int16 array (and the running sum).
                DataType elemTy = arrayElemTypes.TryGetValue(arrBase, out var et) ? et : DataType.UINT8;
                Val acc = new Variable(arrBase + "__0", elemTy);
                for (int i = 1; i < arrSize; ++i)
                {
                    Val vi = new Variable(arrBase + "__" + i, elemTy);
                    Temporary t = MakeTemp(DataTypeExtensions.GetPromotedType(GetValType(acc), elemTy));
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
        { "int", DataType.INT16 }, { "float", DataType.FLOAT }
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
        // A string/f-string argument would otherwise fold to its flash string-id and be
        // silently used as that (wrong) integer; the whole assignment then gets dropped.
        if (expr.Args[0] is StringLiteral or FStringExpr)
            throw UserError($"{callee}() argument must be numeric, not a string");
        // Casting an arithmetic expression to an integer width is the explicit "compute at this
        // width" signal (fixed-width wrap + flags) -- the escape hatch from arithmetic promotion.
        // Hint the immediate binary op via castWidthHint (VisitBinary consumes/clears it).
        if (dstType is not DataType.FLOAT && expr.Args[0] is BinaryExpr)
            castWidthHint = dstType;
        Val v = VisitExpression(expr.Args[0]);
        castWidthHint = null;
        if (v is Constant c)
        {
            // float(int_literal) -> a float constant (e.g. float(5) -> 5.0).
            if (dstType == DataType.FLOAT) return new FloatConstant(c.Value);
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

        // float(float_const) is the identity.
        if (v is FloatConstant fcId && dstType == DataType.FLOAT) return fcId;

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

    // ── Generic stream lowering ──────────────────────────────────────────────────────────────
    // Shared by print() and by uart.write_str/println(f"..."). There are no UART-specific IR
    // instructions: text and values lower to ordinary Call instructions targeting the resolved
    // string/decimal/float write helpers, so any stream sink reuses the same machinery.

    // Resolve the target's string-write function: console.print_str, else uart_write_str, else a
    // module-suffixed variant injected by the build driver.
    private string ResolveWriteStrFn()
    {
        string writeStrFn = ResolveCallee("print_str");
        if (writeStrFn == "print_str")
        {
            writeStrFn = ResolveCallee("uart_write_str");
            if (writeStrFn == "uart_write_str")
                foreach (var fnName in inlineFunctions.Keys)
                    if (fnName.EndsWith("_print_str") || fnName.EndsWith("_uart_write_str")) { writeStrFn = fnName; break; }
        }
        return writeStrFn;
    }

    private string ResolveFloatWriteFn()
    {
        string floatWriteFn = ResolveCallee("uart_write_float");
        if (floatWriteFn == "uart_write_float")
            foreach (var fnName in functionReturnTypes.Keys)
                if (fnName.EndsWith("uart_write_float")) { floatWriteFn = fnName; break; }
        return floatWriteFn;
    }

    // Pick the decimal formatter (and the temp width to widen into) for a value's type, so a
    // uint16/uint32 argument is not silently truncated to 8 bits.
    private (string fn, DataType tmpType) ResolveDecimalWriteFn(DataType argType)
    {
        (string decBase, DataType tmpType) = argType switch
        {
            DataType.UINT16 => ("uart_write_decimal_u16", DataType.UINT16),
            DataType.INT16 => ("uart_write_decimal_i16", DataType.INT16),
            DataType.UINT32 => ("uart_write_decimal_u32", DataType.UINT32),
            DataType.INT32 => ("uart_write_decimal_i32", DataType.INT32),
            // int8 has no dedicated signed formatter: widen to int16 (the Copy sign-extends a
            // signed source) so a negative value prints with its sign, not as an unsigned byte.
            DataType.INT8 => ("uart_write_decimal_i16", DataType.INT16),
            _ => ("uart_write_decimal_u8", DataType.UINT8),
        };
        string decFn = ResolveCallee(decBase);
        if (decFn == decBase)
            foreach (var fnName in functionReturnTypes.Keys)
                if (fnName.EndsWith(decBase, StringComparison.Ordinal)) { decFn = fnName; break; }
        return (decFn, tmpType);
    }

    private void EmitStreamStr(string writeStrFn, string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        VisitCall(new CallExpr(new VariableExpr(writeStrFn), new List<Expression> { new StringLiteral(s) }));
    }

    // Interpolating an instance would need __str__ at runtime, which PyMCU has no room for:
    // the value that reaches the formatter is whatever scalar the instance collapsed to, so it
    // printed a meaningless number (0 for a multi-field class) with no warning at all.
    private void RejectInstanceInterpolation(Expression e)
    {
        if (e is not VariableExpr ve) return;
        if (InstanceClassOfName(ve.Name) is not { } cls) return;
        string shown = cls.Contains('_') ? cls[(cls.LastIndexOf('_') + 1)..] : cls;
        throw UserError(
            $"cannot interpolate '{ve.Name}', an instance of '{shown}': PyMCU resolves attributes " +
            "at compile time and has no runtime __str__. Interpolate a value instead, e.g. " +
            $"f\"{{{ve.Name}.<field>}}\" or a method that returns a number.");
    }

    // A bool value in the Python sense: a True/False literal, or a name bound to one
    // everywhere in the program. A comparison is deliberately NOT a bool here — PyMCU
    // lowers `a < b` to an integer, and printing it as True/False would misreport any
    // other integer flowing through the same name.
    private bool IsBoolExpr(Expression e) => e switch
    {
        BooleanLiteral => true,
        VariableExpr ve => IsBoolName(ve.Name),
        _ => false,
    };

    // Stream a runtime bool as Python spells it: the two words live in flash and the
    // branch picks one, so nothing is formatted at runtime.
    private void EmitStreamBool(string writeStrFn, Expression e)
    {
        var thenBranch = new Block();
        thenBranch.Statements.Add(new ExprStmt(new CallExpr(new VariableExpr(writeStrFn),
            new List<Expression> { new StringLiteral("True") })));
        var elseBranch = new Block();
        elseBranch.Statements.Add(new ExprStmt(new CallExpr(new VariableExpr(writeStrFn),
            new List<Expression> { new StringLiteral("False") })));
        VisitStatement(new IfStmt(
            new BinaryExpr(e, Frontend.BinaryOp.NotEqual, new IntegerLiteral(0)),
            thenBranch, null, elseBranch));
    }

    // Write an already-evaluated value to the stream as a number/float.
    private void EmitStreamVal(string floatFn, Val val)
    {
        bool isFloat = val is FloatConstant ||
                       (val is Variable vf && vf.Type == DataType.FLOAT) ||
                       (val is Temporary tf && tf.Type == DataType.FLOAT);
        if (isFloat)
        {
            Temporary ftmp = MakeTemp(DataType.FLOAT);
            Emit(new Copy(val, ftmp));
            Emit(new Call(floatFn, new List<Val> { ftmp }, ftmp));
            return;
        }
        DataType argType = val switch
        {
            Variable v2 => v2.Type,
            Temporary t2 => t2.Type,
            Constant cc => cc.Value < 0 ? DataType.INT16
                         : cc.Value <= 0xFF ? DataType.UINT8
                         : cc.Value <= 0xFFFF ? DataType.UINT16 : DataType.UINT32,
            _ => DataType.UINT8,
        };
        (string decFn, DataType tmpType) = ResolveDecimalWriteFn(argType);
        Temporary tmp = MakeTemp(tmpType);
        Emit(new Copy(val, tmp));
        Emit(new Call(decFn, new List<Val> { tmp }, tmp));
    }

    // Compile-time string text of an expression if it is statically a string (literal or const
    // string variable); else null. AST-based to avoid the string-id/int ambiguity of a Val.
    private string? StaticStringOf(Expression e)
    {
        if (e is StringLiteral sl) return sl.Value;
        if (e is VariableExpr ve) return ResolveStrConstant(currentInlinePrefix + ve.Name) ?? ResolveStrConstant(ve.Name);
        return null;
    }

    private string ResolveFmtFn()
    {
        string fn = ResolveCallee("uart_write_fmt");
        if (fn == "uart_write_fmt")
            foreach (var k in functionReturnTypes.Keys)
                if (k.EndsWith("uart_write_fmt", StringComparison.Ordinal)) { fn = k; break; }
        return fn;
    }

    // Parse the supported f-string format-spec subset: [0][width][type], type in d/x/X/b/o (c is
    // rejected for now). Returns the radix, field width, pad char and upper-case flag.
    private (int Width, int Base, char Pad, bool Upper) ParseFormatSpec(string spec)
    {
        int i = 0;
        char pad = ' ';
        if (i < spec.Length && spec[i] == '0') { pad = '0'; i++; }
        int width = 0;
        while (i < spec.Length && spec[i] is >= '0' and <= '9') { width = width * 10 + (spec[i] - '0'); i++; }
        char type = i < spec.Length ? spec[i++] : 'd';
        if (i != spec.Length)
            throw UserError($"unsupported f-string format spec ':{spec}'");
        int radix = type switch { 'd' => 10, 'x' or 'X' => 16, 'b' => 2, 'o' => 8, _ => -1 };
        if (radix < 0)
            throw UserError($"unsupported f-string format type '{type}' (supported: d, x, X, b, o)");
        return (width, radix, pad, type == 'X');
    }

    // Emit an interpolated value formatted per its spec, via the generic uart_write_fmt helper.
    private void EmitFormattedExpr(Expression e, string spec)
    {
        var (width, radix, pad, upper) = ParseFormatSpec(spec);
        Val v = VisitExpression(e);
        DataType vt = GetValType(v);
        if (vt == DataType.FLOAT || v is FloatConstant)
            throw UserError("f-string format spec is not supported for float values");

        bool signed = vt is DataType.INT8 or DataType.INT16 or DataType.INT32;
        // Pack the options into one flags byte: bit0 upper, bit1 signed, bit2 zero-pad. Keeping the
        // call to 4 args (int32 + 3 bytes) avoids losing trailing args in AVR argument passing.
        int flags = (upper ? 0x01 : 0) | (signed ? 0x02 : 0) | (pad == '0' ? 0x04 : 0);
        Temporary valArg = MakeTemp(DataType.INT32);   // widen (sign/zero-extend by source type)
        Emit(new Copy(v, valArg));
        string fmtFn = ResolveFmtFn();
        Emit(new Call(fmtFn, new List<Val>
        {
            valArg,
            new Constant(radix),
            new Constant(width),
            new Constant(flags),
        }, MakeTemp(DataType.UINT8)));
    }

    // Lower an f-string to direct stream writes: literal text and constant-string interpolations
    // coalesce into one write_str; a runtime value is emitted via its width-typed formatter. This
    // is the bare-metal equivalent of building the string — no buffer, only the itoa printing pays.
    private void EmitStreamFString(string writeStrFn, string floatFn, FStringExpr fs)
    {
        string pending = "";
        void Flush() { if (pending.Length > 0) { EmitStreamStr(writeStrFn, pending); pending = ""; } }
        foreach (var part in fs.Parts)
        {
            if (!part.IsExpr) { pending += part.Text; continue; }
            // A format spec (e.g. {reg:02x}) routes through the generic formatter.
            if (!string.IsNullOrEmpty(part.FormatSpec)) { Flush(); EmitFormattedExpr(part.Expr!, part.FormatSpec); continue; }
            if (part.Expr is FStringExpr nested) { Flush(); EmitStreamFString(writeStrFn, floatFn, nested); continue; }
            string? sv = StaticStringOf(part.Expr!);
            if (sv != null) { pending += sv; continue; }
            if (part.Expr is BooleanLiteral bl) { pending += bl.Value ? "True" : "False"; continue; }
            if (IsBoolExpr(part.Expr!)) { Flush(); EmitStreamBool(writeStrFn, part.Expr!); continue; }
            RejectInstanceInterpolation(part.Expr!);
            Flush();
            EmitStreamVal(floatFn, VisitExpression(part.Expr!));
        }
        Flush();
    }

    // uart.write_str(f"...") / uart.println(f"..."): lower the f-string straight to stream writes
    // (println appends a newline). Also accepts a runtime-string variable (an f-string-as-value
    // buffer), streamed up to its tracked length. Returns null when this is not a stream method
    // on a UART instance with such an argument, so the normal const[str] method path handles it.
    private Val? TryEmitStreamMethodFString(CallExpr expr)
    {
        if (expr.Callee is not MemberAccessExpr sm) return null;
        if (sm.Member is not ("write_str" or "println")) return null;
        if (expr.Args.Count != 1) return null;
        FStringExpr? sfs = expr.Args[0] as FStringExpr;
        (string Name, string LenVar)? runtimeStr = null;
        if (sfs == null)
        {
            if (expr.Args[0] is VariableExpr rv && TryGetRuntimeStr(rv.Name, out var ri))
                runtimeStr = (rv.Name, ri.LenVar);
            else return null;
        }
        if (sm.Object is not VariableExpr) return null;

        Val sObj = VisitExpression(sm.Object);
        if (sObj is not Variable svObj) return null;
        if (!instanceClasses.TryGetValue(svObj.Name, out var sCls) || !sCls.EndsWith("UART")) return null;

        string wfn = ResolveWriteStrFn();
        if (sfs != null)
        {
            string ffn = ResolveFloatWriteFn();
            EmitStreamFString(wfn, ffn, sfs);
        }
        else EmitRuntimeStrStream(runtimeStr!.Value.Name, runtimeStr.Value.LenVar);
        if (sm.Member == "println") EmitStreamStr(wfn, "\n");
        return new NoneVal();
    }

    private static bool IsScalarIntType(DataType t) => t is DataType.UINT8 or DataType.INT8
        or DataType.UINT16 or DataType.INT16 or DataType.UINT32 or DataType.INT32;

    // A name bound by TryExpandFStringValue (an f-string-as-value buffer), looked up with the
    // same qualification order the expansion used to register it.
    private bool TryGetRuntimeStr(string name, out (string LenVar, int Capacity) info)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix)
            && runtimeStrVars.TryGetValue(currentInlinePrefix + name, out info)) return true;
        if (!string.IsNullOrEmpty(currentFunction)
            && runtimeStrVars.TryGetValue(currentFunction + "." + name, out info)) return true;
        return runtimeStrVars.TryGetValue(name, out info);
    }

    // The per-byte UART writer (free `uart_write(b)` in every arch HAL), resolved like the
    // other streaming helpers: direct name, then suffix match over known functions.
    private string ResolveByteWriteFn()
    {
        string fn = ResolveCallee("uart_write");
        if (fn == "uart_write")
        {
            foreach (var k in functionReturnTypes.Keys)
                if (k.EndsWith("uart_write", StringComparison.Ordinal)) { fn = k; break; }
            if (fn == "uart_write")
                foreach (var k in inlineFunctions.Keys)
                    if (k.EndsWith("uart_write", StringComparison.Ordinal)) { fn = k; break; }
        }
        return fn;
    }

    // Stream a runtime string's bytes: `i = 0; while i < len: uart_write(buf[i]); i += 1`,
    // synthesized as AST so the normal call machinery handles the byte loads and the write.
    private void EmitRuntimeStrStream(string bufName, string lenVar)
    {
        string wfn = ResolveByteWriteFn();
        string idx = $"__fsp_{tempCounter++}";
        VisitStatement(new VarDecl(idx, "uint16", new IntegerLiteral(0)));
        var body = new Block();
        body.Statements.Add(new ExprStmt(new CallExpr(new VariableExpr(wfn),
            new List<Expression> { new IndexExpr(new VariableExpr(bufName), new VariableExpr(idx)) })));
        body.Statements.Add(new AssignStmt(new VariableExpr(idx),
            new BinaryExpr(new VariableExpr(idx), Frontend.BinaryOp.Add, new IntegerLiteral(1))));
        VisitStatement(new WhileStmt(
            new BinaryExpr(new VariableExpr(idx), Frontend.BinaryOp.Less, new VariableExpr(lenVar)), body));
    }

    // lcd.print_str(f"...") on an LCD-like instance: lower the f-string to method calls on the
    // SAME object — print_str("literal") for text, print_fmt(value, base, width, flags) for a
    // value — so the instance's pins flow through the @inline expansion. Returns null when this is
    // not an LCD f-string call (the normal const[str] path handles a plain string).
    private Val? TryEmitLcdMethodFString(CallExpr expr)
    {
        if (expr.Callee is not MemberAccessExpr sm) return null;
        if (sm.Member != "print_str") return null;
        if (expr.Args.Count != 1 || expr.Args[0] is not FStringExpr sfs) return null;
        if (sm.Object is not VariableExpr) return null;

        Val obj = VisitExpression(sm.Object);
        if (obj is not Variable vobj) return null;
        if (!instanceClasses.TryGetValue(vobj.Name, out var cls) || !cls.EndsWith("LCD")) return null;

        string pending = "";
        void FlushStr()
        {
            if (pending.Length == 0) return;
            VisitCall(new CallExpr(new MemberAccessExpr(sm.Object, "print_str"),
                new List<Expression> { new StringLiteral(pending) }));
            pending = "";
        }

        foreach (var part in sfs.Parts)
        {
            if (!part.IsExpr) { pending += part.Text; continue; }
            string? sv = StaticStringOf(part.Expr!);
            if (sv != null && string.IsNullOrEmpty(part.FormatSpec)) { pending += sv; continue; }

            int width = 0, radix = 10; char pad = ' '; bool upper = false;
            if (!string.IsNullOrEmpty(part.FormatSpec))
                (width, radix, pad, upper) = ParseFormatSpec(part.FormatSpec);
            if (InferExprType(part.Expr!) == DataType.FLOAT)
                throw UserError("f-string format on an LCD is not supported for float values");
            bool signed = InferExprType(part.Expr!) is DataType.INT8 or DataType.INT16 or DataType.INT32;
            int flags = (upper ? 0x01 : 0) | (signed ? 0x02 : 0) | (pad == '0' ? 0x04 : 0);

            FlushStr();
            VisitCall(new CallExpr(new MemberAccessExpr(sm.Object, "print_fmt"),
                new List<Expression> { part.Expr!, new IntegerLiteral(radix), new IntegerLiteral(width), new IntegerLiteral(flags) }));
        }
        FlushStr();
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

        // Resolve the target's write helpers once; the lowering itself is the shared, sink-agnostic
        // machinery (print, uart.write_str/println all reuse it — see the EmitStream* methods).
        string writeStrFn = ResolveWriteStrFn();
        string floatWriteFn = ResolveFloatWriteFn();

        void EmitPrintArg(Expression arg)
        {
            // f-string -> stream: lower each part to a direct write.
            if (arg is FStringExpr fs)
            {
                EmitStreamFString(writeStrFn, floatWriteFn, fs);
                return;
            }

            // A plain string literal or const-string variable: one write_str.
            string? staticStr = StaticStringOf(arg);
            if (staticStr != null)
            {
                EmitStreamStr(writeStrFn, staticStr);
                return;
            }

            // A runtime string (f-string-as-value buffer): stream its bytes up to the
            // tracked length.
            if (arg is VariableExpr rsv && TryGetRuntimeStr(rsv.Name, out var rsInfo))
            {
                EmitRuntimeStrStream(rsv.Name, rsInfo.LenVar);
                return;
            }

            if (arg is BooleanLiteral pbl) { EmitStreamStr(writeStrFn, pbl.Value ? "True" : "False"); return; }
            if (IsBoolExpr(arg)) { EmitStreamBool(writeStrFn, arg); return; }

            RejectInstanceInterpolation(arg);
            EmitStreamVal(floatWriteFn, VisitExpression(arg));
        }

        if (posArgs.Count == 0)
        {
            EmitStreamStr(writeStrFn, endStr);
            return new NoneVal();
        }

        for (int i = 0; i < posArgs.Count; ++i)
        {
            if (i > 0) EmitStreamStr(writeStrFn, sepStr);
            EmitPrintArg(posArgs[i]);
        }

        EmitStreamStr(writeStrFn, endStr);
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
            case BinaryExpr be:
            {
                // Full constant arithmetic on address expressions, so an unrolled
                // address like `BASE + 4 * i` (i a loop/inline constant) folds to a
                // single constant MemoryAddress instead of degrading to a runtime
                // (truncated) pointer. Add/Sub recurse to preserve register-symbol
                // resolution on either side; the rest fold both operands.
                if (be.Op is PyMCU.Frontend.BinaryOp.Add or PyMCU.Frontend.BinaryOp.Sub)
                {
                    if (TryEvalConstAddress(be.Left) is not int l) return null;
                    if (TryEvalConstAddress(be.Right) is not int r) return null;
                    return be.Op == PyMCU.Frontend.BinaryOp.Add ? l + r : l - r;
                }
                if (TryEvalConstAddress(be.Left) is not int lh) return null;
                if (TryEvalConstAddress(be.Right) is not int rh) return null;
                return be.Op switch
                {
                    PyMCU.Frontend.BinaryOp.Mul      => lh * rh,
                    PyMCU.Frontend.BinaryOp.Div      => rh != 0 ? lh / rh : (int?)null,
                    PyMCU.Frontend.BinaryOp.FloorDiv => rh != 0 ? lh / rh : (int?)null,
                    PyMCU.Frontend.BinaryOp.Mod      => rh != 0 ? lh % rh : (int?)null,
                    PyMCU.Frontend.BinaryOp.BitAnd   => lh & rh,
                    PyMCU.Frontend.BinaryOp.BitOr    => lh | rh,
                    PyMCU.Frontend.BinaryOp.BitXor   => lh ^ rh,
                    PyMCU.Frontend.BinaryOp.LShift   => lh << rh,
                    PyMCU.Frontend.BinaryOp.RShift   => lh >> rh,
                    _ => null,
                };
            }
            case UnaryExpr ue when ue.Op is PyMCU.Frontend.UnaryOp.Negate or PyMCU.Frontend.UnaryOp.BitNot:
            {
                if (TryEvalConstAddress(ue.Operand) is not int v) return null;
                return ue.Op == PyMCU.Frontend.UnaryOp.Negate ? -v : ~v;
            }
            case VariableExpr ve:
                // Loop-unroll / inline compile-time constants first (same keys as
                // EvaluateConstantExpr): an unrolled `i` must resolve so `BASE + 4*i`
                // folds to a constant address.
                if (constantVariables.TryGetValue(currentInlinePrefix + ve.Name, out int cvip)) return cvip;
                if (!string.IsNullOrEmpty(currentFunction) &&
                    constantVariables.TryGetValue(currentFunction + "." + ve.Name, out int cvf)) return cvf;
                if (constantVariables.TryGetValue(ve.Name, out int cvb)) return cvb;
                // Otherwise a const folds to a Constant; a register/MMIO symbol resolves
                // to a MemoryAddress. Name resolution is side-effect free (no IR emitted).
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

        // Allocate the new buffer. CAUTION: GcAlloc may trigger a collection that compacts the
        // heap and RELOCATES the existing list, updating listVar (a tracked GC root) to its new
        // address. A pointer to the old buffer captured BEFORE this alloc would dangle, so the
        // copy source is re-derived from listVar AFTER the alloc.
        Temporary newPtr = MakeTemp(DataType.GC_REF);
        Emit(new GcAlloc(newAllocSize, newPtr));

        // Write new header
        EmitListStore(newPtr, 0, tmpLen);
        EmitListStore(newPtr, 1, newCap);

        // Copy existing elements byte-by-byte from the (possibly relocated) old buffer at listVar.
        // Compute base pointers outside the loop
        Val oldPtrU16 = listVar with { Type = DataType.UINT16 };
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

        // Repoint EVERY alias at the new buffer, not just listVar: a relocation must update all
        // GC_REF variables that hold the old address (Python list aliasing semantics). gc_list_fixup
        // walks the shadow stack rewriting old->new; passing listVar captures the old address in
        // registers before the routine overwrites listVar's own slot.
        Emit(new Call("gc_list_fixup", new List<Val> { listVar, newPtr }, new NoneVal()));

        // === FAST PATH: write element at offset 2 + len * elemSize ===
        Emit(new Label(fastLabel));

        Val elemVal = VisitExpression(valExpr);
        Temporary appendAddr = EmitElemAddr(listVar, tmpLen, elemSize);
        Emit(new StoreIndirect(elemVal, appendAddr, elemDt));

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
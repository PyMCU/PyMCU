using System.IO;
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
            throw UserError("PIO StateMachine MVP supports state machine 0 only (sm_id=0)", expr.Callee);

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

    /// <summary>
    /// Thin wrapper around <see cref="VisitCallCore"/>: while a call's callee is `<x>.__get__`
    /// or `<x>.__set__`, marks that `<x>` is being evaluated as a descriptor's bound `self`,
    /// not as a plain value read.
    ///
    /// The descriptor protocol (#360/#419) is spelled `Dev.reg.__get__(d, Dev)` explicitly and
    /// by hand as often as it is reached implicitly through `d.reg` -- adafruit_register users
    /// and the compiler's own descriptor rewrite both write exactly this shape, `<ClassName>.
    /// <attr>` immediately followed by `.__get__(...)`/`.__set__(...)`. Reading `<ClassName>.
    /// <attr>` any OTHER way -- as a final value, with no `__get__`/`__set__` call riding on it
    /// -- is the class-level descriptor read CPython answers with `type(attr).__get__(attr,
    /// None, ClassName)` and PyMCU refuses by name (VisitMemberAccess). Without this flag that
    /// refusal could not tell "the user is about to call __get__ themselves" from "the user
    /// wants Box.value as a value", and rejected the former along with the latter.
    /// </summary>
    private Val VisitCall(CallExpr expr)
    {
        bool savedSelfRewrite = insideDescriptorSelfRewrite;
        if (expr.Callee is MemberAccessExpr { Member: "__get__" or "__set__" })
            insideDescriptorSelfRewrite = true;
        try
        {
            return VisitCallCore(expr);
        }
        finally { insideDescriptorSelfRewrite = savedSelfRewrite; }
    }

    private Val VisitCallCore(CallExpr expr)
    {
        // The return-list bookkeeping below belongs to THIS call: a builtin or method
        // path that never sets it must not inherit the previous call's text (a
        // `n = len(p)` after `p = inner() -> list[T]` otherwise registers n as a
        // list). A nested call in an argument position re-clears and re-sets it, and
        // the outer call's own emission sets it after its args -- the outermost
        // call's text is the one that survives for the assignment to read.
        lastCallReturnTypeText = null;
        lastCallReturnTypeExpr = null;
        lastCallReturnListElem = null;
        lastCallReturnedBufferLocal = false;

        // `f(*xs)` and `f(**d)`: splice the elements of the compile-time sequence and the
        // entries of the compile-time mapping into the argument list before ANY path looks at
        // it, so every one of them sees an ordinary call.
        //
        // This used to sit below `TryEmitSuperMethodCall`, which meant the one call shape that
        // most needs it -- `super().__init__(pin, **kwargs)`, the whole point of #368 -- went
        // down the super path with the unspliced node and bound `**kwargs` as an opaque
        // positional argument. Splicing is a rewrite of the argument list and belongs before
        // the callee is chosen, not after.
        if (expr.Args.Any(a => a is StarArgExpr or DoubleStarArgExpr))
            expr = new CallExpr(expr.Callee, SpliceVariadicArgs(expr.Args)) { Line = expr.Line };

        // `return cls()` inside a @classmethod: cls is the receiver class, not a value.
        if (expr.Callee is VariableExpr clsCtorVe
            && ClassmethodClsOf(clsCtorVe.Name) is { } clsCtorMapped)
            expr = new CallExpr(new VariableExpr(clsCtorMapped), expr.Args)
                { Line = expr.Line, Column = expr.Column, Length = expr.Length };

        // RFC 0012: `Timer1()` on a grouped peripheral. A register group is a namespace over
        // the silicon, not a type: no fields, no constructor, no instance. The call used to be
        // accepted and produce nothing at all -- the name still resolved to the group, so the
        // register accesses that followed worked and the meaningless call went unsaid. Refused
        // here, before any constructor path can claim it, because the stable surface should not
        // accept a call that means nothing.
        // ResolveCallee is what decides, and its first question is whether the module being
        // lowered defines a class of that name, so a user's own `class Timer1` shadows the
        // chip's exactly as it does in Python and never reaches this refusal.
        if (expr.Callee is VariableExpr regGroupVe
            && registerGroupClasses.Contains(ResolveCallee(regGroupVe.Name)))
            throw UserError(
                $"'{regGroupVe.Name}' is a peripheral's registers, not a class to instantiate. It "
                + "is named like a type because PEP 8 names classes that way, and it is a namespace "
                + "over the silicon: no fields, no constructor, nothing to construct. Drop the call "
                + $"and reach the registers through the name itself ({regGroupVe.Name}.<REGISTER>"
                + $".value, {regGroupVe.Name}.<REGISTER>[bit]).",
                expr.Callee);

        if (TryEmitCompileTimeSetattr(expr) is { } setattrResult) return setattrResult;
        if (TryEmitPioStateMachine(expr) is { } pioResult) return pioResult;
        if (TryEmitSuperMethodCall(expr) is { } superResult) return superResult;
        if (TryEmitUnboundClassMethodCall(expr) is { } unboundResult) return unboundResult;

        // uart.write_str(f"...") / uart.println(f"...") with a runtime f-string: lower it to direct
        // stream writes instead of letting it reach the const[str] parameter (which would reject the
        // runtime interpolation). Same generic lowering as print().
        if (TryEmitStreamMethodFString(expr) is { } streamResult) return streamResult;
        if (TryEmitLcdMethodFString(expr) is { } lcdResult) return lcdResult;
        if (TryEmitDictMethod(expr) is { } dictResult) return dictResult;
        if (TryEmitSetMethod(expr) is { } setResult) return setResult;

        string callee = "";
        if (expr.Callee is VariableExpr varE)
        {
            // A name bound to a function by `f = a` calls that function directly.
            string fnAliasKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + varE.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + varE.Name : varE.Name);
            callee = loopFunctionAliases.TryGetValue(fnAliasKey, out var boundFn)
                ? boundFn
                : ResolveCallee(varE.Name);
        }
        else if (expr.Callee is MemberAccessExpr memC)
        {
            bool resolvedAsModule = false;

            // Methods on a 2-D grid or one of its rows: the grid is a flat fixed
            // array and a row is a view -- neither has methods. Refused before
            // module/instance dispatch can read the receiver and report an
            // unrelated problem (`g[y].append` used to land in list dispatch).
            if (memC.Object is IndexExpr { Index: not SliceExpr and not TupleExpr } methRowIx
                && ResolveGridKey(methRowIx.Target) != null)
                throw UserError(
                    $"a row of a 2-D grid is not a list -- it has no '{memC.Member}()' " +
                    "method. Index its elements (g[y][x]) or write the loop.", memC);
            if (memC.Object is VariableExpr methRowVe && ResolveRowRef(methRowVe) != null)
                throw UserError(
                    $"a row of a 2-D grid is not a list -- it has no '{memC.Member}()' " +
                    "method. Index its elements (r[x]) or write the loop.", memC);
            if (ResolveGridKey(memC.Object) != null)
                throw UserError(
                    $"a 2-D grid is a flat fixed array -- it has no '{memC.Member}()'; " +
                    "its size and rows are fixed at compile time", memC);

            // RFC 0001 Model B (Class[N]): arr[i].method() dispatch.
            if (TryEmitInstanceArrayMethodCall(expr, memC) is { } iaResult) return iaResult;

            // A ZCA instance array indexed with a run-time value: each element is a distinct
            // compile-time instance, so there is nothing to index. Select among them instead --
            // exactly the if/elif the compiler tells users to write elsewhere, generated here.
            if (TryEmitUnrolledInstanceArrayCall(expr, memC) is { } selResult) return selResult;

            // self.method(args) inside an outlined method: call the sibling outlined method.
            if (TryEmitSelfOutlinedMethodCall(expr, memC) is { } selfResult) return selfResult;

            // RFC 0008: read/seek/tell/close on a romfs handle are lowered here, before
            // module and instance dispatch -- they are intrinsics over a compile-time
            // object, not functions any lookup could find.
            if (ResolveRomfsHandleExpr(memC.Object) is { } romHandle)
                return EmitRomfsMethod(romHandle, expr, memC);

            if (memC.Object is VariableExpr ve)
            {
                // A name bound to an instance is that instance, even when a
                // module alias shares the name: the assignment shadows the
                // import, as it does in Python (#467). Reading it as the
                // module mangled `servo.fraction` to
                // `adafruit_motor_servo_fraction` and `servo.set_pulse_width_range`
                // to a free function of that name.
                if (NamesAModuleMember(ve.Name, memC.Member))
                {
                    // Mangle with the real module name, not the alias: `import time as t`
                    // registers modules["t"] but compiles functions as time_sleep_ms.
                    string realMod = TryImportedAlias(ve.Name, out var rm) && rm != null ? rm : ve.Name;
                    // RFC 0008: os.stat/os.listdir on the embedded-file table are
                    // compile-time answers, not module functions -- the table exists
                    // only while the program is being built.
                    if (realMod.Replace('.', '_') is "os" or "uos" or "pymcu_os")
                    {
                        if (memC.Member == "stat") return EmitOsStat(expr);
                        if (memC.Member == "listdir") return EmitOsListdir(expr);
                    }
                    string mangledMod = realMod.Replace('.', '_');
                    string modFn = mangledMod + "_" + memC.Member;

                    // A package that only RE-EXPORTS the callee: `pkg/__init__.py` doing
                    // `from pkg.mod import f` compiles f under the DEFINING module, so
                    // `pkg.f()` has no `pkg_f` to reach and reported it as undefined. The
                    // member read of the same name already chased this (`usys.maxsize`);
                    // the call did not, so `import pkg` bound a package whose functions
                    // could be read and not called (#468).
                    if (!inlineFunctions.ContainsKey(modFn) && !overloadedFunctions.Contains(modFn)
                        && !methodAstByName.ContainsKey(modFn) && !functionReturnTypes.ContainsKey(modFn)
                        && TryResolveModuleReExport(mangledMod, memC.Member, out var reExportedFn)
                        && (inlineFunctions.ContainsKey(reExportedFn)
                            || overloadedFunctions.Contains(reExportedFn)
                            || methodAstByName.ContainsKey(reExportedFn)
                            || functionReturnTypes.ContainsKey(reExportedFn)
                            || classNames.Contains(reExportedFn)
                            || inlineFunctions.ContainsKey(reExportedFn + "___init__")
                            || overloadedFunctions.Contains(reExportedFn + "___init__")
                            || methodAstByName.ContainsKey(reExportedFn + "___init__")))
                    {
                        modFn = reExportedFn;
                    }

                    // The module's own definition wins over the builtin fallback:
                    // `math.pow` must reach `math`'s software-float pow, not the
                    // constant-integer builtin, once the module defines it. A builtin the
                    // module does NOT define (`c.print` on pymcu.hal.console) still
                    // flattens to the intrinsic the compiler lowers itself.
                    if (inlineFunctions.ContainsKey(modFn) || overloadedFunctions.Contains(modFn)
                        || methodAstByName.ContainsKey(modFn) || functionReturnTypes.ContainsKey(modFn))
                    {
                        callee = modFn;
                    }
                    else if (intrinsicNames.Contains(memC.Member))
                    {
                        callee = memC.Member;
                    }
                    else
                    {
                        callee = modFn;
                    }
                    resolvedAsModule = true;
                }
                // A name bound to an instance is that instance, even when a class shares the
                // name: the binding shadows the class, as it does in Python. Reading it as the
                // class built a receiver-less `<module>_<name>_<member>` and reported it as an
                // undefined function. asyncio.gather's parameters are `a` and `b`, and the async
                // desugar names each coroutine's state-machine class after the coroutine, so
                // `gather(a(), b())` hit this for any user coroutine called a or b.
                else if (classNames.Contains(ve.Name) && InstanceClassOfName(ve.Name) == null)
                {
                    // Resolve the CLASS, do not paste the enclosing prefix in front of it.
                    // currentModulePrefix inside a method is the enclosing class, so
                    // `Base.read(...)` written inside Registry produced `Registry_Base_read`, a
                    // name nothing emits and nothing in the source is called (PyMCU#161). The
                    // same call in a free function worked only because the prefix was empty
                    // there, so the enclosing scope decided, not the construct. ResolveCallee
                    // walks the prefix chain and finds the class wherever it is defined, which
                    // is what every other class lookup on this path already uses.
                    callee = ResolveCallee(ve.Name) + "_" + memC.Member;
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
                // `self.pin_mapping.analog_pins.index(pin)`: the field holds a class
                // OBJECT, so the sequence lives on each candidate class and the field's
                // tag byte picks which one's index applies at run time.
                if (memC.Member == "index" && memC.Object is MemberAccessExpr coIdxOuter
                    && coIdxOuter.Object is MemberAccessExpr coIdxInner
                    && ClassObjectFieldClasses(coIdxInner) is { } idxCands)
                    return EmitClassObjectAttrIndex(expr, memC, coIdxInner,
                                                    coIdxOuter.Member, idxCands);

                // `seq.index(x)` on a tuple/list literal or a name bound to one: the
                // elements are compile-time expressions, so the call resolves to a
                // constant when x folds and a compare chain when it does not -- either
                // way no free-function fallback like `main__GAINS_index` is minted.
                if (memC.Member == "index"
                    && (memC.Object switch
                    {
                        TupleExpr t => t.Elements,
                        ListExpr l => l.Elements,
                        _ => ResolveConstSequenceExpr(memC.Object)
                    }) is { } idxElems)
                    return EmitConstSeqIndex(expr, memC, idxElems);

                // A generator constructed HERE is the receiver of a method call, and the
                // protocol check below (RejectGeneratorProtocol) gives a better answer for that
                // shape than the value-position refusal does: `counter().send(1)` should be told
                // that send() does not exist, not that a generator cannot be a value. So the
                // construction is allowed to complete, and the method call is what is refused.
                //
                // Found by CallColumnTests.TheGeneratorProtocol_PointsAtTheMethod, which is
                // pinned on that column. Without the flag the value-position check fired first
                // and replaced a specific diagnostic with a general one.
                bool prevRecv = loweringMemberReceiver;
                loweringMemberReceiver = true;
                Val objVal;
                try { objVal = VisitExpression(memC.Object); }
                finally { loweringMemberReceiver = prevRecv; }
                // A nested member access (obj.field.method()) yields a Temporary. If it carries a
                // class -- a ZCA field re-tagged with its nested class, like the DHT's
                // machine.Pin._pin -- dispatch the method on it exactly like a named instance by
                // treating the temp as a Variable. Without a class it falls through unchanged.
                //
                // `owner.q.bump()` where `q` is a @property returning a single-field ZCA
                // instance (#445): the getter's result ALIASES the field's own flattened
                // storage (`variableAliases["tmp_N"] == "owner__q"`) rather than carrying a
                // class directly -- ResolveClassCarryingName walks that chain; a direct
                // instanceClasses check on the temp's own name, one hop short, does not.
                if (objVal is Temporary tObj && ResolveClassCarryingName(tObj) is { } tObjClassName)
                    objVal = new Variable(tObjClassName, tObj.Type);
                // A receiver that is an object-typed FIELD (a coroutine's `self.a = Acc(s)`)
                // evaluates to a nameless anchor: the field owns no scalar of its own, only
                // the flattened `<anchor>_<member>` names under it. Its NAME is still the
                // instance the method dispatches on -- hand the dispatch a Variable carrying
                // it, the same way the Temporary re-tag above hands it a class-carrying one.
                if (objVal is not Variable && objVal is not Temporary
                    && AnchorNameOf(memC.Object) is { } anchorRecv)
                    objVal = new Variable(anchorRecv, DataType.UINT8);
                if (objVal is Variable vObj)
                {
                    // `buf.extend(...)` on a fixed-size buffer grows it while compiling (#362).
                    // Asked before the list dispatch below, because a bytearray is not a list and
                    // was falling through to the message written for an untyped one.
                    //
                    // Keyed off the SOURCE name, the way len() is, and not off the lowered
                    // variable's: a buffer declared in an imported module is registered under the
                    // spelling the module wrote, while the value it lowers to carries the
                    // qualified one, so matching on the lowered name saw the single-module case
                    // and missed every library.
                    if (memC.Member == "extend" && ResolveBufferKey(memC.Object) is { } bufKey)
                        return EmitBufferExtend(bufKey, expr, memC);

                    // list[T] method dispatch. The receiver's storage key is the resolved
                    // name (a bound `list[T]` parameter aliases the caller's list), and
                    // the emitted variable must be typed GC_REF: ResolveBinding hands a
                    // module-level list back as UNKNOWN, and a 1-byte pointer operand
                    // makes LoadIntoReg drop the pointer's high byte -- the header ops
                    // then hit a shadow address in low SRAM instead of the list.
                    string listRecvKey = ResolveNameKey(vObj.Name);
                    if (listVarElemTypes.ContainsKey(listRecvKey))
                    {
                        Variable listVar = new Variable(listRecvKey, DataType.GC_REF);
                        switch (memC.Member)
                        {
                            case "append" when expr.Args.Count == 1:
                                return EmitListAppend(listVar, expr.Args[0]);
                            default:
                                throw UserError($"list.{memC.Member}(): method not supported", memC);
                        }
                    }

                    // A name bound by `with C(...) as v:` is a pure alias of the manager: nothing
                    // is registered under v, so this lookup found no class and the call degraded
                    // into a free function called `v_method`. Field access through v always
                    // worked, because that path walks the alias chain. Give v the class the chain
                    // ends at, rather than swapping the receiver for the manager: the self-binding
                    // further down re-resolves the receiver from the AST, where the name is still
                    // v, and the field reads it emits follow the chain on their own (#305).
                    if (!instanceClasses.ContainsKey(vObj.Name)
                        && AliasedInstanceName(vObj.Name) is { } aliasedRecv
                        && instanceClasses.TryGetValue(aliasedRecv, out var aliasedCls))
                        instanceClasses[vObj.Name] = aliasedCls;

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
                        // A generator's class is synthesized and carries only __init__ and
                        // poll(), so every method of the generator protocol resolved to a
                        // symbol built from the generator's own name ('gen_send') and was
                        // reported as undefined, with "(typo, or a missing import?)" pointing
                        // at neither of the two true things: the method is real Python, and it
                        // is the feature that is missing.
                        if (generatorClasses.Contains(clsC!))
                            RejectGeneratorProtocol(clsC!, memC);

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
                        // A slot method reads and writes its fields through a `self` POINTER, so
                        // it can only be called on an instance that has a slot. A nested instance
                        // (`self.inner = Inner()`) is built flattened, field by field, and has
                        // none: the call site passed the field VALUES where the body expected the
                        // address and the callee wrote its state through a null pointer, with no
                        // diagnostic. Fall through to the force-inline path, which expands the
                        // body against the flattened fields.
                        string outlinedInst = objVal is Variable ivName ? ivName.Name : "";
                        bool slotAbiUnavailable = slotMethods.Contains(callee)
                            && !slotInstances.ContainsKey(outlinedInst)
                            && !factoryHandleInstances.Contains(outlinedInst);
                        if (slotAbiUnavailable && !inlineFunctions.ContainsKey(callee)
                            && (instanceMethodDefs.TryGetValue(callee, out var slotImpl)
                                || methodAstByName.TryGetValue(callee, out slotImpl)))
                            inlineFunctions[callee] = slotImpl;

                        if (outlinedMethods.Contains(callee) && !needsVirtualInline
                            && !slotAbiUnavailable)
                        {
                            if (instanceMethodDefs.TryGetValue(callee, out var oShapeDef)
                                || methodAstByName.TryGetValue(callee, out oShapeDef))
                                CheckSignatureShape(oShapeDef, expr.Args,
                                    QualNameForCall(oShapeDef, callee), expr);
                            var oArgs = new List<Val>();
                            string instName = outlinedInst;
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
                                        : CoerceOutlinedArg(callee, oArgs.Count,
                                            VisitExpression(new MemberAccessExpr(memC.Object, fld))));
                            }
                            {
                                functionParams.TryGetValue(callee, out var oKwPnames);
                                int oKwSelf = functionParamSelfCount.GetValueOrDefault(callee);
                                int oKwPos = 0;
                            foreach (var a in expr.Args)
                            {
                                RefuseGridArgument(a);
                                // The parameter this argument binds: a keyword by name,
                                // a positional behind the leading self-derived params.
                                int oPidx = a is KeywordArgExpr oKwA && oKwPnames != null
                                    ? oKwPnames.IndexOf(oKwA.Key) : oKwSelf + oKwPos++;
                                // RFC 0009: a tagged parameter reads the argument's tag
                                // byte, so a live Optional's bare-name read is allowed here.
                                bool oArgTagged = IsTaggedParam(callee, oPidx);
                                if (oArgTagged) optionalReadAllowed++;
                                Val av;
                                try
                                {
                                    av = TryEvalInlineBufferArg(a)
                                        ?? TryEvalLiteralBufferArg(a) ?? VisitExpression(a);
                                }
                                finally { if (oArgTagged) optionalReadAllowed--; }
                                av = CoerceOutlinedArg(callee, oPidx, av);
                                // An array var / field (`self.temp` -> `d_temp`) marshals as
                                // its base, same as the bare-name path in argValuesL -- a
                                // Variable copies the first byte where a pointer is needed.
                                // Only a contiguous array has a base label; a flat sequence
                                // (slice temp) is in arraySizes too but has no `s:` storage.
                                if (av is Variable oArrayVar
                                    && TryResolveArrayStorageKey(oArrayVar.Name, out var oStorage)
                                    && (arraysWithVariableIndex.Contains(oStorage)
                                        || moduleSramArrays.Contains(oStorage)))
                                    av = new ArrayBase(oStorage);
                                if (!oArgTagged) RefuseOptionalPayloadStore(av, a);
                                Expression oValExpr = a is KeywordArgExpr oStrKw ? oStrKw.Value : a;
                                string oPname = oKwPnames != null && oPidx >= 0 && oPidx < oKwPnames.Count
                                    ? oKwPnames[oPidx] : "?";
                                if (IsStrParamSlot(callee, oPidx))
                                    av = BindStrParamArg(callee, oPname, oValExpr, av, expr);
                                // One character is also its code, and a method has always
                                // received it that way; a longer text has no such reading.
                                else if (!IsBufferParam(callee, oPidx, oPname)
                                         && (StaticStringOf(oValExpr) is { Length: not 1 }
                                             || IsRuntimeStrArgument(oValExpr)))
                                    RefuseTextForNonStrParam(callee, oPname, oValExpr, expr);
                                if (oKwPnames != null && oPidx >= 0 && oPidx < oKwPnames.Count
                                    && functionParamTypes.TryGetValue(callee, out var oPtypes)
                                    && oPidx < oPtypes.Count)
                                    NoteArgumentStore(callee + "." + oKwPnames[oPidx], oPtypes[oPidx], av);
                                // The same width coercion a plain call gets. An outlined
                                // method is marshalled by each argument's own width too, and
                                // `o.add(s + 300)` handed a uint16 parameter the uint32 temp
                                // the addition widened to: its high word landed in the
                                // registers of the parameter before it, self_<field>, and the
                                // method read the field as 0 on every call after the first.
                                av = CoerceToParam(callee, oPidx, av);
                                oArgs.Add(av);
                            }
                            }

                            // An omitted argument takes its declared default. An outlined method
                            // is a real subroutine with a fixed parameter list, so a call that
                            // stops early left the remaining parameters unwritten and the body
                            // read them as zero: `def g(self, k: uint8 = 4)` called as `o.g()`
                            // computed with k = 0, on a clean build.
                            // The defaults of an outlined method are recorded against its
                            // synthesized parameter list, the leading self_<field> ones
                            // included, so oArgs.Count is exactly the next position to fill.
                            if (functionParamDefaults.TryGetValue(callee, out var oDefaults))
                            {
                                for (int di = oArgs.Count; di < oDefaults.Count; di++)
                                {
                                    if (oDefaults[di] is not { } defaultExpr)
                                        break;
                                    Val dv = VisitExpression(defaultExpr);
                                    dv = CoerceOutlinedArg(callee, di, dv);
                                    if (IsStrParamSlot(callee, di))
                                        dv = BindStrParamArg(callee,
                                            functionParams.TryGetValue(callee, out var dNames)
                                                && di < dNames.Count ? dNames[di] : "?",
                                            defaultExpr, dv, expr);
                                    oArgs.Add(CoerceToParam(callee, di, dv));
                                }
                            }

                            // RFC 0009: splice each tagged union parameter's member byte
                            // after its payload. expr.Args is the raw (unreordered) list,
                            // so line its expressions up by parameter index first.
                            if (functionParamTags.ContainsKey(callee))
                            {
                                functionParams.TryGetValue(callee, out var oPnames);
                                int oSelf = functionParamSelfCount.GetValueOrDefault(callee);
                                var oArgExprs = new List<Expression?>(new Expression?[oArgs.Count]);
                                int oPos = 0;
                                foreach (var a in expr.Args)
                                {
                                    if (a is KeywordArgExpr okw)
                                    {
                                        int opi = oPnames?.IndexOf(okw.Key) ?? -1;
                                        if (opi >= 0 && opi < oArgExprs.Count) oArgExprs[opi] = okw.Value;
                                    }
                                    else
                                    {
                                        int opi = oSelf + oPos++;
                                        if (opi < oArgExprs.Count) oArgExprs[opi] = a;
                                    }
                                }
                                oArgs = WithParamTags(callee, oArgs, oArgExprs);
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
                                // A union write-back field returns its tag alongside the
                                // payload (the self_<field> parameter carried one in, the
                                // appended `return self.<field>` carries it out) -- the
                                // call lands both and the field's $tag sibling updates
                                // with the same store the payload gets.
                                EmitMaybeTaggedCall(callee, oArgs, wDst);
                                Emit(new Copy(wDst, new Variable(fieldVar, wb.Type)));
                                if (TagOfVal(wDst) is { } wbTag)
                                    Emit(new Copy(wbTag,
                                        new Variable(fieldVar + "$tag", DataType.UINT8)));
                                // A module-level instance whose field is written through this
                                // write-back needs the same real storage a syntactic
                                // `obj.n = ...` gets: there is no assignment anywhere in the
                                // source for the marking pass to have seen, so the reader in
                                // another function folded the constructor's value.
                                if (moduleInstanceMutableFields.Contains(fieldVar))
                                    mutableGlobals[fieldVar] = wb.Type;
                                constantVariables.Remove(fieldVar);
                                killedConstants.Add(fieldVar);
                                variableTypes[fieldVar] = wb.Type;
                                InvalidateFieldsWrittenByCall(callee, instName);
                                return new NoneVal(LiveCallResult: true);
                            }

                            bool rVoid = !functionReturnTypes.TryGetValue(callee, out var rt)
                                         || rt == "void" || rt == "None";
                            // A method that hands back a local list with no `-> list[T]` on
                            // its def was lowered as a void call, and the caller printed or
                            // indexed whatever the result register held.
                            if (rVoid && !ReferenceEquals(expr, discardedStatementCall)
                                && (instanceMethodDefs.TryGetValue(callee, out var oListDef)
                                    || methodAstByName.TryGetValue(callee, out oListDef))
                                && ReturnsALocalList(oListDef))
                                throw UserError(
                                    $"'{memC.Member}' returns a list, and it is compiled as a " +
                                    "subroutine whose def does not say so. Annotate the return " +
                                    $"type, like `def {memC.Member}(self, ...) -> list[uint8]:`", expr);
                            if (rVoid)
                            {
                                // `return self._p` with the receiver's _p marked None: the call
                                // hands back that None, which only this site can see.
                                string rfBase = instName;
                                for (int d = 0; d < 20 && variableAliases.TryGetValue(rfBase, out var rfa); d++)
                                    rfBase = rfa;
                                bool fieldsNone = outlinedSelfFieldReturns.TryGetValue(callee, out var rfs)
                                    && rfs.All(f => noneValuedNames.Contains(instName + "_" + f)
                                                    || noneValuedNames.Contains(rfBase + "_" + f));
                                Emit(new Call(callee, oArgs, new NoneVal()));
                                InvalidateFieldsWrittenByCall(callee, instName);
                                if (fieldsNone) return lastNoneCallResult = new NoneVal();
                                return VoidCallResult(callee);
                            }

                            Temporary oDst = MakeTemp(
                                functionReturnMembers.TryGetValue(callee, out var oMembers)
                                    ? UnionPayloadType(oMembers)
                                    : DataTypeExtensions.StringToDataType(
                                        functionReturnTypes[callee]));
                            EmitMaybeTaggedCall(callee, oArgs, oDst);
                            // A dispatched method declared `-> Cls`: oDst is the produced
                            // instance's scalar carrier (a slot pointer for a multi-field
                            // sret), tagged so a wrapper or tuple slot reads the class
                            // from the value.
                            StampProducedClass(oDst, rt);
                            InvalidateFieldsWrittenByCall(callee, instName);
                            // `-> list[T]`: the result is the list, and print, len() and
                            // indexing find it by its element type. Unregistered, `print(
                            // o.m())` printed the heap pointer.
                            if (rt is { } oRt && oRt.StartsWith("list[") && oRt.EndsWith("]"))
                            {
                                listVarElemTypes[oDst.Name] =
                                    DataTypeExtensions.StringToDataType(oRt[5..^1]);
                                variableTypes[oDst.Name] = DataType.GC_REF;
                            }
                            return oDst;
                        }

                        // Bound-instance outlining: a force-inline method called on a
                        // module-level instance compiles its body once as a real
                        // subroutine with `self` bound to the instance's global
                        // storage, so N call sites share one body instead of N
                        // expansions (the ht16k33 matrix demandant inlined show()
                        // 44 times). Anything the shared body cannot reproduce --
                        // per-site constant bindings, instance/tuple arguments --
                        // keeps the force-inline path below.
                        if (objVal is Variable boundRecv
                            && instanceMethodDefs.TryGetValue(callee, out var boundImpl)
                            && BoundMethodCallee(callee, boundRecv.Name, boundImpl,
                                                 expr.Args) is { } boundCallee)
                        {
                            CheckSignatureShape(boundImpl, expr.Args,
                                QualNameForCall(boundImpl, callee), expr);
                            // Same refusal as the outlined path above: the shared body is a
                            // subroutine, and without `-> list[T]` its list result is lost.
                            if (!ReferenceEquals(expr, discardedStatementCall)
                                && ReturnsALocalList(boundImpl)
                                && !IsListLikeReturnType(boundImpl.ReturnType))
                                throw UserError(
                                    $"'{memC.Member}' returns a list, and it is compiled as a " +
                                    "subroutine whose def does not say so. Annotate the return " +
                                    $"type, like `def {memC.Member}(self, ...) -> list[uint8]:`", expr);
                            Val boundResult = EmitRegularFunctionCall(expr, boundCallee);
                            InvalidateFieldsWrittenByCall(callee, boundRecv.Name);
                            return boundResult;
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
                        // Located through UserError so the line and the file come from the
                        // same frame: `lastLine` is -1 inside an expansion, and a call node
                        // the C# front end built without a line printed `main.py:-1`.
                        if (memC.Member is "append" or "pop" or "insert" or "remove" or "extend" or "clear")
                        {
                            var at = UserError(
                                $"'.{memC.Member}()' requires a typed list; an untyped '[]' has no " +
                                "runtime list. Declare it like `x: list[uint8] = []`, or use a " +
                                "fixed-size array `x: uint8[N]`.", expr.Line > 0 ? expr : memC);
                            throw new NameError(at.Message, at.Line, at.Column, at.Length)
                                { File = at.File, LocationIsFinal = true };
                        }
                        callee = vObj.Name + "_" + memC.Member;
                    }
                }
                else if (objVal is MemoryAddress addr)
                {
                    callee = $"MemoryAddress_{addr.Address}_{memC.Member}";
                }
                else
                {
                    // `"val {}".format(x)` is the pre-f-string way to build a string and is
                    // still the common one in MicroPython code. It fell through to the
                    // nested-ZCA message below, which describes a program with no string in
                    // it at all. f-strings already work, and this IS an f-string written the
                    // other way, so it lowers to one.
                    if (memC.Member == "format" && memC.Object is StringLiteral fmtLit)
                        return VisitExpression(DesugarStrFormat(fmtLit.Value, expr));

                    // `byteorder.strip("RGBWP")`, `byteorder.index("R")` -- the string
                    // transforms an unmodified CircuitPython library applies to its
                    // arguments (adafruit_pixelbuf.parse_byteorder). With the receiver's
                    // text in hand the answer is a new constant, computed here; no string
                    // object is ever built.
                    if (TryEmitConstStrMethod(expr, memC) is { } constStrResult)
                        return constStrResult;

                    // `sep.join(<seq>)` in a value position: a compile-time string list
                    // folds to a constant; a generator/comprehension over a compile-time
                    // sequence materializes into a runtime-string buffer (the same
                    // lowering an f-string-as-value gets). A non-string receiver's .join
                    // is somebody's own method and falls through to the generic checks.
                    if (memC.Member == "join" && StaticStringOf(memC.Object) is { } joinSep)
                        return EmitJoinValue(expr, joinSep);
                    // s.split(sep) DOES work -- as the iterable of a `for` or of enumerate(),
                    // where the chunks unroll at compile time. In a value position it would
                    // have to be a list, and there is no heap to build one in.
                    if (memC.Member == "split")
                        throw UserError(
                            "str.split() is supported only where the chunks are consumed "
                            + "directly: `for chunk in s.split(sep)` or `for i, chunk in "
                            + "enumerate(s.split(sep))`, with s and sep compile-time strings. "
                            + "In this position the result would have to be a value -- a "
                            + "list -- and there is no heap to hold one.", memC);
                    // What arrives here is a method whose RECEIVER is a compile-time constant:
                    // a string ("a,b,c".split(",")) or a number ((5).bit_length()). It used to
                    // answer with a sentence about a ZCA field that is itself a ZCA, like
                    // self.pin.pulse_in(). That described neither the reader's program -- which
                    // has no class, no ZCA and no member access -- nor a real limitation:
                    // self.pin.pulse_in() compiles, at two and three levels of nesting, called
                    // from a method or from main. The message outlived the gap it was written
                    // for and then sent readers looking for a ZCA they did not have.
                    //
                    // A receiver that IS a name or a register never reaches this branch, so
                    // there is no object to name here; say what the receiver is and what does
                    // work on it. Everything offered below is checked to compile.
                    if (StaticStringOf(memC.Object) != null)
                        throw UserError(
                            $"'.{memC.Member}()' is not supported on a string. A PyMCU string is a "
                            + "compile-time constant or a fixed buffer, not an object carrying "
                            + "methods, and there is no heap to build a result in. What does work "
                            + "on a string: len(), indexing (s[0]), iterating it (for c in s), "
                            + "f-strings and '...'.format(x) to build text, and sep.join([...]) "
                            + "assigned to a name.", memC);

                    if (memC.Object is IntegerLiteral or FloatLiteral)
                        throw UserError(
                            $"'.{memC.Member}()' is not supported on a number literal. Numbers in "
                            + "PyMCU are machine integers and floats, not objects carrying methods; "
                            + "use the operators and the builtins (abs, min, max, round) instead.",
                            memC);

                    // A NAME whose value the compiler now knows (#331) arrives here as the
                    // constant it holds, so the receiver is no longer "not a name": `x = 5`
                    // then `x.bit_length()` used to reach the numeric message on the
                    // undefined-function path and now reaches this one. It gets the same
                    // sentence, because neither the reader's program nor their mistake changed.
                    if (memC.Object is VariableExpr constRecv
                        && NumericLocalKind(constRecv.Name) is { } constKind)
                        throw NumericReceiverError(constRecv.Name, constKind, memC.Member, memC);

                    // Anything else that resolves to neither a name nor a register. No cause is
                    // claimed here on purpose: the one this branch used to name now compiles, and
                    // guessing a new one is how the last message became wrong.
                    throw UserError(
                        $"'.{memC.Member}()' cannot be dispatched: its receiver is not a name bound "
                        + "to an object, a register, or a value PyMCU defines methods on.", memC);
                }
            }
        }
        else if (expr.Callee is IndexExpr { Target: VariableExpr idxArrVe0 } idxCallee0)
        {
            return EmitCallableArrayCall(expr, idxCallee0, idxArrVe0);
        }
        else
        {
            throw UserError("Indirect calls not yet supported", expr.Callee);
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

        // One check for every builtin, before the dispatch below. Issue #226.
        expr = CheckBuiltinKeywords(expr, callee);

        // range() has no run-time value: there is no object to hand back, only loops that
        // walk it. Every supported spelling reaches the compiler without passing through
        // here, so a call that does arrive is `r = range(4)`, `len(range(4))` or an argument.
        // It used to be reported as a builtin PyMCU does not provide (PyMCU#288).
        if (callee == "range")
            throw UserError(
                "range() is not a value in PyMCU: use it as the iterable of a for loop, in " +
                "'x in range(...)', in reversed(range(...)) or in enumerate(range(...))", expr.Callee);

        if (callee == "len") return EmitLenBuiltin(expr);
        // RFC 0008: open() on an embedded file resolves here, to a compile-time handle
        // over the blob -- there is no filesystem on the chip for it to call into. A
        // program that defined its own `def open` keeps it (the builtin name loses).
        if (callee == "open" && !IsBuiltinShadowed("open"))
            return EmitRomfsOpen(expr);
        if (callee is "os_stat" or "uos_stat" or "pymcu_os_stat") return EmitOsStat(expr);
        if (callee is "os_listdir" or "uos_listdir" or "pymcu_os_listdir") return EmitOsListdir(expr);
        if (callee == "int_from_bytes") return EmitIntFromBytesBuiltin(expr);
        if (callee == "struct_calcsize") return EmitStructCalcsize(expr);
        if (callee == "struct_unpack") return EmitStructUnpackFrom(expr, "struct.unpack()");
        if (callee == "struct_unpack_from") return EmitStructUnpackFrom(expr);
        if (callee == "struct_pack_into") return EmitStructPackInto(expr);
        if (callee == "struct_pack")
            // The value positions are intercepted where the buffer can be bound: the
            // assignment target (`cmd = struct.pack(...)`) or a slice-assign source
            // (`cmd[off:] = ...`). Anywhere else there is nowhere to put the bytes.
            throw UserError(
                "struct.pack() produces a fixed buffer: bind it to a name "
                + "(`cmd = struct.pack(fmt, v)`) or write it through a slice "
                + "(`cmd[off:] = struct.pack(fmt, v)`). It has no value in this position.",
                expr.Callee);
        if (callee == "abs") return EmitAbsBuiltin(expr);
        if (callee == "min") return EmitMinBuiltin(expr);
        if (callee == "max") return EmitMaxBuiltin(expr);
        if (callee == "ord") return EmitOrdBuiltin(expr);
        if (callee == "chr") return EmitChrBuiltin(expr);

        if (callee == "sum") return EmitSumBuiltin(expr);

        if (callee == "any") return EmitAnyBuiltin(expr);
        if (callee == "all") return EmitAllBuiltin(expr);
        if (callee == "bool") return EmitBoolBuiltin(expr);

        // RFC 0014 decision 4: a Python builtin is shadowed by a user definition of the
        // same name, as in CPython -- "today only open respects shadowing" no longer
        // holds for these four. A user's own `def hex`/`def bin`/`def oct`/`def round`
        // keeps running; the builtin name falls through to the ordinary call dispatch
        // below, which resolves it against functionParams/inlineFunctions like any
        // other user function.
        if (callee == "hex" && !IsBuiltinShadowed("hex")) return EmitHexBuiltin(expr);
        if (callee == "bin" && !IsBuiltinShadowed("bin")) return EmitBinBuiltin(expr);
        if (callee == "oct" && !IsBuiltinShadowed("oct")) return EmitOctBuiltin(expr);
        if (callee == "str") return EmitStrBuiltin(expr);
        if (callee == "repr") return EmitReprBuiltin(expr);
        if (callee == "pow") return EmitPowBuiltin(expr);
        if (callee == "round" && !IsBuiltinShadowed("round")) return EmitRoundBuiltin(expr);
        if (callee == "memoryview") return EmitMemoryviewBuiltin(expr);

        // `list(x)` / `tuple(x)` with a single runtime-list argument copy it into a
        // fresh heap object: unmodified adafruit_irremote writes `list(pulses)`
        // and `tuple(input_pulses)` in decode_bits. A list-literal or fixed-array
        // argument, or a list whose element type is still pending, reports below.
        // Exception: `tuple(t)` where t is already tuple-bound IS t in CPython
        // (`tuple(t) is t` -- an immutable sequence has nothing to defend against
        // by copying). Aliasing the reference saves the allocation, which on a
        // heap of a few hundred bytes is the difference between decode_bits
        // finishing and a MemoryError on the return path.
        if ((callee == "list" || callee == "tuple") && expr.Args.Count == 1
            && expr.Args[0] is VariableExpr copySrc
            && ResolveListVarQualified(copySrc.Name) is { Length: > 0 } copySrcKey)
        {
            if (callee == "tuple" && IsTupleBound(copySrc.Name))
                return new Variable(copySrcKey, DataType.GC_REF);
            return EmitListCopyCtor(copySrcKey, copySrc, expr);
        }

        if (callee == "divmod") return EmitDivmodBuiltin(expr);
        if (CastTypes.ContainsKey(callee)) return EmitNumericCastBuiltin(expr, callee);
        if (callee == "bitcast") return EmitBitcastBuiltin(expr);
        if (callee == "gc_alloc") return EmitGcAllocBuiltin(expr);
        if (callee == "asm") return EmitAsmBuiltin(expr);

        // `isinstance(e, X)` on a name bound by `except ... as` has an answer: the dispatcher
        // already holds the code of the live exception, so the test is the comparison it
        // performs, against a different constant (#369). Ahead of the builtin refusal table,
        // which is right about every OTHER receiver -- a value's type is fixed at compile time
        // and there is nothing left to ask.
        if (callee == "isinstance" && expr.Args.Count == 2
            && expr.Args[0] is VariableExpr isinstRecv
            && TryGetExceptionBinding(isinstRecv.Name, out var isinstBinding))
        {
            if (expr.Args[1] is not VariableExpr wantedType)
                throw UserError(
                    $"isinstance({isinstRecv.Name}, ...) needs an exception type written as a "
                    + "name, because the test is a comparison against that type's code.",
                    expr.Args[1]);

            // `isinstance(e, OSError)` is true when the caught exception is a TimeoutError
            // or a user OSError subclass -- the same subtree expansion the dispatcher's
            // `except OSError` performs, restricted to codes some raise can deliver.
            var isinstResult = MakeTemp(DataType.UINT8);
            var exnVar = new Variable(isinstBinding.CodeVar, DataType.UINT8);
            var isinstCodes = ExpectedExceptionCodes(wantedType.Name, expr);
            if (isinstCodes.Count <= 1)
            {
                Emit(new Binary(PyMCU.IR.BinaryOp.Equal, exnVar,
                                ResolveBinding(wantedType.Name, wantedType), isinstResult));
            }
            else
            {
                Emit(new Copy(new Constant(0), isinstResult));
                foreach (int c in isinstCodes)
                {
                    Temporary hit = MakeTemp(DataType.UINT8);
                    Emit(new Binary(PyMCU.IR.BinaryOp.Equal, exnVar,
                                    new Constant(c), hit));
                    Emit(new Binary(PyMCU.IR.BinaryOp.BitOr, isinstResult, hit,
                                    isinstResult));
                }
            }
            return isinstResult;
        }

        // RFC 0009 section 5: `isinstance(v, T)` on a live tagged union is a tag
        // compare -- the tag byte against the member index T names, OR'd when a
        // tuple names several. A narrowed or provably-None name has its answer at
        // compile time. Ahead of the ZCA fold: a tagged name is a union, not an
        // instance.
        if (callee == "isinstance" && expr.Args.Count == 2
            && expr.Args[0] is Expression isinstUnionRecv
            && OptionalKeyOfExpr(isinstUnionRecv) is { } isinstKey
            && optionalMembersByName.TryGetValue(isinstKey, out var isinstMembers))
        {
            var isinstMatch = IsinstanceMemberIndices(expr.Args[1], isinstMembers);
            if (isinstMatch.Count == 0)
                return new Constant(0);   // T names no member: never true
            if (narrowedOptionals.TryGetValue(isinstKey, out var isinstNIdx))
                return new Constant(isinstMatch.Contains(isinstNIdx) ? 1 : 0);
            if (noneValuedNames.Contains(isinstKey))
                return new Constant(isinstMatch.Contains(NoneIndex(isinstMembers)) ? 1 : 0);
            if (optionalTagSlots.TryGetValue(isinstKey, out var isinstTag))
            {
                // Only the tag is read -- never the payload.
                Temporary isinstRes = MakeTemp(DataType.UINT8);
                if (isinstMatch.Count == 1)
                {
                    Emit(new Binary(PyMCU.IR.BinaryOp.Equal,
                        isinstTag, new Constant(isinstMatch[0]), isinstRes));
                }
                else
                {
                    Emit(new Copy(new Constant(0), isinstRes));
                    foreach (var mi in isinstMatch)
                    {
                        Temporary one = MakeTemp(DataType.UINT8);
                        Emit(new Binary(PyMCU.IR.BinaryOp.Equal, isinstTag, new Constant(mi), one));
                        Emit(new Binary(PyMCU.IR.BinaryOp.BitOr, isinstRes, one, isinstRes));
                    }
                }
                return isinstRes;
            }
        }

        // `isinstance(x, T)` (and `isinstance(x, (T1, T2, ...))`) on a ZCA instance is not a
        // decision, it is a FOLD: every instance has a class fixed when the program is
        // compiled -- that is the whole premise this compiler is built on -- so the answer is
        // knowable right here, true when x's class IS T or a SUBCLASS of one of the T's, false
        // otherwise. adafruit_mcp3xxx's own AnalogIn.__init__ guards its constructor with
        // exactly this: `if not isinstance(mcp, MCP3xxx): raise ValueError(...)`.
        //
        // Answered only when BOTH sides resolve: every candidate to a class this compiler
        // built a layout for, and the receiver either to a known instance class (a name the
        // ordinary receiver-resolution chain already tracks) or to a value that is provably
        // NOT an instance -- a string or number constant, None, an address, a buffer -- for
        // which isinstance is False unconditionally. That last arm is the difference between
        // pruning a constructor's `if not isinstance(mcp, MCP3xxx): raise` tail and lowering
        // it anyway: AnalogIn("not an mcp", ...) binds mcp to a string, and the field reads
        // after the raise were checked against a receiver that is not a class at all. A
        // scalar Variable still falls through to the refusal below, which is right about a
        // receiver that is not a class instance.
        if (callee == "isinstance" && expr.Args.Count == 2
            && expr.Args[0] is VariableExpr isinstZcaRecv)
        {
            var isinstCandidates = expr.Args[1] is TupleExpr isinstTuple
                ? isinstTuple.Elements
                : new List<Expression> { expr.Args[1] };

            var isinstResolved = new List<string>();
            bool isinstAllResolved = isinstCandidates.Count > 0;
            foreach (var cand in isinstCandidates)
            {
                // A module-qualified candidate (`segments.Seg7x4`) spells its class
                // name dotted; ResolveCallee mangles it the same way a call does.
                string? candName = cand switch
                {
                    VariableExpr candVe => candVe.Name,
                    MemberAccessExpr candMa => FormatMemberTarget(candMa),
                    _ => null,
                };
                if (candName != null
                    && ResolveCallee(candName) is { } candCls
                    && (classNames.Contains(candCls) || classFieldLayout.ContainsKey(candCls)))
                    isinstResolved.Add(candCls);
                else { isinstAllResolved = false; break; }
            }

            if (isinstAllResolved)
            {
                // The bound value outranks the declared type: `def f(mcp: MCP3xxx)`
                // called as `f("not an mcp")` binds mcp to a string constant, and
                // isinstance("not an mcp", MCP3xxx) is False whatever the annotation
                // claims -- the guard's `raise` is the taken branch.
                if (ProbeBinding(isinstZcaRecv.Name)
                        is Constant or NoneVal or MemoryAddress or ArrayBase)
                    return new Constant(0);
                if (InstanceClassOfName(isinstZcaRecv.Name) is { } isinstRecvCls)
                {
                    bool isinstMatches = isinstResolved.Any(t => IsClassOrSubclassOf(isinstRecvCls, t));
                    return new Constant(isinstMatches ? 1 : 0);
                }
            }
        }

        // `isinstance(x, (tuple, list))` -- adafruit_ht16k33 / PyMCU#423. The candidates are
        // builtins, not classes with a layout, so the ZCA fold above does not answer. The
        // receiver's shape is still known: a compile-time sequence, a buffer, or a scalar.
        if (callee == "isinstance" && expr.Args.Count == 2
            && expr.Args[0] is VariableExpr isinstShapeRecv
            && TryFoldIsinstanceBuiltinTypes(isinstShapeRecv, expr.Args[1]) is { } isinstShape)
            return isinstShape;

        // The same question about an EXPRESSION, `isinstance(s + 300, int)`: its value has
        // one type too, the type the lowered value carries. Only the name above was asked,
        // so the expression reached the refusal that says isinstance() is not provided --
        // while the same test on a name folded.
        if (callee == "isinstance" && expr.Args.Count == 2
            && expr.Args[0] is IntegerLiteral or FloatLiteral or StringLiteral or BooleanLiteral
                or FStringExpr or BinaryExpr or UnaryExpr
            && TryFoldIsinstanceOfValue(expr.Args[0], expr.Args[1]) is { } isinstValue)
            return isinstValue;

        if (callee == "print") return EmitPrintBuiltin(expr);

        if (callee == "ptr" && intrinsicNames.Contains("ptr"))
        {
            if (expr.Args.Count != 1) throw UserError("ptr() expects exactly one argument", expr.Callee);
            // Evaluate the argument as a compile-time ADDRESS: a literal, a const, or a
            // register/MMIO base combined with constant +/- offsets, e.g. ptr(PORTB + 6).
            // A bare register contributes its address here (not its dereferenced value).
            if (TryEvalConstAddress(expr.Args[0]) is int addr)
                return new MemoryAddress(addr, DataType.UINT8);

            // A string/f-string is not an address (it would otherwise be accepted as its
            // flash string-id, silently aiming the pointer at a garbage address).
            if (expr.Args[0] is StringLiteral or FStringExpr)
                throw UserError("ptr() argument must be a numeric address, not a string", ArgAt(expr, 0));

            // An ARRAY is not an address either, and this is the shape that used to go
            // through in silence. `ptr(buf)` took the runtime-address path below, which
            // evaluates its argument -- and evaluating an array name reads buf[0]. The
            // pointer then held the first BYTE of the array widened to 16 bits, a small
            // number that looks plausible, and `p.value = 42` wrote that many bytes up from
            // address 0: register space, another global, the stack. No diagnostic anywhere.
            // An SRAM array is addressed by an assembler label, not by a number this pass can
            // resolve, so refusing is what can be done here and is what #413 asks for.
            if (NamedArrayArgument(expr.Args[0]) is { } arrName)
                throw UserError(
                    $"ptr() cannot take the array '{arrName}': an array lives at an address "
                    + "the assembler assigns, not at one this pass can compute, and taking it "
                    + "here would read the array's first byte instead. ptr() accepts a "
                    + "hardware register or a numeric address. Index the array directly "
                    + $"(`{arrName}[i]`) when what you want is its elements.",
                    ArgAt(expr, 0));

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
            PyMCU.Common.Diagnostic.Warning(code: "ptr-not-an-intrinsic", text:
                "'ptr' is not recognized as an intrinsic. Did you forget to import from pymcu.types?");
            return new Constant(0);
        }

        if (callee == "const" && intrinsicNames.Contains("const"))
        {
            if (expr.Args.Count != 1) throw UserError("const() expects exactly one argument", expr.Callee);
            Val argVal = VisitExpression(expr.Args[0]);
            if (argVal is Constant) return argVal;
            throw UserError("const() argument must be a compile-time constant expression", ArgAt(expr, 0));
        }

        if ((callee == "funcref" || callee == "pymcu_types_funcref") && intrinsicNames.Contains("funcref"))
            return EmitFuncrefIntrinsic(expr);

        if (callee == "_set_irq_zca_arg" && intrinsicNames.Contains("_set_irq_zca_arg"))
            return EmitSetIrqZcaArgIntrinsic(expr);

        if (callee == "compile_isr" && intrinsicNames.Contains("compile_isr"))
            return EmitCompileIsrIntrinsic(expr);

        if (callee == "claim" && intrinsicNames.Contains("claim"))
            return EmitClaimIntrinsic(expr);

        if (externFunctionMap.TryGetValue(callee, out string cSym))
            return EmitExternCall(expr, callee, cSym);

        // Before the split, so that BOTH paths are covered. The two lose the value in
        // different places -- an @inline expansion narrows the constant into the IR, a real
        // call keeps it in the IR and the backend truncates it loading the parameter's
        // registers -- and a check on either side alone would cover one of them, four times
        // over for the real one, once per backend.
        CheckConstantArgFitsParam(expr, callee);

        if (inlineFunctions.TryGetValue(callee, out var func)) return EmitInlineFunctionCall(expr, callee, func);

        return EmitRegularFunctionCall(expr, callee);
    }

    /// The widest value each declared width holds, or null for a spelling that narrows nothing.
    private static long? WidestValueOf(string? declared) => declared switch
    {
        "uint8" => byte.MaxValue,
        "int8" => sbyte.MaxValue,
        "uint16" => ushort.MaxValue,
        "int16" => short.MaxValue,
        _ => null,
    };

    /// Refuses a literal argument that its parameter's declared width cannot hold.
    ///
    /// `delay_us(480)` against `def delay_us(us: uint8)` used to compile clean and wait 224 us:
    /// the argument is narrowed to the parameter's width, no arm of the body can see the 480,
    /// and nothing said so. It is the same silence `AvrCodeGen.Compile` already refuses at the
    /// top -- a .mir with no geometry is rejected rather than compiled against zeros -- applied
    /// to a literal that does not fit. Measured cost of that silence: the CircuitPython 1-Wire
    /// reset holds its line low for 224 us where the protocol needs 480 (#501), and two UART
    /// layers ran at 50000 baud having been asked for 115200.
    ///
    /// Only a BARE width narrows. `const[uint16]` carries its literal through untouched, which
    /// is why the live PIC UARTs compare a `const[uint16]` baud against 115200 and are right to;
    /// reporting those would send someone to widen a parameter that needs no widening.
    ///
    /// A deliberate mask stays available and already compiles: `f(uint8(0xFFFF))` passes 255 and
    /// says so. That is the whole reason this can refuse instead of warn.
    ///
    /// Silent where it cannot be sure. A callee whose declarations were never recorded, an
    /// argument list that does not line up with the signature, a keyword naming no parameter:
    /// each returns without a word, because a diagnostic that fires on a misalignment is worse
    /// than one that misses.
    private void CheckConstantArgFitsParam(CallExpr expr, string callee)
    {
        if (!functionParamDeclared.TryGetValue(callee, out var declared)) return;
        if (!functionParams.TryGetValue(callee, out var names)) return;
        if (declared.Count != names.Count) return;

        // A method's signature carries the receiver, the call does not. Keyed on the name
        // rather than on the callee's syntax because a constructor reaches __init__ through a
        // plain VariableExpr and still has one.
        int offset = names.Count > 0 && (names[0] == "self" || names[0] == "cls") ? 1 : 0;

        var positional = expr.Args.Where(a => a is not KeywordArgExpr).ToList();
        if (positional.Count + offset > declared.Count) return;

        for (int i = 0; i < positional.Count; ++i)
            RefuseIfTooWide(positional[i], declared[i + offset], names[i + offset]);

        foreach (var arg in expr.Args)
        {
            if (arg is not KeywordArgExpr kw) continue;
            int idx = names.IndexOf(kw.Key);
            if (idx >= 0) RefuseIfTooWide(kw.Value, declared[idx], names[idx]);
        }
    }

    private void RefuseIfTooWide(Expression arg, string? declared, string paramName)
    {
        if (arg is not IntegerLiteral lit) return;

        // Only a literal the PARSER built from a token, which is the only kind a reader wrote.
        // A desugaring synthesises IntegerLiterals that stand for something else and carry no
        // position: `[Led(p) for p in ["PD2", ...]]` expands to calls whose argument is the
        // string's interned id, and 256 as an id is not 256 as a number. Reporting those told
        // an author their string did not fit in a uint8.
        if (lit.Line <= 0 && lit.Column <= 0) return;

        if (WidestValueOf(declared) is not { } widest) return;
        if (lit.Value >= 0 && lit.Value <= widest) return;
        if (lit.Value < 0 && declared is "int8" or "int16"
            && lit.Value >= (declared == "int8" ? sbyte.MinValue : short.MinValue)) return;

        int arrives = NarrowConstantArgToParam(lit.Value, declared);

        // The message names two routes, and only one of them is an edit at THIS position. The
        // other, "widen the parameter", is an edit at the callee's `def` -- in another file
        // whenever the callee is a library, which is the case that produced #501 -- and the
        // generator does not hold that position: `functionParamDeclared` maps a name to its
        // declared widths and keeps no node. So the fix offered here is the narrowing one, and
        // the widening stays prose until the callee's parameter carries a span.
        //
        // MaybeIncorrect, deliberately, and NOT because this particular fix is unreliable.
        //
        // It is reliable. A cast truncates to the low bits rather than saturating, so
        // `uint8(480)` passes 224, which is the number this very message promised the reader
        // two sentences earlier. Measured in the IR: `pulse(uint8(480))` lowers to
        // `const 224`, and `pulse(uint8(0xFFFF))` to `const 255`. (The 255 belongs to the
        // second of those and to the commit that added this refusal; reading it as 480's
        // answer is the mistake this comment exists to stop someone repeating.)
        //
        // The default is conservative because the FORMAT cannot tell which fixes are the
        // reliable ones. PyMCU#280 measured that one in three user-facing refusals leads
        // somewhere worse when followed literally, so while that stands, `machine-applicable`
        // is a promise no site can make on the others' behalf, and an editor that applies
        // fixes in bulk is the fastest way to pay that rate. A site earns the stronger grade
        // by being measured, one at a time, not by arguing for it.
        var narrow = new PyMCU.Common.SuggestedFix(
            Label: $"narrow it on purpose: {declared}({lit.Value}), which passes {arrives}",
            File: LocatedFile ?? string.Empty,
            Line: lit.Line,
            Column: lit.Column,
            Length: lit.Value.ToString().Length,
            Replacement: $"{declared}({lit.Value})",
            Applicability: PyMCU.Common.FixApplicability.MaybeIncorrect);

        throw UserError(
            $"{lit.Value} does not fit in '{paramName}', which is declared {declared}: the "
            + $"argument is narrowed to the parameter's width, so the function would receive "
            + $"{arrives}. Widen the parameter to the type the values need, or write "
            + $"`{declared}({lit.Value})` if narrowing it to {arrives} is what you meant.",
            arg,
            code: "literal-too-wide-for-parameter",
            fixes: [narrow]);
    }

    // Resolve keyword arguments in a call to a regular (non-@inline) function into a flat
    // positional argument list (Python-style binding). Positional args fill leading params;
    // keyword args bind by parameter name; any gap before the last supplied arg is filled
    // from the parameter default. Returns the original list unchanged when there are no
    // keyword args. Reports unknown/duplicate/missing keyword bindings as clean user errors
    // (the inline path already does this; previously a kwarg to a real subroutine reached
    // VisitExpression and surfaced the cryptic "Unknown Expression type: KeywordArgExpr").
    /// <summary>
    /// Where the value in `obj.field` was written, when it is an argument that was stored into
    /// a field earlier. The base name is resolved through the alias chain exactly as the read
    /// of that field resolves it, so the key asked for is the key the field's value lives
    /// under; anything else would be a name that happens to match.
    /// </summary>
    private bool TryFieldOrigin(Expression? arg, string callerPrefix, out Expression origin)
    {
        origin = null!;
        if (arg is not MemberAccessExpr ma || ma.Object is not VariableExpr baseVe) return false;

        foreach (var start in new[] { callerPrefix + baseVe.Name, baseVe.Name })
        {
            string b = start;
            var seen = new HashSet<string>();
            while (variableAliases.TryGetValue(b, out var alias) && alias != b && seen.Add(b)) b = alias;
            if (argumentOrigin.TryGetValue(b + "_" + ma.Member, out origin!)) return true;
        }
        return false;
    }

    private List<Expression> ReorderCallArgs(List<Expression> args, string callee, ASTNode? at)
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
            throw UserError($"keyword arguments are not supported in call to '{shown}'", at);

        var positional = new List<Expression>();
        var byName = new Dictionary<string, Expression>();
        // The keyword NODE beside its value, so "multiple values for argument" further down can
        // point at the keyword that collides rather than at the positional argument, which is
        // the half the reader wrote correctly.
        var byNameNode = new Dictionary<string, KeywordArgExpr>();
        foreach (var a in args)
        {
            if (a is KeywordArgExpr kw)
            {
                if (!paramNames.Contains(kw.Key))
                    throw UserError($"unknown keyword argument '{kw.Key}' in call to '{shown}'", kw);
                if (!byName.TryAdd(kw.Key, kw.Value))
                    throw UserError($"keyword argument '{kw.Key}' repeated in call to '{shown}'", kw);
                byNameNode[kw.Key] = kw;
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
                    throw UserError($"multiple values for argument '{paramNames[i]}' in call to '{shown}'",
                        byNameNode.GetValueOrDefault(paramNames[i]));
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
                    throw UserError($"missing argument '{paramNames[i]}' in call to '{shown}'", at);
                ordered.Add(def);
            }
        }
        return PinKeywordOrder(args, ordered);
    }

    // `f(b=count, a=bump())` evaluates count, then bump, in the order written; the list above
    // is in PARAMETER order, and every argument is evaluated in that order after it. When the
    // two orders differ and an argument can have an effect, the arguments are evaluated here,
    // as written, and carried as values.
    private List<Expression> PinKeywordOrder(List<Expression> written, List<Expression> ordered)
    {
        var values = written.Select(a => a is KeywordArgExpr kw ? kw.Value : a).ToList();
        var passed = ordered.Where(o => values.Any(v => ReferenceEquals(v, o))).ToList();
        if (passed.SequenceEqual(values, ReferenceEqualityComparer.Instance)
            || !values.Any(OperandCanHaveAnEffect))
            return ordered;
        var pinned = new Dictionary<Expression, Expression>(ReferenceEqualityComparer.Instance);
        for (int k = 0; k < values.Count; k++)
        {
            Expression v = values[k];
            if (OperandCanHaveAnEffect(v) && ValueIsHandedBack(v))
                pinned[v] = PinOnce(v);
            else if (v is VariableExpr or MemberAccessExpr && !OperandCanHaveAnEffect(v)
                     && values.Skip(k + 1).Any(OperandCanHaveAnEffect)
                     && SnapshotRead(VisitExpression(v)) is Temporary read)
                pinned[v] = new PreEvaluatedExpr(read, null) { Line = v.Line };
        }
        return ordered.Select(o => pinned.TryGetValue(o, out var p) ? p : o).ToList();
    }

    // Emit a call to a known non-@inline function (a real subroutine): build the arg
    // list (flash-string-by-ref, array base addresses), mangle module-dotted names,
    // fill defaulted params and copy each arg into the callee's param slot, then Call.
    private Val EmitRegularFunctionCall(CallExpr expr, string callee)
    {
        // An expression statement's call has nobody to hand a result to.
        bool regularResultDiscarded = ReferenceEquals(expr, discardedStatementCall);
        if (shapedSignatures.TryGetValue(callee, out var shapeDef))
            CheckSignatureShape(shapeDef, expr.Args, shapeDef.Name, expr);
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
            // `__import__` and `__build_class__` are CPython builtins, not runtime helpers:
            // exempting them by their prefix sent the call to the linker.
            && (!callee.StartsWith("__") || PythonBuiltins.Contains(callee)))
        {
            string shown = callee.Contains('.') ? callee[(callee.LastIndexOf('.') + 1)..] : callee;

            // The name failed to resolve because its defining module refused this target:
            // its module-level `raise CompileError(...)` guard survived if/match folding,
            // so none of its symbols were imported. Report the module author's message at
            // this use site instead of a misleading "undefined function".
            // The twin of the read path in Core.cs, and it goes through the same helper so the
            // two cannot drift. See ModuleGuardError for why the caret moved onto the guard.
            foreach (var g in moduleGuardErrors.OrderByDescending(kv => kv.Key.Length))
                if (callee.StartsWith(g.Key, StringComparison.Ordinal))
                    throw ModuleGuardError(g.Value, expr.Callee);

            // A known class invoked but with no __init__ to construct it — Python would use a
            // default constructor, which PyMCU does not synthesize. Be specific.
            if (classNames.Contains(callee) || classNames.Contains(shown))
                throw UserError(
                    $"class '{shown}' cannot be constructed: it has no __init__ method (PyMCU does " +
                    "not synthesize a default constructor — add `def __init__(self): ...`)", expr.Callee);

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
                    throw UserError($"'{shown}' is not callable (it is a value, not a function)", cv);
            }

            // `sep.join([...])` where the compiler no longer holds `sep`'s text -- it was bound
            // from a field, or from another name -- builds the callee `sep_join`, which nothing
            // defines, and the reader was sent to look for a typo in a symbol their program
            // never mentions. Reaching here means no such function exists, so a class of one's
            // own with a join() method is untouched. The separator is the condition; say so.
            if (expr.Callee is MemberAccessExpr { Member: "join", Object: VariableExpr joinRecv })
                throw UserError(
                    $"'{joinRecv.Name}.join([...])' needs '{joinRecv.Name}' to be a compile-time "
                    + $"string, and it is not: '{joinRecv.Name}' was bound to a value whose text "
                    + "the compiler does not hold. " + JoinIsCompileTime + " Write the separator "
                    + "at the call (\",\".join([...])), or bind the name to a literal.",
                    expr.Callee);

            // `getattr(module, "name", default)` on an imported module is not reflection:
            // a module's members are all registered at compile time, so the answer is
            // already known -- the attribute itself when the module exports it, the
            // default when it does not. This is how neopixel.py feature-detects
            // board.NEOPIXEL / NEOPIXEL_POWER. On anything that is not a module (an
            // instance, a class, a name bound to a value) it stays refused below.
            if (expr.Callee is VariableExpr { Name: "getattr" }
                && TryResolveModuleGetattr(expr) is { } getattrResolved)
                return VisitExpression(getattrResolved);

            if (expr.Callee is VariableExpr { Name: "getattr" }
                && expr.Args.Count == 2
                && expr.Args[0] is VariableExpr getattrObj2
                && expr.Args[1] is StringLiteral getattrMember2
                && modules.ContainsKey(getattrObj2.Name))
            {
                string getattrMod2 = TryImportedAlias(getattrObj2.Name, out var realGetattrMod)
                                     && realGetattrMod != null
                    ? realGetattrMod : getattrObj2.Name;
                throw UserError(
                    $"module '{getattrMod2}' has no attribute '{getattrMember2.Value}' -- "
                    + "the same AttributeError CPython raises, caught at compile time. "
                    + $"Pass a default: getattr({getattrObj2.Name}, \"{getattrMember2.Value}\", ...)",
                    expr);
            }

            // `hasattr(obj, "name")` on a statically-known shape is not runtime
            // reflection either: an instance's class fixes its member set (the
            // same set a Protocol match consults), a module's exports are all
            // registered, and a name bound to a function or a scalar has no
            // attributes to find. adafruit_debouncer's __init__ picks between a
            // value-like IO and a predicate callable this way. A shape that is
            // not pinned -- an ambiguous union parameter -- stays refused below.
            if (expr.Callee is VariableExpr { Name: "hasattr" }
                && expr.Args.Count == 2
                && expr.Args[1] is StringLiteral hasattrMember
                && TryFoldHasattr(expr.Args[0], hasattrMember.Value) is { } hasattrFolded)
                return VisitExpression(hasattrFolded);

            // Reflection builtins: name the real reason instead of "undefined function".
            if (shown is "getattr" or "setattr" or "hasattr" or "delattr" or "eval" or "exec" or "vars" or "dir" or "globals" or "locals")
                throw UserError($"'{shown}' is runtime reflection, which PyMCU does not support " +
                                "(attributes are resolved at compile time); access the attribute directly, " +
                                "or dispatch on an explicit type-tag field", expr.Callee);

            // The name came from an import, so the module is known and so are its exports:
            // "(typo, or a missing import?)" sends the reader to check an import that is right
            // there, and the mangled symbol (pymcu_hal_adc_ADC) is internal name construction
            // leaking into a user-facing message. Say which module does not export it, and
            // offer the near miss.
            if (expr.Callee is VariableExpr impVe && TryImportedAlias(impVe.Name, out var impMod)
                && !string.IsNullOrEmpty(impMod))
            {
                string wanted = AliasOriginal(impVe.Name);
                var exports = ExportedNames(impMod);
                string near = NearestName(exports, wanted);
                string tail = near.Length > 0
                    ? $". Did you mean '{near}'?"
                    : exports.Count > 0
                        ? $". It exports {string.Join(", ", exports.OrderBy(n => n).Take(8))}"
                          + (exports.Count > 8 ? ", ..." : "")
                        : "";
                throw UserError($"'{wanted}' is not exported by {impMod}{tail}", impVe);
            }

            // A Python builtin is in scope in every module and needs no import, so neither half
            // of "(typo, or a missing import?)" can be the answer: the reader checks a spelling
            // that is right and looks for an import that does not exist. Name the builtin, say
            // it is not provided, and say what to write instead.
            // `next(g)` / `iter(g)` where g IS a generator instance. The generic advice is
            // "Loop over the sequence itself", which is advice for a list: a generator is not
            // a sequence, and there is nothing else to loop over. Name the generator instead.
            if (expr.Callee is VariableExpr && (shown == "next" || shown == "iter")
                && expr.Args.Count > 0 && ResolveGeneratorArg(expr.Args[0]) is { } genName)
                // The REASON here used to be false, which is worse than a vague one because a
                // reader believes it. It said exhaustion "would need somewhere to report it,
                // and there is no StopIteration to put it in", which reads as "this compiler
                // has no exceptions". It has: `raise`/`except` across functions compiles on
                // AVR, RP2040 and CH32V003, and a raise inside a generator reaches an enclosing
                // `except` correctly. What is actually missing is narrower -- StopIteration is
                // not one of the six names in BuiltinExceptionNames, and no part of the
                // iterator protocol is implemented on the generator's state machine.
                //
                // So this says what is true (the protocol is not implemented) and stops. It
                // does not explain why, because the honest why is "nobody has written it", and
                // it does not promise it either.
                throw UserError(
                    $"{shown}() is part of the iterator protocol, which PyMCU does not "
                    + $"implement: a generator here is a state machine driven by `for`, and it "
                    + $"has no {shown}(). Consume '{genName}' with `for v in {genName}(...):`, "
                    + "which drives it and ends when it does.", expr.Callee);

            if (expr.Callee is VariableExpr && shown == "open")
            {
                // adafruit_framebuf's BitmapFont gets here: `open(font_name, "rb")` for
                // font_name "font5x8.bin". The reader needs to know WHICH file was being
                // opened and that the wall is the missing filesystem, not their call -- so
                // name the file when the argument is a compile-time string, and name the
                // feature track it belongs to (embedded files are RFC 0008, not yet done).
                string? openName = expr.Args.Count > 0 ? StaticStringOf(expr.Args[0]) : null;
                throw UserError(
                    openName != null
                        ? $"open('{openName}') needs a file to read, and a PyMCU program has no "
                          + "filesystem: embedded files are RFC 0008 work, not yet implemented. "
                          + "Until then the bytes have to live in the program itself -- a bytes "
                          + "literal or a const table."
                        : "open() is a Python builtin that PyMCU does not provide: there is no "
                          + "filesystem. Use the chip's flash or EEPROM helpers",
                    expr.Callee);
            }

            if (expr.Callee is VariableExpr && PythonBuiltins.Contains(shown))
                throw UserError(
                    UnsupportedBuiltins.TryGetValue(shown, out var why)
                        ? $"{shown}() is a Python builtin that PyMCU does not provide: {why}."
                        : $"{shown}() is a Python builtin that PyMCU does not provide. There is no "
                          + "import that adds it -- the supported builtins are len, abs, min, max, "
                          + "sum, any, all, bool, ord, chr, hex, bin, oct, str, pow, divmod, print, "
                          + "range, enumerate and zip, plus the numeric casts int/float/uint8/"
                          + "int8/uint16/int16/uint32/int32.", expr.Callee);

            // A method call on a local whose type has no such member is not a missing function.
            // `x = 5` then `x.bit_length()` reported "call to undefined function 'x_bit_length'":
            // a name the program never wrote, a category that is wrong, and two suggestions that
            // are both dead ends -- the spelling is right and no import adds a method to an int.
            // str, bytearray, list, dict and set already answer properly here; this is int and
            // float catching up with them (#207).
            if (expr.Callee is MemberAccessExpr { Object: VariableExpr numRecv } numMem
                && NumericLocalKind(numRecv.Name) is { } numKind)
            {
                throw NumericReceiverError(numRecv.Name, numKind, numMem.Member, expr.Callee);
            }

            // `mod.member()` where mod really is an imported module. "call to undefined function
            // 'utime_localtime' (typo, or a missing import?)" names a symbol the program never
            // wrote and offers two answers that are both wrong: the spelling is right and the
            // import is already there. A compat module that resolves and does not carry the
            // member is the case behind issue #475, and what the reader needs is which module
            // was asked and what it does carry.
            if (expr.Callee is MemberAccessExpr { Object: VariableExpr modRecv } modMem
                && modules.ContainsKey(modRecv.Name))
            {
                string realMod = TryImportedAlias(modRecv.Name, out var rmName) && rmName != null
                    ? rmName : modRecv.Name;
                string spelled = realMod == modRecv.Name
                    ? $"'{modRecv.Name}'"
                    : $"'{modRecv.Name}' ({realMod})";
                string prefix = realMod.Replace('.', '_') + "_";
                // The module's own functions, not its classes' methods: both are filed under
                // `<module>_<name>`, and `Pin_high` is not something to write after `machine.`.
                // A method is recognised by the class in front of it, never by holding an
                // underscore -- `sleep_ms` and `ticks_diff` are most of what utime offers, and
                // a list that drops them advertises one name out of eight.
                var callables = functionReturnTypes.Keys
                    .Concat(inlineFunctions.Keys)
                    .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(k => k[prefix.Length..])
                    // A class is filed under its BARE name, with its module kept beside it.
                    .Concat(classModuleMap.Where(kv => kv.Value == prefix).Select(kv => kv.Key))
                    .Where(n => n.Length > 0 && n[0] != '_' && !NamesAMethodOfAClass(prefix, n))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();

                // Module-level constants, after the callables. `framebuf` is classes and
                // constants and nothing else, so a list built from functions alone came out
                // EMPTY on it -- and sorting the two together put eight format constants in
                // front of FrameBuffer, which is the name the reader was reaching for.
                var constants = globals.Keys
                    .Concat(mutableGlobals.Keys)
                    .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(k => k[prefix.Length..])
                    .Where(n => n.Length > 0 && n[0] != '_' && !NamesAMethodOfAClass(prefix, n))
                    .Where(n => !callables.Contains(n, StringComparer.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();

                var has = callables.Concat(constants).ToList();
                string carries = has.Count > 0
                    ? $" It does define {string.Join(", ", has.Take(10))}"
                      + (has.Count > 10 ? ", ..." : "") + "."
                    : "";
                throw UserError(
                    $"module {spelled} does not define '{modMem.Member}'. The import resolved, so "
                    + $"this is not a missing import: the module is here and this name is not part "
                    + $"of what it provides on this chip.{carries}", expr.Callee);
            }

            throw UserError($"call to undefined function '{shown}' (typo, or a missing import?)",
                            expr.Callee);
        }

        bool calleeIsKnownFunc = functionParams.ContainsKey(callee);
        // Resolve any keyword arguments into positional order before evaluating them.
        var callArgs = ReorderCallArgs(expr.Args, callee, expr.Callee);
        // RFC 0009: the tag table is keyed by the resolved (module-mangled) name the
        // Call below emits; a still-dotted callee resolves the same way here.
        string tagCallee = callee;
        {
            int tagDot = tagCallee.IndexOf('.');
            if (tagDot != -1 && modules.ContainsKey(tagCallee[..tagDot]))
                tagCallee = tagCallee[..tagDot] + "_" + tagCallee[(tagDot + 1)..];
        }
        var argValuesL = new List<Val>();
        for (int ai = 0; ai < callArgs.Count; ++ai)
        {
            var arg = callArgs[ai];
            // A tagged union parameter reads a live Optional's tag byte, not just its
            // payload -- so the bare-name read that would otherwise refuse is allowed.
            // A possibly-Optional argument to ANY parameter also reads free: the
            // dispatch below the loop hands its member to an untagged parameter.
            bool argIsTagged = IsTaggedParam(tagCallee, ai);
            if (argIsTagged || CouldBeGuardedOperand(arg)) optionalReadAllowed++;
            try
            {
            RefuseGridArgument(arg);
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
            // pass its base address rather than trying to load it as a scalar. A local or
            // parameter holding a number is not one, even when a module-level array shares
            // its name: `def h(b: uint8): g(b)` passed the address of the program's
            // `b = bytearray(...)`, and g printed 0 for 5.
            if (arg is VariableExpr argVe && !NameIsLocalScalar(argVe.Name))
            {
                string argQualified = (!string.IsNullOrEmpty(currentInlinePrefix)
                    ? currentInlinePrefix
                    : currentFunction + ".") + argVe.Name;
                // A parameter or local of this frame is what the name passes, never the
                // module array the fallback below would find under the same bare name. A
                // bound parameter goes on to the general path, which follows its binding.
                if (ShadowingFrameKey(argVe.Name) == null && !arraySizes.ContainsKey(argQualified))
                {
                    // Fall back to unqualified / module-level name
                    string altQ = currentModulePrefix + argVe.Name;
                    if (arraySizes.ContainsKey(altQ)) argQualified = altQ;
                    else if (arraySizes.ContainsKey(argVe.Name)) argQualified = argVe.Name;
                }
                if (arraySizes.ContainsKey(argQualified))
                {
                    // `z = b"QR"` then `f(z)`: a literal bound by name is a compile-time
                    // sequence, sized in arraySizes but with no contiguous storage, so its
                    // base named a label nothing defines and the link failed with
                    // `undefined reference to main_z`. Lay the elements out under a hidden
                    // name and pass that base, as the parameter-bound sequence below does.
                    if (!IsBufferStorageName(argQualified)
                        && ResolveConstSequenceExpr(argVe) is { } namedSeq
                        && MaterializeSequenceArg(namedSeq) is { } namedSeqBuf)
                    {
                        argValuesL.Add(namedSeqBuf);
                        continue;
                    }
                    argValuesL.Add(new ArrayBase(argQualified));
                    continue;
                }
            }

            // `f(bytearray(N))` / `f(bytearray([...]))`: bytearray() written INLINE as a call
            // argument (#380), rather than bound to a name first (`buf = bytearray(N); f(buf)`,
            // which already works -- the VariableExpr branch above passes it by address). The
            // recognition that lays out a fixed buffer for `bytearray(...)` lives in
            // VisitVarDecl (Assign.cs), reached until now only from an assignment target; a
            // call argument assigns nothing, so it fell to the generic call-expression
            // visitor, which has no lowering for the bytearray() builtin and answered
            // "unsupported Python builtin" -- true of no import providing it, false of what
            // happens with a name in between. Give the argument the same hidden binding
            // VisitVarDecl gives a name, under a name this expression alone cannot collide
            // with, and pass its address exactly as a named bytearray argument already is.
            // `f(bytes([...]))` / `f(bytes(N))` take the same path: a `bytes` parameter
            // already receives it exactly as `bytearray` does (#365, #431).
            if (TryEvalInlineBufferArg(arg) is { } bufferArg)
            {
                argValuesL.Add(bufferArg);
                continue;
            }
            if (TryEvalLiteralBufferArg(arg) is { } literalArg)
            {
                argValuesL.Add(literalArg);
                continue;
            }

            Val argEvaluated = VisitExpression(arg);
            // A field holding a fixed array reaches here through an inline binding as a
            // Variable naming the storage (`self.temp` -> `buf` -> the array's flat var).
            // Copying that Variable hands the callee the array's first byte where it needs
            // the base address, so marshal the base -- exactly like the bare-name branch.
            // TryResolveArrayStorageKey maps the function-qualified spelling (`main.b`) back
            // to the storage key the array was registered under (`b`), which a bare
            // arraySizes lookup misses. Only a contiguous array (registered in
            // arraysWithVariableIndex/moduleSramArrays) has a base label to take --
            // a flat sequence (slice temp, `s__0`,`s__1`) is in arraySizes too but has
            // no `s:` storage, so marshaling its base would dangle.
            // A real function's own buffer parameter is a pointer already, and the storage
            // normalization would strip `f.buf` to a module array that shares the bare name.
            if (argEvaluated is Variable argArrayVar
                && !bytearrayParams.Contains(argArrayVar.Name)
                && !(arg is VariableExpr scalarVe && NameIsLocalScalar(scalarVe.Name))
                && TryResolveArrayStorageKey(argArrayVar.Name, out var argStorage)
                && (arraysWithVariableIndex.Contains(argStorage)
                    || moduleSramArrays.Contains(argStorage)))
            {
                argEvaluated = new ArrayBase(argStorage);
            }
            // A parameter bound to a compile-time sequence has no storage at all: the
            // Variable it resolves to names a slot nobody writes (`write(buf)` ->
            // `writeto(addr, buf)` -> `_i2c_writeto(addr, buffer, n)` hands a real
            // subroutine the unbacked name). Give the elements a hidden buffer and
            // pass its base. Not when the bare name is a local scalar: the sequence
            // the resolution finds then is a module-level binding the parameter
            // shadows, and materializing it hands the callee the shadowed buffer.
            else if (argEvaluated is Variable
                     && arg is VariableExpr seqArgV
                     && !NameIsLocalScalar(seqArgV.Name)
                     && ResolveConstSequenceExpr(seqArgV) is { } seqElems
                     && MaterializeSequenceArg(seqElems) is { } seqBuf)
            {
                argEvaluated = seqBuf;
            }
            // Read now when a later argument can have an effect: the call instruction reads
            // a name argument where it runs, after every argument has been evaluated.
            if (callArgs.Skip(ai + 1).Any(a => OperandCanHaveAnEffect(a is KeywordArgExpr ka ? ka.Value : a)))
                argEvaluated = SnapshotRead(argEvaluated);
            argValuesL.Add(argEvaluated);
            }
            finally { if (argIsTagged || CouldBeGuardedOperand(arg)) optionalReadAllowed--; }
        }

        if (functionParamDeclared.TryGetValue(callee, out var declaredParamTypes)
            && functionParams.TryGetValue(callee, out var declaredParamNames))
        {
            string written = expr.Callee switch
            {
                VariableExpr wv => wv.Name,
                MemberAccessExpr wm => wm.Member,
                _ => callee,
            };
            for (int ai = 0; ai < argValuesL.Count && ai < callArgs.Count
                             && ai < declaredParamTypes.Count && ai < declaredParamNames.Count; ++ai)
            {
                var rawArg = callArgs[ai] is KeywordArgExpr kwa ? kwa.Value : callArgs[ai];
                RefuseBufferForNumberParam(written, declaredParamNames[ai], declaredParamTypes[ai],
                    rawArg, argValuesL[ai],
                    unannotatedScalar: string.IsNullOrEmpty(declaredParamTypes[ai])
                                       && !bytearrayParams.Contains(callee + "." + declaredParamNames[ai]));
                RefuseNumberForBufferParam(written, declaredParamNames[ai], declaredParamTypes[ai],
                    rawArg, argValuesL[ai]);
            }
        }

        // RFC 0009: a live Optional argument bound for a parameter that is not
        // Optional dispatches on the tag -- each member marshals at its own
        // width into the same call; the None member raises TypeError at the
        // call boundary. The args were each evaluated once above; the leaf
        // recursion hands them to VisitCall as PreEvaluatedExpr so nothing
        // runs twice. Only the first such arg dispatches here -- a second
        // optional arg is caught by the same scan inside the leaf's call.
        for (int ai = 0; ai < argValuesL.Count && ai < callArgs.Count; ++ai)
        {
            if (IsTaggedParam(callee, ai)) continue;
            // The arg is already evaluated -- build its dispatch entry from the
            // value, never by re-lowering the expression (a call in it would
            // run twice).
            var argG = new GuardedOperand { Ast = callArgs[ai], Evaluated = argValuesL[ai] };
            if (argValuesL[ai] is NoneVal)
                argG.AlwaysNone = true;
            else if (TagOfVal(argValuesL[ai]) is { } at
                     && ValNameOf(argValuesL[ai]) is { } avn
                     && !narrowedOptionals.ContainsKey(avn) && !noneValuedNames.Contains(avn)
                     && optionalMembersByName.TryGetValue(avn, out var avm) && avm.Count > 0)
            {
                argG.Tag = at;
                argG.Members = avm;
                argG.Key = avn;
                argG.Payload = argValuesL[ai];
            }
            else
                continue;
            var staged = new Expression[argValuesL.Count];
            for (int sj = 0; sj < argValuesL.Count; ++sj)
                staged[sj] = new PreEvaluatedExpr(argValuesL[sj], null)
                    { Line = callArgs[sj].Line };
            string pName = functionParams.TryGetValue(callee, out var fp) && ai < fp.Count
                ? fp[ai] : $"arg{ai + 1}";
            string pType = functionParamTypes.TryGetValue(callee, out var fpt) && ai < fpt.Count
                ? DataTypeToSuffixStr(fpt[ai]) : "int";
            return TryEmitGuardedCallArg(expr, ai, pName, pType, argG, staged)!;
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
            // `A.f(x)` where f takes self. CPython binds x to self and the argument the reader
            // meant has nowhere to go; here the arity message named `A_f` and two counts, which
            // is a symbol the program does not contain and a parameter it cannot see (#201).
            // Say which call it is and what the two ways out are. Dropping `self` is the second
            // one because a method without it IS compiled under this name and reached by
            // exactly this call.
            //
            // The receiver decides, not the parameter name: an outlined method's first synthetic
            // parameter is `self_<field>` rather than `self`, so reading the name would have
            // missed every method that carries data.
            if (callArgs.Count != paramNames.Count
                && expr.Callee is MemberAccessExpr { Object: VariableExpr clsRecv } clsMa
                && classNames.Contains(clsRecv.Name) && InstanceClassOfName(clsRecv.Name) == null
                && methodInstanceTypes.ContainsKey(callee))
                throw UserError(
                    $"'{clsRecv.Name}.{clsMa.Member}(...)' calls a method that takes 'self' through "
                    + "the class itself, and there is no instance for it, so the first argument "
                    + $"would become 'self'. Call it on an instance (`obj.{clsMa.Member}(...)`), or "
                    + $"drop 'self' from the definition, which makes '{clsRecv.Name}.{clsMa.Member}"
                    + "(...)' the way to call it.",
                    expr.Callee);

            if (callArgs.Count > paramNames.Count)
                throw UserError(
                    $"Function '{callee}' expects {paramNames.Count} arguments, but {callArgs.Count} were provided", expr.Callee);
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
                            $"Function '{callee}' expects {paramNames.Count} arguments, but {callArgs.Count} were provided", expr.Callee);
                    argValuesL.Add(VisitExpression(def));
                }
            }
            var paramTypes = functionParamTypes.TryGetValue(callee, out var pt) ? pt : new List<DataType>();
            for (int i = 0; i < argValuesL.Count; ++i)
            {
                string paramVarName = callee + "." + paramNames[i];
                Val argVal = argValuesL[i];
                // A param the signature scan never typed takes the argument's own width:
                // defaulting to uint8 truncated a wide arg's marshalled copy before the
                // callee ever read it.
                DataType ptype = i < paramTypes.Count ? paramTypes[i]
                    : argVal is Variable or Temporary or Constant ? GetValType(argVal)
                    : DataType.UINT8;

                // A flash-string-by-reference argument is a 16-bit flash address, regardless
                // of how the const[str] param's nominal type folds.
                if (argVal is FlashStrAddr) ptype = FlashPtrType;

                if (IsStrParamSlot(callee, i))
                {
                    Expression? strArgExpr = i < callArgs.Count ? callArgs[i]
                        : functionParamDefaults.TryGetValue(callee, out var strDefs) && i < strDefs.Count
                            ? strDefs[i] : null;
                    argVal = BindStrParamArg(callee, paramNames[i], strArgExpr, argVal, expr);
                    argValuesL[i] = argVal;
                    ptype = FlashPtrType;
                }
                else if (functionParamDeclared.ContainsKey(callee)
                         && !IsBufferParam(callee, i, paramNames[i])
                         && (argVal is FlashStrAddr
                             || IsRuntimeStrArgument(i < callArgs.Count ? callArgs[i] : null)))
                    RefuseTextForNonStrParam(callee, paramNames[i],
                        i < callArgs.Count ? callArgs[i] : null, expr);

                // A buffer parameter takes an ADDRESS. A chip register evaluates to its
                // CONTENTS, so `f(PORTB)` hands the callee whatever happens to be in the port,
                // and every `reg[i]` inside then reads or writes at that number. It compiled
                // either way before -- as a bit test of a discarded copy, or as a store through
                // the port's contents -- and neither did what the program says. There is no
                // spelling that makes it work (`ptr(PORTB)` evaluates to the same contents), so
                // name it rather than pick between two wrong answers.
                if (argVal is MemoryAddress && bytearrayParams.Contains(paramVarName))
                {
                    string shown = i < callArgs.Count && callArgs[i] is VariableExpr regArg
                        ? $"'{regArg.Name}'" : "that argument";
                    throw UserError(
                        $"{shown} is a chip register, and '{paramNames[i]}' is indexed in "
                        + $"'{callee}' as a buffer, so it needs the ADDRESS of some bytes. A "
                        + "register argument passes its CONTENTS, and every "
                        + $"{paramNames[i]}[i] in the callee would read or write at that number. "
                        + "Pass a buffer (`buf: uint8[N]`), or index the register where it is "
                        + $"declared and pass the bit instead (`def {callee}(b): PORTx[b] = 1`).",
                        i < callArgs.Count ? callArgs[i] : null);
                }

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

                NoteArgumentStore(paramVarName, ptype, argVal);

                // Coerce a scalar argument to the callee's DECLARED param width (see
                // CoerceArgToParamWidth for why the backend needs it).
                if (i < paramTypes.Count
                    && CoerceArgToParamWidth(argVal, ptype) is var coerced && !ReferenceEquals(coerced, argVal))
                {
                    argValuesL[i] = coerced;
                    argVal = coerced;
                }
                // An integer argument to a FLOAT parameter is converted here. The Call
                // marshals the integer's own bytes and the callee reads them as a float, so
                // `half(6)` for `def half(a: float)` received 0.0. A literal converts now;
                // a run-time integer through a float temporary, which the backend converts.
                // Not a tagged union parameter: its slot is FLOAT when a float is the widest
                // member, and the tag spliced in below is read off the argument as written.
                else if (i < paramTypes.Count && ptype == DataType.FLOAT
                         && GetValType(argVal) != DataType.FLOAT && !IsTaggedParam(callee, i))
                {
                    if (argVal is Constant { Text: null } fArgC)
                        argVal = new FloatConstant(fArgC.Value);
                    else if (argVal is Variable or Temporary && IsScalarIntType(GetValType(argVal)))
                    {
                        var fCoerced = MakeTemp(DataType.FLOAT);
                        Emit(new Copy(argVal, fCoerced));
                        argVal = fCoerced;
                    }
                    argValuesL[i] = argVal;
                }

                // RFC 0009: a still-tagged argument only fits a parameter that carries
                // its own tag byte -- a plain parameter would keep the payload and drop
                // which member it is. Bare names were already refused at the read; a
                // call result or other composite reaches here tagged and answers here.
                if (!IsTaggedParam(callee, i))
                    RefuseOptionalPayloadStore(argVal,
                        i < callArgs.Count ? callArgs[i] : expr.Callee);

                Emit(new Copy(argVal, new Variable(paramVarName, ptype)));
            }
        }

        // RFC 0009: a tagged union parameter's member byte rides as the argument
        // right after its payload; a callee with none keeps the list untouched.
        argValuesL = WithParamTags(callee, argValuesL, callArgs);

        // A "void" entry is also the parser's default for an UNANNOTATED def whose
        // return type inference found nothing it could name -- `def f(): return
        // <list local>` keeps "void" even though the body hands back a real value.
        // The emitted `return <list var>` recorded that answer already, so only
        // treat the callee as void when no list return was seen.
        bool returnsVoidEnd = functionReturnTypes.TryGetValue(callee, out string? rType)
            && (rType == "void" || rType == "None")
            && !funcListReturnElems.ContainsKey(callee)
            && (!funcReturnLocalLists.Contains(callee) || regularResultDiscarded);

        if (returnsVoidEnd)
        {
            Emit(new Call(callee, argValuesL, new NoneVal()));
            return VoidCallResult(callee);
        }

        // Type the result temp with the callee's declared return type. Defaulting to uint8 lost
        // the signedness/width of a direct call result (e.g. `print(neg_of(x))` where neg_of
        // returns int8 picked the unsigned formatter, showing 251 instead of -5).
        //
        // `list[T]` (and array.array, which reaches the same UNKNOWN width) has no case in
        // StringToDataType, so it fell to UNKNOWN here -- the actual runtime shape is a GC
        // pointer, not "no known width". lastCallReturnTypeText carries the raw text past this
        // return so a bare `x = f()` assignment can register x as that list, not merely widen
        // the UNKNOWN it would otherwise keep.
        lastCallReturnTypeText = rType;
        lastCallReturnTypeExpr = expr;
        // A live union call carries the widest member's payload width, exactly as the
        // inline result temp above: `Union[...]` resolves to UNKNOWN in StringToDataType
        // and would leave the destination a byte.
        // The unannotated counterpart of the declared text: an outlined callee whose
        // `return <list var>` already ran recorded the element type under its name,
        // and the result temp is a GC pointer even though no annotation says so.
        lastCallReturnListElem = funcListReturnElems.TryGetValue(callee, out var flre)
            ? flre : (DataType?)null;
        // The callee's body has not been compiled yet and its def does not say it returns a
        // list: nothing here can type the result. Refused rather than lowered as the void
        // call it used to be, whose result `print(f())` read out of a stale register.
        if (lastCallReturnListElem == null && !IsListLikeReturnType(rType)
            && funcReturnLocalLists.Contains(callee))
            throw UserError(
                $"'{callee}' returns a list, and this call is compiled before the function's " +
                "body, so its element type is not known here. Annotate the return type, like " +
                $"`def {callee}(...) -> list[uint8]:`", expr);
        // RFC 0001 Model B: a factory declared `-> C` for a single-field class returns the
        // field itself, and its IR return type already says so (VisitFunctionDef). The class
        // name has no width of its own, so without this the result temp was UNKNOWN.
        DataType retDt = functionReturnMembers.TryGetValue(callee, out var cMembers)
            ? UnionPayloadType(cMembers)
            : IsListLikeReturnType(rType) || lastCallReturnListElem.HasValue
                ? DataType.GC_REF
            : rType != null && zcaFactoryClasses.TryGetValue(rType, out var handleFieldType)
                ? DataTypeExtensions.StringToDataType(handleFieldType)
            : rType != null && rType.Length > 0 ? DataTypeExtensions.StringToDataType(rType)
            : DataType.UINT8;
        Temporary dstC = MakeTemp(retDt);
        EmitMaybeTaggedCall(callee, argValuesL, dstC);
        // The dispatched callee's declared return names a user class: this temp is that
        // instance's carrier whatever the call's spelling or splicing was (a `*(...)`
        // rewrite swaps the node, the value does not care). zca handles additionally
        // register in instanceClasses below because the temp IS the field.
        StampProducedClass(dstC, rType);
        // A factory's result is a handle instance whether or not it is bound to a name:
        // `rd(make(2))` binds the parameter to this temporary, and a parameter typed with
        // the class reads its field through the handle only when the handle is known to be
        // one. Untagged, `s.base` flattened to a name nothing wrote and read 0, while
        // `o = make(2); rd(o)` read 2.
        if (rType != null && zcaFactoryClasses.ContainsKey(rType))
        {
            instanceClasses[dstC.Name] = rType;
            factoryHandleInstances.Add(dstC.Name);
        }
        // An outlined sequence result whose element type is resolvable at the call
        // site: `-> list[T]` names it, and a bare `-> list`/`-> tuple` answers
        // through the `return <seq>` name the scan recorded. The callee's own body
        // emits after every module-level caller, so funcListReturnElems cannot
        // answer yet -- without this `print(f())` and a bare `x = f()` saw a GC
        // pointer where the sequence's repr and len() belong.
        if (retDt == DataType.GC_REF && !listVarElemTypes.ContainsKey(dstC.Name)
            && ResolveOutlinedSeqElem(callee, rType, argValuesL) is { } seqElem)
        {
            listVarElemTypes[dstC.Name] = seqElem;
            if (rType != null && (rType.Contains("tuple") || rType.Contains("Tuple")))
                tupleBoundNames.Add(dstC.Name);
        }
        else if (retDt == DataType.GC_REF && !listVarElemTypes.ContainsKey(dstC.Name)
                 && lastCallReturnListElem is { } callListElem)
        {
            listVarElemTypes[dstC.Name] = callListElem;
        }
        return dstC;
    }

    /// <summary>
    /// The class a @classmethod's <c>cls</c> parameter currently names, or null.
    /// </summary>
    private string? ClassmethodClsOf(string name)
    {
        string q = currentInlinePrefix + name;
        if (classmethodClsAlias.TryGetValue(q, out var c)) return c;
        if (classmethodClsAlias.TryGetValue(name, out c)) return c;
        return null;
    }

    /// <summary>
    /// The class an expression names: a class identifier, or <c>cls</c> inside a
    /// @classmethod expansion. Null when the expression is an instance or anything else.
    /// </summary>
    private string? ClassNameOf(Expression e)
    {
        if (e is not VariableExpr ve) return null;
        if (ClassmethodClsOf(ve.Name) is { } mapped)
            return classNames.Contains(mapped) ? mapped : ClassNameForDescriptorRewrite(mapped);
        if (classNames.Contains(ve.Name)) return ve.Name;
        string resolved = ResolveCallee(ve.Name);
        return classNames.Contains(resolved) ? resolved : null;
    }

    private string ClassAttrKey(string cls, string member)
    {
        // Bare class names are in classModuleMap (`Mode` -> `adafruit_sht4x_`).
        // Inherited @classmethod expansions stash the mangled key
        // (`adafruit_sht4x_Mode`); prefixing that again doubled the module.
        if (classModuleMap.TryGetValue(cls, out var p) && p != null)
            return p + cls + "_" + member;
        foreach (var (bare, pfx) in classModuleMap)
        {
            string mangled = (pfx ?? "") + bare;
            if (string.Equals(mangled, cls, StringComparison.Ordinal))
                return mangled + "_" + member;
        }
        return currentModulePrefix + cls + "_" + member;
    }

    /// <summary>
    /// <c>setattr(cls, "NAME", value)</c> at compile time: bind a class attribute.
    /// Adafruit CV.add_values writes <c>setattr(cls, name, value)</c> for each tuple.
    /// </summary>
    private Val? TryEmitCompileTimeSetattr(CallExpr expr)
    {
        if (expr.Callee is not VariableExpr { Name: "setattr" }) return null;
        if (expr.Args.Count != 3) return null;
        if (ClassNameOf(expr.Args[0]) is not { } cls) return null;
        if (StaticStringOf(expr.Args[1]) is not { } attr) return null;

        string key = ClassAttrKey(cls, attr);
        Expression lit = LiteralizeClassAttrValue(expr.Args[2]);
        if (lit is IntegerLiteral il)
        {
            globals[key] = new SymbolInfo { IsMemoryAddress = false, Value = il.Value };
            return new NoneVal();
        }
        if (lit is StringLiteral sl)
        {
            strConstantVariables[key] = sl.Value;
            if (sl.Value.Length == 1)
                globals[key] = new SymbolInfo { IsMemoryAddress = false, Value = sl.Value[0] };
            return new NoneVal();
        }
        if (lit is FloatLiteral fl)
        {
            floatConstantVariables[key] = fl.Value;
            return new NoneVal();
        }
        if (TryEvalElemConst(expr.Args[2], out int iv))
        {
            globals[key] = new SymbolInfo { IsMemoryAddress = false, Value = iv };
            return new NoneVal();
        }
        return null;
    }

    private Expression LiteralizeClassAttrValue(Expression e)
    {
        if (e is IntegerLiteral or FloatLiteral or StringLiteral or BooleanLiteral) return e;
        if (e is VariableExpr ve)
        {
            foreach (var key in Qualifications(ve.Name))
            {
                if (floatConstantVariables.TryGetValue(key, out var fv))
                    return new FloatLiteral(fv) { Line = e.Line };
                if (strConstantVariables.TryGetValue(key, out var sv))
                    return new StringLiteral(sv) { Line = e.Line };
                if (constantVariables.TryGetValue(key, out int iv))
                    return new IntegerLiteral(iv) { Line = e.Line };
            }
        }
        if (TryEvalConstStrElement(e, out var text))
            return new StringLiteral(text) { Line = e.Line };
        if (TryEvalElemConst(e, out int n))
            return new IntegerLiteral(n) { Line = e.Line };
        return e;
    }

    /// <summary>
    /// <c>cls.string[k] = v</c> / <c>Mode.delay[code] = 0.01</c>: accumulate a compile-time
    /// class dict entry. Empty <c>cls.string = {}</c> is registered by EmitMemberAssign.
    /// </summary>
    private void AccumulateClassDictEntry(string dictKey, Expression keyExpr, Expression valueExpr)
    {
        var entries = dictLiteralBindings.TryGetValue(dictKey, out var existing)
            ? new List<(Expression, Expression)>(existing.Entries)
            : new List<(Expression, Expression)>();
        entries.Add((LiteralizeClassAttrValue(keyExpr), LiteralizeClassAttrValue(valueExpr)));
        dictLiteralBindings[dictKey] = new DictExpr(entries);
    }

    /// <summary>
    /// An integer constant bound to a parameter of declared width, narrowed to that width.
    /// A call to a real subroutine gets this from the ABI: the value is copied into the
    /// parameter's slot and the slot is exactly as wide as the declaration says. An @inline
    /// expansion has no slot -- the parameter IS the constant -- so the narrowing has to
    /// happen at the binding, or the same callee body sees a different value depending on
    /// which way it was called.
    ///
    /// What that cost: `_delay_ms_pic14e(ms: uint8)` is `while i < ms` over a uint8 counter.
    /// Bound to 500 rather than to 244, the range fold reads the test as one the counter can
    /// never fail, removes it, and the Curiosity Nano blink became a loop with no exit --
    /// one LATA write and then 600 million cycles of nothing. The subroutine form of the
    /// same function had always stored 0xF4.
    ///
    /// Only the four explicit fixed-width spellings are narrowed. An UNANNOTATED parameter
    /// must be left alone: its annotation is the empty string, which StringToDataType maps
    /// to uint8, and narrowing on that would truncate `f(300)` for a `def f(x):` that never
    /// declared a width. `int` is left alone for the same reason -- it is the width-free
    /// spelling. A const[...] parameter never reaches these sites; it is bound earlier.
    /// </summary>
    private static int NarrowConstantArgToParam(int value, string? declaredType) => declaredType switch
    {
        "uint8" => (byte)value,
        "int8" => (sbyte)value,
        "uint16" => (ushort)value,
        "int16" => (short)value,
        _ => value,
    };

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
            // Located at the recursive call. The frame has not moved into the callee yet, so
            // currentSourcePath still names the file this call is written in, and the call's
            // own node gives the line in that file. `currentStmtLine` is a line of the ENTRY
            // file's statement, and paired with a driver's path it pointed at a docstring of
            // pwmio.py.
            string rn = func?.Name ?? callee;
            var at = UserError(
                $"function '{rn}' is recursive; PyMCU has no call frame for inlined " +
                "or ZCA methods, so recursion is not supported — rewrite it as a loop", expr);
            throw new RecursionError(at.Message, at.Line, at.Column, at.Length)
                { File = at.File, LocationIsFinal = true };
        }

        if (func != null)
        {
            CheckSignatureShape(func, expr.Args, QualNameForCall(func, callee), expr);
            KeywordsToPositions(func, expr.Args);
        }

        // @warning("..."): print the author-supplied note (once per function)
        // when a call to this function is expanded. Informational only -- it
        // does NOT abort compilation, so flagged-but-usable features (soft-float,
        // reduced bare-metal behaviour) still build.
        if (func != null && !string.IsNullOrEmpty(func.WarningMessage) && warningNoticed.Add(func.Name))
        {
            PyMCU.Common.Diagnostic.Warning(func.WarningMessage);
        }

        // Read and cleared here, before the arguments are visited: a call nested in an argument
        // is read by THIS call, whatever this call's own result becomes (#302). The tuple
        // request gets the same treatment: it belongs to this call alone, and a call nested
        // in an argument must not inherit its slot count.
        bool resultDiscarded = callResultIsDiscarded;
        callResultIsDiscarded = false;
        int wantTupleCount = pendingTupleCount;
        pendingTupleCount = 0;

        var exitLabel = MakeLabel();
        var newDepth = inlineDepth + 1;
        var newPrefix = $"inline{newDepth}.{InlineFrameScope(".")}{func?.Name}.";

        // The prefix repeats for every expansion at this depth, and the callee's
        // own locals file under it (`inline2._parse_color.r`): a previous
        // expansion's constant/str/alias bindings would leak into this one and
        // fold a rebind like `b = 0` to the stale value -- `pixels[i] =
        // wheel(...)` reached _parse_color carrying fill()'s constants. Clean
        // before the parameters bind.
        CleanCtState(newPrefix[..^1]);

        Temporary? result = null;
        var tupleResultNames = new List<string>();
        // `return struct.unpack_from(fmt, buf, off)`: the element count lives in the format
        // text, which only a field binding inside the body can resolve -- no signature or
        // TupleExpr scan sees it. The return mints its own slots under this prefix when the
        // call site asked for the tuple by sentinel and got zero.
        // The sequence component makes every expansion's slots unique: `f()[k]` and `*f()`
        // hand the caller a slot NAME it may read only after a sibling call's expansion has
        // run, and a prefix shared per depth let the later expansion overwrite the first
        // one's result (`pair(a)[0] + pair(b)[0]` read b+a's slot twice).
        string tupleSlotPrefix =
            $"{(string.IsNullOrEmpty(currentFunction) ? "main" : currentFunction)}.iret_{newDepth}_{inlineTupleSeq++}_";

        // `-> (T1, T2)` / `-> tuple[T1, T2]`: the arity is part of the signature, so a call
        // that unpacks a different number of targets is a mismatch worth naming here -- the
        // generic "Expected N tuple results, got M" fires far from the declaration.
        var declaredTupleElems = TupleType.ElementTypes(func?.ReturnType);

        // `f()[k]` / `x = f()`: the call site wants the tuple's SLOTS, not unpack
        // targets it wrote. The sentinel asks for them; the arity comes from the
        // declaration, or for an unannotated callee from its tuple returns.
        if (wantTupleCount < 0)
            wantTupleCount = declaredTupleElems.Count > 0
                ? declaredTupleElems.Count
                : TupleReturnArity(func);
        if (declaredTupleElems.Count > 0)
        {
            string declared = TupleType.Describe(func!.ReturnType);
            if (wantTupleCount == 0)
                throw UserError(
                    $"'{func.Name}' returns {declaredTupleElems.Count} values {declared}; " +
                    $"unpack them into {declaredTupleElems.Count} targets", expr.Callee);
            if (wantTupleCount != declaredTupleElems.Count)
                throw UserError(
                    $"'{func.Name}' is declared to return {declaredTupleElems.Count} values " +
                    $"{declared}, but {wantTupleCount} unpack target(s) were given", expr.Callee);
        }

        if (wantTupleCount > 0)
        {
            for (int k = 0; k < wantTupleCount; ++k)
            {
                string slot = tupleSlotPrefix + k;
                tupleResultNames.Add(slot);
                // The annotated element type widens the result slot; without an annotation the
                // slot stays uint8, as it has always been.
                if (k < declaredTupleElems.Count)
                    variableTypes[slot] = DataTypeExtensions.StringToDataType(declaredTupleElems[k]);
                else
                    variableTypes.Remove(slot);
            }
        }
        else if (func.ReturnType != "void" && func.ReturnType != "None")
        {
            // See the identical note in EmitRegularFunctionCall: `list[T]` (and array.array)
            // has no StringToDataType case, so the result temp needs GC_REF, not UNKNOWN, and
            // a bare `x = f()` assignment needs the raw text to register x as that list.
            lastCallReturnTypeText = func.ReturnType;
            lastCallReturnTypeExpr = expr;
            // A live union result carries the widest member's payload width: `Union[...]`
            // has no StringToDataType case either, and typing the temp UNKNOWN made the
            // caller's `t = dhtDevice.temperature` a one-byte destination that dropped the
            // float payload's upper bytes (RFC 0009, adafruit_dht).
            // An inlined callee's `return <list var>` registers the expansion's own
            // ResultTemp instead -- the assignment picks it up from the value, so
            // there is no emitted-name entry to read here.
            lastCallReturnListElem = null;
            result = MakeTemp(
                functionReturnMembers.TryGetValue(callee, out var resMembers)
                    ? UnionPayloadType(resMembers)
                    // A union-returning body that always inlines (a @property getter,
                    // for one) is never a functionsToCompile candidate, so its member
                    // list lives only on the FunctionDef (RFC 0009, adafruit_dht).
                    : func.ReturnMembers is { } fm
                        ? UnionPayloadType(fm)
                    : IsListLikeReturnType(func.ReturnType)
                        ? DataType.GC_REF
                        : DataTypeExtensions.StringToDataType(func.ReturnType));
        }

        var argValues = new List<Val>();

        bool isConstructor = callee.EndsWith("___init__") || callee.Contains("___init____");
        if (isConstructor) NoteConstructedClass(callee);
        int paramOffset = 0;

        if (!isConstructor)
        {
            // Class.method(args): cls is the receiver class. Do not visit the class as a
            // value -- there is no runtime class object -- and skip binding the first
            // parameter from the argument list (paramOffset = 1).
            if (func != null && func.IsClassMethod
                && expr.Callee is MemberAccessExpr { Object: VariableExpr clsRecvVe })
            {
                string owner = ClassmethodClsOf(clsRecvVe.Name)
                    ?? (classNames.Contains(clsRecvVe.Name) ? clsRecvVe.Name : null)
                    ?? ResolveCallee(clsRecvVe.Name);
                if (methodInstanceTypes.TryGetValue(callee, out var mt) && !string.IsNullOrEmpty(mt))
                    owner = ClassNameForDescriptorRewrite(mt);
                string clsParam = func.Params.Count > 0 ? func.Params[0].Name : "cls";
                classmethodClsAlias[newPrefix + clsParam] = owner;
                paramOffset = 1;
            }
            else if (expr.Callee is MemberAccessExpr mem2)
            {
                Val objVal = VisitExpression(mem2.Object);
                // The receiver may be a Temporary, not just a Variable -- e.g. a nested ZCA field
                // re-tagged with its class (machine.Pin._pin). Bind self off either, so a method
                // call on such a value still gets self (otherwise the @inline expansion reports
                // "missing required argument 'self'").
                //
                // A Temporary's name is resolved through ResolveClassCarryingName, not read
                // bare (#445): a @property returning a single-field ZCA instance hands back a
                // temp that ALIASES the field's own flattened storage rather than carrying a
                // class under its own name, and this re-evaluates mem2.Object independently of
                // the dispatch-decision code above -- a SECOND, different temp with the exact
                // same one-hop-short gap a direct instanceClasses.ContainsKey(recvName) missed.
                string? recvName = objVal is Variable v2 ? v2.Name
                                 : (objVal is Temporary t2 ? ResolveClassCarryingName(t2) : null);
                // An object-field receiver (a coroutine's `self.a`) lowers to a nameless
                // anchor, so neither arm above names it -- but its flattened name is the
                // instance self must bind to.
                if (recvName == null) recvName = AnchorNameOf(mem2.Object);
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
        // The keyword argument EXPRESSIONS, kept for their source position the way rawArgExprs
        // keeps the positional ones. `LCD(rs="PA0", ...)` passes every pin by keyword, so
        // without these the origin of a refused pin is lost at the first hop (#193).
        var rawKwArgExprs = new Dictionary<string, Expression>();
        // Keyword arguments that are an empty tuple or list (`preserve_dios=()`): they
        // bind an empty compile-time sequence, which a Val cannot carry, so the keys are
        // recorded here and bound in the keyword loop below.
        var rawKwEmptySeqs = new HashSet<string>();
        var rawStrArgs = new List<StringLiteral?>();
        // The argument EXPRESSIONS, parallel to argValues, kept for their source position.
        var rawArgExprs = new List<Expression?>();
        var rawListArgs = new List<ListExpr?>();
        // The base key of an argument that is a compile-time sequence of ZCA instances --
        // hoisted here, in the CALLER's scope, because the elements must be built exactly once
        // and the parameter is only another name for them. Parallel to argValues.
        var rawSeqBases = new List<string?>();
        // The elements of an argument that is a list of NUMBERS reached by name. Resolved here,
        // in the caller's scope, because the callee's prefix is already in place by the time the
        // parameters are bound.
        var rawConstSeqArgs = new List<List<Expression>?>();

        foreach (var rawArg in expr.Args)
        {
            var arg = rawArg;
            if (arg is KeywordArgExpr kw)
            {
                string savedOuterPct = pendingConstructorTarget;
                pendingConstructorTarget = "";
                if (kw.Value is TupleExpr { Elements.Count: 0 }
                    or ListExpr { Elements.Count: 0 })
                {
                    rawKwEmptySeqs.Add(kw.Key);
                    kwArgValues[kw.Key] = new NoneVal();
                }
                else
                {
                    kwArgValues[kw.Key] = TryEvalInlineBufferArg(kw.Value) is ArrayBase kwBuf
                        ? new Variable(kwBuf.ArrayName, DataType.UINT16)
                        : VisitExpression(kw.Value);
                    // The same rule a positional argument follows: a name or a field is
                    // read where the parameter binds, after every argument ran. A later
                    // argument that can have an effect makes the read happen here, in
                    // the order the call was written (`f(b=count, a=bump())`).
                    if (expr.Args.SkipWhile(a => !ReferenceEquals(a, rawArg)).Skip(1)
                            .Any(a => OperandCanHaveAnEffect(a is KeywordArgExpr ka ? ka.Value : a)))
                        kwArgValues[kw.Key] = SnapshotRead(kwArgValues[kw.Key]);
                }
                if (kw.Value is StringLiteral s) rawKwStrArgs[kw.Key] = s.Value;
                rawKwArgExprs[kw.Key] = kw.Value;
                // Always restore: inner ctor targets (anonymous __cN) must not
                // overwrite the outer assignment target (e.g. "main.spi").
                pendingConstructorTarget = savedOuterPct;
            }
            else
            {
                arg = HoistSlotCtorArg(arg);
                rawStrArgs.Add(arg as StringLiteral);
                rawArgExprs.Add(arg);
                // A bytes/list literal (b"Hi", [1,2,3]) or a tuple literal
                // ((r,g,b)) is a fixed sequence: normalise both to a ListExpr
                // and bind it to the inline parameter so the callee can consume
                // it via `for x in param` (unrolled) or `param[const]` indexing.
                // `bytes([...])` / `bytes(N)` written at the call site is the same shape one
                // call spelling later (#431; adafruit_bus_device's `bus_device.write(bytes([REG]))`,
                // where `write` is an @inline `for i, b in enumerate(buffer)`).
                ListExpr? seqLit = arg as ListExpr
                    ?? (arg is TupleExpr tple ? new ListExpr(tple.Elements) : null)
                    ?? (TryBytesLiteralElements(arg) is { } bytesLitElems ? new ListExpr(bytesLitElems) : null);

                // Elements fold HERE, in the caller's scope: a carried ListExpr is
                // re-evaluated under the callee's inline prefix, where a name bound in the
                // caller (`register` from `_read_register(reg, n)`) no longer resolves --
                // enumerate() then failed on elements that were constant when written.
                // Folding each element to its literal keeps the sequence binding exact in
                // any scope. A `bytes([expr])` / `bytearray([expr])` whose elements are
                // decided at RUN time has no constant sequence to bind at all; it wants
                // storage instead, and the buffer path below materializes it
                // (`i2c.write(bytes([reg & 0xFF]))` in adafruit_bmp280). A plain list/tuple
                // literal has no storage to offer, so it keeps the sequence binding either
                // way.
                if (seqLit != null)
                {
                    var foldedElems = new List<Expression>(seqLit.Elements.Count);
                    bool allElemsConst = true;
                    foreach (var se in seqLit.Elements)
                    {
                        if (TryEvalElemConst(se, out int sv)) foldedElems.Add(new IntegerLiteral(sv) { Line = se.Line });
                        else { allElemsConst = false; break; }
                    }
                    if (allElemsConst) seqLit = new ListExpr(foldedElems) { Line = seqLit.Line };
                    else if (arg is CallExpr) seqLit = null;
                    else seqLit = PinEffectfulElements(seqLit);
                }

                // `Bar([Pin("PD5", Pin.OUT), Pin("PD6", Pin.OUT)])`: a list of INSTANCES is not
                // raw AST to re-evaluate at each subscript -- the elements are built once here
                // and the parameter is bound to the base key they live under, which is the same
                // shape `objs = [A(1), A(2)]` already produces at module level.
                if (arg is ListCompExpr argComp && IsInstanceComprehension(argComp))
                {
                    rawSeqBases.Add(HoistInstanceComprehension(argComp));
                    rawConstSeqArgs.Add(null);
                    rawListArgs.Add(null);
                    argValues.Add(new NoneVal());
                    continue;
                }

                if (seqLit != null && IsInstanceSequenceLiteral(seqLit))
                {
                    rawSeqBases.Add(HoistInstanceSequence(seqLit));
                    rawConstSeqArgs.Add(null);
                    rawListArgs.Add(null);
                    argValues.Add(new NoneVal());
                    continue;
                }

                // A NAME already bound to such a sequence (`pins = [...]` then `Bar(pins)`) is
                // carried the same way: the parameter becomes another name for the same base.
                if (seqLit == null && TryResolveInstanceSequence(arg, out var namedSeqBase, out _))
                {
                    rawSeqBases.Add(namedSeqBase);
                    rawConstSeqArgs.Add(null);
                    rawListArgs.Add(null);
                    argValues.Add(new NoneVal());
                    continue;
                }

                rawSeqBases.Add(null);
                rawConstSeqArgs.Add(seqLit == null && arg is VariableExpr constSeqVe
                    ? ResolveConstSequence(constSeqVe.Name)
                    : null);
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
                    // `obj.m(bytearray([...]))` on an @inline callee (#459): lay out the
                    // buffer under a hidden name and hand the parameter its NAME, so it
                    // binds by alias exactly as a named buffer already does -- the callee
                    // then reads real storage, mutable and runtime-sized included.
                    if (TryEvalInlineBufferArg(arg) is ArrayBase inlineBuf)
                        argValues.Add(new Variable(inlineBuf.ArrayName, DataType.UINT16));
                    else
                        argValues.Add(EvalOptionalCarry(arg));
                    // A register read is a load the parameter binding performs, after every
                    // argument has been evaluated. When a later argument can have an effect
                    // -- one that may write the register -- the load happens here, in order.
                    // A name or a field is the same: read at the binding, it saw the later
                    // argument's write (`f(count, bump())` bound the bumped count).
                    if (expr.Args.SkipWhile(a => !ReferenceEquals(a, rawArg)).Skip(1)
                            .Any(a => OperandCanHaveAnEffect(a is KeywordArgExpr ka ? ka.Value : a)))
                        argValues[^1] = SnapshotRead(argValues[^1]);
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
        // While this body is expanded, diagnostics raised inside it belong to the file that
        // DEFINES it, not to the file that called it. Without this, a `for`-in over a plain
        // `str` parameter in an imported module reported the caller's file: either the
        // caller's line with the caller's name, or the callee's line with the caller's name,
        // which is a location that does not exist. Issue #164.
        //
        // Only set when a path was recorded. An unrecorded callee leaves the caller's path in
        // place, which is the pre-existing behaviour, and RecordSourcePaths covers every
        // function a module defines, so the gap is synthesized bodies rather than user code.
        // A NON-EMPTY recorded path is the only case where the callee can be named. An empty
        // one means the entry file or an unknown origin, and the two are not distinguishable
        // here, so the location stays on the call rather than borrowing a line from a file the
        // diagnostic is not going to name. Getting this wrong moves the LINE into the stdlib
        // while the FILE stays the user's, which is the incoherent location #164 is about,
        // just relocated: `LCD(rs="PA0")` on line 5 of a ten-line file reported line 151.
        string savedSourcePath = currentSourcePath;
        // The FILE label moves with the path. It did not, so an inlined body carried the
        // callee's line and the callee's text under the CALLER's file name: two different
        // stdlib modules both reported as main.py, at lines an eight-line main.py does not
        // have (issue #204). Both halves of a marker have to name the same file.
        string savedSourceFile = currentSourceFile;
        bool savedTracksCallee = inlineTracksCalleeLine;
        int savedCalleeLine = inlineCalleeStmtLine;
        // HELD, not applied. Everything between here and the body walk is argument binding,
        // and the only source nodes it has in hand are the CALLER's: the call expression, its
        // arguments and its keywords. Moving the file label here moved one half of the pair,
        // so the label named the callee while every line still came from the caller and nine
        // diagnostics reported a location that exists in neither program. Measured on the
        // repro in #227, all nine landed on `helper.py:7`, a blank line, and four of them drew
        // a caret at column 16 of it. Leaving both halves alone is coherent by induction: the
        // pair in effect for the calling statement stays in effect while its arguments bind,
        // and a nested expansion inherits the OUTER callee's pair, which is coherent too.
        // Issue #227.
        //
        // KNOWN, not non-empty. The test used to be `!string.IsNullOrEmpty(calleePath)`, and
        // the entry file's own functions are recorded with an EMPTY path, so they failed it
        // and their expansions tracked no callee line at all. Every UNLOCATED diagnostic
        // raised inside one then fell back to `currentStmtLine`, which is the call:
        //
        //     class Box:
        //         def __init__(self, n: uint8) -> None:
        //             self.buf: uint8[n]        <- line 7, what the message is about
        //     b = Box(s)                        <- line 13, what was reported
        //
        // The same class in an imported module already reported line 7, because a non-empty
        // path passed the test. `RecordSourcePaths` runs for the entry file too, so a
        // successful lookup is what says the origin is known, and an empty result means the
        // entry file rather than an unknown origin. Those two were conflated here, which the
        // comment above says outright; separating them is the whole change. An empty path
        // still leaves `LocatedFile` null, which the renderer already reads as the entry file,
        // so the FILE half is unchanged and only the line moves. Issue #233.
        string? calleeSourcePath =
            func != null && functionSourcePath.TryGetValue(func, out var calleePath)
                ? calleePath
                : null;

        string savedPrefix = currentInlinePrefix;
        currentInlinePrefix = newPrefix;

        // A nested @inline function with no `self` parameter of its own (`def bump():` inside
        // a method) that reads or writes `self.<field>` is closing over the enclosing method's
        // receiver -- valid Python needing no `nonlocal self` declaration, because only the
        // ATTRIBUTE is mutated, `self` itself is never rebound. Nothing else forwards `self`
        // into a callee with no receiver argument, so it fell through to an ordinary unbound
        // local of this new frame (`inline2.bump.self`) and every write to it was invisible
        // outside the nested call: `self.value = self.value + 1` silently did nothing (#427).
        // Mirror the enclosing frame's own self binding, the same way a method call mirrors
        // the RECEIVER's (a few lines below, for a real `self` parameter).
        if (func != null && (func.Params.Count == 0 || func.Params[0].Name != "self"))
        {
            // savedPrefix is empty inside a bound-instance subroutine -- no inline
            // frame wraps the body's `self`, whose alias lives on the
            // function-qualified name instead. A plain function's `func.self`
            // simply does not exist, so the lookups miss and nothing binds, as
            // before.
            string outerSelf = !string.IsNullOrEmpty(savedPrefix)
                ? savedPrefix + "self"
                : currentFunction + ".self";
            string newSelf = newPrefix + "self";
            if (variableAliases.TryGetValue(outerSelf, out var outerSelfTarget))
                variableAliases[newSelf] = outerSelfTarget;
            if (instanceClasses.TryGetValue(outerSelf, out var outerSelfCls) && outerSelfCls != null)
                instanceClasses[newSelf] = outerSelfCls;
        }

        // Register the callee's runtime-indexed local arrays so they are allocated as SRAM (not
        // register element-vars). An inlined fixed array is qualified with the expansion's own
        // prefix (FixedArrayKey), same as the load site, so scan under that prefix. The per-function
        // prescan only sees the caller's own body, never an inlined callee's locals, so without
        // this a runtime-indexed local array inside an @inline hit "subscript must be constant".
        if (func != null)
        {
            ScanForVariableIndexedArrays(func.Body.Statements, newPrefix,
                // The callee's own class, when it is a method: `Radio_send` minus `_send`. This
                // is the path that matters for a nested `self.m(...)`, because the per-function
                // prescan never sees an inlined callee's body (see the note above).
                callee.EndsWith("_" + func.Name, StringComparison.Ordinal)
                    ? callee[..^(func.Name.Length + 1)]
                    : null,
                listDeclPrefix: newPrefix);
            // Unlike the array scan above, an `x = []` binding inside the expanded body
            // is spelled with the inline prefix at the emit site, so scan under newPrefix.
            ScanPromotableEmptyLists(func.Body.Statements, newPrefix);
        }

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
            // Derive the MODULE prefix, and an overloaded callee is not `prefix + name`: it is
            // `prefix + name + "___" + <parameter types>`. Subtracting only the name left the
            // mangled key itself as the prefix, `mm_floor___`, and then every name inside the
            // body that happens to spell a parameter type resolved to that overload. `float(t)`
            // written in `floor(x: float)` found `mm_floor___float` and the build died with
            // "function 'floor' is recursive" about a function that does not call itself
            // (PyMCU#182). `int32` escaped only because it is an intrinsic and returns earlier.
            string bare = callee;
            int mangle = bare.IndexOf("___", StringComparison.Ordinal);
            if (mangle > 0 && bare.LastIndexOf(func.Name, mangle, StringComparison.Ordinal) >= 0)
                bare = bare[..mangle];
            if (func.Name.Length < bare.Length)
                currentModulePrefix = bare[..^func.Name.Length];
            else
                currentModulePrefix = "";
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
                // A GENERATOR constructed without a target cannot work, and used to compile
                // (#243). The reason is one line of design rather than a bug: a PyMCU instance
                // is not a value, it is a NAME with fields. Minting `__c<n>` for an ordinary
                // class is fine because its fields carry the content; a generator's synthesized
                // class has none, so the name has nothing behind it and the program passed an
                // uninitialised temporary. Measured: `take(gen())` emitted a call whose argument
                // occurred exactly once in the whole MIR, as that argument, never assigned.
                //
                // The legal consumption supplies a target: the `for` desugaring binds the
                // machine to its own `__gen` temp before polling it. So the branch is the test.
                // Nothing needs to know which value position it was -- argument, return,
                // operand -- because none of them can supply one.
                if (generatorClasses.Contains(classPrefix) && !loweringMemberReceiver)
                    throw UserError(loweringDiscardedExprStmt
                        // The result is thrown away, so this is not a value-position error. It
                        // is the trap every Python programmer meets once: CALLING A GENERATOR
                        // FUNCTION DOES NOT RUN ITS BODY, it builds the generator and returns
                        // it. In CPython the statement is a silent no-op and nothing can warn
                        // you. Here it can, and refusing to emit nothing where the author
                        // plainly expected effects is the whole point -- a silent no-op is the
                        // failure, not the refusal.
                        ? $"calling `{classPrefix}(...)` does not run its body: it is a "
                          + "generator function, so this statement builds a generator, throws "
                          + "it away, and does nothing at all. If you meant to run the body, "
                          + $"drive it: `for v in {classPrefix}(...):`."
                        : $"a generator can only be consumed by `for`: `{classPrefix}(...)` is "
                          + "used here as a value. A PyMCU generator lowers to a state machine "
                          + "that lives in a named variable, and there is nothing to hand to a "
                          + "caller, store, or return. Write `for v in "
                          + $"{classPrefix}(...):` and use `v` in the loop.", expr);

                string bBase = string.IsNullOrEmpty(currentFunction) ? "main" : currentFunction;
                target = bBase + ".__c" + (++ctorAnonId);
                ctorSubexprSynth = target;

                // This nameless instance is held in a field that some method writes through
                // (#183). The scan pass could see the write but not the name; now that the name
                // exists, give the written leaves real storage, BEFORE the body below lowers
                // `self.<leaf> = ...` -- registered after that, the constructor's store has
                // already been folded away and every reader keeps returning its value.
                if (anonCtorMutableLeaves.TryGetValue(expr, out var anonLeaves))
                    foreach (var (leaf, leafType) in anonLeaves)
                    {
                        string key = target + "_" + leaf;
                        killedConstants.Add(key);
                        moduleInstanceMutableFields.Add(key);
                        mutableGlobals[key] = DataTypeExtensions.StringToDataType(leafType);
                    }
            }

            variableAliases[selfName] = target;
            instanceClasses[selfName] = classPrefix;
            instanceClasses[target] = classPrefix;
            virtualInstances.Add(target);
        }

        inlineStack.Add(new InlineContext
            { ExitLabel = exitLabel, ResultTemp = result, ResultVars = tupleResultNames,
              TupleSlotPrefix = tupleSlotPrefix, CalleeName = callee,
              EntryInstructions = currentInstructions, EntryIndex = currentInstructions.Count,
              Prefix = newPrefix, EntryBranchDepth = _runtimeBranchDepth,
              FinallyDepth = finallyStack.Count,
              // For a constructor call the target was consumed above (self already
              // aliases it), so this is empty there; for `r = f()` it still holds
              // the caller's target, which each `return Cls(...)` in the body
              // must be offered again -- see VisitReturn.
              CtorTarget = pendingConstructorTarget,
              // Recorded here because the pair has not moved yet: the switch to the callee
              // happens at the body walk. See #227 and the note on the field.
              CallerSourcePath = currentSourcePath });

        // The callee's body joins this function's optional-capable precompute: an
        // `x = None` inside an inline expansion is still a runtime-optional name,
        // and the caller's own body scan never saw it (the set holds source
        // names, so the expansion's qualified locals match through SourcePartOf).
        CollectExpansionOptionalCapable(func.Body.Statements);

        var boundParams = new HashSet<int>();

        // Too MANY positional arguments used to be dropped on the floor: the loop below simply
        // stopped at the end of the parameter list. A free function has always rejected this
        // ("Function 'sink' expects 3 arguments, but 5 were provided"); an @inline function and
        // a constructor did not, so `Box(3, 99)` for `__init__(self, a)` built clean and the 99
        // vanished. What it cost: `UART(0, 9600)`, the MicroPython spelling, bound baud to 0 and
        // dropped the 9600, and a divisor of 0 is 1 Mbaud on a 16 MHz part. The too-FEW case was
        // already reported below, with the same phrasing this borrows.
        // The name the USER wrote, for diagnostics. The mangled callee carries the module prefix
        // (pymcu_hal_avr_uart_UART), which is not a name anyone typed and not one they can search
        // for in their own file.
        string SourceCalleeName() => expr.Callee switch
        {
            VariableExpr cv => cv.Name,
            MemberAccessExpr cm => cm.Member,
            _ => func.Name,
        };

        // Enforced only when the receiver assumption matches the definition: paramOffset 1 means
        // the callee is expected to take `self`. A module function reached through a dotted name
        // can be given that offset without having one, and counting against it would invent an
        // error on a call that is correct (`time.monotonic()` reported "expects -1 arguments").
        bool offsetMatchesSelf = paramOffset == 0
            || (func.Params.Count > 0 && func.Params[0].Name == "self")
            || (func.IsClassMethod && func.Params.Count > 0);

        // And CORRECTED, not only reported. A module function reached through a dotted name can
        // be handed the receiver offset without having a `self` to receive it, and then the
        // first argument was bound to the SECOND parameter: `alarm.sleep_until_alarms(ta)` left
        // `alarm0` bound to nothing, and the body's first read of it was reported as a name that
        // is not defined -- naming a parameter written in the signature two lines above, in a
        // library file the user never opened (#381).
        //
        // It took a name collision to reach: the module is a receiver here only because
        // something filed `alarm` as an instance, which a user's own `import time` against the
        // layer's module-level `time = _TimeAlarmModule()` is enough to do. The offset is wrong
        // whatever put it there, and a callee with no `self` is the one fact that settles it.
        if (!offsetMatchesSelf) paramOffset = 0;

        // `*args` and `**kwargs` stand for what the call site wrote BEYOND the declaration.
        // They take no part in the arity count, they are never bound by name, and they are
        // never "missing": an empty call gives them an empty sequence and an empty mapping
        // (#368). Python puts them in this order and both front ends preserve it, so the
        // ordinary positional parameters are the ones before the first of the two.
        int varArgIdx = func.Params.FindIndex(p => p.IsVarArg);
        int kwArgIdx = func.Params.FindIndex(p => p.IsKwArg);
        int firstVariadic = func.Params.Count;
        if (varArgIdx >= 0) firstVariadic = Math.Min(firstVariadic, varArgIdx);
        if (kwArgIdx >= 0) firstVariadic = Math.Min(firstVariadic, kwArgIdx);
        var extraPositional = new List<Expression>();
        var extraKeywords = new List<(string Key, Expression Value)>();

        int variadicCount = (varArgIdx >= 0 ? 1 : 0) + (kwArgIdx >= 0 ? 1 : 0);
        int declaredArgs = func.Params.Count - paramOffset - variadicCount;
        if (offsetMatchesSelf && varArgIdx < 0 && declaredArgs >= 0 && argValues.Count > declaredArgs)
        {
            bool isCtorX = callee.Contains("___init__", StringComparison.Ordinal);
            string whatX = isCtorX
                ? $"constructor of '{SourceCalleeName()}'"
                : $"'{SourceCalleeName()}'";
            throw UserError(
                $"too many arguments in call to {whatX}: it expects {declaredArgs} " +
                $"argument(s), but {argValues.Count} were provided", expr.Callee);
        }

        for (int i = 0; i < argValues.Count; ++i)
        {
            int paramIdx = i + paramOffset;
            if (paramIdx >= firstVariadic)
            {
                // Past the declared parameters. With a `*args` these are its elements; without
                // one the arity check above has already refused the call.
                if (varArgIdx >= 0)
                    extraPositional.Add(CarriedArgExpr(rawArgExprs[i], argValues[i]));
                continue;
            }
            string paramName = currentInlinePrefix + func.Params[paramIdx].Name;
            boundParams.Add(paramIdx);

            // Remember where this argument was written, for a refusal that is about the
            // argument rather than about the line inside the library that noticed it.
            // `savedPrefix` is the CALLER's prefix: the expressions were evaluated before this
            // expansion took over `currentInlinePrefix`.
            Expression? origin = i < rawArgExprs.Count ? rawArgExprs[i] : null;
            if (origin is VariableExpr originVe
                && argumentOrigin.TryGetValue(savedPrefix + originVe.Name, out var inherited))
            {
                origin = inherited;              // handed down, so keep pointing at the source
            }
            else if (TryFieldOrigin(origin, savedPrefix, out var fromField))
            {
                // A driver that stores its pin at construction and validates it at first use
                // passes the FIELD, `_avr_read(self.name)`, so the hand-down above has no bare
                // name to follow. The field's provenance is filed under the same key the
                // field's value is, which is what makes this a lookup rather than a guess (#193).
                origin = fromField;
            }
            else if (!string.IsNullOrEmpty(savedSourcePath))
            {
                // The call is itself inside a library body, so this expression was written
                // there and not by the caller. Taking its position would report a library line
                // against the user's file, which is the non-existent location #164 removed.
                // Only a chain that resolved survives from here.
                origin = null;
            }
            if (origin != null && origin.Column > 0) argumentOrigin[paramName] = origin;
            else argumentOrigin.Remove(paramName);

            // A Union[A, B, ...] parameter reads as "the type of the argument at THIS call
            // site, which must be one of the members" (#442): refuse here, at the site whose
            // argument is wrong, rather than let it bind as whichever member the rest of the
            // pipeline happens to treat an unmatched shape as (an instance field silently
            // typed from a scalar, or the reverse).
            CheckUnionArgumentMatchesAMember(
                func, paramIdx, i < rawArgExprs.Count ? rawArgExprs[i] : null, argValues[i]);
            // The argument EXPRESSION resolved under the caller's scope (`savedPrefix`
            // while an inline frame is ambient, else the enclosing function) -- asking
            // its name under the callee's prefix finds an unrelated same-named global.
            string argScopePfx = savedPrefix.Length > 0 ? savedPrefix
                : !string.IsNullOrEmpty(currentFunction) && currentFunction != "main"
                    ? currentFunction + "." : "";
            RefuseBufferForNumberParam(SourceCalleeName(), func.Params[paramIdx].Name,
                func.Params[paramIdx].Type, i < rawArgExprs.Count ? rawArgExprs[i] : null,
                argValues[i], argScopePfx: argScopePfx);
            RefuseNumberForBufferParam(SourceCalleeName(), func.Params[paramIdx].Name,
                func.Params[paramIdx].Type, i < rawArgExprs.Count ? rawArgExprs[i] : null,
                argValues[i]);

            // A register alias bound at an EARLIER call site to the same @inline function
            // survives in constantAddressVariables unless it is cleared here. The parameter key
            // is the inline prefix plus the name, and that key is reused across call sites at
            // the same depth. Reads of a parameter consult this map BEFORE variableTypes, so a
            // second expansion re-read the first call's register and ignored its own argument:
            // `print_byte(GPIOR0.value)` followed by `print_byte(x)` printed the register twice.
            // Only the MemoryAddress branch below re-establishes the alias, and only when this
            // call site actually passes one.
            constantAddressVariables.Remove(paramName);
            constantAddressVariables.Remove(paramName + "_type");

            if (i < rawSeqBases.Count && rawSeqBases[i] != null)
            {
                // A compile-time sequence of instances: the parameter is another name for the
                // base the elements were built under, so `ps[0]`, `for p in ps` and a field that
                // stores it all reach the same `base__k` the module-level form produces.
                BindSequenceAlias(paramName, rawSeqBases[i]!);
                continue;
            }


            // A TYPING-ONLY annotation on this parameter (#367). Recorded before the binding
            // below, and CLEARED when it is not one, because the key is the inline prefix plus
            // the name and that key is reused by every expansion at the same depth.
            //
            // Exception (#419): when the argument at THIS call site is a real instance, the
            // parameter IS that instance. Descriptor protocol `__get__`/`__set__` is the
            // canonical case -- `obj` is annotated `I2CDeviceDriver` (a typing-only
            // placeholder) but the rewrite passes the owning `INA219`. The annotation was a
            // type-checker promise, not the value's type; substitute the argument's class
            // and do not mark the parameter typing-only.
            //
            // The same for `Any`: it means "whatever the caller passed". Adafruit's
            // `UnaryStruct.__set__(..., value: Any)` then `struct.pack_into(..., value)`
            // reads that parameter; the written value is an int with a width, so the
            // annotation is not a reason to refuse the read.
            string? argInstanceClass = InstanceClassOfVal(argValues[i]);
            string paramAnn = paramIdx < func.Params.Count ? (func.Params[paramIdx].Type ?? "") : "";
            bool anyWithRepr = IsAnyAnnotation(paramAnn) && ValHasRepresentation(argValues[i]);
            if (paramIdx < func.Params.Count
                && IsTypingOnlyName(paramAnn)
                && argInstanceClass == null
                && !anyWithRepr)
                typingOnlyValues[paramName] = paramAnn;
            else
                typingOnlyValues.Remove(paramName);
            if (argInstanceClass != null)
                instanceClasses[paramName] = argInstanceClass;
            else if (paramIdx < func.Params.Count
                     && ClassKeyFromAnnotation(func.Params[paramIdx].Type ?? "") is { } annCls)
                instanceClasses[paramName] = annCls;

            if (i < rawListArgs.Count && rawListArgs[i] != null)
            {
                // A buffer parameter whose body needs real storage -- a run-time
                // subscript over elements that are not compile-time constants --
                // cannot stay a compile-time sequence: bound as one, `buf[i]` was
                // refused as "compile-time values with no storage", the refusal the
                // report `UART.write(b"DE\n")` hit once the bytearray overload was
                // chosen. Materialize the literal under a hidden name -- the same
                // `bytes(...)` binding an inline `bytearray(...)` argument already
                // gets -- and alias the parameter to that storage. A store through
                // the parameter is refused inside BufferParamNeedsStorage instead:
                // nothing can write the compile-time sequence, and the dead slot it
                // used to land on ate the store (`b[0] = 65` emitted `bset put.b, 0`).
                // Reads the deferred paths already answer keep the literal binding:
                // `buf[0]` folds, a run-time `buf[i]` over constants reads a
                // `__cttab` flash table, `writeto(a, buf, n)` marshals a `__seqarg`
                // copy, and `len(buf)` or `for b in buf` unroll.
                string litAnn = func.Params[paramIdx].Type ?? "";
                if (litAnn is "" or "bytearray" or "bytes" or "memoryview"
                    && BufferParamNeedsStorage(func.Body, func.Params[paramIdx].Name,
                                               rawListArgs[i]!)
                    && TryEvalLiteralBufferArg(rawListArgs[i]!) is ArrayBase litBuf)
                {
                    variableAliases[paramName] = litBuf.ArrayName;
                    variableTypes[paramName] =
                        DataTypeExtensions.StringToDataType(func.Params[paramIdx].Type);
                    constantVariables.Remove(paramName);
                    strConstantVariables.Remove(paramName);
                    floatConstantVariables.Remove(paramName);
                    continue;
                }
                // Bytes/list/tuple literal bound to this parameter: record the raw AST
                // so `for x in param` unrolls it and `param[const]` folds. Clear any
                // stale scalar bindings.
                listLiteralParams[paramName] = rawListArgs[i]!;
                // The elements stay AST, so they are evaluated late -- under the callee's
                // scope by default. Their scope is the CALLER's: the saved fields above,
                // captured before this expansion installed its own, are what `t[0]`
                // reading `f(reg)` or `self.k` must see (adafruit_ds3231 builds
                // time.struct_time((...)) through such a call inside a descriptor).
                listLiteralParamScopes[paramName] = new SeqArgScope(
                    savedModulePrefix, savedPrefix,
                    savedSourcePath, savedSourceFile,
                    savedTracksCallee, savedCalleeLine);
                constantVariables.Remove(paramName);
                strConstantVariables.Remove(paramName);
                floatConstantVariables.Remove(paramName);
                variableAliases.Remove(paramName);
                continue;
            }
            listLiteralParams.Remove(paramName);
            listLiteralParamScopes.Remove(paramName);

            // `levels = [7, 8, 9]` then `D(levels)`: a list of NUMBERS reached by name keeps its
            // elements against the parameter, so `xs[0]`, `for v in xs` and `len(xs)` answer the
            // same inside the callee as they do outside it. The scalar/array binding below still
            // runs, which is what leaves a run-time subscript reading the array itself.
            constSequenceBindings.Remove(paramName);
            if (i < rawConstSeqArgs.Count && rawConstSeqArgs[i] != null)
                constSequenceBindings[paramName] = rawConstSeqArgs[i]!;

            // A parameter bound to None. There is no value to copy -- None has no runtime
            // representation -- so it is recorded as None-valued, exactly as a parameter
            // DEFAULTING to None already is, and nothing is emitted.
            //
            // Without this the name was left unbound: `p is None` inside the callee answered
            // false, and `match p:` had no subject it could decide, so every arm was lowered
            // and an arm that refuses at compile time fired for a program that never selected
            // it. `pin.pull = None` is CircuitPython's spelling for "no pull" and was refused
            // with "Pull-down resistor not supported on AVR" (#306).
            //
            // The Remove on the other path is not optional: the parameter key is the inline
            // prefix plus the name, and that key is reused across call sites at the same
            // depth, so a None left by an earlier site would answer for this one.
            if (argValues[i] is NoneVal)
            {
                noneValuedNames.Add(paramName);
                constantVariables.Remove(paramName);
                strConstantVariables.Remove(paramName);
                floatConstantVariables.Remove(paramName);
                variableAliases.Remove(paramName);
                continue;
            }
            noneValuedNames.Remove(paramName);

            // `xs: list` -- a bare list annotation -- promises the body a run-time
            // list: `xs[i]`, `len(xs)`, `for x in xs`, `xs.append(...)` all answer
            // through the list the ARGUMENT was. The element type is the argument's,
            // resolved per call site; that is the spelling a CircuitPython library
            // writes (`adafruit_irremote.bin_data` takes `pulses: list`). What cannot
            // keep the promise is a scalar, a string, a register -- refuse here, at
            // the site whose argument is wrong, rather than let `xs[i]` lower as a
            // bit-test on a byte. A class instance is NOT refused: `pulsein` satisfies
            // `input_pulses: list` by popleft/len, which the instance binding answers
            // for. A compile-time sequence bound above is a list already.
            if (paramAnn == "list"
                && argInstanceClass == null
                && !(i < rawConstSeqArgs.Count && rawConstSeqArgs[i] != null))
            {
                string argListKey = argValues[i] switch
                {
                    Variable blv => ResolveListVarQualified(blv.Name),
                    Temporary blt => ResolveListVarQualified(blt.Name),
                    _ => "",
                };
                if (argListKey.Length == 0)
                    throw UserError(
                        $"parameter '{func.Params[paramIdx].Name}' is declared 'list' -- a "
                        + "run-time list -- and the argument bound to it is not one. If the "
                        + "argument is a fixed-size array or a constant list, give the "
                        + "parameter its element type (`xs: list[uint8]`) or pass the "
                        + "sequence directly");
            }

            if (argValues[i] is FloatConstant fcArg)
            {
                var fcPType = func.Params[paramIdx].Type;
                bool fcIsInt = fcPType is "uint8" or "uint16" or "uint32" or "int8" or "int16" or "int32" or "int";
                if (fcIsInt)
                {
                    constantVariables[paramName] = NarrowConstantArgToParam((int)fcArg.Value, fcPType);
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

            // A module-level `x = r` is lowered inside the synthesized main and files its alias
            // as `main.x`, while the argument read of the global carries the bare `x`. Bound
            // as it stood, the parameter took x's scalar slot (or a constant folded from it)
            // and the callee's `buf[i]` read the bits of that byte instead of r's elements.
            if (argValues[i] is Variable { Name: var modArg } modArgVar && !modArg.Contains('.')
                && variableAliases.TryGetValue("main." + modArg, out var modAlias)
                && FollowAliases(modAlias) is var modTerm && arraySizes.ContainsKey(modTerm))
                argValues[i] = new Variable(modTerm, modArgVar.Type);

            if (argValues[i] is Variable vArg)
            {
                // `neopixel_write(pin, self._post_brightness_buffer)`: an arena-backed
                // buffer arrives as the scalar variable holding its arena offset.
                // Register the parameter under the arena name too, so `for b in buf`,
                // `buf[i]` and `len(buf)` inside the callee resolve through the same
                // path the caller's name did. The ordinary binding below still runs --
                // it is what stores the offset the indexed reads add to.
                if (arenaBufferNames.Contains(vArg.Name))
                {
                    arenaBufferNames.Add(paramName);
                    if (arenaBufferLenVar.TryGetValue(vArg.Name, out var arenaArgLen))
                        arenaBufferLenVar[paramName] = arenaArgLen;
                }

                string varArgDecl = func.Params[paramIdx].Type ?? "";
                bool varStrCapable = varArgDecl is "const[str]" or "str"
                    || varArgDecl.StartsWith("Union[") || varArgDecl.StartsWith("Optional[")
                    || IsAnyAnnotation(varArgDecl) || IsTypingOnlyName(varArgDecl);
                if (varStrCapable)
                {
                    string? strVal = ResolveStrConstant(vArg.Name);
                    // The interned-id fallback stays gated on a definite `str`
                    // declaration: a Union param can just as legitimately receive the
                    // int 300, and 300 can be a string id -- that collision is a real
                    // value, not a tag.
                    if (strVal == null && varArgDecl is "const[str]" or "str"
                        && TryArgumentConstant(vArg.Name, out int sid)
                        && stringIdToStr.TryGetValue(sid, out var internedFromVar))
                        strVal = internedFromVar;
                    if (strVal != null)
                    {
                        strConstantVariables[paramName] = strVal;
                        if (varArgDecl == "const[str]")
                            constantVariables.Remove(paramName);
                        else if (TryArgumentConstant(vArg.Name, out int n))
                            constantVariables[paramName] = n;
                        floatConstantVariables.Remove(paramName);
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
                        constantVariables[paramName] = NarrowConstantArgToParam((int)fv, fvPType);
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

                if (IsConstType(func.Params[paramIdx].Type))
                {
                    int? resolved = TryArgumentConstant(vArg.Name, out int cvv) ? cvv : null;
                    if (resolved is int rv)
                    {
                        constantVariables[paramName] = rv;
                        strConstantVariables.Remove(paramName);
                        variableAliases.Remove(paramName);
                        continue;
                    }
                    string bare = vArg.Name.Split('.')[^1];
                    bool isFunctionRef = functionParams.ContainsKey(vArg.Name)
                        || functionParams.ContainsKey(bare)
                        || inlineFunctions.ContainsKey(vArg.Name)
                        || inlineFunctions.ContainsKey(bare);
                    // Deliberately unlocated, and not for the usual reason. Argument binding
                    // runs BEFORE the callee's first statement, so `inlineCalleeStmtLine` is
                    // still 0 while `currentSourcePath` has already moved to the callee: the
                    // line in hand is the CALLER's and the file label is the CALLEE's. Measured
                    // across two modules, this already reports `helper.py:12` for a six-line
                    // helper.py, line 12 being main.py's call. Adding a column would put a caret
                    // under a character of a line that file does not have. The four sites in
                    // this binding loop stay caretless until that pair is made coherent.
                    if (!isFunctionRef)
                        throw UserError(
                            $"Parameter '{func.Params[paramIdx].Name}' is declared as " +
                            $"{func.Params[paramIdx].Type} and requires a compile-time constant; " +
                            $"'{bare}' varies at runtime. A loop variable qualifies when the " +
                            "loop unrolls, which a `for` over a short constant list or tuple " +
                            "does -- `pins = [11, 12, 13]` then `for p in pins:` -- and so does " +
                            $"a `for` over a constant range of at most {ConstSequenceUnrollLimit} " +
                            "steps. A longer range, or one with a bound that is not known at " +
                            "compile time, stays a real loop. Otherwise select with explicit " +
                            "constants (if/elif or match)");
                }
                // An argument that HOLDS a compile-time constant binds the parameter as that
                // constant, not as a variable that happens to contain it. Otherwise a callee
                // which dispatches on the value -- the calibrated delay loops,
                // pwm_prescaler_for_freq, claim(), any `match` on a const parameter -- took its
                // run-time path as soon as the caller put the value in a local first:
                // `delay_ms(int(s * 1000))` got the calibrated loop and
                // `x = int(s * 1000); delay_ms(x)` got the counted one, 60 bytes more and
                // 974 us where 1000 was asked for (PyMCU#327).
                //
                // Not when the callee ASSIGNS the parameter: such a write reaches the caller's
                // own name through this alias today, and a constant has nothing to write back
                // to.
                if (TryArgumentConstant(vArg.Name, out int argConst)
                    && !ParameterIsAssignedIn(func, func.Params[paramIdx].Name))
                {
                    constantVariables[paramName] =
                        NarrowConstantArgToParam(argConst, func.Params[paramIdx].Type);
                    strConstantVariables.Remove(paramName);
                    floatConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                    variableTypes[paramName] = DataTypeExtensions.StringToDataType(func.Params[paramIdx].Type);
                    continue;
                }

                // A parameter the body REASSIGNS cannot stay an alias: the first write
                // (possibly inside a conditional that does not always run) has to
                // materialize a fresh local, and every read on a path where that write
                // did not happen would see an uninitialized slot -- `x, y = y, x`
                // under `if self.rotation == 1:` in adafruit_framebuf.rect. Bind a
                // real variable initialized with the argument's value instead. A
                // subscripted parameter keeps its alias: `buf[i] = v` writes THROUGH
                // the binding into the caller's array, it never rebinds the name.
                if (ParameterIsAssignedIn(func, func.Params[paramIdx].Name)
                    && !IsSubscriptedInBody(func.Body, func.Params[paramIdx].Name))
                {
                    variableAliases.Remove(paramName);
                    constantVariables.Remove(paramName);
                    strConstantVariables.Remove(paramName);
                    floatConstantVariables.Remove(paramName);
                    variableTypes[paramName] = DataTypeExtensions.StringToDataType(func.Params[paramIdx].Type);
                    // The copy gives the parameter a slot of its own, so reads
                    // between here and the body's first write must already see a
                    // list: inherit the argument's element registration under the
                    // parameter's own key. `decode_bits(pulses: list)` rebinding
                    // `pulses = list(pulses)` reads `pulses` on the right first.
                    if (ResolveListVarQualified(vArg.Name) is { Length: > 0 } argListKey)
                    {
                        listVarElemTypes[paramName] = listVarElemTypes[argListKey];
                        if (listInnerElemTypes.TryGetValue(argListKey, out var argInnerElem))
                            listInnerElemTypes[paramName] = argInnerElem;
                        // The parameter holds the reference itself: binding it at
                        // the annotation's width (uint8 on an unannotated param, which
                        // is also what namedtuple() synthesizes) truncated the 16-bit
                        // GC pointer to its low byte and every `p[i]` inside the body
                        // indexed the register file.
                        variableTypes[paramName] = DataType.GC_REF;
                    }
                    Emit(new Copy(vArg, new Variable(paramName, variableTypes[paramName])));
                    CarryOptionalTagToParam(paramName, vArg);
                    continue;
                }

                // The alias reads the caller's name wherever the body reads the parameter, so
                // it is the argument's value only while nothing writes that name. A global the
                // expansion can write -- `global` in the body or in any function it calls --
                // or a field a method it calls assigns, is bound by value instead: `f(count)`
                // with a body that bumps count read the bumped count. Nothing writing the name
                // keeps the alias, and with it the code.
                if (ExpansionMayWriteArg(func, vArg,
                        i < rawArgExprs.Count ? rawArgExprs[i] : null))
                {
                    constantVariables.Remove(paramName);
                    strConstantVariables.Remove(paramName);
                    floatConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                    variableTypes[paramName] = vArg.Type;
                    Emit(new Copy(vArg, new Variable(paramName, vArg.Type)));
                    continue;
                }

                // A parameter declared with a width narrower than the argument's (or of the
                // other sign) holds the argument converted to that width, as the same
                // parameter of a real subroutine does. An alias read the caller's wider
                // variable: `@inline def f(n: uint8)` called with a 300 printed 300 where the
                // outlined twin printed 44.
                string declParamT = func.Params[paramIdx].Type;
                DataType declParamDt = DataTypeExtensions.StringToDataType(declParamT);
                if ((IsNumericWidthName(declParamT) || declParamT == "int")
                    && IsScalarIntType(vArg.Type) && IsScalarIntType(declParamDt)
                    && !TypeHolds(declParamDt, vArg.Type)
                    && !IsSubscriptedInBody(func.Body, func.Params[paramIdx].Name))
                {
                    variableAliases.Remove(paramName);
                    constantVariables.Remove(paramName);
                    strConstantVariables.Remove(paramName);
                    floatConstantVariables.Remove(paramName);
                    variableTypes[paramName] = declParamDt;
                    Emit(new Copy(vArg, new Variable(paramName, declParamDt)));
                    CarryOptionalTagToParam(paramName, vArg);
                    continue;
                }

                variableAliases[paramName] = vArg.Name;
                PropagateScalarMask(paramName, vArg);
                constantVariables.Remove(paramName);
                strConstantVariables.Remove(paramName);
                floatConstantVariables.Remove(paramName);
                // An unannotated parameter aliasing a variable is as wide as that variable:
                // the uint8 default printed `f(t)` with `t: uint32` as its low byte.
                variableTypes[paramName] = declParamT.Length == 0 && IsScalarIntType(vArg.Type)
                    ? vArg.Type : declParamDt;
                CarryOptionalTagToParam(paramName, vArg);
                continue;
            }

            // The argument as WRITTEN, folded with the caller's locals in scope. The value
            // branches below see what VisitExpression made of it, and a width conversion of a
            // local -- `delay_ms(uint16(ms))`, which is how a HAL narrows a computed value --
            // arrives as a run-time Temporary even where every input is known. Asking the
            // folder about the expression itself is what lets the callee dispatch on it (#327).
            if (!IsConstType(func.Params[paramIdx].Type)
                && argValues[i] is Temporary or Variable
                && i < rawArgExprs.Count && rawArgExprs[i] is { } rawArg
                && TryFoldArgumentExpression(rawArg, func.Params[paramIdx].Type, savedPrefix, out int foldedArg)
                && !ParameterIsAssignedIn(func, func.Params[paramIdx].Name))
            {
                constantVariables[paramName] =
                    NarrowConstantArgToParam(foldedArg, func.Params[paramIdx].Type);
                strConstantVariables.Remove(paramName);
                floatConstantVariables.Remove(paramName);
                variableAliases.Remove(paramName);
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
                    constantVariables[paramName] =
                        NarrowConstantArgToParam(tNum, func.Params[paramIdx].Type);
                    strConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                    continue;
                }
                // A Temporary that stands for a constructed instance's ANCHOR —
                // `return DigitalInOut(...)` in an @inline suppresses the copy into
                // the result temp because the anchor is a namespace, not a byte, and
                // records only `variableAliases[tmp] = <anchor>`. There is no scalar
                // to copy: bind the parameter to the anchor itself so `param.field`
                // and `param.method()` resolve like any object argument. A
                // single-field class's anchor is a real byte (NamesInstanceAnchor
                // answers false) and its temp still takes the Copy below.
                string anchored = FollowAliases(tArg.Name);
                if (anchored != tArg.Name && NamesInstanceAnchor(anchored)
                    && !ParameterIsAssignedIn(func, func.Params[paramIdx].Name))
                {
                    variableAliases[paramName] = anchored;
                    constantVariables.Remove(paramName);
                    strConstantVariables.Remove(paramName);
                    floatConstantVariables.Remove(paramName);
                    variableTypes[paramName] =
                        DataTypeExtensions.StringToDataType(func.Params[paramIdx].Type);
                    if (instanceClasses.TryGetValue(anchored, out var anchoredCls)
                        && anchoredCls != null)
                        instanceClasses[paramName] = anchoredCls;
                    continue;
                }
                // A Temporary that is a tagged class instance (e.g. a single-field ZCA field
                // re-tagged with its nested class) must carry that class onto the @inline param,
                // so the callee's `param.field`/`param.method()` resolves -- the param is bound by
                // a runtime Copy below (its own var), and alias-following stops at tmp_ names, so
                // the class would otherwise be lost.
                PropagateScalarMask(paramName, tArg);
                if (instanceClasses.TryGetValue(tArg.Name, out var tCls) && tCls != null)
                    instanceClasses[paramName] = tCls;
                // A zero-copy factory result passed straight in (`rd(make(2))`) is its one
                // field's scalar. Bound by the ordinary Copy below, the parameter has
                // storage but no handle tag, so `param.field` flattened to
                // `<param>_<field>` -- a name nothing writes -- and read 0 while the copied
                // value sat one name over. Tag it a handle and give it the field's width.
                if (factoryHandleInstances.Contains(tArg.Name))
                {
                    factoryHandleInstances.Add(paramName);
                    constantVariables.Remove(paramName);
                    strConstantVariables.Remove(paramName);
                    floatConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                    variableTypes[paramName] = tArg.Type;
                    Emit(new Copy(tArg, new Variable(paramName, tArg.Type)));
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

                    // Unlocated for the reason recorded at the const[uint8] site above: in the
                    // argument-binding window the line is the caller's and the file label is
                    // the callee's, so a column would point into a file that has no such line.
                    throw UserError(
                        $"Parameter '{func.Params[paramIdx].Name}' is declared as const[str] and requires a compile-time string constant value");
                }

                if (argValues[i] is FloatConstant fcArg2)
                {
                    floatConstantVariables[paramName] = fcArg2.Value;
                    constantVariables.Remove(paramName);
                    strConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                    continue;
                }
                if (!(argValues[i] is Constant cArg2))
                    // Unlocated for the reason recorded at the const[uint8] site above: in the
                    // argument-binding window the line is the caller's and the file label is
                    // the callee's, so a column would point into a file that has no such line.
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
                constantVariables[paramName] =
                    NarrowConstantArgToParam(cArg3.Value, func.Params[paramIdx].Type);
                // A string literal arrives here as a number: an interned id, or -- for a
                // ONE-CHARACTER string -- the character's own code, since that is what makes
                // `c == 'x'` and uart.write('A') work. A use site can ask stringIdToStr what an
                // id stood for; nothing can ask that of a character code, because 44 is also
                // the number 44. So `One(",")` bound a `s: str` parameter to 44 and the field
                // it was assigned to became 44 as well, and the program printed 44.
                //
                // A `str` parameter that received a compile-time string keeps the text, of
                // any length. Only a one-character literal used to land in
                // strConstantVariables (uart.write('A') / `c == 'x'`), so a longer format
                // such as `"<HH"` arrived as an interned id with the characters dropped.
                // `struct.calcsize(struct_format)` inside an inlined descriptor constructor
                // then refused a format the class body passed as a literal
                // (`StructArray(0x06, "<HH", 16)` in adafruit_pca9685). The declared type
                // is still the discriminator: `uart.write('\n')` is a `uint8`, and giving
                // that parameter a text made the UART HAL refuse itself.
                //
                // A Union/Optional/typing-only annotation names a CHOICE the call site
                // already validated (CheckUnionArgumentMatchesAMember), not a width to
                // store: `value: Union[str, float]` receiving "FF23" has to keep the text
                // or `isinstance(value, str)` in the body folds False and the call sinks
                // into the number arm, where str(value) then printed the interned id --
                // adafruit_ht16k33's print_hex showed the id, not the string.
                string constArgDecl = func.Params[paramIdx].Type ?? "";
                bool keepsStrText = constArgDecl is "str" or ""
                    || constArgDecl.StartsWith("Union[") || constArgDecl.StartsWith("Optional[")
                    || IsAnyAnnotation(constArgDecl) || IsTypingOnlyName(constArgDecl);
                if (keepsStrText)
                {
                    string? text = i < rawStrArgs.Count ? rawStrArgs[i]?.Value : null;
                    if (text == null && !string.IsNullOrEmpty(cArg3.Text)) text = cArg3.Text;
                    // A declared `str` keeps resolving an interned id handed in through
                    // another call. An unannotated parameter does not get that fallback:
                    // `f(300)` binds 300, and 300 can collide with an id in stringIdToStr --
                    // the param would come out the other side as text it never held.
                    // (adafruit_framebuf's text(string, ...) is why the unannotated shape
                    // keeps any text at all: `display.text("PyMCU", ...)` has to reach
                    // string.split() inside the method with "PyMCU" still readable.)
                    if (text == null && func.Params[paramIdx].Type == "str"
                        && stringIdToStr.TryGetValue(cArg3.Value, out var internedArg))
                        text = internedArg;
                    if (text != null) strConstantVariables[paramName] = text;
                    else strConstantVariables.Remove(paramName);
                }
                else
                    strConstantVariables.Remove(paramName);
                floatConstantVariables.Remove(paramName);
                variableAliases.Remove(paramName);
                continue;
            }
            if (argValues[i] is MemoryAddress mArg)
            {
                // Aliasing the parameter to the address is for a parameter that names a
                // REGISTER (a `ptr`, as the GPIO HAL passes pin_reg). A parameter declared with
                // a numeric width receives the CONTENTS, so it must be copied like any other
                // run-time value: aliasing it made the body see a register where the program
                // declared a uint8, and arithmetic on it was rejected with a message describing
                // the argument rather than the parameter.
                string mPType = func.Params[paramIdx].Type;
                bool mIsNumeric = mPType is "uint8" or "uint16" or "uint32"
                    or "int8" or "int16" or "int32" or "int" or "bool";
                // `twice(GPIOR0.value)` hands over the register's CONTENTS whatever the
                // parameter says: aliased, `v + v` read the register twice and an unused
                // parameter never read it, where Python reads it once, at the call.
                bool mIsContents = i < rawArgExprs.Count && rawArgExprs[i] is { } mRaw
                    && ReadsARegister(mRaw) && !mPType.StartsWith("ptr");
                if (mIsNumeric || mIsContents)
                {
                    constantVariables.Remove(paramName);
                    strConstantVariables.Remove(paramName);
                    floatConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                    variableTypes[paramName] = mIsNumeric
                        ? DataTypeExtensions.StringToDataType(mPType) : mArg.Type;
                    Emit(new Copy(argValues[i], new Variable(paramName, variableTypes[paramName])));
                    continue;
                }
                constantAddressVariables[paramName] = mArg.Address;
                constantAddressVariables.Remove(paramName + "_type");
                constantVariables.Remove(paramName);
                strConstantVariables.Remove(paramName);
                variableAliases.Remove(paramName);
                // The ELEMENT width the parameter declares, or the argument's own when the
                // parameter is a bare `ptr`. Only the address was recorded, so every use of
                // the parameter fell back to the UINT8 default: a `ptr[uint16]` parameter
                // holding TCNT1 wrote one byte of the pair, and a field bound from a
                // `ptr[uint8]` parameter took its width from the class scan instead (UINT16,
                // because what the field holds is an address) and wrote over the neighbouring
                // register. The width has to travel with the address, the way it does when the
                // value arrives from a `-> ptr[T]` selector.
                variableTypes[paramName] = mPType.StartsWith("ptr[") && mPType.EndsWith("]")
                    ? DataTypeExtensions.StringToDataType(mPType[4..^1])
                    : mArg.Type;
                continue;
            }

            constantVariables.Remove(paramName);
            strConstantVariables.Remove(paramName);
            floatConstantVariables.Remove(paramName);
            variableAliases.Remove(paramName);
            // An unannotated parameter takes the width of the run-time value it is bound to.
            // The uint8 default truncated it: `f(GPIOR0.value + 900)` printed 132 (CPython
            // 900). A later store into the parameter inside the body is checked like any
            // inferred local's.
            DataType declaredParamType = DataTypeExtensions.StringToDataType(func.Params[paramIdx].Type);
            if (func.Params[paramIdx].Type.Length == 0
                && WidthSeeds.IsInt(GetValType(argValues[i]))
                && argValues[i] is Variable or Temporary)
                declaredParamType = InferredSlot(paramName,
                    WidthSeeds.Join(DataType.UINT8, GetValType(argValues[i])));
            DataType paramType = ParamListRefType(argValues[i], paramName, declaredParamType);
            variableTypes[paramName] = paramType;
            Emit(new Copy(argValues[i], new Variable(paramName, paramType)));
            CarryOptionalTagToParam(paramName, argValues[i]);
        }

        foreach (var kvp in kwArgValues)
        {
            bool found = false;
            for (int pi = paramOffset; pi < func.Params.Count; ++pi)
            {
                // `*args` and `**kwargs` are not names a caller may pass by keyword: `f(kwargs=1)`
                // is a keyword argument called "kwargs", not the mapping itself.
                if (func.Params[pi].IsVarArg || func.Params[pi].IsKwArg) continue;
                if (func.Params[pi].Name == kvp.Key)
                {
                    string paramName = currentInlinePrefix + func.Params[pi].Name;
                    boundParams.Add(pi);
                    found = true;

                    if (rawKwEmptySeqs.Contains(kvp.Key))
                    {
                        constSequenceBindings[paramName] = new List<Expression>();
                        constantVariables.Remove(paramName);
                        strConstantVariables.Remove(paramName);
                        floatConstantVariables.Remove(paramName);
                        variableAliases.Remove(paramName);
                        noneValuedNames.Remove(paramName);
                        break;
                    }

                    // A register alias bound at an EARLIER call site to the same @inline function
                    // survives in constantAddressVariables unless it is cleared here. The parameter key
                    // is the inline prefix plus the name, and that key is reused across call sites at
                    // the same depth. Reads of a parameter consult this map BEFORE variableTypes, so a
                    // second expansion re-read the first call's register and ignored its own argument:
                    // `print_byte(GPIOR0.value)` followed by `print_byte(x)` printed the register twice.
                    // Only the MemoryAddress branch below re-establishes the alias, and only when this
                    // call site actually passes one.
                    constantAddressVariables.Remove(paramName);
                    constantAddressVariables.Remove(paramName + "_type");

                    // A keyword argument binds the parameter exactly as a positional one does,
                    // and has to clear the same state. The parameter key is the inline prefix
                    // plus the name, reused by every expansion at the same depth, so a None
                    // left by an EARLIER call site (its own, or the parameter's default)
                    // answered for this one: `UART(bits=7, parity=EVEN)` after any call that
                    // let `parity` default took the `case None` arm and programmed no parity,
                    // clean and silent (#324).
                    if (kvp.Value is NoneVal)
                    {
                        noneValuedNames.Add(paramName);
                        constantVariables.Remove(paramName);
                        strConstantVariables.Remove(paramName);
                        floatConstantVariables.Remove(paramName);
                        variableAliases.Remove(paramName);
                        break;
                    }
                    noneValuedNames.Remove(paramName);

                    if (kvp.Value is Variable vkw)
                    {
                        variableAliases[paramName] = vkw.Name;
                        PropagateScalarMask(paramName, vkw);
                    }

                    // Where this keyword argument was written, on the same terms as a
                    // positional one: a name that already carries an origin hands it down, a
                    // field that carries one hands that down, and an expression written inside
                    // a library body carries nothing (it is not the caller's to fix).
                    Expression? kwOrigin = rawKwArgExprs.TryGetValue(kvp.Key, out var kwRaw) ? kwRaw : null;
                    if (kwOrigin is VariableExpr kwVe
                        && argumentOrigin.TryGetValue(savedPrefix + kwVe.Name, out var kwInherited))
                        kwOrigin = kwInherited;
                    else if (TryFieldOrigin(kwOrigin, savedPrefix, out var kwFromField))
                        kwOrigin = kwFromField;
                    else if (!string.IsNullOrEmpty(savedSourcePath))
                        kwOrigin = null;
                    if (kwOrigin != null && kwOrigin.Column > 0) argumentOrigin[paramName] = kwOrigin;
                    else argumentOrigin.Remove(paramName);

                    if (IsConstType(func.Params[pi].Type))
                    {
                        if (func.Params[pi].Type == "const[str]")
                        {
                            if (rawKwStrArgs.TryGetValue(kvp.Key, out var skw))
                                strConstantVariables[paramName] = skw;
                            else if (kvp.Value is Variable vkw2 && ResolveStrConstant(vkw2.Name) is { } svkw)
                                strConstantVariables[paramName] = svkw;
                            else
                            // Unlocated for the reason recorded at the const[uint8] site
                            // above: in the argument-binding window the line is the caller's
                            // and the file label is the callee's.
                                throw UserError(
                                    $"Parameter '{func.Params[pi].Name}' is declared as const[str] and requires a compile-time string constant value");
                        }
                        else
                        {
                            if (kvp.Value is Constant ckw)
                                constantVariables[paramName] = ckw.Value;
                            else if (kvp.Value is FloatConstant fkw)
                                floatConstantVariables[paramName] = fkw.Value;
                            else
                            // Unlocated for the reason recorded at the const[uint8] site
                            // above: in the argument-binding window the line is the caller's
                            // and the file label is the callee's.
                                throw UserError(
                                    $"Parameter '{func.Params[pi].Name}' is declared as const and requires a compile-time constant value");
                        }
                    }
                    else if (kvp.Value is Constant ckw2)
                    {
                        constantVariables[paramName] =
                            NarrowConstantArgToParam(ckw2.Value, func.Params[pi].Type);
                        if (func.Params[pi].Type == "str")
                        {
                            string? kwText = rawKwStrArgs.TryGetValue(kvp.Key, out var rawKw) ? rawKw : null;
                            if (kwText == null && !string.IsNullOrEmpty(ckw2.Text)) kwText = ckw2.Text;
                            if (kwText == null && stringIdToStr.TryGetValue(ckw2.Value, out var internedKw))
                                kwText = internedKw;
                            if (kwText != null) strConstantVariables[paramName] = kwText;
                            else strConstantVariables.Remove(paramName);
                        }
                        else
                            strConstantVariables.Remove(paramName);
                        floatConstantVariables.Remove(paramName);
                        variableAliases.Remove(paramName);
                    }
                    else
                    {
                        constantVariables.Remove(paramName);
                        strConstantVariables.Remove(paramName);
                        floatConstantVariables.Remove(paramName);
                        DataType paramType = ParamListRefType(kvp.Value, paramName,
                            DataTypeExtensions.StringToDataType(func.Params[pi].Type));
                        variableTypes[paramName] = paramType;
                        if (kvp.Value is Variable kwVar
                            && !ExpansionMayWriteArg(func, kwVar,
                                rawKwArgExprs.TryGetValue(kvp.Key, out var kwRawArg) ? kwRawArg : null))
                        {
                            // Variable arg (including ZCA instances): preserve the alias
                            // set above and skip the Copy, same as positional arg handling.
                        }
                        else
                        {
                            variableAliases.Remove(paramName);
                            Emit(new Copy(kvp.Value, new Variable(paramName,
                                kvp.Value is Variable kwCopyVar ? kwCopyVar.Type : paramType)));
                        }
                        CarryOptionalTagToParam(paramName, kvp.Value);
                    }

                    break;
                }
            }

            // The USER's spelling, not the mangled callee. `SPI(baudrate=...)` reported
            // "in call to machine_SPI___init__", a symbol nobody typed and nobody can search
            // for in their own file (issue #194). The neighbouring rejections on this same
            // path already print SourceCalleeName(), including the "constructor of" form for
            // an __init__, so this borrows both rather than inventing a third convention.
            if (!found)
            {
                // A callee with `**kwargs` accepts it: that is what the mapping IS, the
                // keyword arguments the call site wrote and this function does not declare.
                if (kwArgIdx >= 0)
                {
                    extraKeywords.Add((kvp.Key,
                        CarriedArgExpr(rawKwArgExprs.GetValueOrDefault(kvp.Key), kvp.Value)));
                    continue;
                }

                bool isCtorKw = callee.Contains("___init__", StringComparison.Ordinal);
                string whatKw = isCtorKw
                    ? $"constructor of '{SourceCalleeName()}'"
                    : $"'{SourceCalleeName()}'";
                throw UserError($"unknown keyword argument '{kvp.Key}' in call to {whatKw}", expr.Callee);
            }
        }

        BindVariadicParams(func, varArgIdx, kwArgIdx, extraPositional, extraKeywords);

        for (int i = paramOffset; i < func.Params.Count; ++i)
        {
            if (boundParams.Contains(i)) continue;
            // Bound just above, to a sequence or a mapping that may legitimately be empty.
            // Falling through would report them as missing required arguments.
            if (func.Params[i].IsVarArg || func.Params[i].IsKwArg) continue;
            if (func.Params[i].DefaultValue != null)
            {
                string paramName = currentInlinePrefix + func.Params[i].Name;
                // `preserve_dios=()`: an empty tuple or list default is a compile-time
                // EMPTY SEQUENCE -- the same binding an empty list argument carries --
                // not a runtime tuple value, which the target does not have.
                if (func.Params[i].DefaultValue is TupleExpr { Elements.Count: 0 }
                    or ListExpr { Elements.Count: 0 })
                {
                    constSequenceBindings[paramName] = new List<Expression>();
                    constantVariables.Remove(paramName);
                    strConstantVariables.Remove(paramName);
                    floatConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                    noneValuedNames.Remove(paramName);
                    continue;
                }
                // A parameter defaulting to None (e.g. `cs: Pin = None`) is bound as
                // None, not as a value: track it so `cs is None` folds correctly and
                // emit no Copy (None has no runtime representation for a reference).
                if (func.Params[i].DefaultValue is NoneLiteral)
                {
                    noneValuedNames.Add(paramName);
                    continue;
                }
                // And the mirror of it: a default that is NOT None has to clear a None an
                // earlier expansion of the same parameter left behind (#324).
                noneValuedNames.Remove(paramName);
                // A default value is written in the CALLEE's file, so this one expression
                // is lowered under the callee's location. Without it the pair inverts rather
                // than being repaired: a diagnostic about `def f(n: const[uint8] = REG.value)`
                // would carry the callee's line under the caller's file name, which is the
                // same non-existent location the other way round.
                Val defaultVal = VisitDefaultValueUnderCallee(
                    func.Params[i].DefaultValue!, calleeSourcePath);

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

                    // A float default is as constant as an int one, and binds the way a
                    // float argument to the same parameter does. It was refused as "not a
                    // compile-time constant", so `interval: const[float] = 0.020` compiled
                    // only when every call passed the argument.
                    if (defaultVal is FloatConstant cfdf)
                    {
                        floatConstantVariables[paramName] = cfdf.Value;
                        constantVariables.Remove(paramName);
                        strConstantVariables.Remove(paramName);
                        variableAliases.Remove(paramName);
                        continue;
                    }

                    if (!(defaultVal is Constant cdf))
                        throw UserError(
                            $"Default value for const parameter '{func.Params[i].Name}' must be a compile-time constant", expr.Callee);
                    constantVariables[paramName] = cdf.Value;
                    continue;
                }

                if (defaultVal is Constant cdf2)
                {
                    constantVariables[paramName] =
                        NarrowConstantArgToParam(cdf2.Value, func.Params[i].Type);
                    strConstantVariables.Remove(paramName);
                    floatConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                }
                else if (defaultVal is FloatConstant fdf)
                {
                    // A FLOAT default binds the way a float ARGUMENT does (#374). The branch
                    // below emits a Copy and registers nothing, and the definedness check reads
                    // the tables rather than the instructions, so `timeout: float = 0.1` on a
                    // constructor the call omits was reported as a name that is "read here but
                    // never assigned, imported, or received as a parameter" -- inside the very
                    // function that declares it. The same parameter with an INT default bound,
                    // and so did the same float default passed explicitly, which is what said
                    // the gap was this one path and not floats or defaults.
                    // Recorded the way a float KEYWORD argument is: as a compile-time float and
                    // nothing else. Giving it a variableTypes entry as well would make the read
                    // resolve to a run-time slot nothing writes, which is how the field came out
                    // 0.0 rather than 0.1.
                    floatConstantVariables[paramName] = fdf.Value;
                    // The same four clears the explicit float ARGUMENT does, and for the
                    // documented reason: the key is the inline prefix plus the name, reused by
                    // every expansion at the same depth, so anything an earlier call site left
                    // under it answers for this one.
                    constantVariables.Remove(paramName);
                    strConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                    noneValuedNames.Remove(paramName);
                }
                else
                {
                    DataType paramType = DataTypeExtensions.StringToDataType(func.Params[i].Type);
                    Emit(new Copy(defaultVal, new Variable(paramName, paramType)));
                    variableTypes[paramName] = paramType;
                    constantVariables.Remove(paramName);
                    strConstantVariables.Remove(paramName);
                    floatConstantVariables.Remove(paramName);
                    variableAliases.Remove(paramName);
                }
            }
            else
            {
                // Required parameter with no argument and no default. Python raises TypeError;
                // reject clearly instead of leaving it uninitialised (read as 0) -- e.g. P(5)
                // for __init__(self, x, y) silently set y = 0.
                string what = callee.Contains("___init__", StringComparison.Ordinal)
                    ? $"constructor of '{SourceCalleeName()}'"
                    : $"'{SourceCalleeName()}'";
                throw UserError(
                    $"missing required argument '{func.Params[i].Name}' in call to {what} " +
                    $"(expects {func.Params.Count - paramOffset} argument(s))", expr.Callee);
            }
        }

        // The pair moves HERE, both halves together, because this is where the callee's lines
        // start. `inlineCalleeStmtLine` stays 0 until VisitStatement walks the first body
        // statement, and until then `UserError` falls back to `currentStmtLine`, which is the
        // caller's line. That fallback is why the file must not move ahead of it.
        if (calleeSourcePath != null)
        {
            currentSourcePath = calleeSourcePath;
            currentSourceFile = calleeSourcePath.Length > 0 ? SourceFileLabel(calleeSourcePath) : "";
            inlineTracksCalleeLine = true;
            inlineCalleeStmtLine = 0;
        }
        else
        {
            // Every frame decides for itself. A nested expansion into a body with no path of
            // its own must not inherit the outer frame's tracking, or the line follows a file
            // the diagnostic will not name.
            inlineTracksCalleeLine = false;
        }

        int savedLastLine = lastLine;
        lastLine = -1;
        // The body's own termination stays in the body: a `return` ended the expansion,
        // not the caller's sequence -- and the caller's state must not leak in either,
        // or a `return` in a `with` body would leave `__exit__`'s expansion unlowered.
        bool savedSeqTerminated = _seqTerminated;
        _seqTerminated = false;
        try
        {
            VisitBlock(func.Body);
            RestoreSeqTerminatedAfterExpansion(savedSeqTerminated, exitLabel);
        }
        catch (CompilerError)
        {
            throw;
        }
        catch (Exception ex)
        {
            int callLine = currentStmtLine > 0 ? currentStmtLine : 1;
            throw new CompilerError("CompileError", ex.Message, callLine);
        }

        lastLine = savedLastLine;

        Emit(new Label(exitLabel));

        if (isForceInlined)
            Emit(new InlineExpansionMarker(callee, true));
        else if (isInlineMethod)
            Emit(new InlineExpansionMarker(Optimizer.InlineMarkerTag + callee, true));

        // The slots THIS call produced describe its result; when it produced none,
        // the list must be emptied, not left holding whatever an inner call last
        // returned -- `temperature`'s `return self.measurements[0]` consumed the
        // inner tuple itself, and print(s.temperature) then re-read those slots
        // and wrote "(10, 20)" where the scalar 10 belonged.
        lastTupleResults = Enumerable.Last<InlineContext>(inlineStack).ResultVars.Count > 0
            ? new List<string>(Enumerable.Last<InlineContext>(inlineStack).ResultVars)
            : new List<string>();
        // The buffer-slot counterpart, same reasoning as the comment above: a stale set from
        // an inner call must not survive into a tuple this call did not return.
        lastTupleResultBuffers = Enumerable.Last<InlineContext>(inlineStack).ReturnedBufferSlots;
        lastTupleResultLocalBuffers = Enumerable.Last<InlineContext>(inlineStack).ReturnedLocalBufferSlots;
        // A result temporary the expansion allocated itself (a value return in a callee the
        // parser filed as void) is the call's value too.
        result ??= Enumerable.Last<InlineContext>(inlineStack).ResultTemp;

        var finishedCtx = Enumerable.Last<InlineContext>(inlineStack);
        string? returnedArr = finishedCtx.ReturnedBuffer;
        if (returnedArr != null)
        {
            lastCallReturnTypeText = "bytearray";
            lastCallReturnTypeExpr = expr;
            lastCallReturnListElem = null;
            lastCallReturnedBufferLocal = finishedCtx.ReturnedBufferIsLocal;
        }
        else
        {
            // The expansion's own calls overwrote the fields set before it ran; the
            // surviving attribution is this callee's signature, not whatever its
            // body happened to call last.
            lastCallReturnTypeText = func?.ReturnType;
            lastCallReturnTypeExpr = expr;
            lastCallReturnListElem = null;
            lastCallReturnedBufferLocal = false;
            // An inlined callee declared `-> Cls` whose body hands back a scalar
            // carrier (a relay's `return make()`, a slot pointer) rather than an
            // instance name: the temp the caller receives still names the class.
            StampProducedClass(result, func?.ReturnType);
        }
        // Two triggers, because neither sees the other's case. `ResultAssigned` is what the
        // expansion actually walked, which is exact for a body whose branches fold away. A
        // body that returns under a RUN-TIME condition assigns the result on the path taken
        // here and still falls through on the other, so the syntax has to be read as well.
        bool resultWasNeverProduced =
            returnedArr == null
            && !resultDiscarded && func != null && finishedCtx.ResultTemp != null
            && finishedCtx.ResultVars.Count == 0
            && finishedCtx.ReturnedBuffer == null
            && (!finishedCtx.ResultAssigned || !AlwaysLeaves(func.Body))
            // A body whose every path ends in `raise` never reaches a result to produce: the
            // call either raises or, caught by a caller's `try`, never hands a value on.
            // `@inline def f() -> uint32: raise ValueError("x")` inside `try: t = f()` was
            // refused as reaching the end without returning.
            && !(AlwaysLeaves(func.Body)
                 && !TypeInference.WalkStatements(func.Body).OfType<ReturnStmt>().Any());

        inlineStack.RemoveAt(inlineStack.Count - 1);
        activeInlineExpansions.Remove(callee);
        // The snapshot each `return Cls(...)` was offered dies with the expansion:
        // if it is still the pending target, clear it so a later bare `Cls()`
        // mints its own `__cN` instead of writing the finished call's fields.
        if (pendingConstructorTarget == finishedCtx.CtorTarget)
            pendingConstructorTarget = "";
        if (func != null && func.IsClassMethod && func.Params.Count > 0)
            classmethodClsAlias.Remove(newPrefix + func.Params[0].Name);
        // Nested expansions pop innermost-first, so after the RHS finishes this holds
        // the OUTERMOST call's declared return type — the width the assignment needs
        // when the result folded to a bare Constant.
        lastInlineReturnType = functionReturnMembers.TryGetValue(callee, out var lirMembers)
            ? UnionPayloadType(lirMembers)
            : func.ReturnMembers is { } lirFm
                ? UnionPayloadType(lirFm)
                : DataTypeExtensions.StringToDataType(func.ReturnType);

        // A `return <list var>` inside the expansion recorded its element type on
        // the context; the branch joins that ran since rebuilt listVarElemTypes and
        // dropped the temp's entry. The temp the caller actually receives is the
        // one that needs the registration.
        if (result is { } listRes && finishedCtx.ResultListElem is { } resListElem)
        {
            listVarElemTypes[listRes.Name] = resListElem;
            variableTypes[listRes.Name] = DataType.GC_REF;
            if (finishedCtx.ResultListInnerElem is { } resInnerElem)
                listInnerElemTypes[listRes.Name] = resInnerElem;
        }

        currentSourcePath = savedSourcePath;
        currentSourceFile = savedSourceFile;
        inlineTracksCalleeLine = savedTracksCallee;
        inlineCalleeStmtLine = savedCalleeLine;
        currentInlinePrefix = savedPrefix;
        currentModulePrefix = savedModulePrefix;
        inlineDepth--;
        EmitRaiseLandings(finishedCtx);

        if (resultWasNeverProduced) throw UnproducedResultError(func!);

        // `return <local array>`: the buffer itself is the expansion's fixed slot, so the
        // call's value is a Variable naming that storage. `x = f()` aliases `x` to it (the
        // generic Variable->Variable binding in VisitAssign) and no bytes are copied.
        if (finishedCtx.ReturnedBuffer is { } retBufKey)
            return new Variable(retBufKey, arrayElemTypes.TryGetValue(retBufKey, out var retBufEt)
                ? retBufEt : DataType.UINT8);

        // Every reachable return produced None (`duty_cycle == 0` folded and `return None`
        // ended the getter, the `return <expr>` under it dead): hand back NoneVal, not the
        // declared-type slot -- `x.prop is None` and `print(x.prop)` must see the None, and
        // the slot only holds residue from whatever temporary used it last. A `return f()`
        // on a void-declared call propagates the live return-register channel instead.
        // An Optional-annotated callee is exempt: its answer lives in the tag temp the
        // MarkOptional below reads, and a bare NoneVal here would skip the tag and let a
        // provable-None result store into an untagged slot without the narrow-it refusal.
        if (finishedCtx.ResultTagTemp == null && finishedCtx.ResultIsNone)
            return isConstructor ? new NoneVal() : MarkNoneCallResult();
        if (finishedCtx.ResultTagTemp == null && finishedCtx.ResultIsLiveCall)
            return new NoneVal(LiveCallResult: true);

        // RFC 0009: the expansion's tag temp is the call result's tag. Mark the result a
        // live optional only when a reached return can actually report None -- an expansion
        // whose None arms all folded away keeps the compile-time answer it always had.
        if (result != null && finishedCtx.ResultTagTemp is { } resTag
            && finishedCtx.SawOptionalNone)
        {
            var rMembers = (func?.ReturnMembers) ?? new List<string> { "uint8", "None" };
            MarkOptional(result.Name, resTag, rMembers);
        }

        // `return x` where x is an instance: the call's value is THAT instance, not a copy of
        // its handle byte. The result temporary only aliased it, and an alias through a
        // scratch temporary is not followed, so `w = one(a)` bound w to the temp and `w.k`
        // read `w_k`, which nothing writes: 0 for 7. Hand back the instance's own name so the
        // caller's binding aliases the object, exactly as `w = a` does.
        var retInsts = finishedCtx.ReturnedInstances;
        if (!resultDiscarded && retInsts.Count > 0 && retInsts.All(n => n != null))
        {
            var distinctInsts = retInsts.Distinct().ToList();
            if (distinctInsts.Count == 1)
            {
                // A single-field class collapses the instance onto its field, so the
                // returned name may be the widened anchor itself: minting it at u8 would
                // truncate `r: uint16 = f.up(300)` at the copy. Multi-field instances have
                // no scalar slot of their own; u8 is only a formality there.
                var inst = distinctInsts[0]!;
                DataType instT = variableTypes.TryGetValue(inst, out var ivt) && ivt != DataType.UNKNOWN
                    ? ivt
                    : instanceClasses.TryGetValue(inst, out var icls) && icls != null
                        && classFieldLayout.TryGetValue(icls, out var ilay) && ilay.Count == 1
                        ? DataTypeExtensions.StringToDataType(ilay[0].Type)
                        : DataType.UINT8;
                return new Variable(inst, instT);
            }
            throw UserError(
                $"'{callee}' returns a different instance depending on a run-time condition "
                + $"({string.Join(", ", distinctInsts.Select(DisplayInstanceName))}). An instance "
                + "chosen at run time cannot be returned from an @inline function: PyMCU lays "
                + "each instance out at compile time, so the caller would receive no object "
                + "to read fields from. Return a value that identifies it (an index or a "
                + "field) and select the instance with an `if` at the call site.",
                expr);
        }

        if (result != null) return result;
        if (ctorSubexprSynth != null) return new Variable(ctorSubexprSynth);
        // No runtime result at all: for a plain call the value IS None, and the
        // assignment's `x = f()` recognizes it by identity, so the shared marker
        // has to be the object that flows out. A constructor is different: its
        // caller binds the built instance through pendingConstructorTarget and
        // this NoneVal is only the body's formal answer -- marking `x` None in
        // `x = Cls()` would fold `x is None` to True under a live object.
        return isConstructor ? new NoneVal() : MarkNoneCallResult();
    }

    private static string DisplayInstanceName(string? name) =>
        name == null ? "?" : name[(name.LastIndexOf('.') + 1)..];

    // A heap-list argument arrives as a GC_REF Temporary (`tuple(xs)`) or a
    // Variable the alias chase resolves to a registered list. Copying it into a
    // param var of the annotation's width -- uint8 when unannotated, which is
    // also what namedtuple() synthesizes for every field -- truncates the
    // 16-bit pointer to its low byte; `p[i]` inside the body then indexes the
    // register file. The parameter IS the list the caller passed: keep the
    // binding at pointer width and carry the element type. Positional and
    // keyword arguments bind through this one rule -- the keyword form is
    // adafruit_irremote's `IRMessage(..., code=tuple(output))`.
    private DataType ParamListRefType(Val arg, string paramName, DataType declared)
    {
        string? argBindName = arg switch
        { Variable av => av.Name, Temporary at => at.Name, _ => null };
        for (string? cur = argBindName; cur != null;)
        {
            if (listVarElemTypes.TryGetValue(cur, out var argElemDt))
            {
                listVarElemTypes[paramName] = argElemDt;
                if (listInnerElemTypes.TryGetValue(cur, out var argInnerDt))
                    listInnerElemTypes[paramName] = argInnerDt;
                return DataType.GC_REF;
            }
            if (!variableAliases.TryGetValue(cur, out var argNext)) break;
            cur = argNext;
        }
        return declared;
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
    // A method call on a set-literal binding. A set literal is a compile-time membership
    // table here, so no method on it has a lowering, and without this the call mangled the
    // receiver and the member together into an undefined `s_add` (issue #197): a name that
    // appears nowhere in the program, called a FUNCTION when it is a method, and offered a
    // typo-or-missing-import suggestion when the spelling is right and no import can add a
    // method to a set. The dict path a few lines above answers the same question properly
    // and this is its counterpart, phrased alike on purpose.
    /// <summary>
    /// "an integer" / "a float" when <paramref name="name"/> is a numeric local in scope, else
    /// null. Only ever consulted on the way to an error, so a name it cannot classify simply
    /// falls through to the message that was going to be printed anyway.
    ///
    /// Deliberately positive: it answers only for names it finds in a numeric map, never by
    /// ruling other kinds out. A receiver that is a string, an array, an instance or a register
    /// is answered by its own path long before this one, and adding a negative test here would
    /// be a second place to keep that list correct.
    /// </summary>
    /// <summary>
    /// `x.bit_length()` on a name holding a number: the receiver named as the reader wrote it.
    ///
    /// ONE site for the sentence, reached from two paths. It used to live only under the
    /// undefined-function fallback, and a name whose value the compiler knows stopped going
    /// there once reads of locals began to fold (#331) -- the reader then got a sentence about
    /// a receiver that is "not a name", for a program whose receiver is a name.
    /// </summary>
    private PyMCU.Common.CompilerError NumericReceiverError(
        string name, string kind, string member, PyMCU.Frontend.ASTNode at)
    {
        bool isInt = kind == "an integer";
        // Everything offered here is checked to compile on a run-time value of that type, not
        // read off the builtin list: hex(), bin() and pow() are on that list and all three
        // refuse anything but a compile-time constant, and round() does not exist at all.
        string works = isInt
            ? "the arithmetic, comparison and bitwise operators, the builtins abs(), "
              + "min(), max() and divmod(), and the width casts uint8()/int8()/"
              + "uint16()/int16()/uint32()/int32()"
            : "the arithmetic and comparison operators, abs(), and int() to truncate "
              + "toward zero";

        return UserError(
            $"'{name}' is {kind}: '{member}()' is not available. "
            + $"{(isInt ? "Integers" : "Floats")} on this target are fixed-width machine "
            + $"values, not objects carrying methods. Supported: {works}.",
            at);
    }

    private string? NumericLocalKind(string name)
    {
        foreach (var key in Qualifications(name))
        {
            if (floatConstantVariables.ContainsKey(key)) return "a float";
            if (constantVariables.ContainsKey(key)) return "an integer";
            if (variableTypes.TryGetValue(key, out var dt))
                return dt == DataType.FLOAT ? "a float"
                     : dt is DataType.UINT8 or DataType.INT8 or DataType.UINT16 or DataType.INT16
                            or DataType.UINT32 or DataType.INT32 ? "an integer"
                     : null;
        }

        return null;
    }

    /// <summary>The keys a local name can be filed under, most specific first.</summary>
    private IEnumerable<string> Qualifications(string name)
    {
        if (!string.IsNullOrEmpty(currentInlinePrefix)) yield return currentInlinePrefix + name;
        if (!string.IsNullOrEmpty(currentFunction)) yield return currentFunction + "." + name;
        yield return name;
    }

    private Val? TryEmitSetMethod(CallExpr expr)
    {
        if (expr.Callee is not MemberAccessExpr { Object: VariableExpr sv } sm) return null;
        if (!TryGetSetBinding(sv.Name, out _)) return null;

        // Every member, not a list of rejected ones: nothing on a set literal is available,
        // so naming a supported subset would be inventing one.
        throw UserError(
            $"'{sv.Name}' is a compile-time set literal (read-only membership table): " +
            $"'{sm.Member}()' is not available. Supported: x in {sv.Name}, " +
            $"len({sv.Name}). For a collection you write into, use a " +
            "bytearray or a fixed-size list.", expr.Callee);
    }

    private Val? TryEmitDictMethod(CallExpr expr)
    {
        if (expr.Callee is not MemberAccessExpr dm) return null;
        if (dm.Object is not VariableExpr && dm.Object is not MemberAccessExpr) return null;
        if (!TryGetDictFor(dm.Object, out var dict)) return null;
        string dvName = dm.Object is VariableExpr dvn ? dvn.Name
            : dm.Object is MemberAccessExpr dma ? dma.Member : "d";

        if (dm.Member != "get")
            // Named receiver, matching its set counterpart above. Two sibling messages that
            // differ in whether they name the receiver is the inconsistency each of them
            // exists to avoid, and with two dicts in scope the name is the useful half.
            throw UserError(
                $"'{dvName}' is a compile-time lookup table (read-only dict literal): " +
                $"'{dm.Member}()' is not available. Supported: {dvName}[key], " +
                $"key in {dvName}, len({dvName}), {dvName}.get(key, default). " +
                "For a mutable dict use pymcu.collections.FixedDict(capacity).", expr.Callee);

        if (expr.Args.Count != 2)
            throw UserError(
                "d.get(key, default) needs the default spelled out: PyMCU has no None value to " +
                "return for a missing key.", expr.Callee);

        return EmitDictLookup(dict, expr.Args[0], expr.Args[1]);
    }

    // Resolve an overloaded call to its concrete mangled name: build a type suffix
    // from the positional arg types, prefer the exact match, else a default-aware
    // arity fallback. Returns callee unchanged when it is not overloaded.
    /// <summary>
    /// True when a resolved name stands for a contiguous buffer that a call marshals by its
    /// BASE ADDRESS -- the same three sets the argument-marshalling step consults before it
    /// rewrites the argument into an <c>ArrayBase</c>, plus the memoryview windows, which take
    /// the base of the array they look into. Overload selection has to ask exactly this
    /// question: an argument that travels as a pointer must never be typed as its element.
    /// </summary>
    private bool IsBufferStorageName(string key)
    {
        if (arraysWithVariableIndex.Contains(key) || moduleSramArrays.Contains(key)
            || bytearrayParams.Contains(key) || arrayViewBase.ContainsKey(key))
            return true;
        // A flat sequence (`s__0`, `s__1`, a slice temp) is in arraySizes too and has no `s:`
        // label, so requiring the resolved storage to be one of the contiguous sets is what
        // keeps a compile-time sequence out of the bytearray overload.
        return TryResolveArrayStorageKey(key, out var storage)
               && (arraysWithVariableIndex.Contains(storage) || moduleSramArrays.Contains(storage));
    }

    /// <summary>
    /// The flat storage name a field access stands for -- <c>self.temp</c> in a method of `d`
    /// is <c>d_temp</c>, and <c>self.inner.temp</c> is <c>d_inner_temp</c>. Aliases are
    /// followed at the root and at every hop, so a field that is itself another instance's
    /// name resolves before the next member is appended. Returns null when the chain does not
    /// start at a plain name (a subscript or a call in the middle has no flat name).
    /// </summary>
    private string? FlattenFieldChain(MemberAccessExpr field)
    {
        var members = new List<string>();
        Expression node = field;
        while (node is MemberAccessExpr m)
        {
            members.Add(m.Member);
            node = m.Object;
        }

        if (node is not VariableExpr root) return null;
        members.Reverse();

        string b = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + root.Name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + root.Name : root.Name);
        for (int d = 0; d < 20 && variableAliases.TryGetValue(b, out var ba); d++) b = ba;

        // Every hop but the last is followed through its aliases here; the last one is left
        // alone so the caller's own alias walk keeps asking its questions in the order it
        // asked them before this chain existed.
        for (int i = 0; i < members.Count; i++)
        {
            b = b + "_" + members[i];
            if (i + 1 == members.Count) break;
            for (int d = 0; d < 20 && variableAliases.TryGetValue(b, out var ba); d++) b = ba;
        }

        return b;
    }

    /// <summary>
    /// True when a DECLARED type names a buffer rather than a scalar -- `bytearray`, `bytes`,
    /// or a sized array spelling such as `uint8[4]`. A value of such a type reaches a call as a
    /// base address, so overload selection has to spell it "bytearray".
    /// </summary>
    /// Whether <paramref name="e"/> is a @property read or a method call whose declared result
    /// is a buffer.
    private bool IsDeclaredBufferResult(Expression e)
    {
        if (e is MemberAccessExpr { Object: VariableExpr recv } prop && IsPropertyGetterRead(prop)
            && InstanceClassOfName(recv.Name) is { } cls
            && ResolveMROPropertyClass(cls, prop.Member) is { } propCls)
            return DeclaredTypeIsBuffer(functionReturnTypes.GetValueOrDefault(propCls + "_" + prop.Member));
        if (e is CallExpr { Callee: MemberAccessExpr mc } && FieldOwnerClass(mc) is { } owner)
            return DeclaredTypeIsBuffer(functionReturnTypes.GetValueOrDefault(owner + "_" + mc.Member));
        return false;
    }

    private static bool DeclaredTypeIsBuffer(string? declared)
    {
        if (string.IsNullOrEmpty(declared)) return false;
        if (declared is "bytearray" or "bytes") return true;
        int open = declared.IndexOf('[');
        return open > 0 && declared.EndsWith("]", StringComparison.Ordinal)
               && declared[(open + 1)..^1].All(char.IsDigit)
               && open + 1 < declared.Length - 1;
    }

    /// <summary>
    /// A declared parameter type that holds one number: the integer widths, `int`, `bool` and
    /// `float`, bare or wrapped in `const[...]`. A buffer can never be one -- it travels as an
    /// address, and a number parameter reads that address as its own value.
    /// </summary>
    private static bool DeclaredTypeIsNumber(string? declared)
    {
        if (string.IsNullOrEmpty(declared)) return false;
        string t = declared.StartsWith("const[", StringComparison.Ordinal) && declared.EndsWith("]")
            ? declared[6..^1] : declared;
        return t is "uint8" or "int8" or "uint16" or "int16" or "uint32" or "int32"
            or "int" or "bool" or "float";
    }

    /// <summary>
    /// A call argument that is a buffer rather than a number: a bytes or list literal
    /// (`b"ab"` parses to a ListExpr), a `bytes(...)` / `bytearray(...)` / `memoryview(...)`
    /// written in place, a name bound to a list literal, or a value the marshalling step
    /// already turned into a base address. A tuple is not counted: it is a compile-time group
    /// of values, not something a callee indexes as bytes.
    /// </summary>
    /// <param name="argScopePfx">The scope the argument EXPRESSION resolved under: the
    /// caller's inline prefix at an argument-binding window, where
    /// `currentInlinePrefix` already names the callee's own expansion. Null asks the
    /// question in the current scope, as the real-call site always did.</param>
    private bool ArgumentIsBuffer(Expression? arg, Val? evaluated, string? argScopePfx = null)
    {
        if (evaluated is ArrayBase) return true;
        // A local or parameter holding a number shadows a module-level buffer of the same
        // name: the storage lookups below fall back to the bare spelling, and `b: uint8` in
        // uart_write_byte_repr read as the program's own `b = b"AZ"`.
        if (arg is VariableExpr shadowVe && NameIsLocalScalar(shadowVe.Name, argScopePfx)) return false;
        if (evaluated is Variable ev && IsBufferStorageName(ev.Name)) return true;
        string argKey(string n) => argScopePfx != null
            ? argScopePfx + n
            : (!string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix
                : currentFunction + ".") + n;
        return arg switch
        {
            ListExpr => true,
            CallExpr { Callee: VariableExpr { Name: "bytes" or "bytearray" or "memoryview" } } => true,
            VariableExpr ve => NameIsListLiteralSequence(ve.Name, argScopePfx)
                               || IsBufferStorageName(argKey(ve.Name)),
            _ => false,
        };
    }

    /// <summary>
    /// The mirror of <see cref="RefuseBufferForNumberParam"/>: `takesbuf(buf[i])`, or `b` from
    /// `for i, b in enumerate(buf)` passed to `takesbuf(b)`, hands ONE ELEMENT -- a number --
    /// to a parameter declared 'bytearray'/'bytes' or a fixed array. CPython raises
    /// `TypeError: 'int' object is not subscriptable` the first time the callee indexes it;
    /// PyMCU instead marshalled the number as if it were the pointer a real buffer travels
    /// as, so `v[0]` inside the callee read whatever SRAM byte that number happened to
    /// address (`takesbuf(buf[0])` on `[10, 20, 30]` answered 255, 255, 30 -- never an error,
    /// never CPython's value). Refused at the argument, naming what was passed and what the
    /// parameter expects.
    /// </summary>
    private void RefuseNumberForBufferParam(string calleeName, string paramName, string? declared,
                                             Expression? arg, Val? evaluated)
    {
        // Not ArgumentIsBuffer alone: its shadowing guard (`arg is VariableExpr && NameIsLocalScalar
        // (bare name)`) asks whether the BARE spelling is shadowed by a local *at the current
        // inline depth*, and two sibling expansions of the same @inline method share one
        // generated prefix (`inline1.scan.` for every call to `d.scan(...)` at this call depth,
        // not one per call site). The first expansion's own parameter binding registers
        // `inline1.scan.buf` in variableTypes; the second expansion then reads that leftover
        // entry as "buf is shadowed here" and NameIsLocalScalar silently flips from false to
        // true -- same arguments, same prefix, different answer. `evaluated` itself still names
        // the caller's real storage both times, so resolving ITS name directly is the fix: this
        // mirrors the check's own target name that TryResolveArrayStorageKey's doc comment calls
        // the bare/qualified mismatch (PyMCU#258), not the shadow question.
        if (!DeclaredTypeIsBuffer(declared)
            || ArgumentIsBuffer(arg, evaluated)
            || (evaluated is Variable realArrVar && TryResolveArrayStorageKey(realArrVar.Name, out _))
            || !ArgumentIsScalarElement(arg)) return;
        string what = arg switch
        {
            IndexExpr { Target: VariableExpr iv } => $"'{iv.Name}[...]', one element,",
            VariableExpr ve => $"'{ve.Name}', one element,",
            _ => "a number",
        };
        throw UserError(
            $"'{calleeName}' is passed {what} for parameter '{paramName}', which is declared "
            + $"'{declared}' and reads a whole buffer. A '{declared}' parameter travels as the "
            + "buffer's address, so one element would be read back as that address instead of "
            + "the bytes. Pass the buffer itself, or declare the parameter a number type to "
            + "take one element.",
            arg);
    }

    /// A call argument guaranteed to carry a single number at this call site: a plain
    /// (non-slice) subscript of a buffer or of a list whose elements are themselves numbers
    /// (never a list of lists, where the element is a buffer in its own right), or a name
    /// bound to a local scalar -- which is exactly what an `enumerate()`/`for` loop variable
    /// is. Narrower than it could be on purpose: a literal or arithmetic expression passed to
    /// a buffer-typed parameter the callee never actually indexes is not a divergence from
    /// CPython (TypingOnlyNameTests' `d.take(0)` with an unused `buf` param never raises),
    /// so only the two shapes the enumerate/subscript bug actually produces are caught here.
    private bool ArgumentIsScalarElement(Expression? arg) => arg switch
    {
        IndexExpr { Index: not SliceExpr } ix => IndexTargetHoldsScalarElements(ix.Target),
        VariableExpr ve => NameIsScalarAtThisSite(ve.Name),
        _ => false,
    };

    /// <see cref="NameIsLocalScalar"/> skips the `main.` qualification and the bare name,
    /// because it only has to answer whether a module-level buffer is SHADOWED, and a
    /// top-level scalar has nothing further out to shadow. A loop variable from `for i, b in
    /// enumerate(buf)` written at top level is bound through <c>QualifyBoundName</c>, which
    /// qualifies it as `main.b` (or, rarely, the bare module-prefixed name when one already
    /// exists in <c>mutableGlobals</c>) -- never the plain `b` that helper's two-entry lookup
    /// tries. This checks every spelling a bound loop/local variable can resolve to, so a
    /// scalar reaching a buffer-typed parameter is caught regardless of where it was bound.
    private bool NameIsScalarAtThisSite(string name)
    {
        foreach (string? key in new[]
                 {
                     string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + name,
                     string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + name,
                     name,
                 })
        {
            if (key == null) continue;
            // TryResolveArrayStorageKey, not a bare arraySizes.ContainsKey(key): a module-level
            // array declared `buf: uint8[N]` registers BARE ("buf") in arraySizes, but a read
            // inside a function or an inline expansion arrives qualified ("main.buf") -- the
            // exact mismatch TryResolveArrayStorageKey's own comment documents (PyMCU#258).
            // IsBufferStorageName alone is not enough here: it additionally requires SRAM/
            // variable-index membership, which a small array nothing ever subscripts by a
            // runtime index (this one) never gets. Without the resolved-key check, `d.scan(buf,
            // 8)` called twice refused the SECOND call only: "main.buf" matched no exclusion,
            // fell through to variableTypes.ContainsKey("main.buf") -- a registration left by
            // the FIRST call's own argument marshal -- and a real buffer looked like a scalar.
            //
            // variableAliases.ContainsKey(key) && !valueTrackingAliases.Contains(key), not a
            // bare ContainsKey: `one = buf[0]` makes `one` a VALUE-TRACKING alias of the temp
            // that carried the loaded byte (Assign.cs's `value is Temporary` branch sets this
            // for every such assignment, structural or not) -- it is filed in variableAliases
            // for constant-folding, but `one` genuinely holds a number, not a buffer. Treating
            // every alias as "not a scalar" let `first(one)` -- buf[0]'s value passed to a
            // bytearray parameter -- compile silently instead of refusing: the exclusion must
            // only fire for a STRUCTURAL alias (an instance, a buffer forwarded under another
            // name), which valueTrackingAliases is exactly the set that is NOT.
            if (arraySizes.ContainsKey(key) || constSequenceBindings.ContainsKey(key)
                || listLiteralParams.ContainsKey(key) || bytearrayParams.Contains(key)
                || (variableAliases.ContainsKey(key) && !valueTrackingAliases.Contains(key))
                || IsBufferStorageName(key)
                || TryResolveArrayStorageKey(key, out _))
                return false;
            // A value-tracking alias has no entry of its own in variableTypes -- its type
            // lives on the temp it points at (`one`'s is on `tmp_24`, the load `buf[0]`
            // produced) -- so checking variableTypes(key) alone missed every one of them
            // and `one = buf[0]` fell through this whole loop unanswered. The alias exists
            // ONLY for a number by construction (Assign.cs's value-tracking path never
            // creates one for an instance or a buffer forward), so its presence answers
            // "scalar" directly, the same as a real variableTypes entry would.
            if (valueTrackingAliases.Contains(key) || variableTypes.ContainsKey(key)) return true;
        }
        return false;
    }

    /// True when subscripting `target` answers one number rather than another buffer: a real
    /// bytearray/bytes buffer (every element is one byte), or a heap list registered with a
    /// scalar element type (not one `listInnerElemTypes` also covers, which marks the element
    /// as itself a nested list).
    private bool IndexTargetHoldsScalarElements(Expression target)
    {
        if (target is not VariableExpr tv) return false;
        string prefixed = (!string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix
            : string.IsNullOrEmpty(currentFunction) || currentFunction == "main" ? ""
            : currentFunction + ".") + tv.Name;
        if (IsBufferStorageName(prefixed) || IsBufferStorageName(tv.Name)) return true;
        string key = ResolveListVarQualified(tv.Name);
        return key.Length > 0 && listVarElemTypes.ContainsKey(key)
               && !listInnerElemTypes.ContainsKey(key);
    }

    /// A name bound to a bytes or list literal of constants (`z = b"QR"`), which lives as a
    /// compile-time sequence rather than as storage. A tuple or a range bound the same way is
    /// a group of values and is left out.
    private bool NameIsListLiteralSequence(string name, string? scopePfx = null)
    {
        // `seqName` qualifies the name under the caller's scope when one is given, so a
        // parameter's binding answers rather than a module-level name that shares it.
        string seqName = scopePfx != null ? scopePfx + name : name;
        return !NameIsLocalScalar(name, scopePfx)
           && !IsTupleBound(name)
           && ResolveConstSequence(seqName) != null
           && !rangeBoundSequences.Contains(seqName)
           && !(scopePfx == null && !string.IsNullOrEmpty(currentFunction) && rangeBoundSequences.Contains(currentFunction + "." + name))
           && !(scopePfx == null && !string.IsNullOrEmpty(currentInlinePrefix) && rangeBoundSequences.Contains(currentInlinePrefix + name));
    }

    /// A parameter bound to a literal the body cannot use as a compile-time sequence.
    /// A subscript or slice of it assigned (`buf[i] = v`) is refused outright: nothing
    /// can write a flash table, the binding's dead slot otherwise ate the store
    /// (`b[0] = 65` compiled to `bset put.b, 0`), and a bytes object takes no item
    /// assignment in CPython either. When nothing writes it, a run-time subscript
    /// still needs storage only where the materialised-on-demand table cannot answer:
    /// an element that is not a compile-time constant. Everything else keeps the
    /// literal binding: a constant subscript folds, a served run-time subscript reads
    /// the `__cttab` flash table, and the marshal lays out a `__seqarg` copy when the
    /// name goes whole into a call.
    private bool BufferParamNeedsStorage(Block body, string name, ListExpr lit)
    {
        static bool IsName(Expression? e, string n) => e is VariableExpr v && v.Name == n;
        foreach (var s in TypeInference.WalkStatements(body))
        {
            var target = s switch
            {
                AssignStmt a => a.Target,
                AugAssignStmt g => g.Target,
                _ => null,
            };
            if (target is IndexExpr ti && IsName(ti.Target, name))
                throw UserError(
                    $"'{name}' holds {lit.Elements.Count} compile-time values with no "
                    + "storage behind them, so it cannot be written. Declare an array and "
                    + $"pass that (`table: uint8[{lit.Elements.Count}] = [...]`).", ti);
        }
        if (ConstValuesOf(lit.Elements) != null) return false;
        foreach (var e in TypeInference.WalkExpressions(
                     TypeInference.WalkStatements(body).ToList()))
            if (e is IndexExpr ix && IsName(ix.Target, name)
                                   && ix.Index is not IntegerLiteral)
                return true;
        return false;
    }

    /// A name the current function or inline expansion binds to a number, which hides any
    /// sequence or buffer of the same name further out. <paramref name="scopePfx"/> asks the
    /// question about a DIFFERENT frame -- the caller's, while `currentInlinePrefix` already
    /// names the callee's inside an argument-binding window.
    private bool NameIsLocalScalar(string name, string? scopePfx = null)
    {
        string?[] keys = scopePfx != null
            ? new string?[] { scopePfx.Length > 0 ? scopePfx + name : null }
            : new string?[]
            {
                string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + name,
                string.IsNullOrEmpty(currentFunction) || currentFunction == "main"
                    ? null : currentFunction + "." + name,
            };
        foreach (string? key in keys)
        {
            if (key == null) continue;
            if (arraySizes.ContainsKey(key) || constSequenceBindings.ContainsKey(key)
                || listLiteralParams.ContainsKey(key) || bytearrayParams.Contains(key))
                return false;
            // An alias inherits whatever it lands on: a parameter bound to a scalar
            // (`forward(buf[i])` hands `buf` the element's temp) IS the number and
            // shadows the module-level buffer the bare name would otherwise name;
            // bound to an array it keeps answering as a buffer. The sequence tables
            // are asked at every hop, not just the terminal: a chain can pass through
            // a literal-bound name on the way (`writeto.buffer` -> `write.buf` ->
            // `listLiteralParams`), and the scalar table may hold a stale width for a
            // name an earlier call site at this prefix bound by value.
            if (variableAliases.TryGetValue(key, out var alias))
            {
                for (int d = 0; d < 20; d++)
                {
                    if (arraySizes.ContainsKey(alias) || constSequenceBindings.ContainsKey(alias)
                        || listLiteralParams.ContainsKey(alias) || bytearrayParams.Contains(alias)
                        || IsBufferStorageName(alias))
                        return false;
                    if (!variableAliases.TryGetValue(alias, out var nxt)) break;
                    alias = nxt;
                }
                return variableTypes.ContainsKey(alias) || constantVariables.ContainsKey(alias);
            }
            if (variableTypes.ContainsKey(key)) return true;
        }
        return false;
    }

    /// <summary>
    /// `f(b"AB")` against `def f(x: uint8)`: the buffer arrives as its address and the
    /// parameter reads that address as a number, so `print(x)` printed 0 and
    /// `UART.write(b"DE\n")` put one 0x00 on the wire, with nothing said. Refused at the
    /// argument, naming what was passed and what the parameter holds.
    /// </summary>
    /// <paramref name="unannotatedScalar"/> is a real subroutine's parameter written without
    /// a type and never subscripted in its body: it is laid out as one byte, so the address is
    /// cut to its low byte (`def f(x): print(x)` printed 0 for `f(b"AB")`).
    private void RefuseBufferForNumberParam(string calleeName, string paramName, string? declared,
                                            Expression? arg, Val? evaluated,
                                            bool unannotatedScalar = false,
                                            string? argScopePfx = null)
    {
        if (!(DeclaredTypeIsNumber(declared) || unannotatedScalar)
            || !ArgumentIsBuffer(arg, evaluated, argScopePfx)) return;
        string what = arg switch
        {
            ListExpr => "a bytes or list literal",
            CallExpr { Callee: VariableExpr cv } => $"a {cv.Name}",
            VariableExpr ve => $"'{ve.Name}', a buffer,",
            _ => "a buffer",
        };
        string holds = unannotatedScalar
            ? $"which has no type and is never indexed in '{calleeName}', so it holds one number"
            : $"which is declared '{declared}' and holds one number";
        throw UserError(
            $"'{calleeName}' is passed {what} for parameter '{paramName}', {holds}. A buffer "
            + "travels as its address, so the parameter would read the address instead of the "
            + "bytes. Pass one element (`buf[0]`), or declare the parameter 'bytes' or "
            + "'bytearray' so it takes the whole buffer.",
            arg);
    }

    /// The bare class name inside a mangled class key (`mod_TCS34725` -> `TCS34725`).
    private string ShortClassNameOf(string fullKey)
    {
        foreach (var cn in classNames)
        {
            if (fullKey == cn) return cn;
            if (fullKey.Length > cn.Length && fullKey[fullKey.Length - cn.Length - 1] == '_' &&
                fullKey.EndsWith(cn, StringComparison.Ordinal)) return cn;
        }

        return fullKey;
    }

    /// <summary>
    /// The class a field access reads a member of -- written on the class itself
    /// (<c>D.BUF</c>) or reached through an instance (<c>self.BUF</c>). Null when the receiver
    /// is neither.
    /// </summary>
    private string? FieldOwnerClass(MemberAccessExpr field)
    {
        if (field.Object is not VariableExpr recv) return null;

        string written = AliasOriginal(recv.Name);
        if (classNames.Contains(written)) return written;
        string shortWritten = ShortClassNameOf(written);
        if (classNames.Contains(shortWritten)) return shortWritten;

        // ResolveNameKey walks the scope ladder (expansion prefix, enclosing
        // function, module, bare) to the key the receiver's name actually owns --
        // a module-level `factory` read inside an expansion is `factory`, not
        // `<prefix>factory`, which the single prefixed probe missed.
        string key = ResolveNameKey(recv.Name);
        for (int d = 0; d < 20; d++)
        {
            if (instanceClasses.TryGetValue(key, out var cls)) return ShortClassNameOf(cls);
            if (variableAliases.TryGetValue(key, out var nxt)) key = nxt;
            else break;
        }

        return null;
    }

    private string ResolveOverloadedCallee(string callee, CallExpr expr)
    {
        if (overloadedFunctions.Contains(callee))
        {
            // Overload selection is POSITIONAL, and a keyword argument is dropped before it
            // runs: the suffix loop below skips `KeywordArgExpr` and so does the arity count in
            // the fallback. A call written only with keywords therefore presents as a call with
            // no arguments at all, and what happened next depended on the overload set:
            //
            //   no zero-argument candidate   nothing matched, the UNMANGLED name was emitted,
            //                                and `avr-ld` said `undefined reference to 'w'`
            //   a zero-argument candidate    that one was selected, then refused the keyword
            //                                the caller wrote, which the intended overload has
            //   a method                     `missing argument 'self' in call to 'A_w'`, naming
            //                                a mangled symbol the program never contained
            //
            // None of the three names the cause, and the first is not a diagnostic at all: it
            // is a link error in a tool the reader did not invoke. Refused here instead, at the
            // call, which is the line they can change. Issue #232.
            if (expr.Args.Any(a => a is KeywordArgExpr))
            {
                string written = expr.Callee switch
                {
                    VariableExpr kv => kv.Name,
                    MemberAccessExpr km => km.Member,
                    _ => callee,
                };
                throw UserError(
                    $"'{written}' has more than one @inline overload, and an overload is chosen " +
                    "by the types of the POSITIONAL arguments, so a keyword argument cannot " +
                    $"select one. Pass the arguments positionally: {written}(value) rather than " +
                    $"{written}(name=value)", expr.Callee);
            }

            string ShortClassName(string fullKey) => ShortClassNameOf(fullKey);

            string ArgTypeSuffix(Expression arg)
            {
                if (arg is StringLiteral) return "str";
                // A bytes or list literal (`b"DE\n"` parses to a ListExpr), `bytes(...)` and
                // `bytearray(...)` written in place, and a name bound to a list literal are
                // buffers. They typed as their ELEMENT, so `UART.write(b"DE\n")` took the
                // `write(data: uint8)` overload and put one 0x00 on the wire.
                if (arg is ListExpr
                    || arg is CallExpr { Callee: VariableExpr { Name: "bytes" or "bytearray" } }
                    || (arg is VariableExpr seqVe && NameIsListLiteralSequence(seqVe.Name)))
                    return "bytearray";
                // A memoryview, and a slice of one, travel as a base address plus an offset --
                // the same way the array they look into travels. Neither has a name at this
                // point (the window is created when the argument is visited), so both typed as
                // their element and took the scalar overload. This one was never about fields:
                // a view over a module-level buffer was wrong in exactly the same way.
                if (arg is CallExpr { Callee: VariableExpr { Name: "memoryview" } }) return "bytearray";
                if (arg is IndexExpr { Index: SliceExpr } viewSlice
                    && ArgTypeSuffix(viewSlice.Target) == "bytearray")
                    return "bytearray";
                // A nested constructor call types as its class: ADC(Pin(14)) must select
                // the Pin overload, not fall through to a numeric suffix and land on
                // the const[uint8] channel overload.
                if (arg is CallExpr { Callee: VariableExpr ctor })
                {
                    string ctorName = AliasOriginal(ctor.Name);
                    if (classNames.Contains(ctorName)) return ctorName;
                    string shortCtor = ShortClassName(ctorName);
                    if (classNames.Contains(shortCtor)) return shortCtor;

                    // A call to a FUNCTION declared to return a compile-time string --
                    // `Low(name_for(n), k)`. InferExprType below has no string to report, so the
                    // argument typed numerically and the call bound to the numeric overload with
                    // nothing said. The declared return type answers it without evaluating the
                    // argument, which has not been visited at this point.
                    foreach (var fnKey in new[] { ResolveCallee(ctor.Name), ctorName, shortCtor })
                        if (functionReturnTypes.TryGetValue(fnKey, out var frt)
                            && frt is "str" or "const[str]")
                            return "str";

                    foreach (var fnKey in new[] { ResolveCallee(ctor.Name), ctorName, shortCtor })
                        if (functionReturnTypes.TryGetValue(fnKey, out var frt)
                            && DeclaredTypeIsBuffer(frt))
                            return "bytearray";
                }

                // The same question for a METHOD that hands its buffer back -- `sink(self.buf())`
                // against `def buf(self) -> bytearray`. The declared return type answers it
                // without evaluating the call, which has not been visited at this point, and
                // without it the argument typed as the element and took the scalar overload.
                if (arg is CallExpr { Callee: MemberAccessExpr methodCallee }
                    && FieldOwnerClass(methodCallee) is { } methodOwner
                    && functionReturnTypes.TryGetValue(methodOwner + "_" + methodCallee.Member, out var mrt)
                    && DeclaredTypeIsBuffer(mrt))
                    return "bytearray";
                if (arg is VariableExpr v)
                {
                    string key = currentInlinePrefix + v.Name;
                    // Whether `v.Name` is bound in THIS frame -- an inline parameter or a
                    // local. When it is, the bare-name array check below must not fire:
                    // `buf` the scalar parameter shares its spelling with the caller's
                    // global array `buf`, and reading the bare name would pick the
                    // bytearray overload for a plain scalar element.
                    bool nameBoundHere = ShadowingFrameKey(v.Name) != null;
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
                    //
                    // qKey ("<enclosing function>.<bare name>") is a FALLBACK guess for when
                    // `key`'s own alias-chasing (from currentInlinePrefix) finds nothing --
                    // useful outside any inline expansion, where currentInlinePrefix is empty
                    // and `key` is just the bare name itself. INSIDE an inline expansion,
                    // `key` is already the properly scoped "<inline-frame>.<name>", and qKey's
                    // guess (currentFunction, the OUTERMOST function, "." + the bare name)
                    // stops being a fallback and becomes a coincidence: it matches whenever an
                    // UNRELATED variable in the outer scope happens to share the same bare
                    // name. `uart.write(buf[j])`, `buf` a variable-indexed array, forwarded
                    // through the MicroPython compat layer's machine.UART.write(uint8) (whose
                    // OWN parameter is also spelled "buf") to the native HAL's write(bytearray)
                    // overload: qKey = "main.buf" happened to name the CALLER's own array,
                    // wrongly selecting the bytearray overload for a plain uint8 read
                    // (#p2avr-7, found adding the native write(bytearray) overload this
                    // fallback had never been exercised against before).
                    bool insideInline = !string.IsNullOrEmpty(currentInlinePrefix);
                    string qKey = !insideInline && !string.IsNullOrEmpty(currentFunction)
                        ? currentFunction + "." + v.Name : key;
                    if (arraysWithVariableIndex.Contains(key) || arraysWithVariableIndex.Contains(qKey) ||
                        (!nameBoundHere && arraysWithVariableIndex.Contains(v.Name)) ||
                        moduleSramArrays.Contains(key) || moduleSramArrays.Contains(qKey) ||
                        bytearrayParams.Contains(key) || bytearrayParams.Contains(qKey))
                        return "bytearray";
                }

                // A FIELD argument (`self._name`) was typed by InferExprType alone, which has no
                // string to report, so a const[str] field bound to the numeric overload: the
                // MicroPython layer stores the port name in `self._name` and hands it to the HAL
                // as `_Pin(self._name, mode)`, and every pin came out as whichever overload was
                // declared first. Fields flatten to `<base>_<member>`, so resolve that name the
                // same way a plain variable is resolved.
                // A field of a field (`self.i2c_device.buffer`) is the same argument one hop
                // further in, and it flattens the same way -- `d_inner_temp`. Reading only the
                // outermost member left every such argument to InferExprType, which is how a
                // buffer held one object down still reached the scalar overload after the
                // direct field stopped doing so.
                if (arg is MemberAccessExpr fieldArg && FlattenFieldChain(fieldArg) is { } chainFlat)
                {
                    string flat = chainFlat;
                    for (int depth = 0; depth < 20; depth++)
                    {
                        if (instanceClasses.TryGetValue(flat, out string fic)) return ShortClassName(fic);
                        if (strConstantVariables.ContainsKey(flat)) return "str";
                        if (IsBufferStorageName(flat)) return "bytearray";
                        if (variableAliases.TryGetValue(flat, out string fak)) flat = fak;
                        else break;
                    }

                    // A CLASS attribute (`BUF = bytearray(2)` written in the class body) has no
                    // per-instance storage: it is registered once under the class-canonical
                    // name, which is neither the name the instance spells (`d_BUF`) nor the one
                    // the class spells (`D_BUF`) -- it is `main.D_BUF`. Both spellings reach one
                    // buffer and both took the scalar overload.
                    if (FieldOwnerClass(fieldArg) is { } ownerCls
                        && IsBufferStorageName(ownerCls + "_" + fieldArg.Member))
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
                // An argument that IS an instance must never bind to a numeric parameter. The
                // suffix of an instance argument is its class name, so "is this a class?" is the
                // same question on both sides -- and asking it keeps ADC(<an instance>) off the
                // ADC(channel: const[uint8]) overload, where it landed as "'__c3' varies at run
                // time", a message about a temporary the program never mentions.
                bool IsInstanceType(string t) => classNames.Contains(t);
                // An integer argument must not bind to a float parameter while an integer one is
                // on offer, and the reverse. Before this the shape test asked ONLY "is this an
                // instance?", which both numeric candidates answered the same way, so the winner
                // was whichever key `inlineFunctions` happened to enumerate first. Declaring the
                // float overload second was enough to make `floor(count)` on a whole number pick
                // it and drag the software float routines into the image, silently (PyMCU#182).
                // Swapping the two declarations swapped the answer, which is how the ordering
                // dependence was measured rather than inferred.
                bool IsFloatType(string t) => t == "float";
                var argSuffixes = suffix == "void"
                    ? new List<string>()
                    : suffix.Split('_').ToList();
                // A buffer argument wants a buffer parameter, and a number wants a number: with
                // `write(data: uint8)` declared before `write(buf: bytes)`, a buffer took the
                // first one here and the guard below then refused a call the second one takes.
                bool IsBufferShape(string t) => DeclaredTypeIsBuffer(t)
                    || t == "list" || t.StartsWith("list[", StringComparison.Ordinal)
                    || t == "ptr" || t.StartsWith("ptr[", StringComparison.Ordinal);
                bool SameShape(List<Param> ps)
                {
                    if (ps.Count != argSuffixes.Count) return false;
                    for (int i = 0; i < ps.Count; i++)
                        if (IsInstanceType(NormType(ps[i].Type)) != IsInstanceType(argSuffixes[i])
                            || IsFloatType(NormType(ps[i].Type)) != IsFloatType(argSuffixes[i])
                            || IsBufferShape(NormType(ps[i].Type)) != IsBufferShape(argSuffixes[i]))
                            return false;
                    return true;
                }

                string? pick = null;
                string? sameShape = null;
                string? arityOnly = null;
                string? exactPick = null;
                foreach (var kvp in inlineFunctions)
                {
                    if (!kvp.Key.StartsWith(callee + "___")) continue;
                    var ps = kvp.Value.Params.Where(p => p.Name != "self").ToList();
                    if (ps.Count != argCount) continue;
                    arityOnly ??= kvp.Key;
                    if (sameShape is null && SameShape(ps)) sameShape = kvp.Key;
                    if (argCount == 0
                        || string.Join("_", ps.Select(p => NormType(p.Type))) == suffix)
                    {
                        pick = exactPick = kvp.Key;
                        break;
                    }
                }

                // An exact-arity overload that agrees on which arguments are instances beats
                // anything the default-aware step below can offer, and it must be taken BEFORE
                // that step: the step fills `pick` with the first arity-compatible overload it
                // finds, after which no later preference can be applied.
                pick ??= sameShape;

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
                    if (typed != null) exactPick = typed;
                    pick = typed ?? anyArity;
                }

                // Nothing matched on types: keep the old arity-only choice rather than failing to
                // resolve at all, so a call whose argument types we cannot name still compiles.
                pick ??= arityOnly;

                // A choice that was not an exact type match is a choice made on SHAPE, and a
                // shape the candidate does not have is not a choice at all: it is whichever key
                // the registry enumerated first. Widening one number into another is fine and is
                // what the steps above are for. A BUFFER against a parameter that is not one is
                // not: the buffer arrives as an address and the parameter reads it as its own
                // type, which is the silent wrong byte on the wire PyMCU#503 was reported for.
                // Say so at the call, which is the line the caller can change.
                if (pick != null && pick != exactPick
                    && inlineFunctions.TryGetValue(pick, out var picked))
                {
                    var pps = picked.Params.Where(p => p.Name != "self").ToList();
                    string ShapeOf(string t) => IsBufferShape(t) ? "a buffer"
                        : IsInstanceType(t) ? "an instance of " + t
                        : IsFloatType(t) ? "a float" : "a number";

                    for (int i = 0; i < argSuffixes.Count && i < pps.Count; i++)
                    {
                        string want = NormType(pps[i].Type), got = argSuffixes[i];
                        if (IsBufferShape(want) == IsBufferShape(got)) continue;

                        string writtenName = expr.Callee switch
                        {
                            VariableExpr nv => nv.Name,
                            MemberAccessExpr nm => nm.Member,
                            _ => callee,
                        };
                        var offered = inlineFunctions.Keys
                            .Where(k => k.StartsWith(callee + "___", StringComparison.Ordinal))
                            .Select(k => k[(callee.Length + 3)..].Replace("_", ", "))
                            .ToList();
                        throw UserError(
                            $"no @inline overload of '{writtenName}' takes {ShapeOf(got)} as " +
                            $"argument {i + 1}. The overloads on offer take " +
                            string.Join(" / ", offered.Select(o => $"({o})")) +
                            $", and the closest one wants {ShapeOf(want)} there. Selection is by " +
                            "the types of the positional arguments, so declare an overload for " +
                            "the type being passed, or pass a value of a type one of them takes",
                            expr.Callee);
                    }
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

        // The child class super() searches from is the class the calling method was defined
        // on. An OUTLINED body keeps it in methodInstanceTypes[currentFunction]; an EXPANDED
        // one keeps it on the innermost inline frame's callee (currentFunction is the
        // enclosing real function there, so it never answered -- `super()._pixel` inside a
        // force-inlined override reported "super() is a Python builtin that PyMCU does not
        // provide"). The module prefix is only the inline construction path's fallback
        // (super().__init__()).
        string childClass =
            inlineStack.Count > 0
            && inlineStack[^1].CalleeName is { Length: > 0 } expandedCallee
            && methodInstanceTypes.TryGetValue(expandedCallee, out var mitExpanded)
                ? mitExpanded
                : methodInstanceTypes.TryGetValue(currentFunction, out var mitChild)
                    ? mitChild
                    : (string.IsNullOrEmpty(currentModulePrefix)
                        ? ""
                        : currentModulePrefix.Substring(0, currentModulePrefix.Length - 1));
        if (!classBasePrefixes.TryGetValue(childClass, out var basePrefix)) return null;

        // Walk the MRO: the member may live on a grandparent. Matrix16x8's
        // super()._pixel skips Matrix8x8 (which defines `pixel`, not `_pixel`)
        // and lands on HT16K33._pixel in a different module. Previously only the
        // direct base was tried, so the call fell through to the builtin table
        // and reported "super() is a Python builtin PyMCU does not provide".
        FunctionDef? funcSuper = null;
        for (string? searchPrefix = basePrefix;
             !string.IsNullOrEmpty(searchPrefix);)
        {
            var candidate = searchPrefix + mem.Member;
            if (inlineFunctions.TryGetValue(candidate, out funcSuper)
                || instanceMethodDefs.TryGetValue(candidate, out funcSuper)
                || methodAstByName.TryGetValue(candidate, out funcSuper))
            {
                basePrefix = searchPrefix;
                break;
            }
            string clsKey = searchPrefix.Substring(0, searchPrefix.Length - 1);
            if (!classBasePrefixes.TryGetValue(clsKey, out searchPrefix))
                break;
        }
        // The base method may be @inline (in inlineFunctions) OR a default-outlined method
        // (only its AST is in instanceMethodDefs). Either way, expand its BODY in place with
        // self aliased to the current instance -- this sidesteps the outlined-call ABI and
        // works whether the OVERRIDING method is itself outlined or force-inlined. Before,
        // only an @inline base method resolved; a non-inline one fell through to an undefined
        // 'super' (super().<method>() only worked for __init__).
        if (funcSuper == null)
            return null;

        // The KEY the in-scope `self` alias is filed under: the inline frame's
        // prefixed name, or -- inside a bound-instance subroutine, where no
        // inline prefix exists -- the function-qualified `synth.self`.
        // EmitUnboundMethodBody re-aliases it through variableAliases itself,
        // so it wants the raw key, not the resolved root ResolveNameKey returns.
        return EmitUnboundMethodBody(basePrefix, funcSuper,
            !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + "self"
                : currentFunction + ".self",
            expr.Args, $"super().{mem.Member}");
    }

    // Base.method(self, ...) -- the unbound spelling of a base-class call, and ordinary
    // Python: a constructor forwarding with `Base.__init__(self, offset)` is what a great
    // deal of code writes before learning super() is the spelling this compiler grew first.
    // Callee resolution mangled it as <CallingClass>_<Base>_<method> (the base class name
    // treated as part of the method name, looked up under the caller's own prefix), so the
    // build failed naming a function the program never mentions.
    //
    // The receiver arrives as the first argument instead of through super(), so bind it and
    // expand the same body the bound call reaches. Returns null -- fall through to the
    // ordinary path -- for anything that is not a method call on an instance: a
    // @staticmethod, a class-level helper, a zero-argument Cls.m().
    private Val? TryEmitUnboundClassMethodCall(CallExpr expr)
    {
        if (expr.Callee is not MemberAccessExpr { Object: VariableExpr clsVe } mem) return null;
        if (!classNames.Contains(clsVe.Name)) return null;
        if (expr.Args.Count == 0) return null;
        if (expr.Args[0] is KeywordArgExpr or StarArgExpr) return null;

        string cls = ResolveCallee(clsVe.Name);
        string callee = cls + "_" + mem.Member;
        if (!inlineFunctions.TryGetValue(callee, out var func)
            && !instanceMethodDefs.TryGetValue(callee, out func)
            && !methodAstByName.TryGetValue(callee, out func))
            return null;

        // A @staticmethod has no receiver parameter, so its call is an ordinary function
        // call the normal path already resolves. Only a method whose first parameter is the
        // receiver takes the first argument as self.
        // A @staticmethod has no receiver parameter, so its call is an ordinary function call
        // the normal path already resolves. A method that got the OUTLINED ABI does have one,
        // spelled as one `self_<field>` parameter per field rather than as `self`: that shape
        // is expanded like any other, which is what makes the two spellings behave alike now
        // that the shared emitter binds a flattened receiver correctly (PyMCU#157).
        if (func.Params.Count == 0 || !IsReceiverParamName(func.Params[0].Name)) return null;

        // The first argument must actually name an instance. `self` inside a method resolves
        // through the current inline frame exactly as the super() path resolves it; any other
        // spelling has to be a name already known to carry a class, otherwise this is not the
        // construct it looks like and the ordinary path keeps its own diagnostics.
        // This test must not EMIT anything. It can still fail, and on failure the call falls
        // through to the ordinary path, which evaluates the receiver itself: a receiver
        // evaluated here and again there RUNS TWICE. Visiting it cost exactly that --
        // `Base.read(r.probe(), x)` emitted two calls to probe(), so a receiver expression
        // with a side effect performed it twice with nothing reported.
        //
        // So the receiver is resolved from the AST, by name. That is no loss of reach: the
        // instance has to be keyed by name here anyway, and a receiver that is not a name has
        // no key. Anything else returns null before a single instruction is emitted, and the
        // ordinary path keeps its own diagnostics.
        if (expr.Args[0] is not VariableExpr recvVe) return null;

        string selfKey;
        if (recvVe.Name == "self")
        {
            // The alias KEY, as above: inside a bound-instance subroutine there is
            // no inline prefix to qualify `self` with, so it lives on the
            // function-qualified name. EmitUnboundMethodBody re-aliases the key
            // through variableAliases, so the resolved root would be wrong here.
            selfKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + "self"
                : currentFunction + ".self";
        }
        else
        {
            // Same qualification order the read side uses: inline prefix, then the enclosing
            // function, then the bare name.
            string? found = null;
            foreach (var key in new[]
            {
                string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + recvVe.Name,
                string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + recvVe.Name,
                recvVe.Name,
            })
            {
                if (key == null) continue;
                string? chased = key;
                for (int hop = 0; hop < 20 && chased != null && !instanceClasses.ContainsKey(chased); ++hop)
                    chased = variableAliases.TryGetValue(chased, out var next) ? next : null;
                if (chased != null && instanceClasses.ContainsKey(chased)) { found = key; break; }
            }

            if (found == null) return null;
            selfKey = found;
        }

        return EmitUnboundMethodBody(cls + "_", func, selfKey, expr.Args.Skip(1).ToList(),
            $"{clsVe.Name}.{mem.Member}");
    }

    // Shared body of the two spellings of an explicit base-class call, super().m(args) and
    // Base.m(self, args): expand the method's body in place with self bound to
    // <selfAliasKey>'s instance and <args> bound to the remaining parameters.
    //
    // <spelling> is what the user wrote, for the arity diagnostic: the mangled callee carries a
    // module prefix nobody typed and nobody can search their own file for.
    // The receiver of a method reaches its body either as `self`, or, for a method that got
    // the outlined ABI, as one `self_<field>` parameter per field. Neither is an argument the
    // caller writes, and both must be skipped when binding arguments.
    /// <summary>
    /// The shape a signature's `*` and `/` give a call (PEP 3102, PEP 570), refused with
    /// CPython's own sentences. A keyword-only parameter was bound by position -- `f(x, 7)`
    /// against `def f(size, *, order=3)` set `order` to 7 where CPython raises TypeError --
    /// and a positional-only one by name, in the one front end that parsed `/` at all (#389).
    /// Only a signature that has either marker is checked, so no other call changes.
    /// </summary>
    private void CheckSignatureShape(FunctionDef fn, List<Expression> args, string qualName,
                                     ASTNode? at)
    {
        if (!fn.Params.Any(p => p.IsKeywordOnly || p.IsPositionalOnly)) return;
        if (args.Any(a => a is StarArgExpr)) return;   // a spread's length is the call site's

        int receivers = fn.Params.TakeWhile(p => IsReceiverParamName(p.Name)
                                                 || (fn.IsClassMethod && p == fn.Params[0])).Count();
        var declared = fn.Params.Skip(receivers).ToList();
        bool hasVarArg = declared.Any(p => p.IsVarArg);
        bool hasKwArg = declared.Any(p => p.IsKwArg);
        var positionalParams = declared.Where(p => !p.IsKeywordOnly && !p.IsVarArg && !p.IsKwArg).ToList();
        var positionalArgs = args.Where(a => a is not KeywordArgExpr).ToList();

        if (!hasVarArg && positionalArgs.Count > positionalParams.Count)
        {
            int most = positionalParams.Count + receivers;
            int least = positionalParams.Count(p => p.DefaultValue == null) + receivers;
            string takes = least == most
                ? $"{most} positional argument{(most == 1 ? "" : "s")}"
                : $"from {least} to {most} positional arguments";
            int given = positionalArgs.Count + receivers;
            throw UserError($"{qualName}() takes {takes} but {given} " +
                            $"{(given == 1 ? "was" : "were")} given",
                positionalArgs[positionalParams.Count]);
        }

        if (!hasKwArg)
        {
            var misplaced = args.OfType<KeywordArgExpr>()
                .Where(k => declared.Any(p => p.IsPositionalOnly && p.Name == k.Key)).ToList();
            if (misplaced.Count > 0)
                throw UserError($"{qualName}() got some positional-only arguments passed as " +
                                $"keyword arguments: '{string.Join(", ", misplaced.Select(k => k.Key))}'",
                    misplaced[0]);
        }
    }

    /// <summary>
    /// A keyword argument binds exactly as the positional one it stands for. The binding
    /// loop below gives a positional argument every sequence shape (a tuple or list literal,
    /// a bytes literal, a sequence of instances) and a keyword argument only a VALUE, so
    /// `f(a=(1, 2))` was refused as "tuples are not supported as runtime values" while
    /// `f((1, 2))` compiled: `keypad.KeyMatrix(row_pins=(...), column_pins=(...))`, the way
    /// the CircuitPython documentation writes it. When the keywords fill the parameters right
    /// after the positional arguments with no gap, they are moved into those positions; a
    /// gap (a default in between) keeps the keyword path, which evaluates the default in the
    /// callee's scope. Rewritten in place: the order is Python's own binding, so doing it
    /// again is a no-op.
    /// </summary>
    private void KeywordsToPositions(FunctionDef fn, List<Expression> args)
    {
        if (!args.Any(a => a is KeywordArgExpr) || args.Any(a => a is StarArgExpr)) return;
        if (fn.Params.Any(p => p.IsVarArg || p.IsKwArg)) return;
        int receivers = fn.Params.TakeWhile(p => IsReceiverParamName(p.Name)
                                                 || (fn.IsClassMethod && p == fn.Params[0])).Count();
        var declared = fn.Params.Skip(receivers).ToList();
        var positional = args.Where(a => a is not KeywordArgExpr).ToList();
        var byName = new Dictionary<string, Expression>();
        foreach (var kw in args.OfType<KeywordArgExpr>())
            if (!byName.TryAdd(kw.Key, kw.Value)) return;          // the binder names the repeat
        if (byName.Keys.Any(k => declared.FindIndex(p => p.Name == k) < positional.Count))
            return;                                               // "multiple values", said there
        // A keyword-only parameter stays a keyword: the shape check reads the call again
        // whenever the same call node is lowered again, and must still see it as one.
        int next = positional.Count;
        var moved = new List<Expression>();
        while (next < declared.Count && !declared[next].IsKeywordOnly
               && byName.Remove(declared[next].Name, out var v))
        {
            moved.Add(v);
            next++;
        }
        if (moved.Count == 0) return;
        var kept = args.OfType<KeywordArgExpr>().Where(k => byName.ContainsKey(k.Key)).ToList();
        // A keyword left for the keyword path evaluates after the positional binds -- later
        // than where it was written. With an effectful argument in the call that reordering
        // is observable, so the keyword path keeps the whole call.
        if (kept.Count != 0
            && args.Any(a => OperandCanHaveAnEffect(a is KeywordArgExpr ka ? ka.Value : a)))
            return;
        var written = args.ToList();
        var ordered = new List<Expression>();
        ordered.AddRange(positional);
        ordered.AddRange(moved);
        ordered.AddRange(kept);
        // `moved` sits in declared order; Python evaluates the arguments as they were
        // written. PinKeywordOrder answers the difference the way ReorderCallArgs does:
        // `f(b=_c, a=bump())` still reads _c before bump() runs.
        args.Clear();
        args.AddRange(PinKeywordOrder(written, ordered));
    }

    /// The name CPython's call errors give a function: `f`, or `Cls.m` for a method.
    private string QualNameForCall(FunctionDef fn, string callee)
    {
        bool isMethod = fn.Params.Count > 0 && (IsReceiverParamName(fn.Params[0].Name) || fn.IsClassMethod);
        if (isMethod && callee.EndsWith("_" + fn.Name, StringComparison.Ordinal))
            return ShortClassName(callee[..^(fn.Name.Length + 1)]) + "." + fn.Name;
        return fn.Name;
    }

    private static bool IsReceiverParamName(string name) =>
        name == "self" || name.StartsWith("self_", StringComparison.Ordinal);

    /// <summary>
    /// Bind a call's arguments to a method's parameters by NAME as well as by position (#349).
    ///
    /// The binding loop below is positional, so a `KeywordArgExpr` reached VisitExpression and
    /// came out as "Unknown Expression type: KeywordArgExpr" -- the name of a class in this
    /// compiler, about a program that contains no such word. That is what
    /// `super().__init__(pwm_out, min_pulse=min_pulse, max_pulse=max_pulse)` printed, which is
    /// line 110 of adafruit_motor/servo.py and the normal way a driver subclass forwards.
    ///
    /// Against the FunctionDef's own parameters rather than through ReorderCallArgs, which
    /// reads the `functionParams` tables a method is not registered in. The rules are Python's
    /// and the ordinary path's: positional arguments fill the leading parameters, keywords bind
    /// by name, a gap before the last supplied argument takes the parameter's default, and an
    /// unknown or repeated keyword is refused by name at the keyword itself.
    /// </summary>
    private List<Expression> BindMethodArgs(FunctionDef fn, List<Expression> args, string spelling)
    {
        CheckSignatureShape(fn, args,
            spelling.EndsWith(fn.Name, StringComparison.Ordinal) ? spelling : spelling + "." + fn.Name,
            args.FirstOrDefault());
        var parameters = fn.Params.Where(p => !IsReceiverParamName(p.Name)).ToList();

        // A `*args` or a `**kwargs` on the base is bound from what is left over, so the
        // declared parameters end where the first of the two begins (#368). The extras ride
        // back on the end of the returned list and EmitUnboundMethodBody sorts them by shape.
        int varIdx = parameters.FindIndex(p => p.IsVarArg);
        int kwIdx = parameters.FindIndex(p => p.IsKwArg);
        int firstVariadic = parameters.Count;
        if (varIdx >= 0) firstVariadic = Math.Min(firstVariadic, varIdx);
        if (kwIdx >= 0) firstVariadic = Math.Min(firstVariadic, kwIdx);

        // Nothing to bind and nothing to fill in: the argument list already covers every
        // parameter, so it is handed back exactly as it arrived and the firmware is unchanged.
        if (varIdx < 0 && kwIdx < 0
            && !args.Any(a => a is KeywordArgExpr) && args.Count >= parameters.Count) return args;
        var positional = new List<Expression>();
        var byName = new Dictionary<string, Expression>();
        var leftoverKeywords = new List<Expression>();
        foreach (var a in args)
        {
            if (a is not KeywordArgExpr kw) { positional.Add(a); continue; }
            if (!parameters.Any(p => p.Name == kw.Key && !p.IsVarArg && !p.IsKwArg))
            {
                if (kwIdx >= 0) { leftoverKeywords.Add(kw); continue; }
                throw UserError($"unknown keyword argument '{kw.Key}' in call to '{spelling}'", kw);
            }
            if (!byName.TryAdd(kw.Key, kw.Value))
                throw UserError($"keyword argument '{kw.Key}' repeated in call to '{spelling}'", kw);
        }

        // Up to the last parameter that HAS a value, from any source -- a position, a keyword,
        // or its own default. The positional loop this feeds binds nothing past the end of the
        // list it is given and leaves the rest unbound, so stopping at the last EXPLICIT value
        // left a trailing defaulted parameter with no value at all: `super().__init__(a, b=7)`
        // against `(a, b=1, c=2)` reached the base body and read `c` as a name nobody defined.
        // EVERY parameter, so a required one that no argument reaches is named here. It used to
        // be left unbound and the base body then read it, which answered "name 'b' is not
        // defined -- never assigned, imported, or received as a parameter" about a parameter,
        // one line under its own declaration.
        int lastIdx = firstVariadic - 1;

        var ordered = new List<Expression>();
        for (int i = 0; i <= lastIdx; i++)
        {
            if (i < positional.Count)
            {
                if (byName.ContainsKey(parameters[i].Name))
                    throw UserError(
                        $"multiple values for argument '{parameters[i].Name}' in call to '{spelling}'",
                        args.OfType<KeywordArgExpr>().First(k => k.Key == parameters[i].Name));
                ordered.Add(positional[i]);
            }
            else if (byName.TryGetValue(parameters[i].Name, out var v)) ordered.Add(v);
            else if (parameters[i].DefaultValue is { } dflt) ordered.Add(dflt);
            else
                throw UserError(
                    $"missing argument '{parameters[i].Name}' in call to '{spelling}'", fn);
        }

        // Positions past the declared parameters are the `*args` elements; keywords the base
        // does not declare are the `**kwargs` entries. Both keep their node shape so the
        // binding loop can tell them apart without a second list to keep in step.
        for (int i = firstVariadic; i < positional.Count; i++) ordered.Add(positional[i]);
        ordered.AddRange(leftoverKeywords);
        return ordered;
    }

    private Val EmitUnboundMethodBody(string basePrefix, FunctionDef funcSuper,
        string selfAliasKey, List<Expression> args, string spelling)
    {
        args = BindMethodArgs(funcSuper, args, spelling);

        // Too many positional arguments, refused here as an ordinary call already refuses them
        // (see 7b5097ff). The binding loop below stops at the end of the parameter list, so a base
        // silently dropped the extras: `super().__init__(offset, 99)` built clean and the 99
        // vanished, and so did the same mistake written `Base.__init__(self, offset, 99)`.
        // Phrasing borrowed from the check on the ordinary path so the two read alike.
        int superVarArgIdx = funcSuper.Params.FindIndex(p => p.IsVarArg);
        int superKwArgIdx = funcSuper.Params.FindIndex(p => p.IsKwArg);
        bool superIsVariadic = superVarArgIdx >= 0 || superKwArgIdx >= 0;

        int declaredArgs = funcSuper.Params.Count(p => !IsReceiverParamName(p.Name));
        if (!superIsVariadic && args.Count > declaredArgs)
        {
            string what = funcSuper.Name == "__init__"
                ? $"constructor of '{spelling}'"
                : $"'{spelling}'";
            throw UserError(
                $"too many arguments in call to {what}: it expects {declaredArgs} " +
                $"argument(s), but {args.Count} were provided",
                args[declaredArgs]);
        }

        var exitLabel = MakeLabel();
        var newDepth = inlineDepth + 1;
        var newPrefix = $"inline{newDepth}_{InlineFrameScope("_")}{funcSuper.Name}_";

        // Every expansion of this base method at this depth reuses the prefix: drop the
        // compile-time bindings the previous one left under it (see EmitDunderCall).
        CleanCtState(newPrefix[..^1]);

        var selfAlias = selfAliasKey;
        if (variableAliases.TryGetValue(selfAlias, out var vAlias))
            variableAliases[newPrefix + "self"] = vAlias;
        // Only a CONSTRUCTOR may fall back to the pending target: during construction that
        // target IS the instance being built. For any other method it is the target of the
        // ASSIGNMENT the call feeds, a different object entirely -- `p = super().split(raw)`
        // aliased self to `p`, so the base body read `p.offset` instead of the receiver's.
        else if (funcSuper.Name == "__init__" && !string.IsNullOrEmpty(pendingConstructorTarget))
            variableAliases[newPrefix + "self"] = pendingConstructorTarget;
        // The unbound spelling can name the receiver directly (`Base.read(probe, x)`), in
        // which case there is no alias to follow: the key IS the instance.
        else if (instanceClasses.ContainsKey(selfAlias))
            variableAliases[newPrefix + "self"] = selfAlias;
        // Propagate the concrete instance type so the base body's self.<field> resolves.
        if (instanceClasses.TryGetValue(selfAlias, out var selfClsSuper) && selfClsSuper != null)
            instanceClasses[newPrefix + "self"] = selfClsSuper;

        // Inside an OUTLINED method there is no `self` to alias: the instance arrives as one
        // parameter per field (self_a, self_b, ...). The base body still writes self.<field>,
        // which resolves to <newPrefix>self_<field> -- a name nobody writes, so every
        // inherited field read as ZERO and the override computed from 0 without a word.
        // Point each of those at this method's own parameter.
        // The base body reads those names literally, so an alias is not enough: copy the
        // value across.
        if (functionParams.TryGetValue(currentFunction, out var ownParams))
            foreach (var ownParam in ownParams)
            {
                string bare = ownParam.Contains('.') ? ownParam[(ownParam.LastIndexOf('.') + 1)..] : ownParam;
                if (!bare.StartsWith("self_", StringComparison.Ordinal)) continue;

                string mine = currentFunction + "." + bare;
                DataType fieldType = variableTypes.TryGetValue(mine, out var ft) ? ft : DataType.UINT8;
                string theirs = newPrefix + bare;
                variableTypes[theirs] = fieldType;
                Emit(new Copy(new Variable(mine, fieldType), new Variable(theirs, fieldType)));
            }

        var paramIdx = 0;
        foreach (var p in funcSuper.Params)
        {
            // Skipping only the exact name `self` bound the first ARGUMENT into the receiver's
            // first flattened field: `super().split(raw)` emitted `copy raw -> ..._self_offset`
            // right over the copy that had just put the real offset there, and the callee's own
            // `raw` parameter was then never bound at all. The body computed raw + raw.
            if (IsReceiverParamName(p.Name)) continue;
            // Bound below, from whatever the declared parameters did not take.
            if (p.IsVarArg || p.IsKwArg) continue;
            if (paramIdx >= args.Count) continue;
            var argVal = EvalOptionalCarry(args[paramIdx]);
            var paramKey = newPrefix + p.Name;
            constantVariables.Remove(paramKey);
            strConstantVariables.Remove(paramKey);
            floatConstantVariables.Remove(paramKey);
            variableAliases.Remove(paramKey);
            // A None argument (literal, or a caller name already tracked as None) has
            // no runtime value. The ordinary @inline binder records that on the
            // parameter; this unbound/super path used to Copy the NoneVal into a
            // Variable and leave noneValuedNames empty. The base body then stored
            // `self.reset_pin = reset` as a live DigitalInOut, `if self.reset_pin:`
            // did not fold, and Pin.low() ran on a port that was never a register
            // (adafruit_ssd1306's Optional reset=None).
            if (argVal is NoneVal || SourceIsNoneInThisScope(args[paramIdx]))
            {
                noneValuedNames.Add(paramKey);
                paramIdx++;
                continue;
            }
            noneValuedNames.Remove(paramKey);
            // A local that HOLDS a compile-time constant binds the same way
            // the ordinary @inline path does (PyMCU#327). Without this,
            // super().__init__(buf, w, h, _FRAMEBUF_FORMAT) forwarded a
            // Variable, `if buf_format == MVLSB` was a run-time compare,
            // every format class was constructed, and self.format.fill
            // expanded the last elif (GS2HMSBFormat.fill / ssd1306).
            if (argVal is Variable arrArg && TryArraySource(arrArg.Name, out var arrSrc))
            {
                // A memoryview window (or any fixed array) is storage, not a
                // scalar a Copy can carry. Alias the param so self.buf = buf
                // and len(framebuf.buf) see the same bytes
                // (ssd1306's I2C -> _SSD1306 -> FrameBuffer hop).
                BindArrayAlias(paramKey, arrSrc);
            }
            else if (argVal is Constant cArg)
            {
                constantVariables[paramKey] = cArg.Value;
                // A compile-time string reaches this path as a Constant too -- the
                // interned id carrying its Text. Binding only the number dropped the
                // text the ordinary binder keeps (`super().__init__(byteorder=
                // pixel_order)` in neopixel.py): `len(byteorder)` inside the base
                // body then refused a string the compiler was holding.
                if (p.Type is "str" or "const[str]" or "")
                {
                    string? text = args[paramIdx] is StringLiteral argLit ? argLit.Value
                        : cArg.Text;
                    if (text == null && p.Type != ""
                        && stringIdToStr.TryGetValue(cArg.Value, out var interned))
                        text = interned;
                    if (text != null) strConstantVariables[paramKey] = text;
                }
            }
            else if (argVal is FloatConstant fArg)
            {
                // A float through super().__init__ binds the way the ordinary @inline
                // binder and the property setter bind it: a compile-time float, or an
                // int when the parameter is declared an integer type. Falling to the
                // Copy arm materialized it into a run-time float slot, so `round()`,
                // `if x:` and friends could no longer fold it (HT16K33's brightness
                // default 1.0 reached the setter as a Temporary).
                if (p.Type is "uint8" or "uint16" or "uint32" or "int8" or "int16"
                    or "int32" or "int")
                    constantVariables[paramKey] = (int)fArg.Value;
                else
                    floatConstantVariables[paramKey] = fArg.Value;
            }
            else if (argVal is Variable vStrArg
                     && p.Type is "str" or "const[str]"
                     && ResolveStrConstant(vStrArg.Name) is string vStr)
            {
                // A name holding a compile-time string, arriving as a Variable:
                // the same binding the ordinary path makes for `str`/`const[str]`
                // params, so `byteorder=pixel_order` keeps "GRB" across the super()
                // hop too.
                strConstantVariables[paramKey] = vStr;
                if (p.Type != "const[str]" && TryArgumentConstant(vStrArg.Name, out int strId))
                    constantVariables[paramKey] = strId;
            }
            else if (!ParameterIsAssignedIn(funcSuper, p.Name)
                     && (argVal is Variable vArg && TryArgumentConstant(vArg.Name, out int argConst)
                         || TryFoldedConstant(argVal, out argConst)
                         || TryFoldArgumentExpression(args[paramIdx], p.Type,
                             currentInlinePrefix, out argConst)))
            {
                constantVariables[paramKey] = argConst;
            }
            else if (argVal is Variable instArg
                     && instanceClasses.TryGetValue(FollowAliases(instArg.Name), out var instArgCls)
                     && instArgCls != null)
            {
                // An INSTANCE argument is an object, not a value a Copy can carry:
                // `super().__init__(reset_dio, ...)` forwarded the pin's flattened scalar and
                // the base body's `pin.direction = ...` wrote a dead name. Alias the param to
                // the instance so field and method reads resolve through it
                // (adafruit_character_lcd's pin-setup loop).
                variableAliases[paramKey] = FollowAliases(instArg.Name);
                instanceClasses[paramKey] = instArgCls;
            }
            else if (argVal is Variable supFnArg
                     && FunctionNameBehind(supFnArg.Name) is { } supFnBound)
            {
                // A FUNCTION argument is a compile-time binding too: `super().__init__(pin,
                // **kwargs)` on adafruit_debouncer's Button forwards `pin`, which the
                // Callable member of the union lets be a predicate. Materializing it as a
                // scalar would copy a function's name as a value; the base body's
                // `self.f = io_or_predicate` needs the function behind the name.
                loopFunctionAliases[paramKey] = supFnBound;
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
                CarryOptionalTagToParam(paramKey, argVal);
            }

            paramIdx++;
        }

        if (superIsVariadic)
        {
            // Still in the CALLER's frame here, which is where these expressions were written,
            // so they are evaluated and pinned before the prefix switches to the base's.
            var superExtraPositional = new List<Expression>();
            var superExtraKeywords = new List<(string Key, Expression Value)>();
            for (int i = paramIdx; i < args.Count; i++)
            {
                if (args[i] is KeywordArgExpr leftover)
                    superExtraKeywords.Add((leftover.Key,
                        CarriedArgExpr(leftover.Value, VisitExpression(leftover.Value))));
                else
                    superExtraPositional.Add(CarriedArgExpr(args[i], VisitExpression(args[i])));
            }
            BindVariadicParams(funcSuper, superVarArgIdx, superKwArgIdx,
                               superExtraPositional, superExtraKeywords, newPrefix);
        }

        // A value-returning super method needs a result temp; the base body's `return`
        // copies into it via the inline frame's ResultTemp. Without this the value was lost.
        Temporary? superResult = null;
        if (funcSuper.ReturnType != "void" && funcSuper.ReturnType != "None")
            superResult = MakeTemp(DataTypeExtensions.StringToDataType(funcSuper.ReturnType));
        // `super().m()` declared `-> Cls`: the result temp is the produced instance's
        // carrier the same way a direct call's is.
        StampProducedClass(superResult, funcSuper.ReturnType);

        var savedPrefix = currentInlinePrefix;
        var savedMod = currentModulePrefix;
        var savedDepth = inlineDepth;
        var savedSourcePath = currentSourcePath;
        var savedSourceFile = currentSourceFile;
        bool savedTracksCallee = inlineTracksCalleeLine;
        int savedCalleeLine = inlineCalleeStmtLine;

        currentInlinePrefix = newPrefix;
        currentModulePrefix = basePrefix;
        inlineDepth = newDepth;
        var superCtx = new InlineContext { ExitLabel = exitLabel, ResultTemp = superResult,
            EntryBranchDepth = _runtimeBranchDepth, CallerSourcePath = currentSourcePath,
            CtorTarget = pendingConstructorTarget,
            FinallyDepth = finallyStack.Count };
        inlineStack.Add(superCtx);

        // Same optional-capable scan the plain inline path runs: the base method's
        // `x = None` locals are runtime-optional names this expansion must tag.
        CollectExpansionOptionalCapable(funcSuper.Body.Statements);

        // The base method's body is text in the file that method is DEFINED in, which is not
        // the file the `super().m()` / `Base.m(self)` call is written in. Without the switch
        // the body's own nodes kept their lines while LocatedFile kept naming the caller's
        // module, and the pair met in the middle of nowhere: `bytearray(17 * len(dev))` in
        // adafruit_ht16k33/ht16k33.py reported segments.py:60, a line of a font table.
        // The same four assignments EmitInlineFunctionCall makes, for the same reason.
        string? baseSourcePath =
            functionSourcePath.TryGetValue(funcSuper, out var basePath) ? basePath : null;
        if (baseSourcePath != null)
        {
            currentSourcePath = baseSourcePath;
            currentSourceFile = baseSourcePath.Length > 0 ? SourceFileLabel(baseSourcePath) : "";
            inlineTracksCalleeLine = true;
            inlineCalleeStmtLine = 0;
        }
        else
        {
            inlineTracksCalleeLine = false;
        }

        int savedLastLine = lastLine;
        lastLine = -1;
        bool savedSeqTerminated = _seqTerminated;
        _seqTerminated = false;
        VisitBlock(funcSuper.Body);
        RestoreSeqTerminatedAfterExpansion(savedSeqTerminated, exitLabel);
        lastLine = savedLastLine;
        Emit(new Label(exitLabel));
        inlineStack.RemoveAt(inlineStack.Count - 1);
        if (pendingConstructorTarget == superCtx.CtorTarget)
            pendingConstructorTarget = "";

        currentSourcePath = savedSourcePath;
        currentSourceFile = savedSourceFile;
        inlineTracksCalleeLine = savedTracksCallee;
        inlineCalleeStmtLine = savedCalleeLine;
        currentInlinePrefix = savedPrefix;
        currentModulePrefix = savedMod;
        inlineDepth = savedDepth;
        EmitRaiseLandings(superCtx);

        // The base method is UNANNOTATED as often as not (`def describe(self): return
        // self.value`), which the parser reads as returning "void" -- so `superResult` above
        // stayed null and this returned NoneVal outright, no matter what the body actually
        // returned. VisitReturn already covers exactly this for an ordinary @inline call (see
        // its own comment: "the first value return decides the width") by lazily creating
        // ResultTemp on the context the FIRST time it sees a real value -- but that lazy
        // creation mutates `superCtx`, which this function never looked at again, holding on
        // to its own now-stale `null` copy instead. `super().describe() + self.extra` read
        // None for the base call on every instance, constant-argument or not: `describe()`
        // has no annotation regardless of what built the receiver (#430).
        return superCtx.ResultTagTemp == null && superCtx.ResultIsNone ? new NoneVal()
            : superCtx.ResultTagTemp == null && superCtx.ResultIsLiveCall
                ? new NoneVal(LiveCallResult: true)
            : superCtx.ResultTemp ?? (Val)new NoneVal();
    }

    // RFC 0001 Model B (Class[N]): `arr[i].method(args)` — compute the element address
    // (base + i*stride) and call the shared slot method with it as the self pointer.
    // Returns the call result when handled; null when the receiver is not an instance
    // array (fall through to normal member-call resolution).
    /// <summary>
    /// `pins[i].high()` where `pins` is a list of ZCA instances and `i` varies at run time.
    /// The elements are separate compile-time instances (there is no array to index), so the
    /// call is lowered as a selection over the constant indices: the LED chaser, the keypad
    /// scan and the stepper sequence all have this shape, and `for p in pins` only covers
    /// "do the same to all of them", not "act on the i-th".
    /// </summary>
    /// <summary>
    /// Replaces every `*seq` argument with the elements of the sequence it names, and every
    /// `**map` argument with the entries of the mapping it names. There is no run-time
    /// argument list and no run-time keyword dictionary on this target, so both have to be
    /// known now: written at the call, or a name bound to one the compiler can see.
    ///
    /// This is the whole of the forwarding pattern. `super().__init__(pin, **kwargs)` inside a
    /// callee whose own `**kwargs` parameter is bound to a mapping expands here into the
    /// keyword arguments the base declares, which is the call the program would have written
    /// by hand -- and therefore compiles to the same firmware (#368).
    /// </summary>
    private List<Expression> SpliceVariadicArgs(List<Expression> args)
    {
        var spliced = new List<Expression>();
        foreach (var a in args)
        {
            if (a is DoubleStarArgExpr dstar)
            {
                var entries = ResolveKwargMapping(dstar.Value);
                if (entries == null)
                    throw UserError(
                        "f(**kwargs) needs a mapping the compiler can see: a dict literal "
                        + "written at the call, a name bound to one, or a '**' parameter of "
                        + "the enclosing function. There is no run-time keyword dictionary on "
                        + "this target, so the entries are spliced in at compile time.",
                        dstar.Value);

                foreach (var (key, value) in entries)
                    spliced.Add(new KeywordArgExpr(key, value) { Line = dstar.Line });
                continue;
            }

            if (a is not StarArgExpr star) { spliced.Add(a); continue; }

            List<Expression>? elements = star.Value switch
            {
                ListExpr le => le.Elements,
                TupleExpr te => te.Elements,
                VariableExpr ve => ResolveConstSequence(ve.Name)
                    ?? (List<Expression>?)ResolveListLiteralParam(ve.Name)?.Elements
                    ?? NamedTupleElemsOf(ve.Name),
                _ => null,
            };
            // A field bound to a constant sequence (`*_GAINS`) resolves without emitting;
            // a member read or call that returns a tuple (`*registers.tuple_of_numbers`,
            // where Struct.__get__ is `return struct.unpack_from(...)`) delivers its
            // elements through the result slots -- evaluated under the sentinel, and the
            // slots read back as pre-evaluated operands. Both run AFTER the pure cases so
            // a literal or a name never pays for a speculative evaluation.
            elements ??= ResolveConstSequenceExpr(star.Value) ?? TupleResultElementsOf(star.Value);

            if (elements == null)
                throw UserError(
                    "f(*args) needs a sequence the compiler can see: a list or tuple written "
                    + "at the call, or a name bound to a short constant one. There is no "
                    + "run-time argument list on this target, so the elements are spliced in "
                    + "at compile time.",
                    star.Value);

            spliced.AddRange(elements);
        }

        return spliced;
    }

    /// <summary>
    /// The elements of a `*expr` whose operand yields a tuple RETURN -- `*f()` or
    /// `*obj.prop`, where the callee's `return` filled the expansion's result slots.
    /// The sentinel requests them, exactly as `f()[k]` does; each slot is wrapped
    /// pre-evaluated so a later re-visit does not emit the load twice. Null when the
    /// expression produced no tuple, leaving the splice's diagnostic to name the shape.
    /// </summary>
    private List<Expression>? TupleResultElementsOf(Expression e)
    {
        lastTupleResults.Clear();
        lastTupleResultBuffers = null;
        lastTupleResultLocalBuffers = null;
        pendingTupleCount = -1;
        VisitExpression(e);
        pendingTupleCount = 0;
        if (lastTupleResults.Count == 0) return null;

        var elems = new List<Expression>(lastTupleResults.Count);
        for (int k = 0; k < lastTupleResults.Count; ++k)
        {
            // A buffer element answers its storage name -- the slot holds no scalar,
            // and the spliced argument must still resolve to array storage.
            string s = lastTupleResultBuffers is { } splBufs
                && splBufs.TryGetValue(k, out var splBuf) ? FollowAliases(splBuf)
                : lastTupleResults[k];
            elems.Add(new PreEvaluatedExpr(
                new Variable(s, variableTypes.TryGetValue(s, out var sdt) ? sdt : DataType.UINT8),
                null));
        }
        // The splice consumed the expansion's result list; a scalar-producing
        // expression wrapped around it must not read them back as its own.
        lastTupleResults.Clear();
        lastTupleResultBuffers = null;
        lastTupleResultLocalBuffers = null;
        return elems;
    }

    /// The expression that stands for an argument once it has been carried into a `*args` or a
    /// `**kwargs` binding.
    ///
    /// The argument was written and evaluated in the CALLER's frame, and the sequence or
    /// mapping it lands in is read from inside the callee, under a different prefix. A literal
    /// means the same thing in both, and keeping it as written also keeps its source position
    /// for any diagnostic raised against it. Anything else is pinned to the value already
    /// computed, under a globally unique name that the bare-name fallback in ResolveNameKey
    /// finds from any prefix. Without the pin the same spelling would resolve to a different
    /// variable on the two sides of the hop, and do it in silence (#368).
    private Expression CarriedArgExpr(Expression? source, Val value)
    {
        if (source is IntegerLiteral or FloatLiteral or BooleanLiteral or StringLiteral or NoneLiteral)
            return source;
        if (value is Constant { Text: null } c) return new IntegerLiteral(c.Value);

        // An instance is carried by NAME, not by value: its fields live under the
        // element's qualified base (`ta__deadline_ms`) and a Copy of the head moves
        // none of them. Aliasing the pin to the name the class hangs on makes a
        // `*args` element dispatch field reads exactly like a declared parameter.
        // The global registrations still happen -- without them a read inside a
        // deeper expansion prefixes the bare name and finds nothing at all.
        if (ResolveClassCarryingName(value) is { } carriedName)
        {
            string pinnedInst = "__variadic" + (tempCounter++);
            DataType instType = value switch
            {
                Variable v => v.Type,
                Temporary tv => tv.Type,
                _ => DataType.UINT8,
            };
            variableTypes[pinnedInst] = instType;
            mutableGlobals[pinnedInst] = instType;
            variableAliases[pinnedInst] = carriedName;
            instanceClasses[pinnedInst] = instanceClasses[carriedName];
            return new VariableExpr(pinnedInst);
        }

        string pinned = "__variadic" + (tempCounter++);
        DataType type = value switch
        {
            Variable v => v.Type,
            Temporary tv => tv.Type,
            _ => DataType.UINT8,
        };
        // MODULE-LEVEL, and that is the whole point of the pin. A local is spelled
        // `<prefix><name>` inside an expansion, so writing the bare name here and reading it
        // one hop down wrote `__variadicN` and read `inline1.__init__.__variadicN`: two
        // different variables, the second never written, and `interval_ms=pick(n)` arrived as
        // 0 while `interval_ms=n` arrived correctly. A global has one spelling from every
        // prefix, which is what a value carried ACROSS a binding needs.
        variableTypes[pinned] = type;
        mutableGlobals[pinned] = type;
        Emit(new Copy(value, new Variable(pinned, type)));
        return new VariableExpr(pinned);
    }

    /// The elements of a `*args` and the entries of a `**kwargs`, bound to their parameters.
    ///
    /// Both are cleared first, unconditionally. The expansion prefix is keyed by DEPTH and not
    /// by call site, so two calls to the same function at the same depth reuse the same names:
    /// a binding left by the previous call would be inherited by a call that passed nothing,
    /// which is how #194 and #324 each became a silent wrong answer.
    private void BindVariadicParams(FunctionDef func, int varArgIdx, int kwArgIdx,
                                    List<Expression> extraPositional,
                                    List<(string Key, Expression Value)> extraKeywords,
                                    string? prefix = null)
    {
        prefix ??= currentInlinePrefix;
        if (varArgIdx >= 0)
        {
            string name = prefix + func.Params[varArgIdx].Name;
            constSequenceBindings.Remove(name);
            listLiteralParams.Remove(name);
            listLiteralParamScopes.Remove(name);
            constSequenceBindings[name] = extraPositional;
        }

        if (kwArgIdx >= 0)
        {
            string name = prefix + func.Params[kwArgIdx].Name;
            dictLiteralBindings.Remove(name);
            dictLiteralBindings[name] = new Frontend.DictExpr(
                extraKeywords.Select(e => ((Expression)new StringLiteral(e.Key), e.Value)).ToList());
        }
    }

    /// The key/value pairs behind a `**` argument, or null when the compiler cannot see them.
    ///
    /// The keys have to be names, because they become keyword arguments: a key that is not a
    /// string literal is refused by name rather than dropped, since dropping it would be a
    /// keyword argument that silently never arrives.
    private List<(string Key, Expression Value)>? ResolveKwargMapping(Expression e)
    {
        Frontend.DictExpr? dict = e switch
        {
            Frontend.DictExpr literal => literal,
            VariableExpr ve when TryGetDictBinding(ve.Name, out var bound) => bound,
            _ => null,
        };
        if (dict == null && e is MemberAccessExpr && TryGetDictFor(e, out var fieldDict)) dict = fieldDict;
        if (dict == null) return null;

        var entries = new List<(string, Expression)>();
        foreach (var (key, value) in dict.Entries)
        {
            if (key is not StringLiteral sk)
                throw UserError(
                    "a '**' argument needs string-literal keys: each one becomes a keyword "
                    + "argument, and this key is not a name the compiler can read.",
                    key);
            entries.Add((sk.Value, value));
        }
        return entries;
    }

    /// <summary>
    /// A call of the generator protocol on a generator instance. Each one is refused by its own
    /// name and its own reason: `send` because there is nowhere for the value to arrive,
    /// `throw`/`close` because there is no generator object to act on at run time, `__next__`
    /// because manual iteration would need somewhere to put "exhausted".
    /// </summary>
    private void RejectGeneratorProtocol(string generatorName, MemberAccessExpr at)
    {
        string member = at.Member;
        string? why = member switch
        {
            "send" =>
                "a sent value arrives at `x = yield v`, and `yield` is a statement here, not an "
                + "expression, so there is nowhere for it to land",
            "throw" =>
                "raising into a suspended generator needs a generator object at run time, and a "
                + "generator lowers to a state machine with no such object",
            "close" =>
                "the state machine is a plain value with no heap allocation and no finalizer, so "
                + "there is nothing to release",
            // The twin of the next() message above, and it carried the same false reason.
            // PyMCU HAS exceptions -- raise/except across functions compiles on AVR, RP2040 and
            // CH32V003, and a raise inside a generator reaches an enclosing handler. What is
            // missing is that StopIteration is not one of the six names in
            // BuiltinExceptionNames and the protocol is not implemented on the state machine.
            // Found by grepping the message text of the one above, which is the only way twins
            // like this turn up.
            "__next__" or "next" =>
                "the state machine is driven as a whole by `for`, and pulling one value at a "
                + "time is not implemented on it",
            _ => null,
        };
        if (why == null) return;

        throw UserError(
            $"'{generatorName}.{member}()' is the generator protocol, which PyMCU does not "
            + $"provide: {why}. A generator is consumed with `for v in {generatorName}(...):`, "
            + "which drives it to exhaustion and ends when it does.", at);
    }

    /// <summary>
    /// The generator function's name when <paramref name="arg"/> names an instance of a class
    /// the generator lowering synthesized, else null. Tries the same key shapes instance
    /// lookups use elsewhere, because a local is stored qualified by its function.
    /// </summary>
    private string? ResolveGeneratorArg(Expression arg)
    {
        if (arg is not VariableExpr v) return null;
        foreach (var key in new[]
                 {
                     currentInlinePrefix + v.Name,
                     string.IsNullOrEmpty(currentFunction) ? v.Name : currentFunction + "." + v.Name,
                     currentModulePrefix + v.Name,
                     v.Name,
                 })
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (instanceClasses.TryGetValue(key, out var cls) && cls != null
                && generatorClasses.Contains(cls))
                return cls;
        }
        return null;
    }

    private Val? TryEmitUnrolledInstanceArrayCall(CallExpr expr, MemberAccessExpr memC)
    {
        if (memC.Object is not IndexExpr idxExpr) return null;

        // The sequence may be reached by name (`pins[i].on()`) or through a FIELD
        // (`self._pins[i].on()`, which is how a driver that was handed its pins writes it).
        // Both denote the same compile-time elements, so both get the same selection.
        string q;
        string sourceName;
        if (idxExpr.Target is VariableExpr arrVe)
        {
            sourceName = arrVe.Name;
            q = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + arrVe.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + arrVe.Name : arrVe.Name);
            if (!instanceClasses.ContainsKey(q + "__0") && instanceClasses.ContainsKey(arrVe.Name + "__0"))
                q = arrVe.Name;
        }
        else if (idxExpr.Target is MemberAccessExpr fieldMem
                 && TryResolveInstanceSequence(fieldMem, out var fieldBase, out _))
        {
            sourceName = FormatMemberTarget(fieldMem);
            q = fieldBase;
        }
        else return null;

        if (!instanceClasses.ContainsKey(q + "__0")) return null;

        // A constant index is already handled by the normal path.
        if (idxExpr.Index is IntegerLiteral) return null;
        Val probe = VisitExpression(idxExpr.Index);
        if (probe is Constant) return null;

        int count = 0;
        while (instanceClasses.ContainsKey(q + "__" + count)) count++;

        // The selection costs one comparison and one expansion per element, so it is the right
        // shape for the handful of pins this pattern is about and the wrong one for a big table.
        const int maxUnrolled = 8;
        if (count > maxUnrolled)
            throw UserError(
                $"'{sourceName}[i].{memC.Member}()' selects among {count} instances at run time, "
                + $"which is lowered as {count} branches -- past {maxUnrolled} that is more code "
                + "than it is worth. Iterate with `for p in " + sourceName + ":`, or split the "
                + "array.", expr.Callee);

        string methodName = memC.Member;
        string endLabel = MakeLabel();

        // The result slot has to exist before the branches so every arm writes the same place.
        // The method may be overloaded (Pin.value() reads, Pin.value(x) writes), in which case
        // the bare key is vacated and only the suffixed ones exist. Pick by arity, and treat
        // "no match" as void: guessing a width here would truncate whatever comes back.
        string firstClass = instanceClasses[q + "__0"] ?? "";
        string firstMethod = string.IsNullOrEmpty(firstClass) ? "" : firstClass + "_" + methodName;
        // Overloads share the bare key, and it holds whichever definition registered LAST
        // (Pin.value's writing overload, declared after the reading one), so the ASTs are the
        // reliable source: pick the definition whose parameter count matches this call.
        string? rtName = null;
        if (firstMethod.Length > 0)
        {
            foreach (var kv in inlineFunctions)
            {
                if (kv.Key != firstMethod && !kv.Key.StartsWith(firstMethod + "___", StringComparison.Ordinal))
                    continue;
                var def = kv.Value;
                if (def == null) continue;
                if (def.Params.Count(pp => pp.Name != "self") != expr.Args.Count) continue;
                rtName = def.ReturnType;
                if (rtName is not (null or "" or "void" or "None")) break;
            }

            rtName ??= functionReturnTypes.GetValueOrDefault(firstMethod);
        }

        bool hasValue = rtName is not (null or "" or "void" or "None");
        Temporary? result = hasValue
            ? MakeTemp(DataTypeExtensions.StringToDataType(rtName!))
            : null;
        // The per-element dispatch picks a method at run time; when the resolved
        // return names a class the merged temp is that instance's carrier.
        StampProducedClass(result, rtName);

        for (int k = 0; k < count; k++)
        {
            string nextLabel = MakeLabel();
            Emit(new JumpIfNotEqual(probe, new Constant(k), nextLabel));

            var armCall = new CallExpr(
                new MemberAccessExpr(new IndexExpr(idxExpr.Target, new IntegerLiteral(k)), methodName),
                expr.Args) { Line = expr.Line };
            Val armVal = VisitCall(armCall);
            if (result != null && armVal is not NoneVal) Emit(new Copy(armVal, result));

            Emit(new Jump(endLabel));
            Emit(new Label(nextLabel));
        }

        Emit(new Label(endLabel));
        return result is not null ? result : new NoneVal();
    }

    private Val? TryEmitInstanceArrayMethodCall(CallExpr expr, MemberAccessExpr memC)
    {
        if (memC.Object is not IndexExpr { Target: VariableExpr iaArr } iaIdx) return null;

        string iaQ = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + iaArr.Name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + iaArr.Name : iaArr.Name);
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
        var iaArgExprs = new List<Expression?> { null };   // elemAddr binds the self slot
        int iaSelf = functionParamSelfCount.GetValueOrDefault(iaMethod);
        int iaPos = 0;
        foreach (var a in expr.Args)
        {
            int iaPidx = a is KeywordArgExpr iaKw && functionParams.TryGetValue(iaMethod, out var iaPn)
                ? iaPn.IndexOf(iaKw.Key) : iaSelf + iaPos++;
            bool iaTagged = IsTaggedParam(iaMethod, iaPidx);
            if (iaTagged) optionalReadAllowed++;
            Val iaAv;
            try
            {
                iaAv = TryEvalInlineBufferArg(a) ?? TryEvalLiteralBufferArg(a) ?? VisitExpression(a);
            }
            finally { if (iaTagged) optionalReadAllowed--; }
            if (!iaTagged) RefuseOptionalPayloadStore(iaAv, a);
            iaArgs.Add(CoerceToParam(iaMethod, iaPidx, iaAv));
            iaArgExprs.Add(a is KeywordArgExpr iaKw2 ? iaKw2.Value : a);
        }

        iaArgs = WithParamTags(iaMethod, iaArgs, iaArgExprs);

        bool iaVoid = !functionReturnTypes.TryGetValue(iaMethod, out var iaRt)
                      || iaRt == "void" || iaRt == "None";
        if (iaVoid)
        {
            Emit(new Call(iaMethod, iaArgs, new NoneVal()));
            return VoidCallResult(iaMethod);
        }
        Temporary iaDst = MakeTemp(
            functionReturnMembers.TryGetValue(iaMethod, out var iaMembers)
                ? UnionPayloadType(iaMembers)
                : DataTypeExtensions.StringToDataType(functionReturnTypes[iaMethod]));
        EmitMaybeTaggedCall(iaMethod, iaArgs, iaDst);
        // `xs[i].m()` dispatched to a callee declared `-> Cls`: iaDst is the produced
        // instance's carrier, tagged like every other call result.
        StampProducedClass(iaDst, iaRt);
        return iaDst;
    }

    // `bytearray(N)` / `bytearray([...])` / `bytes(...)` written INLINE as a call argument
    // (#380 for plain calls, #459 for the method-call paths): the recognition that lays out
    // a fixed buffer lives in VisitVarDecl, so give the argument the same hidden binding a
    // named local gets and pass its address exactly as a named buffer argument already is.
    // Returns the buffer's base address when the arg is one of those calls; null otherwise.
    private Val? TryEvalInlineBufferArg(Expression arg)
    {
        if (arg is not CallExpr { Callee: VariableExpr { Name: "bytearray" or "bytes" } baName } baCall)
            return null;
        string hiddenName = $"__inline_{baName.Name}_arg{tempCounter++}";
        VisitVarDecl(new VarDecl(hiddenName, baName.Name, baCall) { Line = baCall.Line });
        string hiddenQualified = (!string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix
            : currentFunction + ".") + hiddenName;
        if (!arraySizes.ContainsKey(hiddenQualified))
        {
            string altHQ = currentModulePrefix + hiddenName;
            if (arraySizes.ContainsKey(altHQ)) hiddenQualified = altHQ;
            else if (arraySizes.ContainsKey(hiddenName)) hiddenQualified = hiddenName;
        }
        return new ArrayBase(hiddenQualified);
    }

    // `f(b"ab")` / `o.m(b"")`: a bytes literal (which parses to a ListExpr) written
    // INLINE as a call argument to a real subroutine -- the same hole #380 closed for
    // `bytearray(...)`, one spelling earlier. A literal bound by name keeps its
    // compile-time sequence, but a subroutine needs an addressable buffer, so give the
    // argument the same hidden `bytes(...)` binding a named local gets and pass its
    // base. Inline callees bind the literal's elements by name and never reach this
    // path, so their `enumerate(param)` unrolling is untouched.
    private Val? TryEvalLiteralBufferArg(Expression arg)
    {
        if (arg is not ListExpr lit) return null;
        string hiddenName = $"__inline_bytes_arg{tempCounter++}";
        if (lit.Elements.Count == 0)
        {
            // b"": no elements means no storage through the VarDecl path, but the
            // callee still needs a base address it never dereferences -- one byte of
            // placeholder storage so the label exists.
            string emptyQ = (!string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix
                : currentFunction + ".") + hiddenName;
            arrayElemTypes[emptyQ] = DataType.UINT8;
            variableTypes[emptyQ] = DataType.UINT8;
            arraySizes[emptyQ] = 0;
            bufferLogicalLen[emptyQ] = 0;
            Emit(new ArrayStore(emptyQ, new Constant(0), new Constant(0), DataType.UINT8, 1));
            return new ArrayBase(emptyQ);
        }
        VisitVarDecl(new VarDecl(hiddenName, "bytes",
            new CallExpr(new VariableExpr("bytes"), new List<Expression> { lit })
                { Line = lit.Line })
            { Line = lit.Line });
        string hiddenQualified = (!string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix
            : currentFunction + ".") + hiddenName;
        if (!arraySizes.ContainsKey(hiddenQualified))
        {
            string altHQ = currentModulePrefix + hiddenName;
            if (arraySizes.ContainsKey(altHQ)) hiddenQualified = altHQ;
            else if (arraySizes.ContainsKey(hiddenName)) hiddenQualified = hiddenName;
        }
        return new ArrayBase(hiddenQualified);
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
        int fwdSelf = 0;
        if (slotMethods.Contains(currentFunction))
        {
            fwdArgs.Add(new Variable(currentFunction + ".self", DataType.UINT16));
            fwdSelf = 1;
        }
        else
            foreach (var (fld, ty, _) in outlineFieldLayout[currentFunction])
            {
                fwdArgs.Add(new Variable(currentFunction + ".self_" + fld,
                    DataTypeExtensions.StringToDataType(ty)));
                fwdSelf++;
            }
        // RFC 0009: the sibling's parameter list leads with its own self-derived
        // entries (a target inherited from a narrower base takes fewer than this
        // method forwards); the union-parameter scan lines user args up by the
        // TARGET's self count.
        int fwdSelfParams = functionParamSelfCount.GetValueOrDefault(target);
        var fwdArgExprs = new List<Expression?>();
        for (int si = 0; si < fwdSelfParams; ++si) fwdArgExprs.Add(null);
        int fwdPos = 0;
        foreach (var a in expr.Args)
        {
            int fwdPidx = a is KeywordArgExpr fKw && functionParams.TryGetValue(target, out var fPn)
                ? fPn.IndexOf(fKw.Key) : fwdSelfParams + fwdPos++;
            bool fwdTagged = IsTaggedParam(target, fwdPidx);
            if (fwdTagged) optionalReadAllowed++;
            Val fAv;
            try
            {
                fAv = TryEvalInlineBufferArg(a) ?? TryEvalLiteralBufferArg(a) ?? VisitExpression(a);
            }
            finally { if (fwdTagged) optionalReadAllowed--; }
            if (!fwdTagged) RefuseOptionalPayloadStore(fAv, a);
            if (functionParams.TryGetValue(target, out var fwdPnames) && fwdPidx >= 0
                && fwdPidx < fwdPnames.Count
                && functionParamTypes.TryGetValue(target, out var fwdPtypes) && fwdPidx < fwdPtypes.Count)
                NoteArgumentStore(target + "." + fwdPnames[fwdPidx], fwdPtypes[fwdPidx], fAv);
            fwdArgs.Add(CoerceToParam(target, fwdPidx, fAv));
            fwdArgExprs.Add(a is KeywordArgExpr fKw2 ? fKw2.Value : a);
        }
        fwdArgs = WithParamTags(target, fwdArgs, fwdArgExprs);

        // RFC 0001 (write-back), sibling case: the callee is a mutator that returns its
        // updated field because Model A passes the field BY VALUE. This method's own copy
        // lives in its field parameter, so the returned value has to land there -- otherwise
        // the sibling mutates a copy that dies with the call and the write is lost. (Model B
        // needs nothing: both share the slot the self pointer names.)
        if (!slotMethods.Contains(currentFunction)
            && outlineWriteBack.TryGetValue(target, out var swb))
        {
            Temporary swDst = MakeTemp(swb.Type);
            EmitMaybeTaggedCall(target, fwdArgs, swDst);
            Emit(new Copy(swDst, new Variable(currentFunction + ".self_" + swb.Field, swb.Type)));
            // A union write-back field's tag lands with its payload: update the same
            // self_<field>$tag sibling the body's own tagged parameter binds.
            if (TagOfVal(swDst) is { } swTag
                && optionalTagSlots.TryGetValue(currentFunction + ".self_" + swb.Field,
                                                out var swTagHome))
                Emit(new Copy(swTag, swTagHome));
            return new NoneVal(LiveCallResult: true);
        }

        bool tVoid = !functionReturnTypes.TryGetValue(target, out var tRt)
                     || tRt == "void" || tRt == "None";
        if (tVoid) { Emit(new Call(target, fwdArgs, new NoneVal())); return VoidCallResult(target); }
        Temporary tDst = MakeTemp(
            functionReturnMembers.TryGetValue(target, out var tMembers)
                ? UnionPayloadType(tMembers)
                : DataTypeExtensions.StringToDataType(functionReturnTypes[target]));
        EmitMaybeTaggedCall(target, fwdArgs, tDst);
        StampProducedClass(tDst, tRt);
        return tDst;
    }

    /// <summary>
    /// The value a call to a void-declared subroutine stands for. A callee that returns None
    /// on every path left nothing in the return register, so its result IS None -- the
    /// compile-time kind `is None`, print() and a binding all answer for. Any other void
    /// callee may still have left an undeclared value there, and the live flag says so.
    /// </summary>
    private Val VoidCallResult(string callee)
    {
        if (!noneReturningFunctions.Contains(callee)) return new NoneVal(LiveCallResult: true);
        return MarkNoneCallResult();
    }

    // The shared identity of "this call produced None": a binding tests it with
    // ReferenceEquals, because a constructor hands back a NoneVal too and
    // `x = Cls()` must not mark x None. The object returned here is the marker.
    private NoneVal MarkNoneCallResult()
    {
        lastNoneCallResult = new NoneVal();
        return lastNoneCallResult;
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
        else if (expr.Callee is MemberAccessExpr lamMemC
                 && FlattenedCallableFieldKey(lamMemC) is { } lamFieldKey)
        {
            // `self.f()` where `f` is a field bound to a callable at
            // construction. The lambda expands in place; a plain function name
            // rewrites to an ordinary call -- either way `self` is NOT passed,
            // because the stored callable takes no receiver.
            if (lambdaVariableNames.TryGetValue(lamFieldKey, out var lk3))
                lambdaKey = lk3;
            else if (loopFunctionAliases.TryGetValue(lamFieldKey, out var boundFieldFn))
                return VisitExpression(new CallExpr(
                    new VariableExpr(boundFieldFn), expr.Args) { Line = expr.Line });
            else if (boundMethodFields.TryGetValue(lamFieldKey, out var bmField))
            {
                // `self.f(args)` where `f` was bound to `obj.method`: re-visit
                // `<seed>.method(args)` on a fresh name aliased to the recorded
                // receiver's terminal instance key. The seed is filed under the
                // SAME key this scope would give the name, so the binding lands
                // exactly where the lookup looks -- the receiver AST the write
                // saw (`self._ow`, or a ctor param `p`) is not resolvable here.
                // The generic member-call path then resolves the method exactly
                // as if the source had spelled `obj.method(args)`, so an
                // outlined callee gets the outlined ABI and an inline one
                // expands in place.
                string bmSeed = "__bm_" + boundMethodCounter++;
                string bmSeedKey = !string.IsNullOrEmpty(currentInlinePrefix)
                    ? currentInlinePrefix + bmSeed
                    : currentFunction + "." + bmSeed;
                variableAliases[bmSeedKey] = bmField.Recv;
                return VisitExpression(new CallExpr(
                    new MemberAccessExpr(new VariableExpr(bmSeed), bmField.Member),
                    expr.Args) { Line = expr.Line });
            }
        }

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
        // Names the lambda captured from the scope it was WRITTEN in get re-seeded
        // under this expansion's prefix: `lambda: io_or_predicate.value` inside
        // __init__ read the constructor's parameter, and `self.f()` runs in
        // update()'s scope where that parameter does not exist.
        if (lambdaCaptures.TryGetValue(lambdaKey, out var captures))
        {
            foreach (var (freeName, cap) in captures)
            {
                var ck = pfx + freeName;
                variableAliases[ck] = cap.Alias;
                if (cap.Cls != null) instanceClasses[ck] = cap.Cls;
                if (cap.HasConst) constantVariables[ck] = cap.Const;
                if (cap.Str != null) strConstantVariables[ck] = cap.Str;
            }
        }
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
            Val indDst = new NoneVal(LiveCallResult: true);
            Emit(new IndirectCall(tmpFn, indArgs, indDst));
            return indDst;
        }
        throw UserError($"Callable array '{idxArr.Name}' not found or element type is not Callable", expr.Callee);
    }

    // ── Built-in functions (each handled when callee matches; always returns or throws) ──

    // len(x): compile-time constant for fixed-size arrays / list literals; runtime header
    // load for list[T]; __len__ dunder for ZCA instances.
    private Val EmitLenBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("len() expects exactly one argument", expr.Callee);

        // `len(e.args)` on the exception a handler bound: args is () when the raise
        // carried no argument and (message,) when it did -- 0 or 1, decided at run
        // time by the message word (and the deferred-print site id, when the program
        // has one). A raise's argument list never holds more than the one message.
        if (expr.Args[0] is MemberAccessExpr { Object: VariableExpr lenArgsObj, Member: "args" }
            && TryGetExceptionBinding(lenArgsObj.Name, out var lenArgsBinding))
        {
            string? lenSnap = lenArgsBinding.Snap;
            DeclareExceptionMessageVar();
            Temporary hasArg = MakeTemp(DataType.UINT8);
            Emit(new Binary(BinaryOp.NotEqual,
                new Variable(lenSnap != null ? ExnSnapVar(lenSnap, ExceptionMessageVar)
                                            : ExceptionMessageVar,
                             DataType.UINT16), new Constant(0), hasArg));
            if (programHasDynamicRaiseMessage)
            {
                DeclareExceptionSiteVar();
                Temporary hasSite = MakeTemp(DataType.UINT8);
                Emit(new Binary(BinaryOp.NotEqual,
                    new Variable(lenSnap != null ? ExnSnapVar(lenSnap, ExceptionSiteVar)
                                                : ExceptionSiteVar,
                                 DataType.UINT8), new Constant(0), hasSite));
                Temporary lenEither = MakeTemp(DataType.UINT8);
                Emit(new Binary(BinaryOp.BitOr, hasArg, hasSite, lenEither));
                hasArg = lenEither;
            }
            return hasArg;
        }
        if (expr.Args[0] is ListExpr le2) return new Constant(le2.Elements.Count);
        if (expr.Args[0] is TupleExpr te2) return new Constant(te2.Elements.Count);
        if (expr.Args[0] is Frontend.DictExpr de2) return new Constant(de2.Entries.Count);
        if (expr.Args[0] is Frontend.SetExpr se2) return new Constant(se2.Elements.Count);
        // A compile-time string constant (literal or a str / const[str] variable) has a
        // statically known length.
        if (expr.Args[0] is StringLiteral slLen) return new Constant(slLen.Value.Length);
        // So does any expression whose text the compiler holds -- `len(stnum[:dot])`,
        // `len(str(n) + "x")`: a slice or concatenation of constants is still a
        // compile-time string (adafruit_ht16k33's _number measures stnum this way).
        if (expr.Args[0] is not StringLiteral && StaticStringOf(expr.Args[0]) is { } lenText)
            return new Constant(lenText.Length);

        // `len(a.rom)` through a @property, `len(a.get())` through a method declared to return
        // a buffer: the getter hands back the buffer's NAME (a `return self._rom` expansion),
        // and its length is that array's. Asked only of the two declared-buffer shapes, so a
        // call returning a string or a tuple keeps the paths below.
        // The call is lowered here, once, so an answer this cannot give is the same refusal
        // the end of this method gives rather than a second evaluation further down.
        if (IsDeclaredBufferResult(expr.Args[0]))
        {
            if (VisitExpression(expr.Args[0]) is Variable { Name: var retBuf }
                && arraySizes.TryGetValue(retBuf, out int retBufSize))
                return new Constant(LogicalArrayLen(retBuf, retBufSize));
            throw UserError("len() argument must be a fixed-size array or list literal", ArgAt(expr, 0));
        }

        // 2-D grid lengths: len(g) is the row count H; len(g[y]) -- and len(r)
        // where r was bound to a row -- are the row width W. Both fold at
        // compile time; the row is never a value.
        if (expr.Args[0] is IndexExpr lenIx
            && lenIx.Index is not SliceExpr and not TupleExpr
            && ResolveGridKey(lenIx.Target) is { } lenRowGrid)
            return new Constant(gridDims[lenRowGrid].W);
        if (expr.Args[0] is VariableExpr lenRowVe && ResolveRowRef(lenRowVe) is { } lenRowRef)
            return new Constant(gridDims[lenRowRef.GridKey].W);
        if (ResolveGridKey(expr.Args[0]) is { } lenGridKey)
            return new Constant(gridDims[lenGridKey].H);

        // RFC 0009: len() on a live Optional dispatches on its tag -- every
        // member raises `object of type '<member>' has no len()` (the union's
        // members are scalars), the None member's wording included.
        if (TryEmitGuardedLen(expr) is { } lenGuarded)
            return lenGuarded;

        // RFC 0008: len() on a read view is its avail count; on a readline buffer the
        // line's length variable; on os.listdir()'s result the entry count. A direct
        // len(f.read(n)) mints the view and takes its avail.
        if (expr.Args[0] is CallExpr lenRead && TryRomfsReadCall(lenRead, out var lenH))
            return romfsViews[EmitRomfsReadView(lenH, lenRead)].Avail;
        if (SequenceKeyOf(expr.Args[0]) is { } romLenKey)
        {
            if (romfsViews.TryGetValue(romLenKey, out var lenView)) return lenView.Avail;
            if (romfsBufLen.TryGetValue(romLenKey, out var romLenVar))
                return new Variable(romLenVar, DataType.UINT16);
        }
        if (expr.Args[0] is CallExpr lenLd && IsOsFsCall(lenLd, "listdir"))
            return new Constant(OsListdirExprs(lenLd).Count);

        if (expr.Args[0] is VariableExpr vLen)
        {
            // A runtime string (f-string-as-value buffer): its length is the tracked write
            // position, not the buffer capacity that arraySizes would report below.
            if (TryGetRuntimeStr(vLen.Name, out var rsLen))
                return VisitExpression(new VariableExpr(rsLen.LenVar));

            // docs/rfcs/0004-arena-allocator.md: len() on an arena-allocated runtime-sized
            // bytearray reads the paired uint16 that TryLowerArenaBytearray stored the
            // allocation size into (arraySizes has no entry for one of these -- it is not a
            // compile-time-sized array).
            if (TryResolveArenaBuffer(vLen.Name, out string arenaLenQ)
                && arenaBufferLenVar.TryGetValue(arenaLenQ, out string arenaLenVar))
                return new Variable(arenaLenVar, DataType.UINT16);

            // Dict/set literal bindings have a compile-time size.
            if (TryGetDictBinding(vLen.Name, out var dLen)) return new Constant(dLen.Entries.Count);
            if (TryGetSetBinding(vLen.Name, out var sLen)) return new Constant(sLen.Elements.Count);

            // An @inline parameter bound to a list/tuple literal argument has a
            // statically known length (e.g. len(prog) inside a HAL helper).
            if (ResolveListLiteralParam(vLen.Name) is ListExpr boundLen)
                return new Constant(boundLen.Elements.Count);
            if (!string.IsNullOrEmpty(currentInlinePrefix) &&
                arraySizes.TryGetValue(currentInlinePrefix + vLen.Name, out int s1))
                return new Constant(LogicalArrayLen(currentInlinePrefix + vLen.Name, s1));
            // A frame that binds the name answers alone: at a module-level expansion the
            // enclosing `main.<name>` below IS the module global of the parameter's name, and
            // in a real function the bare lookup is. A frame binding that is no array (a
            // buffer parameter reached by pointer, a list) goes on to its own paths.
            string? lenFrameStore = FrameArrayStorage(vLen.Name, out bool lenFrameBinds);
            if (lenFrameStore != null && arraySizes.TryGetValue(lenFrameStore, out int sf))
                return new Constant(LogicalArrayLen(lenFrameStore, sf));
            if (!lenFrameBinds && !string.IsNullOrEmpty(currentFunction) &&
                arraySizes.TryGetValue(currentFunction + "." + vLen.Name, out int s2))
                return new Constant(LogicalArrayLen(currentFunction + "." + vLen.Name, s2));
            // The BINDING of this frame answers before the bare name does. A parameter is
            // spelled with the expansion's prefix and aliased to the caller's array, so the
            // bare lookup below is about a MODULE GLOBAL -- and when the program happens to
            // have a global of the parameter's name, the bare lookup used to run first and
            // answer with the global's length. `len(buf)` inside a shim written
            // `def writeto(self, addr, buf: bytearray)` then measured the caller's own `buf`
            // rather than what it was handed: the bytes on the wire were right and there was
            // the wrong number of them. The name is bound HERE, so what it is bound to is the
            // more specific answer and has to be asked for first. PyMCU#512.
            string lenKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + vLen.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + vLen.Name : vLen.Name);
            string lenResolved = lenKey;
            for (int depth = 0; depth < 20; depth++)
            {
                if (!variableAliases.TryGetValue(lenResolved, out string lenNext)) break;
                lenResolved = lenNext;
                if (TryResolveArrayStorageKey(lenResolved, out var lenStored))
                    return new Constant(LogicalArrayLen(lenStored, arraySizes[lenStored]));
            }

            if (!lenFrameBinds && arraySizes.TryGetValue(vLen.Name, out int s3))
                return new Constant(LogicalArrayLen(vLen.Name, s3));

            string lenStrKey = !string.IsNullOrEmpty(currentInlinePrefix)
                ? currentInlinePrefix + vLen.Name
                : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + vLen.Name : vLen.Name);
            if (ResolveStrConstant(lenStrKey) is string svLen) return new Constant(svLen.Length);
        }

        // A compile-time sequence held in a field: `len(self._pins)` / `len(self._levels)`.
        // Both are fixed at compile time, so the count is the answer -- the field is not a
        // container with a run-time length.
        if (expr.Args[0] is MemberAccessExpr lenMem)
        {
            // PyMCU#418: len(self.buf) / len(d.buf) on an arena-allocated field -- see
            // TryResolveArenaBufferField (Assign.cs) and the matching index hooks in
            // Expr.cs / Assign.cs.
            if (TryResolveArenaBufferField(lenMem.Object, lenMem.Member, out string lenFieldQ)
                && arenaBufferLenVar.TryGetValue(lenFieldQ, out string lenFieldVar))
                return new Variable(lenFieldVar, DataType.UINT16);
            // len(m.f) where the field holds a heap list or tuple: the field
            // slot is a GC_REF whose header byte 0 is the count, same as a named
            // list's. A single-field class collapses `m.f` onto m's own binding,
            // so the name the access evaluates to is asked for too -- gated to a
            // plain field so no getter or descriptor call is replayed.
            if (lenMem.Object is VariableExpr lenOv)
            {
                string lenMemKey = ResolveNameKey(lenOv.Name) + "_" + lenMem.Member;
                if (listVarElemTypes.ContainsKey(lenMemKey))
                {
                    Val memListPtr = new Variable(lenMemKey, DataType.GC_REF);
                    return EmitListLoad(memListPtr, 0, DataType.UINT8);
                }
                if (InstanceClassOfName(lenOv.Name) is { } lenMemCls
                    && classFieldLayout.TryGetValue(lenMemCls, out var lenMemLay)
                    && lenMemLay.Any(f => f.Field == lenMem.Member)
                    && !IsPropertyGetterRead(lenMem) && !IsDescriptorMemberRead(lenMem)
                    && VisitExpression(lenMem) is { } lenMemVal)
                {
                    string? lenMemName = lenMemVal switch
                    { Variable lmv => lmv.Name, Temporary lmt => lmt.Name, _ => null };
                    if (lenMemName != null && listVarElemTypes.ContainsKey(lenMemName))
                        return EmitListLoad(lenMemVal, 0, DataType.UINT8);
                }
            }
            if (TryGetDictFor(lenMem, out var lenDict)) return new Constant(lenDict.Entries.Count);
            if (TryGetSetFor(lenMem, out var lenSet)) return new Constant(lenSet.Elements.Count);
            if (TryResolveInstanceSequence(lenMem, out _, out int lenSeqCount))
                return new Constant(lenSeqCount);
            if (ResolveMemberArrayName(lenMem) is { } lenFlat) return new Constant(LogicalArrayLen(lenFlat, arraySizes[lenFlat]));
            if (ResolveConstSequenceExpr(lenMem) is { } lenConstSeq)
                return new Constant(lenConstSeq.Count);
            // `len(self.pin_mapping.analog_pins)`: a class-object field's sequence
            // attribute -- each candidate's count, selected on the field's tag.
            if (lenMem.Object is MemberAccessExpr coLenInner
                && ClassObjectFieldClasses(coLenInner) is { } coLenCands)
            {
                var lenVals = new List<Val>(coLenCands.Count);
                foreach (var cand in coLenCands)
                {
                    if (!constSequenceBindings.TryGetValue(ClassAttrKey(cand, lenMem.Member),
                                                           out var ce))
                        throw UserError(
                            $"field '{coLenInner.Member}' can hold class {ShortClassName(cand)}, "
                            + $"which has no compile-time sequence attribute '{lenMem.Member}' "
                            + "to measure", lenMem);
                    lenVals.Add(new Constant(ce.Count));
                }
                return EmitClassObjectSelect(coLenInner, coLenCands, lenVals);
            }
        }

        // A name bound to a short constant list keeps its elements, not an array: `len(xs)`
        // inside a method whose parameter was handed `levels = [7, 8, 9]`.
        if (expr.Args[0] is VariableExpr lenSeqVe && ResolveConstSequence(lenSeqVe.Name) is { } lenSeqElems)
            return new Constant(lenSeqElems.Count);

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

            // Outlined __len__: dispatch as a method call rather than falling through to
            // the container path, which rejects a class instance by type.
            if (expr.Args[0] is VariableExpr lenVe
                && TryResolveInstanceMethodAst(lenVe.Name, "__len__") != null)
                return VisitCall(new CallExpr(
                    new MemberAccessExpr(lenVe, "__len__"),
                    new List<Expression>()) { Line = expr.Line });
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

        // Library mode: len() of a buffer parameter reads the hidden length that travels with
        // it. This is the only length a kernel can trust, so it is the one `len()` answers.
        if (expr.Args[0] is VariableExpr lenBufVe && !string.IsNullOrEmpty(currentFunction)
            && bufferLengthParams.TryGetValue(currentFunction + "." + lenBufVe.Name, out string lenParam))
            return new Variable(lenParam, variableTypes[lenParam]);

        throw UserError("len() argument must be a fixed-size array or list literal", ArgAt(expr, 0));
    }

    // int.from_bytes(bytes, endian): assemble a uint16 from a two-byte literal/list.
    private Val EmitIntFromBytesBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 2)
            throw UserError("int.from_bytes() expects exactly two arguments (bytes, endian)", expr.Callee);
        bool littleEndian = true;
        if (expr.Args[1] is StringLiteral estr)
        {
            if (estr.Value == "big") littleEndian = false;
            else if (estr.Value != "little")
                throw UserError("int.from_bytes() endian must be 'little' or 'big'", ArgAt(expr, 1));
        }
        else throw UserError("int.from_bytes() endian argument must be a string literal", ArgAt(expr, 1));

        if (expr.Args[0] is ListExpr le)
        {
            if (le.Elements.Count < 2) throw UserError("int.from_bytes() requires at least 2 bytes", ArgAt(expr, 0));
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

        throw UserError("int.from_bytes() first argument must be a bytes literal b\"...\" or list [lo, hi]", ArgAt(expr, 0));
    }


    // ---------------------------------------------------------------------------------------
    // struct: the subset an ahead-of-time target can do without a heap.
    //
    // The refusal used to be at the IMPORT, in StandardModuleNames, on the grounds that there
    // is no heap for packed bytes to be handed back in. That reason is exact for the half of
    // `struct` that returns a tuple, and describes nothing about the half the driver libraries
    // actually write: measured across the twelve most-used Adafruit libraries, every format is
    // a string literal (21 of 21), the workhorse descriptors index the result on the spot
    // (`unpack_from(fmt, buf, off)[0]`) so no tuple ever escapes, and every `pack_into` target
    // is a buffer the caller already owns. So the import resolves now -- lib/src/pymcu/struct.py
    // exists and the stdlib-alias fallback finds it, the same way `math` and `time` are found --
    // and the refusal moved HERE, to the call, where it can say which shape was out of scope.
    //
    // Everything is expanded from the format read at compile time. Nothing parses a format on
    // the chip, and there is no run-time `struct` object.
    //
    // OUT OF SCOPE, each refused by name and never guessed at: a non-literal format,
    // a non-literal index, and any code outside B/b/H/h/I/i/L/l. A width read from the wrong
    // code is a silent wrong value on a sensor reading, which is the failure this whole
    // surface has to not have.

    /// <summary>
    /// AST-only: does this call name `struct.&lt;member&gt;`, under whatever name the file imported
    /// the module by? Nothing is visited and no IR is emitted, so a caller may still decline.
    /// </summary>
    private bool IsStructCall(CallExpr c, string member)
    {
        if (c.Callee is MemberAccessExpr { Object: VariableExpr mv } ma && ma.Member == member)
        {
            string mod = TryImportedAlias(mv.Name, out var real) && real != null
                ? real : mv.Name;
            return mod == "struct";
        }
        // `from struct import unpack_from`: the bare name resolves through the same alias to
        // the module-qualified key.
        return c.Callee is VariableExpr fv && fv.Name == member
            && ResolveCallee(fv.Name) == "struct_" + member;
    }

    /// <summary>One field of a struct format: where it starts, how wide it is, how it is read.</summary>
    private readonly record struct StructField(int Offset, int Width, bool Signed, bool LittleEndian);

    private const string StructCodes = "B, b, H, h, I, i, L, l";

    /// <summary>
    /// The fields of a struct format string, or a located refusal naming what was unsupported.
    /// `who` names the call in the message, since one format serves all three entry points.
    /// </summary>
    private List<StructField> ParseStructFormat(string fmt, string who, ASTNode at)
    {
        if (fmt.Length == 0)
            throw UserError($"{who}: the format string is empty", at);

        bool little = true;
        bool orderGiven = false;
        int i = 0;
        switch (fmt[0])
        {
            case '<': little = true;  orderGiven = true; i = 1; break;
            case '>': little = false; orderGiven = true; i = 1; break;
            case '!': little = false; orderGiven = true; i = 1; break;
            case '=':
            case '@':
                throw UserError(
                    $"{who}: the '{fmt[0]}' byte-order prefix means native order AND native "
                    + "alignment, which depends on the host that ran CPython and is not "
                    + "something this compiler can reproduce for a chip. Write '<' or '>' to "
                    + "say which order the device uses.", at);
        }

        var fields = new List<StructField>();
        int offset = 0;
        for (; i < fmt.Length; i++)
        {
            char c = fmt[i];
            // I and L are both 4 bytes under a '<'/'>'/'!' prefix (standard sizes), which is
            // the only spelling this parser accepts for anything wider than a byte anyway.
            int width = c switch
            {
                'B' or 'b' => 1,
                'H' or 'h' => 2,
                'I' or 'i' or 'L' or 'l' => 4,
                _ => 0,
            };
            if (width == 0)
            {
                string extra = char.IsDigit(c)
                    ? " A repeat count is not supported; write the code out once per field."
                    : "";
                throw UserError(
                    $"{who}: '{c}' is not a supported struct code. PyMCU expands the format at "
                    + $"compile time into loads and stores, and implements {StructCodes}."
                    + extra, at);
            }
            // Without a prefix CPython uses the host's order and alignment. For a single
            // one-byte field neither can differ, so that one spelling is accepted -- it is what
            // `ROUnaryStruct(0x34, "b")` writes -- and anything wider is refused rather than
            // guessed.
            if (!orderGiven && width > 1)
                throw UserError(
                    $"{who}: '{fmt}' gives no byte order, and a {width}-byte field needs one. "
                    + "Write '<' for little-endian or '>' for big-endian.", at);

            fields.Add(new StructField(offset, width, c is 'b' or 'h' or 'i' or 'l', little));
            offset += width;
        }

        if (fields.Count == 0)
            throw UserError($"{who}: '{fmt}' declares no fields", at);
        return fields;
    }

    /// <summary>
    /// The read for one field as an expression over `byteAt(n)` -- the field's n-th buffer
    /// byte. Multi-byte fields assemble most-significant byte first, so the emitted load
    /// order IS the byte order. A 4-byte field widens each byte to uint32 before it shifts:
    /// a uint8 shift promotes only one storage step (to uint16), where a 24-bit shift would
    /// lose the byte it is there to read.
    /// </summary>
    private static Expression AssembleStructField(StructField f, Func<int, Expression> byteAt)
    {
        Expression? acc = null;
        for (int n = 0; n < f.Width; n++)
        {
            Expression term = byteAt(f.LittleEndian ? f.Width - 1 - n : n);
            int shift = 8 * (f.Width - 1 - n);
            if (f.Width == 4)
                term = new CallExpr(new VariableExpr("uint32"), new List<Expression> { term });
            if (shift > 0)
                term = new BinaryExpr(term, PyMCU.Frontend.BinaryOp.LShift, new IntegerLiteral(shift));
            acc = acc == null ? term : new BinaryExpr(acc, PyMCU.Frontend.BinaryOp.BitOr, term);
        }
        return acc!;
    }

    /// <summary>
    /// The format argument of a struct call as compile-time text, or a located refusal.
    ///
    /// StaticStringOf already reaches a literal, a name bound to one, a `str`
    /// parameter that received a compile-time string (any length, not only one
    /// character), and -- through StaticStringOfField -- a string held in a FIELD
    /// at any depth, which is what `self.format` is after `__init__` stored the
    /// literal a descriptor was built with.
    /// </summary>
    private string StructFormatArg(CallExpr expr, string who)
    {
        if (expr.Args.Count == 0)
            throw UserError($"{who}: expects a format string as its first argument", expr.Callee);
        if (StaticStringOf(expr.Args[0]) is { } fmt) return fmt;
        throw UserError(
            $"{who}: the format must be a string known at compile time, because the loads and "
            + "stores it describes are chosen while compiling. This one is only known at run "
            + "time, so there is nothing to expand.", ArgAt(expr, 0));
    }

    /// <summary>A struct call's `offset` argument, which must be a constant.</summary>
    private int StructOffsetArg(CallExpr expr, int argIndex, string who)
    {
        if (expr.Args.Count <= argIndex) return 0;
        try { return EvaluateConstantExpr(expr.Args[argIndex]); }
        catch
        {
            throw UserError(
                $"{who}: the offset must be known at compile time, because it decides which "
                + "bytes are read. Use a literal or a const.", ArgAt(expr, argIndex));
        }
    }

    /// <summary>`struct.calcsize(fmt)`: the record's size, folded.</summary>
    private Val EmitStructCalcsize(CallExpr expr)
    {
        const string who = "struct.calcsize()";
        if (expr.Args.Count != 1)
            throw UserError($"{who} expects exactly one argument (the format)", expr.Callee);
        var fields = ParseStructFormat(StructFormatArg(expr, who), who, expr.Callee);
        return new Constant(fields[^1].Offset + fields[^1].Width);
    }

    /// <summary>
    /// A bare `struct.unpack_from(...)`. Always refused: the value it would produce is a tuple,
    /// and the supported shape is the one that never lets one exist.
    /// </summary>
    private Val EmitStructUnpackFrom(CallExpr expr, string who = "struct.unpack_from()")
    {
        // Parse first, so a program that is wrong about BOTH hears about the format it wrote
        // rather than about a shape it can then fix and be refused again.
        ParseStructFormat(StructFormatArg(expr, who), who, expr.Callee);
        throw UserError(
            $"{who} returns a tuple, which has no value in this position. Bind it to a name "
            + "(`t = unpack_from(fmt, buf, off)`) and index, slice or iterate that, or index "
            + "the call on the spot (`unpack_from(fmt, buf, off)[0]`).", expr.Callee);
    }

    /// <summary>
    /// `struct.unpack(fmt, buf)[k]` / `struct.unpack_from(fmt, buf, off)[k]` -- the whole
    /// expression, expanded into a read of field k. Called from VisitIndex, which is the
    /// only place the `[k]` is visible.
    ///
    /// Built as AST and handed to the ordinary expression lowering rather than emitting
    /// ArrayLoad here: a fixed-size array indexed by a constant is UNROLLED into per-element
    /// names and only a variable-indexed one becomes a real SRAM array, so bypassing that
    /// would read bytes the rest of the program never wrote.
    /// </summary>
    private Val EmitStructUnpackFromIndexed(CallExpr call, Expression indexExpr)
    {
        bool isUnpackFrom = IsStructCall(call, "unpack_from");
        string who = isUnpackFrom ? "struct.unpack_from()" : "struct.unpack()";
        var fields = ParseStructFormat(StructFormatArg(call, who), who, call.Callee);

        int maxArgs = isUnpackFrom ? 3 : 2;
        if (call.Args.Count < 2 || call.Args.Count > maxArgs)
            throw UserError(
                $"{who} expects (format, buffer)"
                + (isUnpackFrom ? " or (format, buffer, offset)" : ""), call.Callee);

        int k;
        try { k = EvaluateConstantExpr(indexExpr); }
        catch
        {
            throw UserError(
                $"{who}[i]: the index must be known at compile time, because it decides which "
                + "field is read and how wide it is.", indexExpr);
        }
        if (k < 0 || k >= fields.Count)
            throw UserError(
                $"{who}[{k}]: the format describes {fields.Count} field"
                + (fields.Count == 1 ? "" : "s") + ", so there is no field {k}.".Replace("{k}", k.ToString()),
                indexExpr);

        var f = fields[k];
        Expression at = new IntegerLiteral(StructOffsetArg(call, 2, who) + f.Offset);
        Expression buf = NormalizeUnpackBuffer(call.Args[1], ref at);

        Expression Byte(int n) => new IndexExpr(buf, AddByteOffset(at, n));
        Expression assembled = AssembleStructField(f, Byte);

        Val v = VisitExpression(assembled);

        // The bytes are assembled unsigned; a signed code means the same bits read as signed.
        // Same width, so this is a reinterpretation and not a conversion.
        DataType want = (f.Width, f.Signed) switch
        {
            (1, false) => DataType.UINT8,
            (1, true) => DataType.INT8,
            (2, false) => DataType.UINT16,
            (2, true) => DataType.INT16,
            (4, false) => DataType.UINT32,
            _ => DataType.INT32,
        };
        if (GetValType(v) == want) return v;
        Temporary typed = MakeTemp(want);
        Emit(new Copy(v, typed));
        return typed;
    }

    /// <summary>
    /// `struct.unpack(fmt, buf)` / `struct.unpack_from(fmt, buf[, off])` bound to a name --
    /// possibly through `list(...)`/`tuple(...)`: the result is a compile-time sequence of
    /// per-field reads, one expression each, carrying the field's width and sign in a cast so
    /// the slots it is stored into keep them. The buffer argument is pinned to its storage
    /// key, so the reads still land on the same bytes after the target name is rebound
    /// (`coeff = list(unpack(fmt, bytes(coeff)))` assigns `coeff` again). `list(seq)` /
    /// `tuple(seq)` of a compile-time sequence or a fixed array produces its elements as a
    /// copy, which is what those calls mean. Returns false for anything else, leaving the
    /// call for the paths that report it.
    /// </summary>
    private bool TryStructUnpackSeq(Expression e, out List<Expression> elems, out List<DataType>? types)
    {
        elems = null!;
        types = null;

        Expression inner = e;
        bool wrapped = false;
        if (inner is CallExpr { Callee: VariableExpr { Name: "list" or "tuple" } } wrap
            && wrap.Args.Count == 1)
        {
            inner = wrap.Args[0];
            wrapped = true;
        }

        if (inner is CallExpr call
            && (IsStructCall(call, "unpack") || IsStructCall(call, "unpack_from")))
        {
            bool isUnpackFrom = IsStructCall(call, "unpack_from");
            string who = isUnpackFrom ? "struct.unpack_from()" : "struct.unpack()";
            var fields = ParseStructFormat(StructFormatArg(call, who), who, call.Callee);
            int maxArgs = isUnpackFrom ? 3 : 2;
            if (call.Args.Count < 2 || call.Args.Count > maxArgs)
                throw UserError(
                    $"{who} expects (format, buffer)"
                    + (isUnpackFrom ? " or (format, buffer, offset)" : ""), call.Callee);
            int packedOff = isUnpackFrom ? StructOffsetArg(call, 2, who) : 0;
            Expression baseOff = new IntegerLiteral(packedOff);

            Expression buf = NormalizeUnpackBuffer(call.Args[1], ref baseOff);
            if (buf is VariableExpr bufVar && ResolveBufferKey(bufVar) is { } bufKey)
                buf = new VariableExpr(bufKey);

            elems = new List<Expression>(fields.Count);
            types = new List<DataType>(fields.Count);
            foreach (var f in fields)
            {
                Expression Byte(int n) => new IndexExpr(buf, AddByteOffset(baseOff, f.Offset + n));
                Expression assembled = AssembleStructField(f, Byte);
                (DataType dt, string? cast) = (f.Width, f.Signed) switch
                {
                    (1, false) => (DataType.UINT8, (string?)null),
                    (1, true) => (DataType.INT8, "int8"),
                    (2, false) => (DataType.UINT16, "uint16"),
                    (2, true) => (DataType.INT16, "int16"),
                    (4, false) => (DataType.UINT32, "uint32"),
                    _ => (DataType.INT32, "int32"),
                };
                if (cast != null)
                    assembled = new CallExpr(new VariableExpr(cast), new List<Expression> { assembled });
                elems.Add(assembled);
                types.Add(dt);
            }
            return true;
        }

        if (wrapped)
        {
            if (ResolveConstSequenceExpr(inner) is { } seqElems)
            {
                elems = new List<Expression>(seqElems);
                return true;
            }
            if (SequenceKeyOf(inner) is { } skey
                && arraySizes.TryGetValue(skey, out int sn) && sn > 0
                && !instanceClasses.ContainsKey(skey + "__0"))
            {
                elems = FixedArrayElementExprs(inner, sn);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The elements of a fixed-size array as subscript-read expressions of the source
    /// expression itself. Constant indices fold to the element slot on an unrolled array
    /// (keeping each slot's own type) and to an ArrayLoad on a flat SRAM one; letting the
    /// index path resolve the name is also what keeps an @inline-prefixed binding from
    /// being qualified twice.
    /// </summary>
    private List<Expression> FixedArrayElementExprs(Expression target, int count)
    {
        var result = new List<Expression>(count);
        for (int k = 0; k < count; k++)
            result.Add(new IndexExpr(target, new IntegerLiteral(k)));
        return result;
    }

    /// <summary>
    /// `struct.pack_into(fmt, buf, off, v0, ...)` -- one value per format field, written
    /// into a buffer the caller owns. Lowered as the byte stores the format describes,
    /// through the ordinary assignment path for the same reason the read goes through
    /// the ordinary expression path.
    /// </summary>
    private Val EmitStructPackInto(CallExpr expr)
    {
        const string who = "struct.pack_into()";
        string fmt = StructFormatArg(expr, who);
        var fields = ParseStructFormat(fmt, who, expr.Callee);

        // (format, buffer, offset, v0, v1, ...) -- one value argument per field the
        // format describes. `pack_into(fmt, buf, off, *pair)` arrives spliced, with
        // pair's elements already in the list. A count that disagrees is the mistake
        // CPython raises for pack_into as well: an N-field format wants N values.
        int nValues = expr.Args.Count - 3;
        if (expr.Args.Count < 4 || nValues != fields.Count)
            throw UserError(
                $"{who} writes one value per field: '{fmt}' describes {fields.Count} field"
                + (fields.Count == 1 ? "" : "s")
                + $" and {Math.Max(nValues, 0)} value{(nValues == 1 ? "" : "s")} "
                + (nValues == 1 ? "was" : "were")
                + " given. A `*seq` argument splices to one value per element, so a "
                + "sequence that does not fill the format still stops here.", expr.Callee);

        int at = StructOffsetArg(expr, 2, who);
        Expression buf = expr.Args[1];

        EmitPackFields(fields, buf, at, expr.Args.GetRange(3, nValues), expr.Line, expr.Column);

        // pack_into returns None. A caller that uses the value gets the ordinary void-in-an-
        // expression diagnostic rather than a zero.
        return new Constant(0);
    }

    /// <summary>
    /// `name = struct.pack(fmt, v...)` -- the value form. On a heap-free target the result is
    /// a fixed bytearray of calcsize bytes with the fields' bytes stored into it, the same
    /// writes pack_into makes onto a fresh name.
    /// </summary>
    private void EmitStructPackToName(string name, CallExpr expr, int line, int column)
    {
        const string who = "struct.pack()";
        string fmt = StructFormatArg(expr, who);
        var fields = ParseStructFormat(fmt, who, expr.Callee);
        int size = fields[^1].Offset + fields[^1].Width;

        int nValues = expr.Args.Count - 1;
        if (nValues != fields.Count)
            throw UserError(
                $"{who} writes one value per field: '{fmt}' describes {fields.Count} field"
                + (fields.Count == 1 ? "" : "s")
                + $" and {Math.Max(nValues, 0)} value{(nValues == 1 ? "" : "s")} "
                + (nValues == 1 ? "was" : "were") + " given.", expr.Callee);

        VisitStatement(new VarDecl(name, "bytearray",
            new CallExpr(new VariableExpr("bytearray"),
                         new List<Expression> { new IntegerLiteral(size) })
        ) { Line = line, Column = column });

        EmitPackFields(fields, new VariableExpr(name), 0, expr.Args.GetRange(1, nValues), line, column);
    }

    /// <summary>
    /// The per-field byte stores pack() and pack_into() share: least significant byte of
    /// each field first, so the store order is the byte order.
    /// </summary>
    private void EmitPackFields(List<StructField> fields, Expression buf, int at,
        List<Expression> values, int line, int column)
    {
        for (int k = 0; k < fields.Count; ++k)
        {
            var f = fields[k];
            Expression value = values[k];

            void Store(int n, Expression e) =>
                VisitStatement(new AssignStmt(new IndexExpr(buf, new IntegerLiteral(at + f.Offset + n)), e)
                    { Line = line, Column = column });

            if (f.Width == 1)
            {
                Store(0, new BinaryExpr(value, PyMCU.Frontend.BinaryOp.BitAnd, new IntegerLiteral(0xFF)));
            }
            else
            {
                for (int n = 0; n < f.Width; n++)
                {
                    Expression piece = new BinaryExpr(
                        new BinaryExpr(value, PyMCU.Frontend.BinaryOp.RShift, new IntegerLiteral(8 * n)),
                        PyMCU.Frontend.BinaryOp.BitAnd, new IntegerLiteral(0xFF));
                    Store(f.LittleEndian ? n : f.Width - 1 - n, piece);
                }
            }
        }
    }

    // abs(x): compile-time fold for constants, else a branchless-ish negate-if-negative.
    private Val EmitAbsBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("abs() expects exactly one argument", expr.Callee);
        if (expr.Args[0] is StringLiteral or FStringExpr)
            throw UserError("abs() argument must be numeric, not a string", ArgAt(expr, 0));
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

    /// The elements of a single `min`/`max` argument when they are known at compile time:
    /// a list or tuple literal, or a name bound to a fixed-size array. Null when the length is
    /// not known, which is the case that has to be refused rather than guessed at.
    ///
    /// `max(xs)` is the ordinary Python spelling with a list in hand, and it unrolls to exactly
    /// the comparisons `max(a, b, c)` already emits, so it is expanded into that form rather
    /// than given a lowering of its own.
    private List<Expression>? ConstantElementsOf(Expression arg)
    {
        switch (arg)
        {
            case ListExpr le:  return le.Elements.Count > 0 ? le.Elements : null;
            case TupleExpr te: return te.Elements.Count > 0 ? te.Elements : null;
            case VariableExpr ve:
            {
                // Same resolution order the Callable[N] call uses: inline prefix, then the
                // enclosing function, then the bare name for a module-level array.
                string key = !string.IsNullOrEmpty(currentInlinePrefix)
                    ? currentInlinePrefix + ve.Name
                    : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + ve.Name : ve.Name);
                if (!arraySizes.ContainsKey(key) && arraySizes.ContainsKey(ve.Name)) key = ve.Name;
                if (!arraySizes.TryGetValue(key, out int n) || n <= 0) return null;

                var elems = new List<Expression>(n);
                for (int i = 0; i < n; i++)
                    elems.Add(new IndexExpr(new VariableExpr(ve.Name), new IntegerLiteral(i)));
                return elems;
            }
            default: return null;
        }
    }

    /// The elements of a min/max sequence argument, or the refusal that names the shape.
    private List<Expression> SequenceElementsOrThrow(Expression arg, string name)
        => ConstantElementsOf(arg)
           ?? throw UserError(
               $"{name}() over a sequence needs a length known at compile time, and this one " +
               $"does not have it. Pass the operands as separate arguments " +
               $"(`{name}(a, b, c)`), index a fixed-size array " +
               $"(`{name}(xs[0], xs[1], xs[2])`), or keep a running {name} in a loop.",
               arg);

    /// Rewrites `f(xs)` into `f(xs[0], xs[1], ...)` for min/max, or throws naming the shape.
    private Val ExpandSequenceMinMax(CallExpr expr, string name)
    {
        var elems = SequenceElementsOrThrow(expr.Args[0], name);

        if (elems.Count == 1) return VisitExpression(elems[0]);
        return VisitExpression(new CallExpr(new VariableExpr(name), new List<Expression>(elems)));
    }

    private int _minMaxKeyTemp;

    /// Splits a `key=` argument out of a min()/max() call. Any other keyword is refused by
    /// name here: min and max are handled before the general keyword path, so a kwarg used to
    /// reach VisitExpression and surface as "Unknown Expression type: KeywordArgExpr", which
    /// is a class of this compiler reported to someone who wrote `key=` (#190).
    private (List<Expression> Args, Expression? Key) SplitMinMaxKey(CallExpr expr, string name)
    {
        if (!expr.Args.Any(a => a is KeywordArgExpr)) return (expr.Args, null);

        var positional = new List<Expression>();
        Expression? key = null;
        foreach (var a in expr.Args)
        {
            if (a is not KeywordArgExpr kw) { positional.Add(a); continue; }
            if (kw.Key != "key")
                throw UserError(
                    $"unknown keyword argument '{kw.Key}' in call to '{name}()'; {name}() takes "
                    + "the values to compare and an optional 'key'", kw.Value);
            if (key != null)
                throw UserError($"keyword argument 'key' repeated in call to '{name}()'", kw.Value);
            key = kw.Value;
        }

        // `key=None` is Python's way of saying no key at all, so it leaves the plain lowering.
        if (key is NoneLiteral) key = null;
        return (positional, key);
    }

    /// `min(a, b, key=f)` / `max(...)`: the key is applied to each operand and the operands are
    /// compared by their keys, so what comes back is the ORIGINAL value, not its key.
    ///
    /// Every operand is bound to a name of its own before anything is compared, because it is
    /// read twice -- once by the key call and once as the result -- and re-evaluating the
    /// expression would run its side effects twice. The running winner and its key are bound
    /// the same way at each step, which is what keeps the key call count at one per operand,
    /// the number CPython makes, instead of re-deriving the winner's key at every comparison.
    ///
    /// `<=` for min and `>=` for max keep CPython's tie rule: the FIRST operand with the
    /// winning key is the one returned.
    private Val EmitMinMaxByKey(List<Expression> operands, Expression key, string name, ASTNode? at)
    {
        if (operands.Count == 0) throw UserError($"{name}() needs an argument", at);
        if (operands.Count == 1) operands = SequenceElementsOrThrow(operands[0], name);

        string Bind(Expression e)
        {
            string bound = "__minmax_key_" + (_minMaxKeyTemp++);
            VisitStatement(new AssignStmt(new VariableExpr(bound), e));
            return bound;
        }

        string KeyOf(string operand)
            => Bind(new CallExpr(key, new List<Expression> { new VariableExpr(operand) }));

        var op = name == "min" ? PyMCU.Frontend.BinaryOp.LessEq : PyMCU.Frontend.BinaryOp.GreaterEq;
        string best = Bind(operands[0]);
        string bestKey = KeyOf(best);

        for (int i = 1; i < operands.Count; i++)
        {
            string candidate = Bind(operands[i]);
            string candidateKey = KeyOf(candidate);

            // Both sides of this comparison are bound names, so writing it twice re-reads two
            // variables and calls nothing.
            BinaryExpr BestWins() => new(new VariableExpr(bestKey), op, new VariableExpr(candidateKey));

            string nextBest = Bind(new TernaryExpr(
                new VariableExpr(best), BestWins(), new VariableExpr(candidate)));
            // The winner's key is only needed to compare against the NEXT operand.
            if (i < operands.Count - 1)
                bestKey = Bind(new TernaryExpr(
                    new VariableExpr(bestKey), BestWins(), new VariableExpr(candidateKey)));
            best = nextBest;
        }

        return VisitExpression(new VariableExpr(best));
    }

    /// The class name as the source spells it, without the module prefix the tables carry.
    private static string Unqualified(string classKey) =>
        classKey.Contains('_') ? classKey[(classKey.LastIndexOf('_') + 1)..] : classKey;

    /// <summary>
    /// min() and max() compare their arguments numerically and never consult a class, so an
    /// instance argument was read as the flattened handle -- a slot nobody writes. The pair
    /// came back as whichever zero won, and `max(a, b).n` printed 0 with nothing said (#491).
    /// A comparison dunder does not help: the reduction is emitted as a Binary, not as the
    /// comparison expression the dunder path sees.
    /// </summary>
    private void RefuseMinMaxOverInstances(CallExpr expr, string name)
    {
        foreach (var arg in expr.Args)
        {
            if (arg is not VariableExpr ve) continue;

            // `max(xs)` over a SEQUENCE of instances is the same reduction spelled once: it
            // expands to the pairwise form over the elements and answers from their handles.
            // `max(xs)` over a SEQUENCE of instances is the same reduction spelled once, and it
            // answers from the elements' handles just as the pairwise form did. A list of
            // instances is filed either as an instance ARRAY or, when it is a compile-time
            // sequence, under the per-element `<name>__<k>` keys.
            string? seqCls = null;
            foreach (var el in ElementsOfNamedSequence(ve.Name) ?? new List<Expression>())
                if (el is VariableExpr elv && InstanceClassOfName(elv.Name) is { Length: > 0 } elCls)
                    seqCls = elCls;
            foreach (string key in new[]
                     {
                         string.IsNullOrEmpty(currentInlinePrefix) ? ve.Name : currentInlinePrefix + ve.Name,
                         string.IsNullOrEmpty(currentFunction) ? ve.Name : currentFunction + "." + ve.Name,
                         ve.Name,
                     })
            {
                if (seqCls != null) break;
                if (instanceArrayClass.TryGetValue(key, out var arrCls) && arrCls.Length > 0)
                    seqCls = arrCls;
                else if (instanceClasses.TryGetValue(key + "__0", out var ctCls) && !string.IsNullOrEmpty(ctCls))
                    seqCls = ctCls;
            }
            if (seqCls != null)
                throw UserError(
                    $"{name}() compares the elements numerically and does not consult the class, "
                    + $"so '{ve.Name}', a sequence of '{Unqualified(seqCls)}' instances, has no "
                    + "values for it to compare. Reduce over the field you mean, or pick the "
                    + "object with a loop of your own.", arg);

            if (InstanceClassOfName(ve.Name) is not { } cls || cls.Length == 0) continue;
            throw UserError(
                $"{name}() compares its arguments numerically and does not consult the class, so "
                + $"'{ve.Name}', an instance of '{Unqualified(cls)}', has no value for it to "
                + $"compare. Pass the field you mean ({name}({ve.Name}.<field>, ...)), or pick "
                + "the object with an `if` of your own.", arg);
        }
    }

    // min(a, b): compile-time fold for constants, else compare-and-select.
    // min(xs): expanded to the above over the array's elements.
    private Val EmitMinBuiltin(CallExpr expr)
    {
        RefuseMinMaxOverInstances(expr, "min");
        if (expr.Args.Count > 0 && expr.Args[0] is GeneratorExpr)
            return EmitGenExpReduction(expr, "min");
        var (minArgs, minKey) = SplitMinMaxKey(expr, "min");
        if (minKey != null) return EmitMinMaxByKey(minArgs, minKey, "min", expr.Callee);
        if (minArgs.Count != expr.Args.Count) expr = new CallExpr(expr.Callee, minArgs) { Line = expr.Line };

        if (expr.Args.Count == 0) throw UserError("min() needs an argument", expr.Callee);
        if (expr.Args.Count == 1) return ExpandSequenceMinMax(expr, "min");
        if (expr.Args.Count > 2)
        {
            var folded = expr.Args[0];
            for (int k = 1; k < expr.Args.Count; k++)
                folded = new CallExpr(new VariableExpr("min"),
                    new List<Expression> { folded, expr.Args[k] });
            return VisitExpression(folded);
        }
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
    // max(xs): expanded to the above over the array's elements.
    private Val EmitMaxBuiltin(CallExpr expr)
    {
        RefuseMinMaxOverInstances(expr, "max");
        if (expr.Args.Count > 0 && expr.Args[0] is GeneratorExpr)
            return EmitGenExpReduction(expr, "max");
        var (maxArgs, maxKey) = SplitMinMaxKey(expr, "max");
        if (maxKey != null) return EmitMinMaxByKey(maxArgs, maxKey, "max", expr.Callee);
        if (maxArgs.Count != expr.Args.Count) expr = new CallExpr(expr.Callee, maxArgs) { Line = expr.Line };

        if (expr.Args.Count == 0) throw UserError("max() needs an argument", expr.Callee);
        if (expr.Args.Count == 1) return ExpandSequenceMinMax(expr, "max");
        if (expr.Args.Count > 2)
        {
            var folded = expr.Args[0];
            for (int k = 1; k < expr.Args.Count; k++)
                folded = new CallExpr(new VariableExpr("max"),
                    new List<Expression> { folded, expr.Args[k] });
            return VisitExpression(folded);
        }
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
        if (expr.Args.Count != 1) throw UserError("ord() expects exactly one argument", expr.Callee);
        if (expr.Args[0] is StringLiteral sl)
        {
            if (sl.Value.Length != 1) throw UserError("ord() argument must be a single character", ArgAt(expr, 0));
            return new Constant((int)sl.Value[0]);
        }

        // `for char in "PyMCU"` binds the loop variable to each compile-time character;
        // ord() of it folds to the code point instead of emitting a run-time read.
        if (TryGetCompileTimeText(expr.Args[0]) is { Length: 1 } ordText)
            return new Constant(ordText[0]);

        return VisitExpression(expr.Args[0]);
    }

    // chr(n): a byte value treated as a character; pass the value through unchanged.
    private Val EmitChrBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("chr() expects exactly one argument", expr.Callee);
        Val v = VisitExpression(expr.Args[0]);
        // A char on an 8-bit target is a single byte; a compile-time argument outside 0..255
        // would otherwise pass through as a too-large Constant and be silently truncated.
        if (v is Constant c && (c.Value < 0 || c.Value > 255))
            throw new ValueError($"chr() arg not in range(256): {c.Value}",
                expr.Line > 0 ? expr.Line : lastLine, expr.Column);
        // The byte IS the character on this target, and that is the whole reason the
        // char-ness kept being lost: a bare code is indistinguishable from any other number,
        // so only the one site that recognised the `chr(...)` CALL by syntax -- print's own
        // ladder -- ever wrote a character. Bind the text to the value instead, and it
        // survives being assigned to a name, folded through an @inline, and returned (#436).
        if (v is Constant cc)
            return new Constant(cc.Value, ((char)cc.Value).ToString());
        return v;
    }

    // sum(seq): fold a list literal or sum a fixed-size array's unrolled elements.
    //
    // Both lower to the `+` chain CPython evaluates, `seq[0] + seq[1] + ...`, through
    // VisitBinary: the elements are read the way a subscript reads them and each step
    // promotes like `+` does. The array form used to read flattened `a__0` slots that no
    // store writes (the array lives in SRAM), so sum(a) printed 0 or whatever the previous
    // expression left in the register; and an add at the element width wrapped
    // uint8 200 + 100 + 50 to 94.
    private Val EmitSumBuiltin(CallExpr expr)
    {
        if (expr.Args.Count > 0 && expr.Args[0] is GeneratorExpr)
            return EmitGenExpReduction(expr, "sum");
        if (expr.Args.Count != 1) throw UserError("sum() expects exactly one argument", expr.Callee);
        List<Expression> elems;
        switch (expr.Args[0])
        {
            case ListExpr { Elements.Count: 0 }:
                return new Constant(0);
            case ListExpr le:
                elems = le.Elements;
                break;
            case VariableExpr sumVar:
            {
                int arrSize = -1;
                if (!string.IsNullOrEmpty(currentInlinePrefix)
                    && arraySizes.TryGetValue(currentInlinePrefix + sumVar.Name, out int s))
                    arrSize = s;
                if (arrSize < 0 && !string.IsNullOrEmpty(currentFunction)
                    && arraySizes.TryGetValue(currentFunction + "." + sumVar.Name, out int s1))
                    arrSize = s1;
                if (arrSize < 0 && arraySizes.TryGetValue(sumVar.Name, out int s2))
                    arrSize = s2;

                if (arrSize <= 0) throw UserError("sum() requires a list literal or fixed-size array", ArgAt(expr, 0));
                elems = new List<Expression>(arrSize);
                for (int i = 0; i < arrSize; ++i)
                    elems.Add(new IndexExpr(sumVar, new IntegerLiteral(i) { Line = sumVar.Line })
                        { Line = sumVar.Line, Column = sumVar.Column });
                break;
            }
            default:
                throw UserError("sum() requires a list literal or fixed-size array", ArgAt(expr, 0));
        }

        Expression chain = elems[0];
        for (int i = 1; i < elems.Count; ++i)
            chain = new BinaryExpr(chain, PyMCU.Frontend.BinaryOp.Add, elems[i]) { Line = expr.Line, Column = expr.Column };
        return VisitExpression(chain);
    }

    // bool(x): Python's truth test, which for every value PyMCU can hold is "not zero".
    // Lowered as `x != 0` so the result is the 0/1 a materialized comparison already produces,
    // at the operand's own width (bool(300) is True, not bool(300 & 0xFF)).
    private Val EmitBoolBuiltin(CallExpr expr)
    {
        // Python's bool() with no argument is False.
        if (expr.Args.Count == 0) return new Constant(0);
        if (expr.Args.Count != 1) throw UserError("bool() expects at most one argument", expr.Callee);

        // A string is truthy when it is non-empty, which has nothing to do with the flash
        // address it lowers to. Fold the literal; anything else would compare the address.
        if (expr.Args[0] is StringLiteral sl) return new Constant(sl.Value.Length > 0 ? 1 : 0);
        if (expr.Args[0] is FStringExpr)
            throw UserError("bool() of an f-string is not supported: the string is built as it "
                            + "is printed, so there is no value to test. Test the values that go "
                            + "into it instead.", ArgAt(expr, 0));

        if (TryBufferTruthiness(expr.Args[0], out bool bufBool)) return new Constant(bufBool ? 1 : 0);

        // `bool(obj)` asks the object the same question `if obj:` does. It lowered straight to
        // `obj != 0`, which compares whatever the instance collapsed to, so bool() answered 0
        // for an object whose __len__ says 3 -- for a NAME as well as for a field (#385).
        return VisitExpression(
            new BinaryExpr(LowerInstanceTruthiness(expr.Args[0]),
                           Frontend.BinaryOp.NotEqual, new IntegerLiteral(0))
                { Line = expr.Line });
    }

    /// <summary>
    /// round(x): Python's round-half-even. There is no run-time float rounding routine, so the
    /// one-argument builtin folds only where x is a compile-time constant -- adafruit_ht16k33's
    /// `round(15 * brightness)` with the default brightness=1.0 is that shape. A run-time
    /// one-argument call still gets the honest refusal from <see cref="UnsupportedBuiltins"/>.
    ///
    /// round(x, n) (P2 AVR gaps bundle, item 2): n must be a compile-time constant (the digit
    /// buffer it sizes has to be). An integer x keeps CPython's own int semantics -- n &gt;= 0
    /// answers x unchanged, n &lt; 0 rounds to a multiple of 10**-n, still an int -- computed
    /// directly at compile time since there is no float32 precision question for an int. A
    /// float x (constant or run-time) forwards to pymcu.round2's _pymcu_round2, half-to-even
    /// on the EXACT decimal expansion of the float32 value, the same algorithm the f-string
    /// float format spec already uses (#p2avr-2).
    /// </summary>
    private Val EmitRoundBuiltin(CallExpr expr)
    {
        if (expr.Args.Count == 1)
        {
            Val rv = VisitExpression(expr.Args[0]);
            if (rv is Constant ic) return ic;
            if (rv is FloatConstant fc)
                return new Constant((int)Math.Round(fc.Value, MidpointRounding.ToEven));
        }
        else if (expr.Args.Count == 2)
        {
            if (!TryFoldInt(expr.Args[1], out int n))
                throw UserError("round()'s ndigits argument must be a compile-time constant "
                    + "integer", ArgAt(expr, 1));
            if (n is < -15 or > 15)
                throw UserError($"round()'s ndigits ({n}) is out of the supported range "
                    + "-15..15", ArgAt(expr, 1));

            // An integer x: CPython keeps it an int (round(5, 2) == 5, round(1234, -2) ==
            // 1200), no float32 precision question involved, so this folds directly.
            // Symmetric around zero: round the MAGNITUDE half-to-even, then restore sign.
            if (TryFoldInt(expr.Args[0], out int xi))
            {
                if (n >= 0) return new Constant(xi);
                long scale = 1;
                for (int k = 0; k < -n; ++k) scale *= 10;
                long ax = xi < 0 ? -(long)xi : xi;
                long q = ax / scale, r = ax % scale, twice = r * 2;
                if (twice > scale || (twice == scale && (q & 1) != 0)) q += 1;
                long result = (xi < 0 ? -q : q) * scale;
                return new Constant((int)result);
            }

            // A float x, constant or run-time: pow2(int(x)) is decided by xi failing to
            // fold above, so anything reaching here that is not a float is refused below.
            Val xv = VisitExpression(expr.Args[0]);
            bool isFloatArg = xv is FloatConstant
                || (xv is Variable fvv && fvv.Type == DataType.FLOAT)
                || (xv is Temporary fvt && fvt.Type == DataType.FLOAT);
            if (isFloatArg)
            {
                string round2Mod = RequireRound2Mod(expr);
                var fwd = new CallExpr(
                    new MemberAccessExpr(new VariableExpr(round2Mod), "_pymcu_round2"),
                    new List<Expression>
                    {
                        new PreEvaluatedExpr(xv, DataType.FLOAT),
                        new IntegerLiteral(n),
                    })
                { Line = expr.Line, Column = expr.Column };
                return VisitCall(fwd);
            }
        }
        throw UserError($"round() is a Python builtin that PyMCU does not provide: "
                        + UnsupportedBuiltins["round"] + ".", expr.Callee);
    }

    // RFC 0014 decision 4: true when a module-level `def <name>` or inline function of
    // the given spelling is in scope, which shadows a Python builtin of the same name
    // the way CPython's own name resolution would. A plain module-level assignment
    // (`round = something`) is not covered -- functionParams/inlineFunctions only
    // track defs, the same surface the pre-existing `open` check used.
    private bool IsBuiltinShadowed(string name)
        => functionParams.ContainsKey(name) || inlineFunctions.ContainsKey(name);

    // The module pymcu build injects (`import pymcu.round2 as ...`) once round(x, n) on a
    // run-time float is resolved here -- the same resolve-by-import-alias RequireStrfmtMod
    // uses. `pymcu build` does not scan the source for this (RFC 0014 decision 5) -- it
    // compiles, and on [NEEDS_ROUND2] injects the import and compiles again.
    private string RequireRound2Mod(Expression blame)
    {
        foreach (var kv in importedAliases)
            if (kv.Value == "pymcu.round2") return kv.Key;
        Logger.NeedsRound2();
        throw UserError(
            "round(x, n) on a float needs the pymcu.round2 helper; `pymcu build` injects it "
            + "automatically -- if invoking the compiler by hand, add "
            + "`import pymcu.round2 as _pymcu_round2` to the entry file.", blame);
    }

    /// <summary>
    /// Python builtins PyMCU does not provide, each with the reason and the way out. A builtin
    /// is always in scope and is spelled the same everywhere, so "(typo, or a missing import?)"
    /// sends the reader to check two things that are both already right; these messages name the
    /// builtin instead. Names absent from this table but present in <see cref="PythonBuiltins"/>
    /// get the generic "builtin PyMCU does not provide" wording.
    /// </summary>
    private static readonly Dictionary<string, string> UnsupportedBuiltins = new()
    {
        ["round"] =
            "rounding a float to the nearest integer is not implemented. `int(x)` truncates "
            + "toward zero; for round-half-away-from-zero write `int(x + 0.5)` when x >= 0 and "
            + "`int(x - 0.5)` when it is negative. Note that neither matches CPython's round(), "
            + "which rounds a tie to the even neighbour",
        ["isinstance"] =
            "every value here has one type, fixed when the program is compiled, so a run-time "
            + "type test has no question left to answer. Branch on a value you set yourself (an "
            + "explicit tag field), or write one function per type",
        ["issubclass"] =
            "the class hierarchy is resolved at compile time and does not exist at run time. "
            + "Decide the branch when you write the code",
        ["type"] =
            "types are resolved at compile time and no type object exists at run time. Use an "
            + "explicit tag field if the program has to distinguish two shapes of value",
        ["repr"] = "there is no run-time object model to describe. Use str(x), or print the "
                   + "fields you care about",
        ["input"] = "there is no console to read from. Read a byte from the UART instead "
                    + "(`uart.read()`)",
        ["open"] = "there is no filesystem. Use the chip's flash or EEPROM helpers",
        ["sorted"] = "it returns a new list, which needs a heap. Sort a fixed-size array in "
                     + "place instead",
        ["reversed"] = "it returns an iterator, which needs a heap. Walk the indices backwards "
                       + "with `for i in range(n - 1, -1, -1)`",
        ["map"] = "it returns an iterator, which needs a heap. Write the loop",
        ["copyright"] = "it prints the interpreter's text in an interactive session, and there is "
                        + "no interpreter or session on the target",
        ["credits"] = "it prints the interpreter's text in an interactive session, and there is "
                      + "no interpreter or session on the target",
        ["license"] = "it prints the interpreter's text in an interactive session, and there is "
                      + "no interpreter or session on the target",
        ["__import__"] = "modules are resolved when the program is compiled and nothing is "
                         + "imported at run time. Write the `import` statement",
        ["__build_class__"] = "classes are built when the program is compiled; there is no "
                              + "run-time class object to build. Write the `class` statement",
        ["filter"] = "it returns an iterator, which needs a heap. Write the loop with an `if`",
        ["list"] = "a growable list needs a heap. Declare a fixed-size array "
                   + "(`buf: uint8[4] = [...]`)",
        ["dict"] = "a growable dict needs a heap. Use `FixedDict` from pymcu.collections",
        ["set"] = "a growable set needs a heap. Use a bitmask, or a fixed-size array",
        ["tuple"] = "building a tuple at run time needs a heap. A tuple literal works where the "
                    + "compiler can see all of its elements",
        ["frozenset"] = "a set needs a heap. Use a bitmask, or a fixed-size array",
        ["iter"] = "there is no iterator protocol; `for` lowers each iterable shape directly. "
                   + "Loop over the sequence itself",
        ["next"] = "there is no iterator protocol outside `for`. Loop over the sequence itself",
        ["callable"] = "whether a name is a function is decided at compile time. Nothing is "
                       + "callable-or-not at run time",
        ["id"] = "objects have no run-time identity. Use `ptr(...)` if you need an address",
        ["hash"] = "there is no run-time object model to hash. Hash the bytes you care about "
                   + "yourself",
        ["format"] = "use an f-string (`f\"{x}\"`), which the compiler lowers directly",
        ["super"] = "base-class calls are resolved at compile time; name the base class "
                    + "explicitly (`Base.method(self, ...)`)",
        ["complex"] = "complex numbers are not supported",

        ["slice"] = "slice objects need a heap. Index the sequence directly",
        ["exit"] = "there is nothing to exit to; the program is the whole system. Loop forever, "
                   + "or reset the chip",
        ["quit"] = "there is nothing to quit to; the program is the whole system. Loop forever, "
                   + "or reset the chip",
    };

    /// <summary>
    /// Every name in CPython's builtins namespace. Membership is what rules out "typo, or a
    /// missing import?": a builtin is always in scope, so neither branch of that suggestion can
    /// be the answer. The list itself lives in PyMCU.Common so the imported-name check reads the
    /// same one rather than a copy that drifts.
    /// </summary>
    private static readonly HashSet<string> PythonBuiltins = PyMCU.Common.PythonBuiltinNames.All;

    // any(list-literal): compile-time fold when all elements are constant, else an
    // OR-reduction over the elements.
    private Val EmitAnyBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("any() expects exactly one argument", expr.Callee);
        if (expr.Args[0] is GeneratorExpr) return EmitGenExpReduction(expr, "any");
        if (!(expr.Args[0] is ListExpr le)) throw UserError("any() requires a list literal argument", ArgAt(expr, 0));
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
        if (expr.Args.Count != 1) throw UserError("all() expects exactly one argument", expr.Callee);
        if (expr.Args[0] is GeneratorExpr) return EmitGenExpReduction(expr, "all");
        if (!(expr.Args[0] is ListExpr le)) throw UserError("all() requires a list literal argument", ArgAt(expr, 0));
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

    // The CPython spelling of a compile-time constant in a given base: the sign, if
    // any, goes BEFORE the prefix ("-0x1", not "0x-1"), and a constant that folded
    // into the Unsigned lane (a raw bit pattern > int.MaxValue, e.g. from a uint32
    // literal) is never negative to begin with -- .Value there IS the magnitude's
    // bit pattern, not a two's-complement encoding of something smaller.
    // hex(-1) used to print "0xffffffff" (C#'s (-1).ToString("x") on the raw
    // 32-bit pattern) instead of CPython's "-0x1": a silent wrong answer, not a
    // refusal, for every compile-time NEGATIVE argument.
    private static string ConstBaseSpelling(Constant c, string prefix, int radix)
    {
        ulong mag; bool neg;
        if (c.Unsigned) { mag = unchecked((uint)c.Value); neg = false; }
        else if (c.Value < 0) { mag = unchecked((ulong)(-(long)c.Value)); neg = true; }
        else { mag = (uint)c.Value; neg = false; }
        string digits = radix switch
        {
            16 => mag.ToString("x"),
            2 => Convert.ToString((long)mag, 2),
            8 => Convert.ToString((long)mag, 8),
            _ => mag.ToString(),
        };
        return (neg ? "-" : "") + prefix + digits;
    }

    // hex(x)/oct(x)/bin(x) of a RUN-TIME value: builds the same spelling a compile-time
    // constant would get, into a buffer through pymcu.strfmt -- the same machinery an
    // f-string value assignment uses. The sign (if the source looks signed) is written
    // directly, ahead of the prefix; strfmt._fs_fmt only ever sees the unsigned
    // magnitude, so it never has to decide where the prefix goes relative to a sign it
    // would otherwise emit itself.
    private string EmitIntBaseRuntimeStr(string bufName, Val v, Expression srcExpr,
                                         int radix, string prefix, Expression blame)
    {
        string strfmtMod = RequireStrfmtMod(blame);
        // Signedness is decided by the VALUE, not only the spelling: LooksSigned has
        // no CallExpr case, so hex(minus_one()) on an int16-returning function spelled
        // "0xffffffff". A call result, an index read or anything else that produced a
        // signed-typed Val gets the sign branch too.
        bool signed = LooksSigned(srcExpr) || GetValType(v).IsSigned();
        int digitsMax = radix switch { 16 => 8, 8 => 11, 2 => 32, _ => 10 };
        int bufSize = 1 + prefix.Length + digitsMax + 1;   // sign + prefix + digits + NUL
        VisitStatement(new VarDecl(bufName, "bytearray",
            new CallExpr(new VariableExpr("bytearray"), new List<Expression> { new IntegerLiteral(bufSize) })));
        string lenVar = "__fslen_" + bufName;
        VisitStatement(new VarDecl(lenVar, "uint16", new IntegerLiteral(0)));
        string qualified = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + bufName
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + bufName : bufName);
        ClearStaleConstantText(qualified, bufName);
        runtimeStrVars[qualified] = (lenVar, bufSize);

        var buf = new VariableExpr(bufName);
        var pos = new VariableExpr(lenVar);
        Expression valueExpr = new PreEvaluatedExpr(v, signed ? DataType.INT32 : DataType.UINT32);
        string magVar = "__fsmag_" + bufName;
        if (signed)
        {
            VisitStatement(new VarDecl(magVar, "uint32", new IntegerLiteral(0)));
            var thenB = new Block();
            thenB.Statements.Add(new AssignStmt(pos,
                new CallExpr(new MemberAccessExpr(new VariableExpr(strfmtMod), "_fs_text"),
                    new List<Expression> { buf, pos, new StringLiteral("-") })));
            thenB.Statements.Add(new AssignStmt(new VariableExpr(magVar),
                new CallExpr(new VariableExpr("uint32"), new List<Expression> {
                    new BinaryExpr(new IntegerLiteral(0), Frontend.BinaryOp.Sub, valueExpr) })));
            var elseB = new Block();
            elseB.Statements.Add(new AssignStmt(new VariableExpr(magVar),
                new CallExpr(new VariableExpr("uint32"), new List<Expression> { valueExpr })));
            VisitStatement(new IfStmt(
                new BinaryExpr(valueExpr, Frontend.BinaryOp.Less, new IntegerLiteral(0)),
                thenB, null, elseB));
        }
        else
        {
            VisitStatement(new VarDecl(magVar, "uint32", valueExpr));
        }

        EmitStrfmtCall(strfmtMod, lenVar, "_fs_text",
            new List<Expression> { buf, pos, new StringLiteral(prefix) });
        EmitStrfmtCall(strfmtMod, lenVar, "_fs_fmt", new List<Expression>
        {
            buf, pos, new VariableExpr(magVar),
            new IntegerLiteral(radix), new IntegerLiteral(0), new IntegerLiteral(0),
        });
        VisitStatement(new AssignStmt(new IndexExpr(buf, pos), new IntegerLiteral(0)));
        return bufName;
    }

    // hex(x): a compile-time constant interns its spelling as a flash string literal
    // (zero run-time cost, unchanged); a run-time value builds the same spelling into
    // a buffer (#p2avr-1).
    private Val EmitHexBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("hex() expects exactly one argument", expr.Callee);
        Val v = RequireIntBaseArg(VisitExpression(expr.Args[0]), "hex", expr.Args[0]);
        if (v is Constant c) return InternedStringConstant(ConstBaseSpelling(c, "0x", 16));
        return new Variable(
            EmitIntBaseRuntimeStr("__hex" + tempCounter++, v, expr.Args[0], 16, "0x", expr),
            DataType.UINT8);
    }

    // bin(x): same shape as hex(), base 2.
    private Val EmitBinBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("bin() expects exactly one argument", expr.Callee);
        Val v = RequireIntBaseArg(VisitExpression(expr.Args[0]), "bin", expr.Args[0]);
        if (v is Constant c) return InternedStringConstant(ConstBaseSpelling(c, "0b", 2));
        return new Variable(
            EmitIntBaseRuntimeStr("__bin" + tempCounter++, v, expr.Args[0], 2, "0b", expr),
            DataType.UINT8);
    }

    // oct(x): same shape as hex(), base 8. Previously unimplemented -- calling it fell
    // through to the generic "builtin PyMCU does not provide" refusal.
    private Val EmitOctBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("oct() expects exactly one argument", expr.Callee);
        Val v = RequireIntBaseArg(VisitExpression(expr.Args[0]), "oct", expr.Args[0]);
        if (v is Constant c) return InternedStringConstant(ConstBaseSpelling(c, "0o", 8));
        return new Variable(
            EmitIntBaseRuntimeStr("__oct" + tempCounter++, v, expr.Args[0], 8, "0o", expr),
            DataType.UINT8);
    }

    // hex()/bin()/oct() refuse a STRING argument (a Constant carrying Text, the same
    // discriminator print() uses to tell "this constant stands for text" from "this
    // constant stands for a number" -- see EmitStreamVal). Without this, `hex("A")`
    // silently hex-encoded the interned string id instead of raising, the way CPython's
    // TypeError ("'str' object cannot be interpreted as an integer") never lets it.
    private Val RequireIntBaseArg(Val v, string name, Expression at)
    {
        if (v is Constant { Text: { } } || v is FloatConstant)
            throw UserError($"{name}() argument must be an integer", at);
        return v;
    }

    // str(const): intern the decimal form as a flash string literal (compile-time only).
    // A constant that already IS a string carries its text in .Text -- `str(s)` is `s`,
    // not the decimal of its interned id (adafruit_ht16k33's _number does
    // `str(number)` on whatever the Union[str, float] parameter bound).
    private Val EmitStrBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1) throw UserError("str() expects exactly one argument", expr.Callee);
        // `str(seq)` on a literal list/tuple in fixed slots: the repr materialized into
        // a runtime-string buffer (`x = str(v)`, `f"{str(v)}"`, print(str(v)) falls
        // into the streamed intercept before ever calling this).
        if (expr.Args[0] is VariableExpr sve
            && ResolveArrayVar(sve.Name) is { } sSeq
            && literalSequenceArrays.Contains(sSeq.Name))
            return new Variable(EmitSeqReprRuntimeStr("__str" + tempCounter++, sSeq.Name,
                tupleBoundNames.Contains(sSeq.Name) || IsTupleBound(sve.Name), expr), DataType.UINT8);
        Val v = VisitExpression(expr.Args[0]);
        // `str(s)` where s is already a runtime string: str() is idempotent -- the
        // buffer it names is the answer.
        if (v is Variable rvv && TryGetRuntimeStr(rvv.Name, out _)) return rvv;
        // `str(f())` where f's return handed back a literal sequence's fixed slots:
        // the repr of those slots, as a buffer.
        if (v is Variable svv && literalSequenceArrays.Contains(svv.Name))
            return new Variable(EmitSeqReprRuntimeStr("__str" + tempCounter++, svv.Name,
                tupleBoundNames.Contains(svv.Name) || IsTupleBound(svv.Name), expr), DataType.UINT8);
        // `str(x)` on a float: MicroPython's 7-significant-digit repr print()
        // streams, built into a runtime-string buffer by strfmt -- the same
        // lowering an f-string value would get, for one part.
        if (v is FloatConstant
            || (v is Variable sfv && sfv.Type == DataType.FLOAT)
            || (v is Temporary sft && sft.Type == DataType.FLOAT))
            return new Variable(EmitFloatReprRuntimeStr("__str" + tempCounter++, v, expr),
                DataType.UINT8);
        if (!(v is Constant c)) throw UserError("str() argument must be a compile-time constant integer", ArgAt(expr, 0));
        string decstr = c.Text ?? c.Value.ToString();
        if (!stringLiteralIds.ContainsKey(decstr))
        {
            stringLiteralIds[decstr] = nextStringId;
            stringIdToStr[nextStringId] = decstr;
            nextStringId++;
        }

        return new Constant(stringLiteralIds[decstr], decstr);
    }

    // repr(x): on a float it is str(x) -- PyMCU's float repr follows
    // MicroPython's 7-significant-digit policy, not CPython's shortest
    // round-trip one (see _f32_repr). Everything else keeps the table
    // refusal: no run-time object model exists to describe it.
    private Val EmitReprBuiltin(CallExpr expr)
    {
        if (expr.Args.Count == 1 && expr.Args[0] is not KeywordArgExpr
            && (expr.Args[0] is FloatLiteral
                || InferExprType(expr.Args[0]) == DataType.FLOAT
                || (expr.Args[0] is CallExpr { Callee: VariableExpr rc }
                    && functionReturnTypes.TryGetValue(ResolveCallee(rc.Name), out var rrt)
                    && rrt == "float")))
            return EmitStrBuiltin(expr);
        throw UserError($"repr() is a Python builtin that PyMCU does not provide: {UnsupportedBuiltins["repr"]}.", expr.Callee);
    }

    // pow(base, exp): folds compile-time integer operands in place; anything
    // else is real floating-point exponentiation, which the embedded runtime
    // helper lowers -- the same implementation `math.pow` expands to.
    private Val EmitPowBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 2) throw UserError("pow() expects exactly two arguments", expr.Callee);

        // The evaluator emits nothing, so asking it first leaves the run-time
        // path to visit each argument exactly once.
        if (TryFoldInt(expr.Args[0], out int @base) && TryFoldInt(expr.Args[1], out int exp))
        {
            if (exp < 0)
            {
                // 0 ** negative raises ZeroDivisionError in CPython, an int as much as a
                // float ("zero to a negative power", same message either width) -- a
                // genuine run-time exception, not something CPython refuses at compile
                // time, so this raises here too instead of folding or refusing (#p2avr-4).
                // Any other negative exponent on an int base is the PRE-EXISTING, separate
                // refusal below: CPython widens that to a float (`2 ** -1 == 0.5`), a
                // promotion this compiler does not implement for integer pow().
                if (@base == 0)
                {
                    EmitRaiseUnwind(new Constant(6 /* ZeroDivisionError */), unhandledInMain: true);
                    // A literal placeholder, not MakeTemp: see the identical note in
                    // LowerPow (Expr.cs) -- the raise never returns, so nothing downstream
                    // observes this value, but an ever-unwritten Temporary read-never-written
                    // flagged as if it could (ir-verify).
                    return new Constant(0);
                }
                throw UserError("pow() negative exponent not supported", ArgAt(expr, 1));
            }
            int res = 1;
            for (int k = 0; k < exp; ++k) res *= @base;
            return new Constant(res);
        }

        // Two integers are integer exponentiation, the same operation `**` is: pow(b, 5)
        // with a run-time b unrolls to multiplies. Forwarding them to the float routine
        // below handed its float parameters two integer bit patterns, and every run-time
        // pow() of integers printed 1.0.
        bool anyFloat = InferExprType(expr.Args[0]) == DataType.FLOAT
                        || InferExprType(expr.Args[1]) == DataType.FLOAT;
        if (!anyFloat)
            return LowerPow(VisitExpression(expr.Args[0]), VisitExpression(expr.Args[1]),
                expr.Args[1], "pow()");

        // Runtime float operands lower to a CALL on the __pymcu_powf subroutine -- one
        // shared software-float implementation, the same one `math.pow` delegates to. The
        // 0.0-to-a-negative-power guard runs first so the bare builtin gets CPython's own
        // ZeroDivisionError for that value instead of __pymcu_powf's math.pow-flavored
        // ValueError (see EmitPowZeroNegGuard, Expr.cs) -- math.pow() itself never reaches
        // here, it calls __pymcu_powf directly with no guard in front.
        if (functionParams.ContainsKey("__pymcu_powf"))
        {
            Val ToFloatArg(Val x)
            {
                if (x is FloatConstant) return x;
                if (x is Constant ci) return new FloatConstant(ci.Value);
                if (GetValType(x) == DataType.FLOAT) return x;
                Temporary ft = MakeTemp(DataType.FLOAT);
                Emit(new Copy(x, ft));
                return ft;
            }

            Val fBase = ToFloatArg(VisitExpression(expr.Args[0]));
            Val fExp = ToFloatArg(VisitExpression(expr.Args[1]));
            EmitPowZeroNegGuard(fBase, fExp);
            Temporary dst = MakeTemp(DataType.FLOAT);
            Emit(new Call("__pymcu_powf", new List<Val> { fBase, fExp }, dst));
            return dst;
        }

        throw UserError("pow() arguments must be compile-time constant integers", ArgAt(expr, 0));
    }

    // memoryview(buf): there is no buffer protocol to build an object with, but
    // the buffer already has fixed element storage, so the view is a compile-time
    // ALIAS of it -- the same Val the name alone would lower to. `mv =
    // memoryview(buf)` then binds through the ordinary name-alias machinery.
    // `memoryview(buf)[k]` unwraps in VisitIndex; `memoryview(buf)[k:]` as a
    // value is a writable window (ssd1306). unpack_from still adds the slice
    // start via NormalizeUnpackBuffer (PyMCU#361).
    private Val EmitMemoryviewBuiltin(CallExpr expr)
    {
        if (expr.Args.Count != 1)
            throw UserError("memoryview() expects exactly one argument", expr.Callee);
        Val inner = VisitExpression(expr.Args[0]);
        if (inner is Variable v
            && (arraySizes.ContainsKey(v.Name) || bytearrayParams.Contains(v.Name)
                || TryResolveArrayStorageKey(v.Name, out _)))
            return inner;
        throw UserError(
            "memoryview() wraps a fixed-size buffer (a bytearray or a fixed array); " +
            "there is no run-time buffer protocol to view anything else through", ArgAt(expr, 0));
    }

    /// <summary>
    /// <c>memoryview(buf)[a:b]</c> as a value: a named window of
    /// <paramref name="inner"/>, not a copy. Writes through the window
    /// reach the original bytes, which is why ssd1306 passes
    /// <c>memoryview(self.buffer)[1:]</c> into FrameBuffer.
    /// </summary>
    private Val EmitMemoryviewSliceView(Expression inner, IndexExpr at)
    {
        if (at.Index is not SliceExpr sl)
            throw UserError("memoryview slice expected", at);
        if (sl.Step != null)
            throw UserError(
                "a strided view of the buffer is not supported; use a plain "
                + "`buf[off:]` slice", sl.Step);

        string? storage = null;
        if (inner is VariableExpr ve)
        {
            string q = ResolveNameKey(ve.Name);
            if (TryResolveArrayStorageKey(q, out var sk)) storage = sk;
            else if (arraySizes.ContainsKey(q)) storage = q;
            else if (arraySizes.ContainsKey(ve.Name)) storage = ve.Name;
        }
        else if (inner is MemberAccessExpr mem && ResolveMemberArrayName(mem) is { } flat)
            storage = flat;

        if (storage == null)
        {
            Val v = VisitExpression(inner);
            if (v is Variable vv)
            {
                if (TryResolveArrayStorageKey(vv.Name, out var sk)) storage = sk;
                else if (arraySizes.ContainsKey(vv.Name)) storage = vv.Name;
            }
        }

        if (storage == null || !arraySizes.TryGetValue(storage, out int srcSize))
            throw UserError(
                "memoryview() wraps a fixed-size buffer (a bytearray or a fixed array); " +
                "there is no run-time buffer protocol to view anything else through", inner);

        int nestedOff = 0;
        string real = storage;
        while (arrayViewBase.TryGetValue(real, out var next))
        {
            nestedOff += arrayViewOffset.TryGetValue(real, out var vo) ? vo : 0;
            real = next;
        }

        int start = sl.Start != null ? EvaluateConstantExpr(sl.Start) : 0;
        int stop = sl.Stop != null ? EvaluateConstantExpr(sl.Stop) : srcSize;
        if (start < 0) start += srcSize;
        if (stop < 0) stop += srcSize;
        start = Math.Max(0, Math.Min(start, srcSize));
        stop = Math.Max(0, Math.Min(stop, srcSize));
        int count = Math.Max(0, stop - start);
        if (count == 0)
            throw UserError("a memoryview slice must cover at least one byte", sl);

        string viewName = "__view_" + tempCounter++;
        DataType elemDt = arrayElemTypes.TryGetValue(real, out var edt)
            ? edt : DataType.UINT8;
        arraySizes[viewName] = count;
        arrayElemTypes[viewName] = elemDt;
        arrayViewBase[viewName] = real;
        arrayViewOffset[viewName] = nestedOff + start;
        arraysWithVariableIndex.Add(viewName);
        return new Variable(viewName, elemDt);
    }

    /// <summary>
    /// Unwrap <c>bytes</c>/<c>memoryview</c> and a slice view, adding the slice start
    /// to <paramref name="baseOff"/>. A compile-time start folds into the offset
    /// (i2c_struct's <c>unpack_from(fmt, memoryview(self._buf)[1:])</c>, #361). A
    /// run-time start (<c>data[i*6:(i*6)+6]</c> in adafruit_sht31d) stays an add of
    /// that expression, because the format still decides how many bytes are read.
    /// </summary>
    private Expression NormalizeUnpackBuffer(Expression buf, ref Expression baseOff)
    {
        while (true)
        {
            // RFC 0008: `struct.unpack(fmt, f.read(n))` reads straight from the embedded
            // blob -- the read mints its view here and the field subscripts below land
            // on it as flash loads (or constants, while the position is compile-time).
            if (buf is CallExpr readBuf && TryRomfsReadCall(readBuf, out var romH))
                return new VariableExpr(EmitRomfsReadView(romH, readBuf));
            if (buf is CallExpr { Callee: VariableExpr { Name: "memoryview" or "bytes" or "bytearray" } } wrap
                && wrap.Args.Count == 1)
            {
                buf = wrap.Args[0];
                continue;
            }
            if (buf is IndexExpr { Index: SliceExpr sl } sliced)
            {
                if (sl.Step != null)
                    throw UserError(
                        "a strided view of the buffer is not supported; use a plain "
                        + "`buf[off:]` slice", sl.Step);
                if (sl.Start != null)
                {
                    if (TryFoldInt(sl.Start, out int start))
                    {
                        if (start < 0)
                            throw UserError("a negative view offset is not supported", sl.Start);
                        baseOff = AddByteOffset(baseOff, start);
                    }
                    else
                    {
                        baseOff = AddByteOffset(baseOff, sl.Start);
                    }
                }
                buf = sliced.Target;
                continue;
            }
            return buf;
        }
    }

    private static Expression AddByteOffset(Expression @base, int extra)
    {
        if (extra == 0) return @base;
        if (@base is IntegerLiteral il) return new IntegerLiteral(il.Value + extra);
        return new BinaryExpr(@base, PyMCU.Frontend.BinaryOp.Add, new IntegerLiteral(extra));
    }

    private static Expression AddByteOffset(Expression @base, Expression extra)
    {
        if (@base is IntegerLiteral { Value: 0 }) return extra;
        if (extra is IntegerLiteral { Value: 0 }) return @base;
        if (@base is IntegerLiteral a && extra is IntegerLiteral b)
            return new IntegerLiteral(a.Value + b.Value);
        return new BinaryExpr(@base, PyMCU.Frontend.BinaryOp.Add, extra);
    }

    private bool TryFoldInt(Expression e, out int value)
    {
        try { value = EvaluateConstantExpr(e); return true; }
        catch { value = 0; return false; }
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
        if (expr.Args.Count != 2) throw UserError("divmod() expects exactly two arguments", expr.Callee);
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
            // A literal is sized by its own value: every negative used to fold to
            // INT16, so -2147483648 counted as rank 1 and the a / -1 overflow
            // refusal below never ran on it -- the pair was sized int32 and the
            // quotient CPython gives as 2147483648 wrapped back to -2147483648.
            // AsLong, not Value: a uint32 literal's Value is its negative int32
            // pattern (0xFFFFFFFF reads -1), so Value sized it INT16 and folded
            // the division on -1 where the number is 4294967295.
            Constant c => c.AsLong < -32768 ? DataType.INT32
                          : c.AsLong < 0 ? DataType.INT16
                          : c.AsLong <= 0xFF ? DataType.UINT8
                          : c.AsLong <= 0xFFFF ? DataType.UINT16 : DataType.UINT32,
            // Was falling to the UINT8 default below, so a FloatConstant operand's width lost
            // to an integer operand's whenever the integer type was 4 bytes wide -- rt then
            // picked the integer type for a divmod() that mixes int and float (Python promotes
            // the pair to float, like `divmod(5, 2.5)`), and the float divisor was divided as
            // whatever bit pattern its four bytes happened to hold as an integer.
            FloatConstant => DataType.FLOAT,
            _ => DataType.UINT8,
        };
        DataType ta = ValType(aVal), tb = ValType(bVal);
        // The results are signed when either operand is signed, and sized for what a
        // division can produce, not just the wider operand: the quotient's worst case
        // is a / -1 = -a (one signed rank above a's own), and the remainder takes the
        // divisor's sign while |r| < |b| (an unsigned divisor needs one signed rank
        // above its own to hold b's unsigned maximum). Size alone picked the left
        // operand's type: divmod(uint8, int8) divided signed but stored into uint8
        // and printed (-4, -3) as (252, 253).
        static int Rank(DataType t) => t.SizeOf() <= 1 ? 0 : t.SizeOf() == 2 ? 1 : 2;
        static DataType SignedRank(int rank) => rank <= 0 ? DataType.INT8
            : rank == 1 ? DataType.INT16 : DataType.INT32;
        int needRank = WidthSeeds.IsSigned(ta) || WidthSeeds.IsSigned(tb)
            ? Math.Max(Rank(ta) + (WidthSeeds.IsSigned(tb) ? 1 : 0),
                       Rank(tb) + (WidthSeeds.IsSigned(tb) ? 0 : 1))
            : 0;
        DataType rt = ta is DataType.FLOAT || tb is DataType.FLOAT ? DataType.FLOAT
            : !WidthSeeds.IsSigned(ta) && !WidthSeeds.IsSigned(tb)
                ? (ta.SizeOf() >= tb.SizeOf() ? ta : tb)
                : SignedRank(Math.Min(2, needRank));
        if (rt == DataType.UNKNOWN || rt.SizeOf() == 0) rt = DataType.UINT8;

        // Dividing by a literal zero is a compile-time error whatever the dividend is, the
        // same rule the / // % operators apply (Expr.cs) -- for BOTH an int and a float
        // constant divisor. Checked once here, ahead of every path below, because those paths
        // used to build their Binary(FloorDiv)/Binary(Mod) nodes straight from aVal/bVal
        // without ever routing through the checked binary-expression codegen that / // %
        // normally goes through, so a float-constant zero divisor reached the emitted
        // division unchecked and an integer one only got the int-constant fold's own check.
        if ((bVal is Constant zc && zc.Value == 0) || (bVal is FloatConstant zfc && zfc.Value == 0.0))
            throw UserError("divmod(): division by zero", ArgAt(expr, 1));

        // The width formula caps at int32, the widest PyMCU has; the pair it would need
        // beyond that is refused rather than wrapped, the same test the arithmetic
        // operators apply through ValRange before promoting, except there is no wider
        // rank to promote to. The quotient overflows int32 only through a / -1 = -a: a
        // signed dividend at int32's floor, or an unsigned one past int32's ceiling,
        // each needing a divisor that can be -1. The remainder follows the divisor's
        // sign and |r| < |b|, so only a uint32 divisor can hold an r int32 cannot.
        if (needRank > 2 && rt == DataType.INT32)
        {
            var (aMin, aMax) = ValRange(aVal);
            var (bMin, bMax) = ValRange(bVal);
            // `bMin <= -1` alone is not proof: -1 must be reachable inside the
            // divisor's range, which a constant -2's [-2, -2] never contains.
            bool quotientOverflow = bMin <= -1 && -1 <= bMax
                && (aMin < -2147483647L || aMax > 2147483648L);
            bool remainderOverflow = !WidthSeeds.IsSigned(tb) && bMax > 2147483648L;
            if (quotientOverflow || remainderOverflow)
                throw UserError(
                    $"divmod({TypeName(ta)}, {TypeName(tb)}): the "
                    + (quotientOverflow ? "quotient" : "remainder")
                    + " can exceed int32 and PyMCU has no wider integer type to hold it; "
                    + "narrow the operands (e.g. keep the divisor from being -1) so the "
                    + "result is representable", expr.Callee);
        }

        // divmod() returns a 2-tuple in Python; PyMCU has no general runtime tuple VALUE (one
        // that can be passed around, stored in a field, ...), only fixed result SLOTS. Two
        // shapes read those slots as a tuple already: `q, r = divmod(a, b)` unpacks them
        // (pendingTupleCount == 2, checked by the caller), and `v = divmod(a, b)` /
        // `print(divmod(a, b))` ask for them through the same sentinel every other
        // multi-return call (`f()`, `obj.prop`) answers through (pendingTupleCount == -1,
        // read below via lastTupleResults -- see BindNamedTuple and EmitPrintArg's
        // CallExpr/MemberAccessExpr branch). Outside both shapes (an argument position, a
        // field write, ...) this used to fall through and silently answer the QUOTIENT
        // ALONE, contradicting the docs (LANGUAGE_ROADMAP.md / limitations.md both say
        // divmod "returns (quotient, remainder)"). Refusing names the real gap instead of
        // quietly answering a different, smaller value than Python's divmod() returns
        // (#p2avr-3).
        if (pendingTupleCount != 2 && pendingTupleCount != -1)
            throw UserError(
                "divmod() returns a 2-tuple (quotient, remainder); PyMCU supports unpacking it "
                + "into two targets (`q, r = divmod(a, b)`), binding it to one name "
                + "(`v = divmod(a, b)`), and printing it (`print(divmod(a, b))`) -- not passing "
                + "it as an argument, storing it in a field, or any other position", expr.Callee);

        if (aVal is Constant ca && bVal is Constant cb)
        {
            // Python's divmod floors: divmod(-17, 5) is (-4, 3). C#'s / and % truncate
            // toward zero and answered (-3, -2). AsLong on both sides: Value is the
            // raw int32 pattern, so an unsigned 0xFFFFFFFF folded as -1 and answered
            // (-1, 1) where CPython answers (2147483647, 1).
            long lq = ca.AsLong / cb.AsLong;
            if ((ca.AsLong ^ cb.AsLong) < 0 && lq * cb.AsLong != ca.AsLong) lq--;
            long lr = ca.AsLong - lq * cb.AsLong;
            // The pair is computed at int width; a constant answer PyMCU cannot hold
            // (a == INT32_MIN divided by -1) wraps here and must not become a wrong
            // value: refuse it, since no wider integer type exists to promote to.
            var (rtLo, rtHi) = RangeOfType(rt);
            if (lq < rtLo || lq > rtHi || lr < rtLo || lr > rtHi)
                throw UserError(
                    $"divmod({ca.AsLong}, {cb.AsLong}): the "
                    + (lq < rtLo || lq > rtHi ? $"quotient {lq}" : $"remainder {lr}")
                    + " does not fit in any PyMCU integer type", ArgAt(expr, 1));
            string bBase = string.IsNullOrEmpty(currentFunction) ? "main" : currentFunction;
            string qn = bBase + ".divmod_q" + tempCounter;
            string rn = bBase + ".divmod_r" + (tempCounter + 1);
            tempCounter += 2;
            // The operands' width, as the run-time division below stores it, made signed
            // when a result is negative: sizing each slot to its own value left the
            // remainder of divmod(-17, 5) a uint8 that a later divmod(17, -5) reused.
            // Sized on the folded numbers themselves -- the int32 patterns Value would
            // read for a quotient past 2^31 mislead every width ladder.
            long mag = Math.Max(lq < 0 ? -lq : lq, lr < 0 ? -lr : lr);
            bool anyNeg = lq < 0 || lr < 0;
            DataType ct = anyNeg
                ? (mag <= 0x7F ? DataType.INT8 : mag <= 0x7FFF ? DataType.INT16 : DataType.INT32)
                : (mag <= 0xFF ? DataType.UINT8 : mag <= 0xFFFF ? DataType.UINT16 : DataType.UINT32);
            if (rt.SizeOf() > ct.SizeOf())
                ct = !anyNeg || rt.IsSigned() ? rt
                    : rt.SizeOf() >= 4 ? DataType.INT32 : DataType.INT16;
            DataType qt = ct, rtt = ct;
            // Constant.Of keeps the Unsigned mark a quotient past int32 needs: a bare
            // `new Constant(-1)` stores the same bytes but folds back to a signed -1
            // on the next read instead of 4294967295.
            Constant qc = Constant.Of(lq)!;
            Constant rc = Constant.Of(lr)!;
            Emit(new Copy(qc, new Variable(qn, qt)));
            Emit(new Copy(rc, new Variable(rn, rtt)));
            // The unpack sizes each target from its slot; an unregistered slot sized
            // it uint8, and -4 printed as 252.
            variableTypes[qn] = qt;
            variableTypes[rn] = rtt;
            constantVariables[qn] = qc.Value;
            constantVariables[rn] = rc.Value;
            if (qc.Unsigned) unsignedConstNames.Add(qn);
            if (rc.Unsigned) unsignedConstNames.Add(rn);
            lastTupleResults = new List<string> { qn, rn };
            lastTupleResultBuffers = null;
            lastTupleResultLocalBuffers = null;
            return new NoneVal();
        }

        {
            string bBase = string.IsNullOrEmpty(currentFunction) ? "main" : currentFunction;
            string qn = bBase + ".divmod_q" + tempCounter;
            string rn = bBase + ".divmod_r" + (tempCounter + 1);
            tempCounter += 2;
            var qvar = new Variable(qn, rt);
            var rvar = new Variable(rn, rt);
            // Registered so the unpack targets take the division's width and sign: without
            // it `q, r = divmod(a, b)` stored an int16 quotient of -143 into a uint8 q.
            variableTypes[qn] = rt;
            variableTypes[rn] = rt;

            // A divisor the compiler cannot fold still has to raise, exactly as the operators
            // do -- the compile-time constant case above already rejected a literal zero.
            EmitDivModZeroCheck(bVal, isFloatOp: rt is DataType.FLOAT);

            // Emit the quotient and remainder as the same FloorDiv/Mod the // and %
            // operators produce, adjacent and sharing operands, so the AVR backend's
            // divmod fusion folds the pair into a single division call.
            Emit(new Binary(BinaryOp.FloorDiv, aVal, bVal, qvar));
            Emit(new Binary(BinaryOp.Mod, aVal, bVal, rvar));
            lastTupleResults = new List<string> { qn, rn };
            lastTupleResultBuffers = null;
            lastTupleResultLocalBuffers = null;
            return new NoneVal();
        }
    }

    // uint8(x)/int16(x)/… numeric cast: constant- and float-constant-fold, else a
    // width-changing Copy. `callee` is guaranteed to be a key of CastTypes.
    /// <summary>
    /// The text of a string argument when the compiler knows it: a literal, or a name bound to
    /// a compile-time string. Returns null for anything whose contents only exist at run time.
    /// </summary>
    private string? TryGetCompileTimeText(Expression arg)
    {
        if (arg is StringLiteral lit) return lit.Value;

        // A string held in a FIELD (`self.n`, `o.n`, `o.inner.sep`). The characters live in
        // flash exactly as they do for a plain name; only the key differs, since a field is
        // stored under the flattened `<instance>_<field>`. Without this the read fell through
        // to the numeric writer and printed the string's interned id: `print(o.n)` sent 256.
        //
        // The chain is walked to any depth. Matching only `<name>.<field>` left every deeper
        // read -- `o.inner.sep`, which is filed as `main.o_inner_sep` and whose text the
        // compiler has -- falling through to that same numeric writer, so two levels of
        // nesting printed 256 again while one level printed the string.
        if (arg is MemberAccessExpr ma)
        {
            if (IntrospectionTextOf(ma) is { } introspected) return introspected;
            (VariableExpr? recv, List<string> fields) = FieldChainOf(ma);
            if (recv == null) return null;

            // A bound exception is no instance: `e.errno` and every other member read
            // on it are answered by the exception paths, and resolving the name here
            // would throw the binding diagnostic before they get asked.
            if (TryGetExceptionBinding(recv.Name, out _)) return null;

            Val objVal = VisitExpression(recv);
            string bse = objVal is Variable ov ? ov.Name : recv.Name;
            bse = ResolveAlias(bse);

            foreach (var joiner in new[] { "_", "." })
            {
                string flat = bse;
                foreach (string f in fields) flat = ResolveAlias(flat + joiner + f);
                if (ResolveStrConstant(flat) is { } fieldText) return fieldText;
            }
            return null;
        }

        if (arg is not VariableExpr ve) return null;

        string key = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + ve.Name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + ve.Name : ve.Name);
        if ((ResolveStrConstant(key) ?? ResolveStrConstant(ve.Name)) is { } text)
            return text;

        // A parameter bound to a string literal without a const[str] annotation lands in
        // constantVariables as the interned string's id -- `def __init__(self,
        // font_name="font5x8.bin")` then `os.stat(font_name)` inside the body. Decode it
        // through the string table the way the argument binder does.
        if ((TryArgumentConstant(key, out int keyId) || TryArgumentConstant(ve.Name, out keyId))
            && stringIdToStr.TryGetValue(keyId, out var internedText))
            return internedText;
        return null;
    }

    /// <summary>
    /// True when the expression names a string whose characters live in RAM -- a buffer filled
    /// at run time, which no compile-time parse can read.
    /// </summary>
    private bool IsRuntimeStringExpr(Expression arg)
    {
        if (arg is not VariableExpr ve) return false;
        string key = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + ve.Name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + ve.Name : ve.Name);
        return runtimeStrVars.ContainsKey(key) || runtimeStrVars.ContainsKey(ve.Name);
    }

    /// <summary>
    /// Parses the text of a compile-time string into the cast's target type, the way Python's
    /// int()/float() would, and refuses text that is not a number instead of yielding one.
    /// </summary>
    private Val ParseTextAsNumber(string text, string callee, DataType dstType, ASTNode? at)
    {
        string t = text.Trim();
        if (dstType == DataType.FLOAT)
        {
            // float("inf") / float("nan") and their sign/case variants (P2 AVR gaps
            // bundle, item 5): CPython accepts "inf"/"infinity"/"nan", case-insensitive,
            // with an optional leading +/-, none of which double.TryParse's Float style
            // recognises on its own (it only takes the exact tokens "Infinity"/"NaN").
            // The target float32 already round-trips both correctly -- _f32_repr
            // (lib/src/pymcu/hal/uart_text.py) has printed "inf"/"nan" from the start, by
            // reading the exponent/mantissa bit pattern directly -- so this was a parsing
            // gap, not a representation one.
            string bare = t;
            bool neg = false;
            if (bare.Length > 0 && (bare[0] == '+' || bare[0] == '-'))
            {
                neg = bare[0] == '-';
                bare = bare[1..];
            }
            string lower = bare.ToLowerInvariant();
            if (lower is "inf" or "infinity")
                return new FloatConstant(neg ? float.NegativeInfinity : float.PositiveInfinity);
            if (lower == "nan")
                return new FloatConstant(float.NaN);

            if (!double.TryParse(t, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out double d))
                throw UserError($"{callee}(\"{text}\"): not a number", at);
            return new FloatConstant((float)d);
        }

        if (!long.TryParse(t, System.Globalization.NumberStyles.Integer,
                           System.Globalization.CultureInfo.InvariantCulture, out long n))
            throw UserError($"{callee}(\"{text}\"): not a whole number", at);

        long lo = dstType switch
        {
            DataType.UINT8 => 0, DataType.UINT16 => 0, DataType.UINT32 => 0,
            DataType.INT8 => sbyte.MinValue, DataType.INT16 => short.MinValue,
            _ => int.MinValue,
        };
        long hi = dstType switch
        {
            DataType.UINT8 => byte.MaxValue, DataType.UINT16 => ushort.MaxValue,
            DataType.UINT32 => uint.MaxValue,
            DataType.INT8 => sbyte.MaxValue, DataType.INT16 => short.MaxValue,
            _ => int.MaxValue,
        };
        if (n < lo || n > hi)
            throw UserError($"{callee}(\"{text}\"): {n} does not fit in {callee} ({lo}..{hi})", at);

        return new Constant((int)n);
    }

    /// <summary>
    /// The name in <paramref name="candidates"/> closest to <paramref name="wanted"/>, or ""
    /// when nothing is close enough to be worth suggesting. Used for a module's exports and for
    /// a builtin's keyword arguments, which are the same question asked of different sets.
    /// </summary>
    private static string NearestName(IReadOnlyCollection<string> candidates, string wanted)
    {
        string best = "";
        int bestDistance = int.MaxValue;
        foreach (var name in candidates)
        {
            int d = string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)
                ? 0
                : EditDistance(name, wanted);
            if (d < bestDistance) { bestDistance = d; best = name; }
        }

        // Two edits on a short name is already a different word; suggesting it would be noise.
        int allowed = Math.Max(2, wanted.Length / 3);
        return bestDistance <= allowed ? best : "";
    }

    /// <summary>
    /// Refuse a keyword argument a builtin does not have.
    ///
    /// Every other call already answers this. A user function, an @inline and a constructor all
    /// go through the parameter binder, which says `unknown keyword argument 'x' in call to
    /// 'f'`. `print` and `input` lower their own keywords instead, and neither loop had an else,
    /// so an unrecognised key was dropped without a word: `print(1, 2, foo=3)` emitted exactly
    /// `print(1, 2)`, and `input(maxlenn=8)` took the default 64-byte buffer instead of 8.
    ///
    /// The shape it hides in is a near miss on a keyword that exists, so both the accepted set
    /// and the suggestion are worth the words. CPython raises TypeError here.
    /// </summary>

    /// What each builtin does with a keyword argument: the names CPython takes for a
    /// POSITIONAL parameter, the ones PyMCU implements, and the ones CPython has that PyMCU
    /// does not. Anything absent from all three is a name CPython does not have either.
    ///
    /// Measured against CPython 3.14 rather than recalled, because the answer is not guessable:
    /// `len`, `abs`, `ord`, `chr`, `hex`, `bin`, `divmod`, `any`, `all`, `bool`, `float` and
    /// `range` are positional-ONLY, so `len(obj=xs)` and `range(stop=3)` are TypeErrors in
    /// CPython too, while `pow(base=2, exp=3)`, `str(object=1)` and `enumerate(xs, start=1)`
    /// all run.
    ///
    /// A builtin absent from this table is one with no keywords in either language, and its
    /// call is left exactly as it was.
    /// The names the dispatch in VisitCall answers itself. The keyword check applies to these
    /// and to the numeric casts, and to nothing else: everything else is a user function, a
    /// method or a constructor, and binds its keywords by its own parameter names.
    private static readonly HashSet<string> BuiltinDispatchNames = new(StringComparer.Ordinal)
    {
        "len", "abs", "min", "max", "ord", "chr", "sum", "any", "all", "bool",
        "hex", "bin", "oct", "str", "pow", "divmod", "print", "range", "enumerate", "zip",
    };

    private static readonly Dictionary<string, (string[] Positional, string[] Done, string[] NotYet)>
        BuiltinKeywords = new(StringComparer.Ordinal)
    {
        ["min"]       = ([], ["key"], ["default"]),
        ["max"]       = ([], ["key"], ["default"]),
        ["print"]     = ([], ["sep", "end"], ["file", "flush"]),
        ["pow"]       = (["base", "exp"], [], ["mod"]),
        ["str"]       = (["object"], [], ["encoding", "errors"]),
        ["enumerate"] = (["iterable"], ["start"], []),
        ["sum"]       = ([], [], ["start"]),
        ["zip"]       = ([], [], ["strict"]),
        ["int"]       = ([], [], ["base"]),
    };

    /// Answers a keyword argument to a builtin, and returns the call to dispatch.
    ///
    /// Every builtin used to let a keyword fall through to its own emitter, which then reported
    /// it in whatever vocabulary that emitter had: `abs(x=1)` as the internal "Unknown
    /// Expression type: KeywordArgExpr", `enumerate(xs, start=1)` as a complaint about the
    /// ITERABLE (a correct list literal), and `range(stop=3)` as an argument that is not a
    /// compile-time constant when the argument is the literal 3. One check here replaces a
    /// carve-out per builtin, and the ones that already had a carve-out (`print`, `min`, `max`)
    /// keep their own wording for the keywords they DO take.
    ///
    /// Three answers, and the third is why naming the keyword is not enough on its own. A name
    /// CPython does not have is the reader's typo. A name it DOES have is PyMCU's gap, and
    /// calling that "unknown" tells the reader their Python is wrong when it is not:
    /// `min(default=0)` and `print(flush=True)` were both answered that way.
    private CallExpr CheckBuiltinKeywords(CallExpr expr, string callee)
    {
        if (!expr.Args.Any(a => a is KeywordArgExpr)) return expr;

        // Only the builtins. A user function, a method or a constructor binds its keywords by
        // its own parameter names through ReorderCallArgs, and answering for those here would
        // refuse every keyword argument in the language.
        if (!BuiltinDispatchNames.Contains(callee) && !CastTypes.ContainsKey(callee)) return expr;

        if (!BuiltinKeywords.TryGetValue(callee, out var spec))
        {
            // A builtin with no keywords in either language, so any keyword is a typo and there
            // is nothing it could have meant.
            foreach (var a in expr.Args)
                if (a is KeywordArgExpr bad)
                    throw UserError(
                        $"unknown keyword argument '{bad.Key}' in call to '{callee}()': it takes "
                        + "no keyword arguments in Python either.", bad);
            return expr;
        }

        // A keyword naming a POSITIONAL parameter is the same call spelled with names, so it is
        // moved into place and the emitters below never learn the difference.
        var positional = new List<Expression>();
        var byName = new Dictionary<string, Expression>(StringComparer.Ordinal);
        var kept = new List<Expression>();
        foreach (var a in expr.Args)
        {
            if (a is not KeywordArgExpr kw) { positional.Add(a); continue; }
            if (spec.Done.Contains(kw.Key)) { kept.Add(kw); continue; }
            if (spec.NotYet.Contains(kw.Key))
                throw UserError(
                    $"'{kw.Key}' is a keyword argument of {callee}() in Python, and PyMCU does "
                    + $"not implement it yet. The rest of {callee}() works; only this argument "
                    + "does not.", kw);
            if (!spec.Positional.Contains(kw.Key))
            {
                // The names Python gives this builtin, not the ones PyMCU implements: the
                // reader is being told what they could have meant, and a name PyMCU does not
                // implement yet still answers for itself above.
                string[] taken = [.. spec.Positional, .. spec.Done, .. spec.NotYet];
                string near = NearestName(taken, kw.Key);
                throw UserError(
                    $"unknown keyword argument '{kw.Key}' in call to '{callee}()': the keywords "
                    + $"{callee}() has are " + string.Join(" and ", taken.Select(t => $"'{t}'"))
                    + (near.Length > 0 ? $". Did you mean '{near}'?" : "."),
                    kw);
            }

            if (!byName.TryAdd(kw.Key, kw.Value))
                throw UserError(
                    $"keyword argument '{kw.Key}' repeated in call to '{callee}()'", kw);
        }

        if (byName.Count == 0) return expr;

        var ordered = new List<Expression>(positional);
        for (int i = positional.Count; i < spec.Positional.Length; i++)
            if (byName.TryGetValue(spec.Positional[i], out var v)) ordered.Add(v);
        foreach (var name in spec.Positional.Take(positional.Count))
            if (byName.ContainsKey(name))
                throw UserError(
                    $"multiple values for argument '{name}' in call to '{callee}()'", expr.Callee);

        ordered.AddRange(kept);
        return new CallExpr(expr.Callee, ordered) { Line = expr.Line };
    }

    private void RefuseUnknownKeyword(
        string callee, string key, IReadOnlyList<string> accepted, Expression at)
    {
        string near = NearestName(accepted, key);
        throw UserError(
            $"unknown keyword argument '{key}' in call to '{callee}()': it takes "
            + string.Join(" and ", accepted)
            + (near.Length > 0 ? $". Did you mean '{near}'?" : "."),
            at);
    }

    /// <summary>
    /// The names a module exports, as this generator has them: the functions and classes
    /// registered under the module's mangled prefix.
    /// </summary>
    private HashSet<string> ExportedNames(string moduleName)
    {
        string prefix = moduleName.Replace('.', '_') + "_";
        var exported = new HashSet<string>(StringComparer.Ordinal);

        void Collect(IEnumerable<string> keys)
        {
            foreach (var k in keys)
            {
                if (!k.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string name = k.Substring(prefix.Length);
                int sep = name.IndexOf("___", StringComparison.Ordinal);
                if (sep > 0) name = name.Substring(0, sep);
                if (name.Length > 0 && !name.Contains('.')) exported.Add(name);
            }
        }

        Collect(inlineFunctions.Keys);
        Collect(functionParams.Keys);
        Collect(classNames);
        Collect(mutableGlobals.Keys);
        Collect(globals.Keys);

        // A class's methods are registered as `Class_method`, which is not a name anyone can
        // import; listing them would bury the exports the reader is looking for.
        exported.RemoveWhere(name =>
        {
            int cut = name.IndexOf('_');
            return cut > 0 && exported.Contains(name.Substring(0, cut));
        });

        return exported;
    }

    private static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    /// <summary>
    /// Rewrites the two pre-f-string ways of building a message into the f-string they mean:
    /// `"...".format(x)` and `"text " + str(x)`. Both are what a Python programmer reaches
    /// for first, both were refused, and the machinery to stream them already exists.
    /// </summary>
    private Expression RewriteStringBuilding(Expression arg)
    {
        if (arg is CallExpr { Callee: MemberAccessExpr { Member: "format", Object: StringLiteral fmt } } fmtCall)
            return DesugarStrFormat(fmt.Value, fmtCall);

        return TryFlattenStringConcat(arg) ?? arg;
    }

    /// <summary>
    /// `"a " + str(x) + " b"` as an f-string, or null when the expression is not a string
    /// concatenation of literals and str() calls -- in which case it is left alone and
    /// whatever diagnostic it would have produced still applies.
    /// </summary>
    private FStringExpr? TryFlattenStringConcat(Expression arg)
    {
        if (arg is not BinaryExpr { Op: Frontend.BinaryOp.Add }) return null;

        var parts = new List<FStringPart>();
        bool sawStrCall = false;

        bool Collect(Expression e)
        {
            switch (e)
            {
                case BinaryExpr { Op: Frontend.BinaryOp.Add } add:
                    return Collect(add.Left) && Collect(add.Right);
                case StringLiteral lit:
                    parts.Add(new FStringPart { IsExpr = false, Text = lit.Value });
                    return true;
                case CallExpr { Callee: VariableExpr { Name: "str" }, Args.Count: 1 } strCall:
                    sawStrCall = true;
                    parts.Add(new FStringPart { IsExpr = true, Expr = strCall.Args[0] });
                    return true;
                default:
                    // A bare name may hold a compile-time string, which concatenates fine
                    // today; anything else is not this shape.
                    if (e is VariableExpr v && StaticStringOf(v) is { } bound)
                    {
                        parts.Add(new FStringPart { IsExpr = false, Text = bound });
                        return true;
                    }
                    return false;
            }
        }

        if (!Collect(arg) || !sawStrCall) return null;
        return new FStringExpr(parts) { Line = arg.Line };
    }

    /// <summary>
    /// Rewrites `"text {} more".format(a, b)` into the equivalent f-string. Positional holes
    /// ({}, {0}) and format specs ({:02x}) map straight across; a named hole is a keyword
    /// argument, which has no f-string spelling here, and says so.
    /// </summary>
    private Expression DesugarStrFormat(string format, CallExpr call)
    {
        // `"...{}...{}...".format(*values)`: the starred argument is the positional list --
        // splice it through the same compile-time expansion every other call gets, so the
        // placeholders count the elements, not the star. `**map` arrives here as the keyword
        // argument the check below already refuses.
        var args = call.Args;
        if (args.Any(a => a is StarArgExpr or DoubleStarArgExpr))
            args = SpliceVariadicArgs(args);
        foreach (var a in args)
            if (a is KeywordArgExpr)
                throw UserError(
                    "str.format() with keyword arguments is not supported; use an f-string, "
                    + "e.g. f\"val {x}\"", call.Callee);

        var parts = new List<FStringPart>();
        var text = new System.Text.StringBuilder();
        int nextArg = 0;

        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c == '{' && i + 1 < format.Length && format[i + 1] == '{') { text.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < format.Length && format[i + 1] == '}') { text.Append('}'); i++; continue; }
            if (c != '{') { text.Append(c); continue; }

            int close = format.IndexOf('}', i + 1);
            if (close < 0)
                throw UserError($"str.format(): unmatched '{{' in \"{format}\"", call.Callee);

            string hole = format.Substring(i + 1, close - i - 1);
            i = close;

            string spec = "";
            int colon = hole.IndexOf(':');
            if (colon >= 0) { spec = hole[(colon + 1)..]; hole = hole[..colon]; }

            int argIndex;
            if (hole.Length == 0) argIndex = nextArg++;
            else if (int.TryParse(hole, out int explicitIndex)) argIndex = explicitIndex;
            else
                throw UserError(
                    $"str.format(): named field '{{{hole}}}' is not supported; use an f-string, "
                    + $"e.g. f\"...{{{hole}}}...\"", call.Callee);

            if (argIndex < 0 || argIndex >= args.Count)
                throw UserError(
                    $"str.format(): \"{format}\" needs argument {argIndex}, but "
                    + $"{args.Count} {(args.Count == 1 ? "was" : "were")} given", call.Callee);

            if (text.Length > 0)
            {
                parts.Add(new FStringPart { IsExpr = false, Text = text.ToString() });
                text.Clear();
            }

            parts.Add(new FStringPart { IsExpr = true, Expr = args[argIndex], FormatSpec = spec });
        }

        if (text.Length > 0) parts.Add(new FStringPart { IsExpr = false, Text = text.ToString() });

        return new FStringExpr(parts) { Line = call.Line };
    }

    private Val EmitNumericCastBuiltin(CallExpr expr, string callee)
    {
        DataType dstType = CastTypes[callee];
        if (expr.Args.Count != 1) throw UserError(callee + "() expects exactly one argument", expr.Callee);

        // `uint8(input("n: "))` is the one-line way to read a number, and it is exactly the two
        // statements the user would otherwise write -- both of which already work. Only the
        // composition failed, reported as "call to undefined function 'input'", which sends the
        // reader hunting for an import while the same call one line up compiles. Desugar it into
        // the buffer declaration plus the cast.
        if (expr.Args[0] is CallExpr { Callee: VariableExpr { Name: "input" } } inputCall)
        {
            string bufName = "__input" + (++inputDesugarId);
            VisitStatement(new VarDecl(bufName, "bytearray", inputCall) { Line = expr.Line });
            return EmitNumericCastBuiltin(
                new CallExpr(expr.Callee, new List<Expression> { new VariableExpr(bufName) }) { Line = expr.Line },
                callee);
        }
        // A string argument used to fold to its flash string-id, or to a plain zero when it
        // came through a variable -- `s: str = "42"; uint8(s)` printed 0 and said nothing.
        // A string whose text is known at compile time is parsed here, which is what Python
        // does; anything else is refused by name rather than becoming a number nobody wrote.
        if (TryGetCompileTimeText(expr.Args[0]) is { } text)
            return ParseTextAsNumber(text, callee, dstType, ArgAt(expr, 0));
        if (expr.Args[0] is FStringExpr)
            throw UserError(
                $"{callee}() cannot convert an f-string: its text is only assembled at run time. " +
                "Convert the value before formatting it.", ArgAt(expr, 0));
        if (IsRuntimeStringExpr(expr.Args[0]))
            throw UserError(
                $"{callee}() cannot parse a string that is only known at run time. " +
                "PyMCU has no run-time string-to-number conversion; read the digits and " +
                "accumulate them (d = c - 48), or keep the value numeric end to end.", ArgAt(expr, 0));
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
            if (dstType == DataType.FLOAT) return new FloatConstant(c.AsLong);
            int val = c.Value;
            switch (dstType)
            {
                case DataType.UINT8: val = (byte)val; break;
                case DataType.UINT16: val = (ushort)val; break;
                case DataType.INT8: val = (sbyte)val; break;
                case DataType.INT16: val = (short)val; break;
                // uint32 of a pattern that reads negative is the number past int32
                // (`uint32(-1)` is 4294967295); int32 of one is the negative reading.
                case DataType.UINT32: return new Constant(val, Unsigned: val < 0);
                case DataType.INT32: return new Constant(val);
            }

            return new Constant(val);
        }

        // float(float_const) is the identity.
        if (v is FloatConstant fcId && dstType == DataType.FLOAT) return fcId;

        // Float constant to integer cast: fold at compile time (e.g. uint16(0.5 * 1000) -> 500).
        if (v is FloatConstant fc && dstType != DataType.FLOAT)
        {
            // `(int)fc.Value` is UNSPECIFIED by the C# spec for a double outside int's range --
            // `int(1e10)` measured as -1 on this host (a JIT/platform truncation artifact, not
            // a chosen value), and nothing says another host answers the same -1. CPython's
            // int(1e10) is the exact 10000000000, which no PyMCU integer type (32-bit widest)
            // can hold either way, so there is no CPython value to match here; the honest
            // answer is the compile-time refusal every other constant that does not fit its
            // type already gets (Fold(), above), not a silently different platform-dependent
            // bit pattern each time the compiler itself is rebuilt on a different host.
            if (fc.Value < int.MinValue || fc.Value > uint.MaxValue)
                throw UserError(
                    $"{callee}({fc.Value}): the value does not fit any PyMCU integer type "
                    + "(the widest is 32-bit); the compiler cannot silently choose which bits "
                    + "to keep", ArgAt(expr, 0));
            int val = fc.Value > int.MaxValue ? unchecked((int)(uint)fc.Value) : (int)fc.Value;
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
        if (expr.Args.Count != 2) throw UserError("bitcast() expects exactly two arguments: bitcast(type, value)", expr.Callee);
        string typeName = (expr.Args[0] as VariableExpr)?.Name
            ?? throw UserError("bitcast() first argument must be a type name", ArgAt(expr, 0));
        DataType bcDstType;
        if (typeName == "float")
            bcDstType = DataType.FLOAT;
        else if (!CastTypes.TryGetValue(typeName, out bcDstType))
            throw UserError($"bitcast(): unknown type '{typeName}'", ArgAt(expr, 0));

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
        if (expr.Args.Count != 1) throw UserError("gc_alloc() expects exactly one argument: gc_alloc(size)", expr.Callee);
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
        if (expr.Args.Count < 1) throw UserError("asm() requires at least one string argument", expr.Callee);

        string? code = null;
        if (expr.Args[0] is StringLiteral str2)
            code = str2.Value;
        else if (expr.Args[0] is FStringExpr fstr2)
        {
            var resolved = VisitFStringExpr(fstr2);
            if (resolved is Constant c2 && stringIdToStr.TryGetValue(c2.Value, out var s2))
                code = s2;
            else
                throw UserError("asm() f-string did not resolve to a string constant", ArgAt(expr, 0));
        }
        else if (expr.Args[0] is VariableExpr ve2)
            throw UserError($"asm() argument must be a string literal, got variable '{ve2.Name}'", ArgAt(expr, 0));
        else
            throw UserError("asm() argument must be a compile-time string literal", ArgAt(expr, 0));

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

    /// The writer that walks a flash string from a pointer held at RUN TIME.
    ///
    /// Not the same answer as ResolveWriteStrFn, and the difference is the whole reason this
    /// exists: that one prefers `print_str`, which is `@inline` and binds its `const[str]`
    /// parameter to a literal at compile time, so it has no symbol and no pointer. The
    /// exception message is an address decided by whichever raise ran, so it needs the shared
    /// subroutine `uart_write_str`, which already reads flash through a register pair (#369).
    /// <summary>
    /// Whether a name is a string PARAMETER of the subroutine being compiled (`str` or
    /// `const[str]`), which holds the flash address of its text in a pointer-wide slot.
    /// Asked of the slot the signature laid out, not of the annotation text: a bound
    /// method's synthesized body has no declared list of its own.
    /// </summary>
    // Whether parameter `idx` of `callee` is a string slot of a real subroutine: it holds
    // the flash address of its text (ParamStorageType), not the text's interned id.
    private bool IsStrParamSlot(string callee, int idx) => strParamSlots.Contains(callee + "#" + idx);

    /// <summary>
    /// The argument a string slot receives: the flash address of a text known at compile time,
    /// or another string slot's address passed on. Nothing else fits it -- a string built at
    /// run time lives in RAM and None has no address -- and either one reached the callee as a
    /// number it then printed, so both are refused where the call is written.
    /// </summary>
    private Val BindStrParamArg(string callee, string param, Expression? argExpr, Val argVal,
                                Expression site)
    {
        string shown = CalleeDisplayName(callee);
        if (argVal is FlashStrAddr) return argVal;
        if (argVal is Variable passed && flashStrPtrVars.Contains(passed.Name)) return argVal;
        if (argExpr != null && argVal is not NoneVal && StaticStringOf(argExpr) is { } text)
            return new FlashStrAddr(InternStringAsFlash(text));
        throw UserError(argVal is NoneVal
            ? $"'{param}' of '{shown}' is a str parameter of a subroutine: it holds the flash "
              + $"address of a string, and None has none. Make '{shown}' @inline, or pass a string"
            : $"'{param}' of '{shown}' is a str parameter of a subroutine: it holds the flash "
              + "address of a string known when the program is compiled, and this argument is "
              + $"built at run time. Make '{shown}' @inline, or pass a string literal or constant",
            argExpr ?? site);
    }

    // A parameter that takes the ADDRESS of some bytes: a string built at run time is exactly
    // that (`_fs_text(buf: bytearray, ...)` fills the buffer an f-string assignment builds).
    private bool IsBufferParam(string callee, int idx, string param) =>
        bytearrayParams.Contains(callee + "." + param)
        || (functionParamDeclared.TryGetValue(callee, out var declared) && idx >= 0
            && idx < declared.Count
            && (declared[idx] is "bytearray" or "bytes" or "memoryview" or "ptr"
                || declared[idx].StartsWith("ptr[", StringComparison.Ordinal)));

    // The name a diagnostic shows for a callee: a method's own, not the `Class_method` key.
    private string CalleeDisplayName(string callee)
    {
        foreach (var cls in classNames)
            if (callee.StartsWith(cls + "_", StringComparison.Ordinal)
                && functionParams.ContainsKey(callee))
                return cls + "." + callee[(cls.Length + 1)..];
        return callee;
    }

    // A name holding a string built at run time (`s = f"..."`, a RAM buffer).
    private bool IsRuntimeStrArgument(Expression? e) =>
        e is VariableExpr ve && TryGetRuntimeStr(ve.Name, out _);

    // A string handed to a parameter that is not a string slot reached the callee as a number
    // (the text's address or interned id) and was printed or compared as one.
    private void RefuseTextForNonStrParam(string callee, string param, Expression? arg,
                                          Expression site)
    {
        string shown = CalleeDisplayName(callee);
        throw UserError(IsRuntimeStrArgument(arg)
            ? $"'{param}' of '{shown}' receives a string built at run time, which lives in RAM, "
              + "and a subroutine parameter holds only a string known when the program is "
              + $"compiled (by its flash address). Make '{shown}' @inline"
            : $"'{param}' of '{shown}' receives a string here, and it is not a str parameter "
              + "(another call passes it something else, or it is annotated otherwise), so the "
              + "subroutine would read the text's address as a number. Annotate it `str` and "
              + $"pass only strings, or make '{shown}' @inline", arg ?? site);
    }

    private bool IsConstStrParameter(string name) =>
        !string.IsNullOrEmpty(currentFunction) && string.IsNullOrEmpty(currentInlinePrefix)
        && flashStrPtrVars.Contains(currentFunction + "." + name);

    private string ResolveRuntimeWriteStrFn()
    {
        string fn = ResolveCallee("uart_write_str");
        if (fn != "uart_write_str") return fn;
        foreach (var name in functionParams.Keys)
            if (name.EndsWith("uart_write_str", StringComparison.Ordinal)) return name;
        foreach (var name in functionReturnTypes.Keys)
            if (name.EndsWith("uart_write_str", StringComparison.Ordinal)) return name;
        return fn;
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

    // The run-time-decided string behind `mod.name` or `obj.field`, resolved under the keys a
    // member is filed by: the module-mangled name, and the flattened `<instance>_<field>` the
    // receiver's binding gives (the same two spellings TryGetCompileTimeText reads).
    private bool TryGetMultiStrMember(MemberAccessExpr ma, out string key,
                                      out List<string> values, out bool materialized)
    {
        key = "";
        values = new List<string>();
        materialized = false;
        if (ma.Object is not VariableExpr recv) return false;

        var candidates = new List<string>();
        string moduleBase = StillNamesAModule(recv.Name)
                            && TryImportedAlias(recv.Name, out var realMod) && realMod != null
            ? realMod : recv.Name;
        candidates.Add(moduleBase + "_" + ma.Member);

        if (!StillNamesAModule(recv.Name))
        {
            Val objVal = VisitExpression(recv);
            string bse = objVal is Variable ov ? ov.Name : recv.Name;
            while (variableAliases.TryGetValue(bse, out var alias) && alias != null) bse = alias;
            candidates.Add(bse + "_" + ma.Member);
            candidates.Add(bse + "." + ma.Member);
        }

        foreach (var candidate in candidates)
        {
            if (strConstantVariables.ContainsKey(candidate)) return false;
            if (!multiStrVariables.TryGetValue(candidate, out var vals)) continue;
            key = candidate;
            values = vals;
            materialized = multiStrCandidates.ContainsKey(candidate);
            return true;
        }

        return false;
    }

    // Writes a string whose text is decided at run time: the name holds the interned id, so
    // this is one comparison per text it can hold, each arm a write_str of a literal. The texts
    // stay in flash where a folded write would have left them -- nothing is copied into RAM and
    // nothing is formatted. Returns false when the name is not such a string.
    /// <summary>
    /// The texts of `d[k]` when d is a dict whose values are all strings and k is not a
    /// literal; null otherwise. The lookup yields the chosen text's interned id.
    /// </summary>
    private List<string>? DictOfStringsLookup(Expression e)
    {
        Expression? index;
        Frontend.DictExpr dict;
        string? fallback = null;
        switch (e)
        {
            case IndexExpr { Target: VariableExpr or MemberAccessExpr, Index: not SliceExpr } dix
                when TryGetDictFor(dix.Target, out dict!):
                index = dix.Index;
                break;
            // `d.get(k, "fallback")` yields one of the dict's texts or the default's. A
            // non-string default makes the result a str|int union this dispatch cannot
            // stream, so only the all-string shape is claimed here.
            case CallExpr { Callee: MemberAccessExpr { Member: "get" } gm, Args.Count: 2 } dget
                when TryGetDictFor(gm.Object, out dict!)
                    && StaticStringOf(dget.Args[1]) is { } df:
                index = dget.Args[0];
                fallback = df;
                break;
            default:
                return null;
        }
        if (dict.Entries.Count == 0) return null;
        if (!dict.Entries.All(en => StaticStringOf(en.Value) != null)) return null;
        if (TryEvalConstElement(index, out _)) return null;
        return dict.Entries.Select(en => StaticStringOf(en.Value)!)
                   .Concat(fallback != null ? new[] { fallback } : Enumerable.Empty<string>())
                   .Distinct().ToList();
    }

    private bool TryEmitMultiStrStream(string writeStrFn, Expression arg)
    {
        // `print("mono" if k == 0 else "none")`. A conditional EXPRESSION never reaches the
        // if/else merge that records a name's several texts: it is lowered as a value, and the
        // value of a string literal on this target is its interned id, so a number arrived at
        // the writer and the numeric branch took it -- 260 and 261 where "mono" and "none"
        // were meant, on a real Uno (#378). The arms' texts are known; only WHICH one is
        // decided at run time, exactly as for a name holding several texts, so it is written
        // the same way: the condition once, and a write_str of a literal on each side. Here
        // rather than in print's own ladder, so uart.write_str and println get it too.
        if (arg is TernaryExpr tern
            && StaticStringOf(tern.TrueVal) is { } ternTrue
            && StaticStringOf(tern.FalseVal) is { } ternFalse)
        {
            string ternElse = MakeLabel(), ternEnd = MakeLabel();
            int ternCond = EmitOptimizedConditionalJump(tern.Condition, ternElse, false);
            if (ternCond == 0)
            {
                Val ternVal = VisitExpression(tern.Condition);
                if (ternVal is Constant ternConst)
                {
                    ternCond = ternConst.Value != 0 ? 2 : -1;
                    if (ternCond == -1) Emit(new Jump(ternElse));
                }
                else
                {
                    Emit(new JumpIfZero(ternVal, ternElse));
                    ternCond = 1;
                }
            }
            if (ternCond == 2)
            {
                // Decided true: only the taken arm is lowered. The labels are still defined --
                // a condition ruled true may have jumped to the else label on a path it then
                // ruled out, and that jump needs somewhere to land.
                EmitStreamStr(writeStrFn, ternTrue);
                Emit(new Label(ternElse));
                Emit(new Label(ternEnd));
                return true;
            }
            if (ternCond != -1)
            {
                EmitStreamStr(writeStrFn, ternTrue);
                Emit(new Jump(ternEnd));
            }
            Emit(new Label(ternElse));
            EmitStreamStr(writeStrFn, ternFalse);
            Emit(new Label(ternEnd));
            return true;
        }

        // `d[k]` with a run-time k over a dict whose values are all strings: the lookup hands
        // back the chosen text's interned id, and the number writer printed it (258 for "CD").
        // The texts are known; only which one is decided at run time, so the id picks the
        // write the same way a name holding several texts does.
        if (DictOfStringsLookup(arg) is { } texts)
        {
            Val picked = VisitExpression(arg);
            if (picked is Constant pk && texts.FirstOrDefault(t => StringIdOf(t) == pk.Value) is { } known)
            {
                EmitStreamStr(writeStrFn, known);
                return true;
            }
            string dEnd = MakeLabel();
            foreach (var text in texts)
            {
                string dNext = MakeLabel();
                Emit(new JumpIfNotEqual(picked, new Constant(StringIdOf(text)), dNext));
                EmitStreamStr(writeStrFn, text);
                Emit(new Jump(dEnd));
                Emit(new Label(dNext));
            }
            Emit(new Label(dEnd));
            return true;
        }

        string shown;
        string key;
        List<string> values;
        bool materialized;
        switch (arg)
        {
            case VariableExpr ve when TryGetMultiStr(ve.Name, out key, out values, out materialized):
                shown = ve.Name;
                break;

            // `mod.state` -- a str global of an imported module, and `o.n` -- a field. Both are
            // filed under a key of their own, so the plain-name lookup above never sees them.
            case MemberAccessExpr ma when TryGetMultiStrMember(ma, out key, out values, out materialized):
                shown = FormatMemberTarget(ma);
                break;

            default:
                return false;
        }

        if (!materialized) throw MultiStrUseError(shown, values, arg);

        var slot = new Variable(key, DataType.UINT16);
        string endLabel = MakeLabel();
        foreach (var text in values)
        {
            string nextLabel = MakeLabel();
            Emit(new JumpIfNotEqual(slot, new Constant(StringIdOf(text)), nextLabel));
            EmitStreamStr(writeStrFn, text);
            Emit(new Jump(endLabel));
            Emit(new Label(nextLabel));
        }

        Emit(new Label(endLabel));
        return true;
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
            $"f\"{{{ve.Name}.<field>}}\" or a method that returns a number.", e);
    }

    // A bool value in the Python sense: a True/False literal, a comparison, `not`, a truth
    // builtin, `and`/`or` of bools, a call whose every return is one, or a name bound only to
    // bools everywhere it can be seen. A name that also receives an integer anywhere stays
    // a number, so a bool never misreports an integer flowing through the same name.
    private bool IsBoolExpr(Expression e) => e switch
    {
        BooleanLiteral => true,
        VariableExpr ve => IsBoolName(ve.Name),
        // `and`/`or` hand back one of their operands, so the result is a bool exactly when
        // both are: `print((a > b) or (a > 1))` printed 1 (#386).
        BinaryExpr { Op: Frontend.BinaryOp.And or Frontend.BinaryOp.Or } lo =>
            IsBoolExpr(lo.Left) && IsBoolExpr(lo.Right),
        // Comparisons are bools too: `print(dev.i2c is not None)` went to the decimal
        // writer and sent 1/0 where CPython and CircuitPython spell True/False.
        BinaryExpr be => be.Op is Frontend.BinaryOp.Equal or Frontend.BinaryOp.NotEqual
            or Frontend.BinaryOp.Less or Frontend.BinaryOp.LessEq
            or Frontend.BinaryOp.Greater or Frontend.BinaryOp.GreaterEq
            or Frontend.BinaryOp.Is or Frontend.BinaryOp.IsNot
            or Frontend.BinaryOp.In or Frontend.BinaryOp.NotIn,
        UnaryExpr { Op: Frontend.UnaryOp.Not } => true,
        // A call or property whose declared return is bool -- `print(d.value)`
        // where `value` is `@property -> bool`, `print(pred())`, the same
        // spelling CPython gives the result.
        CallExpr { Callee: var bc } => CalleeDeclaresBool(bc),
        MemberAccessExpr bm => PropertyReadDeclaresBool(bm),
        _ => false,
    };

    private bool CalleeDeclaresBool(Expression callee) => callee switch
    {
        VariableExpr cv =>
            // `isinstance` is a builtin, not a def, so it has no functionReturnTypes
            // entry -- but it always yields a Python bool.
            (IsTruthBuiltin(cv.Name) && !functionParams.ContainsKey(ResolveCallee(cv.Name)))
            || functionReturnTypes.GetValueOrDefault(ResolveCallee(cv.Name)) == "bool"
            || boolReturningFunctions.Contains(ResolveCallee(cv.Name)),
        // A MODULE function (`math.isnan(x)`), mangled the same way VisitCallCore resolves
        // the call itself (`mangledMod + "_" + member`) -- without this, `print(math.
        // isnan(x))` sent "1"/"0" where CPython and every other bool spells True/False,
        // because a module call never matched either the plain-name or the instance-method
        // branch below (#p2avr-5).
        MemberAccessExpr cm when cm.Object is VariableExpr cmMod
            && TryImportedAlias(cmMod.Name, out var cmRealMod) && cmRealMod != null =>
            functionReturnTypes.GetValueOrDefault(cmRealMod.Replace('.', '_') + "_" + cm.Member) == "bool"
            || boolReturningFunctions.Contains(cmRealMod.Replace('.', '_') + "_" + cm.Member),
        MemberAccessExpr cm =>
            cm.Object is VariableExpr cobj
            && InstanceClassOfName(cobj.Name) is { } ccls
            && ResolveMROMethod(ccls, cm.Member) is { } mcls
            && (functionReturnTypes.GetValueOrDefault(mcls + "_" + cm.Member) == "bool"
                || boolReturningFunctions.Contains(mcls + "_" + cm.Member)),
        _ => false,
    };

    private bool PropertyReadDeclaresBool(MemberAccessExpr mem)
        => mem.Object is VariableExpr recv
           && InstanceClassOfName(recv.Name) is { } cls
           && (MemberFieldDeclaresBool(cls, mem.Member)
               || (ResolveMROPropertyClass(cls, mem.Member) is { } propCls
                   && (functionReturnTypes.GetValueOrDefault(propCls + "_" + mem.Member) == "bool"
                       || GetterReturnsOnlyBoolEvidence(propCls, mem.Member))));

    // `ain.is_differential` is a FIELD, not a @property -- `self.is_differential =
    // negative_pin is not None` typed it bool at layout, which is the same promise a
    // `-> bool` getter makes about what the member yields.
    private bool MemberFieldDeclaresBool(string cls, string member)
    {
        string? current = cls;
        for (int depth = 0; current != null && depth < 32; depth++)
        {
            if (classFieldLayout.TryGetValue(current, out var layout)
                && layout.Any(f => f.Field == member && f.Type == "bool"))
                return true;
            if (!classBasePrefixes.TryGetValue(current, out var parentPrefix)
                || string.IsNullOrEmpty(parentPrefix))
                break;
            current = parentPrefix!.EndsWith("_") ? parentPrefix[..^1] : parentPrefix;
        }
        return false;
    }

    // An unannotated getter declares "" -- its body is the evidence. Every value-return
    // must be bool-shaped (literal, comparison, `not`, `bool(...)`, or a bool field read)
    // for the property to print as Python's True/False; a single unrecognized return is
    // enough to stay numeric, matching the conservative declared-type check above.
    private bool GetterReturnsOnlyBoolEvidence(string cls, string member)
    {
        if (!inlineFunctions.TryGetValue(cls + "_" + member, out var fn)) return false;
        bool sawValue = false;
        foreach (var s in TypeInference.WalkStatements(fn.Body.Statements))
        {
            if (s is not ReturnStmt { Value: { } rv }) continue;
            if (!ReturnExprDeclaresBool(rv, cls, member)) return false;
            sawValue = true;
        }
        return sawValue;
    }

    private bool ReturnExprDeclaresBool(Expression e, string cls, string member) => e switch
    {
        BooleanLiteral => true,
        UnaryExpr { Op: Frontend.UnaryOp.Not } => true,
        BinaryExpr { Op: var op } => op is Frontend.BinaryOp.Equal or Frontend.BinaryOp.NotEqual
            or Frontend.BinaryOp.Less or Frontend.BinaryOp.Greater or Frontend.BinaryOp.LessEq
            or Frontend.BinaryOp.GreaterEq or Frontend.BinaryOp.Is or Frontend.BinaryOp.IsNot
            or Frontend.BinaryOp.In or Frontend.BinaryOp.NotIn,
        CallExpr { Callee: VariableExpr { Name: "bool" } } => true,
        MemberAccessExpr { Member: var fm, Object: VariableExpr { Name: "self" } } =>
            classFieldLayout.TryGetValue(cls, out var fl)
            && fl.Any(f => f.Field == fm && f.Type == "bool"),
        VariableExpr { Name: var vn } => IsBoolNameIn(cls + "_" + member, vn),
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

    /// <summary>
    /// RFC 0009 section 8: print()/f-string interpolation of a live tagged Optional --
    /// the one read that can represent BOTH outcomes. The tag picks the text CPython
    /// writes: each real member reads the payload at ITS width through the ordinary
    /// numeric/float/bool writers, and the None member writes the literal "None".
    /// Every other position (arithmetic, an index, a comparison other than `is`,
    /// a non-Optional parameter) keeps the located refusal.
    /// </summary>
    private void EmitOptionalStream(string writeStrFn, string floatFn, Val payload,
        Val tag, List<string> members)
    {
        int noneIdx = NoneIndex(members);
        string optPrinted = MakeLabel();
        for (int mi = 0; mi < members.Count; ++mi)
        {
            if (mi == noneIdx) continue;
            string notMember = MakeLabel();
            Emit(new JumpIfNotEqual(tag, new Constant(mi), notMember));
            if (members[mi] == "bool")
                EmitStreamBool(writeStrFn,
                    new PreEvaluatedExpr(MemberRead(payload, mi, members), null));
            else
                EmitStreamVal(floatFn, MemberRead(payload, mi, members));
            Emit(new Jump(optPrinted));
            Emit(new Label(notMember));
        }
        if (noneIdx >= 0)
            EmitStreamStr(writeStrFn, "None");
        Emit(new Label(optPrinted));
    }

    /// The operand half of the Optional print dispatch: a bare name or a field read
    /// carries the tag beside the payload, so <see cref="LiveOptionalTag"/> finds it
    /// without lowering the read. <paramref name="beforeEmit"/> is the f-string's
    /// pending-text flush: it runs after the payload read lowers and before the
    /// writes, so buffered literal text still precedes this part's bytes on the
    /// wire. A name proven None still prints "None" -- its payload bytes are stale.
    private bool TryEmitOptionalStreamOperand(string writeStrFn, string floatFn,
        Expression arg, Action? beforeEmit = null)
    {
        if (arg is not (VariableExpr or MemberAccessExpr)) return false;
        if (LiveOptionalTag(arg) is { } live)
        {
            Val optPayload = EvalOptionalCarry(arg);
            beforeEmit?.Invoke();
            EmitOptionalStream(writeStrFn, floatFn, optPayload, live.tag, live.members);
            return true;
        }
        if (OptionalKeyOfExpr(arg) is { } noneKey && noneValuedNames.Contains(noneKey))
        {
            beforeEmit?.Invoke();
            EmitStreamStr(writeStrFn, "None");
            return true;
        }
        return false;
    }

    /// The already-evaluated half of the dispatch: a call or property result rides in
    /// a temp whose tag slot sits beside it (MarkOptional filed it when the callee's
    /// return union made the result taggable).
    private bool TryEmitOptionalStreamVal(string writeStrFn, string floatFn, Val v)
    {
        if (ValNameOf(v) is not { } vn || TagOfVal(v) is not { } vt) return false;
        if (noneValuedNames.Contains(vn)) { EmitStreamStr(writeStrFn, "None"); return true; }
        if (narrowedOptionals.ContainsKey(vn)) return false;
        if (!optionalMembersByName.TryGetValue(vn, out var members)) return false;
        EmitOptionalStream(writeStrFn, floatFn, v, vt, members);
        return true;
    }

    // Write the repr of a module-level constant tuple: `(1, 4, 16, 60)`, keeping
    // the trailing comma of the one-element form, exactly as CPython spells it.
    private void EmitConstTupleRepr(string writeStrFn, string floatFn, List<int> values)
    {
        EmitStreamStr(writeStrFn, "(");
        for (int i = 0; i < values.Count; ++i)
        {
            if (i > 0) EmitStreamStr(writeStrFn, ", ");
            EmitStreamVal(floatFn, new Constant(values[i]));
        }
        if (values.Count == 1) EmitStreamStr(writeStrFn, ",");
        EmitStreamStr(writeStrFn, ")");
    }

    // Write an already-evaluated value to the stream as a number/float.
    /// <param name="declared">
    /// The width the SOURCE was declared with, when the caller has it (#331). A Constant
    /// carries no type, so the guess below reads one off the value -- and a value cannot say
    /// whether 4294967295 is a uint32 or the int -1, nor whether -2147483648 is an int32 or
    /// something narrower. That guess was only ever reached by a literal, whose magnitude does
    /// answer for it; a NAME holding a wide value now folds to a Constant too, and then the
    /// name's declared width is the answer and the value is not.
    /// </param>
    private void EmitStreamVal(string floatFn, Val val, DataType? declared = null)
    {
        // None has no runtime representation, but it is still a value print() must
        // spell: CPython writes "None" and so does this -- falling through to the
        // number writer sent 0 (or 0.0 through an Optional[float] getter), which a
        // reader cannot tell from a real zero. `print(servo.angle)` on a disabled
        // servo is the case that surfaced it. A LiveCallResult NoneVal is different:
        // it stands for the return register a void-declared call just filled, and the
        // number writer below is what reads it.
        if (val is NoneVal { LiveCallResult: false })
        {
            EmitStreamStr(ResolveWriteStrFn(), "None");
            return;
        }
        // A Constant that stands for a compile-time STRING is that string, whatever
        // expression shape produced it. This writer asked the value only how WIDE it is and
        // never whether it was a string at all, so `print(hex(255))` sent 257 -- the interned
        // id of "0xff" -- while `print(str(42))` came out right for the unrelated reason that
        // str() is one of the shapes print's ladder recognises by syntax (#393).
        // Text is set exactly where a Constant stands for text and nowhere else, which is
        // what makes this safe for the one-character case: `'A'` carries both its code 65 and
        // its text, and printing it is printing the character, the same answer the ladder's
        // literal branch already gives.
        if (val is Constant { Text: { } constText })
        {
            EmitStreamStr(ResolveWriteStrFn(), constText);
            return;
        }
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
        DataType argType = declared ?? val switch
        {
            Variable v2 => v2.Type,
            Temporary t2 => t2.Type,
            // A register read is a MemoryAddress carrying the width its `ptr[T]` declared.
            // It fell to the UINT8 default below, so `print(TCNT1.value)` on a ptr[uint16]
            // name formatted the LOW BYTE alone and dropped the high one with nothing said:
            // 0x1234 printed as 52. Assigning the same read to a uint16 local first printed
            // it whole, which is what made the truncation look like the register's fault.
            MemoryAddress ma2 => ma2.Type,
            Constant { Unsigned: true } => DataType.UINT32,
            Constant cc => cc.Value < 0
                         ? (cc.Value >= short.MinValue ? DataType.INT16 : DataType.INT32)
                         : cc.Value <= 0xFF ? DataType.UINT8
                         : cc.Value <= 0xFFFF ? DataType.UINT16 : DataType.UINT32,
            _ => DataType.UINT8,
        };
        (string decFn, DataType tmpType) = ResolveDecimalWriteFn(argType);
        Temporary tmp = MakeTemp(tmpType);
        Emit(new Copy(val, tmp));
        Emit(new Call(decFn, new List<Val> { tmp }, tmp));
    }

    // `print(xs)` where xs is a heap list or tuple: CPython writes the bracketed,
    // ", "-separated element repr; the scalar path showed the object pointer's
    // decimal value instead. The object layout is count(u8), capacity(u8), then
    // elements at 2 + i*elemSize, so the walk is a runtime loop over LoadIndirect.
    // GC_REF elements (a list[list[T]]) print their pointer -- nested reprs need a
    // recursion this one-level walk does not do.
    private void EmitSeqRepr(string writeStrFn, string floatWriteFn, Val seqPtr,
                           DataType elemDt, bool isTuple)
    {
        EmitStreamStr(writeStrFn, isTuple ? "(" : "[");
        Temporary seqCount = EmitListLoad(seqPtr, 0, DataType.UINT8);
        Temporary seqIdx = MakeTemp(DataType.UINT8);
        Emit(new Copy(new Constant(0), seqIdx));
        string seqTop = MakeLabel(), seqFirst = MakeLabel(), seqDone = MakeLabel();
        Emit(new Label(seqTop));
        Emit(new JumpIfGreaterOrEqual(seqIdx, seqCount, seqDone));
        Emit(new JumpIfEqual(seqIdx, new Constant(0), seqFirst));
        EmitStreamStr(writeStrFn, ", ");
        Emit(new Label(seqFirst));
        int elemSize = elemDt == DataType.GC_REF ? DataTypeExtensions.PointerWidth : elemDt.SizeOf();
        Temporary seqElemAddr = EmitElemAddr(seqPtr, seqIdx, elemSize);
        Temporary seqElem = MakeTemp(elemDt);
        Emit(new LoadIndirect(seqElemAddr, seqElem, elemDt));
        EmitStreamVal(floatWriteFn, seqElem, elemDt);
        Emit(new AugAssign(PyMCU.IR.BinaryOp.Add, seqIdx, new Constant(1)));
        Emit(new Jump(seqTop));
        Emit(new Label(seqDone));
        if (isTuple)
        {
            // `(x,)` -- CPython keeps the trailing comma on a one-element tuple.
            string seqNotOne = MakeLabel();
            Emit(new JumpIfNotEqual(seqCount, new Constant(1), seqNotOne));
            EmitStreamStr(writeStrFn, ",");
            Emit(new Label(seqNotOne));
        }
        EmitStreamStr(writeStrFn, isTuple ? ")" : "]");
    }

    /// <summary>The width a NAME was declared with, or null when the expression is not one.</summary>
    private DataType? DeclaredWidthOfName(Expression e)
    {
        if (e is not VariableExpr ve) return null;
        foreach (string? key in new[]
                 {
                     string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + ve.Name,
                     string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + ve.Name,
                     ve.Name,
                 })
        {
            if (key != null && variableTypes.TryGetValue(key, out var dt) && dt != DataType.UNKNOWN)
                return dt;
        }
        return null;
    }

    // Compile-time string text of an expression if it is statically a string (literal or const
    // string variable); else null. AST-based to avoid the string-id/int ambiguity of a Val.
    private string? StaticStringOf(Expression e)
    {
        if (e is StringLiteral sl) return sl.Value;
        // `text = f"{'literal string'}"`: a fully compile-time f-string (every part either
        // literal text or a constant expression) is itself statically known text -- the same
        // fold VisitFStringExpr performs when the whole expression lowers, answered here
        // without visiting so the name records its text through the ordinary assign-path
        // preamble below. Without this, StaticStringOf saw nothing (FStringExpr matched no
        // case) and the preamble actively CLEARED any text for the name via the "not a known
        // string" branch; the later scalar Copy then stored the interned string id as a bare
        // integer, and print(text) wrote "257" for `f"{'literal string'}"` (#p2avr).
        if (e is FStringExpr fse) return StaticFStringText(fse);
        if (e is VariableExpr ve)
            return ResolveStrConstant(currentInlinePrefix + ve.Name)
                ?? (!string.IsNullOrEmpty(currentFunction)
                    ? ResolveStrConstant(currentFunction + "." + ve.Name) : null)
                ?? ResolveStrConstant(ve.Name);
        // A field holding a compile-time string IS statically a string; answering null for
        // one made `sep.join([...])` refuse a separator whose text the compiler was holding.
        if (e is MemberAccessExpr ma) return IntrospectionTextOf(ma) ?? StaticStringOfField(ma);
        // `a + b` of two statically-known strings is itself statically known. This is the
        // same fold VisitBinary emits, answered without visiting: a name bound to the result
        // (`c = a + b`) records its text through the assign path, so print(c) writes
        // "helloworld" and not the interned id of the sum (#438).
        if (e is BinaryExpr { Op: PyMCU.Frontend.BinaryOp.Add } ab
            && StaticStringOf(ab.Left) is { } lText
            && StaticStringOf(ab.Right) is { } rText)
            return lText + rText;

        // `stnum = str(number)` where number is a compile-time int: the text the name
        // holds is the decimal spelling, which is all the string methods a driver calls
        // on it (find/len/slices) ever ask (adafruit_ht16k33's _number). str() of a
        // compile-time string is itself.
        if (e is CallExpr { Callee: VariableExpr { Name: "str" }, Args.Count: 1 } strCall)
        {
            if (StaticStringOf(strCall.Args[0]) is { } inner) return inner;
            if (TryEvalElemConst(strCall.Args[0], out int strInt))
                return strInt.ToString();
            return null;
        }

        // `c = chr(65)`: a character IS a one-character string, and the name holds that
        // text. Without this the name held a bare code and `print(c)` wrote 65 (#436).
        if (e is CallExpr { Callee: VariableExpr { Name: "chr" }, Args.Count: 1 } chrStrCall
            && TryEvalElemConst(chrStrCall.Args[0], out int chrStrCode)
            && chrStrCode is >= 0 and <= 255)
            return ((char)chrStrCode).ToString();

        // `po = "GRB" if bpp == 3 else "GRBW"` (adafruit_pixelbuf's pixel_order, then
        // NeoPixel's byteorder= argument): when the condition is a compile-time
        // expression the name holds whichever branch it selects, so the text is the
        // selected branch's text. A condition this evaluator cannot answer means the
        // name genuinely varies at run time -- null, exactly like any other non-static
        // right-hand side.
        if (e is TernaryExpr tern)
        {
            try
            {
                return StaticStringOf(EvaluateConstantExpr(tern.Condition) != 0
                    ? tern.TrueVal : tern.FalseVal);
            }
            catch (CompilerError)
            {
                return null;
            }
        }

        // `txt = stnum[:places]`: a slice of a compile-time string is compile-time text.
        // Bounds that do not fold leave the name unbound rather than guessed at.
        if (e is IndexExpr { Index: SliceExpr idxSlice } idxExpr
            && StaticStringOf(idxExpr.Target) is { } sliceSrc
            && TrySliceStaticText(sliceSrc, idxSlice, out var slicedText))
            return slicedText;

        // `char = char.lower()`: a str method over a receiver whose text is known
        // produces text the compiler knows -- the same answer TryEmitConstStrMethod
        // folds when the call is lowered. Without this the assign path clears the
        // name's binding BEFORE the call is evaluated, so the receiver's own text
        // is gone by the time .lower() asks for it (adafruit_ht16k33's _put).
        if (e is CallExpr { Callee: MemberAccessExpr strMethod } strMCall
            && strMCall.Args.Count == 0
            && StaticStringOf(strMethod.Object) is { } mRecv)
            return strMethod.Member switch
            {
                "lower" => mRecv.ToLowerInvariant(),
                "upper" => mRecv.ToUpperInvariant(),
                "strip" => mRecv.Trim(" \t\n\r\v\f".ToCharArray()),
                "lstrip" => mRecv.TrimStart(" \t\n\r\v\f".ToCharArray()),
                "rstrip" => mRecv.TrimEnd(" \t\n\r\v\f".ToCharArray()),
                _ => null,
            };
        return null;
    }

    // The text a fully compile-time f-string folds to, purely (no IR emitted): each part is
    // either literal text, another statically-known string (recursing through StaticStringOf,
    // which is what lets a string constant's own interpolation fold here too), or a
    // compile-time integer (TryFoldInt, which covers an IntegerLiteral and a declared/folded
    // constant name -- the two shapes TryExpandFStringValue's own IsConstPart already accepts
    // as "not a runtime value"). The moment one part is neither, this is not a compile-time
    // f-string and the caller must fall back to whatever the runtime buffer path decides --
    // returning null, never a partial spelling.
    private string? StaticFStringText(FStringExpr fs)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var part in FlattenFStringParts(fs))
        {
            if (!part.IsExpr) { sb.Append(part.Text); continue; }
            // A string-valued part ignores its own format spec, matching VisitFStringExpr's
            // lowering exactly (a format spec on a string interpolation is not applied there
            // either) -- this fold must agree byte-for-byte with what actually lowers.
            if (StaticStringOf(part.Expr!) is { } text) { sb.Append(text); continue; }
            if (TryFoldInt(part.Expr!, out int iv))
            {
                sb.Append(string.IsNullOrEmpty(part.FormatSpec)
                    ? iv.ToString() : FormatFStringInt(iv, part.FormatSpec));
                continue;
            }
            return null;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Python slice semantics on a compile-time string: bounds fold through the same
    /// constant evaluator the array-slice path uses, locals included (`stnum[:dot]`
    /// where `dot = stnum.find(".")` sits in localConstantValues). False when a bound
    /// is not compile-time known; the caller decides whether that is an error (a slice
    /// READ must produce a string, and there is no storage for a run-time one).
    /// </summary>
    private bool TrySliceStaticText(string text, SliceExpr sl, out string result)
    {
        result = "";
        int len = text.Length;
        int start, stop, step;
        bool savedFoldLocals = foldLocalConstants;
        foldLocalConstants = true;
        try
        {
            step = sl.Step != null ? EvaluateConstantExpr(sl.Step) : 1;
            if (step > 0)
            {
                start = sl.Start != null ? EvaluateConstantExpr(sl.Start) : 0;
                stop = sl.Stop != null ? EvaluateConstantExpr(sl.Stop) : len;
                if (start < 0) start += len;
                if (stop < 0) stop += len;
                start = Math.Clamp(start, 0, len);
                stop = Math.Clamp(stop, 0, len);
            }
            else
            {
                start = sl.Start != null ? EvaluateConstantExpr(sl.Start) : len - 1;
                stop = sl.Stop != null ? EvaluateConstantExpr(sl.Stop) : -1;
                if (start < 0) start += len;
                if (sl.Stop != null && stop < 0) stop += len;
                start = Math.Clamp(start, -1, len - 1);
                stop = Math.Clamp(stop, -1, len - 1);
            }
        }
        catch (CompilerError)
        {
            return false;
        }
        finally { foldLocalConstants = savedFoldLocals; }

        if (step == 0) throw UserError("slice step cannot be zero", sl);
        var sb = new System.Text.StringBuilder();
        for (int i = start; step > 0 ? i < stop : i > stop; i += step)
            sb.Append(text[i]);
        result = sb.ToString();
        return true;
    }

    /// <summary>
    /// Compile-time string methods. `s.strip(...)`, `s.index(...)`, `s.find(...)`,
    /// `s.startswith(...)`, `s.endswith(...)`, `s.count(...)`, `s.replace(...)`,
    /// `s.upper()`/`s.lower()` on a receiver whose text the compiler holds are folded to
    /// a new constant here -- the answer is data the compiler already has, so no string
    /// object is ever built (adafruit_pixelbuf's parse_byteorder runs `strip`/`index`
    /// on the `byteorder` parameter). Null when the receiver's text is not known or the
    /// member is not one of these; the caller then falls through to the refusal.
    /// </summary>
    private Val? TryEmitConstStrMethod(CallExpr expr, MemberAccessExpr memC)
    {
        string? text = StaticStringOf(memC.Object) ?? TryGetCompileTimeText(memC.Object);
        if (text == null) return null;

        // Whitespace as Python defines it for strip(): the same set CPython's
        // str.strip() uses when no argument is given.
        const string whitespace = " \t\n\r\v\f";

        switch (memC.Member)
        {
            case "strip" or "lstrip" or "rstrip":
            {
                if (expr.Args.Count > 1) return null;
                string? chars = whitespace;
                if (expr.Args.Count == 1)
                {
                    chars = StaticStringOf(expr.Args[0]) ?? TryGetCompileTimeText(expr.Args[0]);
                    if (chars == null) return null;
                }
                string trimmed = memC.Member switch
                {
                    "lstrip" => text.TrimStart(chars!.ToCharArray()),
                    "rstrip" => text.TrimEnd(chars!.ToCharArray()),
                    _ => text.Trim(chars!.ToCharArray()),
                };
                return InternConstString(trimmed);
            }

            case "index" or "find":
            {
                if (expr.Args.Count is < 1 or > 3) return null;
                string? needle = StaticStringOf(expr.Args[0]) ?? TryGetCompileTimeText(expr.Args[0]);
                if (needle == null) return null;
                int start = 0, end = text.Length;
                if (expr.Args.Count >= 2 && !TryConstIntArg(expr.Args[1], out start)) return null;
                if (expr.Args.Count >= 3 && !TryConstIntArg(expr.Args[2], out end)) return null;
                start = Math.Clamp(start < 0 ? text.Length + start : start, 0, text.Length);
                end = Math.Clamp(end < 0 ? text.Length + end : end, 0, text.Length);
                int at = start <= end ? text.IndexOf(needle, start, end - start,
                        StringComparison.Ordinal) : -1;
                if (at >= 0 || memC.Member == "find") return new Constant(at);
                // `s.index` on a miss raises ValueError. Lowered as a raise so a
                // try/except around it (pixelbuf wraps its index calls in one) catches
                // it exactly as it would at runtime.
                VisitRaise(new RaiseStmt("ValueError", "substring not found"));
                return new Constant(0);
            }

            case "startswith" or "endswith":
            {
                if (expr.Args.Count != 1) return null;
                string? affix = StaticStringOf(expr.Args[0]) ?? TryGetCompileTimeText(expr.Args[0]);
                if (affix == null) return null;
                bool hit = memC.Member == "startswith"
                    ? text.StartsWith(affix, StringComparison.Ordinal)
                    : text.EndsWith(affix, StringComparison.Ordinal);
                return new Constant(hit ? 1 : 0);
            }

            case "count":
            {
                if (expr.Args.Count != 1) return null;
                string? needle = StaticStringOf(expr.Args[0]) ?? TryGetCompileTimeText(expr.Args[0]);
                if (needle == null || needle.Length == 0) return null;
                int n = 0, from = 0;
                while ((from = text.IndexOf(needle, from, StringComparison.Ordinal)) >= 0)
                {
                    n++;
                    from += needle.Length;
                }
                return new Constant(n);
            }

            case "replace":
            {
                if (expr.Args.Count is < 2 or > 3) return null;
                string? oldS = StaticStringOf(expr.Args[0]) ?? TryGetCompileTimeText(expr.Args[0]);
                string? newS = StaticStringOf(expr.Args[1]) ?? TryGetCompileTimeText(expr.Args[1]);
                if (oldS == null || newS == null || oldS.Length == 0) return null;
                if (expr.Args.Count == 3)
                {
                    if (!TryConstIntArg(expr.Args[2], out int maxCount) || maxCount < 0)
                        return null;
                    // Python's count is a cap on leading replacements, left to right.
                    var sb = new System.Text.StringBuilder();
                    int pos = 0;
                    while (pos <= text.Length - oldS.Length && maxCount > 0)
                    {
                        int hit = text.IndexOf(oldS, pos, StringComparison.Ordinal);
                        if (hit < 0) break;
                        sb.Append(text, pos, hit - pos).Append(newS);
                        pos = hit + oldS.Length;
                        maxCount--;
                    }
                    sb.Append(text, pos, text.Length - pos);
                    return InternConstString(sb.ToString());
                }
                return InternConstString(text.Replace(oldS, newS, StringComparison.Ordinal));
            }

            case "upper": return expr.Args.Count == 0 ? InternConstString(text.ToUpperInvariant()) : null;
            case "lower": return expr.Args.Count == 0 ? InternConstString(text.ToLowerInvariant()) : null;
        }
        return null;
    }

    /// <summary>
    /// `seq.index(x)` where the sequence's elements are compile-time expressions -- a
    /// tuple/list literal or a name bound to one (`_GAINS.index(val)` in tcs34725's
    /// gain setter). A needle that folds yields a constant position; one that does
    /// not lowers to a first-match compare chain. A miss raises ValueError, the same
    /// contract `str.index` above keeps.
    /// </summary>
    private Val EmitConstSeqIndex(CallExpr expr, MemberAccessExpr memC, List<Expression> elems)
    {
        if (expr.Args.Count != 1)
            throw UserError(
                "'.index()' on a compile-time sequence takes the value to find -- " +
                "start/end positions are not supported", memC);

        Val needle = VisitExpression(expr.Args[0]);

        if (needle is Constant nc)
        {
            // The first match wins, so a hit at k is only certain when every element
            // before it folded to something that is NOT the needle -- a run-time
            // element could still equal it, and the lookup must wait for the chain.
            bool allFolded = true;
            for (int k = 0; k < elems.Count; ++k)
            {
                if (VisitExpression(elems[k]) is not Constant ec) { allFolded = false; break; }
                bool hit = nc.Text != null && ec.Text != null
                    ? nc.Text == ec.Text
                    : nc.Value == ec.Value;
                if (hit) return new Constant(k);
            }
            if (allFolded)
            {
                // Every element folded and none matched: a miss the compiler can see.
                VisitRaise(new RaiseStmt("ValueError", "tuple.index(x): x not in tuple"));
                return new Constant(0);
            }
        }

        // At least one element -- or the needle itself -- is run-time: emit a
        // first-match compare chain, with the miss arm's ValueError under a
        // runtime-branch guard so an inlined call does not abort the build on a
        // path the program may never take.
        Temporary result = MakeTemp(elems.Count > 255 ? DataType.UINT16 : DataType.UINT8);
        string endLabel = MakeLabel();
        for (int k = 0; k < elems.Count; ++k)
        {
            Val ev = VisitExpression(elems[k]);
            string nextLabel = MakeLabel();
            Emit(new JumpIfNotEqual(needle, ev, nextLabel));
            Emit(new Copy(new Constant(k), result));
            Emit(new Jump(endLabel));
            Emit(new Label(nextLabel));
        }
        EnterRuntimeBranch(DescribeOperand(expr.Args[0]));
        try
        {
            VisitRaise(new RaiseStmt("ValueError", "tuple.index(x): x not in tuple"));
        }
        finally { LeaveRuntimeBranch(); }
        Emit(new Label(endLabel));
        return result;
    }

    /// <summary>
    /// `self.pin_mapping.analog_pins.index(pin)`: the field holds a class OBJECT, so the
    /// sequence is a compile-time tuple on each candidate class and the field's tag byte
    /// picks which candidate's index applies. Each arm runs the ordinary const-seq index
    /// (with its own miss-raises-ValueError path); one candidate needs no tag at all.
    /// </summary>
    private Val EmitClassObjectAttrIndex(CallExpr expr, MemberAccessExpr memC,
                                         MemberAccessExpr fieldAccess, string attr,
                                         List<string> cands)
    {
        var perClass = new List<List<Expression>>(cands.Count);
        foreach (var cand in cands)
        {
            if (!constSequenceBindings.TryGetValue(ClassAttrKey(cand, attr), out var e))
                throw UserError(
                    $"field '{fieldAccess.Member}' can hold class {ShortClassName(cand)}, which has "
                    + $"no compile-time sequence attribute '{attr}' to index into", memC.Object);
            perClass.Add(e);
        }
        if (cands.Count == 1)
            return EmitConstSeqIndex(expr, memC, perClass[0]);
        Val tag = VisitExpression(fieldAccess);
        Temporary result = MakeTemp(DataType.UINT8);
        string done = MakeLabel();
        for (int k = 0; k < cands.Count; k++)
        {
            string? next = k < cands.Count - 1 ? MakeLabel() : null;
            if (next != null) Emit(new JumpIfNotEqual(tag, new Constant(k), next));
            Emit(new Copy(EmitConstSeqIndex(expr, memC, perClass[k]), result));
            if (next != null) { Emit(new Jump(done)); Emit(new Label(next)); }
        }
        Emit(new Label(done));
        return result;
    }

    /// <summary>Interns a computed compile-time string and returns it as a Constant
    /// carrying its text, the same shape a literal read produces.</summary>
    private Constant InternConstString(string text)
    {
        if (!stringLiteralIds.TryGetValue(text, out int id))
        {
            id = nextStringId++;
            stringLiteralIds[text] = id;
            stringIdToStr[id] = text;
        }
        return new Constant(id, text);
    }

    /// <summary>A compile-time integer argument (literal or folded expression), else false.</summary>
    private bool TryConstIntArg(Expression e, out int v)
    {
        try { v = EvaluateConstantExpr(e); return true; }
        catch (CompilerError) { v = 0; return false; }
    }

    /// <summary>
    /// The dotted chain of a member access, split into the name it starts from and the fields
    /// read off it: `o.inner.sep` gives (`o`, ["inner", "sep"]). The name is null when the
    /// chain does not start from one (a call result, an index).
    /// </summary>
    private (VariableExpr? Root, List<string> Fields) FieldChainOf(MemberAccessExpr ma)
    {
        var fields = new List<string>();
        Expression cur = ma;
        while (cur is MemberAccessExpr m) { fields.Add(m.Member); cur = m.Object; }
        fields.Reverse();
        return (cur as VariableExpr, fields);
    }

    /// <summary>
    /// Compile-time text of a string held in a field, at any depth. A ZCA field has no
    /// storage of its own: it is flattened into a name built from the instance and the field
    /// chain, so `o.inner.sep` read in main is filed as `main.o_inner_sep`. AST-only, like
    /// StaticStringOf -- nothing is visited and no IR is emitted, so a caller that asks may
    /// still decline afterwards.
    /// </summary>
    private string? StaticStringOfField(MemberAccessExpr ma)
    {
        (VariableExpr? root, List<string> fields) = FieldChainOf(ma);
        if (root == null) return null;

        foreach (string prefix in new[]
                 {
                     currentInlinePrefix,
                     !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." : null,
                     "",
                 })
        {
            if (prefix == null) continue;
            string key = ResolveAlias(prefix + root.Name);
            foreach (string f in fields) key = ResolveAlias(key + "_" + f);
            if (strConstantVariables.TryGetValue(key, out var text)) return text;
        }

        // `mod.attr` where mod names a MODULE: the member is a module-level global
        // under <mod>_<member> (mangled with the module's own dots folded to
        // underscores, the same spelling VisitMemberAccess builds), not a field on an
        // instance -- `mcp3xxx.__version__` printed the interned id without this.
        if (ma.Object is VariableExpr modVe && fields.Count == 1)
        {
            string? modBase = modules.ContainsKey(modVe.Name)
                && TryImportedAlias(modVe.Name, out var realMod) && realMod != null
                ? realMod.Replace('.', '_')
                : modules.ContainsKey(modVe.Name) ? modVe.Name.Replace('.', '_') : null;
            if (modBase != null
                && !multiStrVariables.ContainsKey(modBase + "_" + ma.Member)
                && strConstantVariables.TryGetValue(modBase + "_" + ma.Member, out var modText))
                return modText;
        }
        return null;
    }

    /// <summary>
    /// Is `name`, read under a module's mangling prefix, one of that module's CLASSES' methods
    /// rather than a function of the module itself? Both are filed as `&lt;module&gt;_&lt;name&gt;`, and
    /// only the class in front tells them apart.
    /// </summary>
    private bool NamesAMethodOfAClass(string modulePrefix, string name)
    {
        for (int cut = name.IndexOf('_'); cut > 0; cut = name.IndexOf('_', cut + 1))
        {
            string maybeClass = name[..cut];
            if (classNames.Contains(modulePrefix + maybeClass) || classNames.Contains(maybeClass))
                return true;
        }
        return false;
    }

    // The name a variable actually stores under, following the alias chain. Capped, since an
    // alias cycle would otherwise spin.
    private string ResolveAlias(string name)
    {
        string key = name;
        for (int d = 0; d < 20 && variableAliases.TryGetValue(key, out var alias) && alias != null; d++)
            key = alias;
        return key;
    }

    private string? ResolveByteReprFn()
    {
        string fn = ResolveCallee("uart_write_byte_repr");
        if (fn == "uart_write_byte_repr")
        {
            fn = "";
            foreach (var k in functionReturnTypes.Keys)
                if (k.EndsWith("uart_write_byte_repr", StringComparison.Ordinal)) { fn = k; break; }
        }
        return string.IsNullOrEmpty(fn) ? null : fn;
    }

    // print(<bytearray>) / print(arr[a:b]) / print(obj[a:b]) with __getitem__: stream
    // the CPython repr, one uart_write_byte_repr call per byte at compile-time-known
    // indices. Returns false (caller falls back) when the size is not statically
    // known or the target stdlib has no repr helper.
    private bool TryEmitByteArrayReprArg(string writeStrFn, Expression arg)
    {
        List<int>? indices = null;
        Expression? target = null;

        if (arg is VariableExpr av && ResolveArrayVar(av.Name) is { } arr)
        {
            if (arrayElemTypes.TryGetValue(arr.Name, out var et) && et != DataType.UINT8)
                return false;
            indices = Enumerable.Range(0, arr.Size).ToList();
            target = arg;
        }
        else if (arg is IndexExpr { Index: SliceExpr sl } ie)
        {
            int size = -1;
            if (ie.Target is VariableExpr sv && ResolveArrayVar(sv.Name) is { } sarr)
            {
                if (arrayElemTypes.TryGetValue(sarr.Name, out var set2) && set2 != DataType.UINT8)
                    return false;
                size = sarr.Size;
            }
            else
            {
                Val tv = VisitExpression(ie.Target);
                string cls = GetValClass(tv);
                if (!string.IsNullOrEmpty(cls) && inlineFunctions.ContainsKey(cls + "_" + "__getitem__"))
                    size = DunderConstLen(cls) ?? -1;
            }

            if (size < 0) return false;
            try { indices = SliceIndices(sl, size); }
            catch (Exception) { return false; }
            target = ie.Target;
        }

        if (indices == null || target == null) return false;
        string? reprFn = ResolveByteReprFn();
        if (reprFn == null) return false;

        EmitStreamStr(writeStrFn, "bytearray(b'");
        foreach (int i in indices)
        {
            Val b = VisitExpression(new IndexExpr(target, new IntegerLiteral(i)));
            Temporary tmp = MakeTemp(DataType.UINT8);
            Emit(new Copy(b, tmp));
            Emit(new Call(reprFn, new List<Val> { tmp }, tmp));
        }

        EmitStreamStr(writeStrFn, "')");
        return true;
    }

    private string ResolveFmtFn()
    {
        string fn = ResolveCallee("uart_write_fmt");
        if (fn == "uart_write_fmt")
            foreach (var k in functionReturnTypes.Keys)
                if (k.EndsWith("uart_write_fmt", StringComparison.Ordinal)) { fn = k; break; }
        return fn;
    }

    private string ResolveFloatFmtFn()
    {
        string fn = ResolveCallee("uart_write_float_fmt");
        if (fn == "uart_write_float_fmt")
            foreach (var k in functionReturnTypes.Keys)
                if (k.EndsWith("uart_write_float_fmt", StringComparison.Ordinal)) { fn = k; break; }
        return fn;
    }

    /// The minimal hex writer behind a bare `{x:x}` raise-message piece. uart_write_fmt
    /// answers the same spec but carries the generic radix loop and the 32-bit division
    /// helpers (~900 bytes on AVR); the exception line is the wrong place to spend that.
    /// Returns null when the console module was never imported (no writer to call).
    private string? ResolveHexFn()
    {
        string fn = ResolveCallee("uart_write_hex");
        if (fn != "uart_write_hex") return fn;
        foreach (var k in functionReturnTypes.Keys)
            if (k.EndsWith("uart_write_hex", StringComparison.Ordinal)) return k;
        foreach (var k in functionParams.Keys)
            if (k.EndsWith("uart_write_hex", StringComparison.Ordinal)) return k;
        return null;
    }

    // Parse the supported f-string format-spec subset: [0][width][type], type in d/x/X/b/o (c is
    // rejected for now). Returns the radix, field width, pad char and upper-case flag.
    // Unlocated on purpose, and this one is a WRONG caret removed rather than a missing one
    // withheld. Everything between the braces of an f-string is re-lexed by a fresh Lexer over
    // just that text, so an interpolated expression carries line 1 column 1 of the field, not
    // of the file. Measured: `print(f"{x:3d}")` on line 6 of main.py reported main.py:1:1 and
    // drew the caret under the auto-injected comment on line 1. Until the sub-lexer is given
    // the f-string token's offset, no node parsed inside a field can locate anything.
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

    // Parse the float f-string format-spec subset: [0][width][.precision]f. `precision`
    // defaults to 6 like CPython's, and is capped at 15: the digits come from the exact
    // decimal expansion (uart_text._float_fmt_digits), which stays exact through the
    // rounding digit for any float32 at that depth. Unlocated for the same reason as
    // ParseFormatSpec -- the field text is re-lexed at line 1 column 1.
    private (int Width, int Prec, char Pad) ParseFloatFormatSpec(string spec)
    {
        int i = 0;
        char pad = ' ';
        if (i < spec.Length && spec[i] == '0') { pad = '0'; i++; }
        int width = 0;
        while (i < spec.Length && spec[i] is >= '0' and <= '9') { width = width * 10 + (spec[i] - '0'); i++; }
        int prec = 6;
        if (i < spec.Length && spec[i] == '.')
        {
            i++;
            if (i >= spec.Length || spec[i] is not (>= '0' and <= '9'))
                throw UserError($"unsupported f-string format spec ':{spec}' (supported: [0][width][.precision]f)");
            prec = 0;
            while (i < spec.Length && spec[i] is >= '0' and <= '9') { prec = prec * 10 + (spec[i] - '0'); i++; }
        }
        if (i >= spec.Length || spec[i] != 'f' || i != spec.Length - 1)
            throw UserError($"unsupported f-string format spec ':{spec}' (supported: [0][width][.precision]f)");
        if (prec > 15)
            throw UserError($"f-string float precision {prec} exceeds the supported maximum of 15 digits");
        return (width, prec, pad);
    }

    // Emit an interpolated value formatted per its spec, via the generic uart_write_fmt helper.
    private void EmitFormattedExpr(Expression e, string spec)
    {
        // RFC 0009: `{v:.1f}` on a live Optional dispatches on its tag -- each
        // member formats at its own width through the same spec, and the None
        // member raises the TypeError CPython's format protocol raises:
        // `unsupported format string passed to NoneType.__format__`.
        if (CouldBeGuardedOperand(e))
        {
            var fg = ClassifyGuardedOperand(e);
            if (fg.IsOptional)
            {
                var fops = new List<GuardedOperand> { fg };
                var fnames = new List<List<string>> { GuardedMemberNames(fg) };
                EmitGuardedOptionalOp(fops, fnames,
                    emitLeaf: (exprs, _) =>
                    {
                        EmitFormattedExpr(exprs[0], spec);
                        return null;
                    },
                    raiseMessage: _ =>
                        "unsupported format string passed to NoneType.__format__");
                return;
            }
            if (fg.Evaluated != null)
                e = new PreEvaluatedExpr(fg.Evaluated, null) { Line = e.Line };
        }

        Val v = VisitExpression(e);
        DataType vt = GetValType(v);
        if (vt == DataType.FLOAT || v is FloatConstant || spec.EndsWith("f", StringComparison.Ordinal))
        {
            // Float specs route to uart_write_float_fmt(value, prec, width, flags) -- flags
            // bit0 = zero-pad. An int operand under an f spec converts to float, as CPython's
            // `f"{5:.1f}"` -> "5.0" does.
            var (fwidth, prec, fpad) = ParseFloatFormatSpec(spec);
            Val farg = v is Constant icv ? new FloatConstant(icv.Value) : v;
            if (farg is not FloatConstant && GetValType(farg) != DataType.FLOAT)
            {
                // The conversion must keep its FLOAT temp: Copy(Constant -> FLOAT) would be
                // forwarded as a raw int (GetDataType(Constant) is UNKNOWN), which loaded the
                // value's integer bits into the float argument slot.
                Temporary ftmp = MakeTemp(DataType.FLOAT);
                Emit(new Copy(farg, ftmp));
                farg = ftmp;
            }
            Emit(new Call(ResolveFloatFmtFn(), new List<Val>
            {
                farg,
                new Constant(prec),
                new Constant(fwidth),
                new Constant(fpad == '0' ? 1 : 0),
            }, MakeTemp(DataType.UINT8)));
            return;
        }
        var (width, radix, pad, upper) = ParseFormatSpec(spec);

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
    // An operand of a printed line that the IR generator has already evaluated (#371).
    //
    // Never parsed: the print lowering substitutes one for an operand it ran ahead of the text,
    // so the rest of the lowering reads the value instead of evaluating the expression a second
    // time -- which would run its side effects twice.
    private sealed class PreEvaluatedExpr : Expression
    {
        internal PreEvaluatedExpr(Val value, DataType? declared) { Value = value; Declared = declared; }
        internal Val Value { get; }
        internal DataType? Declared { get; }
    }

    // A sequence literal bound to an @inline parameter travels as its elements' AST, and every
    // `seq[k]`, `len(seq)` or unrolled `for` in the body lowers them again where it stands. An
    // element with an effect ran once per use instead of once at the call -- `s2([bump(), 5])`
    // with a body of `seq[0] + seq[0]` called bump twice, a body that never read the element
    // never called it, and `[bump(), bump()]` read backwards ran them in that order. Such an
    // element is evaluated here, in the caller, where the argument stands, and the parameter
    // carries its value. A register read is pinned too: two reads of a volatile register are
    // two different values. Every other element keeps its AST, so a literal still folds.
    private ListExpr PinEffectfulElements(ListExpr seq)
    {
        List<Expression>? pinned = null;
        for (int k = 0; k < seq.Elements.Count; k++)
        {
            Expression el = seq.Elements[k];
            bool pin = (OperandCanHaveAnEffect(el) || ReadsARegister(el)) && OperandYieldsARealValue(el);
            if (pin) pinned ??= seq.Elements.Take(k).ToList();
            pinned?.Add(pin ? PinOnce(el) : el);
        }
        return pinned == null ? seq : new ListExpr(pinned) { Line = seq.Line };
    }

    // Evaluate once and hold the value: a constant stays a literal so it still folds, an
    // integer goes to a fresh temporary so a later write to the name it came from (the target
    // of a walrus, a global the body bumps) cannot reach it, and anything else -- an instance,
    // a buffer -- is carried as evaluated.
    private Expression PinOnce(Expression e) => HeldValue(VisitExpression(e), e);

    private Expression HeldValue(Val v, Expression e)
    {
        if (v is Constant c) return new IntegerLiteral(c.Value) { Line = e.Line };
        if (v is MemoryAddress or Temporary or Variable && string.IsNullOrEmpty(GetValClass(v))
            && GetValType(v) is var dt && dt <= DataType.INT32)
        {
            Temporary held = MakeTemp(dt);
            Emit(new Copy(v, held));
            return new PreEvaluatedExpr(held, null) { Line = e.Line };
        }
        return new PreEvaluatedExpr(v, null) { Line = e.Line };
    }

    // A name, a field or a register reaches the instruction that uses it as itself, and is
    // read when that instruction runs. When something with an effect is evaluated between
    // the two -- a later argument, the right operand, a later operand of a print -- the read
    // has to happen first, where Python makes it: `print(count, bump())` printed the count
    // after the bump. Only an integer scalar is taken; a name that stands for storage, text
    // or an optional keeps its identity.
    private Val SnapshotRead(Val v)
    {
        bool lazy = v is MemoryAddress
            || v is Variable var && var.Type <= DataType.FLOAT
               && string.IsNullOrEmpty(GetValClass(v)) && !NameStandsForStorage(var.Name);
        if (!lazy) return v;
        Temporary held = MakeTemp(GetValType(v));
        Emit(new Copy(v, held));
        return held;
    }

    private bool NameStandsForStorage(string n) =>
        arraySizes.ContainsKey(n) || bytearrayParams.Contains(n) || listVarElemTypes.ContainsKey(n)
        || strConstantVariables.ContainsKey(n) || flashStrPtrVars.Contains(n)
        || constantAddressVariables.ContainsKey(n) || arenaBufferNames.Contains(n)
        || optionalTagSlots.ContainsKey(n)
        || TryResolveArrayStorageKey(n, out _) || TryResolveArrayStorageKey(FollowAliases(n), out _)
        || ResolveConstSequence(n) != null;

    // Whether anything in this print operand can have an effect, looking inside a tuple and
    // an f-string, which the print lowering takes apart.
    private bool PrintOperandHasAnEffect(Expression e) => e switch
    {
        TupleExpr t => t.Elements.Any(PrintOperandHasAnEffect),
        FStringExpr fs => fs.Parts.Any(p => p.IsExpr && p.Expr != null && PrintOperandHasAnEffect(p.Expr)),
        _ => OperandCanHaveAnEffect(e),
    };

    private bool ValueIsHandedBack(Expression value) =>
        OperandYieldsARealValue(value)
        || value is CallExpr { Callee: VariableExpr fv }
           && inlineFunctions.ContainsKey(ResolveCallee(fv.Name));

    // `REG.value` on a pointer, anywhere inside the expression: a volatile load.
    private bool ReadsARegister(Expression? e) => e switch
    {
        MemberAccessExpr { Member: "value" } m =>
            !IsKnownInstanceField(m.Object, "value") && !IsPropertyGetterRead(m),
        MemberAccessExpr m => ReadsARegister(m.Object),
        BinaryExpr b => ReadsARegister(b.Left) || ReadsARegister(b.Right),
        UnaryExpr u => ReadsARegister(u.Operand),
        TernaryExpr t => ReadsARegister(t.Condition) || ReadsARegister(t.TrueVal)
                         || ReadsARegister(t.FalseVal),
        IndexExpr ie => ReadsARegister(ie.Target) || ReadsARegister(ie.Index),
        _ => false,
    };

    // Run every operand of a printed line before any of its text is written (#371).
    //
    // CPython builds the whole line and writes it in one piece, so every side effect of every
    // operand happens before the first character appears. This lowering streams as it goes, so
    // a call inside a printed operand wrote its own output in the MIDDLE of the line:
    // `print(f"a={side()}")` gave `a=SIDE\n7` where CPython gives `SIDE\na=7`. The same order
    // decides what a raise leaves behind: `print((sonar.distance,))`, whose element raises on a
    // timeout, had already put the opening `(` on the wire, so the handler's own line started
    // mid-line as `(Retrying!`.
    //
    // Only an operand that can HAVE an effect is touched -- one that contains a call or a
    // property read -- and only where the lowering would evaluate it as a number. Everything
    // else is text the lowering already holds, or a shape that writes its own bytes; leaving
    // those alone is what keeps every program without a call inside a print byte-identical.
    // <paramref name="written"/> says whether anything of this line has reached the sink yet.
    // An operand that is the very first thing written needs no pre-evaluation -- its effects
    // already happen before any character of the line -- and leaving those alone is what keeps
    // `print(side())` and `print(f"{side()}")` byte-identical. It is set as the walk passes each
    // piece that writes.
    // <paramref name="laterEffect"/> says whether an operand after this one can have an effect.
    // A name, a field or a register read is then taken here too: the lowering reads it where it
    // writes it, which is after that later operand ran.
    private Expression PreEvaluatePrintOperand(Expression arg, ref bool written, bool laterEffect)
    {
        if (arg is PreEvaluatedExpr) { written = true; return arg; }

        if (arg is TupleExpr tup)
        {
            var elems = new List<Expression>(tup.Elements.Count);
            bool tupChanged = false;
            written = true;                       // the opening `(` goes out before any element
            for (int k = 0; k < tup.Elements.Count; k++)
            {
                var el = tup.Elements[k];
                var rewritten = PreEvaluatePrintOperand(el, ref written,
                    laterEffect || tup.Elements.Skip(k + 1).Any(PrintOperandHasAnEffect));
                tupChanged |= !ReferenceEquals(rewritten, el);
                elems.Add(rewritten);
            }
            return tupChanged ? new TupleExpr(elems) { Line = tup.Line } : tup;
        }

        if (arg is FStringExpr fstr)
        {
            var parts = new List<FStringPart>(fstr.Parts.Count);
            bool fsChanged = false;
            for (int k = 0; k < fstr.Parts.Count; k++)
            {
                var part = fstr.Parts[k];
                if (!part.IsExpr || part.Expr == null)
                {
                    if (!string.IsNullOrEmpty(part.Text)) written = true;
                    parts.Add(part);
                    continue;
                }
                var rewritten = PreEvaluateInterpolation(part, ref written,
                    laterEffect || fstr.Parts.Skip(k + 1)
                        .Any(p => p.IsExpr && p.Expr != null && PrintOperandHasAnEffect(p.Expr)));
                fsChanged |= !ReferenceEquals(rewritten, part.Expr);
                parts.Add(ReferenceEquals(rewritten, part.Expr)
                    ? part
                    : new FStringPart
                    {
                        IsExpr = true, Text = part.Text,
                        Expr = rewritten, FormatSpec = part.FormatSpec,
                    });
            }
            return fsChanged ? new FStringExpr(parts) { Line = fstr.Line } : fstr;
        }

        // The first thing written needs no pre-evaluation of its own, unless a later operand
        // is pre-evaluated: that one would then run ahead of it (`print(bump(), other())`).
        bool needed = written || laterEffect;
        written = true;
        if (laterEffect && !OperandCanHaveAnEffect(arg) && PrintOperandIsRunAsANumber(arg, requireEffect: false))
            return ReadBeforeALaterEffect(arg, DeclaredWidthOfName(arg));
        if (!needed || !PrintOperandIsRunAsANumber(arg) || !OperandYieldsARealValue(arg)) return arg;
        RejectInstanceInterpolation(arg);
        return new PreEvaluatedExpr(VisitExpression(arg), DeclaredWidthOfName(arg)) { Line = arg.Line };
    }

    // An operand with no effect of its own, read now because a later one has one. Only a
    // value that would otherwise be read late is taken; a constant, an instance or a buffer
    // keeps its expression, which the lowering prints the way it always did.
    private Expression ReadBeforeALaterEffect(Expression e, DataType? declared)
    {
        Val v = VisitExpression(e);
        Val read = SnapshotRead(v);
        if (ReferenceEquals(read, v) && v is not Temporary) return e;
        return new PreEvaluatedExpr(read, declared) { Line = e.Line };
    }

    // One interpolation of an f-string. A nested f-string recurses; a part with a format spec
    // always reaches the numeric formatter, so only the effect test applies to it.
    private Expression PreEvaluateInterpolation(FStringPart part, ref bool written, bool laterEffect)
    {
        Expression e = part.Expr!;
        if (e is FStringExpr) return PreEvaluatePrintOperand(e, ref written, laterEffect);
        bool needed = written || laterEffect;
        written = true;
        if (laterEffect && !OperandCanHaveAnEffect(e) && PrintOperandIsRunAsANumber(e, requireEffect: false)
            && (!string.IsNullOrEmpty(part.FormatSpec) || StaticStringOf(e) == null && !IsBoolExpr(e)))
            return ReadBeforeALaterEffect(e, null);
        if (!needed || !OperandCanHaveAnEffect(e) || !OperandYieldsARealValue(e)) return e;
        if (string.IsNullOrEmpty(part.FormatSpec))
        {
            if (StaticStringOf(e) != null) return e;
            if (e is BooleanLiteral || IsBoolExpr(e)) return e;
        }
        RejectInstanceInterpolation(e);
        return new PreEvaluatedExpr(VisitExpression(e), null) { Line = e.Line };
    }

    // Whether the print lowering would send this operand to the number/float writer, which is
    // the one path that evaluates it as a value. Every earlier branch of EmitPrintArg either
    // already holds the text or writes its own bytes, and is left alone.
    // <paramref name="requireEffect"/> false asks only about the shape: whether the operand
    // would be printed as a number, effect or not.
    private bool PrintOperandIsRunAsANumber(Expression a, bool requireEffect = true)
    {
        if (requireEffect && !OperandCanHaveAnEffect(a)) return false;
        if (a is StringLiteral or BooleanLiteral or NoneLiteral or IntegerLiteral or FloatLiteral
            or PreEvaluatedExpr) return false;
        if (requireEffect && a is VariableExpr) return false;
        // `print(e)`, `print(str(e))`, `print(e.args[0])`: the message of a bound exception.
        if (a is CallExpr { Callee: VariableExpr { Name: "str" }, Args: [VariableExpr sv] }
            && TryGetExceptionBinding(sv.Name, out _)) return false;
        if (a is IndexExpr
            {
                Target: MemberAccessExpr { Object: VariableExpr av, Member: "args" },
            } && TryGetExceptionBinding(av.Name, out _)) return false;
        // `print(e.args)`: the args tuple is streamed piece by piece, not evaluated --
        // reading it as a value would try to materialise a runtime tuple there is not.
        if (a is MemberAccessExpr { Object: VariableExpr avArgs, Member: "args" }
            && TryGetExceptionBinding(avArgs.Name, out _)) return false;
        if (a is CallExpr { Callee: VariableExpr { Name: "chr" } }) return false;
        if (a is IndexExpr { Index: SliceExpr }) return false;                       // bytearray repr
        if (a is IndexExpr { Index: not SliceExpr } ix && StringBehindSubscript(ix) != null) return false;
        if (a is MemberAccessExpr && TryGetCompileTimeText(a) != null) return false;
        if (StaticStringOf(a) != null) return false;
        if (IsBoolExpr(a)) return false;
        return true;
    }

    // Whether evaluating this operand HANDS BACK its value, or leaves it in the return register
    // for the instruction that follows to pick up.
    //
    // A method or function with no declared result type yields a NoneVal here and the value
    // rides in the return register (the unannotated-method seam of PyMCU#292). Moving such an
    // operand ahead of the line's text puts a write_str call between the two, and the printed
    // number became whatever that call left behind: `print("A", a.plain())` gave `A 0`. Those
    // are left where they are, so nothing moves across a call it cannot survive.
    private bool OperandYieldsARealValue(Expression e) => e switch
    {
        CallExpr { Callee: VariableExpr fv } => CalleeDeclaresAResult(ResolveCallee(fv.Name)),
        CallExpr { Callee: MemberAccessExpr mm } => MemberCalleeDeclaresAResult(mm),
        MemberAccessExpr mem when IsPropertyGetterRead(mem) => MemberCalleeDeclaresAResult(mem),
        // Arithmetic, indexing and the rest build their own temporary, so the value is theirs.
        _ => true,
    };

    private bool CalleeDeclaresAResult(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (functionReturnTypes.TryGetValue(key, out string? rt)
            && !string.IsNullOrEmpty(rt) && rt != "void" && rt != "None"
            && DataTypeExtensions.StringToDataType(rt) != DataType.UNKNOWN) return true;
        return DeclaresARealResult(key);
    }

    private bool MemberCalleeDeclaresAResult(MemberAccessExpr m)
    {
        if (m.Object is not VariableExpr recv) return false;
        string? cls = InstanceClassOfName(recv.Name) ?? ReceiverClassThroughAliases(recv.Name);
        if (string.IsNullOrEmpty(cls)) return false;
        return CalleeDeclaresAResult(cls + "_" + m.Member);
    }

    // Whether evaluating this expression can do anything observable: a call, a property
    // read, which is a call written as an attribute, or a walrus, which writes a name. A plain
    // name, a field, a literal and arithmetic over them cannot, so moving them earlier would
    // only churn the output.
    private bool OperandCanHaveAnEffect(Expression? e)
    {
        switch (e)
        {
            case null: return false;
            case CallExpr: return true;
            case WalrusExpr: return true;
            case MemberAccessExpr mem:
                return IsPropertyGetterRead(mem) || OperandCanHaveAnEffect(mem.Object);
            case BinaryExpr b: return OperandCanHaveAnEffect(b.Left) || OperandCanHaveAnEffect(b.Right);
            case UnaryExpr u: return OperandCanHaveAnEffect(u.Operand);
            case TernaryExpr t:
                return OperandCanHaveAnEffect(t.Condition)
                    || OperandCanHaveAnEffect(t.TrueVal) || OperandCanHaveAnEffect(t.FalseVal);
            case IndexExpr ie: return OperandCanHaveAnEffect(ie.Target) || OperandCanHaveAnEffect(ie.Index);
            default: return false;
        }
    }

    private void EmitStreamFString(string writeStrFn, string floatFn, FStringExpr fs)
    {
        // Every interpolation that has literal text in front of it runs before that text is
        // written (#371). Reached from print() and from uart.write_str/println, which share
        // this lowering.
        bool fsWritten = false;
        if (PreEvaluatePrintOperand(fs, ref fsWritten, false) is FStringExpr prepared) fs = prepared;
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
            // `f"{e.errno}"`: the same bare integer print(e.errno) writes, at the same
            // per-handler width -- ahead of the generic member-access fallback further
            // down, which would resolve `e` as a name and refuse it with the binding
            // diagnostic before this ever runs.
            if (part.Expr is MemberAccessExpr { Object: VariableExpr fErrnoObj, Member: "errno" }
                && TryGetExceptionBinding(fErrnoObj.Name, out var fErrnoB))
            {
                Flush();
                DataType fErrnoWidth = TryGetExceptionCatchable(fErrnoObj.Name, out var fErrnoSites)
                    ? ExceptionArgPrintWidth(fErrnoSites) : DataType.INT32;
                EmitStreamVal(floatFn,
                    ExceptionErrnoValue(fErrnoObj.Name, fErrnoB.ExnType, part.Expr!),
                    fErrnoWidth);
                continue;
            }
            // `f"{v}"` naming a literal list/tuple in fixed slots: the repr print()
            // writes. Ahead of ModuleConstListValues -- it would take a LIST down the
            // tuple-bracket path.
            if (part.Expr is VariableExpr flv
                && ResolveArrayVar(flv.Name) is { } flSeq
                && literalSequenceArrays.Contains(flSeq.Name))
            {
                Flush();
                TryEmitLiteralSequenceRepr(writeStrFn, flSeq.Name,
                    tupleBoundNames.Contains(flSeq.Name) || IsTupleBound(flv.Name),
                    e => EmitStreamVal(floatFn,
                        e is PreEvaluatedExpr fpv ? fpv.Value : VisitExpression(e)));
                continue;
            }
            // `{d[k]}` over a dict of strings, a name whose text a run-time path decided
            // (`s = d[k]` binds the same candidate set), and a name known to be None:
            // the same answers print gives them.
            if (DictOfStringsLookup(part.Expr!) != null || IsMultiStrOperand(part.Expr!)
                || (part.Expr is MemberAccessExpr fsmem
                    && TryGetMultiStrMember(fsmem, out _, out _, out _)))
            {
                Flush();
                TryEmitMultiStrStream(writeStrFn, part.Expr!);
                continue;
            }
            // A name known to be None interpolates as "None", the same answer print gives.
            if (part.Expr is VariableExpr or MemberAccessExpr && IsNoneValued(part.Expr!))
            {
                pending += "None";
                continue;
            }
            // `f"{_GAINS}"` names a module-level tuple of constants: CPython writes
            // the repr `(1, 4, 16, 60)`. Reading the NAME instead evaluated to the
            // table's base and streamed a 0 (adafruit_tcs34725's ValueErrors).
            if (part.Expr is VariableExpr ftv && ModuleConstListValues(ftv.Name) is { } fcv)
            {
                Flush();
                EmitConstTupleRepr(writeStrFn, floatFn, fcv);
                continue;
            }
            // `f"{t}"` where t names a tuple return (`t = f()`): the tuple text
            // CPython would write -- `(a, b, c)` -- without the tuple existing.
            if (part.Expr is VariableExpr fTupName
                && NamedTupleElemsOf(fTupName.Name) is { } fTupElems)
            {
                Flush();
                EmitStreamStr(writeStrFn, "(");
                for (int ti = 0; ti < fTupElems.Count; ++ti)
                {
                    if (ti > 0) EmitStreamStr(writeStrFn, ", ");
                    EmitStreamVal(floatFn, VisitExpression(fTupElems[ti]));
                }
                if (fTupElems.Count == 1) EmitStreamStr(writeStrFn, ",");
                EmitStreamStr(writeStrFn, ")");
                continue;
            }
            // RFC 0009 section 8: `{v}` / `{obj.field}` on a live tagged Optional
            // prints the member's repr or "None" -- the same read print() takes.
            // Ahead of RejectInstanceInterpolation so a union field prints.
            if (TryEmitOptionalStreamOperand(writeStrFn, floatFn, part.Expr!, Flush)) continue;
            RejectInstanceInterpolation(part.Expr!);
            // `{f()}` / `{obj.prop}`: the call lowers first, then the tag it
            // returned decides -- same dispatch, on the evaluated value.
            if (part.Expr is CallExpr or MemberAccessExpr or PreEvaluatedExpr)
            {
                Val partVal = part.Expr is PreEvaluatedExpr pev
                    ? pev.Value : VisitExpression(part.Expr!);
                Flush();
                // `{str(v)}` produced a runtime-string buffer; `{f()}` returning a
                // literal sequence produced its fixed slots -- repr either way.
                if (partVal is Variable prs && TryGetRuntimeStr(prs.Name, out var prsInfo))
                { EmitRuntimeStrStream(prs.Name, prsInfo.LenVar); continue; }
                if (ValNameOf(partVal) is { } pSeqName
                    && literalSequenceArrays.Contains(pSeqName)
                    && TryEmitLiteralSequenceRepr(writeStrFn, pSeqName,
                           tupleBoundNames.Contains(pSeqName) || IsTupleBound(pSeqName),
                           e2 => EmitStreamVal(floatFn,
                               e2 is PreEvaluatedExpr fpv2 ? fpv2.Value : VisitExpression(e2))))
                    continue;
                if (!TryEmitOptionalStreamVal(writeStrFn, floatFn, partVal))
                    EmitStreamVal(floatFn, partVal);
                continue;
            }
            Flush();
            Val partFinal = VisitExpression(part.Expr!);
            // A plain `{v}` can also reach a str() buffer -- the CallExpr arm only
            // runs when the AST still showed the call.
            if (partFinal is Variable pfv && TryGetRuntimeStr(pfv.Name, out var pfvInfo))
            { EmitRuntimeStrStream(pfv.Name, pfvInfo.LenVar); continue; }
            if (!TryEmitOptionalStreamVal(writeStrFn, floatFn, partFinal))
                EmitStreamVal(floatFn, partFinal);
        }
        Flush();
    }

    // True when `cls` is the bare class `name` AND that binding traces back to a pymcu stdlib
    // module whose dotted path ends in `.moduleTail` -- not a user class that merely shares the
    // bare name (a user's own `class UART`, or a driver class like `BleUART`/`MyLCD` that used
    // to match on EndsWith alone). classModuleMap records, per bare class name, the prefix of
    // the module the class was actually DEFINED in ("" for a class the entry file itself
    // declares), so this resolves the class by where it comes from, not by how it is spelled.
    // True when `cls` -- the qualified class name instanceClasses tracks for the instance
    // (module prefix + bare name, e.g. "pymcu_hal_avr_uart_UART"; just the bare name, with no
    // prefix, for a class the entry file declares itself) -- resolves to the pymcu stdlib class
    // `name` from a module whose dotted path ends in `.moduleTail`. A user class that merely
    // shares or ends in the same bare spelling (a user's own `class UART`, or a driver class
    // like `BleUART`/`MyLCD`, both of which used to match on EndsWith alone) carries no such
    // prefix and is excluded.
    private bool IsStdlibClass(string cls, string name, string moduleTail)
    {
        if (cls == name || !cls.EndsWith("_" + name)) return false;
        string prefix = cls.Substring(0, cls.Length - name.Length);
        return prefix.StartsWith("pymcu_") && prefix.EndsWith(moduleTail + "_");
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
        bool multiStr = false;
        CallExpr? joinCall = null;
        string? joinSep = null;
        if (sfs == null)
        {
            if (expr.Args[0] is VariableExpr rv && TryGetRuntimeStr(rv.Name, out var ri))
                runtimeStr = (rv.Name, ri.LenVar);
            else if (expr.Args[0] is VariableExpr mv && TryGetMultiStr(mv.Name, out _, out _, out _))
                multiStr = true;
            else if (expr.Args[0] is CallExpr { Callee: MemberAccessExpr { Member: "join" } ujm } ujc
                     && StaticStringOf(ujm.Object) is { } uSep)
            { joinCall = ujc; joinSep = uSep; }
            else return null;
        }
        if (sm.Object is not VariableExpr) return null;

        Val sObj = VisitExpression(sm.Object);
        if (sObj is not Variable svObj) return null;
        if (!instanceClasses.TryGetValue(svObj.Name, out var sCls) || !IsStdlibClass(sCls, "UART", "uart")) return null;

        string wfn = ResolveWriteStrFn();
        if (sfs != null)
        {
            string ffn = ResolveFloatWriteFn();
            EmitStreamFString(wfn, ffn, sfs);
        }
        else if (joinCall != null)
            EmitJoinStream(wfn, ResolveFloatWriteFn(), joinSep!, joinCall);
        else if (multiStr) TryEmitMultiStrStream(wfn, expr.Args[0]);
        else EmitRuntimeStrStream(runtimeStr!.Value.Name, runtimeStr.Value.LenVar);
        if (sm.Member == "println") EmitStreamStr(wfn, "\n");
        return new NoneVal();
    }

    /// <summary>
    /// An integer argument at the width of the parameter it binds. The Call instruction
    /// marshals each argument by its own Val's width, so a wider temp (a `pos + 1` inferred
    /// u32 passed to a uint16 param) shifts every later argument out of its register slot
    /// on AVR and the callee reads garbage (this silently dropped the buf pointer in
    /// strfmt._fs_i32 -> _fs_u32). Narrow (or widen, with a sign-correct Copy) into a temp
    /// of the param's type. A Constant is widened only: its natural width is its magnitude
    /// (65535 -> UINT16), so a wider param would get its high bytes from register garbage.
    /// </summary>
    private Val CoerceArgToParamWidth(Val argVal, DataType ptype)
    {
        if (!IsScalarIntType(ptype)) return argVal;
        if (argVal is Variable or Temporary
            && IsScalarIntType(GetValType(argVal))
            && GetValType(argVal).SizeOf() != ptype.SizeOf())
        {
            var coerced = MakeTemp(ptype);
            Emit(new Copy(argVal, coerced));
            return coerced;
        }
        if (argVal is Constant argC && GetValType(argVal).SizeOf() < ptype.SizeOf())
        {
            var coerced = MakeTemp(ptype);
            Emit(new Copy(argC, coerced));
            return coerced;
        }
        return argVal;
    }

    // CoerceArgToParamWidth against parameter `index` of `callee`, when its type is known.
    private Val CoerceToParam(string callee, int index, Val arg)
    {
        if (index < 0 || !functionParamTypes.TryGetValue(callee, out var pts) || index >= pts.Count)
            return arg;
        // A float parameter takes an integer as the float it is: marshalled as an integer,
        // the callee read its bytes as a float (0.0 for `o.scale(-3)`).
        if (pts[index] == DataType.FLOAT && !IsTaggedParam(callee, index))
            return IntegerArgAsFloat(arg);
        return CoerceArgToParamWidth(arg, pts[index]);
    }

    /// An integer argument bound for a float parameter, as a float: a literal converts now,
    /// a run-time integer through a float temporary the backend converts. Anything else is
    /// returned as it is.
    private Val IntegerArgAsFloat(Val arg)
    {
        if (arg is Constant { Text: null } ic) return new FloatConstant(ic.Value);
        if (arg is Variable or Temporary && IsScalarIntType(GetValType(arg)))
        {
            var asFloat = MakeTemp(DataType.FLOAT);
            Emit(new Copy(arg, asFloat));
            return asFloat;
        }
        return arg;
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
        // A parameter or local bound to a non-string shadows a same-named module-level
        // runtime string, the same shadow ResolveStrConstant honors (#438).
        foreach (var scoped in LocalScopeKeys(name))
            if (BindsNonString(scoped)) { info = default; return false; }
        return runtimeStrVars.TryGetValue(name, out info);
    }

    /// <summary>Streams one byte that is only known at run time, as a character.</summary>
    // The same raw byte write, from a value the lowering already has. The byte is copied
    // into a temporary first: a call whose declared result is void leaves its answer in the
    // return register, and the copy is what pins it before anything else can overwrite it.
    private void EmitStreamCharVal(Val code)
    {
        Temporary charTmp = MakeTemp(DataType.UINT8);
        Emit(new Copy(code, charTmp));
        VisitCall(new CallExpr(new VariableExpr(ResolveByteWriteFn()),
            new List<Expression> { new PreEvaluatedExpr(charTmp, DataType.UINT8) }));
    }

    private void EmitStreamCharExpr(Expression code)
    {
        VisitCall(new CallExpr(new VariableExpr(ResolveByteWriteFn()),
            new List<Expression> { code }));
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

    // `sep.join(<compile-time seq>)` streamed straight to the writer, used by print() and
    // uart.write_str/println. A list/tuple of compile-time strings folds to one write; a
    // generator or list comprehension unrolls at compile time (GenExpWalk, the same walk
    // the reductions use) into per-element writes with the separator between produced
    // elements -- CPython's output byte for byte.
    private void EmitJoinStream(string writeStrFn, string floatWriteFn, string sep,
                                CallExpr call)
    {
        if (call.Args.Count != 1)
            throw UserError(
                "str.join takes one argument, the sequence to join; this call passes "
                + $"{call.Args.Count}.", call.Callee);
        var seq = call.Args[0];

        if (seq is ListExpr or TupleExpr)
        {
            var elems = seq is ListExpr sll ? sll.Elements : ((TupleExpr)seq).Elements;
            if (elems.All(e => StaticStringOf(e) != null))
            {
                EmitStreamStr(writeStrFn,
                    string.Join(sep, elems.Select(e => StaticStringOf(e)!)));
                return;
            }
            throw UserError(JoinSequenceRefusal(seq), seq);
        }

        GeneratorExpr? gen = seq switch
        {
            GeneratorExpr g => g,
            ListCompExpr lc => new GeneratorExpr(lc.Element, lc.VarName, lc.Iterable,
                lc.Var2Name, lc.Iterable2, lc.Filter) { Line = lc.Line, Column = lc.Column },
            _ => null,
        };
        if (gen == null) throw UserError(JoinSequenceRefusal(seq), seq);

        // The separator goes between PRODUCED elements. With no runtime filter that is
        // "not the first iteration" at compile time; behind a runtime filter the count of
        // produced elements is itself runtime, so a flag seeded before the first guard
        // jump answers it instead (the min()/max() `produced` pattern: a Temporary, so no
        // local-constant folding can answer the read).
        bool first = true;
        Temporary? sepFlag = null;
        GenExpWalk(gen, "str.join", (g, guarded) =>
        {
            if (!first)
            {
                if (sepFlag != null)
                {
                    string skipSep = MakeLabel();
                    Emit(new JumpIfZero(sepFlag, skipSep));
                    EmitStreamStr(writeStrFn, sep);
                    Emit(new Label(skipSep));
                }
                else EmitStreamStr(writeStrFn, sep);
            }
            EmitJoinStreamElement(writeStrFn, floatWriteFn, g.Element);
            if (sepFlag != null)
                Emit(new Copy(new Constant(1), sepFlag));
            first = false;
            return true;
        }, beforeFilterJump: () =>
        {
            if (sep.Length > 0 && sepFlag == null)
            {
                sepFlag = MakeTemp();
                Emit(new Copy(new Constant(first ? 0 : 1), sepFlag));
            }
        });
    }

    // One produced element streamed to the writer: an f-string goes through the same
    // per-part lowering print() gives it, a compile-time string is one write_str, chr(b)
    // is the byte itself, and a name bound to a runtime string (an f-string-as-value
    // buffer) streams up to its tracked length.
    private void EmitJoinStreamElement(string writeStrFn, string floatWriteFn, Expression e)
    {
        if (e is FStringExpr fel) { EmitStreamFString(writeStrFn, floatWriteFn, fel); return; }
        if (e is CallExpr { Callee: VariableExpr { Name: "chr" }, Args.Count: 1 } chrEl)
        {
            if (TryEvalConstElement(chrEl.Args[0], out int chrConst)
                && chrConst is >= 0 and <= 255)
                EmitStreamStr(writeStrFn, ((char)chrConst).ToString());
            else
                EmitStreamCharExpr(chrEl.Args[0]);
            return;
        }
        if (e is VariableExpr rev && TryGetRuntimeStr(rev.Name, out var ri))
        {
            EmitRuntimeStrStream(rev.Name, ri.LenVar);
            return;
        }
        if (StaticStringOf(e) is { } constEl) { EmitStreamStr(writeStrFn, constEl); return; }
        throw UserError(JoinElementRefusal(e), e);
    }

    // `sep.join(<compile-time seq>)` in a value position: a list/tuple of compile-time
    // strings folds to a constant; a generator/comprehension materializes into a
    // compiler-managed buffer (EmitJoinRuntimeStr) whose name is the value.
    private Val EmitJoinValue(CallExpr expr, string sep)
    {
        if (expr.Args.Count != 1)
            throw UserError(
                "str.join takes one argument, the sequence to join; this call passes "
                + $"{expr.Args.Count}.", expr.Callee);
        var seq = expr.Args[0];

        if (seq is ListExpr vle
            && vle.Elements.All(e => StaticStringOf(e) != null))
            return InternConstString(string.Join(sep, vle.Elements.Select(e => StaticStringOf(e)!)));
        if (seq is TupleExpr vte
            && vte.Elements.All(e => StaticStringOf(e) != null))
            return InternConstString(string.Join(sep, vte.Elements.Select(e => StaticStringOf(e)!)));

        if (seq is GeneratorExpr or ListCompExpr)
        {
            var (buf, _) = EmitJoinRuntimeStr($"__join{tempCounter++}", sep, seq, expr);
            return new Variable(buf, DataType.UINT8);
        }

        throw UserError(JoinSequenceRefusal(seq), seq);
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
        if (!instanceClasses.TryGetValue(vobj.Name, out var cls) || !IsStdlibClass(cls, "LCD", "lcd")) return null;

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
                throw UserError("f-string format on an LCD is not supported for float values", expr.Callee);
            bool signed = InferExprType(part.Expr!) is DataType.INT8 or DataType.INT16 or DataType.INT32;
            int flags = (upper ? 0x01 : 0) | (signed ? 0x02 : 0) | (pad == '0' ? 0x04 : 0);

            FlushStr();
            VisitCall(new CallExpr(new MemberAccessExpr(sm.Object, "print_fmt"),
                new List<Expression> { part.Expr!, new IntegerLiteral(radix), new IntegerLiteral(width), new IntegerLiteral(flags) }));
        }
        FlushStr();
        return new NoneVal();
    }

    // The keywords print really has. Named so the refusal below and the loop that accepts them
    // cannot drift apart, which is how the loop came to accept everything else in silence.
    private static readonly string[] PrintKeywords = { "sep", "end" };

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
                    else throw UserError($"print() '{kw.Key}' must be a compile-time string literal", expr.Callee);
                }
                else RefuseUnknownKeyword("print", kw.Key, PrintKeywords, expr);
            }
            // `print("val {}".format(x))` streams like the f-string it is. Rewritten here
            // rather than when the call is evaluated, so it takes the streaming path instead
            // of the "f-string in an unsupported position" one.
            else posArgs.Add(RewriteStringBuilding(arg));
        }

        // Resolve the target's write helpers once; the lowering itself is the shared, sink-agnostic
        // machinery (print, uart.write_str/println all reuse it — see the EmitStream* methods).
        string writeStrFn = ResolveWriteStrFn();
        string floatWriteFn = ResolveFloatWriteFn();

        void EmitPrintArg(Expression arg)
        {
            // The message of an exception bound by `except ... as`, in any of its three
            // spellings. The address is a run-time word rather than a literal, and the shared
            // write_str subroutine already walks flash from a pointer in registers, so this is
            // the same call print makes for every other string with a different operand (#369).
            RefuseBadArgsIndex(arg);
            if (TryExceptionMessage(arg, out var exnMsgPtr, out var exnSnap))
            {
                // `e.args[0]` of an exception raised with no argument is CPython's IndexError:
                // args is the empty tuple. A raise without a message leaves the word at zero
                // (and the site id with it), which is what tells the two apart here.
                bool indexChecked = arg is IndexExpr;
                if (indexChecked)
                    EmitExceptionArgsIndexCheck(exnMsgPtr, exnSnap);
                if (programHasDynamicRaiseMessage)
                {
                    // The printers replay the LIVE record's slots, which a nested handled
                    // raise under this handler has overwritten -- restore the snapshot
                    // this binding's name stands for first.
                    if (exnSnap != null)
                        Emit(new ExnRecordMark(exnSnap, Restore: true));
                    // The args-context spelling prints the argument AS an argument: an
                    // errno-carrying OSError renders the bare integer here, where
                    // print(e) renders `[Errno n] NAME`. With no errno raise in the
                    // program EmitExceptionArgsPrint calls the message printer itself.
                    if (arg is IndexExpr) EmitExceptionArgsPrint();
                    else EmitExceptionMessagePrint();
                }
                else if (indexChecked)
                {
                    // EmitExceptionArgsIndexCheck already raised IndexError on the zero case
                    // and does not return from that branch, so exnMsgPtr is proven non-zero
                    // by the time control reaches here -- the zero guard below would just be
                    // asking the same question twice. `print(e.args[0])` used to pay for both:
                    // the check's own IndexError machinery AND a second, always-false zero
                    // jump wrapped around the print it guards.
                    Emit(new Call(ResolveRuntimeWriteStrFn(), new List<Val> { exnMsgPtr }, new NoneVal()));
                }
                else
                {
                    // Zero is "raised with no message": print nothing, as str(E()) is ''.
                    // Handing zero to the writer streamed flash from address 0 instead.
                    string noMsg = MakeLabel();
                    Emit(new JumpIfZero(exnMsgPtr, noMsg));
                    Emit(new Call(ResolveRuntimeWriteStrFn(), new List<Val> { exnMsgPtr }, new NoneVal()));
                    Emit(new Label(noMsg));
                }
                return;
            }

            // `print(e.errno)` prints the integer error code -- a number, not the
            // `[Errno n] NAME` text print(e) renders. Ahead of the probing branches
            // below, which resolve the object as a name and would refuse `e` with the
            // binding diagnostic before the member access is ever visited.
            //
            // Declared at ExceptionArgPrintWidth's answer, not the storage word's own
            // INT32: ResolveDecimalWriteFn keys off this declared width, not the value's
            // real one, to pick the print runtime, and INT32 always pulls in the 32-bit
            // divider (uart_write_decimal_i32 -> __div32). Every raise this handler can
            // catch is a known site, so the widest one of them is a compile-time fact --
            // a table-sized errno costs the u8/u16 writer, and a raise that really can
            // carry a 32-bit value (a runtime OSError(70000), a 32-bit seeded one) still
            // gets the wide writer, never a truncated one. `e.args[0]`
            // (ExceptionArgsItemValue) keeps the wide type for its own VALUE reads: it
            // answers any raise argument, not only an OSError's, and has no per-handler
            // site list to narrow against the same way.
            if (arg is MemberAccessExpr { Object: VariableExpr errnoObj, Member: "errno" }
                && TryGetExceptionBinding(errnoObj.Name, out var errnoB))
            {
                DataType errnoPrintWidth = TryGetExceptionCatchable(errnoObj.Name, out var errnoSites)
                    ? ExceptionArgPrintWidth(errnoSites) : DataType.INT32;
                EmitStreamVal(floatWriteFn,
                    ExceptionErrnoValue(errnoObj.Name, errnoB.ExnType, arg),
                    errnoPrintWidth);
                return;
            }

            // `print(e.args)` prints the whole args tuple -- `()` when the caught raise
            // carried no argument, `(message,)` when it did. Which of the two is a
            // run-time fact on this target (the message word, and the deferred-print
            // site id when the program has one), so EmitExceptionArgsTuplePrint emits
            // both halves behind that check.
            if (arg is MemberAccessExpr { Object: VariableExpr argsObj, Member: "args" }
                && TryGetExceptionBinding(argsObj.Name, out var argsBinding))
            {
                EmitExceptionArgsTuplePrint(writeStrFn, argsBinding.Snap);
                return;
            }

            // `print(chr(n))` is a character, not the number n. chr() yields the byte itself
            // (a char IS its byte on this target), which is right internally and wrong here:
            // the value went to the decimal writer, so print(chr(65)) sent "65" instead of "A".
            if (arg is CallExpr { Callee: VariableExpr { Name: "chr" }, Args.Count: 1 } chrCall)
            {
                if (TryEvalConstElement(chrCall.Args[0], out int chrConst)
                    && chrConst is >= 0 and <= 255)
                {
                    EmitStreamStr(writeStrFn, ((char)chrConst).ToString());
                    return;
                }

                // A run-time code point: one raw byte. Routed through VisitCall with the
                // original expression, because the writer is @inline and a direct Call
                // instruction to it would reference a symbol nobody emits.
                EmitStreamCharExpr(chrCall.Args[0]);
                return;
            }

            // The same character, bound to a NAME. `c = chr(s + 72)` then `print(c)` sent 72: a
            // constant code point keeps its text through the name, a run-time one is a bare
            // byte, and the binding is the only thing that says it is a character (#436).
            if (arg is VariableExpr chrName && CharNameState(chrName.Name) is var chrState and not 0)
            {
                if (chrState < 0)
                    throw UserError(
                        $"'{chrName.Name}' is bound to chr(...) on one path and to something else "
                        + "on another, so print() cannot tell whether it holds a character or a "
                        + "number: on this target a character IS its byte. Print it where each "
                        + "binding happens, or keep the character in a name of its own.", arg);
                // A constant code point's text is filed under the name at the binding, the
                // same place the string branch below would have read it from.
                if (StaticStringOf(arg) is { } chrNameText)
                {
                    EmitStreamStr(writeStrFn, chrNameText);
                    return;
                }
                Val chrVal = VisitExpression(arg);
                if (chrVal is Constant { Text: { } chrText })
                    EmitStreamStr(writeStrFn, chrText);
                else if (chrVal is Constant { Value: >= 0 and <= 255 } chrCode)
                    EmitStreamStr(writeStrFn, ((char)chrCode.Value).ToString());
                else
                    EmitStreamCharVal(chrVal);
                return;
            }

            // The same character, handed back by a FUNCTION. `def make(n): return chr(n)` then
            // `print(make(66))` sent 66: the branch above recognises the `chr(...)` call by
            // its syntax, and a `return` hides it, so the caller saw a bare byte and the
            // decimal writer took it. The scan recorded which functions hand back a
            // character, so the call itself is what the raw byte writer takes (#436).
            if (arg is CallExpr { Callee: VariableExpr charFn } charCall
                && (charReturningFunctions.Contains(ResolveCallee(charFn.Name))
                    || charReturningFunctions.Contains(charFn.Name)))
            {
                // Through the VALUE, not through the expression: an unannotated callee hands
                // its result back in the return register with a `none` destination, and
                // passing the CALL as the writer's argument left that register to be read by
                // whatever came next -- the previous call's character came out instead.
                EmitStreamCharVal(VisitExpression(charCall));
                return;
            }

            // `print(s[i])` is a character too. Python has no char type: `"abcd"[0]` is the
            // one-character string "a", and printing it shows a. The subscript yields the byte,
            // which went to the decimal writer and sent "97".
            if (arg is IndexExpr { Index: not SliceExpr } strIx && StringBehindSubscript(strIx) is { } sText)
            {
                if (TryEvalConstElement(strIx.Index, out int ixConst)
                    && ixConst >= 0 && ixConst < sText.Length)
                {
                    EmitStreamStr(writeStrFn, sText[ixConst].ToString());
                    return;
                }

                EmitStreamCharExpr(strIx);
                return;
            }

            // The same character, out of a RUNTIME string. `s = f"t={x:04d}"` then
            // `print(s[2])` sent 48 -- the code of '0' -- because the lookup above is the only
            // one that ever ran and a run-time string has no compile-time text to look up.
            // Python has no char type either way: `s[2]` is the one-character string at that
            // position, and on this target that string IS the byte, so the raw writer is the
            // whole answer for a constant index and a run-time one alike (#399).
            if (arg is IndexExpr { Index: not SliceExpr, Target: VariableExpr rsTarget } rsSub
                && TryGetRuntimeStr(rsTarget.Name, out _))
            {
                EmitStreamCharExpr(rsSub);
                return;
            }

            // And out of a string PARAMETER of a subroutine, whose text is in flash behind the
            // address the slot holds: `print(msg[0])` sent 104 for "hi".
            if (arg is IndexExpr { Index: not SliceExpr, Target: VariableExpr fsTarget } fsSub
                && IsConstStrParameter(fsTarget.Name))
            {
                EmitStreamCharExpr(fsSub);
                return;
            }

            // A string held in a FIELD. `print(o.n)` and `print(self.n)` sent 256, the string's
            // interned id, because the read fell through to the numeric writer: the plain-name
            // case knew about string constants and the field case did not.
            if (arg is MemberAccessExpr && TryGetCompileTimeText(arg) is { } fieldText)
            {
                EmitStreamStr(writeStrFn, fieldText);
                return;
            }

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

            // `print(sep.join(<compile-time seq>))`: a list of compile-time strings folds
            // to one write; a generator/comprehension unrolls to per-element writes, the
            // same lowering an f-string's parts get.
            if (arg is CallExpr { Callee: MemberAccessExpr { Member: "join" } joinM } joinCall
                && StaticStringOf(joinM.Object) is { } joinSep)
            {
                EmitJoinStream(writeStrFn, floatWriteFn, joinSep, joinCall);
                return;
            }

            // A string PARAMETER of a real subroutine (`const[str]` or `str`, declared or
            // inferred from the call sites). The argument arrives as the
            // flash ADDRESS of the text, and it arrives whole -- `const[str]` lowers to a
            // 16-bit slot -- so the value was right the entire time and only this writer was
            // wrong: it sent the address to the decimal writer and `take("passed")` printed
            // 538. The shared write_str subroutine already walks flash from a pointer in
            // registers, which is the same call print makes for an exception's message.
            //
            // This is the 23rd branch of a 22-branch ladder that dispatches on the SYNTACTIC
            // SHAPE of the argument, which is why each of these holes had to be found one
            // program at a time. The fix that scales is the one #393 took: ask the VALUE
            // whether it stands for text, not the tree. Adding branch 24 without doing that
            // is feeding the problem rather than fixing it.
            //
            // A `str` parameter lowered to a one-byte slot until it got the same pointer-wide
            // slot (ParamStorageType), so its address arrived truncated and could not be
            // streamed; it now can.
            if (arg is VariableExpr strParam && IsConstStrParameter(strParam.Name))
            {
                Emit(new Call(ResolveRuntimeWriteStrFn(),
                    new List<Val> { VisitExpression(arg) }, new NoneVal()));
                return;
            }

            // A string decided at run time: dispatch on the id the name holds.
            if (TryEmitMultiStrStream(writeStrFn, arg)) return;

            // A runtime string (f-string-as-value buffer): stream its bytes up to the
            // tracked length.
            if (arg is VariableExpr rsv && TryGetRuntimeStr(rsv.Name, out var rsInfo))
            {
                EmitRuntimeStrStream(rsv.Name, rsInfo.LenVar);
                return;
            }

            // RFC 0009 section 8: the unnarrowed Optional read that CAN answer --
            // the tag picks the member's repr or "None". Ahead of
            // RejectInstanceInterpolation so a live union FIELD prints too.
            if (TryEmitOptionalStreamOperand(writeStrFn, floatWriteFn, arg)) return;

            // A name bound to None that is not an Optional: an @inline parameter whose
            // default is None and whose call passed nothing, or `x = f()` where f
            // returns nothing. Its slot holds whatever the last expansion left, and the
            // number writer printed that (`g()` printed 0).
            if (arg is VariableExpr or MemberAccessExpr && IsNoneValued(arg))
            {
                EmitStreamStr(writeStrFn, "None");
                return;
            }

            if (arg is BooleanLiteral pbl) { EmitStreamStr(writeStrFn, pbl.Value ? "True" : "False"); return; }
            if (IsBoolExpr(arg)) { EmitStreamBool(writeStrFn, arg); return; }

            // A name bound to a list or tuple literal of run-time values lives in fixed slots,
            // and the bytearray repr below took it for one, or the scalar path printed 0:
            // `v = [GPIOR0.value + 1, ...]; print(v)` printed `0`. Ahead of
            // ModuleConstListValues: an all-constant literal resolves there too, and its
            // path writes tuple brackets for a LIST.
            if (arg is VariableExpr litSeqName && ResolveArrayVar(litSeqName.Name) is { } litSeq
                && TryEmitLiteralSequenceRepr(writeStrFn, litSeq.Name,
                       tupleBoundNames.Contains(litSeq.Name) || IsTupleBound(litSeqName.Name), EmitPrintArg))
                return;

            // `print(str(seq))` on the same literal: the repr text, streamed -- no
            // buffer materialization needed where the print sink is already the wire.
            if (arg is CallExpr { Callee: VariableExpr { Name: "str" }, Args.Count: 1 } pStrCall
                && pStrCall.Args[0] is VariableExpr pStrSeqVe
                && ResolveArrayVar(pStrSeqVe.Name) is { } pStrSeq
                && TryEmitLiteralSequenceRepr(writeStrFn, pStrSeq.Name,
                       tupleBoundNames.Contains(pStrSeq.Name) || IsTupleBound(pStrSeqVe.Name), EmitPrintArg))
                return;

            // `print(_GAINS)` names a module-level tuple of constants: CPython prints
            // the repr `(1, 4, 16, 60)` where the bare name read as a scalar 0.
            if (arg is VariableExpr ptv && ModuleConstListValues(ptv.Name) is { } pcv)
            {
                EmitConstTupleRepr(writeStrFn, floatWriteFn, pcv);
                return;
            }

            // `print(t)` where t names a tuple return (`t = f()`): the tuple CPython
            // would have printed, not the `bytearray(b'...')` repr the fixed slots
            // it lives in would otherwise take. Rewrite to the literal form so the
            // branch below writes the same text.
            if (arg is VariableExpr tupName && NamedTupleElemsOf(tupName.Name) is { } tupElems)
                arg = new TupleExpr(tupElems) { Line = arg.Line };

            // A whole bytearray, an array slice, or a slice of a __getitem__ object
            // (microcontroller.nvm[0:4]): CPython-style bytearray(b'...') repr. As a
            // scalar the array VARIABLE streamed through decimal_u8 and printed garbage.
            if (TryEmitByteArrayReprArg(writeStrFn, arg)) return;

            // `print((a, b))` is the tuple CPython prints, not a tuple the program builds
            // (#375). Adafruit's examples pass one on purpose, because the Mu plotter reads a
            // printed tuple, and `hcsr04_simpletest.py` prints `(sonar.distance,)`.
            //
            // It never has to exist at run time: the text is `(`, the elements with `, `
            // between them, and `)` -- with the ONE-ELEMENT form keeping the trailing comma,
            // which is how CPython tells `(12.5,)` from `(12.5)`. The advice the old refusal
            // gave, a fixed list, does not print the same text either.
            if (arg is TupleExpr printTup)
            {
                EmitStreamStr(writeStrFn, "(");
                for (int ti = 0; ti < printTup.Elements.Count; ++ti)
                {
                    if (ti > 0) EmitStreamStr(writeStrFn, ", ");
                    EmitPrintArg(printTup.Elements[ti]);
                }
                if (printTup.Elements.Count == 1) EmitStreamStr(writeStrFn, ",");
                EmitStreamStr(writeStrFn, ")");
                return;
            }

            if (arg is PreEvaluatedExpr pre)
            {
                if (!TryEmitOptionalStreamVal(writeStrFn, floatWriteFn, pre.Value))
                    EmitStreamVal(floatWriteFn, pre.Value, pre.Declared);
                return;
            }

            RejectInstanceInterpolation(arg);

            // `print(obj.prop)` / `print(f())` where the read or call returns a tuple
            // (Struct.__get__'s `return struct.unpack_from(...)`): the sentinel asks the
            // expansion for the result slots, and the text is the tuple they form.
            if (arg is MemberAccessExpr or CallExpr)
            {
                lastTupleResults.Clear();
                lastTupleResultBuffers = null;
                lastTupleResultLocalBuffers = null;
                pendingTupleCount = -1;
                Val seqVal = VisitExpression(arg);
                pendingTupleCount = 0;
                // Snapshot before emitting: the "(" and ", " writes expand the
                // @inline print_str helper, and an expansion that yields no tuple
                // slots empties lastTupleResults -- the loop would read nothing.
                var printSlots = new List<string>(lastTupleResults);
                var printBufs = lastTupleResultBuffers;
                if (printSlots.Count > 0)
                {
                    EmitStreamStr(writeStrFn, "(");
                    for (int k = 0; k < printSlots.Count; ++k)
                    {
                        if (k > 0) EmitStreamStr(writeStrFn, ", ");
                        string slot = printSlots[k];
                        // A buffer element prints the bytearray(b'...') repr CPython
                        // prints, not a byte of its storage -- the same arg form a
                        // named bytearray takes.
                        if (printBufs is { } && printBufs.TryGetValue(k, out var printBuf))
                        {
                            EmitPrintArg(new VariableExpr(FollowAliases(printBuf))
                                { Line = arg.Line });
                            continue;
                        }
                        EmitPrintArg(new PreEvaluatedExpr(
                            new Variable(slot, variableTypes.TryGetValue(slot, out var sdt)
                                ? sdt
                                : constantVariables.TryGetValue(slot, out int slotConst)
                                    ? WidestElemType(new List<int> { slotConst })
                                    : DataType.UINT8), null)
                            { Line = arg.Line });
                    }
                    if (printSlots.Count == 1) EmitStreamStr(writeStrFn, ",");
                    EmitStreamStr(writeStrFn, ")");
                    return;
                }
                // A call result held as a single GC_REF (`print(f())` where f returns a
                // heap list/tuple): CPython writes the bracketed element repr, not the
                // pointer the decimal writer would show.
                string? seqResName = seqVal switch
                { Variable sv => sv.Name, Temporary st => st.Name, _ => null };
                if (seqResName != null
                    && listVarElemTypes.TryGetValue(seqResName, out var callSeqElem))
                {
                    EmitSeqRepr(writeStrFn, floatWriteFn, seqVal, callSeqElem,
                        (lastCallReturnTypeText?.Contains("tuple") == true
                         || lastCallReturnTypeText?.Contains("Tuple") == true)
                        || IsTupleBound(seqResName));
                    return;
                }
                // An inline callee's `return <literal list local>` hands back its slots.
                if (seqResName != null
                    && TryEmitLiteralSequenceRepr(writeStrFn, seqResName, IsTupleBound(seqResName), EmitPrintArg))
                    return;
                // `print(str(v))` / `print(join(...))`: the call built a runtime-string
                // buffer -- stream its bytes, not the buffer pointer's decimal.
                if (seqVal is Variable seqRv && TryGetRuntimeStr(seqRv.Name, out var seqRInfo))
                {
                    EmitRuntimeStrStream(seqRv.Name, seqRInfo.LenVar);
                    return;
                }
                // `print(f())` / `print(obj.prop)` on a tagged union result: the
                // tag the callee returned decides "None" or the member's repr.
                if (TryEmitOptionalStreamVal(writeStrFn, floatWriteFn, seqVal)) return;
                EmitStreamVal(floatWriteFn, seqVal, DeclaredWidthOfName(arg));
                return;
            }

            // `print(xs)` on a heap list or tuple held in a name: the bracketed element
            // repr, same as a call result above, not the pointer's decimal value.
            if (arg is VariableExpr seqVe
                && ResolveListVarQualified(seqVe.Name) is { Length: > 0 } seqQual
                && listVarElemTypes.TryGetValue(seqQual, out var seqVarElem))
            {
                EmitSeqRepr(writeStrFn, floatWriteFn,
                    new Variable(seqQual, DataType.GC_REF), seqVarElem,
                    tupleBoundNames.Contains(seqQual) || IsTupleBound(seqVe.Name));
                return;
            }

            // The declared width of a NAME travels with its value, because a folded constant no
            // longer carries one (#331): `lo: int32 = -2147483648` printed its low byte.
            Val argV = VisitExpression(arg);
            // A subscript whose __getitem__ hands back a heap list (`print(buf[0])` on
            // adafruit_pixelbuf) is a list value like a call result above; the decimal
            // writer would print its pointer.
            if (arg is IndexExpr
                && (argV switch { Variable iv => iv.Name, Temporary it => it.Name, _ => null })
                    is { } idxResName)
            {
                if (listVarElemTypes.TryGetValue(idxResName, out var idxSeqElem))
                {
                    EmitSeqRepr(writeStrFn, floatWriteFn, argV, idxSeqElem, IsTupleBound(idxResName));
                    return;
                }
                if (TryEmitLiteralSequenceRepr(writeStrFn, idxResName, IsTupleBound(idxResName), EmitPrintArg))
                    return;
            }
            // A guarded Optional op result is a tagged temp: print it member-wise
            // (`print(x + 1)` on Optional reads the tag, not the widest width).
            if (TryEmitOptionalStreamVal(writeStrFn, floatWriteFn, argV)) return;
            EmitStreamVal(floatWriteFn, argV, DeclaredWidthOfName(arg));
        }

        if (posArgs.Count == 0)
        {
            EmitStreamStr(writeStrFn, endStr);
            return new NoneVal();
        }

        // Every operand that has text in front of it runs before that text is written, which is
        // the order CPython's one-piece write gives (#371). See PreEvaluatePrintOperand.
        bool written = false;
        for (int i = 0; i < posArgs.Count; ++i)
            posArgs[i] = PreEvaluatePrintOperand(posArgs[i], ref written,
                posArgs.Skip(i + 1).Any(PrintOperandHasAnEffect));

        for (int i = 0; i < posArgs.Count; ++i)
        {
            if (i > 0) EmitStreamStr(writeStrFn, sepStr);
            EmitPrintArg(posArgs[i]);
        }

        EmitStreamStr(writeStrFn, endStr);
        return new NoneVal();
    }

    /// <summary>
    /// The repr of a compile-time sequence laid out from a list or tuple literal: `[a, b]`,
    /// `(a, b)`, `(a,)`. Each element is read from its slot -- a `name__k` variable, or the
    /// SRAM array when the sequence is indexed at run time; an all-constant literal has no
    /// slots at all, so its element constants are taken from ctArrayConstElements. The
    /// recorded LiteralSeqElemKind spells a bool True/False and a string quoted, because
    /// the slot stores both as bare numbers. False when the key is not one.
    /// </summary>
    private bool TryEmitLiteralSequenceRepr(string writeStrFn, string key, bool isTuple,
                                            Action<Expression> printArg)
    {
        if (!literalSequenceArrays.Contains(key) || !arraySizes.TryGetValue(key, out int count))
            return false;
        DataType elemDt = arrayElemTypes.TryGetValue(key, out var et) ? et : DataType.UINT8;
        bool inSram = arraysWithVariableIndex.Contains(key) || moduleSramArrays.Contains(key);
        literalSeqElemKinds.TryGetValue(key, out var kinds);
        ctArrayConstElements.TryGetValue(key, out var constElems);
        EmitStreamStr(writeStrFn, isTuple ? "(" : "[");
        for (int k = 0; k < count; k++)
        {
            if (k > 0) EmitStreamStr(writeStrFn, ", ");
            var (kind, arg) = kinds != null && k < kinds.Count
                ? kinds[k] : (LiteralSeqElemKind.Number, (string?)null);
            if (kind == LiteralSeqElemKind.Str)
            {
                EmitStreamStr(writeStrFn, PyStrReprText(arg!));
                continue;
            }
            if (kind == LiteralSeqElemKind.RuntimeStr
                && TryGetRuntimeStr(arg!, out var rsrc))
            {
                EmitStreamStr(writeStrFn, "'");
                EmitRuntimeStrStream(arg!, rsrc.LenVar);
                EmitStreamStr(writeStrFn, "'");
                continue;
            }
            Val elem;
            if (constElems != null && k < constElems.Count)
                elem = new Constant(constElems[k]);
            else if (inSram)
            {
                var t = MakeTemp(elemDt);
                Emit(new ArrayLoad(key, new Constant(k), t, elemDt, count));
                elem = t;
            }
            else
            {
                string slot = key + "__" + k;
                elem = new Variable(slot, variableTypes.TryGetValue(slot, out var sdt) ? sdt : elemDt);
            }
            if (kind == LiteralSeqElemKind.Bool)
            {
                if (elem is Constant bc)
                    EmitStreamStr(writeStrFn, bc.Value != 0 ? "True" : "False");
                else
                    EmitStreamBool(writeStrFn,
                        new BinaryExpr(new PreEvaluatedExpr(elem, null),
                            Frontend.BinaryOp.NotEqual, new IntegerLiteral(0)));
                continue;
            }
            printArg(new PreEvaluatedExpr(elem, null));
        }
        if (isTuple && count == 1) EmitStreamStr(writeStrFn, ",");
        EmitStreamStr(writeStrFn, isTuple ? ")" : "]");
        return true;
    }

    // funcref(fn): resolve a function name (through any alias chain) to a FunctionRef
    // value (a function pointer usable in Callable[N] arrays / indirect calls).
    private Val EmitFuncrefIntrinsic(CallExpr expr)
    {
        if (expr.Args.Count != 1)
            throw UserError("funcref() expects exactly one argument: a function name", expr.Callee);
        if (expr.Args[0] is not VariableExpr fnRefExpr)
            throw UserError("funcref() argument must be a function name identifier", ArgAt(expr, 0));

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
    private int IsrCallLine(CallExpr expr) =>
        expr.Line > 0 ? expr.Line : (currentStmtLine > 0 ? currentStmtLine : lastLine);

    /// <summary>
    /// `claim(key, value, owner="", hint="")`: a compile-time resource claim. Emits nothing.
    ///
    /// The first claim on a key records the value and its owner. A later claim with the same
    /// value adds its owner. A later claim with a DIFFERENT value is refused where it is
    /// written, unless the only owner so far is the claimant itself (a channel retuning the
    /// timer it alone uses). Measured need: two PWM channels of one AVR timer both wrote the
    /// prescaler and the last construction won in silence, PyMCU#300; a run-time check was
    /// measured at +104 bytes on a 52-byte program because the raise drags in the exception
    /// runtime, which is why the claim is a compile-time fact and not an instruction.
    ///
    /// `key`, `owner` and `hint` must be compile-time strings (a literal, a const[str]
    /// parameter, a string constant). `value` must fold to a constant; a run-time value has
    /// nothing to claim and is let through, so the visit of it is the only thing this call
    /// can cost, and a bare local costs nothing.
    /// </summary>
    /// A claim() string argument: a literal or a name the AST resolver knows, else whatever
    /// the expression folds to when visited -- a const[str] parameter bound two @inline
    /// expansions deep (`pin` in the chip module's helper) reaches the visitor as the
    /// interned id of its text. Visiting a name emits nothing.
    private string ClaimString(CallExpr expr, int index, string what)
    {
        string? text = StaticStringOf(expr.Args[index]);
        if (text != null) return text;
        Val v = VisitExpression(expr.Args[index]);
        if (v is Constant c && stringIdToStr.TryGetValue(c.Value, out var s)) return s;
        throw UserError($"claim() {what} must be a compile-time string", ArgAt(expr, index));
    }

    private Val EmitClaimIntrinsic(CallExpr expr)
    {
        if (expr.Args.Count < 2 || expr.Args.Count > 4)
            throw UserError("claim() takes claim(key, value, owner=\"\", hint=\"\")", expr.Callee);

        string key = ClaimString(expr, 0, "key");
        string owner = expr.Args.Count > 2 ? ClaimString(expr, 2, "owner") : "";
        string hint = expr.Args.Count > 3 ? ClaimString(expr, 3, "hint") : "";

        // Only a branch that is decided at compile time can hold a claim. Inside a branch
        // that stays a run-time decision (the HAL's threshold chain over a run-time
        // frequency, say) every arm would register its own value and the arms would
        // conflict with each other; nothing is known there, so nothing is claimed. Same
        // rule and same measure as a `raise CompileError` in that position.
        int baseDepth = inlineStack.Count > 0 ? inlineStack[^1].EntryBranchDepth : 0;
        if (_runtimeBranchDepth > baseDepth) return new NoneVal();

        Val v = VisitExpression(expr.Args[1]);
        if (v is not Constant c) return new NoneVal();   // a run-time value: nothing to hold

        int line = inlineDepth > 0 && currentStmtLine > 0 ? currentStmtLine : expr.Line;
        // `file:line`, never a bare "line N", even for the entry file. Two reasons, and the
        // second is the measured one (#303). A message that says only "line 55" does not say
        // which file, and the reader of a program with an imported HAL has several. And the
        // build driver compiles a SYNTHETIC entry under dist/_generated -- print() alone adds
        // four lines to it -- so every line the compiler states for the entry file is in that
        // file's numbering and has to be mapped back. The driver maps a header by recognising
        // `path:line`; giving the citation the same shape is what lets it map this one too. A
        // bare number could not be told from any other number in the sentence: "already 3 for
        // PD6 at line 43" named a line past the end of a 41-line file, and was correct only in
        // a file the reader never wrote.
        string? sitePath = CallSiteSourcePath();
        string site = $"{(sitePath != null ? Path.GetFileName(sitePath) : EntryFileName)}:{line}";
        string who = owner.Length > 0 ? owner : "an earlier site";

        if (!claims.TryGetValue(key, out var rec))
        {
            rec = new ClaimRecord { Value = c.Value, Site = site };
            rec.Owners.Add(who);
            claims[key] = rec;
            return new NoneVal();
        }
        if (rec.Value == c.Value)
        {
            if (!rec.Owners.Contains(who)) rec.Owners.Add(who);
            return new NoneVal();
        }
        if (rec.Owners.Count == 1 && rec.Owners[0] == who)
        {
            rec.Value = c.Value;    // the sole owner may retune what only it uses
            rec.Site = site;
            return new NoneVal();
        }

        string others = string.Join(" and ", rec.Owners.Where(o => o != who));
        if (others.Length == 0) others = string.Join(" and ", rec.Owners);
        string msg = $"{key}: already {rec.Value} for {others} at {rec.Site}, and {who} asks for {c.Value}";
        if (hint.Length > 0) msg += ". " + hint;
        throw new ArchitectureError(msg, line, 0) { File = sitePath, LocationIsFinal = true };
    }

    /// <summary>The compile-time value a result slot holds, when it holds one.</summary>
    private bool TryFoldedConstant(Val v, out int value)
    {
        value = 0;
        string? key = v switch { Variable var => var.Name, Temporary tmp => tmp.Name, _ => null };
        if (key == null) return false;
        for (int depth = 0; depth < 20; ++depth)
        {
            if (constantVariables.TryGetValue(key, out value)) return true;
            if (!variableAliases.TryGetValue(key, out var next) || next == null) return false;
            key = next;
        }
        return false;
    }

    /// <summary>
    /// The compile-time value a call argument holds, if it holds one. Follows the alias chain
    /// and asks BOTH maps: constantVariables, which tracks module level, and
    /// localConstantValues, which tracks what a function-local was last assigned.
    /// </summary>
    private bool TryArgumentConstant(string name, out int value)
    {
        string chase = name;
        for (int hop = 0; hop < 20; ++hop)
        {
            if (constantVariables.TryGetValue(chase, out value)) return true;
            if (localConstantValues.TryGetValue(chase, out value) && !ForeignFlowRead(chase)) return true;
            if (!variableAliases.TryGetValue(chase, out var next) || next == null) break;
            chase = next;
        }
        value = 0;
        return false;
    }

    // Whether the argument that produced <paramref name="vArg"/> names caller-side storage
    // the expansion of <paramref name="func"/> can write: a plain name when it is a mutable
    // global, a field when any reachable method assigns that member, and a `mod.x` module
    // attribute under either write shape. Only an int or float scalar is in play: storage,
    // text and instances keep their identity binding.
    private bool ExpansionMayWriteArg(FunctionDef func, Variable vArg, Expression? rawExpr) =>
        vArg.Type <= DataType.FLOAT && !NameStandsForStorage(vArg.Name)
        && string.IsNullOrEmpty(GetValClass(vArg))
        && rawExpr switch
        {
            VariableExpr gve when mutableGlobals.ContainsKey(vArg.Name) =>
                ExpansionMayWrite(func, gve.Name, null),
            // `mod.x` is another module's global, which a `global` statement in a reachable
            // body writes by the member's own name; an instance's field is written through
            // a receiver assignment (`self.x = ...`).
            MemberAccessExpr fme => ExpansionMayWrite(func,
                fme.Object is VariableExpr mo && importedAliases.ContainsKey(mo.Name)
                    ? fme.Member : null,
                fme.Member),
            _ => false,
        };

    // Whether expanding <paramref name="func"/> can write the module global
    // <paramref name="global"/> or a field named <paramref name="member"/>: the body or any
    // function it can reach, by name. A global counts only where a `global` statement makes
    // the assignment reach it; a field counts on any receiver, since which object a method
    // runs on is not known here.
    private bool ExpansionMayWrite(FunctionDef func, string? global, string? member)
    {
        var seen = new HashSet<FunctionDef>();
        var work = new Stack<FunctionDef>();
        work.Push(func);
        while (work.Count > 0)
        {
            var f = work.Pop();
            if (!seen.Add(f)) continue;
            var names = new HashSet<string>();
            var receivers = new HashSet<(string, string)>();
            CollectMutatedNames(f.Body, names, receivers);
            if (global != null && names.Contains(global)
                && TypeInference.WalkStatements(f.Body)
                    .Any(st => st is GlobalStmt g && g.Names.Contains(global)))
                return true;
            if (member != null && receivers.Any(r => r.Item2 == "=" + member))
                return true;
            var callees = new HashSet<string>(receivers.Where(r => !r.Item2.StartsWith('='))
                .Select(r => r.Item2));
            foreach (var st in TypeInference.WalkStatements(f.Body))
                CollectCalleeNames(st, callees);
            foreach (var name in callees)
                if (FunctionsByName().TryGetValue(name, out var defs))
                    foreach (var d in defs) work.Push(d);
        }
        return false;
    }

    private Dictionary<string, List<FunctionDef>>? functionsByName;

    private Dictionary<string, List<FunctionDef>> FunctionsByName()
    {
        if (functionsByName != null) return functionsByName;
        functionsByName = new Dictionary<string, List<FunctionDef>>();
        foreach (var f in functionSourcePath.Keys
                     .Concat(instanceMethodDefs.Values).Concat(methodAstByName.Values)
                     .Concat(inlineFunctions.Values.OfType<FunctionDef>()).Distinct())
        {
            if (!functionsByName.TryGetValue(f.Name, out var l)) functionsByName[f.Name] = l = new();
            l.Add(f);
        }
        return functionsByName;
    }

    // The bare names of the plain functions a statement calls, `f(...)` anywhere in it.
    private static void CollectCalleeNames(Statement st, HashSet<string> into)
    {
        void E(Expression? e)
        {
            switch (e)
            {
                case null: return;
                case CallExpr c:
                    if (c.Callee is VariableExpr cv) into.Add(cv.Name);
                    E(c.Callee);
                    foreach (var a in c.Args) E(a);
                    return;
                case KeywordArgExpr k: E(k.Value); return;
                case BinaryExpr b: E(b.Left); E(b.Right); return;
                case UnaryExpr u: E(u.Operand); return;
                case MemberAccessExpr m: E(m.Object); return;
                case IndexExpr ix: E(ix.Target); E(ix.Index); return;
                case TernaryExpr t: E(t.Condition); E(t.TrueVal); E(t.FalseVal); return;
                case WalrusExpr w: E(w.Value); return;
                case ListExpr l: foreach (var x in l.Elements) E(x); return;
                case TupleExpr tu: foreach (var x in tu.Elements) E(x); return;
                case FStringExpr fs: foreach (var p in fs.Parts) E(p.Expr); return;
                default: return;
            }
        }
        switch (st)
        {
            case AssignStmt a: E(a.Target); E(a.Value); break;
            case AugAssignStmt aug: E(aug.Target); E(aug.Value); break;
            case AnnAssign an: E(an.Value); break;
            case VarDecl vd: E(vd.Init); break;
            case TupleUnpackStmt tu: E(tu.Value); break;
            case ExprStmt es: E(es.Expr); break;
            case ReturnStmt r: E(r.Value); break;
            case ForStmt f: E(f.Iterable); E(f.RangeStart); E(f.RangeStop); E(f.RangeStep); break;
            case WhileStmt w: E(w.Condition); break;
            case IfStmt i:
                E(i.Condition);
                foreach (var (cond, _) in i.ElifBranches) E(cond);
                break;
            case WithStmt wi: E(wi.ContextExpr); break;
            case MatchStmt m: E(m.Target); break;
        }
    }

    /// <summary>
    /// Whether <paramref name="func"/> assigns to its own parameter. Such a parameter is bound
    /// by alias so the write reaches the caller's name; binding it as a constant would drop
    /// the write.
    /// </summary>
    private static bool ParameterIsAssignedIn(FunctionDef func, string paramName)
    {
        var names = new HashSet<string>();
        var receivers = new HashSet<(string, string)>();
        CollectMutatedNames(func.Body, names, receivers);
        return names.Contains(paramName);
    }

    /// <summary>
    /// The compile-time value of an argument EXPRESSION, with the caller's locals in scope, or
    /// false when it has none. A width conversion is transparent while the value fits the width
    /// it converts to; where it would truncate, the number the callee sees is not the one the
    /// folder returns, so the argument is left to the run-time path.
    /// </summary>
    private bool TryFoldArgumentExpression(Expression e, string paramType, string callerPrefix,
                                           out int value)
    {
        bool saved = foldLocalConstants;
        // The expression was WRITTEN in the caller, and by the time the parameters are bound
        // `currentInlinePrefix` is already the callee's -- so a name in it has to be looked up
        // under the caller's prefix or it is not found at all.
        string savedInline = currentInlinePrefix;
        foldLocalConstants = true;
        currentInlinePrefix = callerPrefix;
        try
        {
            if (!TryFoldThroughConversions(e, out value)) return false;
            // The parameter's own width has the last word for the same reason.
            return FitsInScalar(value, paramType);
        }
        finally
        {
            foldLocalConstants = saved;
            currentInlinePrefix = savedInline;
        }
    }

    private bool TryFoldThroughConversions(Expression e, out int value)
    {
        if (e is CallExpr { Callee: VariableExpr conv, Args.Count: 1 } call
            && IsScalarWidthName(conv.Name))
        {
            if (!TryFoldThroughConversions(call.Args[0], out value)) return false;
            return FitsInScalar(value, conv.Name);
        }

        try { value = EvaluateConstantExpr(e); return true; }
        catch { value = 0; return false; }
    }

    private static bool IsScalarWidthName(string name) => name
        is "uint8" or "int8" or "uint16" or "int16" or "uint32" or "int32" or "int" or "bool";

    /// <summary>Whether a folded value survives a store of the named width unchanged.</summary>
    private static bool FitsInScalar(int value, string type) => type switch
    {
        "uint8" or "bool" => value is >= 0 and <= 255,
        "int8" => value is >= -128 and <= 127,
        "uint16" => value is >= 0 and <= 65535,
        "int16" or "int" => value is >= -32768 and <= 32767,
        "uint32" => value >= 0,
        "int32" => true,
        // An unannotated or non-numeric parameter: the store width is not stated here, so the
        // value is only carried when it fits the narrowest one the backend could choose.
        _ => value is >= 0 and <= 255,
    };

    private Val EmitCompileIsrIntrinsic(CallExpr expr)
    {
        if (expr.Args.Count != 2)
            throw UserError("compile_isr() expects exactly 2 arguments: compile_isr(handler, vector)", expr.Callee);
        Val vecVal = VisitExpression(expr.Args[1]);
        int vector = 0;
        if (vecVal is Constant c) vector = c.Value;
        // An @inline function whose body is a `match` over const arms returning literals HAS a
        // compile-time value; what it hands back is the slot that value was folded into, and
        // only a bare Constant was accepted. Composing the vector out of the GPIO module's pin
        // table was refused for it, so a HAL module that owns an interrupt had to write the
        // pin-to-vector table out a second time (#321).
        else if (TryFoldedConstant(vecVal, out int foldedVec)) vector = foldedVec;
        else throw UserError("compile_isr() second argument (vector) must be a compile-time constant", ArgAt(expr, 1));

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
            throw UserError("compile_isr() first argument must be a function reference or 0", ArgAt(expr, 0));
        }

        if (!handlerProvided) return new NoneVal();

        // A global the handler declares is written between any two instructions of the
        // code the vector fires into, so the value a watched store gave it is provable
        // nowhere -- inside the handler's own body least of all. pulse_isr's
        // `if _pulse_armed == 0` folded to the clear's store whenever the handler lowered
        // after the module level that ran the clear (the entry file builds its sensor at
        // top level, which is exactly what hoists main's lowering ahead of it), and the
        // arm's `return` discarded the whole recording tail as dead.
        KillIsrDeclaredGlobals(handlerFuncName);

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
                RegisterIsr(synthName, vector, expr);
                return new NoneVal();
            }
            // Synthesis returned empty -- fall through to original name (will fail if ZCA param)
        }

        RegisterIsr(handlerFuncName, vector, expr);
        return new NoneVal();
    }

    /// <summary>
    /// Files one `compile_isr(handler, vector)`. A handler already registered at ANOTHER
    /// vector is refused here instead of overwriting the first registration: the table holds
    /// one vector per routine, so the earlier vector was silently left on
    /// `__bad_interrupt` and every edge on that pin was lost. Two pins handled by the same
    /// code on two vectors -- a quadrature encoder on INT0 and INT1 is the case every Arduino
    /// guide wires -- is exactly the shape that hit it (#325).
    /// </summary>
    private void RegisterIsr(string handlerName, int vector, CallExpr expr)
    {
        if (pendingIsrRegistrations.TryGetValue(handlerName, out int already) && already != vector)
        {
            pendingIsrOrigins.TryGetValue(handlerName, out var first);
            string firstAt = first.Line > 0 ? $" (registered at line {first.Line})" : "";
            throw UserError(
                $"'{handlerName}' is already the handler for interrupt vector 0x{already:X4}" +
                $"{firstAt}, and this call asks for 0x{vector:X4}. A routine can only sit at " +
                "one vector: the table has one entry per routine, and the earlier vector would " +
                "be left on the bad-interrupt handler. Give the second vector a function of " +
                "its own that calls the shared body -- `def on_int1(): step()` -- and register " +
                "that.", expr.Callee);
        }

        pendingIsrRegistrations[handlerName] = vector;
        pendingIsrOrigins[handlerName] = (currentFunction, IsrCallLine(expr), currentModulePrefix);
    }

    /// <summary>
    /// Every name the handler's `global` statements declare is a name it may write, and an
    /// interrupt writes between any two instructions of the code around the call site -- so
    /// no store the compiler watched is the value a later read provably sees. The constant
    /// the write recorded is removed AND the name marked, because the next constant store
    /// would record it again (the same hazard loop invalidation has for method-written
    /// fields).
    /// </summary>
    private void KillIsrDeclaredGlobals(string handlerName)
    {
        FunctionDef? def = null;
        string? pfx = null;
        foreach (var entry in functionsToCompile)
            if (entry.Prefix + entry.Func.Name == handlerName)
            {
                def = entry.Func;
                pfx = entry.Prefix;
                break;
            }
        def ??= methodAstByName.GetValueOrDefault(handlerName)
              ?? instanceMethodDefs.GetValueOrDefault(handlerName)
              ?? inlineFunctions.GetValueOrDefault(handlerName);
        if (def == null) return;
        pfx ??= handlerName.EndsWith(def.Name, StringComparison.Ordinal)
            ? handlerName[..^def.Name.Length]
            : currentModulePrefix;
        var declared = new HashSet<string>();
        CollectGlobalDeclarations(def.Body, declared);
        foreach (var g in declared)
        {
            string key = pfx + g;
            killedConstants.Add(key);
            constantVariables.Remove(key);
            strConstantVariables.Remove(key);
            localConstantValues.Remove(key);
        }
    }

    /// An argument of a call to an outlined method, in the type of the parameter it binds.
    /// A compile-time float was rounded to an int whatever the parameter was, so `p.f(-3.0)`
    /// for `def f(self, a: float)` passed the integer -3, the callee read its bytes as a float
    /// and saw 0.0. Only a parameter that is not a float still collapses a compile-time float;
    /// a float parameter takes an integer as the float it is, as EmitExternCall does.
    private Val CoerceOutlinedArg(string callee, int pidx, Val av)
    {
        bool floatParam = functionParamTypes.TryGetValue(callee, out var pts)
            && pidx >= 0 && pidx < pts.Count && pts[pidx] == DataType.FLOAT
            && !IsTaggedParam(callee, pidx);
        if (!floatParam)
            return av is FloatConstant fc ? new Constant((int)Math.Round(fc.Value)) : av;
        if (av is Constant { Text: null } ic) return new FloatConstant(ic.Value);
        if (av is Variable or Temporary && IsScalarIntType(GetValType(av)))
        {
            var asFloat = MakeTemp(DataType.FLOAT);
            Emit(new Copy(av, asFloat));
            return asFloat;
        }
        return av;
    }

    // Call into a C extern function (@extern): coerce float args to ints per the C ABI
    // and emit a direct Call to the resolved C symbol.
    private Val EmitExternCall(CallExpr expr, string callee, string cSym)
    {
        // The declared parameter decides what crosses: a float literal passed to a `float`
        // parameter used to be rounded to an int here, so C read the integer bit pattern as a
        // float. Only a parameter that is not a float still collapses a compile-time float.
        functionParamTypes.TryGetValue(callee, out var extParamTypes);
        var extArgs = new List<Val>();
        for (int ai = 0; ai < expr.Args.Count; ai++)
        {
            Val av = VisitExpression(expr.Args[ai]);
            DataType pType = extParamTypes != null && ai < extParamTypes.Count
                ? extParamTypes[ai]
                : DataType.UNKNOWN;

            if (pType == DataType.FLOAT)
            {
                // An integer literal in a float position is the C promotion, done here so the
                // backend stages it through the float argument registers.
                if (av is Constant ic) av = new FloatConstant(ic.Value);
                else if (av is Variable fv2 && floatConstantVariables.TryGetValue(fv2.Name, out double fvv))
                    av = new FloatConstant(fvv);
            }
            else if (av is FloatConstant avFc)
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
            return new NoneVal(LiveCallResult: true);
        }

        Temporary extDst = MakeTemp(DataTypeExtensions.StringToDataType(functionReturnTypes[callee]));
        Emit(new Call(cSym, extArgs, extDst));
        return extDst;
    }

    // The array an expression NAMES, when it names one: an ordinary array/bytearray, or an
    // instance-member array. Used to refuse ptr(<array>), where evaluating the argument
    // would read the array's first element instead of taking its address (#413). Resolving
    // a name emits no IR.
    private string? NamedArrayArgument(Expression e)
    {
        switch (e)
        {
            case VariableExpr ve:
                foreach (string? key in new[]
                         {
                             string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + ve.Name,
                             string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + ve.Name,
                             ve.Name,
                         })
                    if (key != null && TryResolveArrayStorageKey(key, out _)) return ve.Name;
                return null;
            case MemberAccessExpr mem when ResolveMemberArrayName(mem) != null:
                return (mem.Object is VariableExpr mv ? mv.Name + "." : "") + mem.Member;
            default:
                return null;
        }
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
    /// <summary>
    /// `list[T]`, and `array.array` (whose element width is never in the TEXT -- it comes from
    /// whatever the body actually builds and returns), both reach the same UNKNOWN width in
    /// StringToDataType. Getting this wrong at the point a call result's Temporary is CREATED
    /// is not something a later dictionary registration can repair: codegen moves bytes by the
    /// WIDTH baked into that Temporary's own Type field, not by a name looked up afterwards, so
    /// a 1-byte-wide temp holding a 2-byte GC pointer had its high byte zeroed on the way into
    /// the caller's list variable -- silently, since 8-bit garbage still looks like a plausible
    /// (wrong) address (PyMCU#433).
    /// </summary>
    private static bool IsListLikeReturnType(string? returnType)
        => returnType != null && (returnType.StartsWith("list[") || returnType == "list"
                                  || returnType == "array.array"
                                  || returnType == "tuple" || returnType.StartsWith("tuple[")
                                  || returnType.StartsWith("Tuple[")
                                  || returnType == "NamedTuple");

    // The element type of an outlined callee's sequence result, when it is
    // resolvable before the callee's own body emits: `-> list[T]` names it in the
    // annotation; a bare `-> list`/`-> tuple` answers through the `return <seq>`
    // name the scan recorded -- a returned parameter takes the bound argument's
    // element type, a returned module-level sequence its own registration.
    private DataType? ResolveOutlinedSeqElem(string callee, string? rType, List<Val> argValues)
    {
        if (rType is { Length: > 0 } rt && rt.StartsWith("list[") && rt.EndsWith("]"))
            return DataTypeExtensions.StringToDataType(rt.Substring(5, rt.Length - 6));
        if (!funcReturnSeqExprs.TryGetValue(callee, out var seq)) return null;
        // A returned parameter answers with the argument bound at THIS call.
        if (functionParams.TryGetValue(callee, out var fparams)
            && fparams.IndexOf(seq.Name) is int pi && pi >= 0 && pi < argValues.Count)
        {
            string? argName = argValues[pi] switch
            { Variable av => av.Name, Temporary at => at.Name, _ => null };
            if (argName != null && ResolveListVarQualified(argName) is { Length: > 0 } argKey
                && listVarElemTypes.TryGetValue(argKey, out var argElem))
                return argElem;
        }
        // A module-level sequence of the callee's own module.
        if (listVarElemTypes.TryGetValue(seq.ModulePrefix + seq.Name, out var ge)) return ge;
        if (listVarElemTypes.TryGetValue(seq.Name, out var ge2)) return ge2;
        return null;
    }

    /// <summary>
    /// A `Union[A, B, ...]` parameter is resolved at its call site (CheckAnnotationNames'
    /// `allowUnion`): the argument's type there is known, and has to be one of the members.
    /// Refuses when it is none of them, naming the members, rather than let an unmatched shape
    /// bind as whichever the rest of the pipeline happens to treat it as. `Optional[X]` is
    /// already read as X and never reaches here as a union.
    /// </summary>
    private void CheckUnionArgumentMatchesAMember(FunctionDef func, int paramIdx, Expression? rawArg, Val argVal)
    {
        string ptype = func.Params[paramIdx].Type ?? "";
        // `val: ColorUnion` is `val: Union[...]` under the alias the discarded `from typing
        // import` try recorded -- resolve before the shape test the same way
        // CheckAnnotationNames does.
        for (int hops = 0; typeAliases.TryGetValue(ptype, out var aliasedP) && hops < 8; hops++)
            ptype = aliasedP;
        if (!ptype.StartsWith("Union[") || !ptype.EndsWith("]") || rawArg == null) return;

        var members = PyMCU.Common.AnnotationText.SplitTopLevel(ptype[6..^1])
            .Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
        if (members.Count == 0) return;

        // A member that is itself an alias -- `Union[ColorUnion, Sequence[ColorUnion]]` --
        // stands for the text it was bound to; a member that resolves to another union
        // contributes its members, which is what `Union[int, Tuple[...]]` inside one means.
        var expanded = new List<string>();
        var work = new Queue<string>(members);
        for (int hops = 0; work.Count > 0 && hops < 64; hops++)
        {
            string m = work.Dequeue();
            if (typeAliases.TryGetValue(m, out var aliasedM)
                && aliasedM.StartsWith("Union[") && aliasedM.EndsWith("]"))
            {
                foreach (var inner in PyMCU.Common.AnnotationText.SplitTopLevel(aliasedM[6..^1]))
                    if (inner.Trim().Length > 0) work.Enqueue(inner.Trim());
            }
            else
            {
                expanded.Add(typeAliases.TryGetValue(m, out var leaf) ? leaf : m);
            }
        }
        members = expanded.Count > 0 ? expanded : members;

        static string Bare(string m) => m.Contains('.') ? m[(m.LastIndexOf('.') + 1)..] : m;

        void Refuse() => throw UserError(
            $"'{func.Params[paramIdx].Name}' is declared 'Union[{string.Join(", ", members)}]', " +
            "and this argument's type matches none of those members", rawArg);

        string? argClass = argVal switch
        {
            Variable v => instanceClasses.GetValueOrDefault(v.Name),
            Temporary t => instanceClasses.GetValueOrDefault(t.Name),
            _ => null,
        };
        if (argClass == null && rawArg is CallExpr { Callee: VariableExpr ctorVe }
            && classNames.Contains(ResolveCallee(ctorVe.Name)))
            argClass = ResolveCallee(ctorVe.Name);

        if (argClass != null)
        {
            if (members.Any(m => Bare(m) == argClass || argClass!.EndsWith("_" + Bare(m), StringComparison.Ordinal)))
                return;
            // A Protocol member is structural (#465): DigitalInOut is not named ROValueIO,
            // but it has the `.value` property the protocol asks for, and CPython accepts it.
            if (members.Any(m => ClassSatisfiesProtocol(argClass!, Bare(m))))
                return;
            Refuse();
            return;
        }

        // A fixed array/tuple literal, or a name bound to one (#352-style compile-time
        // sequence): `List[int]`/`Tuple[int, ...]` is not a representable run-time type here,
        // and the array it names is exactly what such an argument already is.
        bool isSeq = rawArg is ListExpr or TupleExpr
            || (rawArg is VariableExpr seqVe && ResolveConstSequence(seqVe.Name) != null);
        if (isSeq)
        {
            // `Sequence[X]`/`Iterable[X]` reach here un-normalized inside a union's member
            // list -- the same compile-time sequence a `list[X]` member asks for.
            if (members.Any(m => Bare(m).StartsWith("List[") || Bare(m).StartsWith("Tuple[")
                                || Bare(m).StartsWith("Sequence[") || Bare(m).StartsWith("Iterable[")
                                || Bare(m).StartsWith("list[") || Bare(m).StartsWith("tuple[")
                                || Bare(m) is "list" or "tuple" or "Sequence" or "Iterable"))
                return;
            Refuse();
            return;
        }

        // A name bound to a function (a plain function reference, the way `Callable[[], bool]`
        // is passed).
        bool isCallable = rawArg is VariableExpr fnVe
            && (functionParams.ContainsKey(ResolveCallee(fnVe.Name))
                || inlineFunctions.ContainsKey(ResolveCallee(fnVe.Name))
                || loopFunctionAliases.ContainsKey(fnVe.Name));
        if (isCallable)
        {
            if (members.Any(m => Bare(m).StartsWith("Callable")))
                return;
            Refuse();
            return;
        }

        // A scalar: matches whichever member is not itself a class, an array or a Callable --
        // this compiler already promotes freely between numeric widths, so the members are not
        // pinned to one exact width the way a class name is pinned to one exact class.
        bool anyScalarMember = members.Any(m =>
            !classNames.Contains(Bare(m)) && !Bare(m).StartsWith("List[")
            && !Bare(m).StartsWith("Tuple[") && !Bare(m).StartsWith("Callable"));
        if (!anyScalarMember) Refuse();
    }

    /// <summary>
    /// A Protocol in a Union is structural (#465). The argument's class matches when it
    /// has every method/property the protocol body names -- a field of the same name
    /// counts, which is how <c>DigitalInOut.value</c> satisfies <c>ROValueIO</c>.
    /// </summary>
    private bool ClassSatisfiesProtocol(string argClass, string protocolBare)
    {
        string? protoKey = FindClassKey(protocolBare);
        if (protoKey == null || !protocolClasses.Contains(protoKey)) return false;

        var needed = new HashSet<string>(
            classDirectMethods.GetValueOrDefault(protoKey) ?? []);
        needed.Remove("__init__");

        string? argKey = FindClassKey(argClass);
        if (argKey == null) return needed.Count == 0;

        return needed.All(ClassMemberNames(argKey).Contains);
    }

    /// <summary>
    /// Every member name a class is known to carry: direct methods, members
    /// assigned on `self.` anywhere in the class, flattened field slots, and
    /// property getters. The same set a Protocol match and a `hasattr` fold
    /// consult.
    /// </summary>
    private HashSet<string> ClassMemberNames(string classKey)
    {
        var has = new HashSet<string>(
            classDirectMethods.GetValueOrDefault(classKey) ?? []);
        if (assignedMemberNamesByClass.TryGetValue(classKey, out var assigned))
            has.UnionWith(assigned);
        if (classFieldLayout.TryGetValue(classKey, out var layout))
            foreach (var (field, _, _) in layout)
                has.Add(field);
        foreach (var g in propertyGetters)
        {
            int dot = g.LastIndexOf('.');
            if (dot > 0 && g.AsSpan(0, dot).SequenceEqual(classKey))
                has.Add(g[(dot + 1)..]);
        }
        return has;
    }

    /// <summary>
    /// `hasattr(obj, "name")` folds when the object's shape is statically
    /// known: an instance (or the class name itself) answers from its member
    /// set, a module answers from its exports, a function or scalar binding
    /// answers False. Returns null when nothing pins the shape, leaving the
    /// call to the runtime-reflection refusal.
    /// </summary>
    private Expression? TryFoldHasattr(Expression objExpr, string member)
    {
        if (objExpr is not VariableExpr ve) return null;

        if (TryImportedAlias(ve.Name, out var hasMod) && hasMod != null
            || modules.ContainsKey(ve.Name))
            return new BooleanLiteral(
                ExportedNames(hasMod ?? ve.Name).Contains(member)) { Line = objExpr.Line };

        // The same qualifications the type read side uses: the inline prefix,
        // then the enclosing function, then the bare name -- each followed
        // through aliases. The terminal name is kept: a Callable union member
        // binds the parameter as an alias of the caller's function, so
        // `hasattr(pred_param, "value")` asks about the FUNCTION, not the param.
        string terminal = ve.Name;
        foreach (var start in new[]
        {
            string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + ve.Name,
            string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + ve.Name,
            ve.Name,
        })
        {
            if (start == null) continue;
            var key = start;
            for (var i = 0; i < 20; ++i)
            {
                if (instanceClasses.TryGetValue(key, out var cls) && cls != null
                    && FindClassKey(cls) is { } clsKey)
                    return new BooleanLiteral(ClassMemberNames(clsKey).Contains(member))
                        { Line = objExpr.Line };
                // A Callable-union param bound through super() carries
                // loopFunctionAliases, not a variable alias.
                if (loopFunctionAliases.ContainsKey(key))
                    return new BooleanLiteral(false) { Line = objExpr.Line };
                if (variableAliases.TryGetValue(key, out var alias)) { key = alias; terminal = key; }
                else break;
            }
        }

        // A class name itself -- `hasattr(DigitalInOut, "value")` -- reads the
        // same member set.
        if (FindClassKey(terminal) is { } ownKey)
            return new BooleanLiteral(ClassMemberNames(ownKey).Contains(member))
                { Line = objExpr.Line };

        // A function binding has no instance attributes to find -- this is the
        // Callable member of a union param, answered False.
        if (FunctionNameBehind(terminal) != null
            || FunctionNameBehind(ve.Name) != null
            || loopFunctionAliases.ContainsKey(terminal))
            return new BooleanLiteral(false) { Line = objExpr.Line };
        foreach (var key in new[] { terminal, ve.Name })
        {
            if (constantVariables.ContainsKey(key) || strConstantVariables.ContainsKey(key))
                return new BooleanLiteral(false) { Line = objExpr.Line };
        }
        return null;
    }

    /// <summary>
    /// The function a name resolves to, when it is one: a def, an @inline, or a
    /// name already bound to a function (a Callable-union param forwarded
    /// through super()). The module scope's `main.` qualifier on an alias
    /// terminal strips off, the way `main.f` names f(). Null otherwise.
    /// </summary>
    private string? FunctionNameBehind(string name)
    {
        string terminal = FollowAliases(name);
        string bare = terminal.Contains('.') ? terminal[(terminal.LastIndexOf('.') + 1)..] : terminal;
        if (functionParams.ContainsKey(bare) || inlineFunctions.ContainsKey(bare)) return bare;
        if (loopFunctionAliases.TryGetValue(terminal, out var trans)) return trans;
        if (loopFunctionAliases.TryGetValue(name, out var trans2)) return trans2;
        string resolved = ResolveCallee(terminal);
        if (functionParams.ContainsKey(resolved) || inlineFunctions.ContainsKey(resolved)) return resolved;
        return null;
    }

    /// <summary>
    /// The flattened key a callable bound on an instance field is filed under:
    /// the receiver's resolved name + "_" + member, matching the field-write
    /// path. Returns null when the receiver is not a name.
    /// </summary>
    private string? FlattenedCallableFieldKey(MemberAccessExpr mem)
        => mem.Object is VariableExpr rve ? ResolveNameKey(rve.Name) + "_" + mem.Member : null;

    /// <summary>
    /// The terminal key a name's instance is filed under, probing the scopes
    /// <see cref="InstanceClassOfName"/> does (inline prefix, enclosing
    /// function, bare) and following the alias chain to the end. Null when no
    /// binding carries a class -- the same fact InstanceClassOfName reports,
    /// but returning the KEY <see cref="EmitUnboundMethodBody"/> needs to alias
    /// `self` onto.
    /// </summary>
    private string? InstanceKeyOfName(string name)
    {
        foreach (var key in new[]
        {
            string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + name,
            string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + name,
            name,
        })
        {
            if (key == null) continue;
            string? chased = key;
            for (int hop = 0; hop < 20 && chased != null && !instanceClasses.ContainsKey(chased); ++hop)
                chased = variableAliases.TryGetValue(chased, out var next) ? next : null;
            if (chased != null && instanceClasses.ContainsKey(chased)) return chased;
        }
        return null;
    }

    /// <summary>
    /// `obj.method` used as a VALUE -- the right-hand side of
    /// `self.f = obj.method`. Resolves the receiver to its terminal instance
    /// key and the member to a method on the receiver's class. Returns null
    /// for a member that is data, so the assignment keeps its ordinary
    /// meaning.
    /// </summary>
    private BoundMethodField? BoundMethodBehind(MemberAccessExpr rhs)
    {
        string? recvKey = rhs.Object switch
        {
            VariableExpr ve => InstanceKeyOfName(ve.Name),
            MemberAccessExpr nested => FlattenedCallableFieldKey(nested),
            _ => null,
        };
        if (recvKey == null) return null;
        for (int hop = 0; hop < 20; ++hop)
        {
            if (instanceClasses.TryGetValue(recvKey, out var cls) && cls != null)
            {
                // `obj.prop` where prop is a @property is a READ that invokes the
                // getter -- not a bound method to store. `pwm.frequency` on the
                // right of a member store (`GPIOR0.value = o.frequency`) must keep
                // its ordinary meaning. The lookup is keyed by the DEFINING
                // class, so ask through the MRO the way the setter path does.
                if (ResolveMROPropertyClass(cls, rhs.Member) != null)
                    return null;
                string fn = cls + "_" + rhs.Member;
                bool has = inlineFunctions.ContainsKey(fn) || instanceMethodDefs.ContainsKey(fn)
                       || methodAstByName.ContainsKey(fn);
                return has
                    ? new BoundMethodField { Recv = recvKey, Member = rhs.Member, Fn = fn }
                    : null;
            }
            if (!variableAliases.TryGetValue(recvKey, out var next) || next == null) return null;
            recvKey = next;
        }
        return null;
    }

    /// <summary>
    /// Snapshot the resolution of every free name a lambda's body reads, so an
    /// expansion somewhere else can re-seed them (see lambdaCaptures). A name
    /// that resolves to nothing here needs no capture -- it will resolve in the
    /// call's own scope or fail there honestly.
    /// </summary>
    private void RecordLambdaCaptures(string lambdaKey, LambdaExpr lam)
    {
        var names = new HashSet<string>();
        CollectVariableNames(lam.Body, names);
        foreach (var p in lam.Params) names.Remove(p.Name);
        if (names.Count == 0) return;

        var caps = new Dictionary<string, CapturedName>();
        foreach (var n in names)
        {
            foreach (var start in new[]
            {
                string.IsNullOrEmpty(currentInlinePrefix) ? null : currentInlinePrefix + n,
                string.IsNullOrEmpty(currentFunction) ? null : currentFunction + "." + n,
                n,
            })
            {
                if (start == null) continue;
                string key = start;
                for (var i = 0; i < 20 && variableAliases.TryGetValue(key, out var a); ++i) key = a;
                var cap = new CapturedName { Alias = key };
                bool known = false;
                if (instanceClasses.TryGetValue(key, out var capCls) && capCls != null)
                { cap.Cls = capCls; known = true; }
                if (constantVariables.TryGetValue(key, out var capC))
                { cap.Const = capC; cap.HasConst = true; known = true; }
                if (strConstantVariables.TryGetValue(key, out var capS))
                { cap.Str = capS; known = true; }
                if (known || key != n)
                {
                    caps[n] = cap;
                    break;
                }
            }
        }
        if (caps.Count > 0) lambdaCaptures[lambdaKey] = caps;
    }

    private static void CollectVariableNames(Expression e, HashSet<string> into)
    {
        switch (e)
        {
            case VariableExpr v: into.Add(v.Name); break;
            case MemberAccessExpr m: CollectVariableNames(m.Object, into); break;
            case CallExpr c:
                CollectVariableNames(c.Callee, into);
                foreach (var a in c.Args) CollectVariableNames(a, into);
                break;
            case BinaryExpr b: CollectVariableNames(b.Left, into); CollectVariableNames(b.Right, into); break;
            case UnaryExpr u: CollectVariableNames(u.Operand, into); break;
            case AwaitExpr aw: CollectVariableNames(aw.Operand, into); break;
            case IndexExpr ix: CollectVariableNames(ix.Target, into); CollectVariableNames(ix.Index, into); break;
            case SliceExpr sl:
                if (sl.Start != null) CollectVariableNames(sl.Start, into);
                if (sl.Stop != null) CollectVariableNames(sl.Stop, into);
                if (sl.Step != null) CollectVariableNames(sl.Step, into);
                break;
            case TernaryExpr t:
                CollectVariableNames(t.TrueVal, into);
                CollectVariableNames(t.Condition, into);
                CollectVariableNames(t.FalseVal, into);
                break;
            case ListExpr l: foreach (var x in l.Elements) CollectVariableNames(x, into); break;
            case TupleExpr tp: foreach (var x in tp.Elements) CollectVariableNames(x, into); break;
            case SetExpr s: foreach (var x in s.Elements) CollectVariableNames(x, into); break;
            case DictExpr d:
                foreach (var (dk, dv) in d.Entries) { CollectVariableNames(dk, into); CollectVariableNames(dv, into); }
                break;
            case FStringExpr f:
                foreach (var part in f.Parts) if (part.Expr != null) CollectVariableNames(part.Expr, into);
                break;
            case KeywordArgExpr kw: CollectVariableNames(kw.Value, into); break;
            case StarArgExpr st: CollectVariableNames(st.Value, into); break;
            case DoubleStarArgExpr ds: CollectVariableNames(ds.Value, into); break;
            case WalrusExpr w: CollectVariableNames(w.Value, into); break;
            case ListCompExpr lc:
                CollectVariableNames(lc.Iterable, into);
                if (lc.Iterable2 != null) CollectVariableNames(lc.Iterable2, into);
                if (lc.Filter != null) CollectVariableNames(lc.Filter, into);
                CollectVariableNames(lc.Element, into);
                into.Remove(lc.VarName);
                if (!string.IsNullOrEmpty(lc.Var2Name)) into.Remove(lc.Var2Name);
                break;
            case GeneratorExpr ge:
                CollectVariableNames(ge.Iterable, into);
                CollectVariableNames(ge.Element, into);
                into.Remove(ge.VarName);
                break;
            case LambdaExpr inner:
                CollectVariableNames(inner.Body, into);
                foreach (var ip in inner.Params) into.Remove(ip.Name);
                break;
        }
    }

    private string? FindClassKey(string name)
    {
        if (classDirectMethods.ContainsKey(name) || protocolClasses.Contains(name)
            || classFieldLayout.ContainsKey(name))
            return name;
        foreach (var k in classDirectMethods.Keys)
            if (k.EndsWith("_" + name, StringComparison.Ordinal)) return k;
        foreach (var k in protocolClasses)
            if (k.EndsWith("_" + name, StringComparison.Ordinal) || k == name) return k;
        return null;
    }

    private string ResolveListVarQualified(string name)
    {
        string? qualified = null;
        if (!string.IsNullOrEmpty(currentInlinePrefix))
        {
            string k = currentInlinePrefix + name;
            if (listVarElemTypes.ContainsKey(k)) return k;
            qualified = k;
            // A parameter alias (inline1.pulses -> evens) is the call-site
            // argument binding: it outranks a same-named list in the CALLER's
            // scope, which the currentFunction fallback below would otherwise
            // resolve to first (bin_data(pulses) iterating main.pulses).
            for (int depth = 0; depth < 20; depth++)
            {
                if (!variableAliases.TryGetValue(qualified, out string next)) break;
                qualified = next;
                if (listVarElemTypes.ContainsKey(qualified)) return qualified;
            }
        }
        if (!string.IsNullOrEmpty(currentFunction))
        {
            string k = currentFunction + "." + name;
            if (listVarElemTypes.ContainsKey(k)) return k;
            qualified ??= k;
        }
        if (listVarElemTypes.ContainsKey(name)) return name;

        // Follow variableAliases to resolve through a force-inlined callee's list[T]
        // parameter (e.g. len(buf) inside a plain-Python `array.array`-annotated method,
        // expanded at the call site the same way a bytearray/ZCA parameter already is).
        string resolved = qualified ?? name;
        for (int depth = 0; depth < 20; depth++)
        {
            if (!variableAliases.TryGetValue(resolved, out string next)) break;
            resolved = next;
            if (listVarElemTypes.ContainsKey(resolved)) return resolved;
        }
        return "";
    }

    // Computes basePtr + 2 + index * elemSize as a UINT16 address Temporary. A negative
    // constant index counts from the list's run-time length (header byte 0): `xs[-1]` used
    // as it was addressed the header and printed its bytes as an element.
    private Temporary EmitElemAddr(Val basePtr, Val index, int elemSize)
    {
        if (index is Constant { Value: < 0 } negIdx)
        {
            Temporary count = EmitListLoad(basePtr, 0, DataType.UINT8);
            Temporary fromEnd = MakeTemp(DataType.UINT16);
            Emit(new Binary(BinaryOp.Sub, count, new Constant(-negIdx.Value), fromEnd));
            index = fromEnd;
        }

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

    /// <summary>
    /// `buf.extend(bytes(n))` on a fixed-size buffer, with `n` known at compile time (PyMCU#362).
    ///
    /// A buffer that starts at one byte and is grown to its final size by the constructors that
    /// use it is the standard CircuitPython shape, and nothing about it is dynamic: every caller
    /// asks for a size that is a literal by the time it arrives here, so the set of sizes the
    /// buffer is ever asked for is known while compiling. The buffer takes the LARGEST of them,
    /// which is why a later, smaller `extend` adds nothing rather than shrinking anything --
    /// the `claim()` intrinsic's registry with `max` where claim has `equal`.
    ///
    /// The bytes added are zeroed here rather than at the declaration, because the declaration
    /// does not yet know the final size. Without that a grown byte reads whatever the SRAM held.
    ///
    /// A size only known at run time is refused, naming the buffer: there is no allocator to
    /// grow one with, and taking the declared size in silence is how a driver writes past its
    /// own buffer.
    /// </summary>
    private Val EmitBufferExtend(string bufKey, CallExpr expr, MemberAccessExpr memC)
    {
        if (expr.Args.Count != 1)
            throw UserError($"{bufKey}.extend() takes exactly one argument", memC);

        // The size bump happens once, when the statement lowers, so it is only correct when
        // every execution of the extend is preceded by the buffer's declaration re-running --
        // the same run-time branch context the declaration recorded. extend() inside a loop
        // or a non-folding conditional would grow the buffer a different number of times than
        // the declaration runs (`buf += src` refuses the same shape for the same reason).
        var extDeclTokens = bufferDeclBranchTokens.TryGetValue(bufKey, out var extDeclT)
            ? extDeclT : new List<int>();
        if (!extDeclTokens.SequenceEqual(_runtimeBranchTokens))
            throw UserError(
                $"{memC.Object}.extend() runs in a different context than where "
                + $"'{memC.Object}' was declared -- inside a loop or conditional it would "
                + "grow the buffer a different number of times than the declaration runs, "
                + "but a buffer's size is fixed while compiling. Build it at its final size "
                + "and slice-assign into it, or keep a write index and store at it.",
                expr.Args[0]);

        // The count runs first: a size known only at run time is refused here, naming
        // the BUFFER, before any element work can trip its own less helpful message
        // (`bytes(n)` with a run-time `n` is this case). For a spelled source the
        // count is just the element count -- no value is evaluated for it. The
        // count form's own argument DOES evaluate here (`bytes(n)` lowers n), which
        // is still ahead of the receiver's mutation below.
        int added = BufferExtendCount(expr.Args[0], bufKey);

        // CPython evaluates the whole argument before the receiver mutates: pin every
        // spelled element to the value it holds while len(buf) still answers the size
        // before the bump -- `xs.extend([len(xs)])` reads 1, not the grown 2. The
        // per-element refusals run in the same pass, ahead of that element's eval.
        //
        // When the argument spells its elements out -- a literal, a string, or a name
        // bound to a compile-time sequence -- the bytes it adds are those elements.
        // Each one lands through the canonical element store (an indexed AssignStmt),
        // the same lowering `buf += src` runs per element, so every receiver
        // representation -- fixed buffer, slot-flattened sequence, growable list --
        // writes the storage its own reads answer. It used to only COUNT the elements
        // and zero-fill, and an ArrayStore on the buffer key wrote storage a slot
        // list's `xs[k]` never reads: `xs.extend([1, 2, 3])` printed 0 0 0 either way.
        //
        // `bytes(n)` is NOT a spelled source: its elements are n zeros, which is what
        // the count-form zero-fill below already emits -- through the counted loop
        // past the threshold (PyMCU#411) instead of one store per slot.
        List<Expression>? extPinned = null;
        if (BufferExtendElements(expr.Args[0]) is { } extElems)
        {
            extPinned = new List<Expression>(extElems.Count);
            foreach (Expression extEl in extElems)
            {
                if (extEl is ListExpr or TupleExpr)
                    throw UserError(
                        $"{bufKey}.extend() takes a flat sequence of bytes -- an "
                        + "element that is itself a list or tuple has no byte to store.",
                        extEl);
                if (InstanceClassOfValueExpr(extEl) is { } extInstCls)
                    throw UserError(
                        $"'.extend()' cannot take an instance of "
                        + $"'{ShortClassNameOf(extInstCls)}': " + InstanceIsFlattened
                        + ", so the grown byte would read shared storage at every "
                        + "index. " + InstanceElementAdvice(extInstCls), extEl);
                extPinned.Add(PinOnce(extEl));
            }
        }

        // The tail goes at the LOGICAL end: sibling inline expansions share the
        // storage key, and another expansion's extend/+= must not move this one's.
        // The length is read only NOW, after the argument has fully evaluated:
        // evaluating it can itself extend this very buffer (`xs.extend([grow()])`
        // where grow calls xs.extend), and a `current` captured earlier would
        // place the new bytes on top of the ones the argument's effects appended.
        int current = LogicalArrayLen(bufKey, arraySizes[bufKey]);
        int grown = Math.Max(current, current + added);
        if (grown <= current) return new NoneVal();

        arraySizes[bufKey] = Math.Max(arraySizes[bufKey], grown);
        bufferLogicalLen[bufKey] = grown;

        if (extPinned is { })
        {
            for (int k = 0; k < extPinned.Count; ++k)
                VisitStatement(new AssignStmt(
                    new IndexExpr(memC.Object, new IntegerLiteral(current + k)), extPinned[k]));
            return new NoneVal();
        }

        {
            var elem = arrayElemTypes.TryGetValue(bufKey, out var et) ? et : DataType.UINT8;
            // Below the threshold, an unrolled store per slot is smaller: the loop's own code
            // (a compare, a branch, an index increment, a jump back) is a fixed cost that N
            // small unrolled stores can undercut, and unrolling keeps every existing small
            // extend() byte-identical (PyMCU#411). Above it, N ArrayStores grow with N while the
            // loop's fixed cost does not -- 69 of them, one .extend() from 1 to 70 uint16
            // entries, cost 432 bytes on an otherwise-754-byte program. Measured on AVR with a
            // uint16 element (PulseCapture's ring): unrolled and looped cross between 13 and 14
            // added slots (522 B vs. 524 B fixed); BufferExtendLoopThreshold is 13 so the
            // crossover itself still unrolls, and a bytearray's one-byte element crosses at
            // roughly double that, so 13 stays on the unrolled side there too.
            if (grown - current > BufferExtendLoopThreshold)
            {
                Temporary idx = MakeTemp(DataType.UINT16);
                string loop = MakeLabel(), done = MakeLabel();
                Emit(new Copy(new Constant(current), idx));
                Emit(new Label(loop));
                Emit(new JumpIfGreaterOrEqual(idx, new Constant(grown), done));
                Emit(new ArrayStore(bufKey, idx, new Constant(0), elem, grown));
                Emit(new AugAssign(BinaryOp.Add, idx, new Constant(1)));
                Emit(new Jump(loop));
                Emit(new Label(done));
            }
            else if (!listVarElemTypes.ContainsKey(bufKey)
                     && !literalSequenceArrays.Contains(bufKey)
                     && !constSequenceBindings.ContainsKey(bufKey))
            {
                for (int i = current; i < grown; i++)
                    Emit(new ArrayStore(bufKey, new Constant(i), new Constant(0), elem, grown));
            }
            else
            {
                // A receiver whose elements are not addressed as buf[i] -- a growable
                // list or a slot-flattened sequence -- stores each zero through the
                // canonical element store, the same as the spelled-elements path.
                for (int i = current; i < grown; i++)
                    VisitStatement(new AssignStmt(
                        new IndexExpr(memC.Object, new IntegerLiteral(i)),
                        new IntegerLiteral(0)));
            }
        }
        return new NoneVal();
    }

    /// <summary>
    /// The unrolled zero-store form and the counted loop form cross over here: below this many
    /// new slots the unrolled form is smaller, above it the loop is. Measured on AVR with the
    /// two-word (uint16) element this HAL's pulse ring uses -- 522 bytes unrolled against 524
    /// looped at 13 added slots, 526 against 524 at 14; see PyMCU#411.
    /// </summary>
    private const int BufferExtendLoopThreshold = 13;

    /// <summary>
    /// The arraySizes key a source-level buffer name stands for here, tried in the same order
    /// len() tries it: under the inline expansion's prefix, under the current function, bare,
    /// and then through the alias chain an @inline parameter binding leaves behind.
    /// </summary>
    private string? ResolveBufferKey(Expression objExpr)
    {
        if (objExpr is not VariableExpr ve) return null;

        if (!string.IsNullOrEmpty(currentInlinePrefix)
            && arraySizes.ContainsKey(currentInlinePrefix + ve.Name))
            return currentInlinePrefix + ve.Name;
        if (!string.IsNullOrEmpty(currentFunction)
            && arraySizes.ContainsKey(currentFunction + "." + ve.Name))
            return currentFunction + "." + ve.Name;
        if (arraySizes.ContainsKey(ve.Name)) return ve.Name;

        string key = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + ve.Name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + ve.Name : ve.Name);
        for (int depth = 0; depth < 20; depth++)
        {
            if (!variableAliases.TryGetValue(key, out string next)) break;
            key = next;
            if (arraySizes.ContainsKey(key)) return key;
        }
        return null;
    }

    /// <summary>
    /// How many bytes an `extend()` argument adds, refusing anything the compiler cannot count.
    /// </summary>
    private int BufferExtendCount(Expression arg, string bufName)
    {
        // `bytes([a, b, c])` and a bare list add one byte per element.
        Expression counted = arg is CallExpr { Callee: VariableExpr { Name: "bytes" } } bc
                             && bc.Args.Count == 1
            ? bc.Args[0]
            : arg;
        if (counted is ListExpr le) return le.Elements.Count;
        if (counted is StringLiteral sl) return sl.Value.Length;
        if (ResolveConstSequenceExpr(counted) is { } seq) return seq.Count;

        // `bytes(n)` adds n zero bytes. The count is lowered rather than pattern-matched so a
        // size that only becomes a literal through an inlined call site still folds.
        Val v = VisitExpression(counted);
        if (v is Constant c) return c.Value;

        throw UserError(
            $"{bufName}.extend(): how many bytes this adds decides the size of '{bufName}', and a "
            + "buffer's size is fixed while compiling -- there is no allocator to grow one at run "
            + "time. This count is only known at run time. Declare the buffer at its final size "
            + $"(`{bufName} = bytearray(N)`), or extend it by an amount that is a literal or a const.",
            arg);
    }

    /// <summary>
    /// The elements an `extend()` argument spells out, or null when it only has a count
    /// (`bytes(n)`, a name the compiler cannot see into). For those arguments the
    /// caller falls back to the count-and-zero-fill path.
    /// </summary>
    private List<Expression>? BufferExtendElements(Expression arg)
    {
        Expression counted = arg is CallExpr { Callee: VariableExpr { Name: "bytes" } } ebc
                             && ebc.Args.Count == 1
            ? ebc.Args[0]
            : arg;
        if (counted is ListExpr extLit) return extLit.Elements;
        // `buf.extend("ab")` appends the bytes the string spells: each char's code.
        if (counted is StringLiteral extStr)
            return extStr.Value
                .Select(ch => (Expression)new IntegerLiteral(ch)).ToList();
        if (ResolveConstSequenceExpr(counted) is { } extSeq) return extSeq;
        return null;
    }

    // Emits IR for list.append(val). Handles fast path (len < cap) and slow path (realloc).
    private Val EmitListAppend(Variable listVar, Expression valExpr)
    {
        // A class instance has no run-time storage of its own here: a PyMCU instance is a
        // NAME whose fields are flattened to compile-time storage (`self.x` lives at
        // `<name>_x`), never a value with an address a byte-array append can copy. Before
        // this check, `xs.append(Counter(i))` (a fresh instance) and `xs.append(c)` (an
        // existing named one) both fell through to the generic value path below, which read
        // the instance's own bare handle -- a name NOTHING ever writes, since only its
        // flattened fields are assigned. Every element after the first then read back as
        // uninitialized storage (0), indistinguishable from a real value: `xs[1]` silently
        // answered `xs[0]`'s field. Refused here, at the append, rather than producing that.
        //
        // InstanceClassOfValueExpr spells the instance every way it arrives: a ctor call,
        // a named instance or instance field, an element read off an instance array, and
        // a call whose declared return is a class.
        if (InstanceClassOfValueExpr(valExpr) is { } appendInstCls)
            throw UserError(
                $"'.append()' cannot take an instance of '{ShortClassNameOf(appendInstCls)}': "
                + "PyMCU instances are flattened to compile-time storage, so a growable list "
                + "has nothing to copy into its buffer and would silently repeat one element "
                + "at every index. " + InstanceElementAdvice(appendInstCls), valExpr);

        DataType elemDt = listVarElemTypes[listVar.Name];

        // A promoted `x = []` learns its element type here, from the first
        // append: the binding site emitted a header-only object with the type
        // left pending. The answer must come from the expression's type alone
        // -- the value itself is visited late (after the grow alloc) so a
        // list-literal argument's own GcAlloc cannot be moved out from under
        // it by the grow's collection.
        if (elemDt == DataType.UNKNOWN)
        {
            elemDt = InferListElemType(valExpr);
            if (elemDt is DataType.UNKNOWN or DataType.VOID)
                throw UserError(
                    $"cannot infer the element type of '{listVar.Name}' from this append; " +
                    "declare it like `x: list[uint8] = []`", valExpr);
            listVarElemTypes[listVar.Name] = elemDt;

            // A GC_REF element makes the payload an array of inner-list
            // pointers: record the appended list's own element type so
            // `x[i][j]` and `for inner in x` resolve it, exactly as a
            // declared list[list[T]] does. A literal argument's inner type is
            // recorded after it materializes below.
            if (elemDt == DataType.GC_REF && valExpr is VariableExpr valVar
                && listVarElemTypes.TryGetValue(ResolveNameKey(valVar.Name), out var argElem)
                && argElem != DataType.UNKNOWN)
                listInnerElemTypes[listVar.Name] = argElem;
        }

        // The element type of a list bound to a literal was read off the literal's elements;
        // nothing declared it. A value those elements cannot hold would be stored in their
        // width -- `v = [1, 2]` then `v.append(300)` kept 44 -- so it is refused here, where
        // declaring the list is the fix.
        if (inferredLiteralLists.Contains(listVar.Name))
        {
            DataType valDt = InferListElemType(valExpr);
            if (valDt != DataType.UNKNOWN && valDt != DataType.VOID && valDt != elemDt
                && DataTypeExtensions.GetPromotedType(elemDt, valDt) != elemDt)
                throw UserError(
                    $"this value does not fit the elements of '{listVar.Name}': its element type " +
                    $"was inferred as {elemDt.ToString().ToLowerInvariant()} from the literal " +
                    "it was bound to. Declare the list with the wider type, like " +
                    $"`x: list[{valDt.ToString().ToLowerInvariant()}] = [...]`", valExpr);
        }

        // The promoted object was allocated with the ref-bearing flag clear
        // (the element type was unknowable then); an append of a GC_REF makes
        // the payload an array of pointers, so set bit6 of the mark byte the
        // way gc_alloc_refs does at creation. Emitted at every GC_REF append
        // site: the flag is idempotent and each site may run first at runtime.
        if (elemDt == DataType.GC_REF && promotedEmptyLists.Contains(listVar.Name))
            EmitRefPayloadFlag(listVar);

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

        // === SLOW PATH: realloc to grow capacity ===

        // gc_alloc encodes the payload length in one header byte, so a list buffer's
        // 2 + cap*elemSize bytes must stay <= 255. Grow by 2x in u16 -- cap*2 cannot
        // wrap -- then clamp to the largest capacity that fits, and raise
        // MemoryError when the list is already at that ceiling.
        int maxCap = (255 - 2) / elemSize;
        Temporary newCapWide = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.LShift, tmpCap, new Constant(1), newCapWide));
        Temporary tooBig = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.GreaterThan, newCapWide, new Constant(maxCap), tooBig));
        string capFitsLabel = MakeLabel();
        Emit(new JumpIfZero(tooBig, capFitsLabel));
        Emit(new Copy(new Constant(maxCap), newCapWide));
        Emit(new Label(capFitsLabel));

        // A promoted `x = []` starts at capacity 0, which stays 0 under any
        // growth factor: floor at the same first-fit a declaration grants before
        // the grew-check would call it finished. Never floor past the ceiling.
        // Only a promoted list can hold cap 0 -- declared lists mint at 8 -- so the
        // floor is emitted only where the promotion feature engaged.
        if (promotedEmptyLists.Contains(listVar.Name))
        {
            int floorCap = Math.Min(8, maxCap);
            string capFlooredLabel = MakeLabel();
            Emit(new JumpIfGreaterOrEqual(newCapWide, new Constant(floorCap), capFlooredLabel));
            Emit(new Copy(new Constant(floorCap), newCapWide));
            Emit(new Label(capFlooredLabel));
        }

        Temporary grew = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.GreaterThan, newCapWide, tmpCap, grew));
        string growableLabel = MakeLabel();
        Emit(new JumpIfNotZero(grew, growableLabel));
        EmitRuntimeRaise("MemoryError", "list capacity limit reached");
        Emit(new Label(growableLabel));

        Temporary newCap = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.BitAnd, newCapWide, new Constant(0xFF), newCap));

        // new_alloc_size = 2 + new_cap * elemSize  (<= 255 by the clamp above)
        Temporary newCapScaled = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, newCap, new Constant(elemSize), newCapScaled));
        Temporary newAllocSize = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, newCapScaled, new Constant(2), newAllocSize));

        // Allocate the new buffer. CAUTION: GcAlloc may trigger a collection that compacts the
        // heap and RELOCATES the existing list, updating listVar (a tracked GC root) to its new
        // address. A pointer to the old buffer captured BEFORE this alloc would dangle, so the
        // copy source is re-derived from listVar AFTER the alloc.
        Temporary newPtr = MakeTemp(DataType.GC_REF);
        Emit(new GcAlloc(newAllocSize, newPtr, elemDt == DataType.GC_REF));

        // gc_alloc returns 0 on OOM; the stores and copy loop below write through the
        // pointer unchecked, so a null result would land a list header and the element
        // bytes at address 0x0000 -- which is IO space, SPH included.
        Val newPtrU16Chk = newPtr with { Type = DataType.UINT16 };
        Temporary allocOk = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.NotEqual, newPtrU16Chk, new Constant(0), allocOk));
        string allocOkLabel = MakeLabel();
        Emit(new JumpIfNotZero(allocOk, allocOkLabel));
        EmitRuntimeRaise("MemoryError", "list append out of memory");
        Emit(new Label(allocOkLabel));

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

        // `bins.append([pulse, 1])`: a literal argument has no value position
        // of its own -- materialize it into a heap object the payload can point
        // at. This runs after the grow: the literal's own gc_alloc may collect,
        // and listVar (a named root) is where the new address lands.
        Val elemVal;
        if (valExpr is ListExpr appendLit)
        {
            Variable litVar = MaterializeSequenceLiteral(appendLit.Elements,
                listInnerElemTypes.TryGetValue(listVar.Name, out var litDecl)
                    && litDecl != DataType.UNKNOWN ? litDecl : null, valExpr);
            listInnerElemTypes[listVar.Name] = listVarElemTypes[litVar.Name];
            elemVal = litVar;
        }
        else if (valExpr is TupleExpr appendTup)
        {
            Variable litVar = MaterializeSequenceLiteral(appendTup.Elements,
                listInnerElemTypes.TryGetValue(listVar.Name, out var litDecl)
                    && litDecl != DataType.UNKNOWN ? litDecl : null, valExpr);
            listInnerElemTypes[listVar.Name] = listVarElemTypes[litVar.Name];
            elemVal = litVar;
        }
        else
        {
            elemVal = VisitExpression(valExpr);
            // The AST check at the top cannot see every spelling -- a member-callee
            // factory (`obj.make()`) evaluates to an instance handle that the same
            // flattened-storage hole swallows. Not on a MemberAccessExpr, though:
            // `m.value` on a single-field class evaluates to the instance's own
            // storage, which is the FIELD's byte -- a legal append, not an instance.
            if (valExpr is not MemberAccessExpr
                && InstanceClassOfVal(elemVal) is { } lateInstCls)
                throw UserError(
                    $"'.append()' cannot take an instance of '{ShortClassNameOf(lateInstCls)}': "
                    + "PyMCU instances are flattened to compile-time storage, so a growable "
                    + "list has nothing to copy into its buffer and would silently repeat "
                    + "one element at every index. " + InstanceElementAdvice(lateInstCls),
                    valExpr);
        }
        Temporary appendAddr = EmitElemAddr(listVar, tmpLen, elemSize);
        Emit(new StoreIndirect(elemVal, appendAddr, elemDt));

        // length += 1
        Temporary newLen = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.Add, tmpLen, new Constant(1), newLen));
        Emit(new StoreIndirect(newLen, listVar));

        return new NoneVal();
    }

    /// <summary>
    /// `list(x)` / `tuple(x)` on a runtime heap list: a fresh GC object holding
    /// the same elements. The copy is an exact fit -- capacity equals count --
    /// because copying the source's capacity byte verbatim would claim headroom
    /// the new allocation never received, and a later append would write past
    /// the object's end. The result keeps the source's element type (and inner
    /// element type for a list of lists) so reads, len() and append on it
    /// resolve exactly as they did on the source.
    /// </summary>
    private Val EmitListCopyCtor(string srcKey, VariableExpr srcExpr, CallExpr expr)
    {
        DataType elemDt = listVarElemTypes[srcKey];
        if (elemDt == DataType.UNKNOWN)
            throw UserError(
                $"cannot infer the element type of '{srcExpr.Name}' yet; append an element " +
                "first or declare it like `x: list[uint8] = []`", expr);
        int elemSize = elemDt.SizeOf();
        Variable srcVar = new Variable(srcKey, DataType.GC_REF);

        // len = src[0]. Read it before the alloc: the value itself is immune to
        // compaction, while the base pointer is re-derived from the (possibly
        // relocated) variable afterwards.
        Temporary len = MakeTemp(DataType.UINT8);
        Emit(new LoadIndirect(srcVar, len));
        Temporary totalBytes = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Mul, len, new Constant(elemSize), totalBytes));
        Temporary allocSize = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, totalBytes, new Constant(2), allocSize));

        Temporary dst = MakeTemp(DataType.GC_REF);
        Emit(new GcAlloc(allocSize, dst, elemDt == DataType.GC_REF));

        // A null return is real heap exhaustion; a header store through it is
        // SRAM[0] (the register file) -- refuse loudly instead.
        string okLabel = MakeLabel();
        Emit(new JumpIfNotZero(dst with { Type = DataType.UINT16 }, okLabel));
        EnterRuntimeBranch($"copying '{srcExpr.Name}'");
        try
        {
            VisitRaise(new RaiseStmt("MemoryError",
                "list copy ran out of heap on this target"));
        }
        finally { LeaveRuntimeBranch(); }
        Emit(new Label(okLabel));

        EmitListStore(dst, 0, len);
        EmitListStore(dst, 1, len);

        Val srcU16 = srcVar with { Type = DataType.UINT16 };
        Val dstU16 = dst with { Type = DataType.UINT16 };
        Temporary srcBase = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, srcU16, new Constant(2), srcBase));
        Temporary dstBase = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, dstU16, new Constant(2), dstBase));

        Temporary byteOff = MakeTemp(DataType.UINT16);
        Emit(new Copy(new Constant(0), byteOff));
        string copyLoopLabel = MakeLabel();
        string copyLoopEnd = MakeLabel();
        Emit(new Label(copyLoopLabel));
        Temporary cmpDone = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.GreaterEqual, byteOff, totalBytes, cmpDone));
        Emit(new JumpIfNotZero(cmpDone, copyLoopEnd));
        Temporary srcAddr = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, srcBase, byteOff, srcAddr));
        Temporary dstAddr = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Add, dstBase, byteOff, dstAddr));
        Temporary byteTmp = MakeTemp(DataType.UINT8);
        Emit(new LoadIndirect(srcAddr, byteTmp));
        Emit(new StoreIndirect(byteTmp, dstAddr));
        Emit(new AugAssign(BinaryOp.Add, byteOff, new Constant(1)));
        Emit(new Jump(copyLoopLabel));
        Emit(new Label(copyLoopEnd));

        listVarElemTypes[dst.Name] = elemDt;
        if (listInnerElemTypes.TryGetValue(srcKey, out var innerElem))
            listInnerElemTypes[dst.Name] = innerElem;
        // `tuple(x)` shares this lowering with `list(x)`: the fresh object is a
        // heap sequence either way, but only the tuple() call binds the
        // immutable bracket the repr and indexed stores look up.
        if (expr.Callee is VariableExpr { Name: "tuple" })
            tupleBoundNames.Add(dst.Name);

        return dst;
    }

    /// <summary>
    /// The element type an append argument would give a pending list. A list
    /// or tuple value makes a list of references (GC_REF); everything else
    /// answers the expression's own width.
    /// </summary>
    private DataType InferListElemType(Expression e) => e switch
    {
        ListExpr or TupleExpr => DataType.GC_REF,
        _ => InferExprType(e),
    };

    /// <summary>
    /// Set the ref-bearing bit (bit6) of a GC object's mark byte, which sits
    /// two bytes under the user pointer -- the same flag gc_alloc_refs writes
    /// at creation. Used when a promoted `x = []`, allocated while its element
    /// type was still unknown, first holds a GC_REF element.
    /// </summary>
    private void EmitRefPayloadFlag(Variable listVar)
    {
        usesRefPayloads = true;
        Temporary hdrAddr = MakeTemp(DataType.UINT16);
        Emit(new Binary(BinaryOp.Sub, listVar with { Type = DataType.UINT16 },
            new Constant(2), hdrAddr));
        Temporary hdrByte = MakeTemp(DataType.UINT8);
        Emit(new LoadIndirect(hdrAddr, hdrByte));
        Temporary hdrSet = MakeTemp(DataType.UINT8);
        Emit(new Binary(BinaryOp.BitOr, hdrByte, new Constant(0x40), hdrSet));
        Emit(new StoreIndirect(hdrSet, hdrAddr));
    }

    /// <summary>
    /// Fold <c>isinstance(x, tuple/list/int/...)</c> when the receiver's shape is already
    /// known (#423, adafruit_ht16k33). Candidates that are not these builtins return null
    /// so the refusal still names a run-time type test that has no answer.
    /// </summary>
    private Constant? TryFoldIsinstanceBuiltinTypes(VariableExpr recv, Expression typesExpr)
    {
        var cands = typesExpr is TupleExpr t ? t.Elements : new List<Expression> { typesExpr };
        if (cands.Count == 0) return null;
        var names = new List<string>();
        foreach (var c in cands)
        {
            if (c is not VariableExpr ve) return null;
            // `slice` matches nothing here: PyMCU has no slice VALUE at all (a[b:c]
            // reaches __setitem__ as a SliceExpr index, never as a bound object), so
            // `isinstance(x, slice)` on any name the shapes below resolve is False --
            // pixelbuf's `if isinstance(index, slice)` is exactly that test.
            if (ve.Name is not ("tuple" or "list" or "int" or "bool"
                or "bytes" or "bytearray" or "str" or "float" or "slice"))
                return null;
            names.Add(ve.Name);
        }

        string q = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + recv.Name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + recv.Name : recv.Name);

        // The name's VALUE is not necessarily filed under the name itself. An inlined
        // parameter binds through variableAliases to the caller's binding -- pixelbuf's
        // `isinstance(index, slice)` asks about the loop variable `main.i`, which only
        // the walk to the alias's terminal reaches. And a module-level string's text is
        // filed under the qualified `module.name` key while the bare name holds the
        // string's storage id, so a bare probe is refused by BindsNonString before the
        // module-global fallback can answer. Every flag probes the qualified key, the
        // alias-terminal keys, and the bare/module spellings.
        string rk = FollowAliases(q);
        string rb = FollowAliases(recv.Name);
        var keys = new List<string> { q };
        foreach (var k in new[] { rk, rb, recv.Name, currentModulePrefix + recv.Name })
            if (!keys.Contains(k)) keys.Add(k);

        bool isSeq = ResolveArrayVar(recv.Name) != null
            || ResolveConstSequence(recv.Name) != null
            || ResolveListLiteralParam(recv.Name) != null
            || keys.Any(k => bytearrayParams.Contains(k));
        bool isClass = InstanceClassOfName(recv.Name) != null;
        bool isStr = keys.Any(k => ResolveStrConstant(k) != null)
            || keys.Any(k => runtimeStrVars.ContainsKey(k))
            || keys.Any(k => multiStrVariables.ContainsKey(k));
        bool isFloat = keys.Any(k => floatConstantVariables.ContainsKey(k))
            || keys.Any(k => variableTypes.TryGetValue(k, out var ft) && ft == DataType.FLOAT);

        static bool IsIntDt(DataType d) => d is DataType.UINT8 or DataType.UINT16 or DataType.UINT32
            or DataType.INT8 or DataType.INT16 or DataType.INT32;
        bool isInt = !isSeq && !isClass && !isStr && !isFloat
            && (keys.Any(k => constantVariables.ContainsKey(k))
                || keys.Any(k => variableTypes.TryGetValue(k, out var it) && IsIntDt(it))
                || keys.Any(k => mutableGlobals.TryGetValue(k, out var mg) && IsIntDt(mg))
                || keys.Any(k => globals.TryGetValue(k, out var g) && !g.IsMemoryAddress && IsIntDt(g.Type)));

        // `isinstance(p, T)` where p is bound to None: None is none of the builtin
        // candidates, so the answer is known even though no flag above names it --
        // neopixel's `elif isinstance(pixel_order, tuple)` on the defaulted parameter.
        bool isNone = keys.Any(k => noneValuedNames.Contains(k));

        if (!isSeq && !isClass && !isStr && !isFloat && !isInt && !isNone)
            return null;

        bool match = false;
        foreach (var n in names)
        {
            if (n is "tuple" or "list" or "bytes" or "bytearray") match |= isSeq;
            else if (n == "str") match |= isStr;
            else if (n == "float") match |= isFloat;
            else if (n is "int" or "bool") match |= isInt;
        }
        return new Constant(match ? 1 : 0);
    }

    /// <summary>
    /// <c>isinstance(expr, int/float/str/bool/...)</c> for a receiver that is not a name: the
    /// expression's type answers, the same fold the name form does on a bound value. The
    /// receiver forms admitted here are all free of side effects, so asking the type with
    /// InferExprType emits nothing -- an f-string in particular cannot be visited as a
    /// value at all. Null when a candidate is not one of these builtins or the type is not
    /// one of them either, which leaves the refusal in place.
    /// </summary>
    private Constant? TryFoldIsinstanceOfValue(Expression recv, Expression typesExpr)
    {
        var cands = typesExpr is TupleExpr t ? t.Elements : new List<Expression> { typesExpr };
        if (cands.Count == 0) return null;
        var names = new List<string>();
        foreach (var c in cands)
        {
            if (c is not VariableExpr { Name: "int" or "bool" or "float" or "str" or "bytes"
                    or "bytearray" or "tuple" or "list" or "slice" } ve)
                return null;
            names.Add(ve.Name);
        }

        bool isBool = IsBoolExpr(recv);
        bool isStr = recv is StringLiteral or FStringExpr || StaticStringOf(recv) != null;
        DataType dt = isStr ? DataType.VOID : InferExprType(recv);
        bool isFloat = !isStr && (recv is FloatLiteral || dt == DataType.FLOAT);
        bool isInt = !isStr && !isFloat && (recv is IntegerLiteral or BooleanLiteral
            || IsIntegerType(dt));
        if (!isStr && !isFloat && !isInt) return null;

        bool match = false;
        foreach (var n in names)
        {
            if (n == "str") match |= isStr;
            else if (n == "float") match |= isFloat;
            else if (n == "int") match |= isInt;             // bool is an int too
            else if (n == "bool") match |= isInt && isBool;
        }
        return new Constant(match ? 1 : 0);
    }

    // -------------------------------------------------------------------------
    // Class hierarchy helpers — MRO resolution and virtual-dispatch gate
    // -------------------------------------------------------------------------

    /// <summary>
    /// Whether <paramref name="cls"/> IS <paramref name="target"/> or inherits from it,
    /// walking the same base-class chain <see cref="ResolveMROMethod"/> does. Used to fold
    /// `isinstance(x, T)`: with a class fixed at compile time, this is the whole answer.
    /// Bounded the same way MRO resolution is, against a base cycle this compiler should
    /// already refuse elsewhere.
    /// </summary>
    private bool IsClassOrSubclassOf(string cls, string target)
    {
        string? current = cls;
        for (int depth = 0; current != null && depth < 32; depth++)
        {
            if (current == target) return true;
            if (!classBasePrefixes.TryGetValue(current, out var parentPrefix)
                || string.IsNullOrEmpty(parentPrefix))
                break;
            current = parentPrefix!.EndsWith("_") ? parentPrefix[..^1] : parentPrefix;
        }
        return false;
    }

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
        var savedSourcePath    = currentSourcePath;
        var savedSourceFile    = currentSourceFile;
        bool savedTracksCallee = inlineTracksCalleeLine;
        int savedCalleeLine    = inlineCalleeStmtLine;
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
        inlineTracksCalleeLine = false;
        inlineCalleeStmtLine  = 0;
        currentFunctionGlobals = new HashSet<string>();

        // The handler's body is text in the file that function is DEFINED in, which can be a
        // module the registration call only imported. The wrapper keeps the caller's prefix
        // already; the file has to move the same way or a diagnostic raised inside pairs the
        // handler's line with a module that does not contain it.
        if (functionSourcePath.TryGetValue(funcDef, out var handlerPath))
        {
            currentSourcePath = handlerPath;
            currentSourceFile = handlerPath.Length > 0 ? SourceFileLabel(handlerPath) : "";
        }

        // Bind handler's first parameter to the ZCA root variable
        string paramName = currentInlinePrefix + funcDef.Params[0].Name;
        variableAliases[paramName] = zcaRootKey;
        if (instanceClasses.TryGetValue(zcaRootKey, out string? cls) && cls != null)
            instanceClasses[paramName] = cls;

        // The handler runs between any two instructions of the program, so the value main's
        // lowering holds for a field at this point is provable only for a field nothing writes
        // after its constructor. Every other field is read from memory inside the handler:
        // folding them made I2CTarget's `if t._phase == 0` always true and indexed `mem[0]`
        // for every byte, because the fold was the 0 that __init__ had just stored.
        var hiddenFields = new Dictionary<string, (int Value, bool WasKilled)>();
        foreach (var key in IsrMutableFieldKeys(zcaRootKey))
        {
            hiddenFields[key] = (constantVariables[key], killedConstants.Contains(key));
            constantVariables.Remove(key);
            killedConstants.Add(key);
        }

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
        bool savedSeqTerminated = _seqTerminated;
        _seqTerminated = false;
        VisitBlock(funcDef.Body);
        _seqTerminated = savedSeqTerminated;
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

        // A field the handler writes stays unfolded for the rest of main too: from here on the
        // interrupt can change it under any read. A field it only reads is main's to track
        // again, with the value it held before the handler was lowered.
        var writtenByHandler = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ins in wrapperFunc.Body)
        {
            foreach (var dv in Verifier.DstVals(ins))
                if (dv is Variable dvv) writtenByHandler.Add(dvv.Name);
            if (ins is AugAssign { Target: Variable av }) writtenByHandler.Add(av.Name);
        }
        foreach (var (key, (value, wasKilled)) in hiddenFields)
        {
            if (writtenByHandler.Contains(key)) continue;
            if (!wasKilled) killedConstants.Remove(key);
            constantVariables.TryAdd(key, value);
        }

        // Restore compilation state
        currentInstructions    = savedInstructions;
        currentFunction        = savedFunction;
        currentModulePrefix    = savedModulePrefix;
        currentInlinePrefix    = savedInlinePrefix;
        inlineDepth            = savedInlineDepth;
        loopStack              = savedLoopStack;
        inlineStack            = savedInlineStack;
        lastLine               = savedLastLine;
        currentSourcePath      = savedSourcePath;
        currentSourceFile      = savedSourceFile;
        inlineTracksCalleeLine = savedTracksCallee;
        inlineCalleeStmtLine   = savedCalleeLine;
        currentFunctionGlobals = savedFunctionGlobals;

        return synthName;
    }

    /// <summary>
    /// The folded fields of <paramref name="root"/> that something may write after the
    /// constructor: a method of the field's class other than `__init__` assigning
    /// `self.&lt;field&gt;`, or any store `&lt;expr&gt;.&lt;field&gt; = v` anywhere in the program
    /// whose object is not a bare `self` -- the handler's own `t.memaddr = ...`, a
    /// `obj.limit = 9` in main, `self._hw.x = v` in another class. The second rule is by name
    /// and errs on "mutable", which only costs a fold inside the handler; the first is by
    /// class, so a HAL field another class happens to share a name with keeps the constant
    /// the backend needs for a bit mask. Compile-time strings are left alone: no store can
    /// give one a run-time value.
    /// </summary>
    private List<string> IsrMutableFieldKeys(string root)
    {
        var keys = new List<string>();
        if (!instanceClasses.TryGetValue(root, out var cls) || cls == null) return keys;

        var storedByName = MemberNamesStoredOutsideSelf();
        string pfx = root + "_";
        foreach (var key in constantVariables.Keys.Where(k => k.StartsWith(pfx, StringComparison.Ordinal)).ToList())
        {
            if (strConstantVariables.ContainsKey(key)) continue;

            // Walk the path down the layouts to the class that declares the leaf field:
            // `_hw__freq` under an I2CTarget is `_hw` (an _I2C) and then its `_freq`.
            string rest = key[pfx.Length..];
            string? owner = cls;
            string? leaf = null;
            while (owner != null && classFieldLayout.TryGetValue(owner, out var lay))
            {
                if (lay.Any(f => f.Field == rest)) { leaf = rest; break; }
                var hop = lay.Where(f => rest.StartsWith(f.Field + "_", StringComparison.Ordinal)
                                         && classFieldLayout.ContainsKey(f.Type))
                             .OrderByDescending(f => f.Field.Length).FirstOrDefault();
                if (hop.Field == null) break;
                rest = rest[(hop.Field.Length + 1)..];
                owner = hop.Type;
            }

            bool mutable;
            if (leaf != null && owner != null)
                mutable = storedByName.Contains(leaf)
                          || SiblingMethodsOf(owner).Any(m => m.Key != "__init__"
                                                              && MethodMutatesFieldPublic(m.Value, leaf));
            else
                // A path the layouts do not reach: fall back to the name rule on its tail.
                mutable = storedByName.Any(n => rest == n || rest.EndsWith("_" + n, StringComparison.Ordinal));
            if (mutable) keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// Every member name some statement of the program stores into through an object that is
    /// not a bare `self`: `t.x = v`, `t.x += v`, `self.inner.x = v`, `t.x: uint8 = v`.
    /// </summary>
    private HashSet<string> MemberNamesStoredOutsideSelf()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        void Target(Expression? t)
        {
            switch (t)
            {
                case MemberAccessExpr { Object: VariableExpr { Name: "self" } }: return;
                case MemberAccessExpr ma: names.Add(ma.Member); return;
                case TupleExpr te: foreach (var el in te.Elements) Target(el); return;
            }
        }
        void Dotted(string? name)
        {
            if (string.IsNullOrEmpty(name)) return;
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || name.StartsWith("self.", StringComparison.Ordinal) && dot == 4) return;
            names.Add(name[(dot + 1)..]);
        }

        var programs = new List<ProgramNode>(importedModuleAsts.Values);
        if (mainProgramAst != null) programs.Insert(0, mainProgramAst);
        foreach (var prog in programs)
        foreach (var node in AstNodes(prog, descendIntoFunctions: true))
        {
            switch (node)
            {
                case AssignStmt a: Target(a.Target); break;
                case AugAssignStmt ag: Target(ag.Target); break;
                case AnnAssign an: Dotted(an.Target); break;
                case VarDecl vd: Dotted(vd.Name); break;
                case TupleUnpackStmt tu: foreach (var n in tu.Targets) Dotted(n); break;
            }
        }
        return names;
    }

    /// <summary>
    /// Forget the folded value of every field the called method assigns to. A Model B method
    /// writes its fields through a pointer into the instance's memory, so after the call the
    /// caller's compile-time copy is stale: `self.inner.poll()` stored 7 in `_value` and the
    /// read on the next line still folded the 0 that `__init__` put there, leaving the outer
    /// object at its initial value with no diagnostic.
    /// </summary>
    private void InvalidateFieldsWrittenByCall(string callee, string instName)
    {
        if (string.IsNullOrEmpty(instName)) return;

        string bse = instName;
        while (variableAliases.TryGetValue(bse, out var alias)) bse = alias;

        foreach (var field in FieldsWrittenBy(callee))
        {
            constantVariables.Remove(bse + "_" + field);
            strConstantVariables.Remove(bse + "_" + field);
            // A union field the callee writes is not the None the constructor's
            // mark still claims, and a narrowing proved on one path does not
            // survive a store the caller cannot see (RFC 0009 phase 3).
            noneValuedNames.Remove(bse + "_" + field);
            narrowedOptionals.Remove(bse + "_" + field);
        }
    }


    /// <summary>
    /// The string a subscript reads from, or null when the target is not a known string. Used
    /// to tell `print(s[i])` (a character) from `print(buf[i])` (a number): Python has no char
    /// type, so a one-character string is what indexing a string yields.
    /// </summary>
    private string? StringBehindSubscript(IndexExpr ix) => ix.Target switch
    {
        StringLiteral sl => sl.Value,
        VariableExpr v => ResolveStrConstant(
                              string.IsNullOrEmpty(currentFunction) ? v.Name : currentFunction + "." + v.Name)
                          ?? ResolveStrConstant(v.Name),
        _ => null,
    };

}
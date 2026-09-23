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

namespace PyMCU.IR;

/// <summary>
/// Between-passes IR checker. Enabled with PYMCU_VERIFY_IR=1 (warn on every violation)
/// or PYMCU_VERIFY_IR=strict (fail the build). Runs only when asked: IR generation emits
/// no warning otherwise, so a clean build stays byte- and stderr-identical.
///
/// Every check here exists because a program once shipped wrong code through it:
///   one-storage-key        -- module-level list filed under "main.xs" while readers
///                             used "xs"; one source name, two storage keys (9fd6afc5).
///                             Checked on the last stage only: earlier stages still
///                             carry the `main.X`/`X` spellings the optimizer
///                             canonicalizes -- a collision that survives to the
///                             backend-facing IR is the real split.
///   buffer-param-scalar    -- a bytearray/array parameter marshalled the buffer's
///                             first byte instead of its address (PyMCU#487).
///   write-exceeds-slot     -- a field's declared width was not the join of every
///                             write to it, at any nesting depth (PyMCU#488).
///   read-never-written     -- a BitCheck on a slot no path ever wrote.
///   storage-width          -- a name written at a width narrower than the
///                             widest width it is used at; the backend homes
///                             a name once (union3 register homes, PyMCU#488).
///   index-width            -- an index operand narrower than the addressing mode the
///                             storage extent requires (pymcu-avr#32).
///   tag-contract           -- an Optional/tagged return whose tag is dropped between
///                             Return.Tag and Call.TagDst (RFC 0009).
///   jump-target            -- a branch that names a label no instruction defines.
///   flash-name-resolves    -- an ArrayLoadFlash/FlashStrAddr naming a FlashData table
///                             that does not exist.
/// </summary>
public static class Verifier
{
    public sealed record Violation(string Check, string Where, string Detail)
    {
        public override string ToString() => $"{Check} {Where}: {Detail}";
    }

    public static bool Enabled
        => Environment.GetEnvironmentVariable("PYMCU_VERIFY_IR") is "1" or "strict";

    private static bool Strict
        => Environment.GetEnvironmentVariable("PYMCU_VERIFY_IR") == "strict";

    /// <summary>
    /// Run every check over <paramref name="program"/>. Called at phase boundaries; each
    /// call site passes the name of the pass that just ran, so a violation report says
    /// which stage produced it.
    /// </summary>
    public static List<Violation> Check(ProgramIR program, string pass)
    {
        if (!Enabled) return new List<Violation>();

        var violations = Verify(program, pass);
        foreach (var v in violations)
            Logger.Warning("ir-verify", $"[{pass}] {v}");

        if (Strict && violations.Count > 0)
            throw new CompilerError("VerifyError",
                $"IR verifier: {violations.Count} violation(s) after {pass} " +
                $"(first: {violations[0]})", 0, 0);

        return violations;
    }

    /// <summary>All checks, no logging -- testable directly.</summary>
    public static List<Violation> Verify(ProgramIR program) => Verify(program, "");

    /// <param name="pass">The pipeline stage being checked. Checks whose
    /// question is "does the IR the backend sees have this shape" run only on
    /// the last stage: intermediate stages legitimately hold spellings the
    /// next pass canonicalizes.</param>
    private static List<Violation> Verify(ProgramIR program, string pass)
    {
        var violations = new List<Violation>();
        var declared = DeclaredStorage(program);
        var compileTime = new HashSet<string>(program.CompileTimeNames);
        var fnByName = program.Functions
            .GroupBy(f => f.Name)
            .ToDictionary(g => g.Key, g => g.First());

        // Flash tables by name, for the flash-name and index-width checks.
        var flashTables = program.Functions
            .SelectMany(fn => fn.Body)
            .OfType<FlashData>()
            .GroupBy(fd => fd.Name)
            .ToDictionary(g => g.Key, g => g.First());

        // Parameters of each defined function that its body uses as a buffer/array:
        // the bytearray pointer slot, an ArrayLoad/Store name, a FlashLoadPtr base, or
        // re-passed onward as ArrayBase.
        var bufferParams = new Dictionary<string, HashSet<int>>();
        // Pass 1: every name each function writes (seeded with its parameters), so
        // the read checks can distinguish "written in this frame" from "written
        // only inside somebody else's frame" from "never written anywhere".
        var writtenHere = new Dictionary<string, HashSet<string>>();
        var writtenSomewhere = new HashSet<string>(declared);
        var labels = new Dictionary<string, HashSet<string>>();
        foreach (var f in program.Functions)
        {
            bufferParams[f.Name] = BufferParamIndexes(f);
            var w = new HashSet<string>(f.Params);
            var lbl = new HashSet<string>();
            foreach (var ins in f.Body)
            {
                if (ins is Label l) lbl.Add(l.Name);
                else CollectWrites(ins, w);
            }
            writtenHere[f.Name] = w;
            writtenSomewhere.UnionWith(w);
            labels[f.Name] = lbl;
        }

        // Pass 2: the checks themselves. one-storage-key asks whether the IR
        // the backend sees holds two keys for one source name; intermediate
        // stages still carry the `main.X`/`X` spellings the optimizer later
        // canonicalizes, so the check runs on the last stage only.
        bool lastPhase = pass is "" or "canfail";
        foreach (var f in program.Functions)
        {
            // `x__slot` arrays are byte-cell object storage: a field store
            // carries the whole field value in Src while the backend writes
            // one byte per cell, and the Copy feeding such a store carries
            // the same not-yet-split value. Both are the encoding, not lost
            // data, so CheckWidths leaves them alone.
            var slotSrcs = new HashSet<string>();
            foreach (var ins0 in f.Body)
                if (ins0 is ArrayStore a0 && a0.ArrayName.EndsWith("__slot"))
                {
                    var n0 = ValName(a0.Src);
                    if (n0 != null) slotSrcs.Add(n0);
                }
            var jumps = new List<(string kind, string target)>();
            foreach (var ins in f.Body)
            {
                CollectJumps(ins, jumps);
                if (lastPhase) CheckStorageKeys(ins, f, declared, violations);
                CheckReads(ins, f, writtenHere[f.Name], writtenSomewhere, declared,
                    compileTime, violations);
                CheckWidths(ins, f, compileTime, slotSrcs, violations);
                CheckIndexWidths(ins, f, flashTables, violations);
                CheckTags(ins, f, fnByName, violations);
                CheckCallArgs(ins, f, fnByName, bufferParams, program, violations);
            }

            foreach (var (kind, target) in jumps)
                // __pymcu_* / __exn_* labels are backend-runtime symbols the
                // code generator materializes itself (__pymcu_unhandled_exn is
                // the uncaught-error halt), not per-function IR labels.
                if (!labels[f.Name].Contains(target)
                    && !target.StartsWith("__pymcu_") && !target.StartsWith("__exn_"))
                    violations.Add(new Violation("jump-target", f.Name,
                        $"{kind} -> '{target}', a label no instruction defines"));
        }

        CheckStorageWidth(program, violations);

        return violations;
    }

    // ---------------------------------------------------------------------
    // Storage keys
    // ---------------------------------------------------------------------

    /// Every name that is a declared storage slot: globals, global arrays,
    /// exception machinery the backend owns outright.
    private static HashSet<string> DeclaredStorage(ProgramIR program)
    {
        var s = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in program.Globals) s.Add(g.Name);
        foreach (var k in program.GlobalArrays.Keys) s.Add(k);
        // Register alias materialized by the AVR backend at the catch dispatcher;
        // read by LoadIntoReg, never assigned a slot.
        s.Add("__exn_r22_capture");
        return s;
    }

    /// Names this instruction writes: every Dst position plus the array a store
    /// targets. BytearrayStore/StoreIndirect read their pointer, they do not
    /// write the pointer slot.
    private static void CollectWrites(Instruction ins, HashSet<string> written)
    {
        switch (ins)
        {
            case Unary x: AddDst(written, x.Dst); break;
            case Binary x: AddDst(written, x.Dst); break;
            case Copy x: AddDst(written, x.Dst); break;
            case Bitcast x: AddDst(written, x.Dst); break;
            case LoadIndirect x: AddDst(written, x.Dst); break;
            case Call x: AddDst(written, x.Dst); AddDst(written, x.TagDst); break;
            case IndirectCall x: AddDst(written, x.Dst); break;
            case VirtualCall x: AddDst(written, x.Dst); break;
            case BitCheck x: AddDst(written, x.Dst); break;
            case ArrayLoad x: AddDst(written, x.Dst); written.Add(x.ArrayName); break;
            case ArrayLoadFlash x: AddDst(written, x.Dst); break;
            case FlashLoadPtr x: AddDst(written, x.Dst); break;
            case BytearrayLoad x: AddDst(written, x.Dst); break;
            case ArrayStore x: written.Add(x.ArrayName); break;
            case AugAssign x: AddDst(written, x.Target); break;
            case BitSet x: AddDst(written, x.Target); break;
            case BitClear x: AddDst(written, x.Target); break;
            case BitWrite x: AddDst(written, x.Target); break;
            case GcAlloc x: AddDst(written, x.Dst); break;
            case InlineAsm x when x.Operands != null:
                // Operands are read-write: loaded before the asm, stored after.
                foreach (var op in x.Operands) AddDst(written, op);
                break;
        }
    }

    private static void AddDst(HashSet<string> written, Val? v)
    {
        if (v is Variable vr) written.Add(vr.Name);
        else if (v is Temporary t) written.Add(t.Name);
    }

    /// Every Val this instruction reads, in operand position. Dst slots whose
    /// instruction also reads them (AugAssign, BitSet...) are included by their
    /// own case; pure Dst vals are not.
    private static IEnumerable<Val> ReadVals(Instruction ins)
    {
        switch (ins)
        {
            case Return x:
                yield return x.Value;
                if (x.Tag != null) yield return x.Tag;
                break;
            case Unary x: yield return x.Src; break;
            case Binary x: yield return x.Src1; yield return x.Src2; break;
            case Copy x: yield return x.Src; break;
            case Bitcast x: yield return x.Src; break;
            case LoadIndirect x: yield return x.SrcPtr; break;
            case StoreIndirect x: yield return x.Src; yield return x.DstPtr; break;
            case JumpIfZero x: yield return x.Condition; break;
            case JumpIfNotZero x: yield return x.Condition; break;
            case JumpIfEqual x: yield return x.Src1; yield return x.Src2; break;
            case JumpIfNotEqual x: yield return x.Src1; yield return x.Src2; break;
            case JumpIfLessThan x: yield return x.Src1; yield return x.Src2; break;
            case JumpIfLessOrEqual x: yield return x.Src1; yield return x.Src2; break;
            case JumpIfGreaterThan x: yield return x.Src1; yield return x.Src2; break;
            case JumpIfGreaterOrEqual x: yield return x.Src1; yield return x.Src2; break;
            case Call x:
                foreach (var a in x.Args) yield return a;
                break;
            case IndirectCall x:
                yield return x.FuncAddr;
                foreach (var a in x.Args) yield return a;
                break;
            case VirtualCall x:
                yield return x.Self;
                foreach (var a in x.Args) yield return a;
                break;
            case BitSet x: yield return x.Target; break;
            case BitClear x: yield return x.Target; break;
            case BitCheck x: yield return x.Source; break;
            case BitWrite x: yield return x.Target; yield return x.Src; break;
            case JumpIfBitSet x: yield return x.Source; break;
            case JumpIfBitClear x: yield return x.Source; break;
            case AugAssign x: yield return x.Target; yield return x.Operand; break;
            case InlineAsm x when x.Operands != null:
                foreach (var op in x.Operands) yield return op;
                break;
            case ArrayLoad x: yield return x.Index; break;
            case ArrayLoadFlash x: yield return x.Index; break;
            case FlashLoadPtr x: yield return x.Ptr; yield return x.Index; break;
            case ArrayStore x: yield return x.Index; yield return x.Src; break;
            case BytearrayLoad x:
                yield return new Variable(x.PtrName);
                yield return x.Index;
                break;
            case BytearrayStore x:
                yield return new Variable(x.PtrName);
                yield return x.Index;
                yield return x.Src;
                break;
            case GcAlloc x: yield return x.Size; break;
            case GcRoot x: yield return x.Var; break;
            case GcUnroot x: yield return x.Var; break;
            case SignalError x: yield return x.Code; break;
        }
    }

    // ---------------------------------------------------------------------
    // (a) one storage key per name
    // ---------------------------------------------------------------------

    /// A dotted name "p.n" that is not itself declared storage and is not a
    /// parameter of its function, while the bare tail "n" is declared -- one
    /// source name emitted under two keys, the module-list corruption shape.
    /// The reverse direction (a bare name that shadows a declared dotted one)
    /// is caught by read-never-written when it is only ever read.
    private static void CheckStorageKeys(Instruction ins, Function f,
        HashSet<string> declared, List<Violation> violations)
    {
        foreach (var name in StorageNames(ins))
        {
            int dot = name.IndexOf('.');
            if (dot <= 0 || dot == name.Length - 1) continue;
            string head = name[..dot];
            string tail = name[(dot + 1)..];

            // The candidates for the module-global twin of this name. Inside a
            // module-init frame every binding IS module-level -- `main` is
            // exempt from the local/global trap because its locals are the
            // module's globals -- so a "frame.tail" spelling for declared "tail"
            // can only be a name filed under the wrong key. Any other function's
            // `f.n` coexisting with global `n` is a legal shadow (the trap only
            // refuses it for mutable scalar globals), which is why the check
            // fires on init frames only.
            string? twin = null;
            if (head == "main" || head == "__module_init")
                twin = tail;
            else if (head.EndsWith("___module_init"))
                // "mod___module_init.x" -> module global "mod_x"
                twin = head[..^"___module_init".Length] + "_" + tail;

            if (twin != null && declared.Contains(twin)
                && !declared.Contains(name) && !f.Params.Contains(name))
            {
                violations.Add(new Violation("one-storage-key", f.Name,
                    $"'{name}' collides with declared storage '{twin}': one source " +
                    "name is resolving to two keys"));
            }
        }
    }

    /// Names used as storage by this instruction: every Val with a name, plus the
    /// name-carrying fields (array names, bytearray pointer names, ArrayBase).
    private static IEnumerable<string> StorageNames(Instruction ins)
    {
        foreach (var v in AllVals(ins))
            switch (v)
            {
                case Variable x: yield return x.Name; break;
                case Temporary x: yield return x.Name; break;
                case ArrayBase x: yield return x.ArrayName; break;
            }
        switch (ins)
        {
            case ArrayLoad x: yield return x.ArrayName; break;
            case ArrayStore x: yield return x.ArrayName; break;
            case BytearrayLoad x: yield return x.PtrName; break;
            case BytearrayStore x: yield return x.PtrName; break;
        }
    }

    private static IEnumerable<Val> AllVals(Instruction ins)
    {
        foreach (var v in ReadVals(ins)) yield return v;
        foreach (var v in DstVals(ins)) yield return v;
    }

    /// The vals an instruction writes: every Dst position.
    private static IEnumerable<Val> DstVals(Instruction ins)
    {
        switch (ins)
        {
            case Unary x: yield return x.Dst; break;
            case Binary x: yield return x.Dst; break;
            case Copy x: yield return x.Dst; break;
            case Bitcast x: yield return x.Dst; break;
            case LoadIndirect x: yield return x.Dst; break;
            case Call x:
                yield return x.Dst;
                if (x.TagDst != null) yield return x.TagDst;
                break;
            case IndirectCall x: yield return x.Dst; break;
            case VirtualCall x: yield return x.Dst; break;
            case BitCheck x: yield return x.Dst; break;
            case ArrayLoad x: yield return x.Dst; break;
            case ArrayLoadFlash x: yield return x.Dst; break;
            case FlashLoadPtr x: yield return x.Dst; break;
            case BytearrayLoad x: yield return x.Dst; break;
            case GcAlloc x: yield return x.Dst; break;
        }
    }

    /// Names of every Variable and Temporary an instruction mentions, read or
    /// written. Shared with the generator's compile-time-name export so both
    /// sides enumerate "a name appeared in the IR" the same way.
    internal static IEnumerable<string> ScalarNames(Instruction ins)
    {
        foreach (var v in AllVals(ins))
            if (v is Variable x) yield return x.Name;
            else if (v is Temporary t) yield return t.Name;
    }

    // ---------------------------------------------------------------------
    // (b) buffer/array parameter receives an address, never a scalar
    // ---------------------------------------------------------------------

    /// Index positions of parameters the body uses as a buffer: bytearray pointer
    /// slot, fixed-array name, flash-pointer base, or forwarded as ArrayBase.
    private static HashSet<int> BufferParamIndexes(Function f)
    {
        var set = new HashSet<int>();
        for (int i = 0; i < f.Params.Count; i++)
        {
            var p = f.Params[i];
            foreach (var ins in f.Body)
            {
                bool used =
                    (ins is BytearrayLoad bl && bl.PtrName == p) ||
                    (ins is BytearrayStore bs && bs.PtrName == p) ||
                    (ins is ArrayLoad al && al.ArrayName == p) ||
                    (ins is ArrayStore ast && ast.ArrayName == p) ||
                    (ins is FlashLoadPtr flp && ValName(flp.Ptr) == p) ||
                    (ins is Call c && c.Args.OfType<ArrayBase>().Any(a => a.ArrayName == p)) ||
                    (ins is IndirectCall ic && ic.Args.OfType<ArrayBase>().Any(a => a.ArrayName == p)) ||
                    (ins is VirtualCall vc && vc.Args.OfType<ArrayBase>().Any(a => a.ArrayName == p));
                if (used) { set.Add(i); break; }
            }
        }
        return set;
    }

    private static void CheckCallArgs(Instruction ins, Function caller,
        Dictionary<string, Function> fnByName,
        Dictionary<string, HashSet<int>> bufferParams,
        ProgramIR program, List<Violation> violations)
    {
        switch (ins)
        {
            case Call c:
                CheckArgsAgainstCallee(c.FunctionName, c.Args, caller.Name,
                    fnByName, bufferParams, violations);
                break;
            case IndirectCall ic when ic.FuncAddr is FunctionRef fr:
                CheckArgsAgainstCallee(fr.FunctionName, ic.Args, caller.Name,
                    fnByName, bufferParams, violations);
                break;
            case VirtualCall vc:
                // The dynamic callee is the defining method or any override in a
                // subclass: every candidate that declares the position a buffer
                // makes the argument an address.
                var candidates = new HashSet<string> { vc.DefiningClass };
                if (program.ClassChildren.TryGetValue(vc.DefiningClass, out var kids))
                    candidates.UnionWith(kids);
                foreach (var cls in candidates)
                    CheckArgsAgainstCallee($"{cls}_{vc.MethodName}", vc.Args,
                        caller.Name, fnByName, bufferParams, violations);
                break;
        }
    }

    private static void CheckArgsAgainstCallee(string calleeName, List<Val> args,
        string callerName, Dictionary<string, Function> fnByName,
        Dictionary<string, HashSet<int>> bufferParams, List<Violation> violations)
    {
        // Only callees defined in this program carry a checkable signature;
        // runtime helpers and externs have no Body to infer a buffer use from.
        if (!fnByName.TryGetValue(calleeName, out var callee)) return;
        if (!bufferParams.TryGetValue(calleeName, out var bufIdx)) return;

        for (int i = 0; i < args.Count && i < callee.Params.Count; i++)
        {
            if (!bufIdx.Contains(i)) continue;
            var arg = args[i];
            bool addressLike = arg is ArrayBase or MemoryAddress or FlashStrAddr
                || (ValType(arg)?.SizeOf() ?? 0) >= DataTypeExtensions.PointerWidth;
            if (!addressLike)
                violations.Add(new Violation("buffer-param-scalar", callerName,
                    $"call to '{calleeName}' passes {Describe(arg)} for parameter " +
                    $"'{callee.Params[i]}', which the callee uses as a buffer -- " +
                    "a scalar where an address belongs (PyMCU#487)"));
        }
    }

    // ---------------------------------------------------------------------
    // (c) a write that cannot fit the slot's declared width
    // ---------------------------------------------------------------------

    private static void CheckWidths(Instruction ins, Function f,
        HashSet<string> compileTime, HashSet<string> slotSrcs,
        List<Violation> violations)
    {
        void Fit(Constant c, DataType slotType, string what)
        {
            if (OutOfRange(c.Value, slotType))
                violations.Add(new Violation("write-exceeds-slot", f.Name,
                    $"{what}: value {c.Value} cannot fit {slotType}"));
        }

        switch (ins)
        {
            // A store into a compile-time name truncates a placeholder byte
            // nothing reads at runtime -- the same exemption the read check
            // applies, one name one meaning.
            case Copy x when x.Src is Constant c && x.Dst is Variable or Temporary
                         && WrittenIn(compileTime, ValName(x.Dst) ?? ""):
                break;
            case Copy x when x.Src is Constant c && x.Dst is Variable or Temporary
                         && slotSrcs.Contains(ValName(x.Dst) ?? ""):
                break;
            case Copy x when x.Src is Constant c && x.Dst is Variable or Temporary:
                Fit(c, ValType(x.Dst) ?? DataType.UNKNOWN, $"copy {c.Value} -> {Describe(x.Dst)}");
                break;
            case StoreIndirect x when x.Src is Constant c:
                Fit(c, x.Elem, $"store {c.Value} through pointer as {x.Elem}");
                break;
            case ArrayStore x when x.Src is Constant c && x.ArrayName.EndsWith("__slot"):
                break;
            case ArrayStore x when x.Src is Constant c:
                Fit(c, x.ElemType, $"store {c.Value} into {x.ArrayName}[] as {x.ElemType}");
                break;
        }
    }

    private static bool OutOfRange(long v, DataType t)
    {
        switch (t)
        {
            case DataType.UINT8: return v > byte.MaxValue;
            case DataType.UINT16: return v > ushort.MaxValue;
            case DataType.UINT32: return v > uint.MaxValue;
            case DataType.INT8: return v < sbyte.MinValue || v > sbyte.MaxValue;
            case DataType.INT16: return v < short.MinValue || v > short.MaxValue;
            case DataType.INT32: return v < int.MinValue || v > int.MaxValue;
            // A negative into an unsigned slot is the type's documented wrap; only
            // a positive value past the max is an unambiguous width violation.
            default: return false;
        }
    }

    // ---------------------------------------------------------------------
    // (d) no read of a slot no path writes
    // ---------------------------------------------------------------------

    private static void CheckReads(Instruction ins, Function f,
        HashSet<string> writtenHere, HashSet<string> writtenSomewhere,
        HashSet<string> declared, HashSet<string> compileTime,
        List<Violation> violations)
    {
        foreach (var v in ReadVals(ins))
        {
            var name = ValName(v);
            if (name == null) continue;
            if (WrittenIn(writtenHere, name) || declared.Contains(name)) continue;
            // A name the generator bound to a compile-time value (an object,
            // a bound function, a literal container) is read through a
            // placeholder byte that is never meant to be written.
            if (WrittenIn(compileTime, name)) continue;

            bool dotted = name.Contains('.');
            if (Temporary.IsScratchName(name) || dotted)
            {
                // A module-frame spelling names flat module storage, the same
                // slot in every frame: a write anywhere reaches this read.
                // Only frame-local spellings (inlineN.*, an outlined body's
                // own prefix) are per-frame overlays a foreign write proves
                // nothing about.
                if (IsModuleSpelling(name) && WrittenIn(writtenSomewhere, name))
                    continue;
                violations.Add(new Violation("read-never-written", f.Name,
                    $"'{name}' is read but this function never writes it and it is " +
                    "not a parameter -- it names a slot in somebody else's frame"));
            }
            else if (!WrittenIn(writtenSomewhere, name))
            {
                // A bare name is a global by construction; one that no instruction
                // anywhere writes and nothing declares is a slot that does not exist.
                violations.Add(new Violation("read-never-written", f.Name,
                    $"'{name}' is read but no path writes it and it is not declared " +
                    "as a global -- the 'bchk on an unwritten slot' shape"));
            }
            // bare name written only inside another function: that function wrote a
            // global spelling without registering it -- same split-name family as
            // one-storage-key, flagged there if it collides.
        }
    }

    /// "main.x" and "mod___module_init.x" both resolve to module-level storage:
    /// main IS the entry module's top level, and a module-init frame is the
    /// module's own. Any other dotted head is a frame-local prefix.
    private static bool IsModuleSpelling(string name)
    {
        int dot = name.IndexOf('.');
        if (dot <= 0) return false;
        string head = name[..dot];
        return head == "main" || head.EndsWith("___module_init");
    }

    // Module storage files under two spellings (the bare name and the
    // qualified `main.X`/`module___module_init.X` form); a write to either
    // reaches a read of either -- the same twin-key rule the storage-key
    // check applies. A bare name read anywhere names the same module slot
    // its `main.` spelling does.
    private static bool WrittenIn(HashSet<string> set, string name)
    {
        if (set.Contains(name)) return true;
        if (name.StartsWith("main.")) return set.Contains(name[5..]);
        if (!name.Contains('.')) return set.Contains("main." + name);
        int dot = name.IndexOf('.');
        if (dot > 0 && name[..dot].EndsWith("___module_init"))
            return set.Contains(name[..dot][..^"___module_init".Length] + "_" + name[(dot + 1)..]);
        return false;
    }

    // ---------------------------------------------------------------------
    // (e) index operand width vs the addressing mode it forces
    // ---------------------------------------------------------------------

    private static void CheckIndexWidths(Instruction ins, Function f,
        Dictionary<string, FlashData> flashTables, List<Violation> violations)
    {
        foreach (var v in ReadVals(ins))
            if (v is FlashStrAddr fs && !flashTables.ContainsKey(fs.Name))
                violations.Add(new Violation("flash-name-resolves", f.Name,
                    $"FlashStrAddr names '{fs.Name}' but no FlashData defines it"));

        switch (ins)
        {
            case ArrayLoad x:
                CheckIndex(x.Index, x.Count * x.ElemType.SizeOf(),
                    $"array '{x.ArrayName}'", f, violations);
                break;
            case ArrayStore x:
                CheckIndex(x.Index, x.Count * x.ElemType.SizeOf(),
                    $"array '{x.ArrayName}'", f, violations);
                break;
            case ArrayLoadFlash x:
                if (flashTables.TryGetValue(x.ArrayName, out var table))
                    CheckIndex(x.Index, table.Bytes.Count,
                        $"flash table '{x.ArrayName}'", f, violations);
                else
                    violations.Add(new Violation("flash-name-resolves", f.Name,
                        $"ArrayLoadFlash names '{x.ArrayName}' but no FlashData " +
                        "defines it"));
                break;
        }
    }

    /// A storage extent over 256 bytes forces a 16-bit add to the base pointer;
    /// a 1-byte index operand can never reach the tail of it.
    private static void CheckIndex(Val index, int extentBytes, string what,
        Function f, List<Violation> violations)
    {
        if (extentBytes <= 256 || index is Constant or null) return;
        var t = ValType(index);
        if (t != null && t.Value.SizeOf() < 2)
            violations.Add(new Violation("index-width", f.Name,
                $"{what} spans {extentBytes} bytes but index {Describe(index)} is " +
                $"{t.Value} -- everything past offset 255 is unreachable " +
                "(pymcu-avr#32)"));
    }

    // ---------------------------------------------------------------------
    // (f) tagged-return contract (RFC 0009)
    // ---------------------------------------------------------------------

    private static void CheckTags(Instruction ins, Function f,
        Dictionary<string, Function> fnByName, List<Violation> violations)
    {
        bool fTagged = f.ReturnMembers is { Count: > 0 };
        switch (ins)
        {
            case Return r:
                if (fTagged && r.Tag == null)
                    violations.Add(new Violation("tag-contract", f.Name,
                        "return in a tagged-return function carries no Tag"));
                if (!fTagged && r.Tag != null)
                    violations.Add(new Violation("tag-contract", f.Name,
                        "return carries a Tag but the function declares no " +
                        "ReturnMembers"));
                break;
            case Call c:
                var tagged = fnByName.TryGetValue(c.FunctionName, out var callee)
                             && callee.ReturnMembers is { Count: > 0 };
                if (tagged && c.TagDst == null && c.Dst is not NoneVal)
                    violations.Add(new Violation("tag-contract", f.Name,
                        $"call to tagged-return '{c.FunctionName}' carries no " +
                        "TagDst -- the member byte is silently dropped"));
                if (!tagged && c.TagDst != null)
                    violations.Add(new Violation("tag-contract", f.Name,
                        $"call to '{c.FunctionName}' carries TagDst but the callee " +
                        "declares no ReturnMembers"));
                break;
            case IndirectCall ic when ic.FuncAddr is FunctionRef fr
                                       && fnByName.TryGetValue(fr.FunctionName, out var icc)
                                       && icc.ReturnMembers is { Count: > 0 }:
                violations.Add(new Violation("tag-contract", f.Name,
                    $"indirect call to tagged-return '{fr.FunctionName}' cannot " +
                    "carry a TagDst -- the member byte is lost"));
                break;
            case VirtualCall vc:
                if (fnByName.TryGetValue($"{vc.DefiningClass}_{vc.MethodName}",
                        out var m) && m.ReturnMembers is { Count: > 0 })
                    violations.Add(new Violation("tag-contract", f.Name,
                        $"virtual call to tagged-return '{m.Name}' cannot carry a " +
                        "TagDst -- the member byte is lost"));
                break;
        }
    }

    // ---------------------------------------------------------------------
    // jumps
    // ---------------------------------------------------------------------

    private static void CollectJumps(Instruction ins,
        List<(string kind, string target)> jumps)
    {
        switch (ins)
        {
            case Jump x: jumps.Add(("jmp", x.Target)); break;
            case JumpIfZero x: jumps.Add(("jz", x.Target)); break;
            case JumpIfNotZero x: jumps.Add(("jnz", x.Target)); break;
            case JumpIfEqual x: jumps.Add(("jeq", x.Target)); break;
            case JumpIfNotEqual x: jumps.Add(("jne", x.Target)); break;
            case JumpIfLessThan x: jumps.Add(("jlt", x.Target)); break;
            case JumpIfLessOrEqual x: jumps.Add(("jle", x.Target)); break;
            case JumpIfGreaterThan x: jumps.Add(("jgt", x.Target)); break;
            case JumpIfGreaterOrEqual x: jumps.Add(("jge", x.Target)); break;
            case JumpIfBitSet x: jumps.Add(("jbs", x.Target)); break;
            case JumpIfBitClear x: jumps.Add(("jbc", x.Target)); break;
            case BranchOnError x: jumps.Add(("boe", x.ErrorLabel)); break;
            case SignalError x when x.CatchLabel != null:
                jumps.Add(("sigerr", x.CatchLabel!)); break;
        }
    }

    // ---------------------------------------------------------------------
    // (i) a name's storage is at least as wide as every value stored into it
    // ---------------------------------------------------------------------

    /// The backend sizes each name once, so the width a name is WRITTEN at
    /// must cover every width the name is ever used at: a name homed 2 bytes
    /// while a 4-byte use exists elsewhere is the shape behind the union3
    /// register-home fix and the PyMCU#488 field layout. A write narrower
    /// than the widest occurrence is the one that overflows. UNKNOWN and
    /// VOID carry no width information and are ignored.
    private static void CheckStorageWidth(ProgramIR program,
        List<Violation> violations)
    {
        var widest = new Dictionary<string, (int Width, DataType Type)>();
        var narrowestWrite = new Dictionary<string, (int Width, DataType Type, string Fn)>();

        foreach (var f in program.Functions)
            foreach (var ins in f.Body)
            {
                foreach (var v in ReadVals(ins)) Track(v, f.Name, false);
                foreach (var v in DstVals(ins)) Track(v, f.Name, true);
            }

        void Track(Val v, string fn, bool isWrite)
        {
            var t = ValType(v);
            var name = ValName(v);
            if (name == null || t == null
                || t == DataType.UNKNOWN || t == DataType.VOID)
                return;
            int w = t.Value.SizeOf();
            if (!widest.TryGetValue(name, out var mw) || w > mw.Width)
                widest[name] = (w, t.Value);
            if (isWrite && (!narrowestWrite.TryGetValue(name, out var nw) || w < nw.Width))
                narrowestWrite[name] = (w, t.Value, fn);
        }

        foreach (var (name, nw) in narrowestWrite)
            if (nw.Width < widest[name].Width)
                violations.Add(new Violation("storage-width", nw.Fn,
                    $"'{name}' is written at {nw.Type} but used at " +
                    $"{widest[name].Type} elsewhere -- the backend homes a " +
                    "name once, so the wider use overflows its slot"));
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static string? ValName(Val v) => v switch
    {
        Variable x => x.Name,
        Temporary x => x.Name,
        _ => null
    };

    private static DataType? ValType(Val v) => v switch
    {
        Variable x => x.Type,
        Temporary x => x.Type,
        MemoryAddress x => x.Type,
        _ => null
    };

    private static string Describe(Val v) => v switch
    {
        Variable x => $"var '{x.Name}' ({x.Type})",
        Temporary x => $"tmp '{x.Name}' ({x.Type})",
        Constant x => $"constant {x.Value}",
        ArrayBase x => $"&'{x.ArrayName}'",
        MemoryAddress x => $"mem 0x{x.Address:X4} ({x.Type})",
        NoneVal => "none",
        FunctionRef x => $"fn '{x.FunctionName}'",
        FlashStrAddr x => $"flash-str '{x.Name}'",
        FloatConstant x => $"float {x.Value}",
        _ => v.GetType().Name
    };
}

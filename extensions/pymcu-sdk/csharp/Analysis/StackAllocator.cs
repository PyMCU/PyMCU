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

using PyMCU.IR;

namespace PyMCU.Backend.Analysis;

public class StackAllocator
{
    private class FunctionNode
    {
        public string Name = "";
        public int LocalSize;
        public List<string> Callees = new();
        public HashSet<string> Locals = new();
        public HashSet<string> Params = new();
        // Locals named by a GcRoot instruction: the shadow stack holds the slot
        // address for the whole span between the push and the matching unroot, and
        // the collector reads/writes through it at every GC that runs in between,
        // so the slot can never be shared. These get a full-body live interval.
        public HashSet<string> Rooted = new();
        // First/last body index at which each local is named by an instruction
        // operand -- the flat live interval packing works from.
        public Dictionary<string, int> FirstUse = new();
        public Dictionary<string, int> LastUse = new();
        // Every operand mention: index + access bits (1 = read, 2 = write, 3 =
        // read-modify-write). Needed to tell a loop-carried variable (read inside
        // the loop before any write inside it) from an iteration-local temp.
        public Dictionary<string, List<(int Idx, int Acc)>> Mentions = new();
        // Names that appeared as a Temporary operand -- single-definition
        // compiler temporaries, which are never read before they are written
        // inside a loop iteration, so their interval need not cover the loop.
        public HashSet<string> Temps = new();
        // Loop-carried liveness: a backward jump to an earlier Label marks the
        // region [label index, jump index]. A variable mentioned anywhere inside
        // it can carry a value around the back-edge, so its interval is extended
        // to cover the whole region.
        public List<(int Start, int End)> Loops = new();
        public int BodyLen;
        public bool Visited;
    }

    private readonly Dictionary<string, FunctionNode> _callGraph = new();
    private readonly Dictionary<string, int> _offsets = new();
    private readonly Dictionary<string, int> _offsetsBase = new();
    private readonly HashSet<string> _globalNames = [];
    // RFC 0013 phase 0c: array names (ArrayLoad/ArrayStore's own ArrayName),
    // tracked separately so the "static by exclusion" pass never reclassifies
    // one. Array placement (module-level vs function-scoped, the overlay that
    // lets sibling functions share one address) is already handled correctly
    // by the existing moduleSramArrays/GlobalArrays machinery upstream of this
    // allocator; this set exists only to keep the new pass from double-
    // deciding a name that mechanism already owns.
    private readonly HashSet<string> _arrayNames = [];
    private int _maxStackUsage;
    private int _staticEnd;

    public Dictionary<string, int> VariableSizes { get; } = new();

    // RFC 0013 phase 0: the offset one past the last byte of genuinely STATIC
    // storage (program.Globals, program.GlobalArrays, program.StaticFields) --
    // never the whole frame's high-water mark, which also counts ordinary
    // automatic locals the static allocator packs above this boundary and
    // which Python's own rules already make safe unzeroed (RFC 0013 section 3).
    // Valid after Allocate returns.
    public int StaticEnd => _staticEnd;

    // RFC 0013 phase 0c ("static by exclusion"): every name this allocator
    // decided is of AUTOMATIC duration -- a function's own parameter, local
    // or temporary (program.AutomaticLocals, the frontend's own binding
    // bookkeeping) plus every name ever seen as a compiler Temporary (always
    // automatic by construction: scratch, single-definition, never a
    // persistent object's own identity). A backend consults this directly
    // instead of re-deriving "is this home static" from StaticFields or a
    // name-shape guess: any home (an SRAM slot here, or a pool register in
    // the backend's own register allocator) whose name is absent from this
    // set is static and must read zero at boot. Valid after Allocate returns.
    public HashSet<string> AutomaticNames { get; } = new(StringComparer.Ordinal);

    // Scratch-pool fold keys from the .mir's CanonicalTemps. Consulted by
    // CalculateOffsets instead of the name's own spelling only after the plain
    // layout has failed to fit (see Allocate).
    private readonly Dictionary<string, string> _canonicalTemps = new();

    // Set only for the fallback layout (see Allocate): pooled mode also folds
    // `inlineN.` expansion prefixes, which the plain pass must not do -- the
    // historical compiler only ever merged the `inlineN_` spellings.
    private bool _pooled;

    public (Dictionary<string, int> Offsets, int MaxStack) Allocate(ProgramIR program)
    {
        // Scratch pooling fires only where the plain layout cannot fit. A program
        // whose statics fit keeps its original slot-for-slot layout -- the .mir a
        // compiler with pooling emits is name-identical to one without, so leaving
        // the merge off wherever it is unneeded keeps the emitted bytes identical
        // too. A program that would overflow SRAM folds pooled expansion temps onto
        // their canonical keys and tries again.
        var result = RunAllocate(program);
        if (result.MaxStack > (program.Device?.RamSize ?? int.MaxValue)
            && program.CanonicalTemps.Count > 0)
        {
            foreach (var kv in program.CanonicalTemps) _canonicalTemps[kv.Key] = kv.Value;
            _pooled = true;
            result = RunAllocate(program);
        }
        return result;
    }

    private (Dictionary<string, int> Offsets, int MaxStack) RunAllocate(ProgramIR program)
    {
        _offsets.Clear();
        _offsetsBase.Clear();
        _callGraph.Clear();
        _globalNames.Clear();
        _arrayNames.Clear();
        VariableSizes.Clear();
        AutomaticNames.Clear();
        _maxStackUsage = 0;
        _staticEnd = 0;

        var globalOffset = 0;
        foreach (var globalVar in program.Globals)
        {
            VariableSizes[globalVar.Name] = globalVar.Type.SizeOf();
            _offsets[globalVar.Name] = globalOffset;
            _globalNames.Add(globalVar.Name);
            globalOffset += VariableSizes[globalVar.Name];
        }

        foreach (var kvp in program.GlobalArrays)
        {
            VariableSizes[kvp.Key] = kvp.Value;
            _offsets[kvp.Key] = globalOffset;
            _globalNames.Add(kvp.Key);
            globalOffset += kvp.Value;
        }

        // RFC 0013 (docs/rfcs/0013-memory-model.md, PyMCU-rfc13), phase 0: a
        // field of a module-level instance (program.StaticFields, populated
        // when IRGenerator can trace it back to a module-level root -- see
        // its own doc comment in Tacky.cs) is treated as static duration here
        // exactly like a module global, whether or not IRGenerator's own
        // (narrower) mutableGlobals promotion also gave it a real global
        // entry above. Placed in this same
        // leading, never-recycled region -- not among the automatics a function's
        // frame packs and reuses below -- a name here is skipped by CalculateOffsets
        // exactly as a true global is (the `_globalNames.Contains` guard throughout
        // this file), and a backend's static-storage boundary (StaticEnd) can be
        // read directly off the allocator instead of over-approximated from the
        // whole frame's high-water mark, which also counts ordinary automatic
        // locals that Python's own rules already make safe unzeroed (section 3).
        foreach (var kvp in program.StaticFields)
        {
            if (_globalNames.Contains(kvp.Key)) continue; // already a real global; do not double-book
            VariableSizes[kvp.Key] = kvp.Value.SizeOf();
            _offsets[kvp.Key] = globalOffset;
            _globalNames.Add(kvp.Key);
            globalOffset += VariableSizes[kvp.Key];
        }

        _staticEnd = globalOffset;
        if (globalOffset > _maxStackUsage) _maxStackUsage = globalOffset;

        BuildGraph(program);

        // RFC 0013 phase 0c ("static by exclusion", team-lead directive
        // 2026-09-30): decide AUTOMATIC vs STATIC by exclusion instead of by
        // proving an object's duration. AUTOMATIC is exactly a function's own
        // parameter, local or temporary (program.AutomaticLocals, populated
        // by the frontend's own binding bookkeeping, plus every name any
        // FunctionNode ever saw as a Temporary -- compiler scratch is
        // automatic unconditionally, by construction, never a persistent
        // object's identity). Any OTHER name BuildGraph found as a Local of
        // some function -- most importantly a flattened `self.field`/
        // `obj.field` storage path, however many hops separate it from a
        // module-level root and however the frontend's own object-identity
        // tracking happened to spell it -- is therefore of STATIC duration:
        // give it a home in this same leading, never-recycled region
        // (exactly where Globals/GlobalArrays/StaticFields already sit)
        // instead of leaving it to the per-function frame-packing overlay
        // below, which assumes every slot it shares between functions is
        // write-before-read within one activation (section 3) -- an
        // assumption that is false for exactly these names.
        //
        // _globalNames.Contains is checked first and explicitly, not implied
        // by AutomaticNames' absence: a module global's OWN qualified binding
        // key (if boundNames ever recorded one under a function-qualified
        // spelling that does not match the global's bare storage name) must
        // never cause it to be handled twice or offset from two places.
        AutomaticNames.Clear();
        foreach (var func in program.Functions)
            foreach (var p in func.Params) AutomaticNames.Add(p);
        if (program.AutomaticLocals != null)
            foreach (var n in program.AutomaticLocals) AutomaticNames.Add(n);
        foreach (var node in _callGraph.Values)
            foreach (var t in node.Temps) AutomaticNames.Add(t);

        var staticByExclusion = new List<string>();
        var seenExclusion = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in _callGraph.Values)
            foreach (var varName in node.Locals)
            {
                if (_globalNames.Contains(varName)) continue;
                if (_arrayNames.Contains(varName)) continue;
                if (AutomaticNames.Contains(varName)) continue;
                if (seenExclusion.Add(varName)) staticByExclusion.Add(varName);
            }
        foreach (var name in staticByExclusion)
        {
            int sz = VariableSizes.GetValueOrDefault(name, 1);
            _offsets[name] = globalOffset;
            _globalNames.Add(name);
            globalOffset += sz;
        }
        if (staticByExclusion.Count > 0)
        {
            _staticEnd = globalOffset;
            if (globalOffset > _maxStackUsage) _maxStackUsage = globalOffset;
        }
        if (_callGraph.ContainsKey("main"))
            CalculateOffsets("main", globalOffset);

        // An interrupt handler can preempt main (or any function) at any instant, so its
        // locals are live CONCURRENTLY with the interrupted function's. Allocating it at the
        // same base as main (the old behavior) aliased the ISR's stack slots with main's
        // locals, so an ISR with its own locals corrupted the interrupted code's SRAM
        // variables. Give each ISR call-tree its own region ABOVE main's high-water mark
        // (and above each other ISR's, in case nested interrupts are enabled).
        var isrBase = _maxStackUsage;
        foreach (var func in program.Functions.Where(func => func.IsInterrupt && _callGraph.ContainsKey(func.Name)))
        {
            CalculateOffsets(func.Name, isrBase);
            isrBase = _maxStackUsage;
        }

        // Allocate locals for functions whose address was taken via FunctionRef (Callable).
        // Such functions are entered via IJMP rather than CALL, so they never appear in the
        // main call-graph DFS and would otherwise have no SRAM region for their locals.
        // Each one gets its own non-overlapping region so concurrent executions don't alias.
        var funcRefTargets = new HashSet<string>();
        void CollectFuncRef(Val? v) { if (v is FunctionRef fr) funcRefTargets.Add(fr.FunctionName); }
        foreach (var func in program.Functions)
            foreach (var instr in func.Body)
                switch (instr)
                {
                    // A function's address can be taken not only by `f = fn` but also by
                    // passing it as an argument (e.g. add_task(task)) or storing it into a
                    // Callable[] array — those tasks are entered via IJMP and still need
                    // their locals allocated, or STS/LDS to them resolve to no .equ.
                    case Copy c: CollectFuncRef(c.Src); break;
                    case Call call: foreach (var a in call.Args) CollectFuncRef(a); break;
                    case IndirectCall ic: foreach (var a in ic.Args) CollectFuncRef(a); break;
                    case ArrayStore ast: CollectFuncRef(ast.Src); break;
                }

        var taskBase = _maxStackUsage;
        foreach (var func in program.Functions)
        {
            if (funcRefTargets.Contains(func.Name) && _callGraph.ContainsKey(func.Name))
            {
                CalculateOffsets(func.Name, taskBase);
                taskBase = _maxStackUsage;
            }
        }

        return (_offsets, _maxStackUsage);
    }

    private void BuildGraph(ProgramIR program)
    {
        foreach (var func in program.Functions)
        {
            var node = new FunctionNode { Name = func.Name, BodyLen = func.Body.Count };
            _callGraph[func.Name] = node;

            foreach (var param in func.Params)
            {
                node.Locals.Add(param);
                node.Params.Add(param);
            }

            void NoteUse(string name, int idx, int acc)
            {
                if (!node.FirstUse.TryGetValue(name, out int f) || idx < f) node.FirstUse[name] = idx;
                if (!node.LastUse.TryGetValue(name, out int l) || idx > l) node.LastUse[name] = idx;
                if (!node.Mentions.TryGetValue(name, out var ms))
                    node.Mentions[name] = ms = new List<(int, int)>();
                ms.Add((idx, acc));
            }

            void RegisterVar(Val? val, int idx, int acc)
            {
                // Width registrations MAX: instructions may reference the same variable at
                // different widths (e.g. a uint32 result later read through a uint8-typed
                // view). Last-write-wins shrank a 4-byte local to 1 byte, so the next slot
                // (and overlaid callee frames) sat inside it -- the callee's stores then
                // corrupted the variable's high bytes.
                if (val is Variable v && !_globalNames.Contains(v.Name))
                {
                    node.Locals.Add(v.Name);
                    NoteUse(v.Name, idx, acc);
                    VariableSizes[v.Name] = Math.Max(
                        VariableSizes.TryGetValue(v.Name, out var pv) ? pv : 0, v.Type.SizeOf());
                }

                if (val is Temporary t)
                {
                    node.Locals.Add(t.Name);
                    node.Temps.Add(t.Name);
                    NoteUse(t.Name, idx, acc);
                    VariableSizes[t.Name] = Math.Max(
                        VariableSizes.TryGetValue(t.Name, out var pt) ? pt : 0, t.Type.SizeOf());
                }
            }

            var jumps = new List<(string Target, int Idx)>();
            void NoteJump(string? target, int idx)
            {
                if (target != null) jumps.Add((target, idx));
            }

            for (int i = 0; i < func.Body.Count; i++)
            {
                var instr = func.Body[i];
                switch (instr)
                {
                    case Copy c:
                        RegisterVar(c.Src, i, 1);
                        RegisterVar(c.Dst, i, 2);
                        break;
                    case Bitcast bc2:
                        RegisterVar(bc2.Src, i, 1);
                        RegisterVar(bc2.Dst, i, 2);
                        break;
                    case Binary b:
                        RegisterVar(b.Src1, i, 1);
                        RegisterVar(b.Src2, i, 1);
                        RegisterVar(b.Dst, i, 2);
                        break;
                    case Unary u:
                        RegisterVar(u.Src, i, 1);
                        RegisterVar(u.Dst, i, 2);
                        break;
                    case BitSet bs: RegisterVar(bs.Target, i, 3); break;
                    case BitClear bc: RegisterVar(bc.Target, i, 3); break;
                    case BitCheck bck:
                        RegisterVar(bck.Source, i, 1);
                        RegisterVar(bck.Dst, i, 2);
                        break;
                    case BitWrite bw:
                        RegisterVar(bw.Src, i, 1);
                        RegisterVar(bw.Target, i, 3);
                        break;
                    case Call cl:
                        node.Callees.Add(cl.FunctionName);
                        // Register dst AND args with their declared widths (Locals.Add alone
                        // recorded no size, leaving a call-result-only variable at a stale
                        // or default width).
                        RegisterVar(cl.Dst, i, 2);
                        // RFC 0009: the Optional tag byte is a second destination.
                        if (cl.TagDst != null) RegisterVar(cl.TagDst, i, 2);
                        foreach (var ca in cl.Args) RegisterVar(ca, i, 1);
                        break;
                    case Return r:
                        RegisterVar(r.Value, i, 1);
                        if (r.Tag != null) RegisterVar(r.Tag, i, 1);
                        break;
                    case Jump jm: NoteJump(jm.Target, i); break;
                    case JumpIfZero jz: RegisterVar(jz.Condition, i, 1); NoteJump(jz.Target, i); break;
                    case JumpIfNotZero jnz: RegisterVar(jnz.Condition, i, 1); NoteJump(jnz.Target, i); break;
                    case JumpIfBitSet jbs: RegisterVar(jbs.Source, i, 1); NoteJump(jbs.Target, i); break;
                    case JumpIfBitClear jbc: RegisterVar(jbc.Source, i, 1); NoteJump(jbc.Target, i); break;
                    case JumpIfEqual je: RegisterVar(je.Src1, i, 1); RegisterVar(je.Src2, i, 1); NoteJump(je.Target, i); break;
                    case JumpIfNotEqual jne: RegisterVar(jne.Src1, i, 1); RegisterVar(jne.Src2, i, 1); NoteJump(jne.Target, i); break;
                    case JumpIfLessThan jlt: RegisterVar(jlt.Src1, i, 1); RegisterVar(jlt.Src2, i, 1); NoteJump(jlt.Target, i); break;
                    case JumpIfLessOrEqual jle: RegisterVar(jle.Src1, i, 1); RegisterVar(jle.Src2, i, 1); NoteJump(jle.Target, i); break;
                    case JumpIfGreaterThan jgt: RegisterVar(jgt.Src1, i, 1); RegisterVar(jgt.Src2, i, 1); NoteJump(jgt.Target, i); break;
                    case JumpIfGreaterOrEqual jge: RegisterVar(jge.Src1, i, 1); RegisterVar(jge.Src2, i, 1); NoteJump(jge.Target, i); break;
                    case BranchOnError boe: NoteJump(boe.ErrorLabel, i); break;
                    case ArrayLoad al:
                        if (!_globalNames.Contains(al.ArrayName))
                        {
                            node.Locals.Add(al.ArrayName);
                            _arrayNames.Add(al.ArrayName);
                            // Width registrations MAX (as in RegisterVar): the first
                            // mention is not authoritative -- `b = bytearray([x, y])`
                            // stores a 2-element literal first and a later `b += c`
                            // re-mentions the array with its grown count, so first
                            // mention alone under-allocates the backing bytes.
                            VariableSizes[al.ArrayName] = Math.Max(
                                VariableSizes.TryGetValue(al.ArrayName, out var pal) ? pal : 0,
                                al.Count * al.ElemType.SizeOf());
                            NoteUse(al.ArrayName, i, 1);
                        }

                        RegisterVar(al.Index, i, 1);
                        RegisterVar(al.Dst, i, 2);
                        break;
                    case ArrayLoadFlash alf:
                        RegisterVar(alf.Index, i, 1);
                        RegisterVar(alf.Dst, i, 2);
                        break;
                    case ArrayStore ast:
                        if (!_globalNames.Contains(ast.ArrayName))
                        {
                            node.Locals.Add(ast.ArrayName);
                            _arrayNames.Add(ast.ArrayName);
                            VariableSizes[ast.ArrayName] = Math.Max(
                                VariableSizes.TryGetValue(ast.ArrayName, out var pas) ? pas : 0,
                                ast.Count * ast.ElemType.SizeOf());
                            NoteUse(ast.ArrayName, i, 2);
                        }

                        RegisterVar(ast.Index, i, 1);
                        RegisterVar(ast.Src, i, 1);
                        break;
                    case IndirectCall ic:
                        RegisterVar(ic.FuncAddr, i, 1);
                        foreach (var icArg in ic.Args)
                            RegisterVar(icArg, i, 1);
                        RegisterVar(ic.Dst, i, 2);
                        break;
                    case VirtualCall vc:
                        RegisterVar(vc.Self, i, 1);
                        foreach (var vArg in vc.Args)
                            RegisterVar(vArg, i, 1);
                        RegisterVar(vc.Dst, i, 2);
                        break;
                    case LoadIndirect li:
                        RegisterVar(li.SrcPtr, i, 1);
                        RegisterVar(li.Dst, i, 2);
                        break;
                    case StoreIndirect si:
                        RegisterVar(si.Src, i, 1);
                        RegisterVar(si.DstPtr, i, 1);
                        break;
                    case AugAssign aa:
                        RegisterVar(aa.Target, i, 3);
                        RegisterVar(aa.Operand, i, 1);
                        break;
                    case GcAlloc ga:
                        RegisterVar(ga.Size, i, 1);
                        RegisterVar(ga.Dst, i, 2);
                        break;
                    case GcRoot gr:
                        RegisterVar(gr.Var, i, 3);
                        switch (gr.Var)
                        {
                            case Variable gv when !_globalNames.Contains(gv.Name): node.Rooted.Add(gv.Name); break;
                            case Temporary gt: node.Rooted.Add(gt.Name); break;
                        }
                        break;
                    case GcUnroot gu:
                        RegisterVar(gu.Var, i, 3);
                        break;
                    case SignalError se:
                        RegisterVar(se.Code, i, 1);
                        NoteJump(se.CatchLabel, i);
                        break;
                    case InlineAsm ia:
                        if (ia.Operands != null)
                            foreach (var op in ia.Operands)
                                RegisterVar(op, i, 3);
                        break;
                    case FlashLoadPtr flp:
                        // Ptr is a 16-bit flash byte-address; register it (typically the
                        // function's flash-string parameter) so it is sized as 2 bytes.
                        RegisterVar(flp.Ptr, i, 1);
                        RegisterVar(flp.Index, i, 1);
                        RegisterVar(flp.Dst, i, 2);
                        break;
                    case BytearrayLoad bl:
                        // bytearray pointer params are UINT16 (2-byte address); must be sized explicitly
                        // because PtrName is a string, not a Val, so RegisterVar never sees it.
                        // MAX, not overwrite: a name already registered wider (e.g. as an
                        // ArrayStore backing array) must not shrink back to the pointer size.
                        VariableSizes[bl.PtrName] = Math.Max(
                            VariableSizes.TryGetValue(bl.PtrName, out var pbl) ? pbl : 0, 2);
                        if (!_globalNames.Contains(bl.PtrName)) NoteUse(bl.PtrName, i, 1);
                        RegisterVar(bl.Index, i, 1);
                        RegisterVar(bl.Dst, i, 2);
                        break;
                    case BytearrayStore bs:
                        VariableSizes[bs.PtrName] = Math.Max(
                            VariableSizes.TryGetValue(bs.PtrName, out var pbs) ? pbs : 0, 2);
                        if (!_globalNames.Contains(bs.PtrName)) NoteUse(bs.PtrName, i, 1);
                        RegisterVar(bs.Index, i, 1);
                        RegisterVar(bs.Src, i, 1);
                        break;
                }
            }

            // A backward jump to an earlier Label is a loop back-edge; the code
            // region between them executes repeatedly, so a variable mentioned
            // inside it may carry a value around the edge.
            var labelIndex = new Dictionary<string, int>();
            for (int i = 0; i < func.Body.Count; i++)
                if (func.Body[i] is Label lbl)
                    labelIndex[lbl.Name] = i;
            foreach (var (target, jumpIdx) in jumps)
                if (labelIndex.TryGetValue(target, out int tIdx) && tIdx < jumpIdx)
                    node.Loops.Add((tIdx, jumpIdx));

            node.LocalSize = node.Locals.Count;
        }
    }

    private void CalculateOffsets(string funcName, int currentBase)
    {
        var node = _callGraph[funcName];
        if (node.Visited) return;
        node.Visited = true;

        if (node.Locals.Count > 0)
        {
            var first = node.Locals.GetEnumerator();
            first.MoveNext();
            if (_offsets.ContainsKey(first.Current))
            {
                if (currentBase <= (_offsetsBase.GetValueOrDefault(funcName, 0)))
                {
                    node.Visited = false;
                    return;
                }
            }
        }
        else if (_offsetsBase.TryGetValue(funcName, out var value))
        {
            if (currentBase <= value)
            {
                node.Visited = false;
                return;
            }
        }

        _offsetsBase[funcName] = currentBase;

        // --- Inline-copy slot merging ---
        // Variables named `inlineN_FUNCNAME_rest` that differ only in N (the inline
        // copy index) are sequential, non-overlapping copies of the same local from
        // function FUNCNAME.  Assign them to the same SRAM slot so that N copies of
        // _read_byte() do not consume N × k bytes; instead they share k bytes total.
        //
        // Group key = everything after the leading "inlineN_" token.
        // For variables without the inline prefix, the key is the variable name itself
        // and no merging occurs.
        var canonicalOffset = new Dictionary<string, int>();   // canonical → assigned offset
        var canonicalSize   = new Dictionary<string, int>();   // canonical → max slot size
        var varCanonical    = new Dictionary<string, string>(); // varName  → canonical

        foreach (var varName in node.Locals)
        {
            if (_globalNames.Contains(varName)) continue;
            string canonical = _canonicalTemps.TryGetValue(varName, out var poolKey)
                ? poolKey
                : StripInlinePrefix(varName, _pooled);
            varCanonical[varName] = canonical;
            int sz = VariableSizes.GetValueOrDefault(varName, 1);
            if (canonicalSize.TryGetValue(canonical, out int prev))
                canonicalSize[canonical] = Math.Max(prev, sz);
            else
                canonicalSize[canonical] = sz;
        }

        // --- Live-interval packing ---
        // Each local gets a live interval [first mention, last mention]. Params
        // are live from entry, and GcRoot'd variables get the whole body: the
        // shadow stack scans their slots at every collection, so another
        // variable can never share them. A canonical group (inlineN_ copies of
        // the same local) takes the union of its members' intervals.
        //
        // Loop extension: a variable whose interval overlaps a loop region may
        // carry a value around the back-edge -- a read inside the loop before
        // any write inside it sees the previous iteration's value. Named
        // variables extend conservatively on any overlap: a conditional write
        // could sit between a flat "write then read" order. Compiler
        // temporaries (single-definition, expression-local by construction)
        // extend only when a read inside the loop precedes every write inside
        // it -- otherwise they are iteration-local and free to share slots.
        var ivStart = new Dictionary<string, int>();
        var ivEnd = new Dictionary<string, int>();
        foreach (var varName in node.Locals)
        {
            if (_globalNames.Contains(varName)) continue;
            int s = node.FirstUse.GetValueOrDefault(varName, 0);
            int e = node.LastUse.GetValueOrDefault(varName, node.BodyLen - 1);
            if (node.Params.Contains(varName)) s = 0;
            if (node.Rooted.Contains(varName)) { s = 0; e = node.BodyLen - 1; }
            ivStart[varName] = s;
            ivEnd[varName] = e;
        }

        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var varName in ivStart.Keys)
            {
                int s = ivStart[varName], e = ivEnd[varName];
                bool isTemp = node.Temps.Contains(varName);
                foreach (var (ls, le) in node.Loops)
                {
                    if (e < ls || s > le) continue;   // no overlap with the loop body
                    if (s <= ls && e >= le) continue; // already covers it
                    bool carried;
                    if (isTemp && node.Mentions.TryGetValue(varName, out var ms))
                    {
                        int rMin = int.MaxValue, wMin = int.MaxValue;
                        foreach (var (mi, acc) in ms)
                        {
                            if (mi < ls || mi > le) continue;
                            if ((acc & 1) != 0 && mi < rMin) rMin = mi;
                            if ((acc & 2) != 0 && mi < wMin) wMin = mi;
                        }
                        carried = rMin < wMin;
                    }
                    else carried = true;
                    if (!carried) continue;
                    if (ls < s) { s = ls; grew = true; }
                    if (le > e) { e = le; grew = true; }
                }
                ivStart[varName] = s;
                ivEnd[varName] = e;
            }
        }

        var groupStart = new Dictionary<string, int>();
        var groupEnd = new Dictionary<string, int>();
        foreach (var varName in node.Locals)
        {
            if (_globalNames.Contains(varName)) continue;
            string canonical = varCanonical[varName];
            int s = ivStart[varName], e = ivEnd[varName];
            if (groupStart.TryGetValue(canonical, out int gs)) { if (s < gs) groupStart[canonical] = s; }
            else groupStart[canonical] = s;
            if (groupEnd.TryGetValue(canonical, out int ge)) { if (e > ge) groupEnd[canonical] = e; }
            else groupEnd[canonical] = e;
        }

        // Only canonical groups made entirely of compiler temporaries share
        // slots; named locals keep exclusive slots.
        var PackableGroups = new HashSet<string>(
            groupStart.Keys.Where(canon =>
                node.Locals.Where(v => !_globalNames.Contains(v) && varCanonical[v] == canon)
                           .All(v => node.Temps.Contains(v))));

        // Greedy interval partition, best-fit on slot size: process groups by
        // start index, reuse the smallest vacated slot that fits, else grow the
        // frame. A slot is free only when its previous occupant's interval ended
        // strictly before the new group's starts -- two variables whose mentions
        // share an instruction index (a dying source and a fresh destination in
        // the same Copy, say) never share a slot, so operand read/write order
        // inside an instruction can never alias them.
        int currentFrameSize = 0;
        var busy = new List<(int Offset, int Size, int Until)>();
        foreach (var canonical in groupStart.Keys
                     .OrderBy(k => groupStart[k])
                     .ThenByDescending(k => canonicalSize[k]))
        {
            int sz = canonicalSize[canonical];
            int start = groupStart[canonical];
            int pick = -1;
            for (int bi = 0; bi < busy.Count; bi++)
            {
                if (busy[bi].Until >= start || busy[bi].Size < sz) continue;
                if (pick < 0 || busy[bi].Size < busy[pick].Size) pick = bi;
            }
            if (pick < 0)
            {
                canonicalOffset[canonical] = currentBase + currentFrameSize;
                // Non-packable groups own their slot for the whole frame: mark
                // the slot busy forever so a later group can never take it.
                busy.Add((currentBase + currentFrameSize, sz,
                          PackableGroups.Contains(canonical) ? groupEnd[canonical] : int.MaxValue));
                currentFrameSize += sz;
            }
            else
            {
                // A named local may take a slot a temp has vacated, but from then on it owns
                // it exactly as a fresh one: its own last mention is not when the slot frees.
                // Freeing it at groupEnd handed it on to the next named local, so a caller
                // staging two arguments into its callee's parameters (`math_atan2.y` then
                // `math_atan2.x`, one instruction apart) gave both the same slot, and
                // `asin(x)` became `atan2(v, v)`.
                canonicalOffset[canonical] = busy[pick].Offset;
                busy[pick] = (busy[pick].Offset, busy[pick].Size,
                              PackableGroups.Contains(canonical) ? groupEnd[canonical] : int.MaxValue);
            }
        }

        foreach (var varName in node.Locals)
        {
            if (_globalNames.Contains(varName)) continue;
            _offsets[varName] = canonicalOffset[varCanonical[varName]];
        }

        int childrenBase = currentBase + currentFrameSize;
        if (childrenBase > _maxStackUsage) _maxStackUsage = childrenBase;

        foreach (var callee in node.Callees)
        {
            if (_callGraph.ContainsKey(callee))
                CalculateOffsets(callee, childrenBase);
        }

        node.Visited = false;
    }

    /// <summary>
    /// Strip the leading "inlineN_" prefix (where N is one or more digits) from a
    /// variable name to get its canonical group key.  Multiple inline copies of the
    /// same function produce variables like "inline2__read_byte_result" and
    /// "inline3__read_byte_result"; both share canonical key "_read_byte_result" and
    /// can therefore occupy the same SRAM slot.
    ///
    /// Variables without the prefix are their own canonical key (no merging).
    /// Nested prefixes ("inline2_inline3_...") are stripped one level at a time so
    /// deeply nested inline chains also benefit.
    /// </summary>
    private static string StripInlinePrefix(string name, bool stripDot = false)
    {
        // Match "inline" + digits + "_" at the start of the name; the pooled
        // pass also folds "inline" + digits + ".".
        if (!name.StartsWith("inline", StringComparison.Ordinal)) return name;
        int i = 6; // length of "inline"
        while (i < name.Length && char.IsDigit(name[i])) i++;
        if (i < name.Length && (name[i] == '_' || (stripDot && name[i] == '.')))
            return name[(i + 1)..]; // strip "inlineN_" / "inlineN.", keep the rest
        return name;
    }
}
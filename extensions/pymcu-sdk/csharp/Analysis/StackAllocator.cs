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
        public bool Visited;
    }

    private readonly Dictionary<string, FunctionNode> _callGraph = new();
    private readonly Dictionary<string, int> _offsets = new();
    private readonly Dictionary<string, int> _offsetsBase = new();
    private readonly HashSet<string> _globalNames = [];
    private int _maxStackUsage;

    public Dictionary<string, int> VariableSizes { get; } = new();

    public (Dictionary<string, int> Offsets, int MaxStack) Allocate(ProgramIR program)
    {
        _offsets.Clear();
        _offsetsBase.Clear();
        _callGraph.Clear();
        _globalNames.Clear();
        VariableSizes.Clear();
        _maxStackUsage = 0;

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

        if (globalOffset > _maxStackUsage) _maxStackUsage = globalOffset;

        BuildGraph(program);

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
            var node = new FunctionNode { Name = func.Name };
            _callGraph[func.Name] = node;

            foreach (var param in func.Params)
                node.Locals.Add(param);

            void RegisterVar(Val val)
            {
                // Width registrations MAX: instructions may reference the same variable at
                // different widths (e.g. a uint32 result later read through a uint8-typed
                // view). Last-write-wins shrank a 4-byte local to 1 byte, so the next slot
                // (and overlaid callee frames) sat inside it -- the callee's stores then
                // corrupted the variable's high bytes.
                if (val is Variable v && !_globalNames.Contains(v.Name))
                {
                    node.Locals.Add(v.Name);
                    VariableSizes[v.Name] = Math.Max(
                        VariableSizes.TryGetValue(v.Name, out var pv) ? pv : 0, v.Type.SizeOf());
                }

                if (val is Temporary t)
                {
                    node.Locals.Add(t.Name);
                    VariableSizes[t.Name] = Math.Max(
                        VariableSizes.TryGetValue(t.Name, out var pt) ? pt : 0, t.Type.SizeOf());
                }
            }

            foreach (var instr in func.Body)
            {
                switch (instr)
                {
                    case Copy c:
                        RegisterVar(c.Src);
                        RegisterVar(c.Dst);
                        break;
                    case Bitcast bc2:
                        RegisterVar(bc2.Src);
                        RegisterVar(bc2.Dst);
                        break;
                    case Binary b:
                        RegisterVar(b.Src1);
                        RegisterVar(b.Src2);
                        RegisterVar(b.Dst);
                        break;
                    case Unary u:
                        RegisterVar(u.Src);
                        RegisterVar(u.Dst);
                        break;
                    case BitSet bs: RegisterVar(bs.Target); break;
                    case BitClear bc: RegisterVar(bc.Target); break;
                    case BitCheck bck:
                        RegisterVar(bck.Source);
                        RegisterVar(bck.Dst);
                        break;
                    case BitWrite bw:
                        RegisterVar(bw.Src);
                        RegisterVar(bw.Target);
                        break;
                    case Call cl:
                        node.Callees.Add(cl.FunctionName);
                        // Register dst AND args with their declared widths (Locals.Add alone
                        // recorded no size, leaving a call-result-only variable at a stale
                        // or default width).
                        RegisterVar(cl.Dst);
                        foreach (var ca in cl.Args) RegisterVar(ca);
                        break;
                    case Return r: RegisterVar(r.Value); break;
                    case JumpIfZero jz: RegisterVar(jz.Condition); break;
                    case JumpIfNotZero jnz: RegisterVar(jnz.Condition); break;
                    case JumpIfBitSet jbs: RegisterVar(jbs.Source); break;
                    case JumpIfBitClear jbc: RegisterVar(jbc.Source); break;
                    case JumpIfEqual je: RegisterVar(je.Src1); RegisterVar(je.Src2); break;
                    case JumpIfNotEqual jne: RegisterVar(jne.Src1); RegisterVar(jne.Src2); break;
                    case JumpIfLessThan jlt: RegisterVar(jlt.Src1); RegisterVar(jlt.Src2); break;
                    case JumpIfLessOrEqual jle: RegisterVar(jle.Src1); RegisterVar(jle.Src2); break;
                    case JumpIfGreaterThan jgt: RegisterVar(jgt.Src1); RegisterVar(jgt.Src2); break;
                    case JumpIfGreaterOrEqual jge: RegisterVar(jge.Src1); RegisterVar(jge.Src2); break;
                    case ArrayLoad al:
                        if (!_globalNames.Contains(al.ArrayName) && node.Locals.Add(al.ArrayName))
                        {
                            VariableSizes[al.ArrayName] = al.Count * al.ElemType.SizeOf();
                        }

                        RegisterVar(al.Index);
                        RegisterVar(al.Dst);
                        break;
                    case ArrayStore ast:
                        if (!_globalNames.Contains(ast.ArrayName) && node.Locals.Add(ast.ArrayName))
                        {
                            VariableSizes[ast.ArrayName] = ast.Count * ast.ElemType.SizeOf();
                        }

                        RegisterVar(ast.Index);
                        RegisterVar(ast.Src);
                        break;
                    case IndirectCall ic:
                        RegisterVar(ic.FuncAddr);
                        foreach (var icArg in ic.Args)
                            RegisterVar(icArg);
                        RegisterVar(ic.Dst);
                        break;
                    case LoadIndirect li:
                        RegisterVar(li.SrcPtr);
                        RegisterVar(li.Dst);
                        break;
                    case StoreIndirect si:
                        RegisterVar(si.Src);
                        RegisterVar(si.DstPtr);
                        break;
                    case AugAssign aa:
                        RegisterVar(aa.Target);
                        RegisterVar(aa.Operand);
                        break;
                    case GcAlloc ga:
                        RegisterVar(ga.Size);
                        RegisterVar(ga.Dst);
                        break;
                    case FlashLoadPtr flp:
                        // Ptr is a 16-bit flash byte-address; register it (typically the
                        // function's flash-string parameter) so it is sized as 2 bytes.
                        RegisterVar(flp.Ptr);
                        RegisterVar(flp.Index);
                        RegisterVar(flp.Dst);
                        break;
                    case BytearrayLoad bl:
                        // bytearray pointer params are UINT16 (2-byte address); must be sized explicitly
                        // because PtrName is a string, not a Val, so RegisterVar never sees it.
                        VariableSizes[bl.PtrName] = 2;
                        RegisterVar(bl.Index);
                        RegisterVar(bl.Dst);
                        break;
                    case BytearrayStore bs:
                        VariableSizes[bs.PtrName] = 2;
                        RegisterVar(bs.Index);
                        RegisterVar(bs.Src);
                        break;
                }
            }

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
            string canonical = StripInlinePrefix(varName);
            varCanonical[varName] = canonical;
            int sz = VariableSizes.GetValueOrDefault(varName, 1);
            if (canonicalSize.TryGetValue(canonical, out int prev))
                canonicalSize[canonical] = Math.Max(prev, sz);
            else
                canonicalSize[canonical] = sz;
        }

        int currentFrameSize = 0;
        foreach (var varName in node.Locals)
        {
            if (_globalNames.Contains(varName)) continue;
            string canonical = varCanonical[varName];
            if (!canonicalOffset.TryGetValue(canonical, out int assignedOffset))
            {
                // First member of this canonical group: claim a new slot.
                assignedOffset = currentBase + currentFrameSize;
                canonicalOffset[canonical] = assignedOffset;
                currentFrameSize += canonicalSize[canonical];
            }
            _offsets[varName] = assignedOffset;
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
    private static string StripInlinePrefix(string name)
    {
        // Match "inline" + digits + "_" at the start of the name.
        if (!name.StartsWith("inline", StringComparison.Ordinal)) return name;
        int i = 6; // length of "inline"
        while (i < name.Length && char.IsDigit(name[i])) i++;
        if (i < name.Length && name[i] == '_')
            return name[(i + 1)..]; // strip "inlineN_", keep the rest
        return name;
    }
}
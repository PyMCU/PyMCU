// SPDX-License-Identifier: MIT
using AVR8Sharp.Core;

namespace PyMCU.AVR.Profiler;

/// <summary>
/// Intercepts CALL/RCALL/ICALL/RET/RETI opcodes to maintain an accurate call
/// stack, then emits periodic stack snapshots in Speedscope's "sampled" format.
///
/// The sampled format is robust to preemptive RTOS context switches: because
/// each sample is an independent snapshot, a RETI that jumps to a different task
/// cannot create cyclic frame relationships (which caused infinite recursion in
/// speedscope's internal sort when using the evented format).
///
/// Per-task stacks: each RETI-from-ISR (hardware interrupt) identifies which
/// task is being resumed by reading the return PC from the hardware stack and
/// finding which existing task stack contains that function.  This supports any
/// number of RTOS tasks without needing prior knowledge of how many tasks exist.
/// </summary>
public sealed class CallStackTracker
{
    private readonly SymbolMap _symbols;
    private readonly Cpu _cpu;
    public bool DebugTrace { get; set; }

    private readonly List<string> _frames = new();
    private readonly Dictionary<string, int> _frameIndex = new();

    // Per-task call stacks.  The list grows on demand as the profiler discovers
    // new RTOS tasks (each RETI-from-ISR that resumes an unknown function creates
    // a new slot).  Any number of tasks is supported.
    // Each entry: (name, isTracked). Untracked frames (private helpers whose
    // mangled name starts with '_') are excluded from stack snapshots; their
    // cycles roll up to the nearest user-visible ancestor.
    private readonly List<Stack<(string Name, bool Tracked)>> _taskStacks = [
        new Stack<(string Name, bool Tracked)>(),   // slot 0 = initial task (main)
    ];
    private int _activeTask = 0;
    private Stack<(string Name, bool Tracked)> _callStack => _taskStacks[_activeTask];

    // Optional SRAM byte address of the RTOS "current task index" variable.
    // When provided, the exact task ID is read from RAM on every RETI-from-ISR,
    // enabling correct N-task context switching without any call-graph heuristics.
    private readonly uint? _taskIdAddr;

    // Sampled profile data: parallel lists of stack snapshots and their weights.
    private readonly List<int[]> _samples = new();
    private readonly List<long> _weights = new();
    private ulong _lastSampleCycles = 0;

    // Sample every 500 cycles (~31 µs at 16 MHz). For a 5-second simulation this
    // yields ~160 000 samples, which is well within speedscope's capacity.
    private const ulong SampleInterval = 500;

    // Hardware interrupt detection: ISR entry is NOT a CALL, it's a hardware PC
    // jump to the vector table [0x0001..0x0019]. Detect by PC discontinuity.
    private uint _prevExpectedNextPc;
    private bool _initialized;
    private bool _inIsr;
    private const uint VectorMin = 0x0001u;
    private const uint VectorMax = 0x0019u;

    public CallStackTracker(SymbolMap symbols, Cpu cpu, string rootFrame = "main", uint? taskIdAddr = null)
    {
        _symbols = symbols;
        _cpu = cpu;
        _taskIdAddr = taskIdAddr;
        // main is entered via RJMP from the reset vector, not via CALL.
        _taskStacks[0].Push((rootFrame, Tracked: true));
    }

    private int GetOrAddFrame(string name)
    {
        if (_frameIndex.TryGetValue(name, out var i)) return i;
        i = _frames.Count;
        _frames.Add(name);
        _frameIndex[name] = i;
        return i;
    }

    public void OnInstruction(uint pc, ulong cycles)
    {
        // Hardware ISR detection: PC jumped to the vector table without a CALL.
        if (_initialized && pc != _prevExpectedNextPc
            && pc >= VectorMin && pc <= VectorMax)
        {
            var vecOp = _cpu.ProgramMemory[(int)pc];
            if ((vecOp & 0xF000) == 0xC000) // RJMP at vector slot
            {
                var k = (int)(vecOp & 0x0FFF);
                if ((k & 0x0800) != 0) k -= 0x1000; // sign-extend 12-bit
                var isrAddr = (uint)((int)(pc + 1) + k);
                var isrName = _symbols.Resolve(isrAddr);
                if (DebugTrace)
                    Console.Error.WriteLine($"[ISR]  pc={pc:x4} → isrAddr={isrAddr:x4} name={isrName} task={_activeTask} depth_before={_callStack.Count}");
                PushFrame(isrName, pc);
                _inIsr = true;
            }
        }
        // Tail-call / RJMP-to-function detection: PC discontinuity outside the vector
        // table to an exact known symbol.  Handles two patterns:
        //   1. Empty-stack bootstrap: task entered via RETI (fake-stack context switch)
        //      → push the target symbol as the root frame for this task.
        //   2. Tail-call replacement: `rjmp function` from inside another function
        //      → replace the current top frame with the jump target.
        else if (_initialized && pc != _prevExpectedNextPc && pc > VectorMax)
        {
            var name = _symbols.Resolve(pc);
            if (!name.StartsWith("["))  // exact symbol address
            {
                if (_callStack.Count == 0)
                {
                    // Task was entered via RETI (context switch) — bootstrap root frame.
                    if (DebugTrace)
                        Console.Error.WriteLine($"[ROOT]  pc={pc:x4} name={name} task={_activeTask}");
                    PushFrame(name, pc);
                }
                else
                {
                    // Tail-call via RJMP/RJMP-chain to a known function.
                    var prev = _callStack.Peek().Name;
                    if (prev != name)  // avoid re-pushing the same function (loop back)
                    {
                        if (DebugTrace)
                            Console.Error.WriteLine($"[TAIL]  pc={pc:x4} name={name} replaces={prev} task={_activeTask}");
                        PopFrame(pc);
                        PushFrame(name, pc);
                    }
                }
            }
        }

        var op = _cpu.ProgramMemory[(int)pc];

        if ((op & 0xFE0E) == 0x940E)                        // CALL (abs, 2-word)
        {
            var next = _cpu.ProgramMemory[(int)(pc + 1)];
            var target = (uint)(next | ((op & 1) << 16) | ((op & 0x1F0) << 13));
            PushFrame(_symbols.Resolve(target), pc);
        }
        else if ((op & 0xF000) == 0xD000 && op != 0xDFFF)  // RCALL (rel)
        {
            var k = (int)(op & 0x7FF) - ((op & 0x800) != 0 ? 0x800 : 0);
            var target = (uint)((int)(pc + 1) + k);
            PushFrame(_symbols.Resolve(target), pc);
        }
        else if (op == 0x9509)                               // ICALL (indirect via Z)
        {
            var target = (uint)(_cpu.Mmio.Data[30] | (_cpu.Mmio.Data[31] << 8));
            PushFrame(_symbols.Resolve(target), pc);
        }
        else if (op is 0x9508 or 0x9518)                    // RET / RETI
        {
            if (op == 0x9518 && _inIsr)
            {
                // RETI from hardware ISR: context switch to the next RTOS task.
                // Read the return PC from the hardware stack (the CPU has already
                // restored the new task's SP and registers during portRESTORE_CONTEXT,
                // so SP+1:SP+2 now contains the new task's saved program counter).
                var spl = _cpu.Mmio.Data[0x5D];
                var sph = _cpu.Mmio.Data[0x5E];
                var sp  = (uint)(spl | (sph << 8));
                var pch = _cpu.Mmio.Data[sp + 1];
                var pcl = _cpu.Mmio.Data[sp + 2];
                var retPc = (uint)((pch << 8) | pcl);

                if (DebugTrace)
                    Console.Error.WriteLine($"[RETI] pc={pc:x4} task={_activeTask} isr_popped={(_callStack.Count > 0 ? _callStack.Peek().Name : "<empty>")} SP=0x{sp:X4} retPc=0x{retPc:X4} resumeFn={_symbols.ResolveContaining(retPc)}");

                PopFrame(pc); // pop ISR frame (_systick etc.)
                _activeTask = FindOrCreateTaskSlot(retPc);
                _inIsr = false;

                if (DebugTrace)
                    Console.Error.WriteLine($"[TOGGLE] now task={_activeTask} depth={_callStack.Count} top={(_callStack.Count > 0 ? _callStack.Peek().Name : "<empty>")}");
            }
            else
            {
                PopFrame(pc);
            }
        }

        // Periodic snapshot: record the current call stack every SampleInterval cycles.
        if (cycles - _lastSampleCycles >= SampleInterval)
            RecordSample(cycles);

        _prevExpectedNextPc = pc + GetInstructionWords(op);
        _initialized = true;
    }

    private static uint GetInstructionWords(uint op)
    {
        if ((op & 0xFE0E) == 0x940Cu) return 2u; // JMP
        if ((op & 0xFE0E) == 0x940Eu) return 2u; // CALL
        if ((op & 0xFC0F) == 0x9000u) return 2u; // LDS / STS
        return 1u;
    }

    /// <summary>
    /// Determines which task slot to activate after a RETI-from-ISR context switch.
    ///
    /// When a <see cref="_taskIdAddr"/> is configured (recommended), the exact task
    /// index is read directly from the RTOS's "current task" variable in SRAM.
    /// Slots are created on demand the first time each task ID is encountered.
    ///
    /// Without a task-ID address the call-graph heuristic is used as a fallback:
    /// the resume PC is resolved to a containing function and matched against
    /// existing task stacks.  This works for tasks with disjoint call graphs but
    /// may misidentify tasks when the same function appears in multiple task stacks.
    /// </summary>
    private int FindOrCreateTaskSlot(uint retPc)
    {
        // ── Preferred: read task ID directly from the RTOS variable in SRAM ──────
        if (_taskIdAddr.HasValue)
        {
            var taskId = (int)_cpu.Mmio.Data[_taskIdAddr.Value];
            // Grow the slot list to accommodate this task ID.
            while (_taskStacks.Count <= taskId)
                _taskStacks.Add(new Stack<(string Name, bool Tracked)>());
            return taskId;
        }

        // ── Fallback: call-graph heuristic (works for tasks with unique functions) ─
        var resumeFn = _symbols.ResolveContaining(retPc);
        if (!resumeFn.StartsWith("["))
        {
            // Check the ACTIVE slot first — handles self-switch (same task appears
            // in consecutive schedule slots) without creating a spurious new slot.
            foreach (var (name, _) in _taskStacks[_activeTask])
                if (name == resumeFn) return _activeTask;

            // Search all other slots.
            for (int i = 0; i < _taskStacks.Count; i++)
            {
                if (i == _activeTask) continue;
                foreach (var (name, _) in _taskStacks[i])
                    if (name == resumeFn) return i;
            }
        }

        // No existing stack owns this PC — new task or shared function.
        // Reuse the first empty non-active slot, or allocate a new one.
        for (int i = 0; i < _taskStacks.Count; i++)
            if (i != _activeTask && _taskStacks[i].Count == 0) return i;

        _taskStacks.Add(new Stack<(string Name, bool Tracked)>());
        return _taskStacks.Count - 1;
    }

    private void PushFrame(string name, uint pc)
    {
        // Untracked: ISR helpers (_-prefixed symbols) and private stdlib functions
        // (double underscore separates module from private function, e.g.
        // pymcu_time__delay_1ms_avr_16mhz = pymcu.time._delay_1ms_avr_16mhz).
        // Untracked frames are still pushed so PopFrame stays balanced, but their
        // cycles roll up to the nearest tracked ancestor in RecordSample.
        bool tracked = !name.StartsWith('_') && !name.Contains("__");
        _callStack.Push((name, tracked));
        if (DebugTrace)
            Console.Error.WriteLine($"[PUSH] pc={pc:x4} name={name} tracked={tracked} task={_activeTask} depth={_callStack.Count}");
    }

    /// <summary>
    /// Converts a PyMCU mangled symbol name to a human-readable display name.
    /// <list type="bullet">
    ///   <item>Private stdlib: <c>pymcu_time__delay_1ms</c> → <c>pymcu.time._delay_1ms</c></item>
    ///   <item>Public stdlib: <c>pymcu_time_delay_ms</c> → <c>pymcu.time.delay_ms</c> (best-effort via known module prefixes)</item>
    ///   <item>User functions: returned unchanged.</item>
    /// </list>
    /// </summary>
    private static string Demangle(string name)
    {
        // Private stdlib: split at first "__" → module part uses dots, func preserves leading _
        var dbl = name.IndexOf("__", StringComparison.Ordinal);
        if (dbl > 0)
        {
            var module = name[..dbl].Replace('_', '.');
            var func   = "_" + name[(dbl + 2)..];
            return $"{module}.{func}";
        }

        // Public stdlib (starts with "pymcu_"): best-effort split using known module prefixes.
        // The longest matching known prefix wins; the remainder is the function name.
        if (name.StartsWith("pymcu_", StringComparison.Ordinal))
        {
            foreach (var mod in KnownStdlibModules)
            {
                var prefix = mod.Replace('.', '_') + "_";
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                    return mod + "." + name[prefix.Length..];
            }
        }

        return name;
    }

    // Ordered longest-first so the most specific match wins.
    private static readonly string[] KnownStdlibModules = [
        "pymcu.hal.uart",
        "pymcu.hal.i2c",
        "pymcu.hal.spi",
        "pymcu.hal",
        "pymcu.time",
        "pymcu.io",
        "pymcu.types",
        "pymcu",
    ];

    private void PopFrame(uint pc)
    {
        if (_callStack.Count > 0)
        {
            var top = _callStack.Peek().Name;
            _callStack.Pop();
            if (DebugTrace)
                Console.Error.WriteLine($"[POP]  pc={pc:x4} popped={top} task={_activeTask} depth_after={_callStack.Count} top_after={(_callStack.Count > 0 ? _callStack.Peek().Name : "<empty>")}");
        }
        else if (DebugTrace)
        {
            Console.Error.WriteLine($"[POP!] pc={pc:x4} task={_activeTask} stack was EMPTY (underflow!)");
        }
    }

    private void RecordSample(ulong cycles)
    {
        var weight = (long)(cycles - _lastSampleCycles);
        _lastSampleCycles = cycles;

        // Build sample as root→leaf array of tracked frame indices.
        // _callStack is LIFO (top = innermost), so we reverse for root-first order.
        var stack = _callStack
            .Where(f => f.Tracked)
            .Reverse()
            .Select(f => GetOrAddFrame(Demangle(f.Name)))
            .ToArray();

        if (stack.Length > 0 && weight > 0)
        {
            _samples.Add(stack);
            _weights.Add(weight);
        }
    }

    public SpeedscopeDocument Finalize(ulong endCycles)
    {
        RecordSample(endCycles);
        return new SpeedscopeDocument(
            frames: _frames.Select(n => new SpeedscopeFrame(n)).ToList(),
            samples: _samples,
            weights: _weights,
            endValue: (long)endCycles);
    }
}

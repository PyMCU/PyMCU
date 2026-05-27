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
/// Per-task stacks: each RETI-from-ISR (hardware interrupt) saves the interrupted
/// task's stack and restores the next task's saved stack by toggling between two
/// stack slots.  This ensures pending RET instructions from the interrupted task
/// do not corrupt the resumed task's call depth.
/// </summary>
public sealed class CallStackTracker
{
    private readonly SymbolMap _symbols;
    private readonly Cpu _cpu;
    public bool DebugTrace { get; set; }

    private readonly List<string> _frames = new();
    private readonly Dictionary<string, int> _frameIndex = new();

    // Per-task call stacks.  On each RETI-from-ISR we toggle _activeTask to
    // restore the other task's saved stack (up to 2 RTOS tasks supported).
    // Each entry: (name, isTracked). Untracked frames (private helpers whose
    // mangled name starts with '_') are excluded from stack snapshots; their
    // cycles roll up to the nearest user-visible ancestor.
    private readonly Stack<(string Name, bool Tracked)>[] _taskStacks = [
        new Stack<(string Name, bool Tracked)>(),
        new Stack<(string Name, bool Tracked)>(),
    ];
    private int _activeTask = 0;
    private Stack<(string Name, bool Tracked)> _callStack => _taskStacks[_activeTask];

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

    public CallStackTracker(SymbolMap symbols, Cpu cpu, string rootFrame = "main")
    {
        _symbols = symbols;
        _cpu = cpu;
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
                if (DebugTrace)
                {
                    var spl = _cpu.Mmio.Data[0x5D];
                    var sph = _cpu.Mmio.Data[0x5E];
                    var sp = (uint)(spl | (sph << 8));
                    // RETI reads: Pc = (Data[SP+1] << 8) + Data[SP+2]
                    // so SP+1 = PCH (high byte), SP+2 = PCL (low byte)
                    var pch = _cpu.Mmio.Data[sp + 1];
                    var pcl = _cpu.Mmio.Data[sp + 2];
                    var retPc = (uint)((pch << 8) | pcl);
                    Console.Error.WriteLine($"[RETI] pc={pc:x4} task={_activeTask}→{_activeTask ^ 1} isr_popped={(_callStack.Count > 0 ? _callStack.Peek().Name : "<empty>")} SP=0x{sp:X4} [SP+1]=PCH=0x{pch:X2} [SP+2]=PCL=0x{pcl:X2} ret_pc=0x{retPc:X4}");
                }
                PopFrame(pc); // pop ISR frame (_systick etc.)
                _activeTask ^= 1;
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

    private void PushFrame(string name, uint pc)
    {
        bool tracked = !name.StartsWith('_');
        _callStack.Push((name, tracked));
        if (DebugTrace)
            Console.Error.WriteLine($"[PUSH] pc={pc:x4} name={name} task={_activeTask} depth={_callStack.Count}");
    }

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
            .Select(f => GetOrAddFrame(f.Name))
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

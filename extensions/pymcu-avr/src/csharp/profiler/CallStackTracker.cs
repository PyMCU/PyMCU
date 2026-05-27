// SPDX-License-Identifier: MIT
using AVR8Sharp.Core;

namespace PyMCU.AVR.Profiler;

/// <summary>
/// Intercepts CALL/RCALL/ICALL/RET/RETI opcodes to build a call stack and emit
/// Speedscope O/C events with exact cycle counts.
/// </summary>
public sealed class CallStackTracker
{
    private readonly SymbolMap _symbols;
    private readonly Cpu _cpu;

    private readonly List<SpeedscopeEvent> _events = new();
    private readonly List<string> _frames = new();
    private readonly Dictionary<string, int> _frameIndex = new();

    // Each entry: (name, isTracked). Untracked frames (private helpers starting
    // with '_') don't get their own speedscope events; their cycles are attributed
    // to the nearest tracked ancestor.
    private readonly Stack<(string Name, bool Tracked)> _callStack = new();

    // Hardware interrupt detection: ISR entry is NOT a CALL, it's a hardware PC jump
    // to the vector table [0x0001..0x0019]. Detect by PC discontinuity pointing to
    // that range, then decode the RJMP at the vector slot to find the ISR handler.
    private uint _prevExpectedNextPc;
    private bool _initialized;
    private const uint VectorMin = 0x0001u;
    private const uint VectorMax = 0x0019u;

    public CallStackTracker(SymbolMap symbols, Cpu cpu, string rootFrame = "main")
    {
        _symbols = symbols;
        _cpu = cpu;

        // main is entered via RJMP from reset vector, not via CALL.
        // Synthesize an open event at cycle 0 so the flamegraph has a root.
        var idx = GetOrAddFrame(rootFrame);
        _events.Add(new SpeedscopeEvent("O", idx, 0));
        _callStack.Push((rootFrame, Tracked: true));
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
        // Hardware ISR detection: if PC jumped to the vector table without a CALL,
        // a hardware interrupt fired. Decode the RJMP at that vector slot to get
        // the handler address and synthesize an Open event.
        if (_initialized && pc != _prevExpectedNextPc
            && pc >= VectorMin && pc <= VectorMax)
        {
            var vecOp = _cpu.ProgramMemory[(int)pc];
            if ((vecOp & 0xF000) == 0xC000) // RJMP at vector slot
            {
                var k = (int)(vecOp & 0x0FFF);
                if ((k & 0x0800) != 0) k -= 0x1000; // sign-extend 12-bit
                var isrAddr = (uint)((int)(pc + 1) + k);
                PushFrame(_symbols.Resolve(isrAddr), cycles);
            }
        }

        var op = _cpu.ProgramMemory[(int)pc];

        if ((op & 0xFE0E) == 0x940E)                         // CALL (abs, 2-word)
        {
            var next = _cpu.ProgramMemory[(int)(pc + 1)];
            var target = (uint)(next | ((op & 1) << 16) | ((op & 0x1F0) << 13));
            PushFrame(_symbols.Resolve(target), cycles);
        }
        else if ((op & 0xF000) == 0xD000 && op != 0xDFFF)   // RCALL (rel, excludes spin loop)
        {
            var k = (int)(op & 0x7FF) - ((op & 0x800) != 0 ? 0x800 : 0);
            var target = (uint)((int)(pc + 1) + k);
            PushFrame(_symbols.Resolve(target), cycles);
        }
        else if (op == 0x9509)                                // ICALL (indirect via Z)
        {
            var target = (uint)(_cpu.Mmio.Data[30] | (_cpu.Mmio.Data[31] << 8));
            PushFrame(_symbols.Resolve(target), cycles);
        }
        else if (op is 0x9508 or 0x9518)                     // RET / RETI
        {
            PopFrame(cycles);
        }

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

    private void PushFrame(string name, ulong cycles)
    {
        // Private helpers (names starting with '_') are implementation details;
        // suppress them as separate speedscope frames so their cycles roll up to
        // the nearest user-visible ancestor.
        bool tracked = !name.StartsWith('_');
        _callStack.Push((name, tracked));
        if (tracked)
        {
            var idx = GetOrAddFrame(name);
            _events.Add(new SpeedscopeEvent("O", idx, (long)cycles));
        }
    }

    private void PopFrame(ulong cycles)
    {
        if (_callStack.Count == 0) return;
        var (name, tracked) = _callStack.Pop();
        if (tracked)
        {
            var idx = GetOrAddFrame(name);
            _events.Add(new SpeedscopeEvent("C", idx, (long)cycles));
        }
    }

    public SpeedscopeDocument Finalize(ulong endCycles)
    {
        // Close any still-open tracked frames (main + any tail that didn't return)
        while (_callStack.Count > 0)
        {
            var (name, tracked) = _callStack.Pop();
            if (!tracked) continue;
            var idx = GetOrAddFrame(name);
            _events.Add(new SpeedscopeEvent("C", idx, (long)endCycles));
        }

        return new SpeedscopeDocument(
            frames: _frames.Select(n => new SpeedscopeFrame(n)).ToList(),
            events: _events,
            endValue: (long)endCycles);
    }
}

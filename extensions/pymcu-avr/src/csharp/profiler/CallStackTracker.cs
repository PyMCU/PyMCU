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
    private readonly Stack<string> _callStack = new();

    public CallStackTracker(SymbolMap symbols, Cpu cpu, string rootFrame = "main")
    {
        _symbols = symbols;
        _cpu = cpu;

        // main is entered via RJMP from reset vector, not via CALL.
        // Synthesize an open event at cycle 0 so the flamegraph has a root.
        var idx = GetOrAddFrame(rootFrame);
        _events.Add(new SpeedscopeEvent("O", idx, 0));
        _callStack.Push(rootFrame);
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
    }

    private void PushFrame(string name, ulong cycles)
    {
        var idx = GetOrAddFrame(name);
        _events.Add(new SpeedscopeEvent("O", idx, (long)cycles));
        _callStack.Push(name);
    }

    private void PopFrame(ulong cycles)
    {
        if (_callStack.Count == 0) return;
        var name = _callStack.Pop();
        var idx = GetOrAddFrame(name);
        _events.Add(new SpeedscopeEvent("C", idx, (long)cycles));
    }

    public SpeedscopeDocument Finalize(ulong endCycles)
    {
        // Close any still-open frames (main + any tail that didn't return)
        while (_callStack.Count > 0)
        {
            var name = _callStack.Pop();
            var idx = GetOrAddFrame(name);
            _events.Add(new SpeedscopeEvent("C", idx, (long)endCycles));
        }

        return new SpeedscopeDocument(
            frames: _frames.Select(n => new SpeedscopeFrame(n)).ToList(),
            events: _events,
            endValue: (long)endCycles);
    }
}

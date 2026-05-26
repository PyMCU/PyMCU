// SPDX-License-Identifier: MIT
using AVR8Sharp.Core.Decoders;
using Avr8Sharp.TestKit.Boards;

namespace PyMCU.AVR.DebugServer;

public enum StepMode { None, Into, Over, Instruction }

/// <summary>
/// Manages a single AVR debug session: loads firmware, steps the simulation
/// instruction-by-instruction via ProfilingDecoder, and notifies the caller
/// when execution stops (breakpoint, pause request, or step complete).
/// </summary>
public sealed class DebugSession : IDisposable
{
    private readonly ArduinoUnoSimulation _sim;
    private readonly LineMap              _lineMap;
    private readonly BreakpointSet        _breakpoints;
    private readonly ProfilingDecoder     _decoder;

    // Synchronisation between the TCP handler and the run loop.
    private readonly SemaphoreSlim _resumeSignal = new(0, 1);

    private volatile bool      _pauseRequested;
    private volatile StepMode  _stepMode = StepMode.None;
    private int                _stepBaseDepth;
    private (string file, int line)? _stepBaseLine;

    // Call-stack tracking: holds call-site word addresses (innermost last).
    private readonly Stack<uint> _callStack = new();

    /// <summary>Raised when execution stops. Frames list is innermost-first; entry 0 is the current frame.</summary>
    public event Action<string, string, int, uint, List<(string file, int line, uint pc)>>? OnStopped;
    public event Action?                    OnTerminated;

    public DebugSession(string hexContent, LineMap lineMap, BreakpointSet breakpoints)
    {
        _lineMap     = lineMap;
        _breakpoints = breakpoints;
        _sim         = new ArduinoUnoSimulation();
        _sim.WithHex(hexContent);
        _decoder = new ProfilingDecoder(OnInstruction);
    }

    private void OnInstruction(uint pc, ulong cycles)
    {
        var op = _sim.Cpu.ProgramMemory[(int)pc];
        TrackCallStack(pc, op);
    }

    private void TrackCallStack(uint pc, uint op)
    {
        if ((op & 0xFE0E) == 0x940E || (op & 0xF000) == 0xD000)
            _callStack.Push(pc);   // CALL / RCALL — push call-site word address
        else if (op is 0x9508 or 0x9518)
        {
            if (_callStack.Count > 0) _callStack.TryPop(out _);   // RET / RETI
        }
    }

    /// <summary>Returns the call stack as a list of (file, line, pc) tuples, innermost first.</summary>
    private List<(string file, int line, uint pc)> GetCallStackFrames()
    {
        var frames = new List<(string file, int line, uint pc)>();

        // Frame 0: current execution position.
        var curPos = _lineMap.GetSourcePos(_sim.Cpu.Pc);
        frames.Add(curPos.HasValue
            ? (curPos.Value.file, curPos.Value.line, _sim.Cpu.Pc)
            : ("", 0, _sim.Cpu.Pc));

        // Frames 1+: caller frames (innermost call site first).
        foreach (var callSitePc in _callStack)
        {
            var pos = _lineMap.GetSourcePos(callSitePc);
            if (pos.HasValue)
                frames.Add((pos.Value.file, pos.Value.line, callSitePc));
        }

        return frames;
    }

    /// <summary>Runs the simulation on the calling thread until terminated.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _resumeSignal.WaitAsync(ct);
                if (ct.IsCancellationRequested) break;

                _pauseRequested = false;
                RunUntilStop(ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            OnTerminated?.Invoke();
        }
    }

    private void RunUntilStop(CancellationToken ct)
    {
        // Safety limit for step modes: if we haven't stopped at a new source line
        // after this many instructions, force a pause. Prevents infinite busy-wait
        // loops (e.g. polling peripherals that are not simulated) from hanging the
        // debugger. 2M instructions ≈ 125 ms of simulated time at 16 MHz.
        const long StepSafetyLimit = 2_000_000;
        long stepInstructions = 0;

        while (!ct.IsCancellationRequested)
        {
            if (_pauseRequested)
            {
                ReportStopped("pause");
                return;
            }

            uint pc = _sim.Cpu.Pc;

            // Fire OnInstruction callback + execute one instruction.
            _decoder.Decode(_sim.Cpu);
            _sim.Cpu.Tick();

            uint nextPc = _sim.Cpu.Pc;

            // Check step-mode completion.
            if (_stepMode != StepMode.None)
            {
                // Single-instruction step: stop after executing exactly one opcode.
                if (_stepMode == StepMode.Instruction)
                {
                    _stepMode = StepMode.None;
                    ReportStopped("instruction");
                    return;
                }

                // Safety: if a step-into/over runs too long without a source-line
                // change, auto-pause so the user isn't left with a frozen debugger.
                if (++stepInstructions >= StepSafetyLimit)
                {
                    _stepMode = StepMode.None;
                    ReportStopped("pause");
                    return;
                }

                var pos = _lineMap.GetSourcePos(nextPc);
                if (pos.HasValue && pos != _stepBaseLine)
                {
                    bool shouldStop = _stepMode == StepMode.Into
                        || (_stepMode == StepMode.Over && _callStack.Count <= _stepBaseDepth);
                    if (shouldStop)
                    {
                        _stepMode = StepMode.None;
                        ReportStopped("step");
                        return;
                    }
                }
                continue;
            }

            // Breakpoint check.
            if (_breakpoints.IsBreakpoint(nextPc))
            {
                ReportStopped("breakpoint");
                return;
            }
        }
    }

    private void ReportStopped(string reason)
    {
        var frames = GetCallStackFrames();
        if (frames.Count > 0)
            OnStopped?.Invoke(reason, frames[0].file, frames[0].line, frames[0].pc, frames);
        else
            OnStopped?.Invoke(reason, "", 0, _sim.Cpu.Pc, frames);
    }

    public void Continue()
    {
        _stepMode = StepMode.None;
        TryRelease();
    }

    public void Pause()
    {
        _pauseRequested = true;
    }

    public void StepInto()
    {
        _stepMode     = StepMode.Into;
        _stepBaseLine = _lineMap.GetSourcePos(_sim.Cpu.Pc);
        TryRelease();
    }

    public void StepInstruction()
    {
        _stepMode = StepMode.Instruction;
        TryRelease();
    }

    public void StepOver()
    {
        _stepMode      = StepMode.Over;
        _stepBaseDepth = _callStack.Count;
        _stepBaseLine  = _lineMap.GetSourcePos(_sim.Cpu.Pc);
        TryRelease();
    }

    private void TryRelease()
    {
        if (_resumeSignal.CurrentCount == 0)
            _resumeSignal.Release();
    }

    public Dictionary<string, int> GetRegisters()
    {
        var cpu = _sim.Cpu;
        var regs = new Dictionary<string, int>();
        for (int i = 0; i < 32; i++)
            regs[$"R{i}"] = cpu.Mmio.Data[i];
        regs["PC"]   = (int)cpu.Pc;
        regs["SP"]   = cpu.Sp;
        regs["SREG"] = cpu.Sreg;
        return regs;
    }

    public byte[] GetMemory(int address, int length)
    {
        var data = _sim.Cpu.Mmio.Data;
        length = Math.Min(length, data.Length - address);
        if (length <= 0) return [];
        var result = new byte[length];
        Array.Copy(data, address, result, 0, length);
        return result;
    }

    /// <summary>
    /// Disassembles the loaded program and annotates each instruction with its
    /// source position (from the linemap) where available.
    /// Returns list of (wordAddr, mnemonic, sourceAnnotation) tuples.
    /// sourceAnnotation is "file:line" or "" when no mapping exists.
    /// </summary>
    public List<(uint pc, string mnemonic, string source)> GetProgramDisassembly()
    {
        var progMem = _sim.Cpu.ProgramMemory;
        // Determine effective size: scan backwards for last non-0xFFFF word.
        int wordCount = progMem.Length;
        while (wordCount > 0 && progMem[wordCount - 1] == 0xFFFF)
            wordCount--;
        if (wordCount == 0) return [];

        var instructions = AvrDisassembler.Disassemble(progMem, wordCount);
        var result = new List<(uint, string, string)>(instructions.Count);
        foreach (var instr in instructions)
        {
            var pos = _lineMap.GetSourcePos(instr.Pc);
            string src = pos.HasValue ? $"{pos.Value.file}:{pos.Value.line}" : "";
            result.Add((instr.Pc, instr.Mnemonic, src));
        }
        return result;
    }

    public void Dispose() => _resumeSignal.Dispose();
}

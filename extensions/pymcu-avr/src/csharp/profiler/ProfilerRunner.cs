// SPDX-License-Identifier: MIT
using AVR8Sharp.Core.Decoders;
using Avr8Sharp.TestKit.Boards;

namespace PyMCU.AVR.Profiler;

public static class ProfilerRunner
{
    public static SpeedscopeDocument Run(
        string hexContent,
        SymbolMap symbols,
        ulong cyclesToRun,
        string profileName = "firmware (ATmega328P @ 16MHz)")
    {
        var sim = new ArduinoUnoSimulation();
        sim.WithHex(hexContent);

        var tracker = new CallStackTracker(symbols, sim.Cpu);
        var decoder = new ProfilingDecoder(tracker.OnInstruction);
        sim.RunCyclesProfiled((long)cyclesToRun, decoder);

        return tracker.Finalize(sim.Cpu.Cycles);
    }
}

/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 */

using PyMCU.IR;

namespace PyMCU.Backend.Targets.RiscV;

// The collected heap behind list[T]. GcAlloc, GcRoot and GcUnroot are calls into the routines of
// src/gc/gc.c (emitted below, only when the program uses them); the backend supplies what they
// read: three words of state, the shadow stack and the list of GC_REF globals to seed it with.
// The heap itself is the part of RAM between the linker script's __heap_start and __heap_end.
public partial class RiscvCodeGen
{
    private readonly HashSet<string> gcNeeded = new();

    private void CompileGcAlloc(GcAlloc arg)
    {
        LoadIntoReg(arg.Size, "a0");
        Emit("li", "a1", arg.Refs ? "1" : "0");
        gcNeeded.Add("__pymcu_gc_alloc");
        Emit("call", "__pymcu_gc_alloc");
        StoreRegInto("a0", arg.Dst);
    }

    // The shadow stack holds the ADDRESS of each live GC_REF variable's slot, not its value, so
    // the collector can read the reference and rewrite it when the object moves.
    private void CompileGcRoot(GcRoot arg)
    {
        string name = ValName(arg.Var);
        if (stackLayout.TryGetValue(name, out int offset))
        {
            if (offset >= -2048 && offset <= 2047)
                Emit("addi", "a0", "s0", offset.ToString());
            else
            {
                Emit("li", "a0", offset.ToString());
                Emit("add", "a0", "a0", "s0");
            }
        }
        else
            Emit("la", "a0", name);

        gcNeeded.Add("__pymcu_gc_root_push");
        Emit("call", "__pymcu_gc_root_push");
    }

    private void CompileGcUnroot()
    {
        gcNeeded.Add("__pymcu_gc_root_pop");
        Emit("call", "__pymcu_gc_root_pop");
    }

    // Called after the functions are compiled, once it is known which routines they reached.
    private void EmitGcRuntime(ProgramIR program)
    {
        foreach (var func in program.Functions)
            foreach (var instr in func.Body)
                if (instr is Call { FunctionName: "gc_list_fixup" })
                    gcNeeded.Add("gc_list_fixup");

        if (gcNeeded.Count == 0) return;

        EmitCRuntime(gcNeeded, GcRoutines);

        var gcGlobals = program.Globals.Where(g => g.Type == DataType.GC_REF).Select(g => g.Name).ToList();
        int slots = program.Functions.SelectMany(f => f.Body).Count(i => i is GcRoot) + gcGlobals.Count;

        EmitRaw(".section .bss");
        EmitRaw(".align 2");
        EmitLabel("__pymcu_gc_state");
        EmitRaw("\t.zero\t12");
        EmitLabel("__pymcu_gc_ss_base");
        EmitRaw($"\t.zero\t{Math.Max(slots, 1) * 4}");

        EmitRaw(".section .rodata");
        EmitRaw(".align 2");
        EmitLabel("__pymcu_gc_global_roots");
        EmitRaw(gcGlobals.Count == 0 ? "\t.word\t0" : "\t.word\t" + string.Join(", ", gcGlobals));
        EmitLabel("__pymcu_gc_global_count");
        EmitRaw($"\t.word\t{gcGlobals.Count}");
        EmitRaw(".section .text");
    }

    // Whether v points into a collected object: such a pointer plus an offset is where a list's
    // elements sit, two bytes in, so a word element is not on a word boundary.
    private static bool IsHeapRef(Val v) => TypeOf(v) == DataType.GC_REF;
}

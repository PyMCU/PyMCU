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

namespace PyMCU.Backend.Targets.RiscV;

// Reset path, data sections and the integer helpers RV32EC needs.
// The QingKe cores start at flash address 0 with no bootloader, so the backend
// emits the whole runtime itself: there is no crt0 to link against (the GCC
// multilib set ships no rv32ec libgcc either, hence the mul/div helpers below).
public partial class RiscvCodeGen
{
    // Default when the chip file declares no RAM size: the CH32V003's 2 KB.
    private const int DefaultRamSize = 2048;
    private const int RamBase = 0x20000000;

    // System vectors (reset, NMI, fault, ...) followed by the external IRQ slots.
    private const int SystemVectorCount = 16;
    private const int ExternalVectorCount = 16;

    private bool needsMul;
    private bool needsDiv;
    private bool needsFloorDiv;
    private bool needsMod;

    // Read-only byte tables (const[uint8[N]] and interned strings) destined for
    // .rodata, and the SRAM footprint of every array the program indexes.
    private readonly Dictionary<string, List<int>> flashTables = new();
    private readonly Dictionary<string, int> arrayBytes = new();

    // Label under which a flash table is emitted. Matches the convention the IR
    // already assumes for ArrayLoadFlash and FlashStrAddr.
    private static string FlashSymbol(string name) => "__flash_" + name;

    // Arrays are addressed by symbol, so each one needs a reservation big enough
    // for every indexed access the program makes.
    private void NoteArray(string name, int count, DataType elem)
    {
        int bytes = Math.Max(count, 1) * elem.SizeOf();
        if (!arrayBytes.TryGetValue(name, out int have) || bytes > have)
            arrayBytes[name] = bytes;
    }

    private int StackTop => RamBase + (cfg.RamSize > 0 ? cfg.RamSize : DefaultRamSize);

    private static bool HasMain(ProgramIR program)
    {
        foreach (var f in program.Functions)
            if (f.Name == "main") return true;
        return false;
    }

    private void ScanForRuntimeHelpers(ProgramIR program)
    {
        needsMul = needsDiv = needsFloorDiv = needsMod = false;

        foreach (var func in program.Functions)
        foreach (var instr in func.Body)
        {
            var op = instr switch
            {
                Binary b => (PyMCU.IR.BinaryOp?)b.Op,
                AugAssign a => a.Op,
                _ => null,
            };

            switch (op)
            {
                case PyMCU.IR.BinaryOp.Mul: needsMul = true; break;
                case PyMCU.IR.BinaryOp.Div: needsDiv = true; break;
                case PyMCU.IR.BinaryOp.FloorDiv: needsFloorDiv = true; break;
                case PyMCU.IR.BinaryOp.Mod: needsMod = true; break;
            }
        }
    }

    // ─── Reset path ──────────────────────────────────────────────────────────

    private void EmitStartup(ProgramIR program)
    {
        // Without main there is no program to start: the IR is a library fragment
        // (or a unit-test snippet) and gets no reset path.
        if (!HasMain(program)) return;

        EmitVectorTable(program);

        EmitRaw(".section .init, \"ax\", @progbits");
        EmitRaw(".align 2");
        EmitRaw(".globl _start");
        EmitLabel("_start");

        Emit("li", "sp", $"0x{StackTop:X8}");

        // Vectored, absolute-address trap entry (mtvec[1:0] = 0b11 on QingKe).
        Emit("la", "t0", "_vector_base");
        Emit("ori", "t0", "t0", "3");
        Emit("csrw", "mtvec", "t0");

        EmitComment("copy .data from flash to RAM");
        Emit("la", "t0", "_sidata");
        Emit("la", "t1", "_sdata");
        Emit("la", "t2", "_edata");
        EmitLabel("_copy_data_loop");
        Emit("bgeu", "t1", "t2", "_copy_data_done");
        Emit("lw", "a0", "0(t0)");
        Emit("sw", "a0", "0(t1)");
        Emit("addi", "t0", "t0", "4");
        Emit("addi", "t1", "t1", "4");
        Emit("j", "_copy_data_loop");
        EmitLabel("_copy_data_done");

        EmitComment("zero .bss");
        Emit("la", "t0", "_sbss");
        Emit("la", "t1", "_ebss");
        EmitLabel("_zero_bss_loop");
        Emit("bgeu", "t0", "t1", "_zero_bss_done");
        Emit("sw", "zero", "0(t0)");
        Emit("addi", "t0", "t0", "4");
        Emit("j", "_zero_bss_loop");
        EmitLabel("_zero_bss_done");

        // Enter main through mret so MPIE is promoted to MIE, matching the
        // privilege setup WCH's own startup performs.
        EmitComment("enter main in machine mode with interrupts enabled");
        Emit("li", "t0", "0x1888");
        Emit("csrw", "mstatus", "t0");
        Emit("la", "t0", "main");
        Emit("csrw", "mepc", "t0");
        Emit("mret");

        EmitRaw(".globl _default_handler");
        EmitLabel("_default_handler");
        Emit("j", "_default_handler");
    }

    // The QingKe trap table: one word per vector, indexed by vector number.
    // Slot 0 is the reset entry and holds a jump rather than an address, which is
    // why @interrupt(vector=0) is rejected. Everything the program does not claim
    // parks in _default_handler.
    private void EmitVectorTable(ProgramIR program)
    {
        int slots = SystemVectorCount + ExternalVectorCount;
        foreach (var func in program.Functions)
            if (func.IsInterrupt && func.InterruptVector >= slots)
                slots = func.InterruptVector + 1;

        var handlers = new string[slots];
        foreach (var func in program.Functions)
        {
            if (!func.IsInterrupt) continue;

            if (func.InterruptVector <= 0)
                throw new NotSupportedException(
                    $"RISC-V backend: ISR '{func.Name}' has no interrupt vector. " +
                    "Vector 0 is the reset entry on QingKe cores, so an ISR must " +
                    "declare a non-zero vector (e.g. @interrupt(vector=12) for SysTick).");

            if (handlers[func.InterruptVector] is not null)
                throw new NotSupportedException(
                    $"RISC-V backend: vector {func.InterruptVector} is claimed by both " +
                    $"'{handlers[func.InterruptVector]}' and '{func.Name}'.");

            handlers[func.InterruptVector] = func.Name;
        }

        EmitRaw(".section .vector, \"ax\", @progbits");
        EmitRaw(".align 2");
        EmitRaw(".globl _vector_base");
        EmitLabel("_vector_base");
        // The table is indexed by word, so compression must not shrink the entries.
        EmitRaw(".option push");
        EmitRaw(".option norvc");
        Emit("j", "_start");
        for (int i = 1; i < slots; i++)
            EmitRaw($"\t.word\t{handlers[i] ?? "_default_handler"}");
        EmitRaw(".option pop");
    }

    // ─── Data sections ───────────────────────────────────────────────────────

    // Module-level variables. Every access the codegen emits is a full word load
    // or store, so each entry gets a word-aligned 4-byte cell regardless of its
    // declared width. Everything starts zeroed, which matches the frontend's
    // constant initialisers being replayed as stores at the top of main.
    private void EmitDataSections(ProgramIR program)
    {
        EmitFlashTables();

        if (program.Globals.Count == 0 && arrayBytes.Count == 0) return;

        EmitRaw(".section .bss");
        EmitRaw(".align 2");

        foreach (var g in program.Globals)
        {
            EmitRaw($".globl {g.Name}");
            EmitLabel(g.Name);
            EmitRaw("\t.zero\t4");
        }

        foreach (var (name, bytes) in arrayBytes)
        {
            EmitRaw($".globl {name}");
            EmitLabel(name);
            EmitRaw($"\t.zero\t{bytes}");
            EmitRaw(".align 2");
        }
    }

    // Constant byte tables live in flash and are read with ordinary loads,
    // because RISC-V addresses code and data in one space.
    private void EmitFlashTables()
    {
        if (flashTables.Count == 0) return;

        EmitRaw(".section .rodata");
        EmitRaw(".align 2");

        foreach (var (name, bytes) in flashTables)
        {
            string symbol = FlashSymbol(name);
            EmitRaw($".globl {symbol}");
            EmitLabel(symbol);
            EmitRaw(bytes.Count == 0
                ? "\t.byte\t0"
                : "\t.byte\t" + string.Join(", ", bytes));
            EmitRaw(".align 2");
        }
    }

    // ─── Integer helpers ─────────────────────────────────────────────────────

    private void EmitRuntimeHelpers()
    {
        if (!needsMul && !needsDiv && !needsFloorDiv && !needsMod) return;

        EmitRaw(".section .text");
        EmitRaw(".align 2");

        if (needsMul) EmitMulHelper();
        if (needsDiv) EmitDivHelper();
        if (needsFloorDiv) EmitFloorDivHelper();
        if (needsMod) EmitModHelper();
    }

    // a0 * a1 -> a0, by shift-and-add. Works for signed and unsigned operands
    // alike because the low 32 bits of the product are identical either way.
    private void EmitMulHelper()
    {
        EmitRaw(".globl __mulsi3");
        EmitLabel("__mulsi3");
        Emit("li", "a2", "0");
        EmitLabel("__mulsi3_loop");
        Emit("beqz", "a1", "__mulsi3_done");
        Emit("andi", "a3", "a1", "1");
        Emit("beqz", "a3", "__mulsi3_skip");
        Emit("add", "a2", "a2", "a0");
        EmitLabel("__mulsi3_skip");
        Emit("slli", "a0", "a0", "1");
        Emit("srli", "a1", "a1", "1");
        Emit("j", "__mulsi3_loop");
        EmitLabel("__mulsi3_done");
        Emit("mv", "a0", "a2");
        Emit("ret");
    }

    // Restoring division of the unsigned values in a0 (dividend) and a1 (divisor).
    // Leaves the quotient in a2 and the remainder in a3; clobbers a4 and a5.
    // A zero divisor yields an all-ones quotient, mirroring what libgcc does
    // rather than trapping.
    private void EmitUDivModCore(string prefix)
    {
        Emit("li", "a2", "0");
        Emit("li", "a3", "0");
        Emit("li", "a4", "32");
        EmitLabel($"{prefix}_loop");
        Emit("slli", "a3", "a3", "1");
        Emit("srli", "a5", "a0", "31");
        Emit("or", "a3", "a3", "a5");
        Emit("slli", "a0", "a0", "1");
        Emit("slli", "a2", "a2", "1");
        Emit("bltu", "a3", "a1", $"{prefix}_skip");
        Emit("sub", "a3", "a3", "a1");
        Emit("ori", "a2", "a2", "1");
        EmitLabel($"{prefix}_skip");
        Emit("addi", "a4", "a4", "-1");
        Emit("bnez", "a4", $"{prefix}_loop");
    }

    // a0 / a1 -> a0, truncating toward zero.
    private void EmitDivHelper()
    {
        EmitRaw(".globl __divsi3");
        EmitLabel("__divsi3");
        Emit("li", "t0", "0");
        Emit("bgez", "a0", "__divsi3_num_ok");
        Emit("neg", "a0", "a0");
        Emit("xori", "t0", "t0", "1");
        EmitLabel("__divsi3_num_ok");
        Emit("bgez", "a1", "__divsi3_den_ok");
        Emit("neg", "a1", "a1");
        Emit("xori", "t0", "t0", "1");
        EmitLabel("__divsi3_den_ok");
        EmitUDivModCore("__divsi3");
        Emit("mv", "a0", "a2");
        Emit("beqz", "t0", "__divsi3_done");
        Emit("neg", "a0", "a0");
        EmitLabel("__divsi3_done");
        Emit("ret");
    }

    // a0 // a1 -> a0, rounding toward negative infinity (Python semantics).
    // Truncation and flooring only differ when the signs disagree and the
    // division leaves a remainder, in which case the quotient is one lower.
    private void EmitFloorDivHelper()
    {
        EmitRaw(".globl __floordivsi3");
        EmitLabel("__floordivsi3");
        Emit("li", "t0", "0");
        Emit("bgez", "a0", "__floordivsi3_num_ok");
        Emit("neg", "a0", "a0");
        Emit("xori", "t0", "t0", "1");
        EmitLabel("__floordivsi3_num_ok");
        Emit("bgez", "a1", "__floordivsi3_den_ok");
        Emit("neg", "a1", "a1");
        Emit("xori", "t0", "t0", "1");
        EmitLabel("__floordivsi3_den_ok");
        EmitUDivModCore("__floordivsi3");
        Emit("beqz", "t0", "__floordivsi3_done");
        Emit("neg", "a2", "a2");
        Emit("beqz", "a3", "__floordivsi3_done");
        Emit("addi", "a2", "a2", "-1");
        EmitLabel("__floordivsi3_done");
        Emit("mv", "a0", "a2");
        Emit("ret");
    }

    // a0 % a1 -> a0. The remainder takes the sign of the dividend.
    private void EmitModHelper()
    {
        EmitRaw(".globl __modsi3");
        EmitLabel("__modsi3");
        Emit("li", "t0", "0");
        Emit("bgez", "a0", "__modsi3_num_ok");
        Emit("neg", "a0", "a0");
        Emit("li", "t0", "1");
        EmitLabel("__modsi3_num_ok");
        Emit("bgez", "a1", "__modsi3_den_ok");
        Emit("neg", "a1", "a1");
        EmitLabel("__modsi3_den_ok");
        EmitUDivModCore("__modsi3");
        Emit("mv", "a0", "a3");
        Emit("beqz", "t0", "__modsi3_done");
        Emit("neg", "a0", "a0");
        EmitLabel("__modsi3_done");
        Emit("ret");
    }
}

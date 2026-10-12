/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 */

using System.Text;
using PyMCU.IR;

namespace PyMCU.Backend.Targets.RiscV;

// The unhandled-exception runtime. The error model's counterpart of AVR's T flag is s1: a
// BranchOnError jumps to the label below with the pending code still in s1, and a `raise` that
// nothing catches in main jumps there too. If USART1 is enabled the routine prints
// "E:<Name>" and then either calls __pymcu_exn_tail (which appends ": <message>" and the line
// end, when the program records messages) or prints the line end itself; then it parks.
public partial class RiscvCodeGen
{
    public const string UnhandledExnLabel = "__pymcu_unhandled_exn";

    // USART1 on every WCH part this backend targets (CH32V003 reference manual, "USART":
    // STATR 0x00 with TXE at bit 7, DATAR 0x04, CTLR1 0x0C with UE at bit 13).
    private const uint Usart1Base = 0x4001_3800;

    private bool needsUnhandledExn;

    private static bool TargetsUnhandledExn(Function f) =>
        f.Body.Any(i => i is BranchOnError { ErrorLabel: UnhandledExnLabel }
                        or Call { FunctionName: UnhandledExnLabel })
        // A raise that nothing catches in main has nowhere to propagate to.
        || (f.Name == "main" && f.Body.Any(i => i is SignalError { CatchLabel: null }));

    private void EmitUnhandledExnRuntime(ProgramIR program)
    {
        if (!program.Functions.Any(TargetsUnhandledExn)) return;

        var codes = new SortedSet<int>();
        foreach (var f in program.Functions)
            foreach (var se in f.Body.OfType<SignalError>())
                if (se.Code is Constant { Value: not 0 } c) codes.Add(c.Value);
        bool hasTail = program.Functions.Any(f => f.Name == "__pymcu_exn_tail");

        EmitRaw(".section .text");
        EmitRaw(".align 2");
        EmitLabel(UnhandledExnLabel);
        // Is USART1 on? (Same test the AVR runtime makes on TXEN.)
        Emit("li", "t2", $"0x{Usart1Base + 0x0C:X8}");
        Emit("lw", "t0", "0(t2)");
        Emit("li", "t1", "0x2000");
        Emit("and", "t0", "t0", "t1");
        if (cfg.UartOwnedByProgram)
        {
            // The program configured the UART itself: if it left it off, say nothing.
            Emit("beqz", "t0", "__pymcu_exn_halt");
        }
        else
        {
            // Nothing in the program set the UART up, and a program that raises and never prints
            // would otherwise stop without a word. Turn the transmitter on here, the way the
            // stdout preamble would have: clock IOPD and USART1, PD5 as alternate-function
            // push-pull, BRR for the frequency and `stdout_baud`, UE and TE (reference manual
            // chapters RCC, GPIO and USART).
            Emit("bnez", "t0", "__pymcu_exn_tx_ready");
            Emit("li", "t2", "0x40021018");               // RCC_APB2PCENR
            Emit("lw", "t0", "0(t2)");
            Emit("li", "t1", "0x4020");                   // IOPDEN | USART1EN
            Emit("or", "t0", "t0", "t1");
            Emit("sw", "t0", "0(t2)");
            Emit("li", "t2", "0x40011400");               // GPIOD_CFGLR
            Emit("lw", "t0", "0(t2)");
            Emit("li", "t1", (~(0xF << 20)).ToString());
            Emit("and", "t0", "t0", "t1");
            Emit("li", "t1", (0xB << 20).ToString());
            Emit("or", "t0", "t0", "t1");
            Emit("sw", "t0", "0(t2)");
            long baud = Math.Max(1, cfg.StdoutBaud);
            Emit("li", "t0", (((long)cfg.Frequency + baud / 2) / baud).ToString());
            Emit("li", "t2", $"0x{Usart1Base + 0x08:X8}");   // BRR
            Emit("sw", "t0", "0(t2)");
            Emit("li", "t0", "8200");                     // UE | TE
            Emit("li", "t2", $"0x{Usart1Base + 0x0C:X8}");   // CTLR1
            Emit("sw", "t0", "0(t2)");
            EmitLabel("__pymcu_exn_tx_ready");
        }
        Emit("li", "a0", "69");                      // 'E'
        Emit("call", "__pymcu_exn_putc");
        Emit("li", "a0", "58");                      // ':'
        Emit("call", "__pymcu_exn_putc");

        foreach (int code in codes)
        {
            Emit("li", "t0", code.ToString());
            Emit("beq", "s1", "t0", $"__pymcu_exn_name_{code}");
        }
        Emit("j", "__pymcu_exn_name_unknown");

        foreach (int code in codes)
        {
            EmitLabel($"__pymcu_exn_name_{code}");
            Emit("la", "a1", $"__pymcu_exn_str_{code}");
            Emit("j", "__pymcu_exn_print");
        }
        EmitLabel("__pymcu_exn_name_unknown");
        Emit("la", "a1", "__pymcu_exn_str_unknown");

        EmitLabel("__pymcu_exn_print");
        EmitLabel("__pymcu_exn_print_loop");
        Emit("lbu", "a0", "0(a1)");
        Emit("beqz", "a0", "__pymcu_exn_print_done");
        Emit("mv", "s0", "a1");                       // the frame pointer is dead from here on
        Emit("call", "__pymcu_exn_putc");
        Emit("addi", "a1", "s0", "1");
        Emit("j", "__pymcu_exn_print_loop");
        EmitLabel("__pymcu_exn_print_done");
        if (hasTail)
        {
            Emit("call", "__pymcu_exn_tail");
        }
        else
        {
            Emit("li", "a0", "13");
            Emit("call", "__pymcu_exn_putc");
            Emit("li", "a0", "10");
            Emit("call", "__pymcu_exn_putc");
        }
        EmitLabel("__pymcu_exn_halt");
        Emit("j", "__pymcu_exn_halt");

        // a0 = byte. Polls TXE, then writes DATAR. Touches only t0, t1, t2.
        EmitLabel("__pymcu_exn_putc");
        Emit("li", "t2", $"0x{Usart1Base:X8}");
        EmitLabel("__pymcu_exn_putc_wait");
        Emit("lw", "t0", "0(t2)");
        Emit("andi", "t0", "t0", "128");
        Emit("beqz", "t0", "__pymcu_exn_putc_wait");
        Emit("sw", "a0", "4(t2)");
        Emit("ret");

        EmitRaw(".section .rodata");
        foreach (int code in codes)
        {
            string name = PyMCU.Common.BuiltinExceptionNames.TryGetName(code, out var n) ? n : $"Exception{code}";
            EmitLabel($"__pymcu_exn_str_{code}");
            EmitRaw($".asciz \"{name}\"");
        }
        EmitLabel("__pymcu_exn_str_unknown");
        EmitRaw(".asciz \"Exception\"");
        EmitRaw(".section .text");
    }
}

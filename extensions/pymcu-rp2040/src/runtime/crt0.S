/* SPDX-License-Identifier: MIT
 * PyMCU RP2040 C runtime startup (crt0) + Cortex-M0+ vector table.
 *
 * Layout (see rp2040.ld):
 *   flash + 0x000 : boot2 stub (boot2.S)
 *   flash + 0x100 : this vector table (.vectors) -- found by the emulator
 *   flash + ...   : .text / .rodata, then .data initialiser image
 *
 * Reset_Handler initialises the C environment (copy .data, zero .bss), sets up
 * nothing else (single core, no interrupts in the MVP), and calls main().
 */

    .syntax unified
    .cpu cortex-m0plus
    .thumb

/* ---- Cortex-M0+ vector table (must live at flash+0x100) ---- */
    .section .vectors, "a", %progbits
    .align 2
    .global __vectors
__vectors:
    .word _stack_top            /* 0x00  Initial Stack Pointer            */
    .word Reset_Handler         /* 0x04  Reset (thumb bit set by .thumb_func) */
    .word Default_Handler       /* 0x08  NMI                              */
    .word Default_Handler       /* 0x0C  HardFault                        */
    .word Default_Handler       /* 0x10  MemManage (reserved on M0+)      */
    .word Default_Handler       /* 0x14  BusFault  (reserved on M0+)      */
    .word Default_Handler       /* 0x18  UsageFault(reserved on M0+)      */
    .word 0                     /* 0x1C  reserved                         */
    .word 0                     /* 0x20  reserved                         */
    .word 0                     /* 0x24  reserved                         */
    .word 0                     /* 0x28  reserved                         */
    .word Default_Handler       /* 0x2C  SVCall                           */
    .word Default_Handler       /* 0x30  DebugMon (reserved on M0+)       */
    .word 0                     /* 0x34  reserved                         */
    .word Default_Handler       /* 0x38  PendSV                           */
    .word Default_Handler       /* 0x3C  SysTick                          */
    .size __vectors, . - __vectors

/* ---- Reset handler ---- */
    .section .text._reset, "ax", %progbits
    .align 2
    .global Reset_Handler
    .type Reset_Handler, %function
    .thumb_func
Reset_Handler:
    /* Copy .data from its flash load address (_sidata) to SRAM (_sdata.._edata). */
    ldr  r0, =_sdata
    ldr  r1, =_edata
    ldr  r2, =_sidata
.Lcopy:
    cmp  r0, r1
    bhs  .Lcopy_done
    ldr  r3, [r2]
    str  r3, [r0]
    adds r0, r0, #4
    adds r2, r2, #4
    b    .Lcopy
.Lcopy_done:

    /* Zero .bss (_sbss.._ebss). */
    ldr  r0, =_sbss
    ldr  r1, =_ebss
    movs r3, #0
.Lbss:
    cmp  r0, r1
    bhs  .Lbss_done
    str  r3, [r0]
    adds r0, r0, #4
    b    .Lbss
.Lbss_done:

    bl   main
.Lhang:
    b    .Lhang
    .ltorg
    .size Reset_Handler, . - Reset_Handler

/* ---- Default handler: trap (spin) ---- */
    .section .text._default, "ax", %progbits
    .global Default_Handler
    .type Default_Handler, %function
    .thumb_func
Default_Handler:
    b    Default_Handler
    .size Default_Handler, . - Default_Handler

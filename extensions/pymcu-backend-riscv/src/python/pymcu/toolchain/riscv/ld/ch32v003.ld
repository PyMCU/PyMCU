/*
 * -----------------------------------------------------------------------------
 * PyMCU RISC-V backend — linker script for the WCH CH32V003 (QingKe V2A, RV32EC)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 *
 * Pairs with the reset path pymcuc emits (see RISCVRuntime.cs): the startup code
 * reads _sidata/_sdata/_edata to copy .data into RAM and _sbss/_ebss to zero .bss.
 *
 * Usage:
 *   riscv32-unknown-elf-as  -mabi=ilp32e firmware.s -o firmware.o
 *   riscv32-unknown-elf-ld  -m elf32lriscv -T ch32v003.ld firmware.o -o firmware.elf
 */

OUTPUT_ARCH("riscv")
ENTRY(_start)

MEMORY
{
    FLASH (rx)  : ORIGIN = 0x00000000, LENGTH = 16K
    RAM   (rwx) : ORIGIN = 0x20000000, LENGTH = 2K
}

SECTIONS
{
    /* The reset vector must sit at address 0: the core fetches its first
       instruction there, and mtvec points the trap table at the same base. */
    .vector :
    {
        *(.vector)
        . = ALIGN(4);
    } > FLASH

    .init :
    {
        *(.init)
        . = ALIGN(4);
    } > FLASH

    .text :
    {
        *(.text)
        *(.text.*)
        . = ALIGN(4);
    } > FLASH

    .rodata :
    {
        *(.rodata)
        *(.rodata.*)
        . = ALIGN(4);
    } > FLASH

    /* Initialised data is stored in flash and copied to RAM by _start. */
    _sidata = LOADADDR(.data);

    .data :
    {
        . = ALIGN(4);
        _sdata = .;
        *(.data)
        *(.data.*)
        . = ALIGN(4);
        _edata = .;
    } > RAM AT > FLASH

    .bss :
    {
        . = ALIGN(4);
        _sbss = .;
        *(.bss)
        *(.bss.*)
        *(COMMON)
        . = ALIGN(4);
        _ebss = .;
    } > RAM

    _end = .;

    /DISCARD/ : { *(.comment) }
}

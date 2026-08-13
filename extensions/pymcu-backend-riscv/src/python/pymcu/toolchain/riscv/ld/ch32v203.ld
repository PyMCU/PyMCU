/*
 * -----------------------------------------------------------------------------
 * PyMCU RISC-V backend — linker script for the WCH CH32V203 (QingKe V4B, RV32IMAC)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 *
 * Same layout as the CH32V003 script, with the V203's larger memories: 64K of
 * flash and 20K of RAM. The startup pymcuc emits reads _sidata/_sdata/_edata to
 * copy .data into RAM and _sbss/_ebss to zero .bss.
 *
 * Usage:
 *   riscv32-unknown-elf-as  -mabi=ilp32 firmware.s -o firmware.o
 *   riscv32-unknown-elf-ld  -m elf32lriscv -T ch32v203.ld firmware.o -o firmware.elf
 */

OUTPUT_ARCH("riscv")
ENTRY(_start)

MEMORY
{
    FLASH (rx)  : ORIGIN = 0x00000000, LENGTH = 64K
    RAM   (rwx) : ORIGIN = 0x20000000, LENGTH = 20K
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

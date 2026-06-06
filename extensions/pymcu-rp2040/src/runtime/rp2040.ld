/* SPDX-License-Identifier: MIT
 * PyMCU RP2040 linker script.
 *
 * Flash image layout (consumed by RP2040Sharp PicoSimulation.LoadFlash):
 *   0x10000000  .boot2     256-byte stage-2 stub
 *   0x10000100  .vectors   Cortex-M0+ vector table (emulator scans here first)
 *   0x10000140  .text/.rodata, then the .data initialiser image
 *   0x20000000  .data (copied at reset), .bss, stack grows down from _stack_top
 */

MEMORY
{
    FLASH (rx)  : ORIGIN = 0x10000000, LENGTH = 2048K
    RAM   (rwx) : ORIGIN = 0x20000000, LENGTH = 264K
}

/* Full-descending stack starts at the top of SRAM. */
_stack_top = ORIGIN(RAM) + LENGTH(RAM);

ENTRY(_boot2)

SECTIONS
{
    /* Stage-2 boot loader at the very start of flash. */
    .boot2 ORIGIN(FLASH) :
    {
        KEEP(*(.boot2))
    } > FLASH

    /* Vector table must sit at flash + 0x100 (explicit LMA/VMA). */
    .vectors ORIGIN(FLASH) + 0x100 :
    {
        KEEP(*(.vectors))
    } > FLASH

    .text :
    {
        *(.text._reset)
        *(.text*)
        *(.rodata*)
        . = ALIGN(4);
        _etext = .;
    } > FLASH

    /* .data: virtual in RAM, load image (LMA) in flash after .text. */
    _sidata = LOADADDR(.data);
    .data :
    {
        . = ALIGN(4);
        _sdata = .;
        *(.data*)
        . = ALIGN(4);
        _edata = .;
    } > RAM AT> FLASH

    .bss (NOLOAD) :
    {
        . = ALIGN(4);
        _sbss = .;
        *(.bss*)
        *(COMMON)
        . = ALIGN(4);
        _ebss = .;
    } > RAM

    /DISCARD/ :
    {
        *(.ARM.exidx*)
        *(.ARM.extab*)
        *(.comment)
    }
}

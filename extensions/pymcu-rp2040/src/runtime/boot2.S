/* SPDX-License-Identifier: MIT
 * PyMCU RP2040 stage-2 boot loader (minimal).
 *
 * On the RP2040, execution begins at the first 256 bytes of flash (0x10000000)
 * after the on-chip bootrom copies them into SRAM. On real hardware boot2 must
 * configure the QSPI/SSI interface for execute-in-place (XIP) and ends with a
 * CRC32 over the first 252 bytes.
 *
 * The RP2040Sharp emulator starts executing directly at 0x10000000 with XIP
 * already functional, the bootrom flash-setup functions patched to `bx lr`, and
 * SP/VTOR taken from the firmware vector table at flash+0x100. It does NOT check
 * the boot2 CRC. So for emulator/integration use this stub only needs to hand
 * control to the firmware reset handler.
 *
 * NOTE: a full hardware-correct boot2 (SSI setup + CRC32 trailer) is a follow-up;
 * this build targets the emulator.
 */

    .syntax unified
    .cpu cortex-m0plus
    .thumb

    .section .boot2, "ax", %progbits
    .global _boot2
    .type _boot2, %function
    .thumb_func
_boot2:
    ldr r0, =Reset_Handler
    bx  r0
    .ltorg
    .size _boot2, . - _boot2

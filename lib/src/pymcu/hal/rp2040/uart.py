# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# RP2040 UART HAL -- pymcu.hal.rp2040.uart (UART0, ARM PL011)
#
# Zero-cost: every method is @inline and lowers to volatile MMIO accesses. The
# baud divisors are folded at compile time from the (const) baud argument. The
# default GPIO pins are GP0 (TX) / GP1 (RX), routed to the UART function.

from pymcu.chips.rp2040 import (
    UART0_DR, UART0_FR, UART0_IBRD, UART0_FBRD, UART0_LCR_H, UART0_CR,
    RESETS_RESET_CLR, RESETS_RESET_DONE,
    RESET_UART0, RESET_IO_BANK0, RESET_PADS_BANK0,
    IO_BANK0_BASE, GPIO_FUNC_UART,
    UART_FR_TXFF, UART_FR_RXFE,
)
from pymcu.types import ptr, uint8, uint32, const, inline

# Peripheral clock assumed at the pico-sdk default of 125 MHz. (A future clocks
# HAL will make this configurable; for now clk_peri == clk_sys == 125 MHz.)
_CLK_PERI = 125000000


class UART:
    """Hardware UART0 (PL011), zero-cost abstraction."""

    def __init__(self, baud: const = 115200, tx: const = 0, rx: const = 1):
        # Bring UART0, IO_BANK0 and PADS_BANK0 out of reset; wait for all three.
        reset_mask: uint32 = (1 << RESET_UART0) | (1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0)
        RESETS_RESET_CLR.value = reset_mask
        while (RESETS_RESET_DONE.value & reset_mask) != reset_mask:
            pass

        # Baud rate: 64 * divisor = (clk_peri * 4) / baud.
        UART0_IBRD.value = _CLK_PERI // (16 * baud)
        UART0_FBRD.value = (((_CLK_PERI * 4) // baud) & 0x3F)

        # 8 data bits (WLEN=3 at bits 5:6), FIFOs enabled (FEN=bit4).
        UART0_LCR_H.value = (3 << 5) | (1 << 4)
        # Enable UART (bit0), TX (bit8) and RX (bit9).
        UART0_CR.value = (1 << 0) | (1 << 8) | (1 << 9)

        # Route the TX/RX pins to the UART function.
        tx_ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * tx + 4)
        tx_ctrl.value = GPIO_FUNC_UART
        rx_ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * rx + 4)
        rx_ctrl.value = GPIO_FUNC_UART

    @inline
    def write(self, data: uint8):
        # Wait while the TX FIFO is full.
        while (UART0_FR.value >> UART_FR_TXFF) & 1:
            pass
        UART0_DR.value = data

    @inline
    def read(self) -> uint8:
        # Wait while the RX FIFO is empty.
        while (UART0_FR.value >> UART_FR_RXFE) & 1:
            pass
        return UART0_DR.value & 0xFF

    @inline
    def read_blocking(self) -> uint8:
        return self.read()

    @inline
    def write_str(self, s: const[str]):
        for ch in s:
            self.write(ch)

    @inline
    def println(self, s: const[str]):
        self.write_str(s)
        self.write(10)

    @inline
    def write_hex(self, byte: uint8):
        hi: uint8 = (byte >> 4) & 0x0F
        lo: uint8 = byte & 0x0F
        if hi < 10:
            self.write(hi + 48)
        else:
            self.write(hi - 10 + 65)
        if lo < 10:
            self.write(lo + 48)
        else:
            self.write(lo - 10 + 65)

    @inline
    def available(self) -> uint8:
        # Returns 1 when the RX FIFO holds at least one byte (RXFE = 0).
        if (UART0_FR.value >> UART_FR_RXFE) & 1:
            return 0
        return 1

    @inline
    def read_nb(self) -> uint8:
        # Non-blocking: returns the byte if ready, 0 if the FIFO is empty.
        if (UART0_FR.value >> UART_FR_RXFE) & 1:
            return 0
        return UART0_DR.value & 0xFF

    @inline
    def read_line(self, buf: bytearray, max_len: uint8) -> uint8:
        # Reads bytes into buf until '\n' (10) or max_len-1 bytes received.
        # CR ('\r' = 13) is discarded silently.
        # Appends a null terminator; returns the byte count (excluding newline).
        count: uint8 = 0
        while count < max_len - 1:
            b: uint8 = self.read()
            if b == 10:
                break
            if b != 13:
                buf[count] = b
                count = count + 1
        if count < max_len:
            buf[count] = 0
        return count

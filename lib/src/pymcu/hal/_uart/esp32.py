# SPDX-License-Identifier: MIT
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
# Licensed under the MIT License. See LICENSE for details.
#
# hal/_uart/esp32.py -- UART0 HAL for ESP32
#
# ESP32 UART0 register map (base = 0x3FF40000):
#   UART_FIFO_REG    = 0x3FF40000  -- TX FIFO / RX FIFO (byte at bits [7:0])
#   UART_STATUS_REG  = 0x3FF4001C  -- bits [7:0]=RXFIFO_CNT, bits [19:16]=TXFIFO_CNT
#   UART_CONF0_REG   = 0x3FF40020  -- configuration: baud, frame format
#   UART_CLKDIV_REG  = 0x3FF40014  -- baud rate divisor (APB_CLK / baud)
#
# Pre-computed UART_CLKDIV values for APB_CLK = 80 MHz:
#   9600   -> 8333
#   115200 -> 694
#   921600 -> 86
#
# TX: write byte to FIFO_REG when TXFIFO_CNT < 128.
# RX: read byte from FIFO_REG when RXFIFO_CNT > 0.
#
# Inline asm register convention (call0 ABI):
#   a2 -- holds the 32-bit value to store or receives the loaded value
#   a3 -- holds the MMIO register address
#   MMIO write: s32i a2, a3, 0  -- *(a3+0) = a2
#   MMIO read:  l32i a2, a3, 0  -- a2 = *(a3+0)

from pymcu.types import uint32, inline, const

UART0_FIFO: uint32 = 0x3FF40000
UART0_STATUS: uint32 = 0x3FF4001C
UART0_CONF0: uint32 = 0x3FF40020
UART0_CLKDIV: uint32 = 0x3FF40014

UART0_TXFIFO_SHIFT: uint32 = 16
UART0_TXFIFO_MASK: uint32 = 0xFF


@inline
def uart_init(baud: const[uint32]):
    # Set baud rate divisor (APB_CLK = 80 MHz).
    if baud == 9600:
        div: uint32 = 8333
    elif baud == 115200:
        div: uint32 = 694
    elif baud == 921600:
        div: uint32 = 86
    else:
        div: uint32 = 694  # default 115200
    clkdiv_addr: uint32 = UART0_CLKDIV
    # a2=div (divisor value), a3=UART0_CLKDIV: store divisor into CLKDIV register.
    asm("s32i a2, a3, 0")


@inline
def uart_write(data: uint32):
    # Wait until TX FIFO has space (TXFIFO_CNT at bits [19:16] of STATUS).
    status_addr: uint32 = UART0_STATUS
    while True:
        status: uint32 = 0
        # a3=UART0_STATUS: load status register into a2; result lands in status.
        asm("l32i a2, a3, 0")
        txcnt: uint32 = (status >> UART0_TXFIFO_SHIFT) & UART0_TXFIFO_MASK
        if txcnt < 120:
            break
    fifo_addr: uint32 = UART0_FIFO
    # a2=data (byte to transmit), a3=UART0_FIFO: write byte into TX FIFO.
    asm("s32i a2, a3, 0")


@inline
def uart_available() -> uint32:
    status_addr: uint32 = UART0_STATUS
    status: uint32 = 0
    # a3=UART0_STATUS: load status register into a2; result lands in status.
    asm("l32i a2, a3, 0")
    rxcnt: uint32 = status & 0xFF
    return rxcnt


@inline
def uart_read() -> uint32:
    # Block until a byte is available.
    while uart_available() == 0:
        pass
    fifo_addr: uint32 = UART0_FIFO
    data: uint32 = 0
    # a3=UART0_FIFO: read byte from RX FIFO into a2; result lands in data.
    asm("l32i a2, a3, 0")
    return data & 0xFF

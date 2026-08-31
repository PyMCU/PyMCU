# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
#
# CYW43439 WiFi bring-up over bit-banged gSPI -- pymcu.hal.rp.cyw43
#
# ONE driver for the Pico W (RP2040) and the Pico 2 W (RP2350). The part is the same
# CYW43439 on both and the four GPIOs are wired identically, so the whole gSPI
# protocol and everything above it is shared; what differs is the MCU's own GPIO and
# reset registers, and that is the conditional import below and nothing else. Measured
# before it was written: of 561 lines, 59 touched an MCU register and the rest touched
# none.
#
# The CYW43439 is a half-duplex "gSPI" slave on four GPIOs (WL_REG_ON=23, DATA=24,
# CS=25, CLK=29). On real silicon a PIO state machine clocks it; here we bit-bang it
# with plain SIO GPIO, which the RP2040Sharp and RP2350Sharp emulators (pad-edge
# sampled) accept. This module implements the bus bring-up:
# power the chip, probe the 0xFEEDBEAD test register (chip-alive), switch the bus
# to 32-bit little-endian, open the F1 backplane window, request the ALP/HT clock,
# and take the WLAN core out of reset. Firmware download is intentionally a stub
# (the emulator parks it; there is no RF) -- a real device needs the 43439 blob,
# which awaits an initialized-.rodata path on the ARM backend.
#
# This is WiFi bring-up only. SDPCM/CDC join, a TCP/IP stack and MQTT sit above it
# and are staged follow-ups (see docs).

from pymcu.chips import __CHIP__

# The twelve MCU registers, and ONLY those. Ten of the twelve hold a different value on
# the two parts, checked one by one by resolving both chip files to numbers rather than
# by comparing their source text: RESETS_RESET_CLR and RESETS_RESET_DONE are written
# identically in both and still differ, because the base they are offset from does not
# match. Two coincide today, SIO_GPIO_IN and GPIO_FUNC_SIO, and they are listed here
# anyway: they are MCU registers, and moving them out on a coincidence of values would
# let that coincidence decide the structure.
#
# One of the ten is worth naming. `SIO_GPIO_OUT_CLR` on the RP2040 and
# `SIO_GPIO_OUT_SET` on the RP2350 are BOTH 0xD0000018, so a build carrying the wrong
# map does not fault and does not touch a register that is not there: it SETS where it
# meant to CLEAR. On the bit-banged clock line that is a CLK that never falls, which is
# a dead bus and no diagnostic.
#
# Spelled as two explicit imports rather than one `from pymcu.chips.rp2350 import ...`
# left in place. That would also work, and for the wrong reason: the compiler resolves a
# chip-module name against the TARGET first and falls back to the literal module when the
# target does not declare it, so all twelve would come out correct on an RP2040 by way of
# the fallback that is filed as PyMCU#234. It would pass on silicon today and break the
# day that issue is fixed. The project's rule against writing source that exploits a
# compiler fault to get the right answer applies here with more force than usual, because
# this is the stdlib rather than a user's program.
if __CHIP__.name == "rp2350":
    from pymcu.chips.rp2350 import (
        IO_BANK0_BASE, PADS_BANK0_BASE, RESETS_RESET_CLR, RESETS_RESET_DONE,
        SIO_GPIO_OUT_SET, SIO_GPIO_OUT_CLR, SIO_GPIO_IN,
        SIO_GPIO_OE_SET, SIO_GPIO_OE_CLR,
        GPIO_FUNC_SIO, RESET_IO_BANK0, RESET_PADS_BANK0,
    )
else:
    from pymcu.chips.rp2040 import (
        IO_BANK0_BASE, PADS_BANK0_BASE, RESETS_RESET_CLR, RESETS_RESET_DONE,
        SIO_GPIO_OUT_SET, SIO_GPIO_OUT_CLR, SIO_GPIO_IN,
        SIO_GPIO_OE_SET, SIO_GPIO_OE_CLR,
        GPIO_FUNC_SIO, RESET_IO_BANK0, RESET_PADS_BANK0,
    )

# The CYW43439's own registers and the board wiring. Neither depends on the MCU, so
# neither is conditional and neither is written twice.
from pymcu.hal.cyw43_regs import (
    WL_REG_ON, WL_DATA, WL_CS, WL_CLK,
    SPI_BUS_CONTROL, SPI_READ_TEST_REGISTER, SPI_STATUS_REGISTER,
    SPI_WORD_LENGTH_32, SPI_ENDIAN_BIG,
    GSPI_F0_BUS, GSPI_F1_BACKPLANE, GSPI_F2_WLAN,
    SDIO_BACKPLANE_ADDR_LOW, SDIO_BACKPLANE_ADDR_MID, SDIO_BACKPLANE_ADDR_HIGH,
    SDIO_CHIP_CLOCK_CSR, SBSDIO_ALP_AVAIL_REQ, SBSDIO_HT_AVAIL_REQ, SBSDIO_FORCE_HT,
    BP_WLAN_ARMCORE, AI_RESETCTRL_OFFSET,
    STATUS_F2_PKT_AVAILABLE, STATUS_F2_PKT_LEN_SHIFT, STATUS_F2_PKT_LEN_MASK,
)
from pymcu.types import ptr, uint32, uint8, inline
from pymcu.exceptions import CompileError


class CYW43:
    @inline
    def __init__(self):
        # Bring IO_BANK0/PADS out of reset (idempotent -- GPIO HAL may have too).
        RESETS_RESET_CLR.value = (1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0)
        while (RESETS_RESET_DONE.value & ((1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0))) != ((1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0)):
            pass
        self._word32 = 0
        self._cfg_out(WL_REG_ON)
        self._cfg_out(WL_CS)
        self._cfg_out(WL_CLK)
        self._cfg_out(WL_DATA)
        # Idle levels: CS high (inactive), CLK low, power off.
        SIO_GPIO_OUT_SET.value = 1 << WL_CS
        SIO_GPIO_OUT_CLR.value = (1 << WL_CLK) | (1 << WL_REG_ON) | (1 << WL_DATA)

    @inline
    def _cfg_out(self, pin: uint32):
        # Enable the pad and route the pin to SIO as an output.
        pad: ptr[uint32] = ptr(PADS_BANK0_BASE + 4 + 4 * pin)
        pad.value = 1 << 6                       # input-enable (needed to read DATA back)
        ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * pin + 4)
        ctrl.value = GPIO_FUNC_SIO
        SIO_GPIO_OE_SET.value = 1 << pin

    # ── low-level bit-bang ──
    @inline
    def _clk_out_bit(self, bit: uint32):
        # Present the bit on DATA, then a rising CLK edge (the slave samples here).
        if bit != 0:
            SIO_GPIO_OUT_SET.value = 1 << WL_DATA
        else:
            SIO_GPIO_OUT_CLR.value = 1 << WL_DATA
        SIO_GPIO_OUT_SET.value = 1 << WL_CLK     # rising edge -> slave samples
        SIO_GPIO_OUT_CLR.value = 1 << WL_CLK

    @inline
    def _clk_in_bit(self) -> uint32:
        # The slave presents the next bit on the CLK falling edge; sample after it.
        SIO_GPIO_OUT_SET.value = 1 << WL_CLK
        SIO_GPIO_OUT_CLR.value = 1 << WL_CLK     # falling edge -> slave drives DATA
        return (SIO_GPIO_IN.value >> WL_DATA) & 1

    @inline
    def _clk_out_byte(self, b: uint32):
        # MSB first.
        i: uint32 = 8
        while i > 0:
            i = i - 1
            self._clk_out_bit((b >> i) & 1)

    @inline
    def _cmd_word(self, write: uint32, fn: uint32, addr: uint32, sz: uint32) -> uint32:
        # gSPI command: [31]=write [30]=increment [29:28]=fn [27:11]=addr [10:0]=size
        return (write << 31) | (1 << 30) | (fn << 28) | ((addr & 0x1FFFF) << 11) | (sz & 0x7FF)

    @inline
    def _send_cmd(self, cmd: uint32):
        # In the startup 16-bit-swapped mode the driver lays the command out with a
        # 16-bit word swap; after SPI_BUS_CONTROL it is little-endian. Emit the byte
        # order the chip decodes (Cyw43439Device.DecodeCommand).
        if self._word32 != 0:
            self._clk_out_byte(cmd & 0xFF)
            self._clk_out_byte((cmd >> 8) & 0xFF)
            self._clk_out_byte((cmd >> 16) & 0xFF)
            self._clk_out_byte((cmd >> 24) & 0xFF)
        else:
            self._clk_out_byte((cmd >> 8) & 0xFF)
            self._clk_out_byte(cmd & 0xFF)
            self._clk_out_byte((cmd >> 24) & 0xFF)
            self._clk_out_byte((cmd >> 16) & 0xFF)

    @inline
    def _cs_low(self):
        SIO_GPIO_OUT_CLR.value = 1 << WL_CS

    @inline
    def _cs_high(self):
        SIO_GPIO_OUT_SET.value = 1 << WL_CS

    # ── the rest of the MCU surface ──
    #
    # These four were written out longhand inside read32(), init() and f2_read(), which
    # is where a port of this driver goes wrong without failing to build. A census by
    # function said the MCU-specific code was the six helpers above, 51 lines; it was
    # those plus eight lines sitting INSIDE protocol functions, where they read as
    # protocol. Whoever ported the six would have shipped a driver that compiles, links,
    # and never gets a byte back from the chip.
    #
    # Named here so the answer to "what does this driver need from the MCU" is one block
    # and can be checked by looking rather than by grepping the whole file.

    @inline
    def _power_on(self):
        # WL_REG_ON high: the CYW43439's power/reset gate. Was open-coded in init().
        SIO_GPIO_OUT_SET.value = 1 << WL_REG_ON

    @inline
    def _data_in(self):
        # Release DATA so the slave can drive it. Was open-coded in read32() and f2_read().
        SIO_GPIO_OE_CLR.value = 1 << WL_DATA

    @inline
    def _data_out(self):
        # Take DATA back. Was open-coded in read32() and f2_read().
        SIO_GPIO_OE_SET.value = 1 << WL_DATA


    # ── gSPI register access ──
    @inline
    def read32(self, fn: uint32, addr: uint32) -> uint32:
        self._cs_low()
        self._send_cmd(self._cmd_word(0, fn, addr, 4))
        self._data_in()                          # DATA -> input for the response
        # F1 reads carry a 16-byte dummy pad before data; F0 has none.
        if fn == GSPI_F1_BACKPLANE:
            pad: uint32 = 16
            while pad > 0:
                pad = pad - 1
                self._clk_in_bit()
                self._clk_in_bit()
                self._clk_in_bit()
                self._clk_in_bit()
                self._clk_in_bit()
                self._clk_in_bit()
                self._clk_in_bit()
                self._clk_in_bit()
        # The slave presents bit 0 the moment DATA becomes an input (OE low), then the
        # next bit on each CLK falling edge -- so SAMPLE first, then clock to advance.
        # The 32 bits arrive MSB-first as bytes resp[0..3] where resp[0] is the register's
        # LSB (little-endian), so `raw` = byteswap(value); swap it back.
        raw: uint32 = 0
        n: uint32 = 32
        while n > 0:
            n = n - 1
            # THE ONE MCU-REGISTER SITE LEFT OUTSIDE A HELPER, and it is here on purpose.
            #
            # Sample first, THEN clock: the slave presents bit 0 the moment DATA becomes an
            # input and each later bit on the CLK falling edge, so this is the reverse of
            # _clk_in_bit() and not a duplicate of it.
            #
            # Naming it was tried and reverted. A helper has to take the accumulator and
            # return it, or the shift-and-or moves to AFTER the clock edge and changes the
            # delay between sampling and the rising edge on a bit-banged bus. Taking it
            # costs a copy per bit in this 32-iteration loop, which showed up in the IR as
            # `raw = raw`. Both versions changed the rp2350 output that already works, so
            # the three lines stay where they are and say so instead.
            raw = (raw << 1) | ((SIO_GPIO_IN.value >> WL_DATA) & 1)
            SIO_GPIO_OUT_SET.value = 1 << WL_CLK
            SIO_GPIO_OUT_CLR.value = 1 << WL_CLK
        self._data_out()
        self._cs_high()
        return ((raw & 0xFF) << 24) | (((raw >> 8) & 0xFF) << 16) | (((raw >> 16) & 0xFF) << 8) | ((raw >> 24) & 0xFF)

    @inline
    def write32(self, fn: uint32, addr: uint32, val: uint32):
        self._cs_low()
        self._send_cmd(self._cmd_word(1, fn, addr, 4))
        # Data bytes follow the same swap discipline as the command.
        if self._word32 != 0:
            self._clk_out_byte(val & 0xFF)
            self._clk_out_byte((val >> 8) & 0xFF)
            self._clk_out_byte((val >> 16) & 0xFF)
            self._clk_out_byte((val >> 24) & 0xFF)
        else:
            self._clk_out_byte((val >> 8) & 0xFF)
            self._clk_out_byte(val & 0xFF)
            self._clk_out_byte((val >> 24) & 0xFF)
            self._clk_out_byte((val >> 16) & 0xFF)
        self._cs_high()
        # SPI_BUS_CONTROL is the last swapped command; the bus is 32-bit LE after it.
        if fn == GSPI_F0_BUS and addr == SPI_BUS_CONTROL and self._word32 == 0:
            self._word32 = 1

    # ── bring-up ──
    @inline
    def init(self) -> uint32:
        # Power the chip (WL_REG_ON high) and let it settle.
        self._power_on()
        w: uint32 = 2000
        while w > 0:
            w = w - 1

        # 1. Probe: read SPI_READ_TEST_REGISTER -> 0xFEEDBEAD confirms the chip is alive.
        test: uint32 = self.read32(GSPI_F0_BUS, SPI_READ_TEST_REGISTER)

        # 2. Switch the bus to 32-bit little-endian.
        self.write32(GSPI_F0_BUS, SPI_BUS_CONTROL, SPI_WORD_LENGTH_32 | SPI_ENDIAN_BIG)

        # 3. Point the F1 backplane window at the chip-clock/CSR bank and request ALP/HT.
        self.write32(GSPI_F1_BACKPLANE, SDIO_CHIP_CLOCK_CSR,
                     SBSDIO_ALP_AVAIL_REQ | SBSDIO_HT_AVAIL_REQ | SBSDIO_FORCE_HT)

        # 4. Take the WLAN ARM core out of reset (clear AI_RESETCTRL bit0). This is the
        #    one functional gate the emulator needs to flip F2 ready. Firmware download
        #    is stubbed (no blob).
        self.write32(GSPI_F1_BACKPLANE, SDIO_BACKPLANE_ADDR_LOW,
                     (BP_WLAN_ARMCORE >> 8) & 0xFF)
        self.write32(GSPI_F1_BACKPLANE, SDIO_BACKPLANE_ADDR_MID,
                     (BP_WLAN_ARMCORE >> 16) & 0xFF)
        self.write32(GSPI_F1_BACKPLANE, SDIO_BACKPLANE_ADDR_HIGH,
                     (BP_WLAN_ARMCORE >> 24) & 0xFF)
        self.write32(GSPI_F1_BACKPLANE, AI_RESETCTRL_OFFSET & 0x7FFF, 0)

        # Report chip-alive: the low 16 bits of the test pattern survive the read path.
        return test

    # ── F2 / SDPCM WLAN control plane ──
    @inline
    def join_open(self, ssid: const[str]) -> uint32:
        # Associate with an OPEN AP: send a WLC_SET_SSID ioctl on the SDPCM Control
        # channel. The chip answers with the async EV_SET_SSID -> EV_AUTH -> EV_LINK(up)
        # chain (open-network join). Frame = 12B SDPCM + 16B CDC ioctl + le32(len)+ssid.
        # The buffer + the F2 write are kept in ONE method: passing a local array across
        # a nested @inline call, and reading self._word32 there, are both unreliable under
        # the ZCA collapse. F2 is always post-bring-up (32-bit LE), so emit LE directly.
        n: uint32 = len(ssid)
        total: uint32 = 32 + n
        buf: uint8[64] = [0] * 64
        # SDPCM header (12B)
        buf[0] = total & 0xFF
        buf[1] = (total >> 8) & 0xFF
        buf[2] = (total ^ 0xFFFF) & 0xFF          # ~size
        buf[3] = ((total ^ 0xFFFF) >> 8) & 0xFF
        buf[7] = 12                               # header_len; channel(buf[5])=0 Control
        # CDC ioctl header (16B) at offset 12
        buf[12] = 26                              # WLC_SET_SSID (u32 LE)
        buf[16] = (4 + n) & 0xFF                  # out_len
        buf[20] = 2                               # flags: SDPCM_SET
        # payload at offset 28: le32(ssid_len) + ssid
        buf[28] = n & 0xFF
        i: uint32 = 0
        while i < n:
            buf[32 + i] = ssid[i]
            i = i + 1
        # Clock the F2 write: command (LE) then the whole packet.
        cmd: uint32 = self._cmd_word(1, 2, 0, total)
        self._cs_low()
        self._clk_out_byte(cmd & 0xFF)
        self._clk_out_byte((cmd >> 8) & 0xFF)
        self._clk_out_byte((cmd >> 16) & 0xFF)
        self._clk_out_byte((cmd >> 24) & 0xFF)
        j: uint32 = 0
        while j < total:
            b: uint32 = buf[j]
            # Unrolled 8-bit MSB-first clock-out (no inner loop, so the outer while does
            # not nest a second while through the @inline expansion of _clk_out_byte).
            self._clk_out_bit((b >> 7) & 1)
            self._clk_out_bit((b >> 6) & 1)
            self._clk_out_bit((b >> 5) & 1)
            self._clk_out_bit((b >> 4) & 1)
            self._clk_out_bit((b >> 3) & 1)
            self._clk_out_bit((b >> 2) & 1)
            self._clk_out_bit((b >> 1) & 1)
            self._clk_out_bit(b & 1)
            j = j + 1
        self._cs_high()
        # Read the F0 status: its CS-low starts a fresh transaction that FLUSHES the ioctl
        # write into the chip (the emulator applies a queued write on the next StartTransaction),
        # and it is the protocol-correct poll after issuing an ioctl.
        self.read32(GSPI_F0_BUS, SPI_STATUS_REGISTER)
        return total

    @inline
    def f2_available(self) -> uint32:
        # Length of the next queued inbound SDPCM packet (0 = none), from F0 status.
        st: uint32 = self.read32(GSPI_F0_BUS, SPI_STATUS_REGISTER)
        if (st & STATUS_F2_PKT_AVAILABLE) == 0:
            return 0
        return (st >> STATUS_F2_PKT_LEN_SHIFT) & STATUS_F2_PKT_LEN_MASK

    @inline
    def f2_read(self, buf: bytearray, maxlen: uint32) -> uint32:
        # Read the next inbound SDPCM packet (WLAN control/event/data) over F2 into buf,
        # returning its length. The 8-bit reads are unrolled so the byte loop does not
        # nest a while through an @inline call (see BUG-1 in join_open).
        plen: uint32 = self.f2_available()
        if plen == 0:
            return 0
        rd: uint32 = plen
        if rd > maxlen:
            rd = maxlen
        self._cs_low()
        cmd: uint32 = self._cmd_word(0, 2, 0, plen)
        self._clk_out_byte(cmd & 0xFF)
        self._clk_out_byte((cmd >> 8) & 0xFF)
        self._clk_out_byte((cmd >> 16) & 0xFF)
        self._clk_out_byte((cmd >> 24) & 0xFF)
        self._data_in()                               # DATA -> input (no F2 read pad)
        j: uint32 = 0
        while j < rd:
            b: uint32 = 0
            b = (b << 1) | self._clk_in_bit()
            b = (b << 1) | self._clk_in_bit()
            b = (b << 1) | self._clk_in_bit()
            b = (b << 1) | self._clk_in_bit()
            b = (b << 1) | self._clk_in_bit()
            b = (b << 1) | self._clk_in_bit()
            b = (b << 1) | self._clk_in_bit()
            b = (b << 1) | self._clk_in_bit()
            buf[j] = b
            j = j + 1
        self._data_out()
        self._cs_high()
        return plen

    @inline
    def _f2_send(self, buf: bytearray, n: uint32):
        # Clock a pre-built SDPCM packet of n bytes over F2 (LE), then a status read to
        # flush it. Bits unrolled (BUG-1). Used for both ioctls and Ethernet data frames.
        cmd: uint32 = self._cmd_word(1, 2, 0, n)
        self._cs_low()
        self._clk_out_byte(cmd & 0xFF)
        self._clk_out_byte((cmd >> 8) & 0xFF)
        self._clk_out_byte((cmd >> 16) & 0xFF)
        self._clk_out_byte((cmd >> 24) & 0xFF)
        j: uint32 = 0
        while j < n:
            b: uint32 = buf[j]
            self._clk_out_bit((b >> 7) & 1)
            self._clk_out_bit((b >> 6) & 1)
            self._clk_out_bit((b >> 5) & 1)
            self._clk_out_bit((b >> 4) & 1)
            self._clk_out_bit((b >> 3) & 1)
            self._clk_out_bit((b >> 2) & 1)
            self._clk_out_bit((b >> 1) & 1)
            self._clk_out_bit(b & 1)
            j = j + 1
        self._cs_high()
        self.read32(GSPI_F0_BUS, SPI_STATUS_REGISTER)

    @inline
    def _eth_frame(self, buf: bytearray, ethlen: uint32) -> uint32:
        # Wrap an Ethernet frame already placed at buf[18..18+ethlen] in an SDPCM DATA
        # header (12B) + 2B pad + BDC header (4B); returns the total SDPCM size. Layout
        # matches Sdpcm.HandleEthernetOut: header_len=14, BDC at 14, payload at 18.
        total: uint32 = 18 + ethlen
        buf[0] = total & 0xFF
        buf[1] = (total >> 8) & 0xFF
        buf[2] = (total ^ 0xFFFF) & 0xFF
        buf[3] = ((total ^ 0xFFFF) >> 8) & 0xFF
        buf[5] = 2                                 # channel = Data
        buf[7] = 14                                # header_len (SDPCM 12 + 2 pad)
        return total

    @inline
    def send_arp(self):
        # Broadcast an ARP request for the gateway (192.168.4.1) from the guest
        # (02:00:00:00:00:01 / 192.168.4.2). The virtual gateway answers with its MAC.
        buf: uint8[128] = [0] * 128
        # Ethernet header at offset 18: dst = broadcast, src = guest MAC, type = 0x0806
        buf[18] = 0xFF
        buf[19] = 0xFF
        buf[20] = 0xFF
        buf[21] = 0xFF
        buf[22] = 0xFF
        buf[23] = 0xFF
        buf[24] = 0x02                             # guest MAC 02:00:00:00:00:01
        buf[29] = 0x01
        buf[30] = 0x08                             # ethertype 0x0806 (ARP)
        buf[31] = 0x06
        # ARP at offset 32: htype=1 ptype=0x0800 hlen=6 plen=4 op=1
        buf[33] = 0x01                             # htype
        buf[34] = 0x08                             # ptype hi
        buf[36] = 0x06                             # hlen
        buf[37] = 0x04                             # plen
        buf[39] = 0x01                             # op = request
        buf[40] = 0x02                             # sha = guest MAC
        buf[45] = 0x01
        buf[46] = 192                              # spa = 192.168.4.2
        buf[47] = 168
        buf[48] = 4
        buf[49] = 2
        # tha = 0 (unknown), tpa = 192.168.4.1
        buf[56] = 192
        buf[57] = 168
        buf[58] = 4
        buf[59] = 1
        total: uint32 = self._eth_frame(buf, 42)   # 14 eth + 28 arp
        self._f2_send(buf, total)

    # ── TCP + MQTT (over the F2 DATA channel) ──
    # Static config: guest 02:00:00:00:00:01 / 192.168.4.2, gateway/broker MAC
    # 02:00:5E:00:04:01 / 192.168.4.1:1883, guest ephemeral port 50000. TX frame layout:
    # Ethernet@18, IP@32, TCP@52, TCP-payload@72 (18 + 14 + 20 + 20).
    @inline
    def _ip_csum(self, buf: bytearray) -> uint32:
        s: uint32 = 0
        i: uint32 = 0
        while i < 20:
            s = s + ((buf[32 + i] << 8) | buf[32 + i + 1])
            i = i + 2
        s = (s & 0xFFFF) + (s >> 16)
        s = (s & 0xFFFF) + (s >> 16)
        return (~s) & 0xFFFF

    @inline
    def _tcp_csum(self, buf: bytearray, tlen: uint32) -> uint32:
        # pseudo-header: src 192.168.4.2, dst 192.168.4.1, proto 6, tcp length
        s: uint32 = 0xC0A8 + 0x0402 + 0xC0A8 + 0x0401 + 6 + tlen
        i: uint32 = 0
        while i < tlen:
            hi: uint32 = buf[52 + i]
            lo: uint32 = 0
            if (i + 1) < tlen:
                lo = buf[52 + i + 1]
            s = s + ((hi << 8) | lo)
            i = i + 2
        s = (s & 0xFFFF) + (s >> 16)
        s = (s & 0xFFFF) + (s >> 16)
        return (~s) & 0xFFFF

    @inline
    def _tcp_send(self, buf: bytearray, seq: uint32, ack: uint32, flags: uint32, paylen: uint32):
        # Ethernet header @18
        buf[18] = 0x02   # dst = gateway MAC 02:00:5E:00:04:01
        buf[19] = 0x00
        buf[20] = 0x5E
        buf[21] = 0x00
        buf[22] = 0x04
        buf[23] = 0x01
        buf[24] = 0x02   # src = guest MAC 02:00:00:00:00:01
        buf[25] = 0x00
        buf[26] = 0x00
        buf[27] = 0x00
        buf[28] = 0x00
        buf[29] = 0x01
        buf[30] = 0x08   # ethertype 0x0800 (IPv4)
        buf[31] = 0x00
        # IPv4 header @32
        tot: uint32 = 40 + paylen
        buf[32] = 0x45
        buf[33] = 0
        buf[34] = (tot >> 8) & 0xFF
        buf[35] = tot & 0xFF
        buf[40] = 64     # TTL
        buf[41] = 6      # proto TCP
        buf[44] = 192    # src 192.168.4.2
        buf[45] = 168
        buf[46] = 4
        buf[47] = 2
        buf[48] = 192    # dst 192.168.4.1
        buf[49] = 168
        buf[50] = 4
        buf[51] = 1
        buf[42] = 0
        buf[43] = 0
        c: uint32 = self._ip_csum(buf)
        buf[42] = (c >> 8) & 0xFF
        buf[43] = c & 0xFF
        # TCP header @52
        buf[52] = 0xC3   # src port 50000 = 0xC350
        buf[53] = 0x50
        buf[54] = 0x07   # dst port 1883 = 0x075B
        buf[55] = 0x5B
        buf[56] = (seq >> 24) & 0xFF
        buf[57] = (seq >> 16) & 0xFF
        buf[58] = (seq >> 8) & 0xFF
        buf[59] = seq & 0xFF
        buf[60] = (ack >> 24) & 0xFF
        buf[61] = (ack >> 16) & 0xFF
        buf[62] = (ack >> 8) & 0xFF
        buf[63] = ack & 0xFF
        buf[64] = 0x50   # data offset = 5 words
        buf[65] = flags & 0xFF
        buf[66] = 0x20   # window 0x2000
        buf[67] = 0x00
        buf[68] = 0      # checksum (zeroed for calc)
        buf[69] = 0
        buf[70] = 0      # urgent ptr
        buf[71] = 0
        tc: uint32 = self._tcp_csum(buf, 20 + paylen)
        buf[68] = (tc >> 8) & 0xFF
        buf[69] = tc & 0xFF
        total: uint32 = self._eth_frame(buf, 34 + 20 + paylen)   # eth14 + ip20 + tcp20 + pay
        self._f2_send(buf, total)

    @inline
    def mqtt_publish(self, value: uint32):
        # Connect to the broker and PUBLISH `value` (as one ASCII byte range) to topic "dht".
        # Minimal QoS-0 flow: SYN -> SYN-ACK -> ACK+CONNECT -> PUBLISH.
        buf: uint8[256] = [0] * 256
        rx: uint8[256] = [0] * 256
        # --- SYN (seq 1000) ---
        self._tcp_send(buf, 1000, 0, 0x02, 0)
        # --- read SYN-ACK; broker ISN at rx[54..57], our ack = ISN+1 ---
        self.f2_read(rx, 256)
        bseq: uint32 = (rx[54] << 24) | (rx[55] << 16) | (rx[56] << 8) | rx[57]
        ackn: uint32 = bseq + 1
        # --- ACK (seq 1001) + MQTT CONNECT as payload @72 ---
        # CONNECT: 0x10 len 00 04 'MQTT' 04 02 00 3C 00 02 'pm'  (clientid "pm")
        buf[72] = 0x10
        buf[73] = 16     # remaining length
        buf[74] = 0x00
        buf[75] = 0x04
        buf[76] = 0x4D   # M
        buf[77] = 0x51   # Q
        buf[78] = 0x54   # T
        buf[79] = 0x54   # T
        buf[80] = 0x04   # protocol level 4
        buf[81] = 0x02   # connect flags (clean session)
        buf[82] = 0x00   # keepalive 60
        buf[83] = 0x3C
        buf[84] = 0x00   # client id len 2
        buf[85] = 0x02
        buf[86] = 0x70   # p
        buf[87] = 0x6D   # m
        self._tcp_send(buf, 1001, ackn, 0x18, 18)   # PSH|ACK, 18-byte CONNECT
        self.f2_read(rx, 256)                        # CONNACK (advances broker seq by 4)
        # --- PUBLISH "dht" = value(one ASCII digit set), seq 1019 ---
        # PUBLISH 0x30, remlen, topic(len2 "dht"), payload = value as 3 ASCII digits
        d0: uint32 = 48 + ((value // 100) % 10)
        d1: uint32 = 48 + ((value // 10) % 10)
        d2: uint32 = 48 + (value % 10)
        buf[72] = 0x30
        buf[73] = 8      # remlen = 2 + 3(topic) + 3(payload)
        buf[74] = 0x00   # topic len 3
        buf[75] = 0x03
        buf[76] = 0x64   # d
        buf[77] = 0x68   # h
        buf[78] = 0x74   # t
        buf[79] = d0 & 0xFF
        buf[80] = d1 & 0xFF
        buf[81] = d2 & 0xFF
        self._tcp_send(buf, 1019, ackn + 4, 0x18, 10)   # PSH|ACK, 10-byte PUBLISH

    @inline
    def _drain_rx(self, buf: bytearray):
        # Consume any queued inbound packets (the post-join async events) so a following
        # request/response exchange reads its OWN reply. Unrolled (BUG-1 nested-while).
        self.f2_read(buf, 256)
        self.f2_read(buf, 256)
        self.f2_read(buf, 256)
        self.f2_read(buf, 256)
        self.f2_read(buf, 256)
        self.f2_read(buf, 256)
        self.f2_read(buf, 256)
        self.f2_read(buf, 256)

    @inline
    def settle(self):
        # Drain the post-join async SDPCM events so a later request/response exchange
        # (e.g. the MQTT TCP handshake) reads its OWN reply.
        sbuf: uint8[256] = [0] * 256
        self._drain_rx(sbuf)

    @inline
    def connect(self, ssid: const[str], key: const[str] = ""):
        # Convenience: bring up the radio, join the AP, and settle (drain post-join RX).
        # `key` exists only so the signature matches MicroPython's WLAN.connect();
        # the join path is join_open(), which sends no WPA/PSK material at all, so a
        # key would be silently dropped. Reject it at compile time instead.
        if key != "":
            raise CompileError("WiFi: WPA is not supported yet; connect() can only join open networks -- leave key empty")
        self.init()
        self.join_open(ssid)
        self.settle()

    @inline
    def publish(self, value: uint32):
        self.mqtt_publish(value)

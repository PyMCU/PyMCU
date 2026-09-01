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
# and take the WLAN core out of reset.
#
# FIRMWARE DOWNLOAD IS A STUB, AND IT IS THE ONE THING BETWEEN THIS AND SILICON.
# The CYW43439 has no on-chip flash: the host must upload its WiFi firmware over the
# bus at every power-on, before the WLAN core will answer anything. init() releases
# that core from reset with nothing loaded into it. The emulator does not care,
# because its Sdpcm model answers ioctls itself and no core is running firmware
# there -- which also means NO TEST HERE WOULD CATCH THE OMISSION. On a real part
# the ten ioctls of a join go out on the bus and nothing replies.
#
# This used to say the blob "awaits an initialized-.rodata path on the ARM backend".
# That is no longer true and was measured on 2026-09-01: a const[uint8[235520]]
# table, the size of the real blob, compiles for rp2040 in under a second and the
# bytes are all present in firmware.bin (920 complete 0..255 runs at offset 736).
# Putting the blob in flash is available today. What is missing is the download
# itself: write it into the WLAN core's RAM through the F1 backplane window in
# chunks, load the CLM through the `clmload` iovar, and only then release reset.
# Note before vendoring anything: the 43439 blob is Infineon/Broadcom licensed and
# this project is MIT.
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
# left in place, and that is not a stylistic preference. A chip-module import resolves to
# the module NAMED, not to the target: measured, `from pymcu.chips.rp2350 import
# SIO_GPIO_OUT_SET` compiled for rp2040 emits 0xD0000018, which is the RP2350's SET and
# the RP2040's CLR. So the single-import version would produce exactly the failure
# described above, on the clock line, and it would build clean.
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
from pymcu.time import delay_ms


class CYW43:
    @inline
    def __init__(self):
        # Bring IO_BANK0/PADS out of reset (idempotent -- GPIO HAL may have too).
        RESETS_RESET_CLR.value = (1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0)
        while (RESETS_RESET_DONE.value & ((1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0))) != ((1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0)):
            pass
        self._word32 = 0
        # SDPCM transmit sequence, one 8-bit counter for every F2 packet. See _f2_send.
        self._seq = 0
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
        #    one functional gate the emulator needs to flip F2 ready.
        #
        #    NOTHING HAS BEEN LOADED INTO THAT CORE. On silicon a CYW43439 released from
        #    reset with no firmware answers nothing, so every ioctl below it goes out on
        #    the bus unanswered. The emulator reaches F2-ready anyway because its Sdpcm
        #    model replies in place of a core, so this line looks correct in every test
        #    we have. See the firmware-download note at the top of the file.
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
    def _ioctl_send(self, buf: bytearray, cmd: uint32, paylen: uint32) -> uint32:
        # Frame an SDPCM control packet whose payload is already at offset 28, and clock
        # it out. cmd is two bytes because WLC_SET_WSEC_PMK is 268 and WLC_SET_VAR is 263:
        # join_open writes only buf[12] because WLC_SET_SSID is 26 and fits in one.
        total: uint32 = 28 + paylen
        buf[0] = total & 0xFF
        buf[1] = (total >> 8) & 0xFF
        buf[2] = (total ^ 0xFFFF) & 0xFF          # ~size
        buf[3] = ((total ^ 0xFFFF) >> 8) & 0xFF
        buf[7] = 12                               # header_len; channel(buf[5])=0 Control
        buf[12] = cmd & 0xFF
        buf[13] = (cmd >> 8) & 0xFF
        buf[16] = paylen & 0xFF
        buf[17] = (paylen >> 8) & 0xFF
        buf[20] = 2                               # flags: SDPCM_SET
        self._f2_send(buf, total)
        return total

    @inline
    def _set_ioctl_u32(self, buf: bytearray, cmd: uint32, val: uint32) -> uint32:
        # A plain ioctl whose whole payload is one little-endian u32.
        buf[28] = val & 0xFF
        buf[29] = (val >> 8) & 0xFF
        buf[30] = (val >> 16) & 0xFF
        buf[31] = (val >> 24) & 0xFF
        return self._ioctl_send(buf, cmd, 4)

    @inline
    def _write_iovar_u32(self, buf: bytearray, name: const[str], val: uint32) -> uint32:
        # An iovar SET: WLC_SET_VAR carrying "name\0" followed by the value.
        n: uint32 = len(name)
        i: uint32 = 0
        while i < n:
            buf[28 + i] = name[i]
            i = i + 1
        buf[28 + n] = 0
        buf[29 + n] = val & 0xFF
        buf[30 + n] = (val >> 8) & 0xFF
        buf[31 + n] = (val >> 16) & 0xFF
        buf[32 + n] = (val >> 24) & 0xFF
        return self._ioctl_send(buf, 263, n + 5)

    @inline
    def _write_iovar_u32_u32(self, buf: bytearray, name: const[str],
                             a: uint32, b: uint32) -> uint32:
        # The "bsscfg:" form: the interface index first, then the value. Both u32 LE.
        n: uint32 = len(name)
        i: uint32 = 0
        while i < n:
            buf[28 + i] = name[i]
            i = i + 1
        buf[28 + n] = 0
        buf[29 + n] = a & 0xFF
        buf[30 + n] = (a >> 8) & 0xFF
        buf[31 + n] = (a >> 16) & 0xFF
        buf[32 + n] = (a >> 24) & 0xFF
        buf[33 + n] = b & 0xFF
        buf[34 + n] = (b >> 8) & 0xFF
        buf[35 + n] = (b >> 16) & 0xFF
        buf[36 + n] = (b >> 24) & 0xFF
        return self._ioctl_send(buf, 263, n + 9)

    @inline
    def join_open(self, ssid: const[str]) -> uint32:
        # Associate with an OPEN AP: a WLC_SET_SSID ioctl on the SDPCM Control channel whose
        # payload is le32(ssid_len) + ssid. The chip answers with the async
        # EV_SET_SSID -> EV_AUTH -> EV_LINK(up) chain.
        #
        # This built the frame and clocked it out in one method, because passing a local array
        # through a nested @inline was unreliable and so was reading self._word32 there. #246
        # fixed exactly that, and _ioctl_send carrying a buffer through two levels is the
        # demonstration. Folding it in was NOT tidiness: the hand-built frame never wrote the
        # SDPCM sequence number, so an open join left every frame claiming packet zero and
        # never advanced the counter for anything that ran after it. An open join is the
        # fallback if WPA2 misbehaves on the bench, which made it the one send path still
        # unfixed and the worst one to leave that way.
        n: uint32 = len(ssid)
        buf: uint8[64] = [0] * 64
        buf[28] = n & 0xFF
        i: uint32 = 0
        while i < n:
            buf[32 + i] = ssid[i]
            i = i + 1
        return self._ioctl_send(buf, 26, 4 + n)                 # WLC_SET_SSID

    @inline
    def join_wpa2(self, ssid: const[str], key: const[str]) -> uint32:
        # Associate with a WPA2-PSK AP. The four-way handshake is NOT done here: the
        # CYW43439's own firmware runs the supplicant, so the host's whole job is to hand
        # it the passphrase and the auth parameters and then send the same WLC_SET_SSID
        # that join_open sends. Ten sends, in this order, from cyw43_ll_wifi_join for
        # CYW43_AUTH_WPA2_AES_PSK with no BSSID and no channel.
        #
        # ONE buffer for all ten. See the helpers above for why.
        buf: uint8[128] = [0] * 128

        # wsec = auth_type & 0xFF, and CYW43_AUTH_WPA2_AES_PSK is 0x00400004, so 4 = AES.
        self._set_ioctl_u32(buf, 134, 4)                        # WLC_SET_WSEC
        # Hand the association over to the chip's own supplicant, and give it the two
        # knobs the reference sets: EAPOL version -1 means "whatever the AP speaks".
        self._write_iovar_u32_u32(buf, "bsscfg:sup_wpa", 0, 1)
        self._write_iovar_u32_u32(buf, "bsscfg:sup_wpa2_eapver", 0, 0xFFFFFFFF)
        self._write_iovar_u32_u32(buf, "bsscfg:sup_wpa_tmo", 0, 5000)

        # The passphrase, as a wsec_pmk_t: le16(len) le16(1) then the key, and the struct
        # is a FIXED 4 + 64 bytes. The tail past the key has to be zero -- the buffer is
        # reused across all ten sends, so it is cleared here rather than assumed.
        k: uint32 = len(key)
        buf[28] = k & 0xFF
        buf[29] = (k >> 8) & 0xFF
        buf[30] = 1
        buf[31] = 0
        i: uint32 = 0
        while i < k:
            buf[32 + i] = key[i]
            i = i + 1
        while i < 64:
            buf[32 + i] = 0
            i = i + 1
        # The ONE place this driver depends on wall-clock time instead of edge order: the radio
        # firmware needs settling time before it will accept the PMK, and the reference calls
        # the delay required to avoid intermittent failure. An emulator that checks the
        # SEQUENCE of edges and never their separation cannot see this missing, so leaving it
        # out is green here and flaky on silicon, which is the worst pair for a demonstration.
        delay_ms(2)
        self._ioctl_send(buf, 268, 68)                          # WLC_SET_WSEC_PMK

        self._set_ioctl_u32(buf, 20, 1)                         # WLC_SET_INFRA
        self._set_ioctl_u32(buf, 22, 0)                         # WLC_SET_AUTH, AUTH_TYPE_OPEN
        self._write_iovar_u32(buf, "mfp", 1)                    # MFP_CAPABLE
        self._set_ioctl_u32(buf, 165, 0x0080)                   # WLC_SET_WPA_AUTH, WPA2 PSK

        # And the join itself: le32(ssid_len) + ssid, the same frame join_open sends.
        n: uint32 = len(ssid)
        buf[28] = n & 0xFF
        buf[29] = (n >> 8) & 0xFF
        buf[30] = 0
        buf[31] = 0
        j: uint32 = 0
        while j < n:
            buf[32 + j] = ssid[j]
            j = j + 1
        return self._ioctl_send(buf, 26, 4 + n)                 # WLC_SET_SSID

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
        # SDPCM sequence number, byte 4 of the header. NOT decoration: it is one half of the
        # chip's bus credit flow control. The chip grants credit by advancing bus_data_credit
        # in the headers it sends back, and the host is out of credit when the two are equal.
        # Every frame here used to claim packet zero. The emulator DOES read this byte -- it
        # grants credit as last_host_seq + window -- but it grants relative to whatever the host
        # last sent, so a host frozen at zero never runs out and the defect went unseen until a
        # test read for it. Visible and forgiven, the same shape as the config ioctls. Whether
        # real silicon tolerates it or stalls the bus is still a question only silicon answers.
        buf[4] = self._seq & 0xFF
        self._seq = (self._seq + 1) & 0xFF
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
        # An empty key joins an open network; a key joins WPA2-PSK. Both are const[str],
        # so this branch is decided at compile time and only one join is emitted.
        self.init()
        if key == "":
            self.join_open(ssid)
        else:
            self.join_wpa2(ssid, key)
        self.settle()

    @inline
    def publish(self, value: uint32):
        self.mqtt_publish(value)

# -----------------------------------------------------------------------------
# PyMCU RP2350 clocks -- bring clk_sys up to 150 MHz.
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# The bootrom leaves clk_sys on the low-frequency boot clock (clk_ref), so a
# bare-metal program runs ~12x slower than the 150 MHz the Pico 2 is rated for
# (the hardware-timer-driven delay_ms is unaffected -- it counts the independent
# 1 MHz TIMER -- but anything paced by SysTick or raw CPU work is slow). Call
# clock_init() once at the start of main() to start XOSC, lock PLL_SYS at 150 MHz,
# and switch clk_sys (and clk_peri) onto it. Mirrors the pico-sdk sequence.
from pymcu.types import ptr, uint32


def clock_init():
    # ── XOSC: 12 MHz crystal ─────────────────────────────────────────────────
    xosc_ctrl: ptr[uint32] = ptr(0x40048000)         # XOSC_CTRL (ENABLE + FREQ_RANGE)
    xosc_status: ptr[uint32] = ptr(0x40048004)       # XOSC_STATUS (STABLE = bit 31)
    xosc_startup: ptr[uint32] = ptr(0x4004800C)      # XOSC_STARTUP
    xosc_startup.value = 47                          # ~1 ms startup for a 12 MHz xtal
    xosc_ctrl.value = 0x00FABAA0                     # ENABLE=0xFAB, FREQ_RANGE=0xAA0 (1_15MHZ)
    while (xosc_status.value & 0x80000000) == 0:     # wait for STABLE
        pass

    # ── un-reset PLL_SYS (RESETS bit 14) ─────────────────────────────────────
    resets_clr: ptr[uint32] = ptr(0x40023000)        # RESETS_RESET, atomic CLR alias
    resets_done: ptr[uint32] = ptr(0x40020008)       # RESETS_RESET_DONE
    resets_clr.value = 0x00004000                    # 1 << 14
    while (resets_done.value & 0x00004000) == 0:
        pass

    # ── PLL_SYS: 12 MHz x 125 / (5 x 2) = 150 MHz ────────────────────────────
    pll_cs: ptr[uint32] = ptr(0x40050000)            # CS (REFDIV in [5:0], LOCK = bit 31)
    pll_pwr: ptr[uint32] = ptr(0x40050004)           # PWR
    pll_fbdiv: ptr[uint32] = ptr(0x40050008)         # FBDIV_INT
    pll_prim: ptr[uint32] = ptr(0x4005000C)          # PRIM (POSTDIV1 [18:16], POSTDIV2 [14:12])
    pll_cs.value = 1                                 # REFDIV = 1
    pll_fbdiv.value = 125                            # VCO = 12 * 125 = 1500 MHz
    pll_pwr.value = pll_pwr.value & 0xFFFFFFDE       # power up: clear PD (bit0) + VCOPD (bit5)
    while (pll_cs.value & 0x80000000) == 0:          # wait for LOCK
        pass
    pll_prim.value = 0x00052000                      # POSTDIV1=5 (<<16), POSTDIV2=2 (<<12)
    pll_pwr.value = pll_pwr.value & 0xFFFFFFF7        # enable post dividers: clear POSTDIVPD (bit3)

    # ── clk_sys -> PLL_SYS (CLK_SYS_CTRL.SRC = AUX, AUXSRC = pll_sys = 0) ─────
    clk_sys_ctrl: ptr[uint32] = ptr(0x4001003C)
    clk_sys_ctrl.value = 1

    # ── clk_peri -> clk_sys, enabled (UART/SPI/I2C clock) ────────────────────
    clk_peri_ctrl: ptr[uint32] = ptr(0x40010048)
    clk_peri_ctrl.value = 0x00000800                 # ENABLE (bit 11), AUXSRC = 0 (clk_sys)

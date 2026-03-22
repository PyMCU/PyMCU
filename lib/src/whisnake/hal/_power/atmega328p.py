from whisnake.chips.atmega328p import SMCR
from whisnake.types import uint8, inline

# ATmega328P Sleep / Power Management
# SMCR at DATA 0x53 (I/O 0x33 -- in range for IN/OUT and SBI/CBI)
#
# SMCR bits:
#   bit 3: SM2 \
#   bit 2: SM1  > Sleep Mode select
#   bit 1: SM0 /
#   bit 0: SE   -- Sleep Enable
#
# Sleep modes (SM[2:0]):
#   000 = Idle              -- CPU halted; peripherals (UART, SPI, timers) running; ~6 mA
#   001 = ADC Noise         -- as Idle but ADC, timers, ext-int wake; best ADC accuracy
#   010 = Power-down        -- only ext-int, WDT, TWI address match wake; ~0.1 uA
#   011 = Power-save        -- Power-down but Timer2 (async) keeps running
#   110 = Standby           -- Power-down + fast 6-cycle wake (osc already running)
#   111 = Extended Standby  -- Power-save + fast wake
#
# To enter sleep: set SMCR = (mode << 1) | SE=1, then execute SLEEP instruction.
# To exit sleep: MCU wakes on interrupt, resumes after SLEEP.
# Best practice: clear SE after waking to prevent accidental re-entry.
#
# SMCR I/O address = 0x33. SBI/CBI range is 0x00-0x1F so we use OUT.

@inline
def sleep_idle():
    # Idle: CPU halted, peripherals running. Wake on any interrupt.
    # SMCR = SM=000, SE=1 -> 0x01
    SMCR.value = 0x01
    asm("sleep")
    SMCR.value = 0x00   # clear SE (prevent accidental re-entry)

@inline
def sleep_adc_noise():
    # ADC Noise Reduction: stops CPU and I/O clocks; ADC still runs.
    # Minimises ADC noise from digital switching. Wake on ADC complete or ext-int.
    # SMCR = SM=001, SE=1 -> 0x03
    SMCR.value = 0x03
    asm("sleep")
    SMCR.value = 0x00

@inline
def sleep_power_down():
    # Power-down: deepest sleep. Only external interrupts, TWI address match,
    # or Watchdog timeout can wake. Current: ~0.1 uA at 3V.
    # SMCR = SM=010, SE=1 -> 0x05
    SMCR.value = 0x05
    asm("sleep")
    SMCR.value = 0x00

@inline
def sleep_power_save():
    # Power-save: like Power-down but Timer2 (async) continues running.
    # Useful when a 32.768 kHz crystal is attached to Timer2 for RTC.
    # SMCR = SM=011, SE=1 -> 0x07
    SMCR.value = 0x07
    asm("sleep")
    SMCR.value = 0x00

@inline
def sleep_standby():
    # Standby: Power-down but main oscillator stays on for fast wake (6 cycles).
    # SMCR = SM=110, SE=1 -> 0x0d
    SMCR.value = 0x0d
    asm("sleep")
    SMCR.value = 0x00

@inline
def sleep_extended_standby():
    # Extended Standby: Power-save but main oscillator stays on.
    # SMCR = SM=111, SE=1 -> 0x0f
    SMCR.value = 0x0f
    asm("sleep")
    SMCR.value = 0x00

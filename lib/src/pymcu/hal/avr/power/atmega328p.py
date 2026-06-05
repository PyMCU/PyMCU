from pymcu.chips.atmega328p import SMCR
from pymcu.types import uint8, inline, asm

# ATmega328P Sleep / Power Management
# SMCR at DATA 0x53 (I/O 0x33 -- in range for IN/OUT and SBI/CBI)
#
# SMCR bits:
#   bit 3: SM2  \
#   bit 2: SM1   > Sleep Mode select
#   bit 1: SM0  /
#   bit 0: SE    -- Sleep Enable
#
# Sleep modes (SM[2:0]):
#   000 = Idle              -- CPU halted; all peripherals still running
#   001 = ADC Noise         -- reduces ADC switching noise
#   010 = Power-down        -- deepest sleep; ~0.1 uA
#   011 = Power-save        -- power-down with async timer still running
#   110 = Standby           -- power-down with fast oscillator wake
#   111 = Extended Standby  -- power-save with fast wake

@inline
def sleep_idle():
    SMCR.value = 0x01
    asm("sleep")
    SMCR.value = 0x00

@inline
def sleep_adc_noise():
    SMCR.value = 0x03
    asm("sleep")
    SMCR.value = 0x00

@inline
def sleep_power_down():
    SMCR.value = 0x05
    asm("sleep")
    SMCR.value = 0x00

@inline
def sleep_power_save():
    SMCR.value = 0x07
    asm("sleep")
    SMCR.value = 0x00

@inline
def sleep_standby():
    SMCR.value = 0x0d
    asm("sleep")
    SMCR.value = 0x00

@inline
def sleep_extended_standby():
    SMCR.value = 0x0f
    asm("sleep")
    SMCR.value = 0x00

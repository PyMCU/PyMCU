# ATmega328P: Sensor Dashboard
#
# Samples ADC0 (PC0 / A0) every ~262ms via Timer0 overflow (prescaler 256,
# 64 ticks). Tracks lifetime min/max and a simple EMA (avg = (avg+raw)>>1).
# Blinks PB5 LED on each sample. INT0 (PD2, falling edge) toggles verbose
# <-> compact display mode.
#
# Hardware: Arduino Uno
#   ADC input: PC0 (A0)
#   LED:       PB5 (Arduino pin 13)
#   Button:    PD2 (Arduino pin 2), active-low (INT0 falling edge)
#   UART TX on PD1 at 9600 baud
#
# Verbose:  "R:HH A:HH L:HH H:HH\n"
# Compact:  "HH\n"
#
# Timer0 at prescaler 256, 16 MHz:
#   overflow every 256 * 256 / 16e6 = 4.096 ms
#   64 overflows => ADC sample every ~262 ms
#
from pymcu.types import uint8, uint16
from pymcu.chips.atmega328p import GPIOR0, TIFR0
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART
from pymcu.hal.timer import Timer
from pymcu.hal.adc import AnalogPin


def timer0_ovf_isr():
    GPIOR0[0] = 1


def int0_isr():
    GPIOR0[1] = 1


def main():
    led  = Pin("PB5", Pin.OUT)
    btn  = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)
    uart = UART(9600)
    adc  = AnalogPin("PC0")

    timer = Timer(0, 256)
    timer.irq(timer0_ovf_isr)
    btn.irq(Pin.IRQ_FALLING, int0_isr)

    GPIOR0[0] = 0
    GPIOR0[1] = 0
    uart.println("SENSOR DASHBOARD")

    raw:     uint8  = 0
    avg:     uint8  = 0
    lo:      uint8  = 255
    hi:      uint8  = 0
    verbose: uint8  = 1
    tick:    uint8  = 0

    while True:
        # Timer0 OVF: count ticks, sample ADC every 64 (~262 ms)
        if GPIOR0[0] == 1:
            GPIOR0[0] = 0
            TIFR0[0] = 1       # clear TOV0 flag (write-1-to-clear)
            tick += 1
            if tick == 64:
                tick = 0

                raw16: uint16 = adc.read()
                raw = raw16 >> 2   # scale 10-bit to 8-bit

                if raw < lo:
                    lo = raw
                if raw > hi:
                    hi = raw

                avg = (avg + raw) >> 1

                led.toggle()

                if verbose == 1:
                    uart.write_str("R:")
                    uart.write_hex(raw)
                    uart.write_str(" A:")
                    uart.write_hex(avg)
                    uart.write_str(" L:")
                    uart.write_hex(lo)
                    uart.write_str(" H:")
                    uart.write_hex(hi)
                    uart.write('\n')
                else:
                    uart.write_hex(avg)
                    uart.write('\n')

        # INT0: toggle verbose/compact mode
        if GPIOR0[1] == 1:
            GPIOR0[1] = 0
            if verbose == 1:
                verbose = 0
                uart.println("MODE:COMPACT")
            else:
                verbose = 1
                uart.println("MODE:VERBOSE")

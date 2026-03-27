# ATmega328P: I2C peripheral (TWI) -- interrupt-driven receive at address 0x42
#
# Demonstrates:
#   - I2C(0x42): configure TWI as peripheral at 7-bit address 0x42
#   - i2c.irq(handler): register ISR at TWI vector via compile_isr;
#     no @interrupt decorator, TWCR.TWIE write, or asm("SEI") needed
#   - TWI state machine in the ISR: check TWSR, ACK each event
#   - GPIOR0 atomic storage: SBI/CBI are single-cycle IO instructions
#
# Hardware: Arduino Uno as I2C peripheral
#   SDA = PC4 (Arduino pin A4) -- controlled by TWI hardware
#   SCL = PC5 (Arduino pin A5) -- controlled by TWI hardware
#   UART TX at 9600 baud
#   Connect a controller (another Arduino, Raspberry Pi, etc.) to the bus.
#
# ISR contract: on_event() MUST clear TWINT by writing TWCR with TWINT=1
# (bit 7) to re-arm the interrupt; otherwise the peripheral stalls.
# TWCR = 0xC4: TWINT(7)|TWEA(6)|TWEN(2).
#
# Output:
#   "I2CI\n"    -- boot banner
#   "XX\n"      -- two hex digits for each data byte received from controller
#
from pymcu.types import uint8
from pymcu.chips.atmega328p import TWSR, TWDR, TWCR, GPIOR0
from pymcu.hal.i2c import I2C
from pymcu.hal.uart import UART


def on_event():
    status: uint8 = TWSR.value & 0xF8
    if status == 0x60:          # own SLA+W received, ACK returned
        TWCR.value = 0xC4       # TWINT | TWEA | TWEN -- ready for data
    elif status == 0x80:        # data byte received, ACK returned
        GPIOR0[0] = TWDR.value  # save byte for main loop
        TWCR.value = 0xC4
    elif status == 0xA0:        # STOP or repeated START received
        TWCR.value = 0xC4
    else:
        TWCR.value = 0xC4       # ACK all other events


def main():
    uart = UART(9600)
    i2c  = I2C(0x42)

    # irq() enables TWIE + SEI and places on_event at the TWI vector.
    # No @interrupt decorator or asm("SEI") needed.
    i2c.irq(on_event)

    GPIOR0[0] = 0
    uart.println("I2CI")

    while True:
        if GPIOR0[0] != 0:
            byte: uint8 = GPIOR0[0]
            GPIOR0[0] = 0
            uart.write_hex(byte)
            uart.write('\n')

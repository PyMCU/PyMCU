# ATmega328P: Hardware SPI → 74HC595 shift register — running light
# Tests: SPI HAL (spi_init, spi_select/deselect, spi_transfer),
#        SPDR.value full-byte write (OUT 0x2E — correct, unlike UDR0[0] which uses ORI)
#        uint8 rotate pattern, match/case animation modes, UART debug output
#
# Hardware: Arduino Uno + 74HC595
#   SER   (pin 14) ← MOSI  PB3  (Arduino pin 11)
#   SRCLK (pin 11) ← SCK   PB5  (Arduino pin 13)
#   RCLK  (pin 12) ← SS    PB2  (Arduino pin 10) — latch: low while clocking, high to latch
#   OE    (pin 13) ← GND   (always output-enabled)
#   MR    (pin 10) ← VCC   (never reset)
#   Q0-Q7 → 8 LEDs with resistors to GND
#   Serial terminal at 9600 baud
#
# Animation modes (advance with any UART input):
#   0 = running light   (single bit rotating left)
#   1 = chaser pair     (two adjacent bits)
#   2 = binary counter  (0x00 → 0xFF → 0x00 …)
#
from whisnake.types import uint8
from whisnake.hal.spi import SPI
from whisnake.hal.uart import UART
from pymcu.time import delay_ms

# Animation mode constants
MODE_RUNNING = 0
MODE_CHASER  = 1
MODE_COUNTER = 2


def main():
    spi  = SPI()
    uart = UART(9600)

    uart.println("SPI 74HC595 DEMO")

    pattern: uint8 = 0x01
    mode:    uint8 = MODE_RUNNING

    while True:
        # --- Clock out one byte to 74HC595 ---
        with spi:             # select() on enter, deselect() on exit
            spi.write(pattern)    # MOSI: 8 bits, MSB first at fosc/4

        uart.write(pattern)
        delay_ms(120)

        # --- Advance pattern for next frame ---
        match mode:
            case MODE_RUNNING:
                # Rotate left: Q0→Q1→…→Q7→Q0
                msb: uint8 = (pattern >> 7) & 1
                pattern = (pattern << 1) | msb

            case MODE_CHASER:
                # Two adjacent lit bits rotating left
                msb = (pattern >> 7) & 1
                pattern = (pattern << 1) | msb
                if pattern == 0x03:    # wrapped around
                    pattern = 0x03

            case _:
                # Binary counter
                pattern += 1

        # --- Mode change on any incoming UART byte ---
        # (Non-blocking: check UCSR0A RXC flag without blocking read)
        from whisnake.chips.atmega328p import UCSR0A, UDR0
        if UCSR0A[7] == 1:         # RXC0: data available
            UDR0[0] = 0            # dummy read to clear RXC (clears UART receive FIFO)
            mode += 1
            if mode == 3:
                mode = MODE_RUNNING
            pattern = 0x01         # reset pattern on mode change

            match mode:
                case MODE_RUNNING:
                    uart.println("MODE: RUNNING")
                case MODE_CHASER:
                    uart.println("MODE: CHASER")
                case _:
                    uart.println("MODE: COUNTER")

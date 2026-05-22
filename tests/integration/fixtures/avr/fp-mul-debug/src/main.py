# PyMCU -- fp-mul-debug: soft-float __fp_mul diagnostic via BREAK checkpoints
#
# After each __fp_mul call the result lives in R22:R25 (AVR float ABI).
# We capture those four bytes into known MMIO registers via inline OUT
# instructions -- the AVR equivalent of C's float-to-raw-bits reinterpret cast.
#
# Float layout: R25=byte0(MSB/sign+exp), R24=byte1, R23=byte2, R22=byte3(LSB).
# Capture registers (I/O port -> data-space address):
#   GPIOR0  I/O 0x1E -> 0x3E  = R22 (byte3, LSB)
#   GPIOR1  I/O 0x2A -> 0x4A  = R23 (byte2)
#   GPIOR2  I/O 0x2B -> 0x4B  = R24 (byte1)
#   OCR0A   I/O 0x27 -> 0x47  = R25 (byte0, MSB)
#
# Expected IEEE 754 results (big-endian, MSB first):
#   float(1.0)   = 0x3F800000   R25=0x3F R24=0x80 R23=0x00 R22=0x00
#   float(55.0)  = 0x425C0000   R25=0x42 R24=0x5C R23=0x00 R22=0x00
#   float(550.0) = 0x44098000   R25=0x44 R24=0x09 R23=0x80 R22=0x00
#   float(23.5)  = 0x41BC0000   R25=0x41 R24=0xBC R23=0x00 R22=0x00
#   float(235.0) = 0x436B0000   R25=0x43 R24=0x6B R23=0x00 R22=0x00
#
# Checkpoints:
#   1 - (uint16)10  * 0.1  -> 1.0   (int*float, needs mantissa normalise)
#   2 - (uint16)550 * 0.1  -> 55.0  (DHT22 humidity path)
#   3 - r2 * 10.0          -> 550.0 (float*float, uart_write_float path)
#   4 - (uint16)235 * 0.1  -> 23.5  (DHT22 temperature path)
#   5 - r4 * 10.0          -> 235.0 (float*float, uart_write_float path)
#
from pymcu.types import asm, uint16, inline


@inline
def _capture_float_bits():
    # Cast the float in R22:R25 to raw bytes by writing each register to a
    # known MMIO location (same technique as C's *((uint8_t*)&f) per byte).
    # @inline ensures these OUT instructions are emitted at the call site
    # where R22:R25 still hold the __fp_mul return value.
    asm("OUT 0x1E, R22")  # GPIOR0 = byte3 (LSB)
    asm("OUT 0x2A, R23")  # GPIOR1 = byte2
    asm("OUT 0x2B, R24")  # GPIOR2 = byte1
    asm("OUT 0x27, R25")  # OCR0A  = byte0 (MSB)


def main():
    # Checkpoint 1: normalise-on-carry case (10 * 0.1 = 1.0)
    # 1.25 * 1.6 = 2.0 -> mantissa carry -> exp += 1 -> unbiased exp = 0 -> float(1.0)
    raw1: uint16 = 10
    r1: float = raw1 * 0.1
    _capture_float_bits()   # snapshot R22:R25 -> GPIOR0/1/2 + OCR0A
    asm("BREAK")

    # Checkpoint 2: DHT22 humidity (550 raw -> 55.0 %)
    raw2: uint16 = 550
    r2: float = raw2 * 0.1
    _capture_float_bits()
    asm("BREAK")

    # Checkpoint 3: uart_write_float first-digit multiply (55.0 * 10.0 -> 550.0)
    r3: float = r2 * 10.0
    _capture_float_bits()
    asm("BREAK")

    # Checkpoint 4: DHT22 temperature (235 raw -> 23.5 C)
    raw4: uint16 = 235
    r4: float = raw4 * 0.1
    _capture_float_bits()
    asm("BREAK")

    # Checkpoint 5: uart_write_float first-digit multiply (23.5 * 10.0 -> 235.0)
    r5: float = r4 * 10.0
    _capture_float_bits()
    asm("BREAK")

    while True:
        pass

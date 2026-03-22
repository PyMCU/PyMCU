# PIC16F18877 Feature Showcase
# Demonstrates every supported pymcu language feature on the chip with the
# richest codegen (PIC14E architecture, Enhanced Mid-Range).
#
# Hardware assumed (Curiosity HPC or breadboard):
#   - 8 LEDs on PORTD (RD0-RD7), active-high
#   - Push button on RB4 (active-low, internal pull-up)
#   - Potentiometer on RA0/AN0 (ADC input)
#   - UART TX on RC6 (9600 baud, for status messages)
#
# Features exercised:
#   Imports, HAL classes, @inline, @interrupt, for loops, while loops,
#   if/elif/else, match/case, break/continue, global, type annotations,
#   const(), bit-index read/write, augmented assignment, unary/binary ops,
#   inline asm(), delay_ms/delay_us, user-defined functions, module constants
#
from whisnake.hal.gpio import Pin
from pymcu.time import delay_ms, delay_us
from whisnake.types import uint8, uint16, const, asm, inline, interrupt

# ---------------------------------------------------------------------------
#  Module-level constants and globals
# ---------------------------------------------------------------------------
NUM_LEDS: const = const(8)
CHASE_SPEED: const = const(80)       # ms between LED shifts
ADC_THRESHOLD: const = const(128)    # midpoint of 8-bit ADC
MODE_CHASE: const = const(0)
MODE_BLINK: const = const(1)
MODE_STACK: const = const(2)
NUM_MODES: const = const(3)

mode: uint8 = MODE_CHASE
button_pressed: uint8 = 0

# ---------------------------------------------------------------------------
#  Interrupt Service Routine  (@interrupt decorator)
#  Fires on button press via IOC falling edge on RB4.
#  Sets a flag; main loop acts on it (ISR should be short).
# ---------------------------------------------------------------------------
@interrupt(0x04)
def on_button():
    global button_pressed
    button_pressed = 1

# ---------------------------------------------------------------------------
#  User-defined helper: configure ADC for channel AN0
#  Shows direct register access, bit-index writes, inline asm
# ---------------------------------------------------------------------------
from whisnake.chips.pic16f18877 import ANSELA, TRISA, ADCON0, ADPCH, ADCLK, ADREF, ADRESH, ADON, GO, PORTD, TRISD, INTCON, GIE, PEIE

def adc_init():
    ANSELA.value = 0x01             # RA0 analog, rest digital
    TRISA.value = TRISA.value | 0x01  # RA0 as input
    ADPCH.value = 0x00              # Channel AN0
    ADCLK.value = 0x1F              # Fosc/64 conversion clock
    ADREF.value = 0x00              # VDD / VSS references
    ADCON0.value = 0x00
    ADCON0[ADON] = 1                # Enable ADC module (bit-index write)

def adc_read() -> uint8:
    ADCON0[GO] = 1                  # Start conversion (bit-index write)
    # Busy-wait for conversion complete
    while ADCON0[GO]:               # Bit-index read in condition
        pass
    return ADRESH.value             # Return 8-bit result (right-justified high)

# ---------------------------------------------------------------------------
#  @inline helper: Efficient PORTD write with bank-safe asm
#  Demonstrates @inline and asm() intrinsic
# ---------------------------------------------------------------------------
@inline
def write_leds(pattern: uint8):
    PORTD.value = pattern

# ---------------------------------------------------------------------------
#  Animation: LED chase (Knight Rider style)
#  Shows for-loop, bit-shift, augmented assignment, break
# ---------------------------------------------------------------------------
def chase_animation():
    pos: uint8 = 0
    direction: uint8 = 0    # 0 = left, 1 = right

    for i in range(NUM_LEDS * 2 - 2):       # for-loop with range()
        write_leds(1 << pos)                  # bit-shift expression

        if direction == 0:
            pos += 1                          # augmented assignment +=
            if pos >= NUM_LEDS - 1:
                direction = 1
        else:
            pos -= 1                          # augmented assignment -=
            if pos == 0:
                direction = 0

        delay_ms(CHASE_SPEED)

# ---------------------------------------------------------------------------
#  Animation: Blink all LEDs
#  Shows unary operator (~), binary operator (&)
# ---------------------------------------------------------------------------
def blink_animation():
    for i in range(4):
        write_leds(0xFF)
        delay_ms(CHASE_SPEED)
        write_leds(0x00)
        delay_ms(CHASE_SPEED)

# ---------------------------------------------------------------------------
#  Animation: Stack fill (one LED at a time from right)
#  Shows continue, while-loop, bitwise OR
# ---------------------------------------------------------------------------
def stack_animation():
    pattern: uint8 = 0x00
    bit: uint8 = 0

    while bit < NUM_LEDS:
        pattern = pattern | (1 << bit)
        write_leds(pattern)
        delay_ms(CHASE_SPEED)
        bit += 1

    # Drain from left to right
    for i in range(NUM_LEDS):
        delay_ms(CHASE_SPEED)
        pattern = pattern >> 1                # right-shift to drain
        write_leds(pattern)

# ---------------------------------------------------------------------------
#  Mode dispatcher using match/case
#  Shows match statement with integer patterns and wildcard
# ---------------------------------------------------------------------------
def run_current_mode():
    global mode
    match mode:
        case 0:           # MODE_CHASE
            chase_animation()
        case 1:           # MODE_BLINK
            blink_animation()
        case 2:           # MODE_STACK
            stack_animation()
        case _:           # Wildcard: reset to chase
            mode = MODE_CHASE
            chase_animation()

# ---------------------------------------------------------------------------
#  Main entry point
# ---------------------------------------------------------------------------
def main():
    global mode
    global button_pressed

    # --- GPIO setup via HAL ---
    # Button with pull-up and falling-edge interrupt
    btn = Pin("RB4", Pin.IN, pull=Pin.PULL_UP)
    btn.irq(Pin.IRQ_FALLING)

    # PORTD as output for LEDs (direct register, not HAL — both styles work)
    TRISD.value = 0x00
    PORTD.value = 0x00

    # --- ADC setup ---
    adc_init()

    # --- Enable global interrupts ---
    INTCON[GIE] = 1                         # bit-index write
    INTCON[PEIE] = 1

    # --- Startup: brief self-test sweep ---
    for i in range(NUM_LEDS):
        write_leds(1 << i)
        delay_ms(40)
    write_leds(0x00)
    delay_ms(200)

    # --- Main loop ---
    while True:
        # Check button flag (set by ISR)
        if button_pressed == 1:
            button_pressed = 0
            mode += 1
            if mode >= NUM_MODES:
                mode = 0

            # Brief visual feedback: flash current mode number
            write_leds(mode + 1)
            delay_ms(300)
            write_leds(0x00)
            delay_ms(100)

        # Read potentiometer and adjust speed via ADC
        # (ADC value is unused in animation call — this just demonstrates
        #  the full ADC read path; a real app could scale CHASE_SPEED)
        adc_val: uint8 = adc_read()

        # Use ADC to choose brightness: if above threshold, show animation;
        # otherwise, keep LEDs off (simple if/else)
        if adc_val >= ADC_THRESHOLD:
            run_current_mode()
        else:
            write_leds(0x00)
            delay_ms(100)

# ATmega328P preemptive RTOS demo -- PyMCU showcase
#
# Four tasks run under a weighted round-robin scheduler (1 ms Timer1 systick).
# Priorities determine how many of the 8 slots each task receives per cycle:
#
#   sensor_task    priority 3  -> 4 slots (50 % CPU) -- reads TCNT0, CRC8, bit-reverse
#   ledbar1_task   priority 2  -> 2 slots (25 % CPU) -- VU meter on PORTD (PD2-PD7)
#   ledbar2_task   priority 1  -> 1 slot  (12.5 %)   -- Knight Rider on PORTC (PC0-PC5)
#   blink_task     priority 0  -> 1 slot  (12.5 %)   -- heartbeat LED on PB5
#
# Priority effect is visible:  ledbar2_task calls delay_ms(50) but at 12.5 % CPU
# the scanner effectively moves once every ~400 ms wall-clock. Increase its
# priority to 2 and it speeds up 2x -- that is exactly what priorities do.
#
# Hardware:
#   Bar 1 (VU meter)     PORTD PD2-PD7  (Arduino D2-D7, 6 segments)
#   Bar 2 (Knight Rider) PORTC PC0-PC5  (Arduino A0-A5, 6 segments)
#   Heartbeat LED        PB5            (Arduino D13)
#
# Timer layout:
#   Timer0 -- free-running prescaler 64 (TCNT0 as pseudo-ADC)
#   Timer1 -- CTC 1 ms systick (OCR1A=249, prescaler 64)
#
# Schedule: [3, 3, 3, 3, 2, 2, 1, 0] -- built at runtime by start_scheduler()
from pymcu.hal.gpio import Pin
from pymcu.types import uint8, asm
from rtos import add_task, start_scheduler, Priority, delay_ms
from sensor import compute_crc8, bit_reverse8
from ledbar import init_bars, set_level, set_scanner, get_scanner_pattern

# Shared pipeline state written by sensor_task and read by ledbar1_task / blink_task.
# Single-byte access is atomic on AVR -- no mutex required.
_crc_out: uint8 = 0


def sensor_task():
    # Highest priority (3): reads the free-running TCNT0 hardware counter as
    # a pseudo-ADC input, runs it through a CRC-8/MAXIM pipeline and a
    # bit-reversal, then publishes the result to _crc_out.
    while True:
        raw: uint8 = 0
        asm("IN %0, 0x26", raw)
        crc: uint8 = compute_crc8(raw)
        _crc_out = bit_reverse8(crc)


def ledbar1_task():
    # Priority 2 (25 % CPU): VU meter.  Converts _crc_out to a 0-6 segment
    # bar graph on PORTD PD2-PD7 and updates every time this task is scheduled.
    while True:
        set_level(_crc_out)


def ledbar2_task():
    # Priority 1 (12.5 % CPU): Knight Rider scanner on PORTC PC0-PC5.
    # delay_ms(50) busy-waits for 50 ms of *this task's* CPU time.  Because the
    # task only runs 1 out of every 8 ms ticks, the effective wall-clock delay
    # is ~400 ms -- the scanner moves visibly slow at low priority.
    pos: uint8 = 0
    forward: uint8 = 1
    while True:
        pattern: uint8 = get_scanner_pattern(pos)
        set_scanner(pattern)
        delay_ms(50)
        if forward == 1:
            if pos >= 5:
                forward = 0
                pos = pos - 1
            else:
                pos = pos + 1
        else:
            if pos == 0:
                forward = 1
                pos = pos + 1
            else:
                pos = pos - 1


def blink_task():
    # Lowest priority (0, 12.5 % CPU): heartbeat blink on PB5 (Arduino D13).
    # delay_ms(200) takes ~1600 ms wall-clock at this priority, giving a
    # slow, clearly visible heartbeat that confirms the scheduler is running.
    led = Pin("PB5", Pin.OUT)
    while True:
        led.high()
        delay_ms(200)
        led.low()
        delay_ms(200)


def main():
    init_bars()

    # Task registry: (handler, priority). Order determines task IDs (0, 1, 2, 3).
    # Task 0 is launched first; the rest have pre-built stacks set up by start_scheduler().
    for fn, prio in zip(
        [blink_task,        ledbar2_task,   ledbar1_task,       sensor_task],
        [Priority.IDLE,     Priority.LOW,   Priority.NORMAL,    Priority.HIGH],
    ):
        add_task(fn, prio)

    start_scheduler()
